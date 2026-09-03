# -*- coding: utf-8 -*-
"""
Скрипт для Revit Python Shell
Расстановка семейств креплений по соединительным деталям
с учетом ширины лотка и расстояния до перекрытия (включая связанные модели)
ИСПРАВЛЕННАЯ ВЕРСИЯ
"""

import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitAPIUI')
from Autodesk.Revit.DB import *
from Autodesk.Revit.UI import *
import math

doc = __revit__.ActiveUIDocument.Document
uidoc = __revit__.ActiveUIDocument

# Счетчики причин пропуска
skip_reasons = {
    'not_horizontal': 0,
    'no_width_param': 0,
    'no_family': 0,
    'no_location': 0,
    'no_floor_above': 0,
    'no_level': 0,
    'no_length': 0,
    'other': 0
}

def get_cable_tray_fittings():
    """Получить все соединительные детали лотков заданного семейства"""
    collector = FilteredElementCollector(doc)\
        .OfCategory(BuiltInCategory.OST_CableTrayFitting)\
        .WhereElementIsNotElementType()
    
    fittings = []
    for fitting in collector:
        try:
            family = fitting.Symbol.Family
            if family and "470_DKC_F5_Проволочный лоток" in family.Name:
                fittings.append(fitting)
        except:
            continue
    
    return fittings

def get_family_symbol_by_width(width_mm):
    """Получить символ семейства крепления по ширине лотка"""
    family_name_map = {
        200.0: "Узел крепления проволочный 200",
        300.0: "Узел крепления проволочный 300",
        400.0: "Узел крепления проволочный 400"
    }
    
    family_name = family_name_map.get(width_mm)
    if not family_name:
        return None
    
    collector = FilteredElementCollector(doc)\
        .OfClass(FamilySymbol)\
        .OfCategory(BuiltInCategory.OST_GenericModel)
    
    for symbol in collector:
        try:
            symbol_name = symbol.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME).AsString()
            if symbol_name and family_name in symbol_name:
                return symbol
        except:
            continue
    
    return None

def get_fitting_location(fitting):
    """Получить точку расположения соединительной детали"""
    try:
        location = fitting.Location
        if isinstance(location, LocationPoint):
            return location.Point
    except:
        pass
    return None

def get_fitting_rotation(fitting):
    """Получить угол поворота соединительной детали"""
    try:
        location = fitting.Location
        if isinstance(location, LocationPoint):
            return location.Rotation
    except:
        pass
    return 0.0

def is_fitting_horizontal(fitting, tolerance_degrees=10):
    """Проверить, является ли соединительная деталь горизонтальной"""
    try:
        transform = fitting.GetTransform()
        basis_z = transform.BasisZ
        vertical = XYZ(0, 0, 1)
        dot_product = abs(basis_z.DotProduct(vertical))
        angle_rad = math.acos(min(1.0, dot_product))
        angle_deg = math.degrees(angle_rad)
        
        is_horizontal = angle_deg <= tolerance_degrees
        
        print("    Угол отклонения от горизонтали: {:.2f}° - {}".format(
            angle_deg, 
            "ГОРИЗОНТАЛЬНАЯ" if is_horizontal else "НЕ ГОРИЗОНТАЛЬНАЯ"
        ))
        
        return is_horizontal
        
    except Exception as e:
        print("    Ошибка проверки горизонтальности: {}".format(str(e)))
        return True

def get_fitting_width(fitting):
    """Получить ширину лотка из параметра И_Э_ШиринаЛотка"""
    try:
        param = fitting.LookupParameter("И_Э_ШиринаЛотка")
        if param and param.HasValue:
            width_mm = param.AsDouble() * 304.8
            return round(width_mm, 1)
    except:
        pass
    
    return None

def get_fitting_length(fitting):
    """Получить длину соединительной детали из параметра ADSK_Размер_Длина"""
    try:
        param = fitting.LookupParameter("ADSK_Размер_Длина")
        if param and param.HasValue:
            return param.AsDouble() * 304.8
    except:
        pass
    
    try:
        for param in fitting.Parameters:
            param_name = param.Definition.Name
            if "ADSK_Размер_Длина" in param_name or "Длина" in param_name:
                if param.HasValue:
                    return param.AsDouble() * 304.8
    except:
        pass
    
    return None

