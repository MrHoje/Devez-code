# VS Code 스타일 diff/SCM Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Monaco diff를 VS Code식(스크롤바·오버뷰 룰러 빨강/초록)으로, GitScmView를 VS Code SCM식(동기화 헤더·Ctrl+Enter 커밋·섹션 개수)으로 다듬는다.

**Architecture:** Monaco 시각은 `bridge.js` 옵션 + `MonacoThemePayload` 색 추가로(내장 오버뷰 룰러/스크롤바 테마). SCM 패널은 `GitScmView` XAML/코드비하인드 리스타일. 테마는 기존 `App.ThemeChanged`/`DynamicResource` 유지.

**Tech Stack:** WPF, WebView2 Monaco, git CLI.

## Global Constraints

- **빌드하지 않는다(사용자 지시).** 검증 = `scripts/ui-lint.ps1` + 사용자 빌드/육안.
- 모든 WPF 색 `DynamicResource`, 폰트 `PretendardFont`/`Fs*`. 하드코딩 색 금지(예외: diff 상태색 `#3FB950`/`#F85149`/`#D29922`).
- Monaco 색은 `MonacoThemePayload.Current()` 에만 추가(App.ThemeChanged 로 자동 재적용).
- 두 태스크는 파일이 겹치지 않아 병렬 가능(T1: bridge.js/MonacoThemePayload · T2: GitScmView).
- 각 태스크 끝 커밋. push 금지.

## File Structure
- `Resources/Monaco/web/bridge.js` (수정) — createDiffEditor 옵션.
- `Views/MonacoThemePayload.cs` (수정) — 스크롤바/오버뷰 룰러 색 + HexA 헬퍼.
- `Views/GitScmView.xaml` (수정) — 동기화 헤더, 섹션 개수 헤더, 커밋박스/버튼.
- `Views/GitScmView.xaml.cs` (수정) — BranchText/카운트, Ctrl+Enter, UpdateButtons.

---

### Task 1: Monaco diff 비주얼 (스크롤바 + 오버뷰 룰러)

**Files:**
- Modify: `Views/MonacoThemePayload.cs`
- Modify: `Resources/Monaco/web/bridge.js`

- [ ] **Step 1: MonacoThemePayload 색 추가** — `Views/MonacoThemePayload.cs`

`Hex` 메서드 아래에 알파 파생 헬퍼 추가:
```csharp
    /// <summary>테마 브러시 색 + 2자리 알파(예: "59")로 #RRGGBBAA 생성.</summary>
    private static string HexA(string key, string alpha)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b)
        {
            var c = b.Color;
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}{alpha}";
        }
        return "#808080" + alpha;
    }
```

`colors` 딕셔너리(현재 `diffEditor.removedLineBackground` 다음)에 항목 추가:
```csharp
            ["scrollbarSlider.background"] = HexA("TextMutedBrush", "59"),
            ["scrollbarSlider.hoverBackground"] = HexA("TextMutedBrush", "80"),
            ["scrollbarSlider.activeBackground"] = HexA("TextMutedBrush", "A6"),
            ["diffEditorOverviewRuler.insertedForeground"] = "#3FB950",
            ["diffEditorOverviewRuler.removedForeground"] = "#F85149",
            ["editorOverviewRuler.border"] = "#00000000",
            ["minimap.background"] = Hex("BgBrush"),
```

- [ ] **Step 2: bridge.js createDiffEditor 옵션** — `Resources/Monaco/web/bridge.js`

기존:
```js
          editor = monaco.editor.createDiffEditor(document.getElementById('c'), {
            readOnly: true, automaticLayout: true, renderSideBySide: true,
            minimap: { enabled: true }, scrollBeyondLastLine: false
          });
```
교체:
```js
          editor = monaco.editor.createDiffEditor(document.getElementById('c'), {
            readOnly: true, automaticLayout: true, renderSideBySide: true,
            minimap: { enabled: true }, scrollBeyondLastLine: false,
            renderOverviewRuler: true,          // 좌/우 오버뷰 룰러(변경 위치 빨강/초록)
            renderMarginRevertIcon: false,      // 읽기전용 — revert 아이콘 숨김
            scrollbar: { verticalScrollbarSize: 14, horizontalScrollbarSize: 14, useShadows: false }
          });
```

