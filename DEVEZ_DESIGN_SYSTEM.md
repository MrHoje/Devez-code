# Devez 공통 디자인 시스템

> 대상: `devez-code`, `devez`, `eGhisDevWPF`
> 문서 버전: 1.8
> 기준일: 2026-07-24
> 주 대상 기술: WPF/XAML
> 목적: 새 프로젝트에서 같은 색, 밀도, 컨트롤, 라운드, 아이콘, 보더, 상태 표현을 바로 재현한다.

## 0. 1분 적용 요약

새 UI는 아래 순서로 시작한다.

1. 본문 폰트는 `Pretendard`, 브랜드 로고만 `Bruno Ace SC`를 쓴다.
2. 테마는 `minimal`, `soft`, `dark` 3개를 제공한다.
3. 화면에서 직접 HEX를 쓰지 않고 의미 기반 `DynamicResource`를 쓴다.
4. 기본 버튼은 높이 `38`, 최소 너비 `80`, 좌우 패딩 `16`, 라운드 `10`이다.
5. 기본 입력은 높이 `40`, 보더 `1.5`, 라운드 `8`, 좌우 패딩 `10~12`다.
6. 기본 아이콘은 Lucide `24×24` 좌표계, 실제 렌더 `16×16`, 획 `1.25`, round cap/join이다.
7. 일반 아이콘 버튼 클릭 영역은 `24×24` 또는 `28×28`, 둥근 원형 액션은 `28×28 / CornerRadius=14`다.
8. 팝업·다이얼로그 외곽은 라운드 `14`, 보더 `1`, 그림자 `BlurRadius=40`이다.
9. hover는 한 단계 밝거나 부드러운 surface, pressed는 선/강조색, disabled는 muted 색을 쓴다.
10. 클릭 가능한 앱 컨트롤 커서는 `Arrow`다. 실제 웹 링크만 `Hand`를 허용한다.
11. 일반 업무·내비게이션 앱은 `devez`형 프레임, 터미널·편집기 중심 앱은 `devez-code`형 프레임을 고른다.
12. 프레임을 섞지 않는다. `44 header + side rail` 또는 `32 header + 32 footer`를 하나의 셸 계약으로 적용한다.
13. 설정창은 `970×830`, header `48`, category rail `205`, footer action `100×38` 표준을 쓴다.
14. desktop notification popup은 폭 `340`, radius `12`, edge margin `6`, stack gap `8`을 쓴다.

가장 안전한 시작점:

```xml
<Grid Background="{DynamicResource BgBrush}">
    <Border Margin="20"
            Padding="20"
            Background="{DynamicResource PanelBrush}"
            BorderBrush="{DynamicResource LineBrush}"
            BorderThickness="1"
            CornerRadius="10">
        <!-- content -->
    </Border>
</Grid>
```

## 1. 결정 규칙

### 1.1 우선순위

프로젝트별 값이 다르면 다음 규칙을 적용한다.

1. 세 프로젝트 중 같은 방향을 쓰는 프로젝트가 2개 이상이면 그 값을 채택한다.
2. 세 프로젝트 값이 모두 다르거나 1:1로 갈리면 `devez-code` 값을 채택한다.
3. 특정 컴포넌트가 한 프로젝트에만 있으면 그 구현을 고유 패턴으로 채택한다.
4. 기능 밀도 때문에 값이 다른 경우 브랜드 차이로 보지 않고 `default`와 `compact` 변형으로 분리한다.

### 1.2 조사 결과

| 항목 | devez-code | devez | eGhisDevWPF | 공통 결정 |
|---|---:|---:|---:|---|
| 조사한 XAML | 35개 | 81개 | 40개 | 실제 소스만 집계 |
| 핵심 스타일 파일 | `AppStyles.xaml` 2,077줄 | `AppStyles.xaml` 1,725줄 | `DevezTheme.xaml` 1,233줄 | Devez 계열 기반 |
| 직접 리소스 수 | 147 | 130 | WPF-UI + 자체 스타일 | 130개 공통 리소스 우선 |
| Devez 계열 공통 리소스 | 130개 중 123개 완전 동일 | 동일 | 이식된 의미 토큰 사용 | 공통성이 매우 높음 |
| 아이콘 리소스 | 195개 | 174개 | Path + WPF-UI SymbolIcon | Lucide/PathGeometry 우선 |
| Devez 계열 공통 아이콘 | 172개 중 171개 완전 동일 | 동일 | 일부 이식 | 24 좌표계 규칙 채택 |
| 기본 버튼 라운드 | 10 | 10 | 페이지 4, 다이얼로그 10 | 기본 10, compact 4 |
| 다이얼로그 라운드 | 14 | 14 | 14 | 14 |
| 다이얼로그 액션 | 높이 38 | 높이 38 | 높이 38 | 높이 38 |
| 메인 타이틀바 | 32 | 44 | 44 | 44 |
| 분할선 채널 | 4 | 4 | 화면별 상이 | 기본 4 |

조사 시 `.git`, `bin`, `obj`, `.verify`, `devez-code/.claude/worktrees`는 제외했다.

## 2. 디자인 성격

### 2.1 시각 원칙

- 정보가 많은 데스크톱 UI지만 답답하지 않게 만든다.
- 큰 장식보다 얇은 보더, 미세한 surface 차이, 일관된 라운드로 계층을 만든다.
- 주요 액션만 accent로 채운다. 나머지는 panel, transparent, muted 계열을 쓴다.
- 텍스트와 아이콘은 같은 의미 색을 공유한다.
- 작은 컨트롤도 외곽 클릭 영역을 확보한다.
- 다크·미니멀·소프트 테마에서 레이아웃과 상태 의미는 바뀌지 않는다.

### 2.2 UX 원칙

- 새 컨트롤을 만들기 전에 기존 공용 스타일을 재사용한다.
- 사용자 입력값은 명시적으로 비어 있지 않으면 자동 추천값으로 덮지 않는다.
- 로딩 중 기존 결과가 있으면 결과를 유지한다. 빈 최초 로딩일 때만 중앙 overlay spinner를 보여준다.
- hover, selected, busy, disabled를 색 하나로만 구분하지 않는다. 배경·보더·아이콘·텍스트를 함께 조절한다.
- 메뉴, 다이얼로그, 카드의 기능 배치는 화면마다 새로 발명하지 않는다.
- 파괴적 액션은 danger 색과 확인 단계로 분리한다.
- WebView2·터미널이 포함된 큰 패널은 불필요한 리사이즈 애니메이션을 피한다.

## 3. 의미 기반 색상 토큰

### 3.1 공통 이름 매핑

새 프로젝트에서는 왼쪽의 공통 이름을 사용한다. 기존 프로젝트를 이식할 때 오른쪽 이름으로 대응한다.

| 의미 | 공통 권장 이름 | devez-code / devez | eGhisDevWPF |
|---|---|---|---|
| 앱 바탕 | `AppBackground` | `BgBrush` | `PageBrush` |
| 기본 패널 | `Surface` | `PanelBrush` | `PanelBrush` |
| 보조 패널 | `SurfaceSoft` | `PanelSoftBrush` | `PanelBrush2` |
| 대체 행/표면 | `SurfaceAlt` | 화면별 파생 | `PanelAltBrush` |
| hover | `SurfaceHover` | `HoverBrush` 또는 `PanelSoftBrush` | `HoverBrush` |
| 기본 선 | `BorderSoft` | `LineBrush` | `BorderBrushSoft` |
| 강한 선 | `BorderStrong` | `ProjectCardHoverBorderBrush` 등 | `BorderBrushStrong` |
| 본문 텍스트 | `TextPrimary` | `TextBrush` | `TextMain` |
| 보조 텍스트 | `TextSecondary` | `TextMutedBrush` | `TextSub` |
| 비활성 텍스트 | `TextDisabled` | muted + opacity | `TextDisabled` |
| 강조 | `Accent` | `PrimaryBrush` | `Accent` |
| 강조 hover | `AccentHover` | `PrimaryHoverBrush` | `AccentHover` |
| 강조 pressed | `AccentPressed` | `PrimaryPressedBrush` | `AccentPressed` |
| 약한 강조 | `AccentSoft` | `PrimarySoftBrush` | `AccentSoft` |
| 성공 | `Success` | `SuccessBrush` | `SuccessBrush` |
| 위험 | `Danger` | `DangerBrush` | `DangerBrush` |
| 경고 | `Warning` | `WarningBrush` | `WarningBrush` |

기존 저장소 안에서는 이름을 임의로 바꾸지 않는다. 새 프로젝트의 추상 계층에서만 공통 이름을 도입한다.

### 3.2 핵심 팔레트

#### Minimal

| 토큰 | HEX |
|---|---|
| App background | `#F8FAFC` |
| Surface | `#FFFFFF` |
| Surface soft | `#F1F5F9` |
| Surface hover | `#F1F5F9` (= surface soft) |
| Border soft | `#E2E8F0` |
| Border strong | `#CBD5E1` |
| Text primary | `#0F172A` |
| Text secondary | `#475569` |
| Text disabled | `#94A3B8` |
| Accent | `#2563EB` |
| Accent hover | `#1D4ED8` |
| Accent pressed | `#1E40AF` |
| Accent soft | `#DBEAFE` |
| Success | `#15803D` |
| Danger | `#EF4444` |
| Today | `#F97316` |

#### Soft

| 토큰 | HEX |
|---|---|
| App background | `#F2EDE6` |
| Surface | `#FAF7F2` |
| Surface soft | `#ECE7DE` |
| Surface hover | `#ECE7DE` (= surface soft) |
| Border soft | `#D8D2C6` |
| Border strong | `#C2B8A8` |
| Text primary | `#2A2620` |
| Text secondary | `#5A5448` |
| Text disabled | `#9A917F` |
| Accent | `#5C8C4A` |
| Accent hover | `#4E7A3E` |
| Accent pressed | `#426834` |
| Accent soft | `#DEECD6` |
| Success | `#15803D` |
| Danger | `#D95F5F` |
| Today | `#EA762C` |

#### Dark

| 토큰 | HEX |
|---|---|
| App background | `#1F1F1E` |
| Surface | `#272727` |
| Surface soft | `#2F2F2F` |
| Surface alt | `#2B2B2B` |
| Surface hover | `#424242` |
| Border soft | `#404040` |
| Border strong | `#5A5A5A` |
| Text primary | `#E8E8E8` |
| Text secondary | `#AAAAAA` |
| Text disabled | `#777777` |
| Accent | `#C2622A` |
| Accent hover | `#B5571F` |
| Accent pressed | `#A04C18` |
| Accent soft | `#434343` |
| Success | `#22C55E` |
| Danger | `#EF4444` |
| Today | `#F97316` |

hover 토큰 규칙:

- `HoverBrush`는 minimal·soft에서 surface soft와 같은 색을 공유하고, dark에서만 한 단계 더 밝은 `#424242`를 쓴다.
- 조밀한 목록 행 hover는 별도 `SessionHoverBrush` 토큰이다: minimal `#EFF3F7`, soft `#EAE5DC`, dark는 surface soft(배경과 동일해 사실상 변화 없음).

### 3.3 보조 상태색

| 의미 | Minimal | Soft | Dark |
|---|---|---|---|
| Checked background | `#F0FDF4` | `#D4EDCA` | `#212822` |
| Checked border | `#86EFAC` | `#8FC47E` | `#386038` |
| Checked text | `#166534` | `#2A5220` | `#86EFAC` |
| Saturday | `#2563EB` | `#2563EB` | `#60A5FA` |
| Sunday | `#EF4444` | `#D95F5F` | `#EF4444` |
| Warning canonical | `#EAB308` | `#EAB308` | `#EAB308` |

`eGhisDevWPF`의 데이터 화면은 warning을 `#D97706 / #C97C1A / #F59E0B`로 테마별 보정한다. 새 공통 프로젝트는 `devez-code` 우선 규칙에 따라 `#EAB308`을 기본값으로 쓰고, 작은 텍스트 대비가 부족하면 eGhis 변형을 사용한다.

### 3.4 테마 구현 규칙

- 소비자는 `{DynamicResource ...}`를 사용한다.
- 테마 전환 때 기존 brush의 `Color`만 바꾸지 말고 새 `SolidColorBrush` 인스턴스를 리소스에 다시 넣는다.
- Color와 Brush가 모두 필요한 토큰은 `AccentColor` + `Accent`처럼 함께 제공한다.
- 하드코딩이 허용되는 값은 `White`, 투명 오버레이, 브랜드 자산의 고정색 정도다.
- dark 여부로만 분기하지 않는다. `minimal`, `soft`, `dark`를 독립 palette로 취급한다.

## 4. 타이포그래피

### 4.1 글꼴

| 용도 | 글꼴 |
|---|---|
| 전체 UI | `Pretendard` |
| 한글 fallback | `Malgun Gothic` |
| 브랜드명·로고 | `Bruno Ace SC` |
| 코드·터미널 | `Consolas`, `Cascadia Code`, `Pretendard` 순 |

WPF 리소스:

```xml
<FontFamily x:Key="PretendardFont">
    pack://application:,,,/Fonts/#Pretendard
</FontFamily>
<FontFamily x:Key="BrunoAceSCFont">
    pack://application:,,,/Fonts/#Bruno Ace SC
</FontFamily>
```

### 4.2 크기 체계

기존 공통 리소스는 `8, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 26, 36`을 제공한다.

| 역할 | 크기 | 굵기 | 용도 |
|---|---:|---|---|
| Micro | 10 | Normal | 배지 보조 정보 |
| Caption | 11 | Normal | 단축키, 아주 작은 메타 |
| Small | 12 | Normal/Medium | tooltip, 메뉴, 탭, 부가 정보 |
| Body | 13 | Normal | 기본 본문, 다이얼로그 본문 |
| Body large | 14 | Normal | 입력, 버튼, 행 본문 |
| Section | 15 | SemiBold | 섹션 제목 |
| Heading | 16~18 | SemiBold/Bold | 페이지 제목 |
| Display | 20~36 | Bold | 빈 상태, 큰 수치 |

기본 규칙:

- 버튼 텍스트 `13~14`, `SemiBold`
- 입력 텍스트 `14`
- 메뉴 `12~13`
- 다이얼로그 제목 `14`, `SemiBold`
- 보조 설명 `12~13`, secondary 색
- 본문 line height는 폰트 크기의 약 `1.45~1.55배`

### 4.3 렌더링

```xml
TextOptions.TextFormattingMode="Ideal"
TextOptions.TextRenderingMode="ClearType"
RenderOptions.ClearTypeHint="Enabled"
UseLayoutRounding="True"
SnapsToDevicePixels="True"
```

- `TextBlock`, `TextBox`, `ComboBox`는 `Ideal + ClearType`을 기본으로 한다.
- 창 레벨 고정 레이아웃은 `Display + Fixed`를 쓸 수 있지만 입력 컨트롤은 다시 `Ideal`로 덮는다.
- 인라인 스타일을 만들 때 암시적 전역 스타일을 유지하도록 `BasedOn="{StaticResource {x:Type TextBlock}}"`를 사용한다.

렌더링 상속 구조:

| 계층 | 설정 |
|---|---|
| `Window` | `Display + ClearType + Fixed` |
| `Page` | `Display + ClearType + Fixed` |
| `Control` | `Ideal + ClearType` |
| `TextBlock` | `Ideal + ClearType` |
| `TextBox`, `RichTextBox` | `Ideal + ClearType` |

keyed 또는 인라인 스타일은 암시적 스타일을 통째로 대체한다. 아래처럼 연결하지 않으면 Pretendard와 `Ideal` 설정이 사라질 수 있다.

```xml
<Style x:Key="FieldLabel"
       TargetType="TextBlock"
       BasedOn="{StaticResource {x:Type TextBlock}}">
    <Setter Property="FontWeight" Value="Medium" />
</Style>
```

`Run.Text`는 기본 binding mode가 `TwoWay`다. 읽기 전용 계산 속성에 연결할 때 반드시 `OneWay`를 명시한다.

```xml
<Run Text="{Binding DisplayCount, Mode=OneWay}" />
```

이를 빠뜨리면 해당 요소가 렌더되는 순간 다음 종류의 시작 오류가 발생할 수 있다.

```text
TwoWay 또는 OneWayToSource 바인딩은 읽기 전용 속성에서 작동하지 않습니다.
```

## 5. 간격과 레이아웃

### 5.1 간격 스케일

