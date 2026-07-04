using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DevezCode.Models;

/// <summary>Claude Code 플러그인 한 개. <c>claude plugin list --json</c> 항목과 매핑.
/// id = "name@marketplace". 상태(enabled)는 토글로 바뀌므로 INotify.</summary>
public sealed class ClaudePlugin : INotifyPropertyChanged
{
    public string Id { get; set; } = "";        // "name@marketplace"
    public string Name { get; set; } = "";       // @ 앞
    public string Marketplace { get; set; } = ""; // @ 뒤
    public string Version { get; set; } = "";
    public string Scope { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public DateTime? InstalledAt { get; set; }
    public DateTime? LastUpdated { get; set; }
    public List<string> McpServerNames { get; set; } = new();

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; OnPropertyChanged(); } }
    }

    /// <summary>마켓플레이스가 제공하는 최신 버전(비교 가능할 때만 채워짐). 빈 문자열이면 확인 불가.</summary>
    public string LatestVersion { get; set; } = "";
    /// <summary>설치본이 최신보다 낮음(업데이트 가능).</summary>
    public bool UpdateAvailable { get; set; }

    // ── 표시용 파생 ──
    public string VersionText => string.IsNullOrWhiteSpace(Version) || Version == "unknown"
        ? "버전 미상" : "v" + Version;
    public string McpText => McpServerNames.Count > 0 ? $"MCP {McpServerNames.Count}개" : "";
    public bool HasMcp => McpServerNames.Count > 0;
    public string UpdatedText => LastUpdated.HasValue ? "갱신 " + LastUpdated.Value.ToLocalTime().ToString("yyyy-MM-dd") : "";

    /// <summary>최신 버전을 확인했고 이미 최신임.</summary>
    public bool IsLatest => !string.IsNullOrEmpty(LatestVersion) && !UpdateAvailable;
    /// <summary>업데이트 가능 배지 문구.</summary>
    public string UpdateBadgeText => string.IsNullOrEmpty(LatestVersion) ? "업데이트 가능" : $"업데이트 가능 · v{LatestVersion}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Claude Code 마켓플레이스 한 개. <c>claude plugin marketplace list --json</c> 매핑.</summary>
public sealed class ClaudeMarketplace
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";  // github / git / local …
    public string Repo { get; set; } = "";
    public string Url { get; set; } = "";
    public string InstallLocation { get; set; } = "";

    /// <summary>표시용: repo 또는 url 중 있는 것.</summary>
    public string OriginText => !string.IsNullOrWhiteSpace(Repo) ? Repo
        : !string.IsNullOrWhiteSpace(Url) ? Url : Source;
}
