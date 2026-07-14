# SCM 변경 파일 폴더 트리 Implementation Plan

> **For agentic workers:** 이 계획을 task 순서대로 구현. 빌드/앱 실행/커밋/푸시 금지(본 세션이 DevezCode 내부 실행일 수 있음). 편집만 남긴다.

**Goal:** `GitScmView`의 평면 변경 파일 목록을 VS Code SCM식 **전체 중첩 폴더 트리**로 교체.

**Architecture:** `Models/ScmTreeNode`(폴더/파일 노드) + `GitScmView.BuildTree`(경로 분해→전체 중첩) + 두 `TreeView`(HierarchicalDataTemplate). 기존 stage/unstage/discard·섹션 개수·커밋박스·동기화 헤더·파일 클릭→Monaco diff 보존.

**Tech Stack:** WPF TreeView/HierarchicalDataTemplate.

## Global Constraints
- 빌드/실행/커밋/푸시 금지.
- 모든 색 `DynamicResource`, 아이콘/버튼 기존 `IconButton`·글리프, 인라인 하드코딩 색 금지(diff 상태색 예외).
- 기존 핸들러 `Stage_Click`/`Unstage_Click`/`Discard_Click` 는 `Tag`(GitChange) 기반이라 시그니처 유지 — `Tag="{Binding Change}"` 로만 연결.

---

### Task 1: ScmTreeNode 모델

**Files:** Create `Models/ScmTreeNode.cs`

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DevezCode.Models;

/// <summary>SCM 변경 트리 노드 — 폴더(IsFolder=true, Children) 또는 파일 리프(Change != null).</summary>
public sealed class ScmTreeNode : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public bool IsFolder { get; init; }
    public ObservableCollection<ScmTreeNode> Children { get; } = new();
    /// <summary>파일 리프면 해당 변경, 폴더면 null.</summary>
    public GitChange? Change { get; init; }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); }
    }

    /// <summary>파일 리프의 상태 글자(폴더면 빈 문자열).</summary>
    public string Status => Change?.Status ?? "";
    /// <summary>파일 리프의 상태 색.</summary>
    public string StatusColor => Change?.StatusColor ?? "#8B949E";

    public event PropertyChangedEventHandler? PropertyChanged;
}
```

- [ ] Step 1: 위 파일 생성.

---

### Task 2: GitScmView 코드비하인드 — 트리 빌드 + 클릭

**Files:** Modify `Views/GitScmView.xaml.cs`

- [ ] **Step 1: using 추가** — 파일 상단 `using` 목록에 (없으면):
```csharp
using System.Collections.Generic;
using System.Linq;
```

- [ ] **Step 2: RefreshAsync 에서 트리 세팅** — `RefreshAsync` 의 성공 경로에서
`_staged.Clear(); foreach ... _unstaged.Add(c);` 다음(EmptyText 설정 위)에 추가:
```csharp
        StagedTree.ItemsSource = BuildTree(st.Staged);
        UnstagedTree.ItemsSource = BuildTree(st.Unstaged);
```
그리고 무효/빈 경로(초기 `if (...) { _staged.Clear(); _unstaged.Clear(); ... }`)에도 추가:
```csharp
            StagedTree.ItemsSource = null;
            UnstagedTree.ItemsSource = null;