- [ ] **Step 3: 커밋** (bridge.js 변경으로 diff.html ?v 캐시버스터가 자동 갱신됨)
```bash
git add Views/MonacoThemePayload.cs Resources/Monaco/web/bridge.js
git commit -m "feat(monaco): VS Code식 스크롤바 + diff 오버뷰 룰러 색"
```

---

### Task 2: GitScmView VS Code SCM 스타일

**Files:**
- Modify: `Views/GitScmView.xaml`
- Modify: `Views/GitScmView.xaml.cs`

**Interfaces:** 기존 `PullBtn`/`PushBtn`/`FetchBtn`/`CommitBtn`/`MsgBox` 이름 유지. 신규 `BranchText`/`StagedHeader`/`ChangesHeader` x:Name.

- [ ] **Step 1: 섹션 헤더에 x:Name 부여** — `Views/GitScmView.xaml`

`<TextBlock Text="Staged Changes" Margin="6,6,0,2"` → `<TextBlock x:Name="StagedHeader" Text="Staged Changes" Margin="6,6,0,2"`
`<TextBlock Text="Changes" Margin="6,10,0,2"` → `<TextBlock x:Name="ChangesHeader" Text="Changes" Margin="6,10,0,2"`

- [ ] **Step 2: 하단 Border(Grid.Row=1) 내용 교체** — `Views/GitScmView.xaml`

현재 `<Border Grid.Row="1" ...>` 안의 `<StackPanel> ... </StackPanel>` 전체를 아래로 교체(동기화 헤더 추가, 커밋박스 플레이스홀더 변경, 버튼을 아이콘/커밋으로 재배치):
```xml
            <StackPanel>
                <!-- 동기화 헤더: 브랜치 + ↓N ↑N + pull/push/fetch 아이콘 -->
                <Grid Margin="2,0,0,6">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="⎇" VerticalAlignment="Center"
                               Foreground="{DynamicResource TextMutedBrush}" FontSize="{DynamicResource Fs13}"/>
                    <TextBlock x:Name="BranchText" Grid.Column="1" Margin="6,0,0,0" VerticalAlignment="Center"
                               TextTrimming="CharacterEllipsis"
                               Foreground="{DynamicResource TextBrush}" FontSize="{DynamicResource Fs12}"/>
                    <Button x:Name="PullBtn" Grid.Column="2" Content="↓" Click="Pull_Click"
                            Style="{StaticResource IconButton}" Width="24" Height="24" ToolTip="pull"/>
                    <Button x:Name="PushBtn" Grid.Column="3" Content="↑" Click="Push_Click" Margin="2,0,0,0"
                            Style="{StaticResource IconButton}" Width="24" Height="24" ToolTip="push"/>
                    <Button x:Name="FetchBtn" Grid.Column="4" Content="⟳" Click="Fetch_Click" Margin="2,0,0,0"
                            Style="{StaticResource IconButton}" Width="24" Height="24" ToolTip="fetch"/>
                </Grid>

                <!-- 커밋 메시지 카드 -->
                <Border Background="{DynamicResource BgBrush}"
                        BorderBrush="{DynamicResource LineBrush}" BorderThickness="1"
                        CornerRadius="10" Padding="10,6">
                    <Grid>
                        <TextBox x:Name="MsgBox" BorderThickness="0" Background="Transparent"
                                 Foreground="{DynamicResource TextBrush}" CaretBrush="{DynamicResource TextBrush}"
                                 FontFamily="{StaticResource PretendardFont}" FontSize="{DynamicResource Fs12}"
                                 MaxHeight="80" TextWrapping="Wrap" AcceptsReturn="True"
                                 VerticalScrollBarVisibility="Auto"
                                 TextChanged="MsgBox_TextChanged" PreviewKeyDown="MsgBox_PreviewKeyDown"/>
                        <TextBlock x:Name="MsgPlaceholder" Text="메시지 (Ctrl+Enter로 커밋)" IsHitTestVisible="False"
                                   VerticalAlignment="Center" Margin="1,0,0,0"
                                   Foreground="{DynamicResource TextMutedBrush}"
                                   FontFamily="{StaticResource PretendardFont}" FontSize="{DynamicResource Fs12}">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
                                    <Setter Property="Visibility" Value="Collapsed"/>
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Text, ElementName=MsgBox}" Value="">
                                            <Setter Property="Visibility" Value="Visible"/>
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                    </Grid>
                </Border>

                <!-- 커밋 버튼(전체 폭) -->
                <Button x:Name="CommitBtn" Content="✓ 커밋" Click="Commit_Click" Margin="0,6,0,0"
                        Style="{StaticResource PrimaryButton}" Height="30" IsEnabled="False"
                        HorizontalAlignment="Stretch" FontSize="{DynamicResource Fs12}"/>
            </StackPanel>
```

