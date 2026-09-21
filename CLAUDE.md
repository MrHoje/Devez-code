# DevezCode 작업 지침

## 최우선 규칙: 프로세스 강제 종료 금지

* **빌드·게시·배포를 위해 실행 중인 DevezCode 프로세스를 절대로 강제 종료하지 않는다.** `taskkill /F`, `Stop-Process -Force`, `Process.Kill` 등 강제 종료 수단은 사용 금지.
* 빌드 전에 현재 세션의 프로세스 조상과 실행 중인 모든 `DevezCode.exe`의 `ExecutablePath`를 확인해 **설치본과 작업 트리 빌드본을 구분한다.** 단순히 본 세션이 DevezCode 내부인지 여부만으로 빌드 가능 여부를 판단하지 않는다.
* 실행 경로가 현재 저장소 밖의 설치 위치(예: `%LocalAppData%\DevezCode\DevezCode.exe`)이면 **설치본**이다. 설치본은 작업 트리의 빌드 대상을 잠그지 않으므로 종료·재시작하지 않고 빌드할 수 있다.
* 실행 경로가 현재 저장소 아래이거나 이번 빌드가 덮어쓸 산출물 경로와 같으면 **작업 트리 빌드본**이다. 이 경우에만 먼저 정상 종료를 요청하고 프로세스가 완전히 종료된 것을 확인한 뒤 빌드한다.
* 본 세션을 호스팅하는 작업 트리 빌드본은 종료하면 세션이 끊기므로 임의로 종료하지 않는다. 실행 경로를 확인할 수 없거나 설치본·빌드본 판별이 모호하면 빌드하지 말고 사용자에게 확인한다.
* 정상 종료가 되지 않거나 제한 시간 내에 끝나지 않으면 강제 종료하거나 빌드를 진행하지 말고, 작업을 중단한 뒤 사용자에게 알린다.
* 이 규칙은 아래의 모든 빌드·재시작·배포 절차보다 우선한다.

## 동기화 규칙

* `CLAUDE.md`가 단일 규칙 문서다. 규칙 변경은 이 파일에만 한다.
* `AGENTS.md`는 내용을 두지 않고 `CLAUDE.md`를 참조하기만 한다.

## 빌드 및 재시작 프로세스

코드 변경 후 항상 다음 프로세스를 따르십시오:

```powershell
# 1. 실행 중인 앱의 경로를 확인하고 설치본과 작업 트리 빌드본을 구분.
Get-CimInstance Win32_Process -Filter "Name='DevezCode.exe'" |
    Select-Object ProcessId, ParentProcessId, ExecutablePath, CommandLine

# 2. 이번 빌드 산출물을 사용하는 작업 트리 빌드본만 정상 종료 후 완전히 종료될 때까지 대기.
# 설치본과 본 세션을 호스팅하는 프로세스는 종료하거나 재시작하지 않는다.

# 3. Release 빌드.
dotnet build -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Release 빌드가 실패했습니다." }

# 4. 작업 트리 빌드본을 정상 종료했던 경우에만 빌드 성공 후 재시작.
# Start-Process "bin\DevezCode.exe"
```

빌드가 실패하면 앱을 재시작하지 마십시오. 먼저 빌드 오류를 수정하십시오.
오류의 원인이 직접 수정하지 않은 파일이라면 병행 세션이 수정 중일 수 있으니 빌드를 멈추고 대기.

**코드 수정이 끝나면(devez 참고 여부와 무관하게) 커밋 후 푸시한다.** (빌드 성공 확인 후 commit → push)

## 배포 규칙

* 🚨 **배포 요청을 받으면 그 어떤 응답·질문·코드 확인보다 먼저 `배포.md` 를 읽는다.** "배포/업데이트 올려/릴리스/새 버전 내보내/패치 배포" 등 배포로 읽히는 요청이면 예외 없이, 첫 도구 호출로 `배포.md` 를 읽고 그 지침에 따라 응답을 시작한다(커서 버그 수정 등 다른 작업이 섞여 있어도 배포.md 읽기가 먼저다).
* **`배포.md` 를 읽었으면 그 전체 절차(0→5단계)를 순서대로 따른다.** 코드 커밋·푸시만 하고 끝내지 말 것.
* 배포 = **R2 릴리스**(버전 올림 → `dotnet publish` → zstd 델타 → R2 업로드 → version.json)까지다.
    위 "빌드 및 재시작 프로세스"(로컬 실행 확인용)와 **혼동 금지** — 그건 배포가 아니다.
* 첫 단계는 항상 **업데이트 노트 초안 작성 후 사용자 승인**. 승인 없이 버전을 올리거나 업로드하지 않는다.

## 지식베이스

* **"지식베이스에 저장해"라고 하면** 해당(또는 방금 작업한) 내용 중 다음에 참조 가치가 있는 것을
    `.knowledge/` 폴더에 마크다운 문서로 저장한다. 이미 같은 주제의 문서가 있으면 새로 만들지 말고
    그 문서를 갱신한다.
* `.knowledge/*.md` 는 항상 다 읽지 말고, 아래 인덱스에서 **작업에 해당하는 파일만 골라 읽는다.**
* ⚠️ **`.knowledge/` 에 새 문서를 만들거나 파일명을 바꾸면, 같은 작업 안에서 반드시 아래 인덱스에 한 줄을 추가/수정한다.** (인덱스에 없고 `wpf-*` 접두사도 아닌 문서는 grep 안전망에도 안 걸려 영영 발견되지 않는다.)

