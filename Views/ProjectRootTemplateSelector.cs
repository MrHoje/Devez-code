using System.Windows;
using System.Windows.Controls;
using DevezCode.Models;

namespace DevezCode.Views;

public sealed class ProjectRootTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FolderTemplate { get; set; }
    public DataTemplate? ProjectTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item switch
        {
            ProjectFolderItem => FolderTemplate,
            ProjectItem => ProjectTemplate,
            _ => base.SelectTemplate(item, container),
        };
}