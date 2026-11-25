using Autodesk.Revit.DB;
using Microsoft.Win32;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;

namespace GMDC.UI;

public partial class BatchParametersWindow : Window
{
    private static readonly ModeChoice[] Modes =
    {
        new("Экземпляр", true),
        new("Тип", false)
    };

    private readonly Func<string, ExternalDefinition[]> loadDefinitions;
    private readonly GroupChoice[] groups;
    private readonly GroupChoice defaultGroup;
    private readonly CategoryRow[] categories;
    private readonly bool family;
    private ParameterRow[] parameters = Array.Empty<ParameterRow>();
    private ICollectionView? parameterView;
    private ICollectionView? categoryView;
    private bool updating;

    internal BatchParametersWindow(Document doc, string configuredPath, Category[] availableCategories,
        ForgeTypeId[] availableGroups, Func<string, ExternalDefinition[]> loadDefinitions)
    {
        InitializeComponent();
        this.loadDefinitions = loadDefinitions;
        family = doc.IsFamilyDocument;
        TargetLabel.Text = (family ? "Семейство · " : "Проект · ") + doc.Title;
        groups = availableGroups.Select(id => new GroupChoice(LabelUtils.GetLabelForGroup(id), id))
            .OrderBy(item => item.Label).ToArray();
        defaultGroup = groups.FirstOrDefault(item => item.Id == GroupTypeId.Data) ?? groups.First();
        categories = availableCategories.Select(category => new CategoryRow(category)).ToArray();
        foreach (var row in categories)
            row.PropertyChanged += SelectionChanged;
        categoryView = CollectionViewSource.GetDefaultView(categories);
        categoryView.Filter = item => Matches(((CategoryRow)item).Name, CategorySearch.Text);
        CategoryList.ItemsSource = categoryView;
        if (family)
        {
            CategoryPane.Visibility = System.Windows.Visibility.Collapsed;
            CategoriesColumn.Width = new GridLength(0);
            Width = 865;
        }

        BulkMode.ItemsSource = Modes;
        BulkMode.SelectedIndex = 0;
        BulkGroup.ItemsSource = groups;
        BulkGroup.SelectedItem = defaultGroup;
        if (System.IO.File.Exists(configuredPath))
            LoadFile(configuredPath);
        RefreshSelection();
    }

    internal ParameterAssignment[] Assignments => parameters.Where(row => row.Selected)
        .Select(row => new ParameterAssignment(row.Definition, row.Group.Id, row.Mode.IsInstance)).ToArray();

    internal Category[] Categories => categories.Where(row => row.Selected).Select(row => row.Category).ToArray();