def get_element_level(element):
    """Получить уровень элемента"""
    try:
        level_id = element.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM).AsElementId()
        if level_id and level_id != ElementId.InvalidElementId:
            return doc.GetElement(level_id)
    except:
        pass
    
    try:
        level_id = element.LevelId
        if level_id and level_id != ElementId.InvalidElementId:
            return doc.GetElement(level_id)
    except:
        pass
    
    levels = FilteredElementCollector(doc).OfClass(Level).ToElements()
    if levels:
        return levels[0]
    return None

def get_all_revit_link_instances():
    """Получить все экземпляры связанных файлов Revit"""
    collector = FilteredElementCollector(doc)\
        .OfClass(RevitLinkInstance)
    
    return list(collector)

def get_floor_above_in_current_model(point):
    """Найти перекрытие над точкой в текущей модели"""
    try:
        ray_direction = XYZ(0, 0, 1)
        
        ref_intersector = ReferenceIntersector(
            ElementCategoryFilter(BuiltInCategory.OST_Floors),
            FindReferenceTarget.Face,
            doc.ActiveView
        )
        
        context = ref_intersector.FindNearest(point, ray_direction)
        
        if context:
            reference = context.GetReference()
            floor = doc.GetElement(reference.ElementId)
            
            if floor:
                intersection_point = reference.GlobalPoint
                distance_ft = intersection_point.Z - point.Z
                
                # Проверяем, что перекрытие СВЕРХУ (расстояние положительное)
                if distance_ft > 0:
                    distance_mm = distance_ft * 304.8
                    return floor, distance_mm, "Текущая модель"
        
    except Exception as e:
        print("      Ошибка поиска перекрытия в текущей модели: {}".format(str(e)))
    
    return None, None, None

def get_floor_above_in_linked_model(point, link_instance):
    """Найти перекрытие над точкой в связанной модели"""
    try:
        # Получаем документ связи
        link_doc = link_instance.GetLinkDocument()
        if not link_doc:
            return None, None, None
        
        # Получаем трансформацию связи
        transform = link_instance.GetTotalTransform()
        
        # Преобразуем точку в систему координат связи
        inverse_transform = transform.Inverse
        point_in_link = inverse_transform.OfPoint(point)
        
        ray_direction = XYZ(0, 0, 1)
        
        # Получаем 3D вид из связанной модели (или создаем временный)
        view3d = None
        
        # Пытаемся найти любой 3D вид в связанной модели
        collector_3d = FilteredElementCollector(link_doc).OfClass(View3D)
        for view in collector_3d:
            if not view.IsTemplate:
                view3d = view
                break
        
        # Если 3D вид не найден, используем ActiveView текущего документа
        if not view3d:
            view3d = doc.ActiveView
        
        # Создаем ReferenceIntersector для связанной модели
        ref_intersector = ReferenceIntersector(
            ElementCategoryFilter(BuiltInCategory.OST_Floors),
            FindReferenceTarget.Face,
            view3d
        )
        
        ref_intersector.FindReferencesInRevitLinks = True
        
        # Ищем пересечения с перекрытиями в связанной модели
        # Используем FilteredElementCollector для поиска перекрытий
        floors_in_link = FilteredElementCollector(link_doc)\
            .OfCategory(BuiltInCategory.OST_Floors)\
            .WhereElementIsNotElementType()\
            .ToElements()
        
        closest_floor = None
        closest_distance = None
        
        for floor in floors_in_link:
            try:
                # Получаем геометрию перекрытия
                options = Options()
                options.ComputeReferences = True
                options.DetailLevel = ViewDetailLevel.Fine
                
                geom = floor.get_Geometry(options)
                if not geom:
                    continue
                
                for geom_obj in geom:
                    if isinstance(geom_obj, Solid):
                        # Проверяем пересечение луча с Solid
                        faces = geom_obj.Faces
                        for face in faces:
                            try:
                                # Получаем результат пересечения
                                result = face.Project(point_in_link)
                                if result:
                                    # Получаем точку на поверхности
                                    proj_point = result.XYZPoint
                                    
                                    # Проверяем, что точка выше исходной
                                    if proj_point.Z > point_in_link.Z:
                                        # Преобразуем в координаты основной модели
                                        proj_point_global = transform.OfPoint(proj_point)
                                        distance_ft = proj_point_global.Z - point.Z
                                        
                                        if distance_ft > 0:
                                            distance_mm = distance_ft * 304.8
                                            
                                            # Сохраняем если это ближайшее
                                            if closest_distance is None or distance_mm < closest_distance:
                                                closest_floor = floor
                                                closest_distance = distance_mm
                            except:
                                continue
            except:
                continue
        
        if closest_floor and closest_distance:
            link_name = link_instance.Name
            return closest_floor, closest_distance, link_name
        
    except Exception as e:
        print("      Ошибка поиска перекрытия в связи {}: {}".format(
            link_instance.Name if hasattr(link_instance, 'Name') else "Unknown", 
            str(e)))
    
    return None, None, None

