using System.Windows;
using System.Windows.Controls;

namespace DevezCode.Models;

/// <summary>사이드바 카드의 탭 행(세션/열린 문서/브라우저)을 타입별 템플릿으로 렌더한다.</summary>
public sealed class TabRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SessionTemplate { get; set; }
    public DataTemplate? FileTemplate { get; set; }
    public DataTemplate? BrowserTemplate { get; set; }
    public DataTemplate? DocumentGroupTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        SessionItem => SessionTemplate,
        FileTabItem => FileTemplate,
        BrowserTabItem => BrowserTemplate,
        DocumentGroupItem => DocumentGroupTemplate,
        _ => base.SelectTemplate(item, container),
    };
}
