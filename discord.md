# DevezCode Discord 연동 스펙

## 목적

DevezCode를 실행 중인 PC와 사용자가 접근하는 PC/모바일이 서로 다른 인터넷 망에 있어도 Discord를 중계 채널로 사용해 프로젝트별 세션 상태를 확인하고, 세션별로 메시지를 주고받을 수 있게 한다.

핵심 구조는 다음과 같다.

```text
DevezCode PC
  DevezCode.exe
    DiscordBotService
      outbound WebSocket
        Discord Gateway
          Discord 서버/채널/스레드
```

DevezCode PC는 외부에서 들어오는 포트를 열지 않는다. Discord Bot이 Discord Gateway로 outbound WebSocket 연결을 유지하고, 사용자는 Discord 앱에서 메시지를 보내거나 응답을 확인한다.

## 구현 브랜치/커밋

구현은 `feature/discord-integration` worktree에서 진행했고, 원격 `main`에 fast-forward push했다.

적용 커밋:

1. `cceaafd feat: Discord 연동 설정 저장소 추가`
2. `d9a57e0 feat: Discord 세션 브리지 추가`
3. `c6b1195 feat: Discord 설정 UI 추가`

## 사용 라이브러리

`DevezCode.csproj`에 다음 NuGet 패키지를 추가했다.

```xml
<PackageReference Include="Discord.Net" Version="3.20.1" />
```

선택 이유:

- .NET 9 안정 버전 지원
- WPF 앱에서 별도 ASP.NET/Host 없이 `DiscordSocketClient`를 직접 실행 가능
- Gateway 이벤트, 메시지 수신, 채널/카테고리/스레드 생성 API가 안정적
- Discord의 outbound WebSocket 구조가 서로 다른 망 연결 요구에 맞음

## 설정 저장소

`Services/SettingsService.cs`에 Discord 연동 설정을 추가했다.

저장 위치는 기존과 동일하게 `%AppData%\DevezCode\settings.json`이다.

추가된 설정:

```csharp
public bool DiscordEnabled { get; set; }
public string DiscordBotToken { get; set; } = "";
public ulong DiscordGuildId { get; set; }
public bool DiscordNotifySessionDone { get; set; } = true;
public Dictionary<string, ulong> DiscordProjectCategories { get; set; } = new();
public Dictionary<string, ulong> DiscordProjectChannels { get; set; } = new();
public Dictionary<string, ulong> DiscordSessionThreads { get; set; } = new();
```

의미:

- `DiscordEnabled`: Discord 연동 활성화 여부
- `DiscordBotToken`: Discord Bot Token
- `DiscordGuildId`: 연결 대상 Discord 서버 ID
- `DiscordNotifySessionDone`: 세션 응답 완료 시 Discord 스레드에도 알림 전송 여부
- `DiscordProjectCategories`: 프로젝트 경로 → Discord Category ID 매핑
- `DiscordProjectChannels`: 프로젝트 경로 → Discord text channel ID 매핑
- `DiscordSessionThreads`: 세션 ID → Discord thread ID 매핑

토큰이 비어 있거나 `DiscordEnabled=false`이면 Bot은 시작하지 않는다.

## 설정 UI

`Views/SettingsDialog.xaml`과 `Views/SettingsDialog.xaml.cs`에 `Discord` 설정 카테고리를 추가했다.

설정 항목:

- Discord 연동 사용 토글
- Bot Token 입력
- Guild ID 입력
- 세션 완료 Discord 알림 토글
- 연결 다시 시작 버튼

설정 변경은 즉시 `SettingsService`에 저장된다. 토큰이나 서버 ID 변경 후에는 `연결 다시 시작` 버튼으로 런타임 Bot 연결을 재시작할 수 있다.

## Discord 구조 매핑

DevezCode 내부 구조와 Discord 구조는 다음과 같이 매핑된다.

```text
ProjectItem.Path / ProjectItem.Name
  -> Discord Category
  -> Discord text channel: sessions

SessionItem.Id / SessionItem.Name
  -> Discord Thread
```

예시:

```text
Discord 서버
└── Category: devez-code
    └── Channel: sessions
        ├── Thread: 세션 1
        ├── Thread: 세션 2
        └── Thread: codex 작업
```

