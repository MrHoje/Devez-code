using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>MCP 서버 관리 오버레이. <see cref="CloseRequested"/> 로 닫기 요청.
/// opencode / Claude Code / Codex 세 에이전트의 MCP 설정을 탭으로 묶어 한 창에서 관리.
/// 각 백엔드(IMcpBackend) 는 자기 설정 파일을 직접 다루고, 탭 전환 시 디스크에서 새로 읽는다.
/// [저장] 시 현재 활성 백엔드에 다른 필드는 보존하고 mcp 섹션만 갱신.</summary>
public partial class McpManagerDialog : UserControl
{
    public event EventHandler? CloseRequested;

    private readonly ObservableCollection<McpServer> _servers = new();
    private readonly List<McpServer> _original = new();  // 현재 백엔드의 디스크 스냅샷
    private readonly Dictionary<string, Button> _tabButtons = new();
    private bool _isLoading;
    private bool _suppressTabDirtyCheck;
    private IMcpBackend? _currentBackend;

    public McpManagerDialog()
    {
        InitializeComponent();
        ServerList.ItemsSource = _servers;
        BuildAgentTabs();
        // 첫 사용 가능 백엔드를 자동 선택
        var firstAvailable = McpBackendRegistry.All.FirstOrDefault(b => b.IsAvailable) ?? McpBackendRegistry.All[0];
        SwitchBackend(firstAvailable);
    }

    // ── 에이전트 탭 생성 ─────────────────────────────────────────
    private void BuildAgentTabs()
    {
        AgentTabsPanel.Children.Clear();
        _tabButtons.Clear();
        foreach (var b in McpBackendRegistry.All)
        {
            var btn = new Button
            {
                Content = MakeTabContent(b),
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand,
                IsEnabled = b.IsAvailable,
                ToolTip = b.IsAvailable ? b.ConfigPathHint : $"{b.DisplayName} CLI 가 설치되어 있지 않습니다",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
            };
            btn.SetResourceReference(Button.StyleProperty, "TitleBarChipButton");
            btn.Tag = b.Id;
            btn.Click += AgentTab_Click;
            AgentTabsPanel.Children.Add(btn);
            _tabButtons[b.Id] = btn;
        }
    }

    private static object MakeTabContent(IMcpBackend b)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock
        {
            Text = b.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!b.IsAvailable)
        {
            sp.Children.Add(new TextBlock
            {
                Text = " (미설치)",
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        return sp;
    }

    private void AgentTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var backend = McpBackendRegistry.Get(id);
        if (backend == null || backend == _currentBackend) return;

        if (!_suppressTabDirtyCheck && HasUnsavedChanges())
        {
            var save = ConfirmDialog.Show("저장되지 않은 변경사항",
                $"'{_currentBackend?.DisplayName}' 탭에 저장되지 않은 변경이 있습니다.\n저장하고 '{backend.DisplayName}' 탭으로 이동할까요?",
                okLabel: "저장 후 이동", iconKey: "IconServer");
            if (save)
            {
                SaveBtn_Click(this, new RoutedEventArgs());
                // 저장 실패 시 사용자가 폼을 닫지 않았다면 그냥 머문다
                if (HasUnsavedChanges()) return;
            }
            else
            {
                // 그냥 폐기하고 이동
            }
        }
        SwitchBackend(backend);
    }

    private void SwitchBackend(IMcpBackend backend)
    {
        _currentBackend = backend;
        ConfigPathHint.Text = "…\\" + backend.ConfigPathHint;
        // 탭 외형 갱신: 활성은 Primary 텍스트, 비활성은 Muted
        var primary = (Brush)FindResource("PrimaryBrush");
        var muted   = (Brush)FindResource("TextMutedBrush");
        foreach (var (id, btn) in _tabButtons)
        {
            var isActive = id == backend.Id;
            // 활성일 땐 밑줄 + Primary, 비활성은 muted
            btn.Foreground = isActive ? primary : muted;
            btn.FontWeight = isActive ? FontWeights.Bold : FontWeights.SemiBold;
        }

        // OAuth 섹션은 opencode 에서만 의미 있음
        OAuthSection.Visibility = backend.Id == "opencode" ? Visibility.Visible : Visibility.Collapsed;

        LoadFromDisk();
        _ = RefreshStatusAsync();
    }

    // ── 디스크 ↔ 메모리 ──────────────────────────────────────────
    private void LoadFromDisk()
    {
        _isLoading = true;
        try
        {
            _servers.Clear();
            _original.Clear();
            if (_currentBackend == null) return;
            foreach (var s in _currentBackend.Load())
            {
                _servers.Add(s);
                _original.Add(s.Clone());
            }
            if (_servers.Count > 0)
                ServerList.SelectedIndex = 0;
            else
                ShowEmpty();
        }
        finally { _isLoading = false; }
        UpdateEditorVisibility();
    }

    private void ShowEmpty()
    {
        ServerList.SelectedIndex = -1;
        EmptyState.Visibility = Visibility.Visible;
        EditorScroll.Visibility = Visibility.Collapsed;
        DeleteBtn.Visibility = Visibility.Collapsed;
    }

    private void UpdateEditorVisibility()
    {
        var has = ServerList.SelectedItem is McpServer;
        var isReadOnly = (ServerList.SelectedItem as McpServer)?.IsReadOnly == true;

        EmptyState.Visibility   = has ? Visibility.Collapsed : Visibility.Visible;
        EditorScroll.Visibility = has ? Visibility.Visible  : Visibility.Collapsed;
        DeleteBtn.Visibility    = (has && !isReadOnly) ? Visibility.Visible : Visibility.Collapsed;

        // 읽기 전용 서버: 이름·명령·URL·헤더 + 타입 토글 + 활성 토글 비활성
        NameBox.IsReadOnly   = isReadOnly;
        CommandBox.IsReadOnly = isReadOnly;
        EnvBox.IsReadOnly    = isReadOnly;
        UrlBox.IsReadOnly    = isReadOnly;
        HeaderBox.IsReadOnly = isReadOnly;
        TypeLocalBtn.IsHitTestVisible  = !isReadOnly;
        TypeRemoteBtn.IsHitTestVisible = !isReadOnly;
        EnabledBox.IsHitTestVisible    = !isReadOnly;

        ReadOnlyHint.Visibility = isReadOnly ? Visibility.Visible : Visibility.Collapsed;
        if (isReadOnly && ServerList.SelectedItem is McpServer ro)
            ReadOnlyHintText.Text = string.IsNullOrEmpty(ro.ReadOnlyReason)
                ? "이 서버는 외부에서 제공되어 편집할 수 없습니다."
                : ro.ReadOnlyReason;
    }

    // ── 왼쪽 목록 선택 ──────────────────────────────────────────
    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        UpdateEditorVisibility();
        SyncTypeButtons();
        SyncOAuthRadios();
    }

