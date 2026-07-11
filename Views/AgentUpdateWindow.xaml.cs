using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>앱 시작/설정에서 에이전트 자동 업데이트 진행 상황을 보여주는 작은 창.
/// 로드되면 스스로 업데이트를 실행하고, 진행 로그를 실시간 출력한다.
/// 하단 버튼은 처음엔 숨겨져 로그가 전체 높이를 채우다가,
///  - 완료되면 '닫기'로,
///  - <see cref="SkipRevealSeconds"/> 가 지나도 안 끝나면 '건너뛰고 시작'으로
/// 아래에서 높이가 자라나며 나타난다(로그 영역이 그만큼 줄어듦).</summary>
public partial class AgentUpdateWindow : Window
{
    private const double SkipRevealSeconds = 10;

    /// <summary>하단 버튼('건너뛰고 시작' 또는 '닫기') 클릭 시 발생.</summary>
    public event Action? ProceedRequested;

    /// <summary>업데이트 완료 후 창을 자동으로 닫을지. 시작 시퀀스=true(잠깐 보여주고 진입), 설정 모달=false(사용자가 닫음).</summary>
    public bool AutoCloseOnComplete { get; set; }

    private bool _closed, _started, _buttonShown;

    // '곧 시작됩니다' 점 애니메이션(1→2→3→4→1…).
    private System.Windows.Threading.DispatcherTimer? _dotTimer;
    private int _dotCount;

    public AgentUpdateWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { _closed = true; _dotTimer?.Stop(); };
        Loaded += async (_, _) => await RunAsync();
    }

    /// <summary>업데이트 실행 + 버튼 노출 타이밍 제어.</summary>
    private async Task RunAsync()
    {
        if (_started) return;
        _started = true;

        var updateTask = AgentUpdateService.UpdateEnabledAgentsAsync(Report);

        // 지연(테스트 2s / 최종 10s) 후에도 안 끝났으면 '건너뛰고 시작' 노출.
        _ = Task.Delay(TimeSpan.FromSeconds(SkipRevealSeconds)).ContinueWith(_ =>
        {
            if (!updateTask.IsCompleted) RevealButton("건너뛰고 시작");
        }, TaskScheduler.FromCurrentSynchronizationContext());

        try { await updateTask; } catch { /* best-effort */ }

        MarkComplete();

        if (AutoCloseOnComplete)
        {
            await Task.Delay(1400); // 완료 결과를 잠깐 보여준 뒤 자동으로 닫기(→ 메인 창 진입)
            if (!_closed) { try { Close(); } catch { } }
        }
    }

    /// <summary>진행 로그 한 줄 추가.</summary>
    public void Report(string line)
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Report(line)); return; }
        if (_closed) return;
        if (LogText.Text.Length > 0) LogText.Text += "\n";
        LogText.Text += line;
        LogScroll.ScrollToEnd();
    }

    /// <summary>업데이트 완료 — 헤더/진행바 갱신. 시작 시퀀스면 곧 메인 창이 뜨므로
    /// 비활성 버튼 '곧 시작됩니다' + 점 애니메이션, 설정 모달이면 활성 '닫기'.</summary>
    public void MarkComplete()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(MarkComplete); return; }
        if (_closed) return;
        HeaderText.Text = "에이전트 업데이트 완료";
        Progress.IsIndeterminate = false;
        Progress.Value = Progress.Maximum;

        if (AutoCloseOnComplete)
        {
            RevealButton("곧 시작됩니다.");
            ProceedButton.IsEnabled = false;   // 비활성(회색) — 누를 필요 없이 잠시 후 자동 진입
            StartDotAnimation("곧 시작됩니다");
        }
        else
        {
            RevealButton("닫기");
        }
    }

    /// <summary>버튼 텍스트 뒤에 점을 1→2→3→4→1… 로 순환시킨다.</summary>
    private void StartDotAnimation(string baseText)
    {
        _dotTimer?.Stop();
        _dotCount = 1;
        ProceedButton.Content = baseText + ".";
        _dotTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _dotTimer.Tick += (_, _) =>
        {
            _dotCount = _dotCount % 4 + 1;               // 1,2,3,4,1,2,3,4…
            ProceedButton.Content = baseText + new string('.', _dotCount);
        };
        _dotTimer.Start();
    }

    /// <summary>하단 버튼을 노출한다. 최초 1회만 높이 0→38 + 페이드 애니메이션으로 자라나며,
    /// 하단 행(Auto)이 커지는 만큼 로그 영역(*)이 줄어든다. 이후엔 라벨만 교체.</summary>
    private void RevealButton(string text)
    {
        if (_closed) return;
        ProceedButton.Content = text;
        if (_buttonShown) return;
        _buttonShown = true;

        ProceedButton.Visibility = Visibility.Visible;
        ProceedButton.Opacity = 0;

        var dur  = new Duration(TimeSpan.FromMilliseconds(240));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ProceedButton.BeginAnimation(HeightProperty,  new DoubleAnimation(0, 38, dur) { EasingFunction = ease });
        ProceedButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur)  { EasingFunction = ease });
        ProceedButton.BeginAnimation(MarginProperty,
            new ThicknessAnimation(new Thickness(0), new Thickness(0, 14, 0, 0), dur) { EasingFunction = ease });
    }

    private void ProceedButton_Click(object sender, RoutedEventArgs e) => ProceedRequested?.Invoke();
}