현재 구현은 프로젝트별로 Category를 만들고, 그 아래 `sessions` text channel을 만든 뒤, 세션별 Thread를 생성한다.

## Bot 생명주기

`MainWindow.xaml.cs`에서 싱글톤 서비스로 연결한다.

```csharp
private readonly DiscordBotService _discordBot = DiscordBotService.Instance;
```

초기화:

```csharp
_discordBot.SetProjects(_projects);
```

앱 로드 시:

```csharp
_discordBot.Start();
```

앱 종료 시:

```csharp
_discordBot.Dispose();
```

Bot은 DevezCode 프로세스와 같은 생명주기를 가진다. DevezCode가 종료되면 Discord Bot 연결도 종료된다.

## 메시지 송수신 흐름

### Discord → DevezCode

1. 사용자가 Discord 세션 Thread에 메시지를 작성한다.
2. `DiscordBotService.OnMessageReceived`가 메시지를 수신한다.
3. Thread ID로 `SettingsService.FindDiscordSessionByThread`를 호출해 DevezCode 세션 ID를 찾는다.
4. `TerminalSessionManager.Instance.Get(sessionId)`로 실행 중인 ConPTY 세션을 찾는다.
5. `TerminalSession.Write(content)`와 `TerminalSession.Write("\r")`로 터미널 stdin에 전달한다.

흐름:

```text
Discord Thread 메시지
  -> DiscordBotService
  -> SettingsService DiscordSessionThreads 역조회
  -> TerminalSessionManager.Get(sessionId)
  -> TerminalSession.Write(message + Enter)
```

세션이 실행 중이 아니면 Discord Thread에 경고 메시지를 보낸다.

### DevezCode → Discord

1. `TerminalHostView.WireSession`에서 `TerminalSession.OutputReceived`가 발생한다.
2. 기존 xterm.js 출력 처리와 별도로 `DiscordBotService.Instance.ForwardTerminalOutput(roomId, bytes)`를 호출한다.
3. `DiscordBotService`가 ANSI escape sequence를 제거한다.
4. 짧은 시간 동안 출력 chunk를 버퍼링한다.
5. 대응되는 Discord Thread에 코드 블록 형태로 전송한다.

흐름:

```text
ConPTY stdout bytes
  -> TerminalSession.OutputReceived
  -> TerminalHostView
  -> DiscordBotService.ForwardTerminalOutput
  -> Discord Thread message
```

출력은 Discord 메시지 길이 제한을 피하기 위해 최근 출력 기준으로 잘라 보낸다.

## 세션 완료 알림

기존 DevezCode는 busy 상태가 `true -> false`가 될 때 응답 완료 토스트를 표시한다.

`MainWindow.NotifyIfSessionFinished`에 Discord 알림 호출을 추가했다.

```csharp
_ = _discordBot.NotifySessionDoneAsync(proj, s);
```

전송 내용:

- 프로젝트명
- 세션명
- 마지막 사용자 메시지 일부
- 응답 완료 표시

설정의 `DiscordNotifySessionDone`이 꺼져 있으면 Discord 완료 알림은 보내지 않는다.

## 지원 명령어

현재 Bot은 일반 Thread 메시지 릴레이 외에 간단한 prefix 명령을 지원한다.

### `!dc status`

현재 DevezCode에 등록된 프로젝트/세션 상태를 Discord 메시지로 출력한다.

표시 항목:

- 프로젝트명
- 세션명
- 에이전트 ID (`claude`, `codex`, `opencode`, `gajae` 등)
- 상태 (`작업중`, `실행중`, `중지`)

### `!dc list`

`!dc status`와 동일한 상태 목록을 출력한다.

### `!dc sync`

현재 DevezCode workspace를 기준으로 Discord Category/Channel/Thread 매핑 생성을 다시 시도한다.

## Discord Developer Portal 설정

Discord Bot이 메시지 내용을 읽으려면 Discord Developer Portal에서 다음 설정이 필요하다.

1. Application 생성
2. Bot 생성
3. Bot Token 복사
4. Privileged Gateway Intents에서 `Message Content Intent` 활성화
5. Bot을 대상 서버에 초대
6. DevezCode 설정 > Discord에 Bot Token과 Guild ID 입력
7. Discord 연동 사용 토글 켜기
8. 연결 다시 시작 클릭 또는 DevezCode 재시작