| 토큰 | 값 | 대표 용도 |
|---|---:|---|
| `space-1` | 2 | 아이콘 사이, 미세 정렬 |
| `space-2` | 4 | 인접 아이콘, compact 내부 |
| `space-3` | 6 | 라벨과 컨트롤, 메뉴 내부 |
| `space-4` | 8 | 버튼 간격, 작은 패딩 |
| `space-5` | 10 | 필드 내부, 카드 내 요소 |
| `space-6` | 12 | 행/사이드바 내부 |
| `space-7` | 14 | 다이얼로그 footer 세로 |
| `space-8` | 16 | 섹션 요소 사이 |
| `space-10` | 20 | 다이얼로그·카드 기본 외곽 |
| `space-12` | 24 | 큰 섹션 분리 |
| `space-14` | 28 | 넓은 페이지 좌우 여백 |

임의의 `7`, `9`, `11`은 아이콘 광학 보정이나 기존 템플릿 정렬 외에는 만들지 않는다.

### 5.2 앱 셸

| 영역 | 공통 기준 |
|---|---|
| 타이틀바 | 높이 `44` |
| compact 타이틀바 | 높이 `32`, 터미널 중심 셸에서만 |
| rail | 너비 `44` |
| rail 버튼 | `32×32` |
| 기본 좌측 패널 | 약 `260`, `devez-code` 정밀값 `262` |
| 좌측 패널 최소 | `190` |
| 중앙 작업영역 최소 | `360` |
| 우측 보조 패널 | 약 `300` |
| splitter 채널 | `4` |
| splitter hover handle | 세로 `2×28`, 가로 `28×2` |
| 패널 경계 | `1` |

레이아웃 원칙:

- 메인 창 외곽은 사각형으로 유지한다.
- 패널은 서로 붙이지 않고 `1px border + 4px channel`로 구분한다.
- 좁은 창에서 보조 패널은 중앙을 압축하기보다 overlay/drawer로 전환한다.
- 제목, toolbar, 필터, 본문, footer 순서를 유지한다.

### 5.3 프레임 선택

두 프레임은 같은 테마와 컨트롤을 쓰지만 정보 구조와 창 동작 밀도가 다르다.

| 구분 | `devez`형 side-rail frame | `devez-code`형 header-footer frame |
|---|---|---|
| 적합한 앱 | 기능 전환이 많은 업무 도구, 메신저, 대시보드 | 터미널, IDE, 편집기, 장시간 상태 감시 도구 |
| 기본 창 | `1100×700`, 최소 `900×600` | `1240×760`, 최소 `960×640` |
| 헤더 | `44` | `32` |
| 좌측 고정 영역 | 축소 `44`, 확장 `158` rail | rail 없음, 기본 sidebar `262` |
| 푸터 | 없음 | `32` 상태·사용량·설정 bar |
| 창 제어 버튼 | `46×32` | `40×26` |
| 최대화 | Windows 작업영역 최대화 | 일반 최대화 또는 설정형 전체화면 |
| 정보 밀도 | default | compact |

선택 규칙:

- 기능의 최상위 전환이 5개 이상이고 아이콘으로 안정적으로 구분되면 `devez`형을 우선한다.
- 터미널·편집기처럼 중앙 작업영역이 핵심이고 항상 보여야 하는 상태가 있으면 `devez-code`형을 우선한다.
- 새 프로젝트에서 판단이 어려우면 기본은 `devez-code`형이다. 세 프로젝트가 모두 갈릴 때 `devez-code`를 우선하는 전체 결정 규칙과 같다.
- 한 창에 확장 rail과 상시 footer가 모두 필요할 때만 두 유형을 결합한다. 단순 장식 목적으로 결합하지 않는다.

### 5.4 공통 borderless window chrome 계약

두 프레임 모두 네이티브 타이틀바를 숨기되 Windows 리사이즈·스냅·DWM 전환은 유지한다.

| 속성 | 값 | 이유 |
|---|---:|---|
| `WindowStyle` | `None` | 자체 헤더 사용 |
| `ResizeMode` | `CanResize` | 테두리 리사이즈 허용 |
| `WindowChrome.ResizeBorderThickness` | `5` | 보이지 않는 네 방향 리사이즈 hit area |
| `WindowChrome.UseAeroCaptionButtons` | `False` | 자체 최소화·최대화·닫기 사용 |
| `WindowChrome.GlassFrameThickness` | `0,0,0,1` | DWM 하단 경계 유지 |
| 일반 모서리 | DWM `ROUND` | Windows 11 창 인상 유지 |
| 최대화·전체화면 모서리 | DWM `DONOTROUND` | 화면 모서리 잘림 방지 |
| 외곽 보더 | `LineBrush`, `1` | 프레임과 배경 분리 |
| 배경 | `BgBrush` | 테마 연동 |

공통 XAML 시작점:

```xml
<Window WindowStyle="None"
        ResizeMode="CanResize"
        Background="{DynamicResource BgBrush}">
    <WindowChrome.WindowChrome>
        <WindowChrome CaptionHeight="{StaticResource FrameHeaderHeight}"
                      ResizeBorderThickness="5"
                      UseAeroCaptionButtons="False"
                      GlassFrameThickness="0,0,0,1" />
    </WindowChrome.WindowChrome>

    <Border x:Name="RootChrome"
            Background="{DynamicResource BgBrush}"
            BorderBrush="{DynamicResource LineBrush}"
            BorderThickness="1">
        <!-- frame -->
    </Border>
</Window>
```

메인 셸에는 `AllowsTransparency=True`를 쓰지 않는다. 투명 창은 네이티브 리사이즈, DWM 애니메이션, 성능과 충돌한다. 라운드 `14` 투명 창 패턴은 팝업·다이얼로그 전용이다.

#### Caption hit-test 지도

`CaptionHeight` 안의 빈 배경은 Windows 비클라이언트 캡션이다. 다음 규칙을 지킨다.

- 빈 헤더 영역: drag, 상단 edge snap, 캡션 double-click 대상.
- 버튼·입력·콤보·링크: `WindowChrome.IsHitTestVisibleInChrome="True"`를 지정한다.
- hit-test 예외 컨트롤은 Windows 캡션 동작을 받지 않는다. 필요한 drag나 double-click을 직접 구현한다.
- 창 제어 버튼을 감싼 오른쪽 `StackPanel` 전체에도 hit-test 예외를 지정한다.
- 헤더 안에서 단순 표시만 하는 요소에는 불필요하게 hit-test 예외를 주지 않는다. 캡션 drag 면적이 줄어든다.

#### Windows 상태 처리

- 최소화: `WindowState = WindowState.Minimized`.
- 일반 최대화: 현재 모니터의 `rcWork`에 맞춘다. 작업표시줄은 덮지 않는다.
- `WM_GETMINMAXINFO`를 처리할 때 XAML의 `MinWidth`·`MinHeight`도 DPI 물리 픽셀로 `ptMinTrackSize`에 다시 넣는다.
- `WindowStyle=None`에 Win32 `WS_CAPTION`을 다시 부여해 DWM 최소화·최대화·복원 애니메이션을 살린다. 시각 캡션은 계속 `WindowChrome`이 담당한다.
- 최대화 상태에서는 시스템 frame + padded border를 DPI로 환산해 루트 margin을 보정하고, 복원 시 `Thickness(0)`으로 되돌린다.
- 최대화·복원 때 최대화 버튼 아이콘을 `IconWinMaximize` ↔ `IconWinRestore`, tooltip을 `최대화` ↔ `이전 크기로` 동기화한다.

### 5.5 프레임 A — `devez`형 header + expandable side rail

#### 전체 geometry

```text
┌────────────────────── 44 header ──────────────────────┐
│ brand · header widgets                         _ □ ×  │
├─44/158 rail─┬─260 sidebar─4─┬──── center ≥350 ────────┤
│ fixed top   │               │                         │
│ scroll body │               │                         │
│ fixed foot  │               │                         │
└─────────────┴───────────────┴─────────────────────────┘
```

| 영역 | 실제 값 |
|---|---:|
| 기본 창 | `Width=1100`, `Height=700` |
| 최소 창 | `MinWidth=900`, `MinHeight=600` |
| root rows | `44`, `*` |
| `WindowChrome.CaptionHeight` | `44` |
| sidebar | 기본 `260`, 최소 `180` |
| sidebar splitter | `4` |
| 중앙 작업영역 | `*`, 최소 `350` |
| 선택형 thread/calendar 열 | 닫힘 `0`, 열릴 때 기능별 폭 |
| title/content 가로선 | `1`, `LineBrush` |
| rail/content 세로선 | `1`, `LineBrush` |

#### 44 DIP header

- 왼쪽 brand 시작 margin은 `6,0,0,0`.
- 로고는 높이 `34`; 브랜드 텍스트는 `17`, `Bold`, `Bruno Ace SC`, margin `3,1,0,0`.
- header chip은 투명 배경, hover `PanelSoftBrush`, radius `4~5`; 칩 사이 separator는 `1×16`, opacity `0.5`.
- 오른쪽 창 제어 그룹은 `Margin="0,0,4,0"`, 수평 배치, 별도 gap 없음.
- 최소화·최대화·닫기 버튼은 각각 `46×32`; 헤더 안의 수직 여유는 위·아래 각 `6`.
- 창 제어 아이콘은 `11×11`, `StrokeThickness=1.4`, round cap/join.
- 버튼 radius는 `5`, 기본 투명, hover `PanelSoftBrush + LineBrush 1`, pressed `LineBrush`.
- 닫기 hover는 `#ef4444/#dc2626`, pressed는 `#dc2626/#b91c1c`, glyph는 흰색.

창 제어 폭:

```text
right margin 4 + [46 min][46 max][46 close] = 142 DIP
```

#### Drag, snap, double-click

- 헤더의 일반 캡션 영역을 누르면 Windows native drag가 시작된다.
- 캡션을 화면 상단으로 끌면 Windows edge snap으로 현재 모니터 작업영역에 최대화된다.
- 최대화된 캡션을 아래로 끌면 이전 일반 창 크기로 복원하며 drag가 계속된다.
- 일반 캡션 영역 double-click은 최대화 ↔ 이전 크기를 토글한다.
- brand 영역은 hit-test 예외이며 `ClickCount == 1`일 때 `DragMove()`를 직접 호출한다.
- 현재 구현에서 brand 영역 double-click은 최대화 토글 대상이 아니다. 동일 동작을 원하면 `ClickCount == 2`에서 최대화 토글을 명시한다.

#### Rail geometry

| 항목 | 기본 | 큰 글꼴 변형 |
|---|---:|---:|
| 축소 rail 폭 | `44` | `46` |
| 확장 rail 폭 | `158` | `158` |
| 버튼 높이 | `32` | `34` |
| 버튼 배경 폭 | 축소 시 `32` | 축소 시 `34` |
| 실제 아이콘 | `16×16` | `18×18` |
| 버튼 외부 좌우 margin | `6` | `6` |
| 버튼 radius | `10` | `10` |
| 연속 버튼 세로 gap | `2` | `2` |
| 확장 caption | `Fs13`, ellipsis | `Fs15`, ellipsis |
| 펼침·접힘 animation | `220ms`, `QuadraticEase/EaseOut` | 동일 |

`AppStyles.xaml`의 초기 `RailIconSize=30`만 복사하지 않는다. 앱 시작의 font-scale 적용 후 실제 표준 렌더 크기는 `16`, 큰 글꼴은 `18`이다.

Rail은 세 구역으로 나눈다.

1. 상단 고정: 위 margin `5`, menu 버튼, 아래 gap `4`, home, separator.
2. 중간 scroll: 기능 버튼 목록. scrollbar는 숨기고 wheel 및 별도 위·아래 버튼으로 이동한다.
3. 하단 고정: separator, 개발요청·관리자·설정·휴지통·잠금·로그아웃, 아래 margin `10`.

상·하 separator:

```xml
<Border Height="1"
        Margin="12,8"
        Background="{DynamicResource LineBrush}" />
```

Rail 버튼 상태:

| 상태 | 배경 | foreground |
|---|---|---|
| normal | `Transparent` | `RailIconBrush` |
| hover | `PanelSoftBrush` | 유지 |
| pressed | `LineBrush` | 유지 |
| active (`Tag="active"`) | `PrimaryBrush` | `White` |

확장 상태에서는 아이콘용 고정 `32` 열 오른쪽에 `ToolTip` 문자열을 caption으로 재사용한다. caption은 `Fs13`, `CharacterEllipsis`. 축소 상태에서는 caption을 `Collapsed`로 두고 tooltip만 사용한다.

copy-ready Rail 버튼 template:

