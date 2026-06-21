# WPF — Style.Setter.Value 내부 인라인 자식 요소는 connectionId 충돌을 일으킨다

## 한 줄 요약
`Style.Setter.Property="ContextMenu"` (혹은 `Template`) 의 자식으로 `ContextMenu`+`MenuItem` 같은
이벤트 핸들러가 붙는 요소 트리를 **인라인으로 정의하지 말 것**. 반드시 `UserControl.Resources` 같은
상위 리소스로 분리해 `StaticResource` 로 참조할 것. 안 그러면 런타임에 `XamlParseException` 으로 앱이
즉시 크래시한다.

## 증상
- 이벤트 로그: `CLR20r3`, `P9=System.Windows.Markup.XamlParse`
- 앱 시작 직후(InitializeComponent 중) 크래시
- 스택:
  ```
  System.InvalidCastException: Unable to cast object of type
    'System.Windows.Controls.ContextMenu' to type 'System.Windows.Controls.Border'.
     at DevezCode.Views.TaskQueueView
        .System.Windows.Markup.IComponentConnector.Connect(Int32 connectionId, Object target)
        in C:\...\Views\TaskQueueView.xaml:line 130
  ```
- 컴파일된 `.g.cs` 안 `Connect` 메서드의 특정 `case` 에서:
  ```csharp
  case 5:
      ((System.Windows.Controls.Border)(target))
          .AddHandler(System.Windows.Controls.ContextMenu.OpenedEvent, ...);
  ```
  → `target` 은 실제 `ContextMenu` 인데 컴파일러는 부모 `Border` (ListBoxItem) 로 캐스팅 시도.

## 원인
WPF XAML 컴파일러는 같은 `x:Class` 안에 등장하는 모든 `x:Name` / 이벤트 후크 요소를
**하나의 `IComponentConnector.Connect()` 메서드** 에 `connectionId` 순서로 매핑한다.
그런데 `Setter.Value` 의 인라인 자식 요소들은 다음 규칙을 따른다:

- `Setter.Value` 안의 요소도 `x:Class` 의 일부로 간주되어 `Connect` 에 들어간다.
- `Setter.Value` 의 자식 요소들 (예: `ContextMenu`, `MenuItem`) 은 자신의 `connectionId` 가
  부여되지만, 이들의 이벤트 후크 코드 (`AddHandler`) 는 **부모인 ListBoxItem 을 target 으로
  가정하고** 생성된다.
- 같은 `connectionId` 가 두 가지 다른 객체(ContextMenu + Border/ListBoxItem) 에 매핑되려 하면서
  런타임 캐스팅 실패.

핵심: `Setter.Value` 내부에 정의한 자식 요소는 **`x:Class` 의 직접 자식처럼 취급되지만,
이벤트 후크의 target 은 부모 컨테이너(Setter 가 적용되는 요소)** 라는 비대칭이 있다.

## 재현 패턴 (XAML)

### ❌ 크래시
```xml
<ListBox.ItemContainerStyle>
  <Style TargetType="ListBoxItem">
    <Setter Property="ContextMenu">
      <Setter.Value>
        <ContextMenu Opened="BubbleContextMenu_Opened">      <!-- 자식 요소 -->
          <MenuItem x:Name="CopyMenuItem" Click="...">         <!-- 자식 요소 -->
          <MenuItem x:Name="DeleteMenuItem" Click="...">       <!-- 자식 요소 -->
        </ContextMenu>
      </Setter.Value>
    </Setter>
  </Style>
</ListBox.ItemContainerStyle>
```

### ✅ 정상 (해결)
```xml
<UserControl.Resources>
  <ContextMenu x:Key="BubbleContextMenu" Opened="...">
    <MenuItem Click="...">...</MenuItem>
    <MenuItem Click="...">...</MenuItem>
  </ContextMenu>
</UserControl.Resources>

<ListBox.ItemContainerStyle>
  <Style TargetType="ListBoxItem">
    <Setter Property="ContextMenu" Value="{StaticResource BubbleContextMenu}"/>
  </Style>
</ListBox.ItemContainerStyle>
```

## 디버깅 포인트
1. 크래시 스택에 `IComponentConnector.Connect` + `xaml:line N` 이 보이면 **컴파일된 .g.cs 의
   같은 line 의 `case` 블록을 확인**. `((Foo)(target))` 캐스팅이 Setter 부모 타입과 안 맞으면
   이 버그.
2. `Setter.Value` 내부에 정의한 요소에 `x:Name` 이 있거나 `Click`/`Opened` 같은 라우트 이벤트가
   후크되어 있으면 의심.
3. 같은 패턴은 `Setter Property="Template"` 에서도 가능하다 — `ControlTemplate` 자식에
   라우트 이벤트가 걸린 요소를 인라인으로 두면 같은 충돌이 날 수 있음 (실제 doit 코드 사례는
   확인 안 됨, 잠재적 위험).

## 안전한 가드레일 (체크리스트)
- [ ] `Style.Setter.Value` 내부에는 **단순 값 (bool, double, 색상, StaticResource 참조)** 만
      둔다.
- [ ] 라우트 이벤트가 걸리는 요소(`ContextMenu`, `MenuItem`, `Button`, `ControlTemplate`
      등) 는 **절대 Setter.Value 인라인에 두지 않고** 상위 `Resources` 로 분리.
- [ ] `Setter Property="ContextMenu" Value="{StaticResource X}"` 처럼 참조만.
- [ ] `Setter Property="Template"` 도 같은 원칙 — `ControlTemplate` 전체를 리소스로 분리.

## 관련
- 동일 함정: `DataTemplate` 내부의 `x:Name` 은 컴파일된 `Connect` 에 들어가지 않으므로
  안전 (DataTemplate 은 인스턴스화 방식이 다름). 본 버그는 `Setter.Value` 만 해당.
- `EventSetter` 는 별도 `IStyleConnector.Connect` 로 매핑되므로 안전.
- `d:Name` (design-time only) 사용은 런타임 매핑을 피하는 또 다른 우회법이지만,
  디자인 도구 지원이 끊기므로 리소스 분리가 더 깔끔.
