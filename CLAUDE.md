# DevezCode 작업 지침

## 최우선 규칙: 프로세스 강제 종료 금지

- **빌드·게시·배포를 위해 실행 중인 DevezCode 프로세스를 절대로 강제 종료하지 않는다.** `taskkill /F`, `Stop-Process -Force`, `Process.Kill` 등 강제 종료 수단은 사용 금지.
- 빌드 전에 현재 작업을 수행하는 **본 세션 자체가 DevezCode 내부에서 실행 중인지** 반드시 확인한다.
- 본 세션이 DevezCode 내부에서 실행 중이면 앱 종료로 본 세션이 끊길 수 있으므로, 사용자의 명시적 요청 없이 임의로 앱을 종료하거나 빌드·게시·배포·재시작하지 않는다.
- 빌드가 필요하면 먼저 정상 종료를 요청하고 프로세스가 완전히 종료된 것을 확인한 뒤 빌드한다.
- 정상 종료가 되지 않거나 제한 시간 내에 끝나지 않으면 강제 종료하거나 빌드를 진행하지 말고, 작업을 중단한 뒤 사용자에게 알린다.
- 이 규칙은 아래의 모든 빌드·재시작·배포 절차보다 우선한다.

## 동기화 규칙

- `AGENTS.md`와 `CLAUDE.md`는 동일한 규칙 문서로 유지한다.
- 어느 한 쪽을 수정하면 즉시 다른 쪽도 동일하게 갱신한다.

## 빌드 및 재시작 프로세스

코드 변경 후 항상 다음 프로세스를 따르십시오:

```powershell
# 1. 실행 중인 앱에 정상 종료를 요청하고 완전히 종료될 때까지 대기.
$process = Get-Process -Name DevezCode -ErrorAction SilentlyContinue
if ($process) {
    $process.CloseMainWindow() | Out-Null
    if (-not $process.WaitForExit(30000)) {
        throw "DevezCode가 정상 종료되지 않아 빌드를 중단합니다. 강제 종료하지 마십시오."
    }
}

# 2. Release 빌드.
dotnet build -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Release 빌드가 실패했습니다." }

# 3. 빌드가 성공한 경우에만 앱 재시작.
Start-Process "bin\DevezCode.exe"
```

빌드가 실패하면 앱을 재시작하지 마십시오. 먼저 빌드 오류를 수정하십시오.
오류의 원인이 직접 수정하지 않은 파일이라면 병행 세션이 수정 중일 수 있으니 빌드를 멈추고 대기.

**코드 수정이 끝나면(devez 참고 여부와 무관하게) 커밋 후 푸시한다.** (빌드 성공 확인 후 commit → push)

## 배포 규칙

- **사용자가 "배포/업데이트 올려/릴리스/새 버전 내보내" 등 배포를 요청하면 반드시 먼저 `배포.md` 를 읽고
  그 전체 절차(0→5단계)를 순서대로 따른다.** 코드 커밋·푸시만 하고 끝내지 말 것.
- 배포 = **R2 릴리스**(버전 올림 → `dotnet publish` → zstd 델타 → R2 업로드 → version.json)까지다.
  위 "빌드 및 재시작 프로세스"(로컬 실행 확인용)와 **혼동 금지** — 그건 배포가 아니다.
- 첫 단계는 항상 **업데이트 노트 초안 작성 후 사용자 승인**. 승인 없이 버전을 올리거나 업로드하지 않는다.

## 지식베이스

- **"지식베이스에 저장해"라고 하면** 해당(또는 방금 작업한) 내용 중 다음에 참조 가치가 있는 것을
  `.knowledge/` 폴더에 마크다운 문서로 저장한다. 이미 같은 주제의 문서가 있으면 새로 만들지 말고
  그 문서를 갱신한다.
- `.knowledge/*.md` 는 항상 다 읽지 말고, 아래 인덱스에서 **작업에 해당하는 파일만 골라 읽는다.**
  새 문서를 추가하면 이 인덱스에 한 줄 추가한다.

### 인덱스 (이럴 때 → 이 파일을 읽는다)

