# DevezCode

**하나의 창에서 여러 AI 코딩 CLI를 나란히 돌리는 Windows 데스크톱 워크벤치.**

터미널을 여러 개 띄우고 창을 옮겨 다니는 대신, 프로젝트·세션·파일 탐색기·임베디드 터미널을 한 화면에 모았습니다. 각 세션은 프로젝트 디렉터리에서 원하는 AI CLI를 실제로 실행합니다 — 래퍼가 아니라 진짜 CLI가 ConPTY 위에서 그대로 돕니다.

> DevezCode 소스는 [MIT License](LICENSE). Visual Studio Image Library 및 서드파티 브랜드 자산은 제외됩니다 — [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), [BRAND_ASSETS.md](BRAND_ASSETS.md) 참고.

## 지원 에이전트

PATH에서 자동 감지하며, 설치돼 있지 않으면 설정 화면의 한 줄 설치 명령으로 바로 깔 수 있습니다.

| 에이전트 | 제공자 | 세션 복원 | 상태 추적(스피너·완료기록) |
|---|---|:--:|:--:|
| Claude Code | Anthropic | ✅ | ✅ (훅) |
| Codex | OpenAI | ✅ | ✅ (훅) |
| Grok | xAI | ✅ | ✅ |
| Antigravity | Google | ✅ | ✅ (훅) |
| Kimi | Moonshot AI | ✅ | ✅ (훅) |
| OpenCode | OpenCode | ✅ | ✅ |
| Gajae Code | Gajae | ✅ | ✅ (사이드카) |

## 핵심 기능

- **멀티 에이전트** — 프로젝트마다 세션별로 다른 CLI를 골라 실행. 같은 코드베이스를 Claude·Codex·Grok로 동시에 돌려도 서로 격리됩니다.
- **세션 추적/복원** — 앱을 껐다 켜도 프로젝트·세션 목록이 그대로. 각 CLI의 네이티브 resume 방식(세션 ID·`-c`·`--last` 등)을 에이전트별로 정확히 다뤄 방마다 올바른 대화를 되살립니다.
- **작업 상태 표시** — 세션이 작업 중이면 스피너, 끝나면 완료기록 카드. 훅 또는 사이드카 상태 파일로 CLI가 실제로 바쁜지/끝났는지 판별합니다.
- **토큰 사용량 헤더** — 세션별 입·출력 토큰과 비용을 실시간 집계.
- **중앙 2분할 패널** — 터미널을 좌우로 나눠 두 세션을 동시에. 탭·포커스·터미널 재진입이 분할 상태에서도 격리됩니다.
- **파일 탐색기** — 선택한 프로젝트의 파일 트리를 우측 패널에서 바로 열람.
- **자동 업데이트** — 앱 자체(R2 델타 배포)와 각 AI CLI 모두 시작 시 최신으로 갱신.
- **테마 3종** — minimal / soft / dark (기본 dark). 타이틀바 팔레트 버튼으로 전환.

## 화면 구성 (3-pane)

- **좌측** — 프로젝트(디렉터리) 트리. 각 프로젝트 하위에 세션 목록.
- **중앙** — 세션 탭 + 터미널 영역(선택 시 2분할). 세션을 열면 해당 프로젝트 디렉터리에서 선택한 CLI가 자동 실행됩니다.
- **우측** — 선택한 프로젝트의 파일 탐색기.

## 기술 스택

- .NET 9 (`net9.0-windows`), WPF
- 터미널: **ConPTY**(`CreatePseudoConsole`) + **xterm.js**(WebView2 임베드)
- Windows Terminal `settings.json`의 폰트/컬러 스킴 자동 적용 (`WtSettingsLoader`)
- 프로젝트/세션 영속: `%AppData%\DevezCode\workspace.json`
- 로그인 자격증명은 사용자 로컬 프로필에만 저장 — 저장소에 포함하지 않음

## 빌드 / 실행

```sh
dotnet build -c Release
dotnet run          # 또는 bin/Release/DevezCode.exe
```

- **WebView2 런타임** 필요 (Windows 11 기본 설치).
- 실행하려는 AI CLI가 **PATH에 있어야** 세션을 띄울 수 있습니다(없으면 앱 안에서 설치 안내).

## 주요 파일

| 영역 | 경로 |
|------|------|
| 진입/테마 | `App.xaml(.cs)` — `SetTheme(minimal\|soft\|dark)` |
| 메인 셸 | `MainWindow.xaml(.cs)` — 3-pane + 탭 + 타이틀바 |
| 에이전트 정의 | `Services/AgentRegistry.cs` — 지원 CLI·감지·설치/업데이트 명령 |
| 사이드바 | `Views/SidebarView.*` |
| 파일 탐색기 | `Views/FileExplorerView.*` |
| 터미널 | `Views/TerminalHostView.cs`, `Services/Terminal/*`, `Resources/Terminal/web/*` |
| 영속 | `Services/WorkspaceStore.cs`, `Services/SettingsService.cs` |
| 디자인 | `Styles/AppStyles.xaml`, `Resources/Icons.xaml`, `Fonts/*` |