- [ ] **Step 3: UpdateButtons 교체 + Ctrl+Enter 핸들러** — `Views/GitScmView.xaml.cs`

`UpdateButtons()` 를 아래로 교체(PushBtn.Content 설정 제거 — 이제 아이콘, 카운트는 BranchText로):
```csharp
    private void UpdateButtons()
    {
        CommitBtn.IsEnabled = !_busy && _staged.Count > 0 && !string.IsNullOrWhiteSpace(MsgBox.Text);
        PushBtn.IsEnabled = PullBtn.IsEnabled = FetchBtn.IsEnabled = !_busy && _branch.Branch != null;

        StagedHeader.Text = $"Staged Changes {_staged.Count}";
        StagedHeader.Visibility = _staged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesHeader.Text = $"Changes {_unstaged.Count}";
        ChangesHeader.Visibility = _unstaged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var parts = new System.Collections.Generic.List<string>(2);
        if (_branch.Behind > 0) parts.Add($"↓{_branch.Behind}");
        if (_branch.Ahead > 0) parts.Add($"↑{_branch.Ahead}");
        BranchText.Text = _branch.Branch == null
            ? "(git 저장소 아님)"
            : _branch.Branch + (parts.Count > 0 ? "  " + string.Join(" ", parts) : "");
    }

    private void MsgBox_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            if (CommitBtn.IsEnabled) Commit_Click(CommitBtn, new RoutedEventArgs());
        }
    }
```
(`using System.Windows.Input;` 은 이미 있음.)

- [ ] **Step 4: ui-lint 실행**
```powershell
pwsh scripts/ui-lint.ps1 Views/GitScmView.xaml
```
Expected: `UI 점검 통과: 위반 0`.

- [ ] **Step 5: 커밋**
```bash
git add Views/GitScmView.xaml Views/GitScmView.xaml.cs
git commit -m "feat(git): VS Code SCM식 동기화 헤더 + Ctrl+Enter 커밋 + 섹션 개수"
```

---

## Self-Review 결과

- **스펙 커버리지:** 스크롤바(T1 scrollbarSlider.* + bridge scrollbar opts) / 오버뷰 룰러 빨강·초록(T1 diffEditorOverviewRuler.*) / diff 패널 톤(T1 renderMarginRevertIcon·border) / 커밋박스 Ctrl+Enter·✓커밋(T2) / 동기화 헤더 브랜치+↓↑+pull/push/fetch(T2) / 섹션 개수(T2) / 테마(색 전부 payload·DynamicResource) — 모두 태스크 존재.
- **플레이스홀더:** 없음(모든 코드 스텝 실제 코드).
- **타입 일관성:** `PullBtn/PushBtn/FetchBtn/CommitBtn/MsgBox` 기존 이름 유지, 신규 `BranchText/StagedHeader/ChangesHeader`, `MsgBox_PreviewKeyDown` 핸들러 XAML↔코드 일치, `Commit_Click(object,RoutedEventArgs)` 시그니처로 호출.
- **주의:** `IconButton` 스타일에 텍스트 글리프(↓↑⟳) content — Path 대신 문자열이라도 ContentPresenter로 렌더됨. 육안 확인 시 정렬/크기 어색하면 글리프 FontSize만 조정.
