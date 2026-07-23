# 탭바 SelectedTabSeam — 선택 탭 하단 밑줄(ZIndex + 그라데이션 페이드)

## 한 줄 요약

TabBar 하단 `BorderThickness="0,0,0,1"`(LineBrush) 위에 `Panel.ZIndex="100"`인 1px 높이 `SelectedTabSeam` Border를 덮고,
양 끝을 `LinearGradientBrush`로 투명 페이드 처리해 선택 탭 아래만 border가 없는 것처럼 보이게 한다.

```xml
<Border x:Name="SelectedTabSeam" Grid.Row="1" Height="1"
        VerticalAlignment="Bottom" HorizontalAlignment="Left"
        Background="{DynamicResource PanelBrush}"
        Visibility="Collapsed" IsHitTestVisible="False" Panel.ZIndex="100"/>
```

- `Grid.Row="1"` (TabBar와 같은 행), `VerticalAlignment="Bottom"` → TabBar 하단 1px
- `Panel.ZIndex="100"` → TabBar Border보다 위에 렌더링되어 하단 border를 가림
- `HorizontalAlignment="Left"` → Width/Margin을 코드로 동적 제어

---

## 왜 필요한가

- TabBar는 전체 폭 하단에 `BorderThickness="0,0,0,1"`로 1px LineBrush border가 그려짐
- **선택 탭은 이 border가 없어야** 탭이 콘텐츠 영역과 이어진(connected) 것처럼 보임
- WPF Border의 border는 항상 children 위에 렌더링됨 → 탭 내부 margin trick(`Margin="0,0,0,-1"`)만으로는 가릴 수 없음
- 따라서 **TabBar Border와 동일 부모(Grid.Row="1")에서 ZIndex가 높은 별도 요소**로 border를 물리적으로 덮어야 함

---

## 렌더링 순서 (bottom→top)

```
1. TabBar Border.BorderBrush (LineBrush, 하단 1px 전폭)
       ↓ ZIndex 비교: TabBar=0, Seam=100
2. SelectedTabSeam.Background (PanelBrush, 선택 탭 폭만)
       ↓
결과: 선택 탭 아래는 border가 가려지고, 그 외 영역은 border 유지
```

---

## 코드: `UpdateSelectedTabSeam()` — `WorkspacePaneView.xaml.cs` (메서드명으로 grep — 라인은 수시로 이동)

### 위치 계산

```csharp
var pt = container.TransformToAncestor(TabBar).Transform(new Point(0, 0));
const double seamExtra = 9.15;
double bodyWidth = container.ActualWidth - 3;
double seamWidth = bodyWidth + seamExtra * 2;
double seamLeft = pt.X - seamExtra;
```

| 변수 | 값 | 이유 |
|------|-----|------|
| `container` | 활성 탭의 `ContentPresenter` | `ItemContainerGenerator.ContainerFromItem()` 획득 |
| `pt.X` | ContentPresenter left (TabBar 기준) | |
| `container.ActualWidth - 3` | TabRoot 본체 폭 | 3px 우측 Margin 제외 |
| `seamExtra` | **9.15** | 좌/우 라운드 여유. 양쪽 발(feet) 확장(-10, 13px)보다 약간 좁게 |
| `seamWidth` | `bodyWidth + 9.15 * 2` | 탭 폭 + 좌우 여유 |

### 좌측 클램핑 (스크롤 경계)

```csharp
double clipLeft = TabScroller.TransformToAncestor(TabBar).Transform(new Point(0, 0)).X;
if (clipLeft < 0) clipLeft = 0;
bool clampedLeft = false;
if (seamLeft < clipLeft) {
    seamWidth -= clipLeft - seamLeft;
    seamLeft = clipLeft;
    clampedLeft = true;
}
```

스크롤로 탭이 TabBar 좌측 가장자리에 있을 때 seam이 뷰포트 밖으로 나가지 않도록 자름.
`clampedLeft` 플래그는 그라데이션 왼쪽 끝을 투명 대신 panelColor로 사용해 잘린 부분이 비어 보이지 않게 함.

### LinearGradientBrush — 양 끝 4px 페이드