def get_floor_above(point):
    """
    Найти ближайшее перекрытие НАД заданной точкой 
    (в текущей модели и во всех связанных моделях)
    Возвращает (floor, distance_mm, source_name) или (None, None, None)
    """
    
    closest_floor = None
    closest_distance = None
    closest_source = None
    
    # Проверяем текущую модель
    floor, distance, source = get_floor_above_in_current_model(point)
    if floor and distance is not None and distance > 0:
        closest_floor = floor
        closest_distance = distance
        closest_source = source
        print("      Найдено перекрытие СВЕРХУ в текущей модели: расстояние {:.2f} мм".format(distance))
    
    # Проверяем все связанные модели
    link_instances = get_all_revit_link_instances()
    
    for link_instance in link_instances:
        try:
            floor, distance, source = get_floor_above_in_linked_model(point, link_instance)
            
            if floor and distance is not None and distance > 0:
                print("      Найдено перекрытие СВЕРХУ в связи '{}': расстояние {:.2f} мм".format(source, distance))
                
                # Выбираем ближайшее перекрытие СВЕРХУ (минимальное положительное расстояние)
                if closest_distance is None or distance < closest_distance:
                    closest_floor = floor
                    closest_distance = distance
                    closest_source = source
        except Exception as e:
            print("      Ошибка обработки связи: {}".format(str(e)))
    
    if closest_floor and closest_distance:
        print("      Выбрано ближайшее перекрытие СВЕРХУ: {} (расстояние {:.2f} мм)".format(
            closest_source, closest_distance))
    
    return closest_floor, closest_distance, closest_source

def set_suspension_height_parameter(instance, height_mm):
    """Установить значение параметра 'Высота подвеса' в экземпляре"""
    try:
        param = instance.LookupParameter("Высота подвеса")
        
        if param and not param.IsReadOnly:
            height_ft = height_mm / 304.8
            param.Set(height_ft)
            return True
        else:
            print("      Предупреждение: параметр 'Высота подвеса' не найден или только для чтения")
            return False
            
    except Exception as e:
        print("      Ошибка установки параметра 'Высота подвеса': {}".format(str(e)))
        return False