### 인덱스 (이럴 때 → 이 파일을 읽는다)

| 이런 작업을 할 때 | 읽을 파일 |
| ---------- | ----- |
| 새 UI/UX, 테마, 색상, 폰트, 간격, 라운드, 보더, 버튼, 입력, 카드, 팝업, 탭, 아이콘, DataGrid 등 디자인 작업 | `DEVEZ_DESIGN_SYSTEM.md` |
| 텍스트 표시/입력 컨트롤 추가 (한글 글자 깨짐 방지) | `.knowledge/텍스트렌더링규칙.md` |
| 버튼·카드 등 클릭 가능 컨트롤 추가 (커서=Arrow, Hand 금지) | `.knowledge/컨트롤추가규칙.md` |
| 새 AI CLI 에이전트 추가, 에이전트별 분기 수정(세션 추적/복원·재진입·종료·포크 설정 상속·테마·상태표시·사용량·MCP·클리너) | `.knowledge/에이전트추가규칙.md` |
| 터미널 입력/마우스/클립보드/IME/스크롤 등 커스텀 동작 | `.knowledge/터미널커스텀동작.md` |
| WPF 쪽(터미널 밖) 트랙패드/휠 스크롤이 뚝뚝 끊김·정밀 델타 처리, ScrollViewer 휠 동작 커스텀 | `.knowledge/wpf-정밀휠스크롤-전역.md` |
| `.ps1`/`.cmd` 스크립트를 코드로 생성하거나 **작업용 임시 `.ps1` 을 직접 만들 때**(배포 version.json 조립 등), 한글 사용자명(`C:\Users\김이영`) PC에서만 업데이트·훅·세션추적 실패, 스크립트 안 한글 리터럴이 깨짐 | `.knowledge/생성스크립트-인코딩-한글경로.md` |
| 세션 내 텍스트 검색 요청, 터미널 스크롤백이 비어 보임, 에이전트별 스크롤/검색 동작 차이 | `.knowledge/claude-대체화면-세션내검색-불가.md` |
| 탭바에 탭 추가/생성, 드래그 재정렬, 선택 탭 하단 밑줄(seam)·그라데이션 등 탭바 UI 수정 | `.knowledge/탭바-생성-규칙.md`, `.knowledge/탭바-SelectedTabSeam-밑줄-그라데이션.md` |
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
| claude 완료카드가 한 프롬프트에 여러 장(Task 서브마다), 가짜 응답완료 알림, 턴종료 마커(done) 게이트 | `.knowledge/claude-완료카드-턴종료마커.md` |
| Grok 장시간 작업 중 스피너 중간에 꺼짐(멀티루프 턴·조기 idle) | `.knowledge/grok-장시간턴-스피너조기소등.md` |
| 같은 에이전트인데 세션별로 버그 있/없이 갈림(자동업데이트 후 옛 프로세스 잔재) | `.knowledge/에이전트-자동업데이트-세션별-증상불일치.md` |
| 자식·내부 에이전트가 부모 방의 스피너/완료기록/lastmsg/resume ID를 오염함 | `.knowledge/세션-추적-이벤트-소유권.md` |
| Claude GUI(SDK 브리지) 종료·재시작 시 대화 유실, `bridge.mjs` 수정, 앱 종료 graceful 경로, transcript flush/resume 가드 | `.knowledge/claude-gui-sdk브리지-종료-세션보존.md` |
| **"커서 관련 수정"·커서 좌표 안 맞음·입력이 깔끔하게 안 보임**, 한글 조합이 **모니터 좌상단(화면 원점)** 에 뜸, 터미널 밖 클릭→복귀로만 해결 | `.knowledge/ime-모니터좌상단-조합창-고착.md` |

> 위에 없는 일회성 버그 교훈(특정 컨트롤 트리거 등)은 `.knowledge/wpf-*.md` 로 남아 있으니, 비슷한 증상을 만나면 폴더를 이름으로 grep 해서 찾는다.

## 참고 대상 (devez)

* "devez를 참고해서"라고 하면 프로젝트 상위 폴더의 `devez`를 참고한다.
    `devez`가 없으면 `talkremind_wpf`를 참고한다.
* devez를 참고해 기능을 이식할 때, devez에서 **DB 동기화(Supabase 등)** 로 저장하던 부분은
    DevezCode 에서는 모두 **로컬 저장**으로 대체한다
    (`SettingsService` → `%AppData%\DevezCode\settings.json`, 테마는 `theme.txt`).
    DevezCode 는 서버/DB 인프라가 없으므로 클라우드 동기화 코드를 만들지 말 것.

## 참고

* 디자인 작업은 먼저 `DEVEZ_DESIGN_SYSTEM.md`를 읽고 공통 토큰·컨트롤·SVG·렌더링 규칙을 따른다. 새 UI도 `AppStyles.xaml`의 전역 스타일을 사용하고 인라인 스타일을 남발하지 말 것.
* 작업별로 참조할 `.knowledge/` 문서는 위 **지식베이스 인덱스**를 본다.