```
(`_staged`/`_unstaged` ObservableCollection 은 개수 계산용으로 그대로 유지.)

- [ ] **Step 3: 트리 빌드 + 정렬 헬퍼 추가** — 클래스 내부에:
```csharp
    /// <summary>평면 변경 목록 → 전체 중첩 폴더 트리(폴더 우선·이름 오름차순).</summary>
    private static List<ScmTreeNode> BuildTree(IEnumerable<GitChange> changes)
    {
        var roots = new List<ScmTreeNode>();
        var folders = new Dictionary<string, ScmTreeNode>();   // 누적경로 → 폴더노드
        foreach (var ch in changes)
        {
            var segs = ch.Path.Split('/');
            var siblings = roots;
            var acc = "";
            for (int i = 0; i < segs.Length - 1; i++)
            {
                acc = acc.Length == 0 ? segs[i] : acc + "/" + segs[i];
                if (!folders.TryGetValue(acc, out var folder))
                {
                    folder = new ScmTreeNode { Name = segs[i], IsFolder = true };
                    folders[acc] = folder;
                    siblings.Add(folder);
                }
                siblings = ToList(folder.Children);
            }
            siblings.Add(new ScmTreeNode { Name = segs[^1], IsFolder = false, Change = ch });
        }
        Sort(roots);
        return roots;

        // 폴더의 Children(ObservableCollection)에 계속 add 하기 위한 지역 참조 헬퍼.
        static List<ScmTreeNode> ToList(ObservableCollection<ScmTreeNode> oc) => new ChildProxy(oc);
    }

    /// <summary>ObservableCollection 에 Add 를 위임하는 List 어댑터(트리 빌드용).</summary>
    private sealed class ChildProxy : List<ScmTreeNode>
    {
        private readonly ObservableCollection<ScmTreeNode> _oc;
        public ChildProxy(ObservableCollection<ScmTreeNode> oc) { _oc = oc; }
        public new void Add(ScmTreeNode n) => _oc.Add(n);
    }

    private static void Sort(List<ScmTreeNode> nodes)
    {
        nodes.Sort((a, b) => a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var f in nodes) if (f.IsFolder)
        {
            var tmp = f.Children.ToList();
            Sort(tmp);
            f.Children.Clear();
            foreach (var c in tmp) f.Children.Add(c);
        }
    }
```
> 주의: `roots` 는 순수 `List` 라 `List.Add` 가 맞다. 폴더의 `Children` 는 `ObservableCollection` 이므로 위 `siblings` 가 폴더 자식일 때는 `ChildProxy.Add` 로 위임된다. `roots` 최상위 add 는 `siblings==roots`(순수 List)라 정상. **더 단순히**: `siblings` 를 `IList<ScmTreeNode>` 로 두고 roots(`List`)와 `folder.Children`(`ObservableCollection`) 둘 다 `IList` 로 취급하면 ChildProxy 가 불필요하다 — 아래 대체 구현을 쓰라:
```csharp
    private static List<ScmTreeNode> BuildTree(IEnumerable<GitChange> changes)
    {
        var roots = new List<ScmTreeNode>();
        var folders = new Dictionary<string, ScmTreeNode>();
        foreach (var ch in changes)
        {
            var segs = ch.Path.Split('/');
            System.Collections.Generic.IList<ScmTreeNode> siblings = roots;
            var acc = "";
            for (int i = 0; i < segs.Length - 1; i++)
            {
                acc = acc.Length == 0 ? segs[i] : acc + "/" + segs[i];
                if (!folders.TryGetValue(acc, out var folder))
                {
                    folder = new ScmTreeNode { Name = segs[i], IsFolder = true };
                    folders[acc] = folder;
                    siblings.Add(folder);
                }
                siblings = folder.Children;   // ObservableCollection<T> 는 IList<T> 구현
            }
            siblings.Add(new ScmTreeNode { Name = segs[^1], IsFolder = false, Change = ch });
        }
        Sort(roots);
        return roots;
    }
```
**위 대체 구현(IList 방식)을 채택하고 ChildProxy/ToList 는 쓰지 말 것.**

- [ ] **Step 4: 노드 클릭 핸들러 추가** (기존 `Row_Click` 대체):
```csharp
    private void Node_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ScmTreeNode node }) return;
        if (node.IsFolder) { node.IsExpanded = !node.IsExpanded; return; }
        if (_repo != null && node.Change != null)
            DiffFileActivated?.Invoke(_repo, node.Change.Path, node.Change.IsStaged);
    }