    private void SyncTypeButtons()
    {
        var srv = ServerList.SelectedItem as McpServer;
        var isRemote = srv?.Type == McpServerType.Remote;
        var primary = (Brush)FindResource("PrimaryBrush");
        var line    = (Brush)FindResource("LineBrush");
        var soft    = (Brush)FindResource("PrimarySoftBrush");
        var panel   = (Brush)FindResource("PanelBrush");

        TypeLocalBtn.Background  = isRemote ? panel  : soft;
        TypeLocalBtn.BorderBrush = isRemote ? line   : primary;
        TypeRemoteBtn.Background = isRemote ? soft   : panel;
        TypeRemoteBtn.BorderBrush= isRemote ? primary: line;

        LocalPanel.Visibility  = isRemote ? Visibility.Collapsed : Visibility.Visible;
        RemotePanel.Visibility = isRemote ? Visibility.Visible   : Visibility.Collapsed;
    }

    private void SyncOAuthRadios()
    {
        var srv = ServerList.SelectedItem as McpServer;
        if (srv == null) return;
        var mode = srv.OAuthMode ?? McpOAuthMode.Auto;
        OAuthAutoBtn.IsChecked     = mode == McpOAuthMode.Auto;
        OAuthDisabledBtn.IsChecked = mode == McpOAuthMode.Disabled;
        OAuthExplicitBtn.IsChecked = mode == McpOAuthMode.Explicit;
        OAuthExplicitPanel.Visibility = mode == McpOAuthMode.Explicit
            ? Visibility.Visible : Visibility.Collapsed;
        if (OAuthExplicitPanel.Visibility == Visibility.Visible &&
            string.IsNullOrEmpty(OAuthSecretBox.Password) &&
            !string.IsNullOrEmpty(srv.OAuthClientSecret))
        {
            OAuthSecretBox.Password = srv.OAuthClientSecret;
        }
    }

