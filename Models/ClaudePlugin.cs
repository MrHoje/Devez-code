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

    /// <summary>상세를 열어본(선택된) 플러그인 — 마켓플레이스 카드처럼 보더만 하이라이트.</summary>
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>설치 가능한 플러그인 한 개(Discover). <c>claude plugin list --available --json</c> 의 available[] 매핑.</summary>
public sealed class ClaudeAvailablePlugin
{
    public string Id { get; set; } = "";          // "name@marketplace"
    public string Name { get; set; } = "";
    public string Marketplace { get; set; } = ""; // marketplaceName
    public string Description { get; set; } = "";
    public int InstallCount { get; set; }
    public bool IsInstalled { get; set; }
    public string Version { get; set; } = "";     // source.ref 또는 version
    public string SourceUrl { get; set; } = "";    // source.url (GitHub 등)

    public string InstallCountText => InstallCount > 0 ? $"설치 {InstallCount:N0}" : "";
    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "" : (Version.StartsWith("v") ? Version : "v" + Version);
    public bool HasSourceUrl => !string.IsNullOrWhiteSpace(SourceUrl);
}

/// <summary>Claude Code 마켓플레이스 한 개. <c>claude plugin marketplace list --json</c> 매핑.</summary>
public sealed class ClaudeMarketplace : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";  // github / git / local …
    public string Repo { get; set; } = "";
    public string Url { get; set; } = "";
    public string InstallLocation { get; set; } = "";

    /// <summary>표시용: repo 또는 url 중 있는 것.</summary>
    public string OriginText => !string.IsNullOrWhiteSpace(Repo) ? Repo
        : !string.IsNullOrWhiteSpace(Url) ? Url : Source;

    /// <summary>상세를 열어 하단 Discover에 표시 중인(선택된) 마켓플레이스 — 프로젝트 카드처럼 보더만 하이라이트.</summary>
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