```xml
<Style x:Key="RailIconButton" TargetType="Button">
    <Setter Property="IsTabStop" Value="False" />
    <Setter Property="FocusVisualStyle" Value="{x:Null}" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="Foreground" Value="{DynamicResource RailIconBrush}" />
    <Setter Property="HorizontalAlignment" Value="Stretch" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="Button">
                <Border x:Name="Bd"
                        Height="{DynamicResource RailBtnSize}"
                        Margin="6,0"
                        CornerRadius="10"
                        Background="{TemplateBinding Background}">
                    <StackPanel Orientation="Horizontal">
                        <Grid Width="{DynamicResource RailBtnSize}">
                            <ContentPresenter HorizontalAlignment="Center"
                                              VerticalAlignment="Center" />
                        </Grid>
                        <TextBlock x:Name="Cap"
                                   Text="{TemplateBinding ToolTip}"
                                   Visibility="Collapsed"
                                   VerticalAlignment="Center"
                                   FontSize="{DynamicResource Fs13}"
                                   Foreground="{TemplateBinding Foreground}"
                                   TextTrimming="CharacterEllipsis" />
                    </StackPanel>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Bd" Property="Background"
                                Value="{DynamicResource PanelSoftBrush}" />
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                        <Setter TargetName="Bd" Property="Background"
                                Value="{DynamicResource LineBrush}" />
                    </Trigger>
                    <Trigger Property="Tag" Value="active">
                        <Setter TargetName="Bd" Property="Background"
                                Value="{DynamicResource PrimaryBrush}" />
                        <Setter Property="Foreground" Value="White" />
                    </Trigger>
                    <DataTrigger Binding="{Binding DataContext.IsRailExpanded,
                                                   RelativeSource={RelativeSource AncestorType=Window}}"
                                 Value="True">
                        <Setter TargetName="Cap" Property="Visibility" Value="Visible" />
                    </DataTrigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

#### Rail overflow와 badge

- 중간 영역 overflow가 있을 때만 위·아래 scroll 버튼을 표시한다.
- scroll 버튼은 높이 `24`; 내부 border `Margin="6,1"`, radius `6`.
- chevron은 `15×15`, stroke `2.4`, `PrimaryBrush`.
- 내부 배경은 `PanelSoftBrush`, hover `LineBrush`; shadow는 black `0.35`, blur `7`, depth `1.5`.
- 한 번 클릭할 때 vertical offset을 `44` 이동한다.
- badge는 기본 `16×16`, 큰 글꼴 `18×18`; 축소 font `9/10`, 확장 font `10/11`.
- 축소 badge는 왼쪽 위 `Margin="22,1,0,0"`; 확장 badge는 오른쪽 중앙 `Margin="0,0,14,0"`.
- active rail item의 badge는 흰 배경 + `PrimaryBrush` 텍스트로 반전한다.

#### Rail 상태와 닫기

- 펼침 상태는 view model/session에 저장한다.
- 첫 화면 복원은 animation 없이 폭을 즉시 적용한다. 사용자 toggle만 `220ms` animation을 쓴다.
- rail 폭이 바뀌면 다음 column 경계가 자동 이동해야 한다. overlay의 수동 left margin을 별도로 보정하지 않는다.
- 닫기 버튼은 `close-to-tray` 설정이 켜지면 tray로 숨기고, 꺼지면 앱 종료 흐름을 실행한다.

### 5.6 프레임 B — `devez-code`형 compact header + status footer

#### 전체 geometry

```text
┌────────────────────── 32 header ──────────────────────┐
│ DevezCode · panel toggles · perf/status       _ □ ×   │
├─262 sidebar─4─┬──── center ≥360 ────4─300 auxiliary───┤
│               │                                       │
├────────────────────── 32 footer ──────────────────────┤
│ provider usage bars                         actions   │
└───────────────────────────────────────────────────────┘
```

| 영역 | 실제 값 |
|---|---:|
| 기본 창 | `Width=1240`, `Height=760` |
| 최소 창 | `MinWidth=960`, `MinHeight=640` |
| root rows | `32`, `Auto(현재 0)`, `*`, `Auto=32` |
| `WindowChrome.CaptionHeight` | `32` |
| root outer border | `1`, `LineBrush` |
| sidebar | 기본 `262`, 최소 `190` |
| sidebar splitter | `4` |
| 중앙 작업영역 | `*`, 최소 `360` |
| 우측 보조 패널 | 기본 `300`, 최소 `0` |
| 우측 splitter | `4` |
| footer | `32`, 상단 border `1` |

#### 32 DIP compact header

- 브랜드 블록 margin은 `6,0,0,0`.
- 브랜드 텍스트는 `12`, `Bold`, `Bruno Ace SC`, margin `3,1,0,0`.
- 브랜드 오른쪽 첫 panel toggle은 왼쪽 margin `8`.
- `TitleBarChipButton`은 `30×26`, radius `5`, 아이콘 `16×16`; 기본 투명, hover `PanelSoftBrush`.
- 칩 그룹 divider는 `1×16`, `Margin="3,0,3,0"`, opacity `0.5`.
- 오른쪽 전체 그룹은 `Margin="0,0,4,0"`.
- 최소화·최대화·닫기 버튼은 `40×26`; 헤더 안 수직 여유는 위·아래 각 `3`.
- 최대화 아이콘은 `11×11`, stroke `1.4`; 최소화 glyph는 `16`, 닫기 glyph는 `13`.
- 버튼 radius `5`, hover/pressed/close 상태는 프레임 A와 같다.

창 제어 폭:

```text
right margin 4 + [40 min][40 max][40 close] = 124 DIP
```

브랜드 텍스트는 hit-test 예외다. 현재 세 번 클릭은 GPU ↔ software rendering 전환 전용이다. 따라서 drag와 double-click은 브랜드 텍스트가 아닌 빈 header 캡션 영역에서 수행한다.

#### 32 DIP status footer

- footer는 `Height=32`, `BgBrush`, 위쪽 `LineBrush 1`.
- 상태 content는 `Margin="14,0"`에서 시작하고 수직 중앙 정렬한다.
- provider icon은 `15×15`, 뒤 gap `7`.
- 상태 label과 값은 `Fs12`.
- 사용량 bar는 `56×6`, radius `3`, track `LineBrush`.
- label→bar gap과 bar→percent gap은 각각 `6`.
- 같은 provider 안의 다음 기간 그룹은 왼쪽 gap `14`.
- provider 사이 divider는 `1×12`, 좌우 margin `14`, opacity `0.55`.
- footer action은 `30×26`, 아이콘 `15×15`, 각 오른쪽 gap `2`, 마지막 설정 버튼 오른쪽 margin `4`.
- action column은 `Width=Auto`, `MinWidth=48`이며 우측 보조 패널 접힘과 무관하게 항상 오른쪽에 남는다.
- 우측 보조 패널과 splitter의 footer column은 `SharedSizeGroup`으로 body column과 묶는다.
- sidebar footer column은 접기·펼치기와 splitter 완료 시 body sidebar 폭을 코드로 동기화한다. 상태 문자열 자체는 `Grid.ColumnSpan`으로 넓게 흘려 sidebar 폭 변화에 밀리지 않게 한다.
- 상태 항목이 많아질 때 높이를 키우지 않는다. 낮은 우선순위 provider를 숨기거나 overflow popup으로 보낸다.

#### Drag, edge snap, double-click

전체화면 설정 OFF:

- 빈 header drag는 native caption drag다.
- 화면 상단 drag는 Windows 최대화로 연결되며 작업표시줄을 제외한 `rcWork`를 채운다.
- header double-click과 최대화 버튼은 일반 최대화 ↔ 이전 크기를 토글한다.
- 최대화 상태에서 header를 끌어내리면 일반 창으로 복원된 채 drag가 이어진다.

전체화면 설정 ON:

- 최대화 버튼, header double-click, 상단 edge snap, `Win+↑`로 들어온 최대화 요청을 수동 전체화면으로 변환한다.
- 수동 전체화면은 `WindowState.Normal`을 유지한 채 현재 모니터 전체 rect를 채워 작업표시줄까지 덮는다.
- 전체화면 중 `ResizeMode=NoResize`; 활성 창일 때만 `Topmost=True`, 비활성화하면 `False`.
- 전체화면 진입 시 root maximize margin은 `0`; DWM corner는 `DONOTROUND`.
- 전체화면 header double-click은 해제한다.
- 전체화면 header를 drag threshold 이상 끌면 현재 모니터 작업영역의 `70%×70%` 창으로 축소하고 drag를 계속한다.
- 축소 시 가로 grab 위치는 모니터 폭 대비 비율, 세로 grab 위치는 캡션 상단 기준 물리 pixel offset을 보존한다. 창이 커서에서 튀지 않아야 한다.
- 전체화면에서 단순 클릭 후 release는 아무 동작도 하지 않는다.
- 진입 전 bounds는 창 배치 저장용으로 보존하지만, 사용자가 전체화면을 해제할 때 실제 복원 geometry는 현재 구현 기준 `70%` 중앙 배치다.

전체화면 drag 상태 머신:

```text
WM_NCLBUTTONDOWN(HTCAPTION)
  ├─ double-click time/distance 충족 → fullscreen toggle
  └─ capture + pending
       ├─ WM_MOUSEMOVE가 SM_CXDRAG/SM_CYDRAG 초과
       │    → 70% restore → grab offset 고정 → 수동 SetWindowPos drag
       └─ WM_LBUTTONUP
            → pending/drag/capture 해제
```

#### 닫기와 종료

- 최소화 버튼은 즉시 `WindowState.Minimized`.
- 닫기 버튼은 `Close()`를 호출한다.
- `minimize-on-close` 설정이 켜져 있으면 `Closing`을 취소하고 최소화한다.
- 설정이 꺼져 있으면 종료 확인 후 살아 있는 터미널 세션을 graceful shutdown하고 닫는다.
- 닫기 버튼 context menu의 `완전히 종료`는 `ForceQuit=True`로 minimize-on-close를 우회한다.
- 종료 오버레이를 올리기 전 WebView2/HwndHost를 같은 frame에 숨겨 airspace 침범과 여러 번의 flicker를 막는다.

### 5.7 프레임 렌더링·리사이즈 규칙

#### DIP와 pixel

- XAML geometry는 DIP다. `32`, `44`, `158`, `5`를 물리 pixel로 간주하지 않는다.
- `WM_GETMINMAXINFO`, monitor rect, cursor 위치, `SetWindowPos`는 물리 pixel이다. `VisualTreeHelper.GetDpi`로 명시 변환한다.
- `UseLayoutRounding=True`, `SnapsToDevicePixels=True`를 사용한다.
- divider `1`과 외곽 border `1`은 반 pixel에 놓이지 않도록 column/row 경계를 정수 DIP에 유지한다.
- 100%, 125%, 150% DPI와 서로 다른 DPI의 다중 모니터 사이 drag를 확인한다.

#### 텍스트

- Window 전역은 `TextFormattingMode=Display`; 실제 `TextBlock`, `TextBox`, `RichTextBox` 암시적 스타일은 `Ideal + ClearType`로 보정한다.
- keyed text style은 반드시 `BasedOn="{StaticResource {x:Type TextBlock}}"` 등으로 전역 렌더 설정을 이어받는다.
- 브랜드만 `Bruno Ace SC`; 상태·caption·button은 `Pretendard`.
- header/footer 밀도 때문에 font를 `11` 아래로 줄이지 않는다. compact 기본은 `12`.

#### WebView2·HwndHost

창·패널 리사이즈가 WebView2 터미널을 포함하면 WPF `ZIndex`만으로 flicker를 가릴 수 없다.

- overlay로 덮거나 완전히 숨김: snapshot을 표시하고 host는 `Collapsed`. `Hidden` 금지.
- 패널 폭 변경: WebView DOM 내부 cover를 올리고 HWND는 유지한다. 최종 폭에서 한 번 fit한 뒤 cross-fade한다.
- 전체 창 최대화·전체화면: DOM cover를 `stretch`해 새 viewport 전체를 가린다.
- 전체화면에서 drag 복원: 입력 반응성이 우선이므로 비동기 snapshot 대기를 넣지 않는다. 즉시 단색/DOM cover 후 복원한다.
- 종료: 모든 panel snapshot 준비 → render frame 대기 → 모든 HWND 일괄 hide → 종료 overlay 표시 순서다.

#### Main shell과 rounded dialog 구분

- 메인 셸은 DWM 모서리를 사용하며 자식에 rounded clip을 강제하지 않는다.
- `CornerRadius=14` borderless dialog에서 `ClipToBounds=True`만으로는 자식이 둥글게 잘리지 않는다.
- dialog content에 `RectangleGeometry(radius = outer radius - border thickness)`를 `Loaded + SizeChanged`마다 적용한다.
- 이 clip 규칙을 메인 resizable shell 전체에 적용하면 maximize·DPI 변경 때 불필요한 clip 갱신과 모서리 artifact가 생길 수 있다.

#### 프레임 검수

- [ ] 빈 header drag, 버튼 위 drag 차단, 입력 선택이 각각 맞게 동작
- [ ] header double-click 최대화/복원
- [ ] 상단 edge snap, 최대화 상태 drag-down 복원
- [ ] 4개 모서리와 4개 edge의 `5` DIP resize hit area
- [ ] 최소화·최대화·복원 DWM animation
- [ ] 최대화 시 현재 모니터 작업영역과 정확히 일치
- [ ] 전체화면 ON일 때 작업표시줄까지 덮고 비활성화 시 다른 창이 앞에 표시
- [ ] 전체화면 drag-down이 `70%` 창으로 커서 grab 지점을 유지
- [ ] 최대화·전체화면에서 square corner, 일반 창에서 rounded corner
- [ ] rail `44↔158`, caption ellipsis, overflow scroll, badge 정렬
- [ ] footer 높이 `32`, usage bar `56×6`, action `30×26`
- [ ] 최소 창 크기에서 중앙 작업영역 최소 폭 보존
- [ ] 100%, 125%, 150% 및 mixed-DPI 모니터 이동
- [ ] WebView2 포함 상태에서 패널·창 resize와 종료 overlay flicker 없음

## 6. 라운드와 보더

### 6.1 라운드 스케일

| 값 | 용도 |
|---:|---|
| `0` | 데이터 편집기, 표 내부, edge-to-edge 영역 |
| `4` | compact 필드, DataGrid 셀 상태, DatePicker |
| `5` | 타이틀바 칩, 창 제어, 작은 콤보 |
| `6` | tooltip, menu item, 일반 아이콘 버튼 |
| `7` | selector chip |
| `8` | 입력 컨테이너, 사이드바 항목, 카드 |
| `10` | 기본 버튼, 큰 입력, panel/card, popup 내부 |
| `14` | 다이얼로그·context menu shadow 외곽 |
| `50` | 원형 send/action 버튼 |

### 6.2 보더

| 두께 | 용도 |
|---:|---|
| `0` | 채움 버튼, 투명 toolbar 버튼 |
| `0.5` | toggle thumb 광학 경계 |
| `1` | 패널, 카드, 메뉴, tooltip, 표 grid line |
| `1.5` | 입력, secondary button, checkbox, 선택 강조 |
| `2` | spinner, 작은 강조 아이콘 |
| `3` | 큰 loading spinner |

Secondary button의 보더는 콘텐츠 크기를 줄이지 않도록 별도 overlay로 그린다.

```xml
<Grid>
    <Border x:Name="Bd"
            Background="{TemplateBinding Background}"
            CornerRadius="10"
            Padding="{TemplateBinding Padding}" />
    <Border CornerRadius="10.75"
            BorderBrush="{DynamicResource LineBrush}"
            BorderThickness="1.5"
            Margin="-0.75"
            Background="Transparent"
            IsHitTestVisible="False" />
</Grid>
```

## 7. 그림자와 레이어

| 용도 | 값 |
|---|---|
| 다이얼로그 shadow depth | `0` |
| 기본 blur | `40` |
| dark blur | `48` 허용 |
| dark opacity | `0.68` |
| minimal/soft opacity | `0.35` |
| popup dark opacity | `0.42` |
| popup light opacity | `0.18` |
| shadow outer margin | `20`, dark `22` |
| content border | `1` |
| dialog outer radius | `14` |

그림자 layer와 실제 content layer를 분리한다. 그림자에 `ClipToBounds`를 쓰지 않는다. content layer만 clip한다.

## 8. 컨트롤 규격

### 8.1 버튼

| 종류 | 높이 | 최소 너비 | 패딩 | 라운드 | 배경 |
|---|---:|---:|---|---:|---|
| Primary | 38 | 80 | `16,0` | 10 | Accent |
| Secondary | 38 | 80 | `16,0` | 10 | Surface + 1.5 border |
| Success | 38 | 80 | `16,0` | 10 | Success |
| Today/Special | 38 | 80 | `16,0` | 10 | Today |
| Compact page | 29~32 | 화면별 | `14~16,0` | 4 | eGhis 밀도형 |
| Small utility | 28~34 | 화면별 | `9~14,0` | 6~8 | transparent/surface |

높이 `38`과 최소 너비 `80`은 `PrimaryButton`/`SecondaryButton` 스타일 세터가 아니라 **사용처 규격**이다. 다이얼로그·폼의 액션 버튼은 반드시 `Height="38" MinWidth="80"`을 지정하고, toolbar·인라인 등 compact 사용처는 `28~36`을 쓴다. 스타일만 적용하고 높이를 빠뜨리면 콘텐츠 크기로 수축한다.

상태:

| 상태 | Primary | Secondary |
|---|---|---|
| Normal | Accent / White | Surface / Text primary / Border soft |
| Hover | Accent hover | Surface soft |
| Pressed | Accent pressed | Border soft 또는 Accent soft |
| Disabled | Border soft / Text secondary | Border soft / Text secondary |

Primary 예제:

```xml
<Style x:Key="PrimaryButton" TargetType="Button">
    <Setter Property="FocusVisualStyle" Value="{x:Null}" />
    <Setter Property="Background" Value="{DynamicResource PrimaryBrush}" />
    <Setter Property="Foreground" Value="White" />
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="FontWeight" Value="SemiBold" />
    <Setter Property="Padding" Value="16,0" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="Button">
                <Border x:Name="Bd"
                        Background="{TemplateBinding Background}"
                        CornerRadius="10"
                        Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center"
                                      VerticalAlignment="Center"
                                      TextElement.Foreground="{TemplateBinding Foreground}" />
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Bd" Property="Background"
                                Value="{DynamicResource PrimaryHoverBrush}" />
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                        <Setter TargetName="Bd" Property="Background"
                                Value="{DynamicResource PrimaryPressedBrush}" />
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                        <Setter TargetName="Bd" Property="Background"
                                Value="{DynamicResource LineBrush}" />
                        <Setter Property="Foreground"
                                Value="{DynamicResource TextMutedBrush}" />
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

### 8.2 아이콘 버튼과 원형 버튼

| 용도 | 클릭 영역 | 아이콘 | 라운드 |
|---|---:|---:|---:|
| Tiny inline | `22×22` | `10~12` | 5~6 |
| Compact toolbar | `24×24` | `12~14` | 6 |
| Standard toolbar | `28×28` | `13~16` | 6 |
| Header action | `28×28` | `14~16` | 6 |
| Bubble round action | `28×28` | `13~14` | 14 |
| Rail | `32×32` | 실제 `16`, 큰 글꼴 `18` | 10 |
| Circular send | `36×36` 권장 | `16` | 50 |
| Titlebar chip | `30×26` | `16` | 5 |
| Window control | `40×26` | `11~16` | 5 |
| Dialog close | `40×32` | `11` | 5 |

일반 아이콘 버튼:

```xml
<Button Width="28"
        Height="28"
        Padding="4"
        Style="{StaticResource IconButton}"
        Cursor="Arrow"
        ToolTip="새로 고침">
    <Path Data="{StaticResource IconRefresh}"
          Style="{StaticResource LucideButtonIcon}"
          Width="16"
          Height="16" />
</Button>
```

- hover: `SurfaceSoft`, 아이콘 `TextPrimary`
- pressed: `BorderSoft`
- active: `Accent`, 아이콘 `White`
- disabled: 전체 opacity `0.35`
- close hover: `#EF4444`, pressed `#DC2626`, 아이콘 `White`

### 8.3 입력

#### 기본 한 줄 입력

| 속성 | 값 |
|---|---|
| 높이 | 40 |
| 배경 | Surface |
| 보더 | Border soft, `1.5` |
| 라운드 | 8 |
| 좌우 패딩 | 10~12 |
| 글꼴 | 14 |
| caret | Text primary |
| selection | Accent, opacity `0.35` |
| focus | 보더 Accent |
| disabled | opacity `0.45~0.55` |

