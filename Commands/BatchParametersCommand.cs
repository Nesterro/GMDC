using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace GMDC.Commands;

[Transaction(TransactionMode.Manual)]
public class BatchParametersCommand : IExternalCommand
{
    private const string Title = "Пакетное добавление параметров";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiapp = commandData.Application;
        if (uiapp.ActiveUIDocument?.Document is not Document doc)
            return Result.Cancelled;

        var app = uiapp.Application;
        var previousPath = app.SharedParametersFilename;
        var pathChanged = false;
        try
        {
            var file = new OpenFileDialog
            {
                Title = "Выберите файл общих параметров",
                Filter = "Файл общих параметров (*.txt)|*.txt|Все файлы (*.*)|*.*"
            };
            if (file.ShowDialog() != true)
                return Result.Cancelled;

            app.SharedParametersFilename = file.FileName;
            pathChanged = true;
            var shared = app.OpenSharedParameterFile()
                ?? throw new InvalidOperationException("Revit не смог прочитать файл общих параметров.");
            var definitions = shared.Groups.Cast<DefinitionGroup>()
                .SelectMany(group => group.Definitions.OfType<ExternalDefinition>())
                .OrderBy(definition => definition.OwnerGroup.Name)
                .ThenBy(definition => definition.Name)
                .ToArray();

            if (definitions.Length == 0)
            {
                TaskDialog.Show(Title, "В файле нет общих параметров.");
                return Result.Cancelled;
            }

            var categories = doc.IsFamilyDocument
                ? Array.Empty<Category>()
                : doc.Settings.Categories.Cast<Category>()
                    .Where(category => category.AllowsBoundParameters)
                    .OrderBy(category => category.Name).ToArray();
            var groups = ParameterUtils.GetAllBuiltInGroups()
                .Where(id => !doc.IsFamilyDocument || doc.FamilyManager.IsUserAssignableParameterGroup(id))
                .ToArray();
            var window = new ParameterSelectionWindow(definitions, categories, groups, doc.IsFamilyDocument);
            new WindowInteropHelper(window).Owner = uiapp.MainWindowHandle;
            if (window.ShowDialog() != true)
                return Result.Cancelled;

            var selectedDefinitions = window.Definitions;
            var selectedCategories = window.Categories;
            var isInstance = window.IsInstance;
            var existingFamilyParameters = doc.IsFamilyDocument
                ? doc.FamilyManager.Parameters.Cast<FamilyParameter>().ToArray()
                : Array.Empty<FamilyParameter>();
            var bindingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!doc.IsFamilyDocument)
            {
                var iterator = doc.ParameterBindings.ForwardIterator();
                while (iterator.MoveNext())
                    bindingNames.Add(iterator.Key.Name);
            }

            var added = 0;
            var skipped = new List<string>();
            using var transaction = new Transaction(doc, Title);
            transaction.Start();
            foreach (var definition in selectedDefinitions)
            {
                if (existingFamilyParameters.Any(parameter =>
                        string.Equals(parameter.Definition.Name, definition.Name, StringComparison.OrdinalIgnoreCase) ||
                        (parameter.IsShared && parameter.GUID == definition.GUID)) ||
                    bindingNames.Contains(definition.Name) ||
                    (!doc.IsFamilyDocument && SharedParameterElement.Lookup(doc, definition.GUID) != null))
                {
                    skipped.Add($"{definition.Name} — уже есть в документе.");
                    continue;
                }

                using var item = new SubTransaction(doc);
                item.Start();
                try
                {
                    if (doc.IsFamilyDocument)
                        doc.FamilyManager.AddParameter(definition, window.ParameterGroup, isInstance);
                    else
                    {
                        var categorySet = app.Create.NewCategorySet();
                        foreach (var category in selectedCategories)
                            categorySet.Insert(category);
                        var binding = isInstance
                            ? (ElementBinding)app.Create.NewInstanceBinding(categorySet)
                            : app.Create.NewTypeBinding(categorySet);
                        if (!doc.ParameterBindings.Insert(definition, binding, window.ParameterGroup))
                            throw new InvalidOperationException("Revit отклонил привязку.");
                    }

                    if (item.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit отменил добавление.");
                    bindingNames.Add(definition.Name);
                    added++;
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ArgumentException
                    or Autodesk.Revit.Exceptions.InvalidOperationException or InvalidOperationException)
                {
                    if (item.GetStatus() == TransactionStatus.Started)
                        item.RollBack();
                    skipped.Add($"{definition.Name} — {ex.Message}");
                }
            }

            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit отменил пакетное добавление.");

            var summary = new TaskDialog(Title)
            {
                MainInstruction = $"Добавлено: {added}. Пропущено: {skipped.Count}."
            };
            if (skipped.Count > 0)
                summary.ExpandedContent = string.Join(Environment.NewLine, skipped);
            summary.Show();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
        finally
        {
            if (pathChanged)
                app.SharedParametersFilename = previousPath;
        }
    }

