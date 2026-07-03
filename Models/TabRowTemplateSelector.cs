using System.Windows;
using System.Windows.Controls;

namespace DevezCode.Models;

/// <summary>사이드바 카드의 탭 행(세션/열린 문서)을 타입별 템플릿으로 렌더 — 세션은 SessionTemplate,
/// 파일 문서(FileTabItem)는 FileTemplate. 좌/우 그룹 ItemsControl 이 공유한다.</summary>
public sealed class TabRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SessionTemplate { get; set; }
    public DataTemplate? FileTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        SessionItem => SessionTemplate,
        FileTabItem => FileTemplate,
        _ => base.SelectTemplate(item, container),
    };
}