    private void BrowseFile(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите файл общих параметров",
            Filter = "Файл общих параметров (*.txt)|*.txt|Все файлы (*.*)|*.*",
            FileName = System.IO.File.Exists(FilePath.Text) ? FilePath.Text : string.Empty
        };
        if (dialog.ShowDialog(this) == true)
            LoadFile(dialog.FileName);
    }

    private void LoadFile(string path)
    {
        ExternalDefinition[] definitions;
        try
        {
            definitions = loadDefinitions(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Файл общих параметров", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var previous = parameters.ToDictionary(row => row.Id);
        foreach (var row in parameters)
            row.PropertyChanged -= SelectionChanged;
        parameters = definitions.Select(definition =>
        {
            var row = new ParameterRow(definition, groups, defaultGroup);
            if (previous.TryGetValue(definition.GUID, out var old))
            {
                row.Selected = old.Selected;
                row.Mode = old.Mode;
                row.Group = old.Group;
            }
            row.PropertyChanged += SelectionChanged;
            return row;
        }).ToArray();
        parameterView = CollectionViewSource.GetDefaultView(parameters);
        parameterView.Filter = item =>
        {
            var row = (ParameterRow)item;
            return (SelectedOnly.IsChecked != true || row.Selected) &&
                (Matches(row.Name, ParameterSearch.Text) || Matches(row.SourceGroup, ParameterSearch.Text));
        };
        ParameterTable.ItemsSource = parameterView;
        FilePath.Text = path;
        FilePath.ToolTip = path;
        RefreshSelection();
    }

    private static bool Matches(string value, string search) =>
        value.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private void FilterParameters(object sender, RoutedEventArgs args) => parameterView?.Refresh();
    private void FilterCategories(object sender, RoutedEventArgs args) => categoryView?.Refresh();

    private void SelectVisible(object sender, RoutedEventArgs args) =>
        SetSelection(parameterView?.Cast<CheckableRow>().ToArray() ?? Array.Empty<CheckableRow>(), true);

    private void ClearParameters(object sender, RoutedEventArgs args) => SetSelection(parameters, false);

    private void SelectCategories(object sender, RoutedEventArgs args) =>
        SetSelection(categoryView?.Cast<CheckableRow>().ToArray() ?? Array.Empty<CheckableRow>(), true);

    private void ClearCategories(object sender, RoutedEventArgs args) => SetSelection(categories, false);

    private void SetSelection(IEnumerable<CheckableRow> rows, bool selected)
    {
        updating = true;
        try
        {
            foreach (var row in rows)
                row.Selected = selected;
        }
        finally
        {
            updating = false;
            RefreshSelection();
        }
    }

    private void SelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!updating && args.PropertyName == nameof(CheckableRow.Selected))
            RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (SelectedOnly.IsChecked == true)
            parameterView?.Refresh();
        var count = parameters.Count(row => row.Selected);
        var categoryCount = categories.Count(row => row.Selected);
        Summary.Text = parameters.Length == 0 && System.IO.File.Exists(FilePath.Text)
            ? "В файле нет общих параметров"
            : family
                ? $"Отмечено параметров: {count}"
                : $"Отмечено параметров: {count}     Выбрано категорий: {categoryCount}";
        ApplyBulk.IsEnabled = count > 0;
        AddButton.IsEnabled = count > 0 && (family || categoryCount > 0);
    }

    private void ApplyBulkSettings(object sender, RoutedEventArgs args)
    {
        var mode = (ModeChoice)BulkMode.SelectedItem;
        var group = (GroupChoice)BulkGroup.SelectedItem;
        foreach (var row in parameters.Where(row => row.Selected))
        {
            row.Mode = mode;
            row.Group = group;
        }
    }

    private void Confirm(object sender, RoutedEventArgs args) => DialogResult = true;

    internal sealed record ParameterAssignment(ExternalDefinition Definition, ForgeTypeId Group, bool IsInstance);

    private sealed record GroupChoice(string Label, ForgeTypeId Id)
    {
        public override string ToString() => Label;
    }

    private sealed record ModeChoice(string Label, bool IsInstance)
    {
        public override string ToString() => Label;
    }

    private abstract class CheckableRow : INotifyPropertyChanged
    {
        private bool selected;
        public bool Selected
        {
            get => selected;
            set => Set(ref selected, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    private sealed class ParameterRow : CheckableRow
    {
        private GroupChoice group;
        private ModeChoice mode = BatchParametersWindow.Modes[0];

        public ParameterRow(ExternalDefinition definition, GroupChoice[] groups, GroupChoice defaultGroup)
        {
            Definition = definition;
            Id = definition.GUID;
            Groups = groups;
            group = defaultGroup;
        }

        public ExternalDefinition Definition { get; }
        public Guid Id { get; }
        public string Name => Definition.Name;
        public string SourceGroup => Definition.OwnerGroup.Name;
        public GroupChoice[] Groups { get; }
        public ModeChoice[] Modes => BatchParametersWindow.Modes;
        public GroupChoice Group { get => group; set => Set(ref group, value); }
        public ModeChoice Mode { get => mode; set => Set(ref mode, value); }
    }

    private sealed class CategoryRow(Category category) : CheckableRow
    {
        public Category Category { get; } = category;
        public string Name => Category.Name;
    }
}
