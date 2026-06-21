# DevezCode

Claude Code CLI를 GUI로 감싸는 WPF 데스크톱 앱. Windows Terminal(ConPTY) 기반 임베디드 터미널에서 `claude`를 직접 실행한다.

## 화면 구성 (3-pane)

- **좌측** — 프로젝트(디렉터리) 트리. 각 프로젝트 하위에 세션 목록.
- **중앙** — 세션 탭 + 터미널 영역. 세션을 열면 해당 프로젝트 디렉터리에서 Claude Code가 자동 실행된다..
- **우측** — 선택한 프로젝트의 파일 탐색기.

디자인(테마/색상/폰트/아이콘/팝업)은 `devez` 프로젝트의 디자인 시스템을 그대로 따른다. 테마 3종(minimal/soft/dark, 기본 dark)을 타이틀바 팔레트 버튼으로 전환.

## 기술 스택

- .NET 9 (`net9.0-windows`), WPF
- 터미널: **ConPTY**(`CreatePseudoConsole`) + **xterm.js**(WebView2 임베드) — `devez`의 터미널 스택 이식
- Windows Terminal `settings.json`의 폰트/컬러 스킴 자동 적용 (`WtSettingsLoader`)
- 프로젝트/세션 영속: `%AppData%\DevezCode\workspace.json`

## 빌드 / 실행

```sh
dotnet build
dotnet run        # 또는 bin/Debug/DevezCode.exe
```

WebView2 런타임 필요(Windows 11 기본 설치). `claude` CLI가 PATH에 있어야 세션 실행 가능.

## 주요 파일

| 영역 | 경로 |
|------|------|
| 진입/테마 | `App.xaml(.cs)` — `SetTheme(minimal\|soft\|dark)` |
| 메인 셸 | `MainWindow.xaml(.cs)` — 3-pane + 탭 + 타이틀바 |
| 사이드바 | `Views/SidebarView.*` |
| 파일 탐색기 | `Views/FileExplorerView.*` |
| 터미널 | `Views/TerminalHostView.cs`, `Services/Terminal/*`, `Resources/Terminal/web/*` |
| 영속 | `Services/WorkspaceStore.cs`, `Services/SettingsService.cs` |
| 디자인 | `Styles/AppStyles.xaml`, `Resources/Icons.xaml`, `Fonts/*` |
