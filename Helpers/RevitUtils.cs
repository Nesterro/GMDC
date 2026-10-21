using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace GMDC.Helpers
{
    public static class RevitUtils
    {
        // Unit conversion constants
        public const double MmInFoot = 304.8;

        public static double MmToFeet(double mm)
        {
            return mm / MmInFoot;
        }

        public static double FeetToMm(double feet)
        {
            return feet * MmInFoot;
        }

        /// <summary>
        /// Looks up a parameter on an element instance or its type.
        /// </summary>
        public static Parameter? LookupParameterInElementOrType(Element element, string name)
        {
            Parameter? p = element.LookupParameter(name);
            if (p == null)
            {
                ElementId typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    Element type = element.Document.GetElement(typeId);
                    p = type?.LookupParameter(name);
                }
            }
            return p;
        }

        /// <summary>
        /// Gets the value of a parameter formatted as a string.
        /// </summary>
        public static string GetParameterValueString(Element element, string paramName)
        {
            if (paramName == "Width" || paramName == "Ширина")
            {
                // Specialized helper for Width
                var p = element.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                if (p != null) return p.AsValueString() ?? FeetToMm(p.AsDouble()).ToString("F0");
            }
            if (paramName == "Height" || paramName == "Высота")
            {
                // Specialized helper for Height
                var p = element.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                if (p != null) return p.AsValueString() ?? FeetToMm(p.AsDouble()).ToString("F0");
            }
            if (paramName == "Size" || paramName == "Размер")
            {
                var p = element.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE);
                if (p != null) return p.AsString() ?? string.Empty;
            }
            if (paramName == "Type Name" || paramName == "Имя типа")
            {
                return element.Document.GetElement(element.GetTypeId())?.Name ?? element.Name;
            }
            if (paramName == "Family Name" || paramName == "Имя семейства")
            {
                return (element.Document.GetElement(element.GetTypeId()) as ElementType)?.FamilyName ?? string.Empty;
            }

            Parameter? param = LookupParameterInElementOrType(element, paramName);
            if (param == null) return string.Empty;

            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString() ?? string.Empty;
                case StorageType.Integer:
                    return param.AsValueString() ?? param.AsInteger().ToString();
                case StorageType.Double:
                    return param.AsValueString() ?? param.AsDouble().ToString("F2");
                case StorageType.ElementId:
                    ElementId id = param.AsElementId();
                    if (id == ElementId.InvalidElementId) return string.Empty;
                    Element elem = element.Document.GetElement(id);
                    return elem?.Name ?? id.ToString();
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Writes a value to a parameter of the element.
        /// </summary>
        public static bool SetParameterValue(Element element, string paramName, string value)
        {
            Parameter p = element.LookupParameter(paramName);
            if (p == null || p.IsReadOnly) return false;

            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.Set(value);
                case StorageType.Integer:
                    if (int.TryParse(value, out int iVal))
                    {
                        return p.Set(iVal);
                    }
                    return p.SetValueString(value);
                case StorageType.Double:
                    if (double.TryParse(value, out double dVal))
                    {
                        // SetValueString automatically converts from UI units (e.g. mm) to internal units (feet)
                        return p.SetValueString(value);
                    }
                    return p.SetValueString(value);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Retrieves all writable string parameters of an element.
        /// </summary>
        public static List<string> GetWritableStringParameters(Element element)
        {
            var list = new List<string>();
            foreach (Parameter p in element.Parameters)
            {
                if (!p.IsReadOnly && p.StorageType == StorageType.String)
                {
                    list.Add(p.Definition.Name);
                }
            }
            // Add some standard built-ins if they are writable
            list.Sort();
            return list;
        }

        /// <summary>
        /// Retrieves all parameters (instance and type) that are useful as naming sources.
        /// </summary>
        public static List<string> GetNamingSourceParameters(Element element)
        {
            var set = new HashSet<string> { "Type Name", "Family Name", "Width", "Height", "Size", "Comments", "Mark" };
            foreach (Parameter p in element.Parameters)
            {
                set.Add(p.Definition.Name);
            }
            ElementId typeId = element.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                Element type = element.Document.GetElement(typeId);
                if (type != null)
                {
                    foreach (Parameter p in type.Parameters)
                    {
                        set.Add(p.Definition.Name);
                    }
                }
            }
            var list = new List<string>(set);
            list.Sort();
            return list;
        }
    }
}
