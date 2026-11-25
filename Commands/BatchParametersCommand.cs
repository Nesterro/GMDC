using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GMDC.UI;
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
        try
        {
            ExternalDefinition[] LoadDefinitions(string path)
            {
                var lastPath = app.SharedParametersFilename;
                try
                {
                    app.SharedParametersFilename = path;
                    var file = app.OpenSharedParameterFile()
                        ?? throw new InvalidOperationException("Revit не смог прочитать файл общих параметров.");
                    return file.Groups.Cast<DefinitionGroup>()
                        .SelectMany(group => group.Definitions.OfType<ExternalDefinition>())
                        .GroupBy(definition => definition.GUID)
                        .Select(group => group.First())
                        .OrderBy(definition => definition.OwnerGroup.Name)
                        .ThenBy(definition => definition.Name)
                        .ToArray();
                }
                catch
                {
                    app.SharedParametersFilename = lastPath;
                    throw;
                }
            }

            var categories = doc.IsFamilyDocument
                ? Array.Empty<Category>()
                : doc.Settings.Categories.Cast<Category>()
                    .Where(category => category.AllowsBoundParameters)
                    .OrderBy(category => category.Name).ToArray();
            var groups = ParameterUtils.GetAllBuiltInGroups()
                .Where(id => !doc.IsFamilyDocument || doc.FamilyManager.IsUserAssignableParameterGroup(id))
                .ToArray();
            var window = new BatchParametersWindow(doc, previousPath, categories, groups, LoadDefinitions);
            new WindowInteropHelper(window).Owner = uiapp.MainWindowHandle;
            if (window.ShowDialog() != true)
                return Result.Cancelled;

            var assignments = window.Assignments;
            var selectedCategories = window.Categories;
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
            foreach (var assignment in assignments)
            {
                var definition = assignment.Definition;
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
                        doc.FamilyManager.AddParameter(definition, assignment.Group, assignment.IsInstance);
                    else
                    {
                        var categorySet = app.Create.NewCategorySet();
                        foreach (var category in selectedCategories)
                            categorySet.Insert(category);
                        var binding = assignment.IsInstance
                            ? (ElementBinding)app.Create.NewInstanceBinding(categorySet)
                            : app.Create.NewTypeBinding(categorySet);
                        if (!doc.ParameterBindings.Insert(definition, binding, assignment.Group))
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
            if (app.SharedParametersFilename != previousPath)
                app.SharedParametersFilename = previousPath;
        }
    }
}
