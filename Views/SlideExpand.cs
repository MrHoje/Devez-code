using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DevezCode.Views;

/// <summary>TreeViewItem 자식 목록(ItemsPresenter)의 폴더 접기/펼치기에 높이 슬라이드 애니메이션을 준다.
/// <para>사용법: 대상 ItemsPresenter 에 <c>v:SlideExpand.IsEnabled="True"</c> 를 붙이고,
/// 템플릿의 <c>IsExpanded=False → Visibility=Collapsed</c> 트리거는 제거한다(이 동작이 가시성을 관리).</para>
/// 펼치면 0→콘텐츠높이(EaseOut), 접히면 콘텐츠→0(EaseIn) 후 Collapsed. 클리핑은 자동 설정.</summary>
public static class SlideExpand
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(SlideExpand),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject o, bool v) => o.SetValue(IsEnabledProperty, v);
    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);

    private sealed class State { public TreeViewItem Tvi = null!; public EventHandler Handler = null!; }
    private static readonly ConditionalWeakTable<FrameworkElement, State> _states = new();

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || !(bool)e.NewValue) return;
        fe.ClipToBounds = true;               // 접힌 동안 부분 행이 넘쳐 보이지 않게
        fe.Loaded += OnLoaded;
        fe.Unloaded += OnUnloaded;
    }

    // 컨테이너 실현(재활용 포함) → 소속 TreeViewItem 을 찾아 초기 상태를 즉시 반영하고 IsExpanded 를 구독.
    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        var tvi = FindAncestor<TreeViewItem>(fe);
        if (tvi == null) return;

        SetImmediate(fe, tvi.IsExpanded);     // 애니메이션 없이 현재 상태로

        if (_states.TryGetValue(fe, out _)) return;   // 이미 구독됨(중복 방지)
        var st = new State { Tvi = tvi };
        st.Handler = (_, _) => Animate(fe, tvi.IsExpanded);
        DpDescriptor().AddValueChanged(tvi, st.Handler);
        _states.Add(fe, st);
    }

    // 재활용/해제 → 이전 TreeViewItem 구독 해제(누수 방지). 다시 Loaded 되면 새로 구독.
    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        if (!_states.TryGetValue(fe, out var st)) return;
        DpDescriptor().RemoveValueChanged(st.Tvi, st.Handler);
        _states.Remove(fe);
    }

    private static DependencyPropertyDescriptor DpDescriptor() =>
        DependencyPropertyDescriptor.FromProperty(TreeViewItem.IsExpandedProperty, typeof(TreeViewItem));

    private static void SetImmediate(FrameworkElement fe, bool open)
    {
        fe.BeginAnimation(FrameworkElement.HeightProperty, null);
        if (open) { fe.Visibility = Visibility.Visible; fe.ClearValue(FrameworkElement.HeightProperty); }
        else { fe.Visibility = Visibility.Collapsed; }
    }

    private static void Animate(FrameworkElement fe, bool open)
    {
        fe.BeginAnimation(FrameworkElement.HeightProperty, null);
        if (open)
        {
            fe.Visibility = Visibility.Visible;
            fe.ClearValue(FrameworkElement.HeightProperty);
            fe.UpdateLayout();                 // 자식 실현·측정
            double target = fe.ActualHeight;
            if (target <= 0) { fe.ClearValue(FrameworkElement.HeightProperty); return; }
            fe.Height = 0;
            var a = new DoubleAnimation
            {
                From = 0, To = target, Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            a.Completed += (_, _) => { fe.BeginAnimation(FrameworkElement.HeightProperty, null); fe.ClearValue(FrameworkElement.HeightProperty); };
            fe.BeginAnimation(FrameworkElement.HeightProperty, a);
        }
        else
        {
            double from = fe.ActualHeight;
            fe.Height = from;
            var a = new DoubleAnimation
            {
                From = from, To = 0, Duration = TimeSpan.FromMilliseconds(130),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            a.Completed += (_, _) => { fe.BeginAnimation(FrameworkElement.HeightProperty, null); fe.Visibility = Visibility.Collapsed; fe.ClearValue(FrameworkElement.HeightProperty); };
            fe.BeginAnimation(FrameworkElement.HeightProperty, a);
        }
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }
}