| 이런 작업을 할 때 | 읽을 파일 |
|---|---|
| 텍스트 표시/입력 컨트롤 추가 (한글 글자 깨짐 방지) | `.knowledge/텍스트렌더링규칙.md` |
| 버튼·카드 등 클릭 가능 컨트롤 추가 (커서=Arrow, Hand 금지) | `.knowledge/컨트롤추가규칙.md` |
| 새 AI CLI 에이전트 추가, 에이전트별 분기 수정(세션 추적/복원·재진입·종료·포크·테마·상태표시·사용량·MCP·클리너) | `.knowledge/에이전트추가규칙.md` |
| 터미널 입력/마우스/클립보드/IME/스크롤 등 커스텀 동작 | `.knowledge/터미널커스텀동작.md` |
| 중앙 2분할(Split) 패널·탭 격리·파트너·포커스·터미널 재진입 | `.knowledge/분할패널-탭격리-파트너-포커스.md` |
| 패널 리사이즈/오버레이 시 터미널 깜빡임(WebView2 airspace) | `.knowledge/webview2-airspace-패널리사이즈-깜빡임.md` |
| `Style.Setter.Value`/`Template` 인라인 자식 → connectionId 크래시 | `.knowledge/wpf-contextmenu-setter-value-connectionid-충돌.md` |
| borderless 팝업 창의 라운드 코너 밖으로 자식 사각 모서리 삐져나옴 | `.knowledge/borderless-창-라운드-코너-클립.md` |
| 프로젝트 폴더에 코드조각 이름의 0바이트 가비지 파일이 생김(원인=Claude Bash 툴, DevezCode 아님) | `.knowledge/프로젝트폴더-가비지파일-원인.md` |
| 세션이 작업 중(스피너)에 간헐적으로 멈춤/작업 유실 → diag.log 로 원인 판별 | `.knowledge/세션-작업중-멈춤-진단.md` |
| 세션 작업 끝났는데 스피너 안 꺼짐(stuck-ON), 훅이 방별 상태 파일(busy 등) 쓰기 | `.knowledge/훅-상태파일-원자적쓰기.md` |
| 외부 터미널에서 codex 세션이 프롬프트/응답 시 창 최소화(전역 훅 -WindowStyle Hidden) | `.knowledge/훅-windowstyle-hidden-외부터미널-최소화.md` |
| Claude/Codex 사용량 0%·고정·계정불일치, 예정일 전 공급자 즉시 초기화, 인증 전환·fallback·stale 처리 | `.knowledge/claude-codex-사용량-즉시초기화-인증-신선도.md` |
| 세션 헤더 토큰 사용량(입/출력/비용) 단가 변경·새 모델 추가·비용 계산 방식 | `.knowledge/토큰사용량-단가-갱신.md` |
| gjc 스피너/완료기록 안 뜸·지연, transcript 지연 flush, runtime-state 사이드카, 완료기록 쌓임 스펙 | `.knowledge/gjc-사이드카-상태추적-완료기록.md` |

> 위에 없는 일회성 버그 교훈(특정 컨트롤 트리거 등)은 `.knowledge/wpf-*.md` 로 남아 있으니, 비슷한 증상을 만나면 폴더를 이름으로 grep 해서 찾는다.

## 참고 대상 (devez)

- "devez를 참고해서"라고 하면 프로젝트 상위 폴더의 `devez`를 참고한다.
  `devez`가 없으면 `talkremind_wpf`를 참고한다.
- devez를 참고해 기능을 이식할 때, devez에서 **DB 동기화(Supabase 등)** 로 저장하던 부분은
  DevezCode 에서는 모두 **로컬 저장**으로 대체한다
  (`SettingsService` → `%AppData%\DevezCode\settings.json`, 테마는 `theme.txt`).
  DevezCode 는 서버/DB 인프라가 없으므로 클라우드 동기화 코드를 만들지 말 것.

## 참고

- 원격 접속(RDP/Chrome Remote Desktop)에서는 GPU 합성 화면이 전달되지 않아 창이 안 보일 수 있다.
  `App.OnStartup`에서 원격 세션을 감지해 `RenderMode.SoftwareOnly`를 강제하므로 새 창을 만들 때 이 처리를 빠뜨리지 말 것.
- 디자인(테마/색상/폰트/아이콘/팝업)은 `C:\source\devez`의 디자인 시스템을 따른다. 새 UI도 `AppStyles.xaml`의 전역 스타일을 사용하고 인라인 스타일을 남발하지 말 것.
- 작업별로 참조할 `.knowledge/` 문서는 위 **지식베이스 인덱스**를 본다.