def place_supports_at_fitting(fitting, step_mm=650):
    """
    Разместить крепления в месте расположения соединительной детали
    с учетом её длины, ширины и расстояния до перекрытия
    """
    
    # Проверяем, горизонтальная ли деталь
    if not is_fitting_horizontal(fitting):
        print("    ПРИЧИНА ПРОПУСКА: Деталь не горизонтальная")
        skip_reasons['not_horizontal'] += 1
        return []
    
    # Получаем ширину лотка
    width_mm = get_fitting_width(fitting)
    if not width_mm:
        print("    ПРИЧИНА ПРОПУСКА: Не удалось получить параметр И_Э_ШиринаЛотка")
        skip_reasons['no_width_param'] += 1
        return []
    
    print("    Ширина лотка: {:.1f} мм".format(width_mm))
    
    # Получаем соответствующее семейство крепления
    family_symbol = get_family_symbol_by_width(width_mm)
    if not family_symbol:
        print("    ПРИЧИНА ПРОПУСКА: Семейство крепления для ширины {:.1f} мм не найдено".format(width_mm))
        skip_reasons['no_family'] += 1
        return []
    
    print("    Используется семейство: {}".format(family_symbol.Name))
    
    # Активируем семейство если нужно
    if not family_symbol.IsActive:
        family_symbol.Activate()
        doc.Regenerate()
    
    # Получаем точку расположения
    point = get_fitting_location(fitting)
    if not point:
        print("    ПРИЧИНА ПРОПУСКА: Не удалось получить точку расположения")
        skip_reasons['no_location'] += 1
        return []
    
    # Проверяем наличие перекрытия СВЕРХУ (в текущей модели и связях)
    floor, distance_to_floor, source_name = get_floor_above(point)
    
    if not floor or distance_to_floor is None or distance_to_floor <= 0:
        print("    ПРИЧИНА ПРОПУСКА: Перекрытие СВЕРХУ над деталью не найдено")
        skip_reasons['no_floor_above'] += 1
        return []
    
    print("    Найдено перекрытие СВЕРХУ в: {}".format(source_name))
    print("    Расстояние до перекрытия: {:.2f} мм".format(distance_to_floor))
    
    # Получаем уровень
    level = get_element_level(fitting)
    if not level:
        print("    ПРИЧИНА ПРОПУСКА: Не удалось получить уровень")
        skip_reasons['no_level'] += 1
        return []
    
    # Получаем длину детали
    length_mm = get_fitting_length(fitting)
    if not length_mm:
        print("    ПРИЧИНА ПРОПУСКА: Не удалось получить параметр ADSK_Размер_Длина")
        skip_reasons['no_length'] += 1
        return []
    
    print("    Длина детали: {:.2f} мм".format(length_mm))
    
    # Вычисляем количество креплений
    num_supports = int(round(length_mm / step_mm))
    if num_supports < 1:
        num_supports = 1
    
    print("    Количество креплений: {}".format(num_supports))
    
    # Получаем угол поворота детали и добавляем 90 градусов
    base_rotation = get_fitting_rotation(fitting)
    rotation = base_rotation + math.pi / 2.0
    
    # Вычисляем направление расстановки
    direction = XYZ(math.cos(base_rotation), math.sin(base_rotation), 0).Normalize()
    
    # Конвертируем шаг в футы
    step_ft = step_mm / 304.8
    
    placed_instances = []
    
    # Размещаем крепления
    for i in range(num_supports):
        try:
            # Вычисляем смещение для текущего крепления
            offset = i * step_ft
            
            # Вычисляем точку размещения
            support_point = XYZ(
                point.X + direction.X * offset,
                point.Y + direction.Y * offset,
                point.Z
            )
            
            # Создаем экземпляр семейства крепления
            instance = doc.Create.NewFamilyInstance(
                support_point,
                family_symbol,
                level,
                Structure.StructuralType.NonStructural
            )
            
            if instance:
                # Поворачиваем крепление
                try:
                    axis = Line.CreateBound(support_point, XYZ(support_point.X, support_point.Y, support_point.Z + 1))
                    ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation)
                except Exception as e:
                    print("      Предупреждение: не удалось повернуть крепление #{}: {}".format(i+1, str(e)))
                
                # Устанавливаем параметр "Высота подвеса"
                set_suspension_height_parameter(instance, distance_to_floor)
                
                placed_instances.append(instance)
                print("      ✓ Крепление #{} размещено (смещение: {:.2f} мм, высота подвеса: {:.2f} мм)".format(
                    i+1, offset * 304.8, distance_to_floor))
        
        except Exception as e:
            print("      ✗ Ошибка при создании крепления #{}: {}".format(i+1, str(e)))
    
    return placed_instances

# ========== ОСНОВНОЙ КОД ==========

print("=" * 70)
print("Начало работы скрипта расстановки креплений")
print("=" * 70)

# Проверяем наличие связанных моделей
link_instances = get_all_revit_link_instances()
print("\nНайдено связанных моделей: {}".format(len(link_instances)))
for link in link_instances:
    print("  - {}".format(link.Name))

t = Transaction(doc, "Расстановка креплений с учетом ширины и перекрытий")