```xml
<Border Height="40"
        Padding="10,0"
        Background="{DynamicResource PanelBrush}"
        BorderBrush="{DynamicResource LineBrush}"
        BorderThickness="1.5"
        CornerRadius="8">
    <TextBox Background="Transparent"
             BorderThickness="0"
             FontSize="14"
             Foreground="{DynamicResource TextBrush}"
             CaretBrush="{DynamicResource TextBrush}"
             SelectionBrush="{DynamicResource PrimaryBrush}"
             SelectionOpacity="0.35"
             VerticalContentAlignment="Center" />
</Border>
```

#### 검색 입력

돋보기 아이콘 + placeholder 골격을 화면마다 새로 만들지 않는다.

| 속성 | 값 |
|---|---|
| 컨테이너 | `PanelSoftBrush` 배경, `LineBrush 1`, 라운드 `8` |
| 높이 | `35~37` (패널 밀도에 맞춤) |
| 아이콘 | `IconSearch` `12~13`, `TextMutedBrush`, 왼쪽 margin `9~10` |
| 입력 | 투명 borderless `TextBox`, `Fs13` |
| placeholder | 별도 `TextBlock`, `TextMutedBrush`, `Fs13`, `IsHitTestVisible=False`, `Text=""`일 때만 표시 |
| 열림 방식 | 헤더 돋보기 버튼이 검색 행 Height를 `0 ↔ 행 높이`로 슬라이드 |

버튼 토글 활성화 animation 규칙:

- 검색 행 `Height`를 `0 ↔ 행 높이`(pill + 상하 margin 실측값, 예: 카드 내 `37`, 사이드바 전역 `47`)로 애니메이션해 아래 목록을 밀어낸다. `Visibility` 토글로 뚝 끊지 않는다.
- 시간 `220ms`, `CubicEase EaseInOut`. (기본 모션의 `EaseOut`과 달리 열림·닫힘이 대칭인 밀어내기라 `EaseInOut`을 쓴다.)
- 컨테이너에 `ClipToBounds=True`를 줘 접히는 동안 내용이 부분적으로 넘쳐 보이지 않게 한다.
- 다시 열 때는 현재 `ActualHeight`에서 시작(`From` 지정)해 진행 중 애니메이션과 충돌하지 않게 한다.
- 열림 완료를 기다리지 않고 `Dispatcher(Input priority)`로 즉시 입력에 `Focus()` + `SelectAll()`을 실행해 연속 재검색을 지원한다.
- 스코프가 있는 인라인 검색(카드 내 세션 검색 등)은 닫을 때 검색어를 초기화해 목록이 숨은 필터 상태로 남지 않게 한다.

#### Compact 입력

데이터 화면, filter bar, 표 상단에서만 쓴다.

| 속성 | 값 |
|---|---|
| 높이 | 32 |
| 라운드 | 4 |
| 보더 | 1 |
| 글꼴 | 13 |
| 패딩 | `8,0` |

#### 여러 줄 입력

- 패딩 `16,14`
- 라운드 `10`
- 보더 `1.5`
- 본문 `14`
- vertical alignment `Top`
- 내부 스크롤바가 생기는 edge-to-edge editor는 라운드 `0`을 허용한다.
- 스크롤바가 보더까지 닿아야 하는 editor는 콘텐츠에만 패딩을 주고 scrollbar 열에는 주지 않는다.

### 8.4 ComboBox

| 변형 | 높이 | 라운드 | 보더 | 글꼴 |
|---|---:|---:|---:|---:|
| Dock/meta | 24 | 5 | 1 | 12 |
| Filter compact | 32 | 4 | 1 | 13 |
| 일반 form | 40 | 8 | 1.5 | 14 |

- popup은 Surface, border `1`, 라운드 `10`, 내부 padding `2~4`
- item padding은 `9,5` 또는 `10,8`
- selected item은 Accent + White
- hover item은 Surface soft
- chevron은 `11×11`, 획 `2`

객체 목록 표시 규칙:

- 문자열 목록은 `SelectedItem`을 사용한다.
- 객체 목록은 `SelectedValuePath`와 `SelectedValue` 타입을 정확히 맞춘다.
- `DisplayMemberPath`에만 의존하지 않고 명시적 `ItemTemplate`을 둔다.
- 옵션 객체의 `ToString()`도 같은 표시 문자열을 반환하게 해 닫힌 selection box의 fallback을 보장한다.
- 초기값을 목록 안 값으로 보정해 `SelectedIndex=-1` 상태를 피한다.

```xml
<ComboBox Style="{StaticResource FilterCombo}"
          ItemsSource="{Binding Options}"
          SelectedValuePath="Value"
          SelectedValue="{Binding SelectedValue, Mode=TwoWay}">
    <ComboBox.ItemTemplate>
        <DataTemplate>
            <TextBlock Text="{Binding DisplayName}" />
        </DataTemplate>
    </ComboBox.ItemTemplate>
</ComboBox>
```

### 8.5 CheckBox

| 속성 | 값 |
|---|---|
| box | `18×18` |
| 라운드 | 4 |
| 보더 | `1.5` |
| check 획 | 2 |
| label gap | 8 |
| label | 13 |

상태:

- normal: transparent + Border soft
- hover: Border Accent
- checked: Accent fill, 보더 0, white check
- content가 없으면 오른쪽 gap을 제거한다.

### 8.6 Toggle switch

| 변형 | Track | Thumb | 이동 | Track 라운드 |
|---|---:|---:|---:|---:|
| 기본 | `44×24` | `20×20` | 20 | 12 |
| Small | `36×20` | `16×16` | 16 | 10 |

- thumb margin `2`
- thumb border `0.5`
- off: Border soft/surface 계열
- on: Accent
- 전환 `150ms`
- 상태가 바뀌지 않을 때 상시 animation clock을 유지하지 않는다.

### 8.7 카드와 패널

| 용도 | 배경 | 보더 | 라운드 | 패딩 |
|---|---|---:|---:|---|
| 일반 panel | Surface | 1 | 4~10 | 14~20 |
| interactive card | Surface | 1 | 8~10 | 12~16 |
| selected card | 역할별: 8.8 참조 | 역할별: 8.8 참조 | 8~10 | 동일 |
| soft section | Surface soft | 0~1 | 8~10 | 12~16 |
| data panel | Surface | 1 | 4 | 화면 밀도형 |

interactive card:

- hover 배경은 Surface soft
- selected 상태는 hover보다 강해야 한다.
- 카드 전체가 클릭 가능하면 내부 action 버튼의 hit test와 충돌하지 않게 한다.
- 카드 안에 또 다른 큰 라운드 panel을 중첩하지 않는다.

### 8.8 사이드바와 내비게이션

| 종류 | 패딩 | 글꼴 | 라운드 |
|---|---|---:|---:|
| Sidebar item | `12,13` | 14 | 8 |
| Sidebar sub item | `10,6` | 12 | 부모 8 |
| Thread room item | `14,14` | 14 | 8 |
| Menu button | `10,6` | 12 | 6 |

- 기본 배경 transparent
- hover/selected 배경 Surface soft
- selected는 필요할 때 accent bar, bold, icon 색 중 하나를 추가한다.
- rail selected는 Accent fill + White icon을 사용한다.
- 접기/펼치기 후 포커스가 terminal/editor로 돌아가야 하는 흐름은 명시적으로 복구한다.

#### 좌·우 패널 선택 강조는 다르게 한다

`devez-code`의 좌측 프로젝트 패널과 우측 파일 탐색 패널은 같은 `selected` 표현을 복사하지 않는다. 패널 역할에 따라 다음 규칙을 고정한다.

| 영역/역할 | Normal | Hover | Selected | 금지 |
|---|---|---|---|---|
| 좌측 주 내비게이션의 독립 카드 | `PanelBrush` + `LineBrush` `1.5` | 배경은 유지하고 `ProjectCardHoverBorderBrush` 보더 | 배경은 유지하고 `PrimaryBrush` 보더 `1.5` | selected를 배경색만으로 표현 |
| 우측 보조 패널의 트리·목록 행 | transparent | `HoverBrush` 배경 | `PrimarySoftBrush` 배경만 | selected Accent 보더, 카드 외곽선 추가 |

핵심:

- 좌측은 프로젝트처럼 화면의 주 컨텍스트를 바꾸는 **독립 카드**다. 선택 시 카드 실루엣 전체의 보더만 Accent로 바꾸고 배경을 채우지 않는다.
- 우측은 현재 컨텍스트 안의 파일·항목을 고르는 **조밀한 행 목록**이다. 선택 시 행 배경만 `PrimarySoftBrush`로 채우고 보더는 만들지 않는다.
- 우측 행의 배경 하이라이트는 텍스트/아이콘 content 영역에만 적용한다. expander와 자식 들여쓰기 영역 전체를 큰 카드처럼 감싸지 않는다.
- hover보다 selected가 강해야 하지만 표현 채널은 유지한다. 좌측은 `border → stronger border`, 우측은 `background → stronger background`다.
- 폴더가 접힌 상태에서 내부에 선택 프로젝트가 있으면 좌측 폴더 카드 보더도 `PrimaryBrush`로 표시해 선택 위치를 잃지 않게 한다.
- drag/drop target, validation, keyboard focus ring은 selected와 별도 상태다. 이 상태 때문에 좌·우 선택 규칙을 섞지 않는다.

좌측 카드 예제:

```xml
<Style x:Key="PrimaryContextCard" TargetType="Border">
    <Setter Property="Background" Value="{DynamicResource PanelBrush}" />
    <Setter Property="BorderBrush" Value="{DynamicResource LineBrush}" />
    <Setter Property="BorderThickness" Value="1.5" />
    <Setter Property="CornerRadius" Value="10" />
    <Style.Triggers>
        <DataTrigger Binding="{Binding IsSelected}" Value="True">
            <Setter Property="BorderBrush" Value="{DynamicResource PrimaryBrush}" />
        </DataTrigger>
    </Style.Triggers>
</Style>
```

우측 행 예제:

```xml
<ControlTemplate TargetType="TreeViewItem">
    <Border x:Name="RowBackground"
            Background="Transparent"
            BorderThickness="0"
            CornerRadius="6"
            Padding="4,0">
        <ContentPresenter ContentSource="Header" />
    </Border>
    <ControlTemplate.Triggers>
        <Trigger SourceName="RowBackground" Property="IsMouseOver" Value="True">
            <Setter TargetName="RowBackground" Property="Background"
                    Value="{DynamicResource HoverBrush}" />
        </Trigger>
        <Trigger Property="IsSelected" Value="True">
            <Setter TargetName="RowBackground" Property="Background"
                    Value="{DynamicResource PrimarySoftBrush}" />
        </Trigger>
    </ControlTemplate.Triggers>
</ControlTemplate>
```

### 8.9 ContextMenu와 MenuItem

ContextMenu:

- 배경 Surface
- 보더 `1`
- 내부 padding `4`
- content 라운드 `10`
- shadow 외곽 라운드 `14`
- 자체 `HasDropShadow=False`; 공용 shadow layer 사용

MenuItem:

- 글꼴 `12~13`
- padding `10,8`
- 라운드 `6`
- hover 배경 Surface soft
- disabled opacity `0.4`
- 기본 템플릿 아이콘 호스트는 `16×16`이다. 실사용 컨텍스트 메뉴 아이콘은 `LucideIcon`을 `13×13`으로 지정하고, 닫기·X류만 `11×11`, 텍스트 편집 메뉴(`EditMenuItem`)는 `14×14`를 쓴다.
- 텍스트와 shortcut 사이 여유 `20`

WPF 안전 규칙:

- 이벤트가 연결된 `ContextMenu`, `MenuItem`, `Button`, `ControlTemplate` 트리를 `Style.Setter.Value` 안에 직접 만들지 않는다.
- 상위 `Resources`로 분리하고 Setter에서는 `{StaticResource ...}`만 참조한다.
- 여러 컨트롤에 붙는 ContextMenu는 부모 충돌 방지가 필요하면 `x:Shared="False"`를 쓴다.
- `Setter.Value` 안의 이벤트 트리는 `IComponentConnector.connectionId` 충돌로 `XamlParseException`을 만들 수 있다.

```xml
<UserControl.Resources>
    <ContextMenu x:Key="RowMenu" x:Shared="False">
        <MenuItem Header="복사" Click="Copy_Click" />
    </ContextMenu>
</UserControl.Resources>

<Style x:Key="RowItem" TargetType="ListBoxItem">
    <Setter Property="ContextMenu" Value="{StaticResource RowMenu}" />
</Style>
```

### 8.10 ToolTip

| 속성 | 값 |
|---|---|
| 글꼴 | 12 |
| padding | `8,5` |
| 라운드 | 6 |
| 보더 | 1 |
| 배경 | Surface 또는 Surface soft |
| 텍스트 | Text primary |

툴팁은 동작 이름을 짧게 쓰고, 필요한 경우 괄호로 shortcut을 붙인다.

### 8.11 DataGrid

`eGhisDevWPF` 고유 구현을 공통 data-dense 패턴으로 채택한다.

| 항목 | 값 |
|---|---|
| row height | 30 |
| column header height | 26 |
| header 글꼴 | 14 SemiBold |
| cell 글꼴 | 13~14 |
| cell 보더 | `0,0,1,1` |
| header 보더 | `0,0,1,1` |
| grid outer border | `0,1,0,0` |
| row 배경 | Surface |
| alternate row | Surface alt |
| selection | Accent soft + 강조 overlay |
| virtualizing scroll unit | Item |

규칙:

- selection은 full row
- row 높이는 `30`으로 고정해 열마다 흔들리지 않게 한다.
- 정렬 화살표와 실제 sort descriptor를 항상 같이 초기화한다.
- 우클릭으로 context menu를 열 때 먼저 해당 행을 선택한다.
- horizontal scrollbar가 생겨도 star column이 무너지지 않게 별도 보정한다.

선택 행 보더:

- 선택 자체와 keyboard focus를 분리한다.
- 같은 창에 여러 grid가 있으면 마지막으로 클릭한 grid 하나만 active grid로 표시한다.
- active grid의 selected row에만 `1.5px Accent` overlay border를 그린다.
- 포커스 기반 `IsKeyboardFocusWithin`은 셀 이동·overlay에서 flicker/stuck 상태를 만들 수 있으므로 쓰지 않는다.
- click 시점에 visual tree에서 같은 창의 형제 grid를 찾아 active 상태를 전환한다.
- 콘텐츠 교체로 `Unloaded`가 발생해도 attached handler를 바로 제거하지 않는다. `Enabled=false`일 때만 제거한다.

### 8.12 DatePicker와 Calendar

DatePicker:

| 속성 | 값 |
|---|---|
| 높이 | 36 |
| 최소 너비 | 120 |
| 글꼴 | 12 |
| 필드 라운드 | 4 |
| 보더 | 1.5 |
| 버튼 너비 | 34 |

Calendar popup:

- outer 라운드 `10`
- border `1`
- padding `10,8` 또는 `12,10`
- day button 라운드 `4~6`
- day button 최소 `38×34`
- month/year button 최소 `54×38`
- navigation icon `11`, 획 `2`
- `PART_MonthView`, `PART_YearView`, `DisplayMode` trigger를 함께 구현한다.

날짜 텍스트 클릭 시 전체 선택이 먼저 번쩍이지 않도록 segment selection을 같은 mouse-down 경로에서 동기 실행한다.

### 8.13 Tabs

작업영역 탭은 `devez-code`의 연결형 tab 패턴을 기준으로 한다.

| 항목 | 값 |
|---|---|
| tab bar 높이 | 35 |
| tab bar padding | `0,4,12,0` |
| 첫 탭 앞 여백 | 11 |
| tab 최소/최대 너비 | `131 / 248` |
| tab padding | `0,6` |
| tab radius | `9,9,0,0` |
| tab border | `1,1,1,0` |
| tab bottom margin | `-1` |
| tab title | 12 SemiBold, ellipsis |
| close/hide | `16×16`, radius 5 |
| selected seam | 높이 1, content surface 색 |

```xml
<Border x:Name="TabBd"
        MinWidth="131"
        MaxWidth="248"
        Margin="0,0,0,-1"
        Padding="0,6"
        VerticalAlignment="Bottom"
        Background="{DynamicResource PanelSoftBrush}"
        BorderBrush="{DynamicResource LineBrush}"
        BorderThickness="1,1,1,0"
        CornerRadius="9,9,0,0"
        Cursor="Arrow"
        SnapsToDevicePixels="True"
        UseLayoutRounding="True" />
```

