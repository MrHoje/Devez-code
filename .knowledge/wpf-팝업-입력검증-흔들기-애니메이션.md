# WPF — 입력 팝업(다이얼로그) 검증 실패 시 창 흔들기 피드백 컨벤션

## 한 줄 요약
입력값 검증(빈값·중복·미존재 경로 등)이 실패하면 **2중 팝업(`ConfirmDialog.Alert`) 대신**
창을 좌우로 흔드는 `ShakeWindow()` + 해당 입력 프레임 빨간 테두리 + 인라인 힌트로 피드백한다.
devez `ShellShortcutDialog` 정합. 새 입력 팝업을 만들 때 이 패턴을 따른다.

## 적용 조건
사용자가 텍스트를 입력하는 모달 다이얼로그(`PromptDialog`, `ShortcutDialog`,
`ClaudeCodeRoomDialog` 류). "확인/저장" 시 검증 실패하면 Alert 팝업을 또 띄우지 말고
그 자리에서 흔들기 + 빨간 테두리로 알린다.

## 구현 3요소

### 1) XAML — Window에 RenderTransform 추가 (필수)
`ShakeWindow()`는 `RenderTransform`이 `TranslateTransform`이어야 동작한다. 없으면 조용히 무시됨.
```xml
<Window ... >
    <Window.RenderTransform>
        <TranslateTransform/>
    </Window.RenderTransform>
    ...
```

### 2) code-behind — ShakeWindow() 메서드
```csharp
/// <summary>검증 실패 시 창을 좌우로 흔드는 피드백(devez 정합).</summary>
private void ShakeWindow()
{
    if (RenderTransform is not TranslateTransform tt) return;
    var shake = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(500) };
    double[] offsets = { 0, -8, 8, -6, 6, -3, 3, 0 };
    for (int i = 0; i < offsets.Length; i++)
        shake.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(
            offsets[i], System.Windows.Media.Animation.KeyTime.FromPercent((double)i / (offsets.Length - 1))));
    tt.BeginAnimation(TranslateTransform.XProperty, shake);
}
```
- 500ms, offset `{0,-8,8,-6,6,-3,3,0}` 고정 (모든 팝업 동일하게 유지).
- `using System.Windows.Media;` 필요 (`TranslateTransform`, `SolidColorBrush`, `Color`).

### 3) 검증 분기 — 흔들기 + 빨간 테두리 + (있으면)인라인 힌트
```csharp
if (string.IsNullOrWhiteSpace(NameBox.Text))
{
    NameFrame.BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)); // ef4444 (DangerBrush)
    ShakeWindow();
    NameBox.Focus();
    return;
}
```
- 입력 박스를 감싸는 `Border`(예: `NameFrame`)의 `BorderBrush`만 빨갛게. `TextBox` 자체 아님.
- 인라인 힌트 `TextBlock`(`HintText`)이 있으면 `Visibility.Visible` + 문구 세팅(`PromptDialog` 방식).
- 포커스 회복 후 입력 다시 시작하면 힌트/테두리 원복(`GotFocus`/`KeyDown`에서 처리).

## 빨간색 값
`ef4444` = `DangerBrush`. 리소스가 있으면 `TryFindResource("DangerBrush")`, 없으면
`Color.FromRgb(0xef, 0x44, 0x44)` 하드코딩.

## 현재 적용 현황 (2026-07-31)
- `PromptDialog` — 빈값 시 흔들기 ✅
- `ShortcutDialog` — 경로 빈값/미존재 시 흔들기 ✅
- `ClaudeCodeRoomDialog` — 이름 빈값/디렉토리 미존재 시 흔들기 ✅ (Alert → 흔들기 전환)
- `McpManagerDialog` — 리스트 일괄 검증이라 단일 입력 흔들기 미적용, `Alert("저장 실패", ...)` 유지
- `ProjectAddDialog` — **미해당**(카드 선택형, 텍스트 입력 없음 → 검증 실패 케이스 자체가 없음)

ShakeWindow 3중 코드 복제(PromptDialog/ShortcutDialog/ClaudeCodeRoomDialog)는 **의도적으로 유지 중**
(공용 헬퍼 미도입 — 각 팝업이 독립이라 복제 비용 < 추상화 비용 판단).

## 주의
- `RenderTransform` 누락이 제일 흔한 실수. 흔들기 안 되면 여기부터 확인.
- 흔들기는 검증 실패 **즉시 1회**. 반복 호출하면 애니메이션 리셋되니 분기마다 1번만.
