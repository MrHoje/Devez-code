using System.ComponentModel;

namespace DevezCode.Models;

/// <summary>Diff 패널의 git 기능 사용 여부(전역). 설정 토글과 연동되며, XAML 템플릿 요소가
/// 이 값을 바인딩해 스테이징/되돌리기/상태글자 표시를 즉시 전환한다. 설정 저장 시 갱신.</summary>
public sealed class GitUiState : INotifyPropertyChanged
{
    public static GitUiState Instance { get; } = new();

    private bool _diffGitEnabled;
    public bool DiffGitEnabled
    {
        get => _diffGitEnabled;
        set { if (_diffGitEnabled == value) return; _diffGitEnabled = value; PropertyChanged?.Invoke(this, new(nameof(DiffGitEnabled))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