    // ── 액션 핸들러 ────────────────────────────────────────────
    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        var baseName = "server";
        var name = baseName;
        for (int i = 2; _servers.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{baseName}{i}";

        var srv = new McpServer
        {
            Name = name,
            Type = McpServerType.Local,
            Enabled = true,
        };
        _servers.Add(srv);
        _original.Add(srv.Clone());
        ServerList.SelectedItem = srv;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not McpServer srv) return;
        if (srv.IsReadOnly) return; // first-party/plugin 서버는 삭제 차단
        if (!ConfirmDialog.Show("MCP 서버 삭제", $"'{srv.Name}' 서버를 삭제할까요?", "삭제", danger: true))
            return;
        _original.RemoveAll(s => ReferenceEquals(s, srv) || s.Name == srv.Name);
        _servers.Remove(srv);
        ShowEmpty();
    }

    private void TypeBtn_Click(object sender, MouseButtonEventArgs e)
    {
        if (ServerList.SelectedItem is not McpServer srv) return;
        if (sender is Border b && b.Tag is string tag)
        {
            srv.Type = tag == "remote" ? McpServerType.Remote : McpServerType.Local;
            SyncTypeButtons();
        }
    }

    private void OAuthMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        if (ServerList.SelectedItem is not McpServer srv) return;
        if (sender is not RadioButton rb || rb.IsChecked != true) return;

        McpOAuthMode mode = rb == OAuthAutoBtn     ? McpOAuthMode.Auto
                          : rb == OAuthDisabledBtn ? McpOAuthMode.Disabled
                          :                          McpOAuthMode.Explicit;
        srv.OAuthMode = mode;
        OAuthExplicitPanel.Visibility = mode == McpOAuthMode.Explicit
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OAuthSecretBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is McpServer srv)
            srv.OAuthClientSecret = OAuthSecretBox.Password;
    }

    private async void RefreshStatusBtn_Click(object sender, RoutedEventArgs e)
        => await RefreshStatusAsync();

    private async Task RefreshStatusAsync()
    {
        if (_servers.Count == 0 || _currentBackend == null) return;
        RefreshStatusBtn.IsEnabled = false;
        try
        {
            await _currentBackend.RefreshStatusAsync(_servers);
        }
        finally
        {
            RefreshStatusBtn.IsEnabled = true;
        }
    }

    // ── 저장 / 취소 ────────────────────────────────────────────
    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBackend == null) return;

        var empty = _servers.Where(s => string.IsNullOrWhiteSpace(s.Name)).ToList();
        if (empty.Count > 0) { ConfirmDialog.Alert("저장 실패", "서버 이름이 비어 있습니다."); return; }
        var dups = _servers.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                           .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dups.Count > 0) { ConfirmDialog.Alert("저장 실패", $"중복된 서버 이름이 있습니다: {string.Join(", ", dups)}"); return; }
        var missingCmd = _servers
            .Where(s => s.Type == McpServerType.Local && s.Command.Count == 0)
            .Select(s => s.Name).ToList();
        if (missingCmd.Count > 0) { ConfirmDialog.Alert("저장 실패", $"로컬 서버는 실행 커맨드가 필요합니다: {string.Join(", ", missingCmd)}"); return; }
        var missingUrl = _servers
            .Where(s => s.Type == McpServerType.Remote && string.IsNullOrWhiteSpace(s.Url))
            .Select(s => s.Name).ToList();
        if (missingUrl.Count > 0) { ConfirmDialog.Alert("저장 실패", $"원격 서버는 URL이 필요합니다: {string.Join(", ", missingUrl)}"); return; }

        foreach (var s in _servers) s.Sanitize();

        try
        {
            _currentBackend.Save(_servers);
            ConfirmDialog.Alert("저장 완료",
                $"{_currentBackend.ConfigPathHint} 에 저장했습니다.\n({_currentBackend.DisplayName} 세션은 다음 시작부터 새 설정을 사용합니다)");
            _original.Clear();
            foreach (var s in _servers) _original.Add(s.Clone());
        }
        catch (Exception ex)
        {
            ConfirmDialog.Alert("저장 실패", ex.Message);
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (HasUnsavedChanges())
        {
            var save = ConfirmDialog.Show("저장되지 않은 변경사항",
                "저장되지 않은 변경사항이 있습니다.\n저장하시겠습니까? (취소 시 변경사항이 사라집니다)",
                okLabel: "저장", iconKey: "IconServer");
            if (save) SaveBtn_Click(sender, e);
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool HasUnsavedChanges()
    {
        if (_servers.Count != _original.Count) return true;
        for (int i = 0; i < _servers.Count; i++)
        {
            var a = _servers[i]; var b = _original[i];
            if (a.Name != b.Name || a.Type != b.Type || a.Enabled != b.Enabled || a.TimeoutMs != b.TimeoutMs) return true;
            if (a.Url != b.Url) return true;
            if (!a.Command.SequenceEqual(b.Command)) return true;
            if (!DictEqual(a.Environment, b.Environment)) return true;
            if (!DictEqual(a.Headers, b.Headers)) return true;
            if (a.OAuthMode != b.OAuthMode) return true;
            if (a.OAuthMode == McpOAuthMode.Explicit)
            {
                if (a.OAuthClientId != b.OAuthClientId) return true;
                if (a.OAuthClientSecret != b.OAuthClientSecret) return true;
                if (a.OAuthScope != b.OAuthScope) return true;
            }
        }
        return false;
    }

    private static bool DictEqual(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var v2) || v != v2) return false;
        return true;
    }

    /// <summary>ESC / 외부 호출용 닫기.</summary>
    public void TryClose() => CancelBtn_Click(this, new RoutedEventArgs());

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
        => Window.GetWindow(this)?.DragMove();
}