```
기존 `private void Row_Click(...)` 메서드는 삭제(더 이상 참조 없음).

---

### Task 3: GitScmView XAML — TreeView 교체

**Files:** Modify `Views/GitScmView.xaml`

- [ ] **Step 1: UserControl.Resources 에 트리 스타일/템플릿 추가** — 최상위 `<Grid>` 바로 앞에:
```xml
    <UserControl.Resources>
        <!-- 컴팩트 TreeViewItem: 셰브론 + 행 + 자식(들여쓰기), 선택 하이라이트 제거 -->
        <Style x:Key="ScmTreeItem" TargetType="TreeViewItem">
            <Setter Property="IsExpanded" Value="{Binding IsExpanded, Mode=TwoWay}"/>
            <Setter Property="Focusable" Value="False"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="TreeViewItem">
                        <StackPanel>
                            <Border x:Name="RowBd" CornerRadius="6" Background="Transparent" Padding="2,3">
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="16"/>
                                        <ColumnDefinition Width="*"/>
                                    </Grid.ColumnDefinitions>
                                    <TextBlock x:Name="Chev" Grid.Column="0" Text="▾" TextAlignment="Center"
                                               VerticalAlignment="Center" FontSize="9"
                                               Foreground="{DynamicResource TextMutedBrush}"/>
                                    <ContentPresenter Grid.Column="1" ContentSource="Header"/>
                                </Grid>
                            </Border>
                            <ItemsPresenter x:Name="ItemsHost" Margin="12,0,0,0"/>
                        </StackPanel>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsExpanded" Value="False">
                                <Setter TargetName="ItemsHost" Property="Visibility" Value="Collapsed"/>
                                <Setter TargetName="Chev" Property="Text" Value="▸"/>
                            </Trigger>
                            <Trigger Property="HasItems" Value="False">
                                <Setter TargetName="Chev" Property="Visibility" Value="Hidden"/>
                            </Trigger>
                            <Trigger SourceName="RowBd" Property="IsMouseOver" Value="True">
                                <Setter TargetName="RowBd" Property="Background" Value="{DynamicResource PanelSoftBrush}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- 노드 행: 폴더(이름) 또는 파일(상태+이름+액션). ItemsSource=Children 로 재귀. -->
        <HierarchicalDataTemplate x:Key="ScmNode" DataType="{x:Type models:ScmTreeNode}" ItemsSource="{Binding Children}">
            <Grid Background="Transparent" MouseLeftButtonUp="Node_Click">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <!-- 파일 상태 글자(폴더면 빈칸) -->
                <TextBlock Grid.Column="0" Text="{Binding Status}" Width="16" TextAlignment="Center"
                           FontWeight="Bold" Foreground="{Binding StatusColor}"/>
                <TextBlock Grid.Column="1" Text="{Binding Name}" Margin="6,0,0,0"
                           TextTrimming="CharacterEllipsis" ToolTip="{Binding Change.Path}"
                           Foreground="{DynamicResource TextBrush}"/>
                <!-- 파일 리프 액션: unstage(−) 또는 discard(↩)+stage(+). 폴더면 숨김. -->
                <Button x:Name="UnstageBtn" Grid.Column="2" Content="−" Tag="{Binding Change}"
                        Click="Unstage_Click" Style="{StaticResource IconButton}" Width="22" Height="22"
                        Visibility="Collapsed" ToolTip="unstage"/>
                <Button x:Name="DiscardBtn" Grid.Column="2" Content="↩" Tag="{Binding Change}"
                        Click="Discard_Click" Style="{StaticResource IconButton}" Width="22" Height="22"
                        Visibility="Collapsed" ToolTip="변경 취소"/>
                <Button x:Name="StageBtn" Grid.Column="3" Content="+" Tag="{Binding Change}"
                        Click="Stage_Click" Style="{StaticResource IconButton}" Width="22" Height="22"
                        Visibility="Collapsed" ToolTip="stage"/>
            </Grid>
            <HierarchicalDataTemplate.Triggers>
                <!-- 파일 & staged → unstage 버튼 -->
                <DataTrigger Binding="{Binding Change.IsStaged}" Value="True">
                    <Setter TargetName="UnstageBtn" Property="Visibility" Value="Visible"/>
                </DataTrigger>
                <!-- 파일 & unstaged → discard + stage. IsFolder=false 이고 Change!=null 인 경우만. -->
                <MultiDataTrigger>
                    <MultiDataTrigger.Conditions>
                        <Condition Binding="{Binding IsFolder}" Value="False"/>
                        <Condition Binding="{Binding Change.IsStaged}" Value="False"/>
                    </MultiDataTrigger.Conditions>
                    <Setter TargetName="DiscardBtn" Property="Visibility" Value="Visible"/>
                    <Setter TargetName="StageBtn" Property="Visibility" Value="Visible"/>
                </MultiDataTrigger>
            </HierarchicalDataTemplate.Triggers>
        </HierarchicalDataTemplate>
    </UserControl.Resources>