Selected tab:

- 본체 배경을 content와 같은 Surface로 바꾼다.
- 하단 line을 `Height=1`, `Panel.ZIndex=100`, `IsHitTestVisible=False` seam으로 덮는다.
- seam 양 끝은 `4px` 투명 gradient로 fade한다.
- 탭 하단 양쪽은 `10px` 확장된 feet path로 채워 content와 이어진 모양을 만든다.
- 탭이 scroll 경계에서 잘렸으면 잘린 쪽 seam gradient를 투명하게 시작하지 않는다.
- `CanContentScroll=False`로 픽셀 단위 스크롤을 유지한다.
- `ScrollViewer.Background="{x:Null}"`로 tab bar 배경을 가리지 않는다.
- overflow 때만 좌/우 `22×22` navigation button과 edge fade를 표시한다.
- selected tab은 선택·재정렬·scroll·theme 변경 후 항상 viewport 안으로 보정한다.
- drag 중 feet와 seam을 별도로 숨기거나 이동하고, 종료 시 복원한다.

devez 채팅방 탭 변형(같은 패턴의 밀도 차이):

- tab bar 높이 `45`, 위치 표시 옵션이 켜지면 `54`.
- 첫 탭 앞 받침 여유 `11`은 동일. 선택 탭의 하단 받침(feet) 곡선은 약 `10px`.
- 선택 탭은 `ZIndex=10`으로 인접 탭 위에 올린다. hover 시각화는 탭 버튼이 아니라 부모 border가 담당한다.

`ListBoxItem`, `TabItem`, `TreeViewItem`의 시스템 selected 배경은 `Background="Transparent"`만으로 제거되지 않는다. 커스텀 selection ring만 쓸 때는 기본 `ControlTemplate`의 selected trigger를 제거한 미니멀 template을 사용한다.

```xml
<Setter Property="Template">
    <Setter.Value>
        <ControlTemplate TargetType="ListBoxItem">
            <ContentPresenter />
        </ControlTemplate>
    </Setter.Value>
</Setter>
```

### 8.14 ScrollBar와 Splitter

Scrollbar:

- 기본 폭 `8`
- track transparent
- thumb 배경 Border soft
- thumb 라운드 `3`
- thumb margin `1,2`
- hover 시 Text secondary 또는 strong border 계열

Splitter:

- splitter 열/행 폭 `4`, 기본 transparent
- 전역 스타일에 `Margin="-1,0,-1,0"`(가로 변형은 `0,-1,0,-1`)을 줘 실질 hit 영역을 `6`으로 넓히고 인접 패널의 `1px` 경계 보더 위까지 덮는다. `Panel.ZIndex=1`로 패널 위에 올린다.
- 세로 hover handle `2×28`, 가로 hover handle `28×2`
- handle 라운드 `1`
- hover/drag 색 Accent, opacity `0→1` 즉시 전환(fade animation 없음)
- 가로/세로 커서는 `SizeWE` / `SizeNS`

경계 세퍼레이터 배치(1px 선의 소유권):

- splitter 자체는 선을 그리지 않는다. 채널 양옆 `1px` 경계선은 **사이드 패널이 자기 쪽 보더로 그린다.**
- 좌측 사이드바는 오른쪽 보더 `BorderThickness="0,0,1,0"`, 우측 보조 패널은 왼쪽 보더 `"1,0,0,0"`. 중앙 작업영역은 좌우 보더를 그리지 않는다.
- 즉 구조는 `[사이드 패널+자기 보더 1] [투명 splitter 4] [중앙]`이며, 선을 splitter 열이나 중앙 콘텐츠에 중복으로 넣지 않는다.
- 패널이 접혀 폭 `0`이 되면 보더도 함께 사라지므로 별도 처리 없이 채널이 닫힌다.

### 8.15 추가 form primitive

공통 스타일 조사에서 존재하지만 개별 section에서 빠지기 쉬운 컨트롤이다.

#### PasswordBox

- 외곽 container geometry는 같은 화면의 `TextBox`와 동일하게 쓴다.
- 내부 `PasswordBox`는 transparent, border `0`, padding `0`, `Fs14`, vertical center.
- `TextFormattingMode=Ideal`, `TextRenderingMode=ClearType`를 유지한다.
- 보기/숨기기 버튼은 오른쪽 `28×28`, 아이콘 `14~16`, radius `6`.
- 비밀번호를 일반 `TextBox`에 복사해 계속 유지하지 않는다. reveal 중에만 최소 범위로 동기화한다.
- validation, disabled, focus border도 일반 입력과 같은 의미를 쓴다.

#### Selector chip과 Radio

| 컨트롤 | geometry | 상태 |
|---|---|---|
| Selector chip | padding `9,4`, radius `7`, border `1`, `Fs12` | hover Surface soft + Accent border |
| Radio dot | outer `14`, inner `7`, stroke `1.5` | selected Accent dot |
| Segmented radio | padding `10,6`, outer radius `6` | selected Accent soft + Accent border |

- selector chip은 작은 popup trigger에만 쓴다. form의 핵심 선택은 ComboBox 또는 radio group을 쓴다.
- segmented group은 인접 border를 중복해서 `2px`로 만들지 않는다. 가운데 항목 radius는 `0`, 양 끝만 `6`.
- 선택 상태는 색만 바꾸지 말고 dot, weight, border 중 하나를 함께 바꾼다.

#### Slider와 Progress

| 컨트롤 | Track | Thumb/fill | radius |
|---|---:|---:|---:|
| Settings slider | 높이 `4` | thumb `16×16` | track `2`, thumb `8` |
| Compact usage progress | `56×6` | 높이 `6` | `3` |
| General progress | 높이 `8` | 높이 `8` | `4` |

- track은 `LineBrush`, fill은 의미에 따라 Primary/Success/Warning/Danger다.
- drag 중 값 label이 필요하면 thumb 주변 popup보다 고정된 오른쪽 meta 열을 우선한다.
- percentage가 갱신돼도 row width가 흔들리지 않도록 숫자 영역에 고정 폭을 둔다.
- indeterminate progress는 화면에 보일 때만 animation clock을 실행한다.

### 8.16 내부 패널 헤더 바

셸 header(§5.5~5.6)와 별개로, 사이드바·보조 패널 내부의 섹션 헤더 바 규격이다.

- 높이 `35~37`, 배경 `PanelBrush`, 하단 `LineBrush 1`.
- 라벨은 `Fs12~13` SemiBold, `TextBrush`.
- 오른쪽 액션은 `IconButton` `24×24`, 아이콘 `13`.
- 헤더 바 안에 입력·콤보를 상시 배치하지 않는다. 검색은 §8.3 검색 입력의 슬라이드 행으로 연다.

## 9. SVG와 아이콘

### 9.1 표준

- 기본 아이콘 세트: Lucide
- 원본 viewBox: `0 0 24 24`
- WPF 저장: `Resources/Icons.xaml`의 `PathGeometry`
- 기본 렌더: `16×16`
- 기본 stroke: `1.25`
- `Stretch="Uniform"`
- `StrokeStartLineCap="Round"`
- `StrokeEndLineCap="Round"`
- `StrokeLineJoin="Round"`
- `Fill="Transparent"`
- 버튼 내부 stroke는 버튼 `Foreground`에 바인딩한다.

```xml
<Style x:Key="LucideIcon" TargetType="Path">
    <Setter Property="Stretch" Value="Uniform" />
    <Setter Property="StrokeThickness" Value="1.25" />
    <Setter Property="StrokeStartLineCap" Value="Round" />
    <Setter Property="StrokeEndLineCap" Value="Round" />
    <Setter Property="StrokeLineJoin" Value="Round" />
    <Setter Property="Fill" Value="Transparent" />
    <Setter Property="SnapsToDevicePixels" Value="True" />
    <Setter Property="Width" Value="16" />
    <Setter Property="Height" Value="16" />
    <Setter Property="Stroke" Value="{DynamicResource TextBrush}" />
</Style>

<Style x:Key="LucideButtonIcon"
       TargetType="Path"
       BasedOn="{StaticResource LucideIcon}">
    <Setter Property="Stroke"
            Value="{Binding Foreground,
                            RelativeSource={RelativeSource AncestorType={x:Type Button}}}" />
</Style>
```

### 9.2 24 정규화 마커

`Stretch=Uniform`은 실제 geometry bounding box를 기준으로 확대한다. 아이콘마다 콘텐츠 경계가 다르면 같은 `16×16`이어도 크기가 다르게 보인다.

일반 Lucide geometry에는 보이지 않는 다음 marker를 넣는다.

```text
M2 2 M22 22
```

이 marker가 bounding box를 Lucide의 실제 콘텐츠 영역 `2~22`로 통일한다.

주의:

- path가 대문자 `M`으로 시작하면 marker를 앞에 둘 수 있다.
- path가 소문자 `m` 상대 이동으로 시작하면 marker를 뒤에 둔다.
- 소문자 `m`을 임의로 대문자 `M`으로 바꾸지 않는다.
- `M0 0 M24 24`는 Lucide 기본 2px padding까지 포함해 아이콘이 작아 보이므로 쓰지 않는다.

### 9.3 채움 아이콘

- 로고·상태 badge처럼 fill 기반이면 stroke Path와 분리한다.
- `FilledIcon`: `16×16`, `Stroke=Transparent`, `StrokeThickness=0`
- 버튼 안에서는 fill을 버튼 `Foreground`에 바인딩한다.
- 한 Path 안에서 stroke 요소와 fill 요소를 섞지 않는다.

### 9.4 비표준 좌표계

24 외 좌표계를 유지해야 하면 획을 콘텐츠 폭에 맞춰 환산한다.

```text
StrokeThickness ≈ 1.25 × (콘텐츠 폭 / 20)
```

예: 256 좌표계면 대략 `14~16`.

### 9.5 아이콘 QA

1. geometry의 모든 path/rect/circle이 보존됐는지 확인한다.
2. 같은 버튼 크기에서 기존 Lucide 아이콘과 나란히 비교한다.
3. round cap/join을 확인한다.
4. relative `m` 시작 path가 marker 때문에 이동하지 않았는지 확인한다.
5. light, soft, dark에서 `Foreground` cascade를 확인한다.
6. 100%, 125%, 150% DPI에서 중앙 정렬과 획 두께를 확인한다.

## 10. 다이얼로그

세 프로젝트에서 가장 강하게 일치하는 패턴이다.

| 항목 | 값 |
|---|---|
| outer margin | 20, dark shadow 여유가 필요하면 22 |
| shadow radius | 13 |
| content radius | 14 |
| outer border | 1 |
| header height | 48 |
| title left margin | 20 |
| body margin | `20,16,20,8` |
| footer padding/margin | `20,14,20,14` |
| footer background | Surface soft |
| footer top border | 1 |
| action gap | 8 |
| action height | 38 |
| action min width | 80 |
| close button | `40×32` |
| close icon | 11 |

구조:

```text
Transparent Window
└─ Grid Margin=20
   ├─ Shadow Border Radius=13
   └─ Content Border Radius=14, Border=1, ClipToBounds=True
      ├─ Header 48
      ├─ Body *
      └─ Footer Auto
```

규칙:

- header 전체를 drag 영역으로 쓴다.
- 제목 왼쪽, close 오른쪽 배치를 유지한다.
- footer 액션 순서는 왼쪽부터 `Cancel`, 선택적 middle, `Primary`다.
- primary 액션은 항상 오른쪽 끝이다.
- danger 확인이어도 footer 구조는 바꾸지 않는다. 목표 규칙은 primary 색만 Danger로 바꾸는 것이며, 현재 `devez-code` 구현 차이는 10.1에 명시한다.
- 짧은 메시지를 위해 고정 높이를 과도하게 잡지 않는다. 너비는 약 `460~510` 범위다.

### 10.1 제품 MessageBox: `ConfirmDialog`

WPF 기본 `System.Windows.MessageBox`는 OS가 그리는 별도 UI라 Devez 테마, 폰트, 라운드, 버튼 규격을 보장할 수 없다. 제품 화면에서 말하는 “메시지박스”는 공용 borderless modal window인 `ConfirmDialog`를 뜻한다.

#### 사용 경계

| 상황 | 사용 |
|---|---|
| 단순 안내·오류, 확인 버튼 하나 | `ConfirmDialog.Alert(...)` |
| 확인/취소가 필요한 결정 | `ConfirmDialog.Show(...)` |
| 저장/저장 안 함/취소 같은 3방향 결정 | `ConfirmDialog.ShowThreeWay(...)` |
| 안내 + footer 왼쪽 보조 링크 | `ConfirmDialog.AlertWithLink(...)` |
| 업데이트 노트 + 같은 창 안 다운로드 진행률 | `ConfirmDialog.ShowUpdate(...)` |
| 앱 초기화 전 치명적 오류, 테마 resource나 custom window 생성이 불가능한 최후 경로 | native `MessageBox.Show(...)` 허용 |

규칙:

- 앱이 정상 로드된 뒤에는 native `MessageBox.Show`를 사용하지 않는다.
- native MessageBox를 XAML style로 꾸미려 하지 않는다. 테마가 필요하면 `ConfirmDialog`로 옮긴다.
- 예외 처리기에서 `ConfirmDialog` 생성 자체가 다시 실패할 위험이 있거나 `Application.Current`가 준비되지 않은 경우만 native MessageBox를 fallback으로 허용한다.
- 현재 `devez-code/Views/WakeSchedulerWindow.xaml.cs`의 native MessageBox 2건은 정상 로드 후 사용되는 **알려진 이탈**이다. 새 코드가 따라 하지 않는다.
- owner가 살아 있으면 반드시 지정하고 `ShowDialog()`로 modal 수명을 보장한다.

#### 기본 외형

`devez-code/Views/ConfirmDialog.xaml`을 정본으로 한다.

| 항목 | 값 |
|---|---|
| 기본 window | `490×360` |
| window | `WindowStyle=None`, `AllowsTransparency=True`, `ResizeMode=NoResize` |
| taskbar | `ShowInTaskbar=False` |
| 시작 위치 | `CenterOwner`, 로드 후 `WindowCenter.CenterOverOwner` 재보정 |
| 바깥 shadow 여백 | `20` |
| shadow | depth `0`, blur `40`, theme `ShadowColor/ShadowOpacity` |
| content | `BgBrush`, radius `14`, `LineBrush` border `1` |
| header | 높이 `48`, 아래 border `1` |
| 제목 | 왼쪽 `20`, `Fs14`, SemiBold, `TextBrush` |
| 닫기 버튼 | `40×32`, 오른쪽 `8`, `WinCloseBtn`, X `Fs11` |
| body | 좌우 `28`, 상하 `16`, 세로 scroll 허용 |
| 메시지 | `Fs13`, `TextBrush`, wrap, line height `22` |
| footer | `PanelSoftBrush`, 위 border `1`, margin `20,14,20,14` |
| 액션 | 높이 `38`, 최소 폭 `80`, 좌우 padding `16`, 간격 `8` |
| input | 높이 `40`, radius `8`, border `1.5`, 좌우 padding `12` |

기본 skeleton:

```xml
<Window Width="490"
        Height="360"
        WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize"
        WindowStyle="None"
        AllowsTransparency="True"
        Background="Transparent"
        ShowInTaskbar="False"
        FontFamily="{StaticResource PretendardFont}">
    <Grid Margin="20">
        <Border Background="{DynamicResource BgBrush}"
                CornerRadius="14">
            <Border.Effect>
                <DropShadowEffect ShadowDepth="0"
                                  BlurRadius="40"
                                  Color="{DynamicResource ShadowColor}"
                                  Opacity="{DynamicResource ShadowOpacity}" />
            </Border.Effect>
        </Border>

        <Border Background="{DynamicResource BgBrush}"
                BorderBrush="{DynamicResource LineBrush}"
                BorderThickness="1"
                CornerRadius="14"
                ClipToBounds="True">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="48" />
                    <RowDefinition Height="*" />
                    <RowDefinition Height="Auto" />
                </Grid.RowDefinitions>
                <!-- Header / Scrollable message body / Footer actions -->
            </Grid>
        </Border>
    </Grid>
</Window>
```

정확한 header, body, footer template은 일부를 다시 만들지 말고 정본 `Views/ConfirmDialog.xaml`을 복사한 뒤 namespace와 resource 경로만 바꾼다.

#### 메시지 길이와 window 크기

일반 `Show`/`Alert`/`ShowThreeWay`:

