# borderless 창 라운드 코너 — 자식 사각 모서리 삐져나옴

## 증상
`WindowStyle=None` + `AllowsTransparency=True` 인 팝업 창(MCP 컨트롤러/플러그인 상세/프로젝트 추가 등)에서
콘텐츠 Border 는 `CornerRadius="14"` 로 둥근데, **그 라운드 코너 위로 사각 모서리가 삐져나온다.**
(헤더 배경·카드 등 자식 요소의 직각 모서리가 라운드 밖으로 노출)

## 원인
콘텐츠 Border 에 `ClipToBounds="True"` 를 줘도 **클립은 사각 경계(bounding rect)로만** 이뤄진다.
`ClipToBounds` 는 라운드 코너를 따라 자르지 않으므로, `CornerRadius` 로 깎인 4개 코너의
바깥 삼각형 영역에 자식의 직각 모서리가 그대로 보인다.

## 해결 (McpControlWindow 등 팝업 창 공통 패턴)
콘텐츠 Border 의 자식 UserControl 을 **둥근 `RectangleGeometry` 로 직접 클립**한다.
`Loaded` + `SizeChanged` 에서 실제 크기로 다시 계산해 적용:

```csharp
public MyControlWindow()
{
    InitializeComponent();
    View.SizeChanged += (_, _) => ApplyRoundedClip();
    Loaded += (_, _) => ApplyRoundedClip();
}

private void ApplyRoundedClip()
{
    double w = View.ActualWidth, h = View.ActualHeight;
    if (w <= 0 || h <= 0) return;
    View.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 13, 13);  // radius = CornerRadius(14) - BorderThickness(1)
}
```

## 포인트
- 반경은 **콘텐츠 Border 의 `CornerRadius` 에서 `BorderThickness` 를 뺀 값**(예: 14 - 1 = **13**).
  테두리 안쪽에 딱 맞아 테두리 선이 깨끗하게 남는다.
- **클립은 바깥 Border(CornerRadius=14)가 아니라 자식(내용 Grid/UserControl)에만 건다**
  (`PluginDetailWindow.xaml.cs` 주석 기반): Border 자체를 클립하면 보더 스트로크가 재클립돼
  모서리가 흐려진다. 바깥 Border 는 그대로 두어야 보더 모서리가 선명하다.
- `SizeChanged` 를 반드시 걸어야 리사이즈 가능한 창(`ResizeMode=CanResize`)에서도 유지된다.
- `ClipToBounds="True"` 만으로는 절대 안 된다. `using System.Windows.Media;` 필요.
- 새 borderless 오버레이 창을 만들 때는 현재 패턴 보유자인 `Views/McpControlWindow.xaml.cs` /
  `Views/PluginDetailWindow.xaml.cs` / `Views/ProjectAddDialog.xaml.cs`(셋 다 radius 13,13)의
  이 패턴을 그대로 복사할 것. (과거 대표 예시였던 SettingsWindow 는 커밋 3644f61 로 설정이 MDI
  오버레이로 전환되며 삭제됨. PluginControlWindow 는 이 패턴이 아니라 WindowChrome +
  `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE)` 방식이므로 참고 대상이 아니다.)
