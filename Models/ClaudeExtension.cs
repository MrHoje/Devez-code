using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DevezCode.Models;

/// <summary>Claude Code 스킬 한 개. <c>~/.claude/skills/&lt;name&gt;/SKILL.md</c> 매핑.
/// 잠금(숨김)은 SKILL.md ↔ SKILL.md.off 파일명 토글로 구현 — 잠그면 Claude 가 해당 스킬을 발견하지 못한다.
/// Enabled(=SKILL.md 존재) 는 토글로 바뀌므로 INotify.</summary>
public sealed class ClaudeSkill : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Scope { get; set; } = "user";   // user / project
    public string Dir { get; set; } = "";           // 스킬 폴더 경로
    public string FilePath { get; set; } = "";       // 현재 활성 파일(SKILL.md 또는 SKILL.md.off)

    private bool _enabled = true;
    /// <summary>true=활성(SKILL.md), false=잠금·숨김(SKILL.md.off).</summary>
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; OnPropertyChanged(); } }
    }

    public string ScopeText => Scope == "project" ? "프로젝트" : "사용자";
    public string DescText => string.IsNullOrWhiteSpace(Description) ? "설명 없음" : Description;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Claude Code 서브에이전트 한 개. <c>~/.claude/agents/&lt;name&gt;.md</c> 매핑.</summary>
public sealed class ClaudeAgent
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Scope { get; set; } = "user";   // user / project
    public string FilePath { get; set; } = "";

    public string ScopeText => Scope == "project" ? "프로젝트" : "사용자";
    public string DescText => string.IsNullOrWhiteSpace(Description) ? "설명 없음" : Description;
}
