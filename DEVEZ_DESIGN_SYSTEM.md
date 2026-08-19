# Devez 공통 디자인 시스템

> 현재 감사 대상: `devez-code`, `devez`
> 참고 프로필: `eGhisDevWPF`(2026-07-23 스냅샷, 이번 감사에서 재검증하지 않음)
> 문서 버전: 2.0
> 기준일: 2026-08-19
> 주 대상 기술: WPF/XAML
> 목적: 새 프로젝트에서 같은 색, 밀도, 컨트롤, 라운드, 아이콘, 보더, 상태 표현을 바로 재현한다.

## 빠른 탐색

[결정 규칙](#1-결정-규칙) · [색상과 테마](#3-의미-기반-색상-토큰) · [컨트롤](#8-컨트롤-규격) · [다이얼로그](#10-다이얼로그) · [접근성](#125-접근성) · [검수](#17-검수-체크리스트) · [원본 위치](#18-원본-소스-위치) · [현행 차이](#20-현재-구현-차이와-동기화-계약)

## 0. 1분 적용 요약

새 UI는 아래 순서로 시작한다.

1. 본문 폰트는 `Pretendard`, 브랜드 로고만 `Bruno Ace SC`를 쓴다.
2. 테마는 `minimal`, `soft`, `dark`, `gray`, `softpink`, `midnight` 6개를 제공한다.
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
13. 두 앱의 설정은 메인창 MDI 오버레이로 열고, 자체 헤더·푸터 없이 `350ms` 지연 자동 저장한다. 제품별 밀도와 테마 부작용은 §10.7 프로필을 따른다.
14. desktop notification popup은 폭 `340`, radius `12`, edge margin `6`, stack gap `8`을 쓴다.
15. 시스템 focus 표시를 제거할 때는 동등한 키보드 focus ring과 접근성 이름을 반드시 제공한다. 강조색 위 텍스트는 `OnAccent` 토큰으로 최소 `4.5:1` 대비를 보장한다.

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

1. 두 앱에서 이름·의도·동작이 같은 값은 **공통 필수**로 채택한다.
2. 같은 컴포넌트가 1:1로 갈리면 `devez-code`의 검증된 값을 정본으로 하되, 셸 구조나 기능 차이에서 비롯된 값은 **제품 프로필**로 분리한다.
3. 한 앱에만 있는 컴포넌트는 **제품 고유**로 기록하고, 다른 앱에 이미 구현된 것처럼 서술하지 않는다.
4. 정본과 소스가 다르면 문서 규칙을 완화하지 않고 **현행 이탈**에 등록한다.
5. 새 결정은 근거 소스, 적용 범위, 검증 항목을 함께 갱신한다. 변동성 높은 수치는 기준 스냅샷과 조사 방식을 명시한다.

### 1.2 조사 결과

| 항목 | devez-code | devez | 공통 결정 |
|---|---:|---:|---|
| 조사한 XAML | 34개 | 80개 | 실제 소스만 집계하고 XML parse 통과 |
| 핵심 스타일 파일 | `Styles/AppStyles.xaml` | `Styles/AppStyles.xaml` | Devez 계열 기반 |
| 최상위 직접 리소스 | 154개 | 130개 | 공통 130개 중 119개가 XML 기준 완전 동일 |
| 아이콘 리소스 | 196개 | 174개 | 공통 172개 중 171개가 완전 동일 |
| 지원 테마 | 6개 | 6개 | 같은 key와 카드 geometry 유지 |
| 기본 버튼 라운드 | 10 | 10 | 기본 10, compact는 명시적 토큰 재정의 |
| 다이얼로그 content 라운드 | 14 | 14 | content 14, shadow layer 13을 구분 |
| 다이얼로그 액션 | 높이 38 | 높이 38 | 높이 38 |
| 메인 타이틀바 | 32 | 44 | 프레임 프로필 차이 |
| 설정 category rail | 240 | 170 | MDI 공통, 제품 밀도 차이 |
| 분할선 채널 | 4 | 4 | 기본 4 |

조사 시 `.git`, `bin`, `obj`, `.verify`, `.claude/worktrees`는 제외했다. 직접 리소스는 `ResourceDictionary`의 최상위 자식, 아이콘은 `x:Key`가 있는 geometry로 집계했다.

### 1.3 문서 표기와 준수 수준

| 표기 | 의미 |
|---|---|
| 공통 필수 | 두 앱과 새 프로젝트가 반드시 지킬 정본 |
| 제품 프로필 | 공통 원칙은 같지만 프레임·밀도·기능 때문에 허용된 차이 |
| 제품 고유 | 한 앱만 제공하는 기능과 계약 |
| 현행 | 기준 스냅샷에서 실제 확인한 동작 |
| 목표 | 구현 전에는 준수했다고 간주하지 않는 다음 단계 규칙 |
| 현행 이탈 | 정본과 다른 구현 또는 아직 없는 검증·토큰 |

규범 문장과 현행 사실을 섞지 않는다. §2~17은 정본, §18~19는 근거와 시점, §20은 기준 스냅샷에서 확인된 이탈과 동기화 작업을 기록한다.

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
| 강조 위 텍스트 | `OnAccent` | 목표 `OnPrimaryBrush` | 별도 대비 토큰 권장 |
| 키보드 포커스 | `FocusRing` | `TabFocusRingBrush` 또는 목표 공통 토큰 | `Accent` 계열 |
| 링크 | `Link` | 일부 화면별 값, 공통 토큰 목표 | 화면별 |
| 검증 오류 | `Validation` | `DangerBrush` 파생, 공통 토큰 목표 | `DangerBrush` 파생 |
| 읽기 전용 표면 | `ReadOnlySurface` | `PanelSoftBrush` 파생, 공통 토큰 목표 | 화면별 |
| dim overlay | `OverlayDim` | 화면별 투명 검정, 공통 토큰 목표 | 화면별 |
| 성공 | `Success` | `SuccessBrush` | `SuccessBrush` |
| 위험 | `Danger` | `DangerBrush` | `DangerBrush` |
| 위험 위 텍스트 | `OnDanger` | 목표 `OnDangerBrush` | 별도 대비 토큰 권장 |
| 경고 | `Warning` | `devez-code`의 `WarningBrush`; `devez`는 미구현 | `WarningBrush` |

기존 저장소 안에서는 이름을 임의로 바꾸지 않는다. 새 프로젝트의 추상 계층에서만 공통 이름을 도입한다.

### 3.2 핵심 팔레트

아래 여섯 표는 `devez-code` 구현을 공통 정본으로 정리한 값이다. `devez`의 확장 3테마 현행 차이는 §20에 기록하며, 동기화 전에는 두 앱이 이미 같은 HEX라고 가정하지 않는다.

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
| Bubble / border | `#EEF4FF` / `#C5D8F8` |
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
| Bubble / border | `#E6F0DE` / `#C2D8B0` |
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
| Bubble / border | `#363636` / `#424242` |
| Success | `#22C55E` |
| Danger | `#EF4444` |
| Today | `#F97316` |

#### Gray

| 토큰 | HEX |
|---|---|
| App background | `#F3F4F6` |
| Surface | `#FFFFFF` |
| Surface soft / hover | `#E5E7EB` |
| Border soft | `#D1D5DB` |
| Text primary | `#1F2937` |
| Text secondary | `#5F6774` |
| Accent | `#4B5563` |
| Accent hover | `#374151` |
| Accent pressed | `#1F2937` |
| Accent soft | `#E2E5E9` |
| Bubble / border | `#ECEEF1` / `#C7CDD4` |
| Success | `#15803D` |
| Danger | `#C2413E` |
| Today | `#C2410C` |

#### Soft Pink

| 토큰 | HEX |
|---|---|
| App background | `#FFF7FA` |
| Surface | `#FFFCFD` |
| Surface soft / hover | `#FCEFF4` |
| Border soft | `#EBCFD9` |
| Text primary | `#3B2931` |
| Text secondary | `#735763` |
| Accent | `#B54A6B` |
| Accent hover | `#A43D5F` |
| Accent pressed | `#8E3451` |
| Accent soft | `#F8DCE6` |
| Bubble / border | `#FBE7EE` / `#E8BFCF` |
| Success | `#15803D` |
| Danger | `#C2413E` |
| Today | `#C2410C` |

#### Midnight

| 토큰 | HEX |
|---|---|
| App background | `#111827` |
| Surface | `#1F2937` |
| Surface soft | `#293548` |
| Surface hover | `#263449` |
| Border soft | `#374151` |
| Text primary | `#E5E7EB` |
| Text secondary | `#9CA3AF` |
| Accent | `#60A5FA` |
| Accent hover | `#3B82F6` |
| Accent pressed | `#2563EB` |
| Accent soft | `#1E3A5F` |
| Bubble / border | `#17243A` / `#2D4A6B` |
| Success | `#34D399` |
| Danger | `#F87171` |
| Today | `#FB923C` |

hover 토큰 규칙:

- `HoverBrush`는 light 계열에서 surface soft를 공유한다. dark는 `#424242`, midnight는 `#263449`를 쓴다.
- 조밀한 목록 행 hover는 별도 `SessionHoverBrush` 토큰이다: minimal `#EFF3F7`, soft `#EAE5DC`, dark `#2F2F2F`, gray `#E9EBEF`, softpink `#FAE8EF`, midnight `#263449`.
- 프로젝트 마커, 코드 구문, session 상태, bubble, badge처럼 파생 역할이 많은 색은 뷰에서 알파 혼합을 재계산하지 않는다. 테마 서비스가 여섯 팔레트의 파생 토큰을 소유한다.

### 3.3 보조 상태색

| 테마 | Checked background | Checked border | Checked text |
|---|---|---|---|
| Minimal | `#F0FDF4` | `#86EFAC` | `#166534` |
| Soft | `#D4EDCA` | `#8FC47E` | `#2A5220` |
| Dark | `#212822` | `#386038` | `#86EFAC` |
| Gray | `#F0FDF4` | `#86EFAC` | `#166534` |
| Soft Pink | `#E9F5EC` | `#91C79B` | `#25723C` |
| Midnight | `#12352D` | `#2A7661` | `#6EE7B7` |

Saturday, Sunday, Today, Warning은 본문색을 재사용하지 말고 역할 토큰으로 제공한다. `WarningBrush`는 현재 `devez-code`에만 있으므로 `devez`가 같은 key와 여섯 팔레트 값을 구현하기 전에는 공통 현행으로 간주하지 않는다.

강조 surface 위 텍스트는 고정 `White`로 두지 않는다. 현재 White 대비는 minimal `5.17:1`, soft `3.96:1`, dark `4.14:1`, gray `7.56:1`, softpink `5.05:1`, midnight `2.54:1`이므로 soft·dark·midnight가 일반 텍스트 기준을 충족하지 못한다. `OnAccent` 계열 토큰을 도입하거나 accent 상태색을 보정해 normal·hover·pressed 모두 `4.5:1`, 큰 텍스트와 UI 경계·focus ring은 `3:1` 이상을 검증한다.

### 3.4 테마 구현 규칙

- 소비자는 `{DynamicResource ...}`를 사용한다.
- 테마 전환 때 기존 brush의 `Color`만 바꾸지 말고 새 `SolidColorBrush` 인스턴스를 리소스에 다시 넣는다.
- Color와 Brush가 모두 필요한 토큰은 `AccentColor` + `Accent`처럼 함께 제공한다.
- 하드코딩은 투명 overlay와 변경 불가능한 브랜드 자산 정도만 허용한다. `White`도 의미 surface 위 foreground라면 대비를 검증한 `On*` 토큰을 쓴다.
- dark 여부로만 분기하지 않는다. 여섯 theme key를 독립 palette로 취급하고, dark 계열 판정이 필요한 플랫폼 연동에만 `dark`와 `midnight`를 함께 묶는다.
- 소비자가 테마 이름으로 색을 직접 분기하지 않는다. 테마 서비스가 base·state·content-on-color 토큰을 완성해 전달한다.

## 4. 타이포그래피

### 4.1 글꼴

| 용도 | 글꼴 |
|---|---|
| 전체 UI | `Pretendard` |
| 한글 fallback | `Malgun Gothic` |
| 브랜드명·로고 | `Bruno Ace SC` |
| 코드·터미널 | 사용자 선택 우선; `Cascadia Mono`, `Cascadia Code`, `Consolas`, `D2Coding`, `JetBrains Mono`, `Fira Code`, `NanumGothicCoding` 권장 |

WPF 리소스:

```xml
<FontFamily x:Key="PretendardFont">
    pack://application:,,,/Fonts/#Pretendard
</FontFamily>
<FontFamily x:Key="BrunoAceSCFont">
    pack://application:,,,/Fonts/#Bruno Ace SC
</FontFamily>
```

- 터미널 글꼴 값이 비어 있으면 호스트의 기본값을 유지한다. 임의의 UI 폰트로 덮지 않는다.
- WebView/terminal fallback은 선택 글꼴 뒤에 `Cascadia Mono`, `Consolas`, `D2Coding`, `NanumGothicCoding`, `Malgun Gothic`, symbol/emoji, `monospace` 순으로 둔다.
- 설정에서 글꼴이 바뀌면 WPF 리소스뿐 아니라 이미 열린 WebView·terminal에도 같은 payload를 다시 전송한다.

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
| 좌측 패널 최소 | `220` (`devez-code` 현행; 구버전 `190`을 복사하지 않는다) |
| 중앙 작업영역 최소 | `360` |
| 우측 보조 패널 | 약 `300` |
| splitter 채널 | `4` |
| splitter hover handle | 세로 `2×28`, 가로 `28×2` |
| 패널 경계 | `1` |

레이아웃 원칙:

- 메인 창 외곽은 사각형으로 유지한다.
- 패널은 서로 붙이지 않고 `1 DIP border + 4 DIP channel`로 구분한다.
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
- 새 프로젝트에서 판단이 어려우면 기본은 `devez-code`형이다. 1:1 차이에서 정본을 고르는 §1.1 규칙과 같다.
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
| active (`Tag="active"`) | `PrimaryBrush` | `OnPrimaryBrush` |

확장 상태에서는 아이콘용 고정 `32` 열 오른쪽에 `ToolTip` 문자열을 caption으로 재사용한다. caption은 `Fs13`, `CharacterEllipsis`. 축소 상태에서는 caption을 `Collapsed`로 두고 tooltip만 사용한다.

copy-ready Rail 버튼 template:

```xml
<Style x:Key="RailIconButton" TargetType="Button">
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
                        <Setter Property="Foreground" Value="{DynamicResource OnPrimaryBrush}" />
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
| sidebar | 기본 `262`, 최소 `220` |
| sidebar splitter | `4` |
| 중앙 작업영역 | `*`, 최소 `360` |
| 중앙 세로 최소(하단 패널 열림 시) | `220` |
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

#### 하단 shell 터미널 패널

중앙 작업영역 아래에 접을 수 있는 shell 터미널(에이전트 미연결 pwsh) 패널을 둘 수 있다.

- 중앙 grid의 rows는 `*`(본문, `MinHeight=220`), `Auto`(splitter), `0↔높이`(패널)이다.
- 닫힘 = 행 높이 `0` + 패널 `Collapsed`. HwndHost 포함이므로 `Hidden`을 쓰지 않는다(§13.2 규칙).
- 토글은 footer의 전용 버튼이 담당하고, 열림·닫힘은 즉시 전환한다(WebView2 포함 패널의 리사이즈 animation 금지 원칙).
- 가로 splitter는 `GridSplitterHorizontal` 스타일에 `Height=6`을 준다. 스타일의 상하 `-1` margin이 `2` DIP를 깎아 실질 채널이 사이드 splitter와 같은 `4` DIP가 된다.
- 패널 surface는 `BgBrush`, 보더는 `LineBrush` `1,1,1,0`. 열림 시 위쪽 본문 패널의 하단 `1` DIP 보더를 코드로 함께 켜 더블라인 채널을 만들고, 닫히면 끈다.
- splitter에도 `AllowDrop`을 줘 파일 드래그가 splitter 위를 지날 때 고스트가 깜빡이지 않게 한다.

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
| dark 계열 blur | `48` 허용 |
| dark 계열 opacity | `0.68` |
| light 계열 opacity | `0.35` |
| popup dark opacity | `0.42` |
| popup light opacity | `0.18` |
| shadow outer margin | `20`, dark 계열 `22` |
| content border | `1` |
| dialog shadow layer radius | `13` |
| dialog content radius | `14` |

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

공용 `PrimaryButton`의 라운드는 `PrimaryButtonRadius=10` 토큰으로 연결한다. 작업 큐처럼 한 surface 안에서만 radius `7`이 필요하면 해당 view의 local resources에서 토큰을 재정의하고 공용 template을 복제하지 않는다.

상태:

| 상태 | Primary | Secondary |
|---|---|---|
| Normal | Accent / OnAccent | Surface / Text primary / Border soft |
| Hover | Accent hover | Surface soft |
| Pressed | Accent pressed | Border soft 또는 Accent soft |
| Disabled | Border soft / Text secondary | Border soft / Text secondary |

Primary 예제:

```xml
<Style x:Key="PrimaryButton" TargetType="Button">
    <Setter Property="Background" Value="{DynamicResource PrimaryBrush}" />
    <Setter Property="Foreground" Value="{DynamicResource OnPrimaryBrush}" />
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="FontWeight" Value="SemiBold" />
    <Setter Property="Padding" Value="16,0" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="Button">
                <Border x:Name="Bd"
                        Background="{TemplateBinding Background}"
                        CornerRadius="{DynamicResource PrimaryButtonRadius}"
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
- `VisualStateManager`로 `Unchecked`/`Checked`를 표현하고, control 최초 실현·가상화 재사용 때는 현재 상태로 즉시 snap한다. `EnterActions`/`ExitActions`가 로드 때마다 전환을 재생하는 구현은 피한다.
- 키보드 focus ring, disabled opacity와 track·thumb 대비를 template 상태에 포함한다.

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
- active grid의 selected row에만 `1.5` DIP Accent overlay border를 그린다.
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

공휴일이 있는 달력은 상태 우선순위를 `Today > Holiday/Sunday > Saturday > Normal`로 둔다. 공휴일명은 날짜 아래 `Fs10` SemiBold로 표시하고 한 줄 말줄임과 전체 이름 tooltip을 함께 제공한다. 같은 날짜에 이름이 여러 개면 데이터 계층에서 결합하며, Today가 마지막 visual trigger로 전경·배경 대비를 보장한다.

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
- seam 양 끝은 `4` DIP 투명 gradient로 fade한다.
- 탭 하단 양쪽은 `10` DIP 확장된 feet path로 채워 content와 이어진 모양을 만든다.
- 탭이 scroll 경계에서 잘렸으면 잘린 쪽 seam gradient를 투명하게 시작하지 않는다.
- `CanContentScroll=False`로 픽셀 단위 스크롤을 유지한다.
- `ScrollViewer.Background="{x:Null}"`로 tab bar 배경을 가리지 않는다.
- overflow 때만 좌/우 `22×22` navigation button과 edge fade를 표시한다.
- selected tab은 선택·재정렬·scroll·theme 변경 후 항상 viewport 안으로 보정한다.
- drag 중 feet와 seam을 별도로 숨기거나 이동하고, 종료 시 복원한다.
- 탭은 같은 bar 안 재정렬 외에 다른 분할 패널·문서 그룹으로도 드래그 이동할 수 있다. 이동 경로의 splitter에는 `AllowDrop`을 줘 통과 중 드래그 고스트가 깜빡이지 않게 한다.

devez 채팅방 탭 변형(같은 패턴의 밀도 차이):

- tab bar 높이 `45`, 위치 표시 옵션이 켜지면 `54`.
- 첫 탭 앞 받침 여유 `11`은 동일. 선택 탭의 하단 받침(feet) 곡선은 약 `10` DIP.
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
- thumb margin `1,2`(세로 기준: 좌우 1 이 두께, 상하 2 가 길이 방향 여백)
- 가로 변형은 두께·길이 축이 90도 돌아가므로 margin 도 뒤집어 `2,1` 을 쓴다. 세로용을 그대로 쓰면 thumb 두께가 `4`가 되어 세로(`6`)보다 얇아 보인다.
- hover 시 Text secondary 또는 strong border 계열

Splitter:

- splitter 열/행 폭 `4`, 기본 transparent
- 전역 스타일에 `Margin="-1,0,-1,0"`(가로 변형은 `0,-1,0,-1`)을 줘 실질 hit 영역을 `6`으로 넓히고 인접 패널의 `1` DIP 경계 보더 위까지 덮는다. `Panel.ZIndex=1`로 패널 위에 올린다.
- 세로 hover handle `2×28`, 가로 hover handle `28×2`
- handle 라운드 `1`
- hover/drag 색 Accent, opacity `0→1` 즉시 전환(fade animation 없음)
- 가로/세로 커서는 `SizeWE` / `SizeNS`

경계 세퍼레이터 배치(1 DIP 선의 소유권):

- splitter 자체는 선을 그리지 않는다. 채널 양옆 `1` DIP 경계선은 **사이드 패널이 자기 쪽 보더로 그린다.**
- 좌측 사이드바는 오른쪽 보더 `BorderThickness="0,0,1,0"`, 우측 보조 패널은 왼쪽 보더 `"1,0,0,0"`. 중앙 작업영역은 좌우 보더를 그리지 않는다.
- 즉 구조는 `[사이드 패널+자기 보더 1] [투명 splitter 4] [중앙]`이며, 선을 splitter 열이나 중앙 콘텐츠에 중복으로 넣지 않는다.
- 패널이 접혀 폭 `0`이 되면 보더도 함께 사라지므로 별도 처리 없이 채널이 닫힌다.

트랙패드 정밀 휠 스크롤(`PrecisionWheelScroll`):

- WPF 기본 `ScrollViewer`는 `120` 미만의 트랙패드 미세 델타도 한 번의 휠 틱(3줄)으로 확대한다. 목록형 `ScrollViewer`에는 전역 등록 가능한 attached behavior로 작은 델타를 누적해 `120`(detent) 단위가 찼을 때만 줄 스크롤을 실행한다.
- 일반 마우스의 `120` 단위 입력은 기존 WPF 경로를 그대로 쓴다. 델타 방향이 바뀌거나 마지막 입력 후 `180ms`가 지나면 누적을 리셋한다.
- `Ctrl`/`Shift` 수정키가 눌린 휠(줌·가로 스크롤)은 개입하지 않고 누적도 버린다.
- 가장 가까운 scroll host부터 처리하고, 스크롤 불가(`ScrollableHeight≈0`)이거나 이미 맨 위/맨 아래 경계면 이벤트를 소비하지 않고 부모로 통과시킨다. 제품별 host가 다른 behavior를 쓰면 같은 경계·수정키·중첩 계약으로 검증한다.
- 터미널·WebView2 내부 스크롤에는 적용하지 않는다. WPF 쪽 목록에만 건다.

양축 스크롤:

- 세로와 가로 scrollbar가 함께 보이는 `ScrollViewer`는 `PART_VerticalScrollBar`, `PART_HorizontalScrollBar`, `PART_ScrollContentPresenter`를 보존한 cornerless template을 쓴다. 기본 시스템색 우하단 corner cell을 노출하지 않는다.
- edge fade·overlay는 실제로 표시된 세로/가로 scrollbar의 `8` DIP 영역을 각각 제외한다. 스크롤바 위를 fade가 덮어 입력과 대비를 약화시키면 안 된다.
- `devez-code`의 `CornerlessScrollViewer`가 정본이며, `devez`에 이식되기 전까지는 제품 고유 현행으로 분류한다.

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
- segmented group은 인접 border를 중복해서 `2` DIP로 만들지 않는다. 가운데 항목 radius는 `0`, 양 끝만 `6`.
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

### 8.17 복합 작업 큐 프로필

`devez-code`처럼 선택·일괄 action·drag merge를 한 목록에서 제공할 때의 제품 고유 density다.

- selection bar 높이 `44`, 전체 action bar 높이 `35`, compact action 높이 `27`, radius `7`.
- 공용 `PrimaryButton` template은 유지하고 local `PrimaryButtonRadius=7`만 재정의한다.
- 긴 목록은 virtualization을 유지하며 filter·selection 변경 때 container를 전부 재생성하지 않는다.
- bubble outer radius `14`, inner radius `13`, selected border `1.5`, merge target border `2.5`, pinned badge `18`.
- `Disabled > Busy > Merge target > Selected > Hover > Normal` 순으로 표현하고, drag target과 현재 selection을 색 하나로 합치지 않는다.
- danger action은 Danger/OnDanger, 작업 지시 action은 Arrow cursor를 사용한다. 현행 source가 다르면 §20의 이탈로 관리한다.

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
- `M0 0 M24 24`는 Lucide 기본 2-unit padding까지 포함해 아이콘이 작아 보이므로 쓰지 않는다.

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
5. 여섯 테마에서 `Foreground` cascade를 확인한다.
6. 100%, 125%, 150% DPI에서 중앙 정렬과 획 두께를 확인한다.

### 9.6 브랜드·provider 래스터 자산

- 앱 action과 상태 아이콘은 §9.1의 vector를 유지하고, 공식 브랜드·provider 로고만 래스터를 허용한다.
- 밝은 테마와 dark 계열에서 필요한 흑백 변형을 중앙 자산 서비스가 선택한다. 각 view가 테마 이름을 다시 판정하지 않는다.
- 원본은 실제 렌더 크기의 가까운 정수 배율을 쓴다. 예를 들어 `30px` 원본을 `15` DIP로 표시하고 `BitmapScalingMode=HighQuality`를 지정한다.
- 테마 변경 시 열린 화면의 source도 갱신하고, 아이콘 단독 control에는 접근성 이름과 tooltip을 제공한다.
- 100%, 125%, 150%에서 흐림·half-pixel 중심·명암 반전을 확인한다.

## 10. 다이얼로그

두 앱은 같은 borderless shell·버튼 순서·확인문자 계약을 공유하지만 geometry와 고급 API는 아직 다르다. 아래 표는 공통 목표이며 제품별 현행은 §10.1과 §20에서 분리한다.

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
| 안내 + footer 왼쪽 보조 링크 | `ConfirmDialog.AlertWithLink(...)` — `devez-code` 제품 고유 현행 |
| 업데이트 노트 + 같은 창 안 다운로드 진행률 | `ConfirmDialog.ShowUpdate(...)` — `devez-code` 제품 고유 현행 |
| 앱 초기화 전 치명적 오류, 테마 resource나 custom window 생성이 불가능한 최후 경로 | native `MessageBox.Show(...)` 허용 |

규칙:

- 앱이 정상 로드된 뒤에는 native `MessageBox.Show`를 사용하지 않는다.
- native MessageBox를 XAML style로 꾸미려 하지 않는다. 테마가 필요하면 `ConfirmDialog`로 옮긴다.
- 예외 처리기에서 `ConfirmDialog` 생성 자체가 다시 실패할 위험이 있거나 `Application.Current`가 준비되지 않은 경우만 native MessageBox를 fallback으로 허용한다.
- 과거 `WakeSchedulerWindow.xaml.cs`의 native MessageBox 2건은 깨우기 기능이 설정창 탭으로 이식되며 파일과 함께 제거됐다(이탈 해소). 현재 `devez-code`에 정상 로드 후 native MessageBox 사용처는 없다.
- owner가 살아 있으면 반드시 지정하고 `ShowDialog()`로 modal 수명을 보장한다.

현행 capability:

| 항목 | devez-code | devez | 공통 목표 |
|---|---|---|---|
| `Alert` / `Show` / `ShowThreeWay` | 지원 | 지원 | 공통 필수 |
| `AlertWithLink` | 지원 | 미지원 | 필요 시 공용 API로 이식 |
| `ShowUpdate` / 진행률 | 지원 | 미지원 | 업데이트 UX가 있는 앱에 이식 |
| 자동 폭 | `490~700` | 미지원 | 긴 문구에서 공통 지원 |
| 기본 크기 | `490×360` | `460×340` | `490×360` 정본으로 수렴 |
| body / line height | 좌우 `28` / `22` | 좌우 `20` / `20` | `28` / `22`로 수렴 |
| 줄 수별 높이 | `260/325/360` | `240/300/340` | `260/325/360`으로 수렴 |
| 글머리 렌더·indeterminate·상승 권한 결과 | 지원 | 미지원 | 기능을 이식할 때 함께 적용 |

#### 기본 외형

`devez-code:Views/ConfirmDialog.xaml`을 정본으로 한다.

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

- 폭은 `autoWidth`(`SizeToContent=WidthAndHeight`, `490~700`)로 노트 길이에 맞춘다. §10.8과 같은 규칙이다.
- 높이는 내용에 맞추되 `MaxHeight=430`이다.
- 긴 업데이트 노트는 body만 scroll한다.
- `·`, `•`, `-` 글머리는 glyph와 본문을 분리해 hanging indent로 렌더하고, glyph-body gap `6`, 빈 단락 gap `8`을 유지한다.
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
- destructive action의 **목표 스타일**은 `danger=true`일 때 Primary만 `DangerBrush`, 텍스트는 대비가 검증된 `OnDanger` 토큰이다. header/body/footer 구조는 그대로 둔다.

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
- determinate는 percentage와 fill을 함께 갱신하고, indeterminate는 보이는 동안만 animation을 실행한다.
- 일반 실패는 `UpdateOutcome.Failed`, 권한 상승 거부는 `UpdateOutcome.ElevationDenied`로 분리해 호출자가 맞는 안내를 이어간다.

#### `devez-code` 현재 구현과 목표 규칙 구분

현재 `ConfirmDialog` public API에는 `iconKey`, `danger`, `wideLayout` 매개변수가 있지만 기준 스냅샷의 XAML/code에서 실제 시각 요소에 연결되지 않는다.

- `autoWidth`: 실제 연결된 옵션이다. `SizeToContent=WidthAndHeight`, `MinWidth=490`, `MaxWidth=700`을 적용한다.
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
| settings shell | 메인창 MDI 오버레이, 새 폼형 제품만 선택적 borderless `Window`(§10.7) | 여러 범주의 지속 설정 | MDI: 돌아가기·ESC / 선택 프로필: 저장·취소·X·ESC |
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

### 10.7 설정 셸 — MDI 공통 계약과 제품 프로필

현재 감사한 두 앱은 모두 메인창 안의 MDI 설정을 쓴다. 별도 설정 창은 새 폼형 제품을 위한 선택 프로필일 뿐 두 앱의 현행으로 간주하지 않는다.

| 구분 | 선택 프로필 — 별도 설정 창 | 현행 공통 — 메인창 MDI 오버레이 |
|---|---|---|
| 채택 | 두 앱 미사용; 참고 `eGhisDevWPF` 스냅샷 | `devez-code`, `devez` |
| 셸 | borderless `Window` `970×830` | 메인창 상단바 아래를 덮는 오버레이 `Grid` |
| 헤더/푸터 | header `48` + footer `저장/취소` | 자체 헤더·푸터 없음 |
| 저장 모델 | 저장/취소 트랜잭션 + live preview | 변경 즉시 적용 + `350ms` 지연 저장 + 닫기 전 flush |
| 닫기 | 저장·취소·X·ESC | category rail 맨 위 `앱으로 돌아가기` · ESC |

선택 규칙: 설정이 대부분 즉시 적용 가능한 로컬 옵션이면 MDI, 저장 전 교차 필드 검증·일괄 반영이 필수인 새 폼형 제품이면 별도 창을 검토한다. 기존 두 앱은 설정 셸을 다시 별도 창으로 분기하지 않는다.

#### 변형 A — Window chrome

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

선택 프로필의 `970×830`과 category rail `205`는 과거 설정 창에서 채택한 기준값이다. 두 앱의 현행 소스에는 `SettingsWindow`가 없으므로 현재 구현 근거로 인용하지 않는다. 새 별도 창을 만들 때도 작은 화면에서는 working area 안에서 최대 `calc(100%-32)`로 줄인 뒤 content scroll을 사용한다.

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

#### 변형 A — Settings layout

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

- 첫 category는 `일반`이다. 이후 순서는 제품의 작업 흐름대로 정하되 알림·테마·단축키·변경내역·라이선스처럼 같은 의미의 category 이름은 통일한다.
- category를 추가하거나 재정렬해도 저장 방식과 키보드 탐색 계약은 바꾸지 않는다.
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

두 앱에 공통인 테마 card 규격:

| 항목 | 값 |
|---|---:|
| theme keys | `minimal`, `soft`, `dark`, `gray`, `softpink`, `midnight` |
| labels | `심플`, `소프트`, `다크`, `그레이`, `소프트 핑크`, `미드나잇` |
| card | `150×150` |
| card gap | `12` |
| 배열 | 3열 × 2행 |
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
- 변형 A: card를 누르면 앱 전체에 즉시 live preview하지만 디스크에는 저장하지 않는다. `저장`에서 theme과 font scale을 persist하고 original snapshot을 갱신하며, `취소`에서 열기 시점 상태로 되돌린다.
- MDI: card 클릭 즉시 화면에 적용하고 `350ms` debounce 후 저장한다. 닫기 전에 pending 저장을 flush한다.
- theme 변경의 session 부작용은 제품 프로필을 따른다. `devez-code`는 닫을 때 재시작 여부를 묻고 취소 시 `테마 되돌리기`를 제공하며, `devez`는 저장 뒤 terminal session을 즉시 재시작한다.
- frozen brush, cached seam, WebView theme payload처럼 `DynamicResource`만으로 갱신되지 않는 소비자를 theme changed event에서 다시 만든다.
- 글꼴 크기 card는 `120×56`, radius `10`, border `2`, gap `10`; `작게/크게` 두 단계가 기본이다.

#### 변형 A — Save, cancel, X, ESC

변형 A의 transaction:

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

#### MDI 공통 계약과 두 제품 프로필

공통 호스팅·저장 계약:

- `SettingsHost`는 titlebar 아래의 main content를 덮는 불투명 `BgBrush` grid다. 별도 `Window`, dim 스크림, 자체 header/footer를 만들지 않는다.
- 닫힘 진입점은 category rail 맨 위 `앱으로 돌아가기`와 `ESC`다. 닫기 전에 `350ms` debounce의 pending 저장을 동기 flush한다.
- control 변경은 화면에 즉시 반영하고 한 번의 지연 저장으로 묶는다. 빠른 연속 변경마다 파일을 쓰거나 footer `저장`/`취소`를 다시 만들지 않는다.
- 아래의 WebView2·terminal 같은 native child는 열기 전에 suspend 또는 snapshot + `Collapsed` 처리하고, 닫힌 뒤 복원한다. 제품별 host 종류가 달라도 airspace 계약은 같다.
- 설정이 열린 동안 아래 UI를 다시 노출할 수 있는 titlebar action을 숨기거나 비활성화한다. 닫힌 뒤 원래 visibility와 focus를 복원한다.
- 저장 실패는 해당 setting 가까이에 표시하고 마지막 정상값을 유지한다. debounce timer가 남은 채 view를 폐기하지 않는다.

| 항목 | devez-code 프로필 | devez 프로필 |
|---|---|---|
| host layer | main row 1~3, ZIndex `200` | main row 1, ZIndex `95` |
| category rail | `240` | `170` |
| content | `MaxWidth=1040`, margin `28,32,28,20` | `MaxWidth=1040`, margin `28,22,28,20` |
| 숨김·복원 대상 | terminal/WebView snapshot, 성능·패널 action | memo/thread WebView host, titlebar widgets |
| 테마 부작용 | 닫을 때 session reload 확인; 취소하면 `테마 되돌리기` | 저장 뒤 terminal session 즉시 재시작 |
| 기타 재시작 | 실제 활성 session이 있을 때만 안내 | GPU 설정 변경 시 안내 |
| 닫힌 뒤 후처리 | provider 사용량 갱신 | memo/thread host 재개 |

`devez-code`의 MCP·플러그인 JSON 편집은 별도 관리창의 명시 저장 모델이다. MDI 설정의 공통 auto-save 계약과 섞거나, 현재 숨겨진 category가 pending 편집을 가진다고 가정하지 않는다.

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
- 콘텐츠 자동폭 `490~700`, `SizeToContent=WidthAndHeight`, `MaxHeight=430`. 짧은 노트는 기본 폭을 유지하고 긴 노트는 최대 `700`까지 넓힌다. 노트 약 10줄까지 높이가 자동 확장되고 초과분은 본문 `ScrollViewer`가 스크롤한다.
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

### 10.9 파일 드롭 선택 오버레이

외부 파일을 작업영역에 드래그하면 놓을 위치(에디터로 열기 / 세션에 첨부)를 고르는 오버레이를 띄운다. `devez-code`의 `Views/WorkspacePaneView.xaml` `FileDropOverlay`가 정본이다.

- 오버레이는 창 전체가 아니라 **해당 워크스페이스 패널(중앙 작업영역) 안에만** 띄운다. 분할 상태에서는 파일이 들어온 패널만 덮는다.
- surface는 `PanelBrush` opacity `0.97`, `Panel.ZIndex=1000`, 커서 `Arrow`.
- 표시 전에 패널 안 터미널·브라우저·WebView 편집기를 `Collapsed` 처리해 HwndHost airspace를 제거한다(§13.2).
- 콘텐츠는 중앙 정렬: 안내 제목 `Fs15` SemiBold(`파일을 놓을 위치를 선택하세요`), 아래 `16`.
- 드롭존은 좌우 2개, 높이 `250`, 사이 gap `12`, `MaxWidth=620`. 각 존은 radius `14`, border `1.5`, 배경 `PanelSoftBrush`, 아이콘 `34` `PrimaryBrush`, 제목 `Fs16` SemiBold(위 `18`), 설명 `Fs12` muted(위 `7`).
- hover/drag-over 존은 `PrimaryBrush` 보더로 강조한다. 선택 카드 규칙(§8.8 좌측 카드)과 같은 보더-강조 채널이다.

### 10.10 Resizable 관리창

MCP·플러그인·상세 관리처럼 많은 행과 편집기를 함께 보여주는 창은 ConfirmDialog가 아니라 resizable 관리창 프로필이다.

- `devez-code` 현행에는 약 `720×600`, `940×640`, `1170×700`의 기능별 변형이 있다. 기능 밀도에 따른 profile 값이며 하나의 고정 크기로 합치지 않는다.
- borderless chrome, titlebar action, 최소 크기, owner center, theme token은 공통화한다. 행 버튼·category item style을 창마다 복제하지 않는다.
- 양축 editor는 §8.14 cornerless scroll을 사용하고, 긴 목록은 virtualization·검색 debounce·empty/loading/error 상태를 제공한다.
- JSON처럼 검증이 필요한 편집만 명시 저장을 쓴다. 자동 저장 MDI 설정의 footer를 관리창에 복사하거나 그 반대 흐름을 섞지 않는다.
- 닫기 전에 dirty editor를 확인하고, save 실패 시 창과 입력을 유지한 채 field 가까이에 오류를 표시한다.
- 드롭존 밖에서 놓거나 드래그가 패널을 벗어나면 오버레이를 닫고 아무것도 하지 않는다.
- 드래그 경로의 splitter에는 `AllowDrop`을 줘 통과 중 드래그 고스트가 깜빡이지 않게 한다.

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

- 시스템 점선 focus visual은 동등한 대체 표시가 있을 때만 제거한다. `FocusVisualStyle={x:Null}`만 적용해 키보드 focus를 보이지 않게 만들지 않는다.
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

### 12.5 접근성

- 모든 클릭 동작은 키보드로도 실행 가능해야 한다. 클릭 가능한 `Border`는 가능하면 `Button`/`MenuItem`으로 바꾸고, 불가하면 focus·Enter/Space·automation role을 함께 구현한다.
- 아이콘만 있는 control에는 `AutomationProperties.Name`을 지정한다. tooltip은 시각 보조일 뿐 접근성 이름을 대신하지 않는다.
- Tab 순서에서 주요 action과 설정 control을 제외하지 않는다. 장식 요소만 `IsTabStop=False`로 두고 dialog의 초기 focus, Enter primary, ESC cancel, 닫힌 뒤 owner focus를 검증한다.
- focus ring은 배경·인접색 대비 `3:1` 이상이며 색 외에 두께·윤곽으로도 식별한다. selection과 keyboard focus를 같은 상태로 합치지 않는다.
- 상태·오류·진행률은 색만으로 전달하지 않고 텍스트, icon, pattern, automation property 중 하나를 함께 제공한다.
- 200% 텍스트 확대와 긴 한국어·영문에서 잘림 없이 reflow 또는 scroll한다. icon button의 hit area는 최소 `24×24` DIP, 자주 쓰는 action은 `28×28` DIP 이상을 권장한다.
- Windows High Contrast, screen reader 이름·상태·순서, 키보드 전용 탐색, reduced motion을 release smoke test에 포함한다.
- 텍스트 대비는 일반 `4.5:1`, 큰 텍스트·UI component·focus indicator는 `3:1` 이상을 기본으로 한다.

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
- 시스템에서 animation 감소가 요청되면 장식용 fade·slide·회전을 제거하고 즉시 상태 전환한다. 진행 중임을 전달하는 필수 indicator는 정적인 label 또는 저동작 대체를 제공한다.
- WebView는 `prefers-reduced-motion`과 host 설정을 함께 반영한다. WPF와 web surface 중 한쪽만 animation을 끄지 않는다.

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
- 본문이 먼저 전체 폭을 먹으면 뒤의 시간 영역은 0 DIP가 될 수 있다.
- 시간, badge, action 너비를 먼저 예약하거나 본문에만 cap을 적용한다.

### 13.4 Embedded web·terminal 테마 계약

- navigation 전에 `DefaultBackgroundColor`를 현재 앱 배경으로 설정해 초기 흰색 flash를 막는다.
- 페이지 ready, theme 변경, font 변경 때 여섯 base palette와 코드 구문·상태·font token을 같은 payload로 전송한다. web view가 테마 이름으로 별도 HEX를 재구성하지 않는다.
- CSS 변수, `color-scheme`, `:focus-visible`, selection, scrollbar를 함께 갱신한다. 밝은 네 테마와 dark 두 테마에서 native form control 색도 확인한다.
- streaming/status 안내는 적절한 `aria-live`, 선택 목록은 listbox/option 또는 동등한 role과 상태를 제공한다.
- resume·loading·streaming·error 상태, keyboard-only, screen reader, reduced motion을 WPF host와 함께 검증한다.

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
- 강조 배경의 텍스트를 모든 테마·상태에서 고정 White로 두고 대비 검증 생략
- 대체 focus ring 없이 `FocusVisualStyle={x:Null}` 적용
- 아이콘 단독 action에 접근성 이름·키보드 경로를 제공하지 않음
- 클릭 가능한 `Border`를 mouse handler만으로 구현
- 양축 `ScrollViewer`의 기본 시스템 corner 또는 scrollbar 위를 덮는 fade 방치

## 17. 검수 체크리스트

### Visual

- [ ] 여섯 테마에서 모든 텍스트·보더·상태 토큰 확인
- [ ] Primary/Secondary/hover/pressed/disabled 상태 확인
- [ ] Accent 위 normal/hover/pressed 텍스트 `4.5:1`, UI·focus `3:1` 대비 확인
- [ ] 버튼 높이 38, 입력 높이 40, compact 높이 29~32 구분
- [ ] 기본 버튼 radius 10, 입력 radius 8, dialog radius 14 확인
- [ ] 아이콘 24 좌표계, 16 렌더, stroke 1.25 확인
- [ ] 원형 버튼이 실제 원형인지 확인
- [ ] dialog shadow radius 13 / content radius 14 구분
- [ ] ConfirmDialog 기본 폭 490, 본문 줄 수별 높이 260/325/360, 장문 최대 폭 640 확인
- [ ] ConfirmDialog body `Fs13` / line height 22 / 좌우 28 / scroll 확인
- [ ] ConfirmDialog footer action 높이 38 / 최소 폭 80 / 간격 8 확인
- [ ] 두 MDI 설정이 상단바 아래만 덮고 자체 header/footer가 없는지 확인
- [ ] settings 프로필: category `240/170`, content `MaxWidth=1040`, 제품별 margin 확인
- [ ] theme card 6개, 3열×2행, 150×150 / radius 10 / border 2 확인
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
- [ ] 선택 프로필의 별도 설정 창을 쓴 경우에만 live preview·저장·취소 원복 확인
- [ ] MDI 설정이 변경 즉시 반영, 350ms 저장, 닫기 전 flush 순서를 지킴
- [ ] 설정 중 제품별 titlebar action과 native host를 숨기고 닫힌 뒤 visibility·focus를 복원
- [ ] theme 변경 뒤 `devez-code`의 reload/되돌리기와 `devez`의 즉시 session 재시작 확인
- [ ] 업데이트 노트 팝업이 같은 팝업 안에서 진행률로 전환되고 다운로드 중 닫기 차단
- [ ] desktop notification이 입력 focus를 빼앗지 않음
- [ ] mixed-DPI 모니터에서 notification edge 6 / stack gap 8 유지
- [ ] 좁은 창에서 중앙 영역 최소 폭 보존
- [ ] 키보드만으로 모든 주요 action 실행, visible focus와 owner focus 복귀 확인
- [ ] screen reader에서 icon action 이름, control 상태, 오류·진행률 순서 확인
- [ ] Windows High Contrast, reduced motion, 텍스트 200%, 긴 문구 확인

### XAML

- [ ] `DynamicResource` 사용
- [ ] 인라인 style `BasedOn` 확인
- [ ] XAML XML parse 성공
- [ ] 중복 key 없음
- [ ] 아이콘 relative `m` 경로 확인
- [ ] hidden animation clock 정지
- [ ] border overlay가 hit test를 가로채지 않음
- [ ] `FocusVisualStyle={x:Null}`마다 대비되는 대체 focus 상태 존재
- [ ] icon-only control마다 `AutomationProperties.Name` 존재
- [ ] `Hand`와 hard-coded HEX가 allow-list 밖에 없는지 확인
- [ ] 양축 scroll corner가 투명하고 fade가 8 DIP scrollbar 영역을 피함

### 문서 동기화

- [ ] 두 저장소의 `DEVEZ_DESIGN_SYSTEM.md`가 정규화 비교가 아니라 byte-for-byte 동일하고 SHA-256도 동일
- [ ] 한 종류의 line ending, UTF-8, 마지막 newline 유지
- [ ] heading 번호가 연속이고 code fence·표 열 수·목차 anchor가 유효
- [ ] `devez-code:path`, `devez:path`로 표기한 모든 근거 경로가 해당 저장소에 존재
- [ ] 여섯 theme key·base token 집합과 기준 snapshot이 현재 소스와 일치
- [ ] 모든 XAML XML parse, resource key 중복·미해결 참조, 관련 디자인 회귀 테스트 통과

## 18. 원본 소스 위치

동일 문서가 두 저장소에 있으므로 일반 상대 링크는 쓰지 않는다. `저장소:path` 표기를 locator로 사용하고 검증 스크립트가 각 저장소 root에 매핑한다.

### devez-code

- `devez-code:Styles/AppStyles.xaml`, `devez-code:Resources/Icons.xaml`
- `devez-code:App.xaml.cs`의 `SetTheme`, 파생 토큰, font scale
- `devez-code:MainWindow.xaml`, `devez-code:MainWindow.xaml.cs`: compact chrome, status footer, `SettingsHost`, shell terminal
- `devez-code:Views/SettingsDialog.xaml`, `devez-code:Views/SettingsDialog.xaml.cs`: MDI 설정·350ms 저장·테마 되돌리기
- `devez-code:Views/ConfirmDialog.xaml`, `devez-code:Views/ConfirmDialog.xaml.cs`, `devez-code:Views/NoteText.cs`: MessageBox 정본·업데이트·글머리
- `devez-code:Behaviors/PrecisionWheelScroll.cs`: 전역 정밀 휠 계약
- `devez-code:Views/FileExplorerView.xaml`, `devez-code:Styles/AppStyles.xaml`: 양축 cornerless scroll
- `devez-code:Views/WorkspacePaneView.xaml`: 파일 드롭 선택 오버레이
- `devez-code:Views/SidebarView.xaml`, `devez-code:Views/TaskQueueView.xaml`: 선택·compact action 프로필
- `devez-code:Views/ClaudeChatHostView.xaml.cs`, `devez-code:Resources/ClaudeChat/web/chat.css`, `devez-code:Resources/ClaudeChat/web/chat.html`: embedded web theme·focus·ARIA
- `devez-code:Views/NotificationPopup.xaml`, `devez-code:Views/NotificationPopup.xaml.cs`, `devez-code:Services/UpdateService.cs`
- `devez-code:.knowledge/텍스트렌더링규칙.md`, `devez-code:.knowledge/컨트롤추가규칙.md`, `devez-code:.knowledge/webview2-airspace-패널리사이즈-깜빡임.md`

### devez

- `devez:Styles/AppStyles.xaml`, `devez:Resources/Icons.xaml`
- `devez:App.xaml.cs`의 `SetTheme`, 파생 토큰, font scale
- `devez:Views/MainWindow.xaml`, `devez:Views/MainWindow.xaml.cs`: 44 DIP chrome, rail, tray, `SettingsHost`
- `devez:Views/SettingsDialog.xaml`, `devez:Views/SettingsDialog.xaml.cs`: MDI 설정·350ms 저장
- `devez:TalkRemind_WPF.Tests/SettingsMdiHostTests.cs`: 설정 host·저장 회귀 계약
- `devez:Views/ConfirmDialog.xaml`, `devez:Views/ConfirmDialog.xaml.cs`: 현행 공통 API와 geometry
- `devez:Views/PopupWindowBase.cs`, `devez:Views/NotificationPopup.xaml`, `devez:Views/NotificationPopup.xaml.cs`
- `devez:Views/CalendarView.xaml`, `devez:ViewModels/CalendarViewModel.cs`, `devez:TalkRemind_WPF.Tests/CalendarHolidayTests.cs`: 공휴일 상태와 이름
- `devez:svg.md`, `devez:.knowledge/WPF-소프트웨어렌더-애니메이션.md`, `devez:.knowledge/wpf-esc-focus-닫힘.md`

### eGhisDevWPF

- `eGhisDevWPF:src/eGhisDev2.Presentation/Views/Styles/DevezTheme.xaml`
- `eGhisDevWPF:src/eGhisDev2.Presentation/Views/Styles/DialogChrome.xaml`
- `eGhisDevWPF:src/eGhisDev2.Presentation/Views/MainShell.xaml`
- `eGhisDevWPF:src/eGhisDev2.Presentation/Views/SettingsWindow.xaml`
- `eGhisDevWPF:src/eGhisDev2.Presentation/Views/ConfirmDialog.xaml`

이 참고 목록은 이번 감사에서 경로와 구현을 재검증하지 않았다. 공통 필수 결정을 위한 투표 근거로 사용하지 않는다.

## 19. 기준 스냅샷

이 문서의 구현 사실과 감사 수치는 다음 checkout을 기준으로 작성했다. 이후의 문서 전용 commit은 구현 snapshot을 낡게 만들지 않으며, UI·style·theme source가 바뀔 때만 다시 감사한다.

| 프로젝트 | 브랜치 | 구현 감사 commit | 범위 |
|---|---|---|---|
| devez-code | `main` | `e5ff5cc415d8` | 전체 현재 감사 |
| devez | `master` | `339e867d9b97` | 전체 현재 감사 |
| eGhisDevWPF | `main` | `647326f8b9d4` | 참고 스냅샷, 재감사 안 함 |

스타일 파일이나 테마 서비스가 바뀌면 이 문서의 palette, control geometry, icon 규칙, 현행 이탈, snapshot을 함께 갱신한다.

## 20. 현재 구현 차이와 동기화 계약

§2~17의 정본을 소스가 모두 충족한다는 뜻은 아니다. 아래는 2026-08-19 감사에서 확인한 제품 프로필과 현행 이탈이다.

| 영역 | devez-code 현행 | devez 현행 | 분류·조치 |
|---|---|---|---|
| 기본 테마 | `dark` fallback | `minimal` fallback | 제품 프로필, 허용 |
| 확장 3테마 palette | §3 정본값 | Gray·Soft Pink·Midnight의 일부 bubble·checked·today·accent 파생값이 다름 | `devez-code` 정본으로 동기화 필요 |
| Warning token | 구현 | `WarningBrush/Color` 없음 | 공통 역할 token 구현 필요 |
| OnAccent·대비 | 채움 버튼 foreground가 White | 채움 버튼 foreground가 White | soft·dark·midnight 대비 이탈; token/색 보정과 자동 검사 필요 |
| 공통 resource | 130개 공통 중 119개 exact | 동일 비교 기준 | 아래 11개를 정본·프로필로 분류 후 수렴 |
| 공통 icon | 172개 공통 중 171개 exact | `IconBrushCleaning` geometry가 다름 | `devez-code` geometry로 수렴 |
| Toggle | VSM, 최초 실현 snap, `150ms` | trigger Enter/Exit storyboard | VSM 계약으로 수렴 |
| Primary radius | dynamic `PrimaryButtonRadius`; local `7` 허용 | template fixed `10` | token 방식으로 수렴 |
| Icon disabled | disabled opacity 제공 | 공용 disabled 상태 부족 | 상태 계약 보강 |
| Cursor | 공용 selector/menu는 Arrow | `SelectorChip`, `CodeLangMenuItem` 등에 Hand | non-link Hand 제거 |
| Scrollbar | 가로 thumb `2,1`, cornerless 양축 template | 가로도 `1,2`, cornerless 없음 | §8.14로 수렴 |
| 정밀 wheel | 전역 opt-out 가능한 behavior | 별도 smooth scroll 구현 | 경계·수정키·중첩 계약은 공통 검증 |
| ConfirmDialog | 정본 geometry, link/update/자동폭/진행률 지원 | 작은 geometry, 공통 3 API만 지원 | capability 표를 유지하며 단계적 이식 |
| Settings | rail `240`, 닫을 때 reload/되돌리기 | rail `170`, theme 뒤 session 즉시 재시작 | 제품 프로필, 허용 |
| Theme label | 카드 `미드나잇`, 일부 selector `미드나이트 블루` | `미드나잇` | 한글 표시명 통일 필요 |
| Notification | 다중 위치·no-activate 정렬 프로필 | 우하단, offset `7`, DIP stack | 공통 계약과 제품 capability를 분리해 테스트 필요 |
| Calendar holiday | 해당 surface 없음 | 공휴일명·우선순위 구현 | 제품 고유, §8.12 준수 |
| Automation·focus | 명시적 automation 이름 0건, focus 제거 다수 | 명시적 automation 이름 0건, focus 제거 다수 | §12.5를 구현·회귀 테스트해야 함 |
| BasedOn·cursor lint | local style와 non-link Hand 잔존 | local style와 non-link Hand 잔존 | allow-list 기반 정적 검사 필요 |
| 테스트 | 일부 theme·terminal script, 접근성/6테마 UI 부족 | Settings MDI·공휴일 회귀 있음, dialog/접근성/6테마 부족 | 공통 design contract test 추가 |

서로 다른 공통 resource 11개는 `CodeLangMenuItem`, `IconButton`, implicit `MenuItem`, implicit `ScrollBar`, `PrimaryButton`, `SelectorChip`, `SubMenuHeaderItem`, `ToggleSwitch`, `ToggleSwitchSmall`, `WinCloseBtn`, `WinCtrlBtn`이다. Window control 크기처럼 frame에서 비롯된 값은 제품 프로필로 유지하고, cursor·disabled·animation·token 연결 차이는 공통 정본으로 수렴한다.

동기화 절차:

1. 변경하려는 규칙의 정본 절과 `저장소:path` 근거를 먼저 갱신한다.
2. 두 앱의 제품 프로필과 현행 이탈을 분리해, 미구현 기능을 현행처럼 쓰지 않는다.
3. 문서를 두 저장소에 같은 UTF-8 bytes와 line ending으로 반영한다.
4. XAML parse, resource·theme 검사, 관련 회귀 test, 문서 구조·경로 validator를 실행한다.
5. 외부에서 두 문서의 byte length와 SHA-256을 비교한다. 문서 안에는 자기참조 SHA를 기록하지 않는다.
6. 두 저장소의 문서 commit을 함께 push하고 각 원격 branch가 의도한 commit을 가리키는지 확인한다.