```csharp
var panelColor = (FindResource("PanelBrush") as SolidColorBrush)?.Color ?? Colors.Black;
var clearColor = Color.FromArgb(0, panelColor.R, panelColor.G, panelColor.B);
const double fadePx = 4;
double f = seamWidth > 0 ? Math.Min(0.45, fadePx / seamWidth) : 0;
var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
brush.GradientStops.Add(new GradientStop(clampedLeft ? panelColor : clearColor, 0));
brush.GradientStops.Add(new GradientStop(panelColor, clampedLeft ? 0 : f));
brush.GradientStops.Add(new GradientStop(panelColor, 1 - f));
brush.GradientStops.Add(new GradientStop(clearColor, 1));
```

```
그라데이션 구조 (좌→우):

  0%          f%               1-f%        100%
  │◄──fade──►│◄─── solid ────►│◄──fade──►│
  clearColor  panelColor       panelColor  clearColor
  (투명)       (고정)           (고정)      (투명)

f = min(0.45, 4px / seamWidth)
  → 4px을 비율로 환산, 최대 45%로 제한(아주 좁은 탭에서 fade가 중앙까지 먹는 것 방지)
```

**핵심**: `SolidColorBrush`를 쓰면 seam 끝에서 좌/우 1px border가 "잘려 보인다".  
**그라데이션으로 4px에 걸쳐 투명해지므로** border가 점진적으로 사라져 자연스럽다.

### `clampedLeft=true` 시 좌측 그라데이션

왼쪽이 잘렸으면(`clampedLeft`) 좌측 GradientStop을 `clearColor`(투명) 대신 `panelColor`로:
```
  0%          f%          ...
  panelColor  panelColor  ...  → 투명으로 가는 게 아니라 처음부터 채움
```
잘린 영역에 투명이 들어오는 걸 방지.

---

## 연관: 탭 발(Feet) Paths — 모서리 틈새 채움

`SelectedTabSeam`은 직사각형이라 탭 좌우 라운드 코너(9px) 아래의  
삼각형 틈새는 덮지 못한다. 이 틈새는 `TabFootLeftFill/RighFill` Path가 채운다.

```
  ╭──────────╮
  │  Tab     │
  │          │
  │◄─fill───╯├──►fill
  │◄─seam───►│
```

- `TabFootLeftFill`: `Data="M11,0 L10,0 A10,13 0 0 1 0,13 L11,13 Z"` with `Margin="-10,0,0,-1"`
  - 탭 좌측 하단 모서리 틈새를 panelBrush로 채움
- `TabFootLeftLine`: `Data="M10,-3 L10,0 A10,13 0 0 1 0,13"` with `Stroke="LineBrush"`
  - 좌측 라운드 코너의 border 선 연장
- 우측도 동일 패턴 (mirror)

**Z-order 주의**: Feet은 TabRoot 내부 → TabBar Border 아래.  
따라서 TabBar border가 feet 위에 그려진 후,  
Seam이 border 위에 덮여 feet 영역까지 border가 안 보이게 함.

---

## 호출 시점

| 상황 | 호출 |
|------|------|
| 탭 전환 (`ActivateSession` / `ActivateFileTab`) | → `UpdateSelectedTabSeam()` |
| 탭 닫아서 마지막 탭 사라짐 | `SelectedTabSeam.Visibility = Collapsed` 직접 |
| TabScroller 스크롤 | `ScrollChanged` → `UpdateSelectedTabSeam()` |
| 테마 변경 | `OnThemeChanged_UpdateSeam` → `UpdateSelectedTabSeam()` |
| 외부 탭 재배열 (사이드바 드래그) | `RefreshSelectedTabSeam()` → `Dispatcher.InvokeAsync(Loaded)`로 지연 호출 |
| 탭 드래그 시작 | `SetupDragSeam` → seam `Visibility = Collapsed` |

---

## 실수 방지 체크리스트

- [ ] `HorizontalAlignment="Left"`인가? `Stretch`면 전폭을 덮어버림
- [ ] `Panel.ZIndex="100"`이 TabBar와 **같은 Grid.Row**에 있는가? 다른 행이면 ZIndex 우선순위가 달라짐
- [ ] `VerticalAlignment="Bottom"` + `Height="1"` → TabBar 하단 border와 정확히 같은 위치
- [ ] `container.ActualWidth - 3` (ContentPresenter 경로) vs `container.ActualWidth` (TabRoot 직접 경로) — 마진 포함 여부가 다름
- [ ] 활성 탭이 없으면 반드시 `Visibility = Collapsed` (잔상 방지)
- [ ] `LinearGradientBrush`를 `Freeze()`했는가? (성능)
