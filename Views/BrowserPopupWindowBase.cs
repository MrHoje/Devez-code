using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DevezCode.Views;

/// <summary>DevezCode 커스텀 헤더를 사용하는 공통 WebView2 팝업 창.</summary>
public abstract class BrowserPopupWindowBase : Window
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected readonly WebView2 _view = new();
    private readonly ContentControl _footerHost;

    protected BrowserPopupWindowBase(Window? owner, string windowTitle, double height)
    {
        Owner = owner;
        Title = windowTitle;
        Width = 520;
        Height = height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner != null
            ? WindowStartupLocation.CenterOwner
            : WindowStartupLocation.CenterScreen;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 32,
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false,
        });
        SetResourceReference(FontFamilyProperty, "PretendardFont");
        SetResourceReference(BackgroundProperty, "BgBrush");

        bool dark = App.CurrentTheme == "dark";
        _view.DefaultBackgroundColor = dark
            ? System.Drawing.Color.FromArgb(0x1e, 0x1e, 0x1e)
            : System.Drawing.Color.White;

        var logo = new TextBlock
        {
            Text = "DevezCode",
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 1, 0, 0),
        };
        logo.SetResourceReference(TextBlock.FontFamilyProperty, "BrunoAceSCFont");
        logo.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var closeButton = new Button
        {
            ToolTip = "닫기",
            Content = new TextBlock { Text = "\u2715", FontSize = 13 },
            Margin = new Thickness(0, 0, 4, 0),
        };
        closeButton.SetResourceReference(Button.StyleProperty, "WinCloseBtn");
        closeButton.Click += (_, _) => Close();

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(closeButton, 1);
        headerGrid.Children.Add(logo);
        headerGrid.Children.Add(closeButton);

        var header = new Border
        {
            Height = 32,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerGrid,
        };
        header.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        header.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        header.MouseLeftButtonDown += Header_MouseLeftButtonDown;

        _footerHost = new ContentControl
        {
            Visibility = Visibility.Collapsed,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(_view, 1);
        Grid.SetRow(_footerHost, 2);
        layout.Children.Add(header);
        layout.Children.Add(_view);
        layout.Children.Add(_footerHost);

        var chrome = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = layout,
        };
        chrome.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        chrome.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Content = chrome;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch { }
    }

    protected void SetFooterContent(UIElement content)
    {
        _footerHost.Content = content;
        _footerHost.Visibility = Visibility.Visible;
    }

    protected async Task InitializeBrowserAsync()
    {
        var userDataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevezCode", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
        await _view.EnsureCoreWebView2Async(env);
        _view.CoreWebView2.Profile.PreferredColorScheme = App.CurrentTheme == "dark"
            ? CoreWebView2PreferredColorScheme.Dark
            : CoreWebView2PreferredColorScheme.Light;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try { _view.Dispose(); } catch { }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1) return;
        try { DragMove(); } catch { }
    }
}