    private sealed class ParameterSelectionWindow : Window
    {
        private readonly ListBox parameters = new() { SelectionMode = SelectionMode.Extended };
        private readonly ListBox categories = new() { SelectionMode = SelectionMode.Extended };
        private readonly System.Windows.Controls.ComboBox group = new() { MinWidth = 155 };
        private readonly CheckBox instance = new() { Content = "Параметры экземпляра", IsChecked = true };

        public ExternalDefinition[] Definitions => parameters.SelectedItems.Cast<ParameterChoice>()
            .Select(item => item.Definition).ToArray();
        public Category[] Categories => categories.SelectedItems.Cast<Category>().ToArray();
        public ForgeTypeId ParameterGroup => ((GroupChoice)group.SelectedItem).Id;
        public bool IsInstance => instance.IsChecked == true;

        public ParameterSelectionWindow(ExternalDefinition[] definitions, Category[] availableCategories, ForgeTypeId[] availableGroups, bool family)
        {
            Title = BatchParametersCommand.Title + (family ? " — семейство" : " — проект");
            Width = family ? 600 : 850;
            Height = 580;
            MinWidth = family ? 480 : 650;
            MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var shell = new DockPanel { Margin = new Thickness(16) };
            Content = shell;
            var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);
            shell.Children.Add(footer);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(buttons, Dock.Right);
            footer.Children.Add(buttons);
            var add = new Button { Content = "Добавить", IsDefault = true, MinWidth = 92, Margin = new Thickness(8, 0, 0, 0) };
            add.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(add);
            buttons.Children.Add(new Button { Content = "Отмена", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) });

            var groups = availableGroups
                .Select(id => new GroupChoice(id, LabelUtils.GetLabelForGroup(id)))
                .OrderBy(item => item.Label).ToArray();
            group.ItemsSource = groups;
            group.DisplayMemberPath = nameof(GroupChoice.Label);
            group.SelectedItem = groups.FirstOrDefault(item => item.Id == GroupTypeId.Data) ?? groups.First();
            var options = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            options.Children.Add(new TextBlock { Text = "Группа", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            options.Children.Add(group);
            instance.Margin = new Thickness(16, 0, 0, 0);
            instance.VerticalAlignment = VerticalAlignment.Center;
            instance.Unchecked += (_, _) => instance.Content = "Параметры типа";
            instance.Checked += (_, _) => instance.Content = "Параметры экземпляра";
            options.Children.Add(instance);
            footer.Children.Add(options);

            parameters.ItemsSource = definitions.Select(definition => new ParameterChoice(definition)).ToArray();
            parameters.DisplayMemberPath = nameof(ParameterChoice.Label);
            categories.ItemsSource = availableCategories;
            categories.DisplayMemberPath = nameof(Category.Name);
            var lists = new System.Windows.Controls.Grid();
            lists.ColumnDefinitions.Add(new ColumnDefinition());
            var parameterPane = CreatePane("Параметры из файла", parameters);
            lists.Children.Add(parameterPane);
            if (!family)
            {
                lists.ColumnDefinitions.Add(new ColumnDefinition());
                var categoryPane = CreatePane("Категории проекта", categories);
                categoryPane.Margin = new Thickness(12, 0, 0, 0);
                System.Windows.Controls.Grid.SetColumn(categoryPane, 1);
                lists.Children.Add(categoryPane);
            }
            shell.Children.Add(lists);

            void Refresh() => add.IsEnabled = parameters.SelectedItems.Count > 0 &&
                (family || categories.SelectedItems.Count > 0);
            parameters.SelectionChanged += (_, _) => Refresh();
            categories.SelectionChanged += (_, _) => Refresh();
            Refresh();
        }

        private static DockPanel CreatePane(string label, ListBox list)
        {
            var pane = new DockPanel();
            var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var selectAll = new Button { Content = "Выбрать все", Padding = new Thickness(6, 2, 6, 2) };
            selectAll.Click += (_, _) => list.SelectAll();
            DockPanel.SetDock(selectAll, Dock.Right);
            heading.Children.Add(selectAll);
            heading.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(heading, Dock.Top);
            pane.Children.Add(heading);
            pane.Children.Add(list);
            return pane;
        }

        private sealed record GroupChoice(ForgeTypeId Id, string Label);
        private sealed record ParameterChoice(ExternalDefinition Definition)
        {
            public string Label => $"{Definition.OwnerGroup.Name} / {Definition.Name}";
        }
    }
}
