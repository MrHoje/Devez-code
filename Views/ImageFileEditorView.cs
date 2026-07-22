using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DevezCode.Views;

/// <summary>이미지 파일(png/jpg 등) 뷰어 — 중앙 파일 탭에서 읽기 전용 표시. 코드 전용 UserControl.</summary>
public sealed class ImageFileEditorView : UserControl, IFileTabEditor
{
    public event EventHandler? CloseRequested;
#pragma warning disable CS0067 // 이미지는 dirty 없음 — IFileTabEditor 요구 이벤트라 선언만 하고 발화하지 않음
    public event EventHandler? DirtyChanged;
#pragma warning restore CS0067
    public event EventHandler? Interacted;

    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff" };

    public static bool IsImage(string path) => Exts.Contains(Path.GetExtension(path));

    private string? _path;
    private readonly Image _image;
    private readonly TextBlock _info;

    public ImageFileEditorView()
    {
        var grid = new Grid();
        grid.SetResourceReference(Panel.BackgroundProperty, "BgBrush");

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            Padding = new Thickness(12),
        };
        _image = new Image
        {
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        scroll.Content = _image;
        grid.Children.Add(scroll);

        _info = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8),
            FontSize = 11,
            IsHitTestVisible = false,
        };
        _info.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        grid.Children.Add(_info);

        Content = grid;
        PreviewMouseDown += (_, _) => Interacted?.Invoke(this, EventArgs.Empty);
    }

    public string? FilePath => _path;
    public bool IsDirty => false;

    public bool LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 파일 잠금 방지(로드 후 핸들 해제)
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            _image.Source = bmp;
            _info.Text = $"{Path.GetFileName(path)}  ·  {bmp.PixelWidth}×{bmp.PixelHeight}";
            _path = path;
            Visibility = Visibility.Visible;
            return true;
        }
        catch (Exception ex)
        {
            ConfirmDialog.Alert("이미지 열기", $"이미지를 열 수 없습니다.\n{ex.Message}", iconKey: "IconTriangleAlert");
            return false;
        }
    }

    public bool Save() => true;   // 읽기 전용
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
    public new bool Focus() => base.Focus();

    /// <summary>WPF 네이티브 — 오버레이가 그대로 덮으므로 스냅샷 불필요.</summary>
    public Task<BitmapSource?> CaptureSnapshotAsync() => Task.FromResult<BitmapSource?>(null);
}