try:
    t.Start()
    
    # Получаем соединительные детали
    print("\nПоиск соединительных деталей '470_DKC_F5_Проволочный лоток'...")
    fittings = get_cable_tray_fittings()
    
    if not fittings:
        print("ПРЕДУПРЕЖДЕНИЕ: Соединительные детали не найдены!")
        t.RollBack()
    else:
        print("Найдено соединительных деталей: {}".format(len(fittings)))
        
        total_supports = 0
        processed_count = 0
        
        # Размещаем крепления для каждой детали
        print("\nРазмещение креплений с шагом 650 мм...")
        for idx, fitting in enumerate(fittings, 1):
            try:
                # Получаем информацию о детали
                fitting_type = doc.GetElement(fitting.GetTypeId())
                type_name = fitting_type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME).AsString() if fitting_type else "Неизвестно"
                
                print("  [{}/{}] Деталь: {} (Id: {})".format(
                    idx, len(fittings), type_name, fitting.Id))
                
                # Размещаем крепления
                supports = place_supports_at_fitting(fitting, 650)
                
                if supports:
                    total_supports += len(supports)
                    processed_count += 1
                    print("    ✓ Всего размещено креплений: {}".format(len(supports)))
                else:
                    print("    ○ Крепления не размещены")
                    
            except Exception as e:
                print("    ОШИБКА: {}".format(str(e)))
                skip_reasons['other'] += 1
                import traceback
                print(traceback.format_exc())
        
        # Завершаем транзакцию
        t.Commit()
        
        # Выводим результат
        print("\n" + "=" * 70)
        print("ГОТОВО!")
        print("Найдено соединительных деталей: {}".format(len(fittings)))
        print("Успешно обработано деталей: {}".format(processed_count))
        print("Пропущено деталей: {}".format(len(fittings) - processed_count))
        print("Размещено креплений: {}".format(total_supports))
        print("\n" + "=" * 70)
        print("СТАТИСТИКА ПРИЧИН ПРОПУСКА:")
        print("=" * 70)
        print("Не горизонтальные детали: {}".format(skip_reasons['not_horizontal']))
        print("Нет параметра И_Э_ШиринаЛотка: {}".format(skip_reasons['no_width_param']))
        print("Нет семейства крепления: {}".format(skip_reasons['no_family']))
        print("Нет точки расположения: {}".format(skip_reasons['no_location']))
        print("Нет перекрытия сверху: {}".format(skip_reasons['no_floor_above']))
        print("Нет уровня: {}".format(skip_reasons['no_level']))
        print("Нет параметра ADSK_Размер_Длина: {}".format(skip_reasons['no_length']))
        print("Другие ошибки: {}".format(skip_reasons['other']))
        print("=" * 70)
        
        # Формируем сообщение для диалога
        skip_details = []
        if skip_reasons['not_horizontal'] > 0:
            skip_details.append("Не горизонтальные: {}".format(skip_reasons['not_horizontal']))
        if skip_reasons['no_width_param'] > 0:
            skip_details.append("Нет параметра ширины: {}".format(skip_reasons['no_width_param']))
        if skip_reasons['no_family'] > 0:
            skip_details.append("Нет семейства крепления: {}".format(skip_reasons['no_family']))
        if skip_reasons['no_floor_above'] > 0:
            skip_details.append("Нет перекрытия сверху: {}".format(skip_reasons['no_floor_above']))
        if skip_reasons['no_length'] > 0:
            skip_details.append("Нет параметра длины: {}".format(skip_reasons['no_length']))
        
        skip_message = "\n".join(skip_details) if skip_details else "Нет данных"
        
        TaskDialog.Show(
            "Расстановка креплений завершена",
            "Найдено соединительных деталей: {}\n"
            "Успешно обработано: {}\n"
            "Пропущено: {}\n"
            "Размещено креплений: {}\n\n"
            "Причины пропуска:\n{}".format(
                len(fittings),
                processed_count,
                len(fittings) - processed_count,
                total_supports,
                skip_message
            )
        )

except Exception as e:
    if t.HasStarted():
        t.RollBack()
    print("\n" + "=" * 70)
    print("КРИТИЧЕСКАЯ ОШИБКА!")
    print("=" * 70)
    print(str(e))
    import traceback
    print("\nПодробности:")
    print(traceback.format_exc())
    
    TaskDialog.Show("Ошибка", "Произошла ошибка:\n{}".format(str(e)))