| 본문 줄 수 (`\n` 기준) | 높이 |
|---:|---:|
| 1~2 | `260` |
| 3~4 | `325` |
| 5 이상 | `360` |

- 기본 폭은 `490`이다.
- 가장 긴 줄의 `FormattedText.Width`가 본문 영역을 넘으면 창 폭을 늘린다.
- 계산식은 `longest line + body horizontal 56 + shadow horizontal 40 + safety 8`이다.
- 일반창 최대 확장 폭은 `640`이다. 폭을 무한히 늘리지 않는다.
- `FormattedText`에는 현재 `PretendardFont`, `Fs13`, `VisualTreeHelper.GetDpi(...).PixelsPerDip`를 쓴다.
- 메시지는 `TextWrapping=Wrap`, `LineHeight=22`를 유지한다.
- body는 `ScrollViewer`로 감싸 극단적으로 긴 메시지가 footer를 화면 밖으로 밀지 않게 한다.

업데이트 전용 `ShowUpdate`:

- 폭은 `560` 고정이다.
- 높이는 내용에 맞추되 `MaxHeight=430`이다.
- 긴 업데이트 노트는 body만 scroll한다.
- 취소 문구는 `나중에`다.
- primary를 누르면 창을 닫지 않고 본문 스크롤과 분리된 고정 영역에 진행률을 표시한다.

#### 액션 구성

| API | footer 구성 | 반환 |
|---|---|---|
| `Alert` | Primary 하나 | 없음 |
| `Show` | Cancel, Primary | `bool` |
| `ShowThreeWay` | Cancel, Secondary, Primary | `ConfirmChoice` |
| `ShowThreeWay(hideCancel: true)` | Secondary, Primary | `ConfirmChoice` |
| `AlertWithLink` | 왼쪽 링크, 오른쪽 Primary | 링크 클릭 여부 `bool` |
| `ShowUpdate` | `나중에`, `업데이트`; 진행 중 모두 숨김 | `UpdateOutcome` |

- Primary는 항상 오른쪽 끝이다.
- Secondary와 Cancel은 `SecondaryButton`, Primary는 `PrimaryButton`을 사용한다.
- Alert는 Cancel을 숨기되 footer 자체를 제거하지 않는다.
- 3버튼은 Cancel, Secondary, Primary 순서를 바꾸지 않는다.
- 버튼 문구가 길면 최소 폭 `80`에서 자연 확장하고 `TextTrimming=None`을 유지한다.
- destructive action의 **목표 스타일**은 `danger=true`일 때 Primary만 `DangerBrush`, 텍스트 White다. header/body/footer 구조는 그대로 둔다.

#### 확인문자 입력형 destructive dialog

중대한 삭제처럼 실수 비용이 큰 동작은 `confirmText`를 사용한다.

- body 아래 `14` 간격으로 hint와 input을 표시한다.
- hint: `계속하려면 '{confirmText}'을(를) 입력하세요.`
- input은 열릴 때 focus한다.
- Primary는 처음 disabled다.
- 공백 보정이나 대소문자 무시 없이 입력값이 `confirmText`와 정확히 같을 때만 enabled다.
- 일치 전 Enter는 primary를 실행하지 않는다.
- 일반 삭제에 매번 confirmText를 요구하지 않는다. 복구 불가, 대량 삭제, 잠금 해제 같은 고위험 동작에만 쓴다.

#### 키보드, 닫기, focus

- 일반창은 로드 후 Primary에 focus한다.
- Enter는 Primary다.
- ESC, header X, Cancel은 모두 취소 결과다.
- 3버튼 중 Secondary는 명시적 클릭으로 선택한다. Enter가 모호하게 Secondary를 실행하면 안 된다.
- `PreviewKeyDown`에서도 Enter/ESC를 처리해 자식 `TextBox`가 키를 먼저 소비해도 계약을 유지한다.
- `confirmText`가 일치하지 않으면 Enter를 무시한다.
- 업데이트 다운로드 중에는 Enter, ESC, X, footer action을 모두 차단한다.
- 닫힌 뒤 입력 focus는 owner로 자연 복귀해야 한다.
- Topmost 플로팅 UI 위에서 호출할 때만 `topMost=true`를 쓴다. 일반 메시지박스에 상시 Topmost를 주지 않는다.

#### 업데이트 진행 상태

- 업데이트를 시작하면 footer button group과 header X를 숨긴다.
- 확인문자 영역이 있으면 숨긴다.
- body `ScrollViewer` 아래의 고정 영역에서 `ProgressArea`를 표시해 긴 노트가 스크롤되어도 항상 보이게 한다.
- label은 왼쪽 `다운로드 중…`, percentage는 오른쪽이다.
- track/fill은 높이 `6`, radius `3`이다.
- track은 `LineBrush`, fill은 `PrimaryBrush`다.
- progress는 `0~1`로 clamp하고 percentage와 fill width를 함께 갱신한다.
- 다운로드 실패는 창을 닫고 `UpdateOutcome.Failed`를 반환해 호출자가 수동 설치 안내를 이어간다.

#### `devez-code` 현재 구현과 목표 규칙 구분

현재 `ConfirmDialog` public API에는 `iconKey`, `danger`, `wideLayout` 매개변수가 있지만 v1.8 기준 XAML/code에서 실제 시각 요소에 연결되지 않는다.

- `autoWidth`: 실제 연결된 옵션이다. `SizeToContent=WidthAndHeight`, `MinWidth=360`, `MaxWidth=660`을 적용한다.
- `iconKey`: body에 아이콘 column이나 `Path`가 없다. 동일 외형 재현 시 아이콘을 임의로 추가하지 않는다.
- `danger`: 현재 Primary는 계속 `PrimaryButton`이다. 문서의 Danger primary는 디자인 목표이며 현재 구현 gap이다.
- `wideLayout`: 현재 layout 분기를 만들지 않는다.
- 새 프로젝트에서 이 매개변수를 구현하려면 `ConfirmDialog.xaml`, API 동작, 이 문서를 한 번에 갱신한다.
- “매개변수가 있으니 이미 표시된다”고 문서·테스트에서 주장하지 않는다.

#### 호출 예제

```csharp
ConfirmDialog.Alert(
    "저장 실패",
    "파일을 저장하지 못했습니다.\n권한과 경로를 확인해 주세요.");

if (!ConfirmDialog.Show(
        "프로젝트 제거",
        "이 프로젝트를 목록에서 제거할까요?",
        okLabel: "제거",
        danger: true))
{
    return;
}

var choice = ConfirmDialog.ShowThreeWay(
    "저장되지 않은 변경사항",
    "닫기 전에 변경사항을 저장할까요?",
    primaryLabel: "저장",
    secondaryLabel: "저장 안 함");
```

#### 문구 규칙

- 제목은 `삭제`, `오류`처럼 너무 넓게 쓰지 말고 `프로젝트 제거`, `파일 저장 실패`처럼 대상을 포함한다.
- 본문 첫 문장은 발생한 일, 다음 문장은 사용자가 할 수 있는 해결 행동이다.
- raw exception, CLI stderr, stack trace를 그대로 보여주지 않는다.
- destructive 본문에는 대상 이름과 복구 가능 여부를 쓴다.
- Primary label은 `확인`보다 `삭제`, `제거`, `저장`, `업데이트`처럼 실제 동사를 우선한다.
- Cancel label은 기본 `취소`, 업데이트 미루기는 `나중에`다.
- 성공처럼 사용자가 반드시 확인할 필요 없는 결과는 modal 대신 in-app toast를 쓴다.

### 10.2 Borderless 창의 실제 라운드 clip

`WindowStyle=None + AllowsTransparency=True`에서 `Border CornerRadius=14`와 `ClipToBounds=True`만으로는 자식을 둥글게 자르지 못한다. `ClipToBounds`는 사각 bounding rect만 자르므로 자식 배경의 직각 모서리가 밖으로 보일 수 있다.

content root에 `RectangleGeometry` clip을 적용한다.

```csharp
private void ApplyRoundedClip()
{
    double width = View.ActualWidth;
    double height = View.ActualHeight;
    if (width <= 0 || height <= 0) return;

    // outer radius 14 - border 1 = inner clip radius 13
    View.Clip = new RectangleGeometry(
        new Rect(0, 0, width, height), 13, 13);
}
```

- `Loaded`와 `SizeChanged`에서 모두 호출한다.
- clip radius는 `CornerRadius - BorderThickness`다.
- 리사이즈 가능한 borderless 창은 `SizeChanged` 갱신이 필수다.

### 10.3 입력 검증

입력 다이얼로그에서 검증이 실패해도 alert dialog를 하나 더 띄우지 않는다.

1. 창을 좌우로 흔든다.
2. 잘못된 입력 frame 보더를 Danger로 바꾼다.
3. 입력 아래 inline hint를 보여준다.
4. 해당 입력으로 focus를 돌린다.
5. 사용자가 다시 입력하면 보더와 hint를 원복한다.

공통 shake:

- 전체 시간 `500ms`
- X offset `{0, -8, 8, -6, 6, -3, 3, 0}`
- Window `RenderTransform`은 `TranslateTransform`
- 색은 `DangerBrush`

### 10.4 Popup 종류와 선택

`Popup`이라는 이름으로 서로 다른 수명과 focus 규칙을 섞지 않는다.

| 종류 | 구현 | 용도 | 닫힘 |
|---|---|---|---|
| modal dialog | `Window.ShowDialog()` | 확인, 입력, 삭제, 중요 선택 | 액션·X·ESC |
| settings window | borderless `Window` + content control | 여러 범주의 지속 설정 | 저장·취소·X·ESC |
| anchored popup | WPF `Popup` | ComboBox 목록, 작은 picker, 짧은 보조 메뉴 | 바깥 클릭·선택·ESC |
| desktop notification | `Window`, `Topmost`, no taskbar | 앱 밖에서도 보여야 하는 알림 | 자동 시간·본문·X |
| in-app toast | root overlay/adornment | 저장 성공, 짧은 오류 | 자동 시간 |
| tooltip | `ToolTip` | 1~2문장 보조 설명 | pointer/focus 이탈 |

선택 규칙:

- 사용자가 반드시 결정을 내려야 하면 modal dialog다. desktop notification이나 toast로 대신하지 않는다.
- 입력이 2개를 넘거나 검증·저장이 있으면 anchored popup이 아니라 dialog다.
- anchor와 공간적 관계가 중요하고 즉시 선택만 하면 WPF `Popup`이다.
- 다른 앱을 보고 있어도 알려야 할 때만 desktop notification window를 쓴다.
- 이미 현재 화면 안에서 수행한 동작의 결과는 in-app toast로 충분하다.

### 10.5 Anchored popup

ComboBox, icon picker의 작은 panel, filter menu에 적용한다.

| 항목 | 값 |
|---|---|
| placement | 기본 `Bottom`, 화면 경계에서 flip/clamp |
| anchor gap | `2~4` |
| minimum width | anchor의 `ActualWidth` |
| maximum height | `220~320`, 목록 성격에 따라 선택 |
| background | `PanelBrush` |
| border | `LineBrush`, `1` |
| radius | `8~10` |
| outer shadow margin | `10~14` |
| item padding | `10,8` |
| item radius | `6` |
| item gap | `1~2` |
| hover | `PanelSoftBrush` |
| selected | `PrimarySoftBrush`, 필요하면 Primary foreground |

동작:

- 단일 선택 popup은 `StaysOpen=False`.
- 열 때 현재 선택 항목으로 scroll하고 keyboard focus를 목록에 둔다.
- `Up/Down`, `Enter`, `Escape`를 제공한다.
- 선택 또는 ESC로 닫힌 뒤 focus를 anchor control로 돌린다.
- popup 안에서 `TextBox` 입력이 있으면 바깥 클릭 닫힘 전에 IME 조합 완료를 보장한다.
- 화면 좌표는 `PlacementTarget`만 믿지 말고 다중 모니터 working area에 clamp한다.
- popup root를 `AllowsTransparency=True`로 쓸 때 shadow가 잘리지 않도록 외부 margin을 확보한다.
- 중요한 확인, 삭제, 2단계 저장을 popup 안에 넣지 않는다.

### 10.6 Desktop notification popup

`devez`와 `devez-code`의 공통 알림 geometry를 기준으로 한다.

```text
Transparent top-level Window, Width=340, SizeToContent=Height
└─ outer margin 5,5,5,7
   └─ surface radius 12, border 1
      ├─ header
      ├─ divider 1
      ├─ body
      └─ optional divider + actions
```

| 항목 | 값 |
|---|---:|
| width | `340` |
| height | `SizeToContent=Height` |
| outer margin | `5,5,5,7` |
| surface radius | `12` |
| surface border | `1` |
| shadow | blur `22`, depth `6`, `PopupShadowOpacity` |
| header margin | `16,12,12,12` |
| close hit area | `22×22` |
| close glyph | `9×9`, stroke `1.5` |
| close radius | `4` |
| body margin | `16,14~16,16,16` |
| title | `Fs16`, SemiBold/Bold |
| description | `Fs12~13`, muted |
| footer margin | `16,12,16,12` |
| footer action | 높이 `34`, gap `8` |
| screen edge margin | `6` DIP |
| stack gap | `8` DIP |
| fade in | `200ms` |
| fade out | `180ms`, `CubicEase/EaseIn` |

`stack gap 8`은 표준값(`devez-code` 구현)이다. `devez` 기존 구현은 offset `7` 기반(시각 간격 약 `5`)이라 새 코드에서 복사하지 않는다. 알림에서 파생된 고정 팝업(예: devez 타이머 카드, 폭 `280`)도 surface radius `12` / close `22×22` / X glyph `9×9` 규격을 그대로 재사용한다.

표시와 stack:

- `WindowStyle=None`, `AllowsTransparency=True`, `ResizeMode=NoResize`, `ShowInTaskbar=False`, `ShowActivated=False`, `Topmost=True`.
- 대상 모니터의 전체 영역이 아니라 working area를 사용한다.
- 위치는 좌상·우상·좌하·우하 설정을 지원하고 최신 알림을 corner에 가장 가깝게 둔다.
- 다중 모니터와 PerMonitorV2에서 정확히 맞추려면 대상 모니터로 먼저 이동해 DPI를 안정화한 뒤 `GetWindowRect`와 `SetWindowPos`의 물리 pixel로 최종 정렬한다.
- `SetWindowPos`는 `SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW`를 사용해 알림 등장 때문에 현재 입력 focus를 빼앗지 않는다.
- 다음 알림의 offset은 이전 popup의 실제 물리 pixel 높이 + `8 DIP × DPI`다.
- 자동 닫힘 `0`은 영구 표시로 해석하고 timer를 만들지 않는다.
- 닫힘 animation 연타를 막는 `_closing` guard를 둔다.

내용:

- header에는 앱/출처와 닫기만 둔다.
- 본문 전체가 단일 행동이면 surface click으로 실행하고 닫는다.
- 행동이 2개 이상이면 footer를 분리하고 primary는 오른쪽 끝에 둔다.
- 본문은 제목 1개와 설명 1개를 기본으로 한다. 긴 전문 대신 대상 화면을 여는 행동을 제공한다.
- close button click이 부모 surface click으로 bubbling되지 않게 처리한다.
- 기본 커서는 `Arrow`; 실제 URL을 직접 여는 명시적 링크만 `Hand`.
- 알림을 클릭했을 때 앱을 foreground로 가져오되 먼저 대상 화면/항목 선택 상태를 복원한다.

stack animation 변형:

- 표준은 위치를 먼저 확정한 뒤 `200ms` fade-only다.
- 업무 알림이 위로 쌓이는 움직임을 보여야 하면 등장 시 Y `30→0`, `240ms`, `CubicEase/EaseOut`을 추가할 수 있다.
- 기존 popup 재정렬은 top position `220ms`, `CubicEase/EaseOut`.
- 종료는 두 변형 모두 `180ms` fade를 유지한다.

### 10.7 공통 설정창

세 프로젝트에 공통으로 존재하는 `일반`, `알림`, `테마`, `저장/취소` 흐름을 하나의 settings shell로 고정한다. 제품별 category 내용만 추가한다.

#### Window chrome

| 항목 | 표준 |
|---|---:|
| window size | `970×830` |
| startup | `CenterOwner`; owner가 없거나 main overlay 연동이면 `Manual` |
| resize | `NoResize` |
| taskbar | 숨김 |
| outer window | `WindowStyle=None`, `AllowsTransparency=True`, transparent |
| outer shadow margin | `20`, dark에서 필요하면 `22` |
| shadow radius | `13` |
| content radius | `14` |
| content border | `LineBrush 1` |
| open motion | opacity `0→1`, `220ms`, `CubicEase/EaseOut` |
| close key | `Escape` |

