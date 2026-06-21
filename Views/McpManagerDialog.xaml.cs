using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>MCP 서버 관리 오버레이. <see cref="CloseRequested"/> 로 닫기 요청.
/// 동작: 디스크에 저장된 opencode config 의 mcp 섹션을 로드해 편집·추가·삭제,
/// [저장] 시 다른 필드(plugin, model, ...) 는 보존하고 mcp 만 다시 쓴다.
/// 실시간 상태는 opencode mcp list 로 별도 조회 (저장 트리거 X).</summary>
public partial class McpManagerDialog : UserControl
{
    public event EventHandler? CloseRequested;

    private readonly ObservableCollection<McpServer> _servers = new();
    private readonly List<McpServer> _original = new();  // 취소 시 복원용
    private bool _isLoading;

    public McpManagerDialog()
    {
        InitializeComponent();
        ConfigPathHint.Text = $"…\\{System.IO.Path.GetFileName(OpenCodeConfigService.ConfigPath)}";
        ServerList.ItemsSource = _servers;
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
            foreach (var s in OpenCodeConfigService.Load())
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
        EmptyState.Visibility   = has ? Visibility.Collapsed : Visibility.Visible;
        EditorScroll.Visibility = has ? Visibility.Visible  : Visibility.Collapsed;
        DeleteBtn.Visibility    = has ? Visibility.Visible  : Visibility.Collapsed;
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
        var primary = (System.Windows.Media.Brush)FindResource("PrimaryBrush");
        var line    = (System.Windows.Media.Brush)FindResource("LineBrush");
        var soft    = (System.Windows.Media.Brush)FindResource("PrimarySoftBrush");
        var panel   = (System.Windows.Media.Brush)FindResource("PanelBrush");

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
        // PasswordBox 는 바인딩이 까다로워서 한 번만 채워 넣고 사용자가 변경하면 다시 덮어쓰지 않는다.
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
        // 중복되지 않는 기본 이름 생성
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
        if (_servers.Count == 0) return;
        RefreshStatusBtn.IsEnabled = false;
        try
        {
            await OpenCodeConfigService.RefreshStatusAsync(_servers);
            // UI 갱신: ListBox 가 SelectedItem 의 Status 변경을 감지하도록 강제 새로고침
            // (단순 ObservableCollection.Add/Remove 가 아니라 속성 변경이므로 CollectionViewSource 리셋)
            var saved = ServerList.SelectedItem;
            _isLoading = true;
            ServerList.ItemsSource = null;
            ServerList.ItemsSource = _servers;
            ServerList.SelectedItem = saved;
            _isLoading = false;
        }
        finally
        {
            RefreshStatusBtn.IsEnabled = true;
        }
    }

    // ── 저장 / 취소 ────────────────────────────────────────────
    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        // 입력 검증: 빈 이름, 중복 이름, 로컬인데 Command 비었는지
        var empty = _servers.Where(s => string.IsNullOrWhiteSpace(s.Name)).ToList();
        if (empty.Count > 0)
        {
            ConfirmDialog.Alert("저장 실패", "서버 이름이 비어 있습니다.");
            return;
        }
        var dups = _servers.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                           .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dups.Count > 0)
        {
            ConfirmDialog.Alert("저장 실패", $"중복된 서버 이름이 있습니다: {string.Join(", ", dups)}");
            return;
        }
        var missingCmd = _servers
            .Where(s => s.Type == McpServerType.Local && s.Command.Count == 0)
            .Select(s => s.Name).ToList();
        if (missingCmd.Count > 0)
        {
            ConfirmDialog.Alert("저장 실패", $"로컬 서버는 실행 커맨드가 필요합니다: {string.Join(", ", missingCmd)}");
            return;
        }
        var missingUrl = _servers
            .Where(s => s.Type == McpServerType.Remote && string.IsNullOrWhiteSpace(s.Url))
            .Select(s => s.Name).ToList();
        if (missingUrl.Count > 0)
        {
            ConfirmDialog.Alert("저장 실패", $"원격 서버는 URL이 필요합니다: {string.Join(", ", missingUrl)}");
            return;
        }

        foreach (var s in _servers) s.Sanitize();

        try
        {
            OpenCodeConfigService.Save(_servers);
            ConfirmDialog.Alert("저장 완료",
                "opencode.json 에 저장했습니다.\n(opencode 세션은 다음 시작부터 새 설정을 사용합니다)");
            // 저장 후 원본 갱신(다음 취소 대비)
            _original.Clear();
            foreach (var s in _servers) _original.Add(s.Clone());
            CloseRequested?.Invoke(this, EventArgs.Empty);
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
                "저장되지 않은 변경사항이 있습니다.\n저장하시겠습니까?",
                okLabel: "저장", iconKey: "IconServer");
            if (save) SaveBtn_Click(sender, e);
            else { Revert(); CloseRequested?.Invoke(this, EventArgs.Empty); }
        }
        else
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
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

    private void Revert()
    {
        _servers.Clear();
        foreach (var s in _original) _servers.Add(s.Clone());
    }

    /// <summary>ESC / 외부 호출용 닫기.</summary>
    public void TryClose()
    {
        CancelBtn_Click(this, new RoutedEventArgs());
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
        => Window.GetWindow(this)?.DragMove();
}