필요 권한:

- View Channels
- Send Messages
- Read Message History
- Create Public Threads
- Send Messages in Threads
- Manage Threads 또는 Thread 생성/참여에 필요한 권한
- Manage Channels 권한이 있으면 Category/Channel 자동 생성 가능

## 네트워크 요구사항

필요한 것은 outbound 인터넷 연결뿐이다.

허용되어야 하는 대상:

- `discord.com`
- `gateway.discord.gg`
- HTTPS / WSS outbound

필요하지 않은 것:

- 포트포워딩
- 고정 IP
- inbound 방화벽 오픈
- 별도 서버
- 외부 DB

## 보안 주의사항

현재 Bot Token은 로컬 설정 파일 `%AppData%\DevezCode\settings.json`에 저장된다.

주의:

- 이 파일을 Git에 커밋하지 말 것
- Bot Token을 로그/스크린샷/채팅에 노출하지 말 것
- Bot 권한은 필요한 서버와 채널로 제한할 것
- 가능하면 전용 Discord 서버 또는 전용 카테고리를 사용할 것

현재 구현은 사용자별 권한 검증을 세밀하게 하지 않는다. 즉, Bot이 접근 가능한 Thread에 메시지를 쓸 수 있는 Discord 사용자는 해당 DevezCode 세션에 입력을 보낼 수 있다.

운영 권장:

- 개인 서버 또는 제한된 private channel에서 사용
- Bot 초대 권한 최소화
- Discord 서버 권한으로 접근자를 제한

## 현재 범위

구현 완료:

- Discord.Net 의존성 추가
- Discord 설정 저장
- Discord 설정 UI
- Discord Gateway 연결/해제
- 프로젝트별 Category 생성
- 프로젝트별 `sessions` text channel 생성
- 세션별 Thread 생성
- Thread 메시지 → 세션 터미널 stdin 전달
- 세션 터미널 stdout → Thread 전송
- 세션 완료 알림 Thread 전송
- `!dc status`, `!dc list`, `!dc sync`

아직 미구현 또는 후속 개선 대상:

- Slash command 등록
- Rich Presence
- 파일 업로드/다운로드 브리지
- Discord 첨부파일을 세션 입력으로 전달
- Discord 사용자별 권한 allowlist
- 프로젝트 삭제 시 Discord Category 자동 정리
- 세션 삭제 시 Thread archive/lock 처리
- 긴 출력의 더 정교한 chunking
- Bot 연결 상태 표시 배지
- 연결 테스트 버튼의 상세 성공/실패 표시

## 빌드 확인

구현 후 다음 명령으로 Release 빌드를 확인했다.

```powershell
taskkill /IM DevezCode.exe /F 2>$null
dotnet build -c Release --nologo -v quiet
Start-Process "bin\DevezCode.exe"
```

결과:

- 오류 0개
- 기존 경고만 존재
- 빌드 성공 후 앱 재시작 완료

## 운영 흐름 예시

1. DevezCode PC에서 DevezCode 실행
2. 설정 > Discord에서 토큰/Guild ID 입력 후 활성화
3. `연결 다시 시작` 클릭
4. Discord 서버에 프로젝트별 Category와 `sessions` 채널 생성
5. 세션 Thread 생성
6. 사용자가 Thread에 메시지 입력
7. DevezCode의 해당 세션 터미널에 메시지 전달
8. Claude/Codex/OpenCode/Gajae 응답이 Thread로 스트리밍
9. 응답 완료 시 완료 알림 메시지 전송

## 설계상 중요한 결정

- Slack이 아니라 Discord를 선택했다.
- 이유는 개인/소규모 개발 도구에 맞고, Bot Token 기반 설정이 단순하며, Gateway WebSocket이 서로 다른 망 연결에 적합하기 때문이다.
- 별도 서버/DB를 두지 않는다.
- DevezCode의 기존 로컬 저장 원칙을 유지한다.
- Discord 연동은 기본 비활성화이며, 사용자가 설정해야만 시작된다.
- 터미널 세션 자체는 기존 `TerminalSessionManager`와 `TerminalSession`을 그대로 사용한다.
- Discord는 원격 UI/중계 계층이고, 세션의 실제 상태와 실행은 DevezCode PC에 남는다.