세 프로젝트 값이 `1010×775`, `970×830`, `900×710`으로 모두 달라 전체 우선순위에 따라 `devez-code`의 `970×830`을 표준으로 채택한다. category rail 폭도 `devez` 기존 구현은 `170`이지만 표준은 `devez-code`의 `205`다. 작은 화면에서는 고정 크기를 억지로 유지하지 말고 working area 안에서 최대 `calc(100%-32)`로 줄인 뒤 content scroll을 사용한다.

설정창은 shadow layer와 content layer를 분리한다.

```xml
<Grid x:Name="RootLayer"
      Margin="{DynamicResource WindowShadowOuterMargin}">
    <Border Margin="{DynamicResource WindowShadowInnerMargin}"
            CornerRadius="13"
            Background="{DynamicResource BgBrush}">
        <Border.Effect>
            <DropShadowEffect ShadowDepth="0"
                              BlurRadius="{DynamicResource WindowShadowBlurRadius}"
                              Color="{DynamicResource ShadowColor}"
                              Opacity="{DynamicResource ShadowOpacity}" />
        </Border.Effect>
    </Border>

    <Border CornerRadius="14"
            Background="{DynamicResource BgBrush}"
            BorderBrush="{DynamicResource LineBrush}"
            BorderThickness="1">
        <local:SettingsView x:Name="SettingsView" />
    </Border>
</Grid>
```

`SettingsView`에는 section 10.2의 `RectangleGeometry(13)` clip을 `Loaded + SizeChanged`에 적용한다.

#### Settings layout

```text
┌──────────────────── header 48 ────────────────────┐
│ settings icon · 설정                         ×    │
├─ category 205 ─┬──────── scroll content ──────────┤
│ 일반           │ section title                    │
│ 알림           │ description                      │
│ 테마/글꼴      │ setting rows                     │
│ 제품별 항목    │                                  │
├────────────────┴──────────────────────────────────┤
│                              [취소] [저장] footer │
└───────────────────────────────────────────────────┘
```

| 영역 | 표준 |
|---|---|
| root rows | `48`, `*`, `Auto` |
| header background | `PanelBrush` |
| header bottom border | `LineBrush 1` |
| header title start | `16` |
| header icon | `16×16`, title gap `11` |
| header title | `Fs14`, SemiBold |
| close | `40×32`, right margin `8`, glyph `11` |
| category width | `205` |
| category surface | `PanelSoftBrush`, right border `1` |
| category inner margin | `10,16,10,16` |
| content margin | `28,22,28,20` |
| footer surface | `PanelSoftBrush`, top border `1` |
| footer margin | `20,14,20,14` |
| footer actions | `100×38`, gap `8` |

category:

- 공통 첫 순서는 `일반` → `알림` → `테마/글꼴`이다.
- 그 뒤에 제품별 기능, 연동, 단축키, 업데이트 내역, 라이선스를 배치한다.
- category가 세로 공간을 넘으면 category rail만 독립 scroll한다.
- item은 stretch, `Padding=12,10`, `Margin=0,2`, radius `8`, icon `15×15`, icon-text gap `10`, `Fs13`.
- normal은 transparent/Text, hover는 `PanelBrush`, selected는 `PanelBrush + PrimaryBrush foreground + SemiBold`.
- selected와 hover는 foreground와 weight로도 구분한다.
- category 전환 시 이전 panel은 `Collapsed`, 새 panel만 `Visible`; 겹쳐 놓고 opacity `0`만 주지 않는다.

content:

- 오른쪽은 category별 독립 content를 가진 단일 vertical `ScrollViewer`다.
- section header는 아이콘 `16`, gap `9`, `Fs14 SemiBold`; 설명은 `Fs12 muted`, 아래 `20`.
- 기본 setting row는 `Grid`, `MinHeight=32`, columns `* / Auto`.
- 왼쪽은 label + 선택적 description, 오른쪽은 toggle/combo/input/action이다.
- 왼쪽 content와 오른쪽 control 사이에 최소 `16`을 둔다.
- 두 줄 설명이나 큰 picker가 있으면 row 높이를 늘리지 말고 `Auto`로 확장한다.
- separator는 `LineBrush 1`; 관련된 행은 separator보다 section/card로 묶는다.
- 오류는 footer 왼쪽 또는 해당 field 아래 inline으로 표시한다. 별도 alert를 연쇄적으로 띄우지 않는다.

#### General category

`일반`은 모든 프로젝트에서 첫 category다.

- 앱 시작, 창 복원, 닫기 동작처럼 전역 동작을 먼저 둔다.
- 단일 boolean은 오른쪽 `ToggleSwitch`.
- 선택지가 3개 이상이면 `ComboBox`; 2개이고 의미가 명확하면 segmented/radio card.
- 재시작이 필요한 옵션은 description에 먼저 표시하고 저장 직후 한 번만 안내한다.
- 위험 옵션은 일반 section 하단의 별도 danger card로 분리한다.
- 제품 고유 옵션을 공통 label로 위장하지 않는다. 공통 key가 아니면 제품 category로 이동한다.

#### Theme/Font category

세 프로젝트에 공통인 테마 card 규격:

| 항목 | 값 |
|---|---:|
| theme keys | `minimal`, `soft`, `dark` |
| labels | `심플`, `소프트`, `다크` |
| card | `150×150` |
| card gap | `12` |
| card border | `2` |
| card radius | `10` |
| preview inner margin | `1` |
| preview top radius | `8,8,0,0` |
| label row | `38` |
| radio outer | `14×14`, stroke `1.5` |
| radio dot | `7×7` |
| radio-label gap | `6` |
| selected border/dot | `PrimaryBrush` |

규칙:

- preview 안의 fixed HEX는 실제 테마 견본을 그리는 목적에 한해 허용한다. settings chrome과 label에는 의미 brush를 쓴다.
- card를 누르면 앱 전체에 즉시 live preview하지만 디스크에는 저장하지 않는다.
- `저장`에서 선택 theme와 font scale을 persist하고 original snapshot을 갱신한다.
- `취소`에서 열기 시점의 theme/font/가시성으로 되돌린다.
- theme 변경으로 terminal/WebView 재시작이 필요하면 실제 활성 세션이 있을 때만 저장 확인을 띄운다.
- frozen brush, cached seam, WebView theme payload처럼 `DynamicResource`만으로 갱신되지 않는 소비자를 theme changed event에서 다시 만든다.
- 글꼴 크기 card는 `120×56`, radius `10`, border `2`, gap `10`; `작게/크게` 두 단계가 기본이다.

#### Save, cancel, X, ESC

공통 transaction:

```text
open
  → persisted 값을 original snapshot으로 저장
  → UI 편집
  → theme/font는 preview only
      ├─ 저장 → validate → persist → apply → original 갱신 → close
      ├─ footer 취소 → preview revert → close
      └─ X / ESC → unsaved?
           ├─ yes → 저장 여부 확인 → persist 또는 revert → close
           └─ no  → close
```

- field 입력 중 저장하면 먼저 validation하고 실패 field로 focus를 이동한다.
- 저장 중 `Save`를 disabled/busy 처리해 중복 실행을 막는다.
- footer 취소는 명시적 폐기 행동이므로 추가 확인 없이 원복한다.
- X와 ESC는 실수 닫힘 경로라 미저장 변경이 있으면 한 번 확인한다.
- live preview 값을 비교할 때 UI control 값이 아니라 original snapshot과 편집 model을 비교한다.
- settings window의 X가 content의 revert/confirm 경로를 우회해 바로 `Window.Close()`하지 않게 한다.
- owner가 WebView2를 포함하면 settings를 띄우기 전에 section 5.7의 snapshot + `Collapsed` 흐름을 적용하고 닫힐 때 복원한다.

### 10.8 업데이트 버튼·노트 팝업·진행률

`devez-code` 구현을 표준으로 한다. 흐름은 `버튼 노출 → 노트 팝업 → 같은 팝업 안 진행률 → 재실행`이며 단계마다 팝업을 바꾸지 않는다.

#### 업데이트 버튼

- 위치는 좌측 사이드바 최하단 고정 행. 업데이트가 확인되기 전에는 `Collapsed`.
- geometry: 높이 `34`, 전체 폭, 좌우 margin `12`, 아래 margin `10`, radius `8`.
- 배경 `PrimaryBrush`, hover `PrimaryHoverBrush`, pressed `PrimaryPressedBrush`.
- 내용: `IconDownload` `13×13` White, 아이콘-텍스트 gap `7`, `업데이트 v{버전}` `Fs12` SemiBold White.
- 팝업에서 `나중에`를 누르거나 다운로드가 실패하면 버튼을 다시 노출한다.

#### 노트 팝업

- 표준 다이얼로그 셸(§10)을 재사용한다: content radius `14`, header `48`, footer 액션 `38`, close `40×32`.
- 고정폭 `560`, `SizeToContent=Height`, `MaxHeight=430`. 노트 약 10줄까지 높이가 자동 확장되고 초과분은 본문 `ScrollViewer`가 스크롤한다.
- 릴리스 노트는 현재 버전 이후 누적분을 모두 보여준다.
- 액션은 왼쪽 `나중에`(Secondary), 오른쪽 `업데이트`(Primary). header 아이콘은 `IconDownload`.

#### 진행률

- `업데이트` 클릭 시 창을 닫지 않고 같은 창 안에서 전환한다: footer 버튼·header 닫기·입력 영역을 숨기고 진행률 영역만 표시한다.
- 다운로드 중에는 ESC·X·취소 등 모든 닫기 경로를 차단한다(downloading guard).
- geometry: 본문 아래 `Margin=0,18,0,0`, 좌측 label `다운로드 중…` `Fs12` muted, 우측 percent `Fs12`, label-track gap `6`, track 높이 `6` radius `3` `LineBrush`, fill `PrimaryBrush`.
- percent는 `0~1` clamp 후 `P0`로 표시하고, fill 폭은 track 실측 폭 × 비율로 갱신한다.
- 성공하면 앱이 종료·재실행되므로 호출은 반환되지 않는다. 취소/실패만 결과로 반환하고, 실패 시 팝업을 닫은 뒤 수동 설치 안내를 표시한다.
- 재시작 종료 오버레이에는 `업데이트 후 자동으로 다시 실행됩니다.` 안내를 함께 표시한다.

#### devez 변형

- 별도 `Window` 대신 메인 창 안 오버레이로 띄운다: ZIndex `99`, dim `#88000000`, 폭 `560` 동일, radius `16`, padding `28,24`, 그림자 blur `48`.
- 헤더는 `업데이트 {버전}` `Fs15` Bold + 우상단 `28×28` IconButton 닫기(다운로드 중 숨김). 노트는 `•` 불릿 `Fs13`, LineHeight `22`.
- 액션은 half-width 2버튼(높이 `36`, gap `8`): `나중에`(PanelSoft + Line border 1, radius 8) / `지금 업데이트`(Primary). 진행률 track은 높이 `8` radius `4`.
- 긴급 업데이트는 타이틀바 아래 `DangerBrush` 상단 배너로 별도 고정 노출한다: ZIndex `101`, padding `20,12`, `IconDownload` `18×18` White, X 닫기 후 1분 뒤 재표시.

공통 계약(두 구현 모두 유지): 노트 팝업의 primary 클릭 → 같은 surface 안 진행률 전환, 다운로드 중 모든 닫기 경로 차단·버튼 숨김, cancel 라벨은 `나중에`, 진행 label은 `다운로드 중…` + 우측 %, fill 색은 `PrimaryBrush`.

## 11. 로딩, 빈 상태, 알림

### 11.1 Spinner

| 용도 | 크기 | 획 | Dash | 주기 |
|---|---:|---:|---|---:|
| Full-page loading | `36×36` | 3 | `28 10` | 0.85s |
| Shutdown overlay | `34×34` | 3 | `18 100`, round cap | 0.9s |
| Button loading | `14×14` | 2 | `11 5` | 0.7s |
| Inline busy | `9~14` | `1.4~2` | `12~15 100` | 0.85s |

Full-page loading:

```xml
<Ellipse Width="36"
         Height="36"
         Fill="Transparent"
         Stroke="{DynamicResource PrimaryBrush}"
         StrokeThickness="3"
         StrokeDashArray="28 10"
         RenderTransformOrigin="0.5,0.5">
    <Ellipse.RenderTransform>
        <RotateTransform Angle="{Binding Angle,
            Source={x:Static models:SpinnerSync.Instance}}" />
    </Ellipse.RenderTransform>
</Ellipse>
```

Spinner clock은 화면에 실제로 보이는 spinner가 있을 때만 활성화한다. 숨겨진 spinner 때문에 idle 상태에서 60fps rendering이 지속되면 안 된다.

### 11.2 로딩 표시 조건

- 최초 로딩 + 결과 0개: 중앙 overlay
- 새로 고침 + 기존 결과 있음: 결과 유지, toolbar/button spinner
- 저장 중: 해당 action만 busy 처리
- 전체 앱 종료/필수 작업: 입력 차단 overlay
- 오류: spinner를 멈추고 짧은 원인 + 재시도 action 표시

### 11.3 빈 상태

- 중앙 정렬
- 제목 16~18 SemiBold
- 설명 12~14 secondary
- 아이콘 24~36, secondary 또는 accent soft
- 주요 다음 행동이 있을 때만 primary 버튼 1개

### 11.4 Toast와 알림

- 성공 toast 노출 약 `1.4s`
- 실패 안내는 약 `3s` 또는 사용자가 닫을 때까지
- in-app toast fade-out은 최대 `400ms`
- desktop notification은 fade-in `200ms`, fade-out `180ms`
- 배경 Surface, border 1, radius 8~10
- raw CLI stderr를 그대로 노출하지 않고 사용자 행동 중심 문장으로 바꾼다.

현재 구현 범위: `devez-code`에는 네이티브 WPF in-app toast 오버레이가 없다. WPF 알림 경로는 desktop notification popup(§10.6)뿐이고, markdown 편집기의 toast는 WebView 내부(JS) 구현이다. 위 in-app toast 규격은 새로 만들 때의 기준값이며, 기존 화면에서 찾으려 하지 않는다.

## 12. 상태와 상호작용

### 12.1 상태 우선순위

```text
Disabled > Busy > Pressed > Selected/Checked > Hover > Normal
```

busy와 disabled를 같은 상태로 표현하지 않는다. busy는 진행 표시를 제공하고, disabled는 이유가 필요하면 tooltip을 제공한다.

### 12.2 커서

- Button, icon button, menu item, card, clickable Border: `Arrow`
- 실제 하이퍼링크: `Hand`
- splitter: `SizeWE` / `SizeNS`
- drag handle: 해당 drag cursor
- 새 UI에 습관적으로 `Cursor="Hand"`를 넣지 않는다.

### 12.3 포커스

- 시스템 점선 focus visual은 제거한다.
- 입력 포커스는 Accent border로 보인다.
- 키보드 선택 카드에는 `1.5` Accent 점선, `StrokeDashArray="3 2.5"`, opacity `0.55`를 쓸 수 있다.
- combo/menu가 닫히면 편집기·terminal 중심 흐름에서 원래 작업 영역으로 포커스를 돌려준다.
- 전역 암시적 스타일을 인라인 스타일이 덮지 않도록 `BasedOn`을 유지한다.

ESC 2단계 동작에서 `Keyboard.ClearFocus()`로 focus를 null로 만들지 않는다. 두 번째 ESC가 Window까지 routing되지 않을 수 있다. 논리 focus와 keyboard focus를 Window로 옮긴다.

```csharp
var window = Window.GetWindow(this);
if (window != null)
{
    FocusManager.SetFocusedElement(window, window);
    Keyboard.Focus(window);
}
```

### 12.4 선택과 hover

- hover는 임시 상태, selected는 지속 상태다.
- 둘이 같은 배경이면 selected에 accent 보더, bar, foreground 중 하나를 추가한다.
- 우클릭 context menu는 대상 row/card를 먼저 active selection으로 만든다.
- 확장/축소 아이콘은 상태와 방향이 즉시 일치해야 한다.

## 13. 모션

| 용도 | 시간 |
|---|---:|
| hover/색 전환 | 100~150ms |
| toggle | 150ms |
| chevron 회전 | 180ms |
| panel/tab indicator | 180~220ms |
| tree expand/collapse | 220ms |
| 검색 행 슬라이드 | 220ms, `CubicEase EaseInOut` |
| toast fade-out | 400ms |
| spinner | 0.7~0.9s / 회전 |

