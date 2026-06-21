# WPF — ListBoxItem 기본 템플릿이 IsSelected 트리거로 행 전체에 색을 칠한다

## 한 줄 요약
`ListBoxItem` 의 `Background="Transparent"` 만 설정해서는 부족하다. WPF 기본
`ListBoxItem.Template` 안의 `IsSelected` 트리거가 `TemplateBinding Background` 보다
우선해 **행 전체에 파란색 선택 배경**을 칠한다. 행 배경이 항상 깨끗해야 하면
`ContentPresenter` 만 두는 미니멀 커스텀 템플릿으로 기본 트리거를 완전히 제거할 것.

## 증상
- ListBoxItem 에 `Background="Transparent"` + `FocusVisualStyle="{x:Null}"` 설정.
- 항목을 클릭/우클릭하면 **행(row) 전체 너비로** 파란색(또는 시스템 강조색) 배경이 들어옴.
- 선택 표시는 DataTemplate 안의 SelectionRing 으로만 하고 싶은데, 행 자체가 색이 들어가
  이중으로 강조된 것처럼 보임.
- FocusVisualStyle 은 점선 테두리만 제거하지 행 배경은 못 막음.

## 원인
WPF 기본 `ListBoxItem.Template` 의 트리거:
```xml
<ControlTemplate TargetType="ListBoxItem">
  <Border x:Name="Bd" Background="{TemplateBinding Background}" ...>
    <ContentPresenter ... />
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsSelected" Value="True">
      <Setter Property="Background" TargetName="Bd" Value="{DynamicResource {x:Static SystemColors.HighlightBrushKey}}" />
      <Setter Property="Foreground" Value="{DynamicResource {x:Static SystemColors.HighlightTextBrushKey}}" />
    </Trigger>
    <MultiTrigger>
      <MultiTrigger.Conditions>
        <Condition Property="IsSelected" Value="True" />
        <Condition Property="Selector.IsSelectionActive" Value="False" />
      </MultiTrigger.Conditions>
      <Setter Property="Background" TargetName="Bd" Value="{DynamicResource {x:Static SystemColors.ControlBrushKey}}" />
    </MultiTrigger>
    ...
  </ControlTemplate.Triggers>
</ControlTemplate>
```

`Setter TargetName="Bd" Property="Background" ...` 가 `TemplateBinding Background` 보다
우선한다. 그래서 `Style` 에서 `Background="Transparent"` 를 줘도 IsSelected=true 가
되는 순간 파란색으로 덮어써진다.

`FocusVisualStyle` 은 별개 — 점선 포커스 테두리만 제거하지, 선택 배경 트리거는 그대로.

## 재현 패턴 (XAML)

### ❌ 잘못된 코드 (행이 파랗게 칠해짐)
```xml
<ListBox.ItemContainerStyle>
  <Style TargetType="ListBoxItem">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <!-- … -->
  </Style>
</ListBox.ItemContainerStyle>
```

### ✅ 정상 (행은 항상 투명, 선택은 DataTemplate 의 Ring 으로만)
```xml
<ListBox.ItemContainerStyle>
  <Style TargetType="ListBoxItem">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <!-- … -->
    <!-- 기본 ControlTemplate 의 IsSelected/IsSelectionActive 트리거를 완전히 제거.
         ContentPresenter 만 두면 행 배경은 항상 비어 있고, 선택 표시는
         DataTemplate 내부의 SelectionRing 같은 커스텀 비주얼로만 표현. -->
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ListBoxItem">
          <ContentPresenter/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ListBox.ItemContainerStyle>
```

## 디버깅 포인트
1. ListBox 의 항목이 클릭/우클릭 후 **행 전체 폭** 으로 색이 들어가면 의심.
2. `Background="Transparent"` 만으로 해결 안 되면 90% 확률로 이 함정.
3. 같은 함정이 `ListBox`, `TabControl` (TabItem), `TreeView` (TreeViewItem) 등 Selector
   파생 컨트롤에도 존재 — 같은 패턴의 미니멀 템플릿으로 해결.

## 안전한 가드레일 (체크리스트)
- [ ] ListBoxItem 의 `Background="Transparent"` + `FocusVisualStyle="{x:Null}"` 만으로는
      선택 행 배경을 못 막는다 — 커스텀 `Template` (ContentPresenter) 까지 세트로.
- [ ] 선택 표시는 DataTemplate 의 `RelativeSource AncestorType=ListBoxItem, Path=IsSelected`
      기반 DataTrigger 로만 (행 배경 X, 외곽선/오버레이만).
- [ ] TreeViewItem / TabItem 도 같은 패턴인지 의심.

## 관련
- `wpf-contextmenu-setter-value-connectionid-충돌.md` — 같은 Setter 패턴 함정이지만
  원인(connectionId 충돌)은 다름. 둘 다 "Setter 안에는 복잡한 트리 두지 말라" 는 원칙으로
  연결됨.
- doit 정합: 버블 행 배경은 항상 PanelBrush, 선택은 버블 외곽선 1.5px 만.