```
> 폴더 노드는 `Change==null` → `Change.IsStaged` 바인딩이 null 이라 어떤 트리거도 발화 안 함 → 액션 버튼 모두 숨김(정상). 상태 글자도 빈 문자열.

- [ ] **Step 2: 두 ItemsControl 을 TreeView 로 교체** — `Views/GitScmView.xaml`

`<ItemsControl x:Name="StagedHost"> ... </ItemsControl>` 전체를:
```xml
                <TreeView x:Name="StagedTree" Background="Transparent" BorderThickness="0"
                          ItemContainerStyle="{StaticResource ScmTreeItem}"
                          ItemTemplate="{StaticResource ScmNode}"
                          ScrollViewer.HorizontalScrollBarVisibility="Disabled"/>
```
`<ItemsControl x:Name="UnstagedHost"> ... </ItemsControl>` 전체를:
```xml
                <TreeView x:Name="UnstagedTree" Background="Transparent" BorderThickness="0"
                          ItemContainerStyle="{StaticResource ScmTreeItem}"
                          ItemTemplate="{StaticResource ScmNode}"
                          ScrollViewer.HorizontalScrollBarVisibility="Disabled"/>
```

- [ ] **Step 3: ui-lint 실행**
```powershell
pwsh scripts/ui-lint.ps1 Views/GitScmView.xaml
```
Expected: `UI 점검 통과: 위반 0`. (StatusColor 바인딩 색은 코드에서 오므로 XAML 하드코딩 아님.)

---

## Self-Review
- **스펙 커버리지:** 전체 중첩 트리(BuildTree, 압축 없음) / 폴더=이름·클릭토글(Node_Click) / 파일=상태+이름+호버액션+클릭 diff(HierarchicalDataTemplate + 트리거) / stage·unstage·discard 보존(Tag=Change) / 섹션 개수·커밋박스·동기화 헤더 불변 / 테마 DynamicResource·IconButton 재사용 — 모두 태스크 존재.
- **타입 일관성:** `ScmTreeNode`(Name/IsFolder/Children/Change/IsExpanded/Status/StatusColor), TreeView x:Name `StagedTree`/`UnstagedTree`, `Node_Click(object,MouseButtonEventArgs)`, 기존 `Stage_Click/Unstage_Click/Discard_Click`(Tag=GitChange) 재사용.
- **주의(빌드 시 확인):** ① TreeViewItem 커스텀 템플릿의 셰브론/들여쓰기·마우스오버가 의도대로 보이는지. ② `MouseLeftButtonUp` 이 액션 버튼 클릭과 겹치지 않는지(버튼이 이벤트 소비). ③ 정렬 후 ObservableCollection 재구성이 트리에 반영되는지. 어색하면 미세조정.