규칙:

- `CubicEase EaseOut`을 기본으로 한다.
- 색만 바뀌는 작은 feedback에 큰 scale animation을 쓰지 않는다.
- WebView2, terminal, 대형 DataGrid가 포함된 패널은 즉시 전환을 우선한다.
- animation 완료 후 animation clock을 제거해 이후 직접 크기 조절을 방해하지 않게 한다.
- spinner는 visibility와 함께 시작·정지한다.

### 13.1 소프트웨어 렌더링

- 큰 화면 전환은 live visual tree 전체를 매 frame 움직이기보다 전·후 `RenderTargetBitmap` snapshot을 움직인다.
- 이동 snapshot은 `BitmapScalingMode=NearestNeighbor`, `EdgeMode=Aliased`로 subpixel 재샘플링을 줄인다.
- 목록 항목은 매 전환마다 clear/recreate하지 않고 고정 instance를 재사용해 속성만 갱신한다.
- 소프트웨어 렌더링에서 `BitmapCache`는 full-page bitmap 재합성 때문에 오히려 느릴 수 있다.
- `BitmapCache`는 `RenderOptions.ProcessRenderMode == Default`이고 rendering tier가 2 이상일 때만 사용한다.

### 13.2 WebView2와 HwndHost airspace

WebView2·terminal은 일반 WPF 자식처럼 overlay와 clipping되지 않는다.

| 전환 | 처리 |
|---|---|
| terminal 크기가 바뀌지 않는 overlay | snapshot 후 live host를 `Collapsed` |
| terminal 크기가 바뀌는 panel 전환 | WebView DOM 내부 cover 사용 |
| 전체 창 resize/fullscreen | DOM cover를 stretch한 뒤 최종 폭에서 reveal |
| 흰색 clear 방지 | WebView `DefaultBackgroundColor`를 실제 terminal 배경으로 설정 |

필수 규칙:

- snapshot 경로에서 `Hidden`을 쓰지 않는다. `HwndHost` HWND가 계속 보일 수 있으므로 `Collapsed`를 쓴다.
- active reflow repaint는 최상위 WPF snapshot window를 뚫고 보일 수 있다. 같은 swap chain 안의 DOM cover로 가린다.
- reveal 전에 최종 폭을 확정하고 terminal fit을 1회 수행한다.
- 좌우 terminal을 함께 표시할 때 각각 reveal하지 말고 준비 완료 후 같은 frame에 fade한다.
- suspend 중 상태 이벤트가 live host visibility를 다시 `Visible`로 복원하지 못하게 별도 suspended flag를 확인한다.
- resize가 없는 단순 dialog에서는 복잡한 DOM cover를 쓰지 않는다.

### 13.3 텍스트와 고정 메타 영역

긴 본문 옆 시간·배지·action이 잘리지 않게 max width는 전체 row가 아니라 본문 bubble에만 건다.

```text
잘못됨: DockPanel.MaxWidth = 664
권장:   Bubble.MaxWidth = min(AvailableWidth - ReservedMetaWidth, 664)
```

- DockPanel은 선언 순서대로 먼저 배치된 자식이 공간을 차지한다.
- 본문이 먼저 전체 폭을 먹으면 뒤의 시간 영역은 0px이 될 수 있다.
- 시간, badge, action 너비를 먼저 예약하거나 본문에만 cap을 적용한다.

## 14. 새 프로젝트용 최소 리소스 구조

```text
Resources/
├─ ThemeColors.xaml
├─ Typography.xaml
├─ Icons.xaml
├─ Controls.Buttons.xaml
├─ Controls.Inputs.xaml
├─ Controls.Navigation.xaml
├─ Controls.DataGrid.xaml
├─ Controls.Overlays.xaml
└─ DialogChrome.xaml
```

`App.xaml` 병합 순서:

```xml
<ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="/Resources/ThemeColors.xaml" />
    <ResourceDictionary Source="/Resources/Typography.xaml" />
    <ResourceDictionary Source="/Resources/Icons.xaml" />
    <ResourceDictionary Source="/Resources/Controls.Buttons.xaml" />
    <ResourceDictionary Source="/Resources/Controls.Inputs.xaml" />
    <ResourceDictionary Source="/Resources/Controls.Navigation.xaml" />
    <ResourceDictionary Source="/Resources/Controls.DataGrid.xaml" />
    <ResourceDictionary Source="/Resources/Controls.Overlays.xaml" />
</ResourceDictionary.MergedDictionaries>
```

순서가 중요한 이유:

- control style은 색상·폰트·아이콘 리소스를 참조한다.
- 화면 XAML은 공용 style 이후에 로드돼야 한다.
- view 안 로컬 style은 공용 style을 `BasedOn`으로 확장한다.

## 15. 구현 예시

### 15.1 Form row

```xml
<Grid Margin="0,0,0,16">
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />
        <RowDefinition Height="6" />
        <RowDefinition Height="40" />
    </Grid.RowDefinitions>

    <TextBlock Text="프로젝트 이름"
               FontSize="13"
               FontWeight="Medium"
               Foreground="{DynamicResource TextMutedBrush}" />

    <Border Grid.Row="2"
            Padding="10,0"
            Background="{DynamicResource PanelBrush}"
            BorderBrush="{DynamicResource LineBrush}"
            BorderThickness="1.5"
            CornerRadius="8">
        <TextBox Background="Transparent"
                 BorderThickness="0"
                 FontSize="14"
                 VerticalContentAlignment="Center" />
    </Border>
</Grid>
```

### 15.2 Header action row

```xml
<Grid Height="44">
    <TextBlock Text="작업 목록"
               Margin="20,0,0,0"
               VerticalAlignment="Center"
               FontSize="15"
               FontWeight="SemiBold"
               Foreground="{DynamicResource TextBrush}" />

    <StackPanel Orientation="Horizontal"
                HorizontalAlignment="Right"
                VerticalAlignment="Center"
                Margin="0,0,12,0">
        <Button Width="28" Height="28"
                Style="{StaticResource IconButton}"
                ToolTip="새로 고침">
            <Path Width="16" Height="16"
                  Style="{StaticResource LucideButtonIcon}"
                  Data="{StaticResource IconRefresh}" />
        </Button>
        <Button Height="38"
                MinWidth="80"
                Margin="8,0,0,0"
                Style="{StaticResource PrimaryButton}"
                Content="추가" />
    </StackPanel>
</Grid>
```

### 15.3 Empty/loading/result host

```xml
<Grid>
    <DataGrid x:Name="ResultGrid"
              Style="{StaticResource ListGrid}" />

    <Grid x:Name="EmptyState"
          Visibility="Collapsed"
          Background="{DynamicResource BgBrush}">
        <StackPanel HorizontalAlignment="Center"
                    VerticalAlignment="Center">
            <TextBlock Text="결과가 없습니다"
                       FontSize="16"
                       FontWeight="SemiBold"
                       HorizontalAlignment="Center" />
            <TextBlock Text="필터를 바꾸거나 새 항목을 추가하세요."
                       Margin="0,8,0,0"
                       FontSize="13"
                       Foreground="{DynamicResource TextMutedBrush}" />
        </StackPanel>
    </Grid>

    <!-- IsLoading && ResultCount == 0 일 때만 Visible -->
    <Grid x:Name="InitialLoadingOverlay"
          Visibility="Collapsed"
          Background="{DynamicResource BgBrush}">
        <Ellipse Width="36" Height="36"
                 HorizontalAlignment="Center"
                 VerticalAlignment="Center"
                 Stroke="{DynamicResource PrimaryBrush}"
                 StrokeThickness="3"
                 StrokeDashArray="28 10"
                 Fill="Transparent" />
    </Grid>
</Grid>
```

## 16. 금지 패턴

- 화면마다 새 HEX palette 만들기
- 버튼마다 다른 라운드와 높이 사용
- 기본 버튼에 `Cursor="Hand"` 사용
- 24 좌표계 아이콘을 normalize하지 않고 크기만 억지로 조절
- fill과 stroke 도형을 한 Path에 혼합
- 인라인 style에서 공용 암시적 style을 `BasedOn` 없이 교체
- hover와 selected를 완전히 같은 표현으로 사용
- 기존 결과를 지운 뒤 spinner만 보여주는 refresh
- 숨겨진 spinner animation을 계속 실행
- 다이얼로그마다 header/footer/action 순서를 변경
- 앱이 정상 로드된 제품 화면에서 native `MessageBox.Show` 사용
- 메시지박스마다 별도 Window/XAML을 복제해 크기·키보드·Owner 규칙이 갈라짐
- `ConfirmDialog`의 미사용 `iconKey`, `danger`, `wideLayout`이 이미 렌더링된다고 가정
- `DataGrid` row마다 높이가 달라지는 자동 레이아웃
- raw exception/CLI 메시지를 그대로 제품 UI에 표시
- 테마 전환 시 Color만 바꾸고 brush 소비자를 갱신하지 않음

## 17. 검수 체크리스트

### Visual

- [ ] Minimal, Soft, Dark에서 모든 텍스트와 보더 확인
- [ ] Primary/Secondary/hover/pressed/disabled 상태 확인
- [ ] 버튼 높이 38, 입력 높이 40, compact 높이 29~32 구분
- [ ] 기본 버튼 radius 10, 입력 radius 8, dialog radius 14 확인
- [ ] 아이콘 24 좌표계, 16 렌더, stroke 1.25 확인
- [ ] 원형 버튼이 실제 원형인지 확인
- [ ] dialog shadow radius 13 / content radius 14 구분
- [ ] ConfirmDialog 기본 폭 490, 본문 줄 수별 높이 260/325/360, 장문 최대 폭 640 확인
- [ ] ConfirmDialog body `Fs13` / line height 22 / 좌우 28 / scroll 확인
- [ ] ConfirmDialog footer action 높이 38 / 최소 폭 80 / 간격 8 확인
- [ ] settings header 48 / category 205 / footer action 100×38 확인
- [ ] theme card 150×150 / radius 10 / border 2 확인
- [ ] desktop notification width 340 / radius 12 / close 22×22 확인
- [ ] 100%, 125%, 150% DPI 확인
- [ ] 텍스트 baseline과 아이콘 optical center 확인

### UX

- [ ] keyboard focus 이동과 복귀 확인
- [ ] hover와 selected가 구분되는지 확인
- [ ] 좌측 주 내비게이션 카드는 selected 보더만, 우측 보조 목록 행은 selected 배경만 강조
- [ ] loading 중 기존 결과 유지
- [ ] busy action 중복 실행 차단
- [ ] destructive action 확인 단계 제공
- [ ] Alert/Show/ShowThreeWay/AlertWithLink/ShowUpdate가 용도별 버튼 수와 반환 계약을 지킴
- [ ] ConfirmDialog Enter=Primary, ESC/X/Cancel=취소, 닫힌 뒤 owner focus 복귀
- [ ] confirmText 불일치 시 Primary와 Enter 차단
- [ ] 정상 로드 후 native MessageBox 신규 사용 없음
- [ ] right-click selection 선행
- [ ] popup이 화면 밖으로 나가지 않는지 확인
- [ ] popup이 닫힌 뒤 anchor/owner focus 복귀
- [ ] settings theme live preview 후 저장은 유지, 취소는 원복
- [ ] settings X/ESC가 미저장 확인 경로를 우회하지 않음
- [ ] 업데이트 노트 팝업이 같은 팝업 안에서 진행률로 전환되고 다운로드 중 닫기 차단
- [ ] desktop notification이 입력 focus를 빼앗지 않음
- [ ] mixed-DPI 모니터에서 notification edge 6 / stack gap 8 유지
- [ ] 좁은 창에서 중앙 영역 최소 폭 보존

### XAML

- [ ] `DynamicResource` 사용
- [ ] 인라인 style `BasedOn` 확인
- [ ] XAML XML parse 성공
- [ ] 중복 key 없음
- [ ] 아이콘 relative `m` 경로 확인
- [ ] hidden animation clock 정지
- [ ] border overlay가 hit test를 가로채지 않음

## 18. 원본 소스 위치

### devez-code

- `Styles/AppStyles.xaml`
- `Resources/Icons.xaml`
- `App.xaml.cs`의 `SetTheme`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`의 window chrome, maximize/fullscreen, footer 상태 처리
- `Views/SidebarView.xaml`의 `ProjectCard`: 좌측 selected 보더-only 규칙
- `Views/FileExplorerView.xaml`의 `TreeViewItem`: 우측 selected 배경-only 규칙
- `Views/SettingsWindow.xaml`
- `Views/SettingsWindow.xaml.cs`
- `Views/SettingsDialog.xaml`
- `Views/SettingsDialog.xaml.cs`
- `Views/NotificationPopup.xaml`
- `Views/NotificationPopup.xaml.cs`
- `Views/ConfirmDialog.xaml`: 제품 MessageBox 정본 geometry와 template
- `Views/ConfirmDialog.xaml.cs`: `Alert`, `Show`, `ShowThreeWay`, `AlertWithLink`, `ShowUpdate`, 크기·Owner·키보드 계약
- `Views/WakeSchedulerWindow.xaml.cs`: 정상 로드 후 native MessageBox를 쓰는 알려진 이탈
- `Views/SidebarView.xaml`의 `UpdateButton`: 사이드바 업데이트 버튼
- `Services/UpdateService.cs`
- `Models/SpinnerSync.cs`
- `.knowledge/텍스트렌더링규칙.md`
- `.knowledge/컨트롤추가규칙.md`
- `.knowledge/borderless-창-라운드-코너-클립.md`
- `.knowledge/wpf-contextmenu-setter-value-connectionid-충돌.md`
- `.knowledge/wpf-listboxitem-default-template-selected-배경.md`
- `.knowledge/wpf-run-text-기본-twoway-읽기전용-크래시.md`
- `.knowledge/wpf-팝업-입력검증-흔들기-애니메이션.md`
- `.knowledge/탭바-생성-규칙.md`
- `.knowledge/탭바-SelectedTabSeam-밑줄-그라데이션.md`
- `.knowledge/webview2-airspace-패널리사이즈-깜빡임.md`

### devez

- `Styles/AppStyles.xaml`
- `Resources/Icons.xaml`
- `App.xaml.cs`의 `SetTheme`
- `Views/MainWindow.xaml`
- `Views/MainWindow.xaml.cs`의 rail animation, caption, maximize, tray 처리
- `Views/SettingsWindow.xaml`
- `Views/SettingsWindow.xaml.cs`
- `Views/SettingsDialog.xaml`
- `Views/SettingsDialog.xaml.cs`
- `Views/PopupWindowBase.cs`
- `Views/NotificationPopup.xaml`
- `Views/ConfirmDialog.xaml`
- `svg.md`
- `.knowledge/WPF-소프트웨어렌더-애니메이션.md`
- `.knowledge/wpf-esc-focus-닫힘.md`
- `.knowledge/wpf-채팅버블-시간-잘림.md`

### eGhisDevWPF

- `src/eGhisDev2.Presentation/App.xaml`
- `src/eGhisDev2.Presentation/Services/ThemeService.cs`
- `src/eGhisDev2.Presentation/Views/Styles/DevezTheme.xaml`
- `src/eGhisDev2.Presentation/Views/Styles/DialogChrome.xaml`
- `src/eGhisDev2.Presentation/Views/MainShell.xaml`
- `src/eGhisDev2.Presentation/Views/SettingsWindow.xaml`
- `src/eGhisDev2.Presentation/Views/SettingsWindow.xaml.cs`
- `src/eGhisDev2.Presentation/Views/Pages/SettingsPage.xaml`
- `src/eGhisDev2.Presentation/Views/Pages/SettingsPage.xaml.cs`
- `src/eGhisDev2.Presentation/Views/ConfirmDialog.xaml`
- `src/eGhisDev2.Presentation/Views/Controls/DateBox.xaml`
- `src/eGhisDev2.Presentation/Models/Terminal/SpinnerSync.cs`
- `.knowledge/ui/combobox-display.md`
- `.knowledge/ui/grid-row-border.md`

## 19. 기준 스냅샷

이 문서는 다음 checkout을 기준으로 작성했다.

| 프로젝트 | 브랜치 | commit |
|---|---|---|
| devez-code | `main` | `6e838bcdfc7a` |
| devez | `master` | `c2405608658c` |
| eGhisDevWPF | `main` | `647326f8b9d4` |

스타일 파일이나 테마 서비스가 크게 바뀌면 이 문서의 palette, control geometry, icon 규칙, snapshot을 함께 갱신한다.
