# 훅 -WindowStyle Hidden → 외부 터미널 창 최소화

## 증상
DevezCode **밖에서** (독립 터미널/conhost 등) codex 세션을 직접 실행하면, **프롬프트를 보낼 때·응답이 끝날 때**
그 터미널 창이 최소화/숨김됨. DevezCode 자체 터미널 안에서는 안 보임. claude 세션은 재현 안 됨.

## 원인 (2026-07-11 진단)
1. codex 훅은 `~/.codex/hooks.json` 에 **전역** 등록(`CodexHookInstaller.InstallHooksJson`, 앱 시작 시).
   → 외부 터미널의 codex 세션에서도 `UserPromptSubmit`(프롬프트)·`Stop`(응답 완료) 시 훅이 발화.
2. 훅 명령이 `powershell ... -WindowStyle Hidden -File hook.ps1` 이었다.
3. **codex(Rust)는 훅 프로세스를 부모 콘솔을 상속(inherit)해 스폰**한다 → 자식 powershell 이 외부 터미널의
   **실제 콘솔 창을 공유**. `-WindowStyle Hidden` 은 powershell 시작 시 `ShowWindow(GetConsoleWindow(), SW_HIDE)`
   를 호출하는데, 이 HWND 가 **공유 콘솔(= 외부 터미널 창)** 이라 그 창이 통째로 숨김/최소화된다.
4. 증상이 딱 두 시점(전송·완료)에만 나는 것 = `UserPromptSubmit`/`Stop` 두 훅 이벤트와 정확히 일치.

## claude 는 왜 안 그런가 (대조)
- claude(Node CLI)는 훅/statusLine 자식을 **`windowsHide`(CREATE_NO_WINDOW)** 로 스폰 → 자식에 콘솔이 아예 없음
  → `GetConsoleWindow()` 가 NULL → `-WindowStyle Hidden` 의 `ShowWindow` 가 **no-op** → 부모 창 안 건드림.
- 즉 "전역 설치라서" 가 아니다. claude 도 statusLine 을 `~/.claude/settings.json` 에 **전역** 설치하고 같은
  `-WindowStyle Hidden` 을 쓴다(`UserStatusLineInstaller`). 차이는 **자식 스폰 방식**(콘솔 상속 vs windowsHide)뿐.
- claude 방별 busy/statusLine 훅은 `--settings` 로 세션마다 주입(`TerminalSessionManager.BuildRoomSettings`) —
  전역이 아니지만, 여기서도 스폰 방식 덕에 무해.

## 수정
- `CodexHookInstaller.BuildHookCommand` 와 `GrokHookInstaller.InstallHooksJson` 의 훅 명령에서
  **`-WindowStyle Hidden` 제거**. 콘솔을 상속하므로 이걸 빼도 **새 창이 안 뜨어 flash 없음**, ConPTY(헤드리스)
  에서도 창이 없어 무해. (commit `a0c57f7`)
- 반영 조건: **새 빌드를 한 번 실행**해야 `InstallHooksJson` 이 `~/.codex/hooks.json` 을 새 명령으로 다시 쓴다.
  기존 PC 는 그전까지 옛 `-WindowStyle Hidden` 명령이 남아 있음.

## 재발 방지
- **콘솔을 상속해 스폰하는 CLI(codex/grok 등)의 전역 훅 명령에 `-WindowStyle Hidden` 절대 금지.**
  숨겨야 할 새 창이 애초에 없고, 있으면 부모(외부 터미널)를 숨긴다.
- claude 계열은 `windowsHide` 로 스폰돼 Hidden 이 무해하므로 건드리지 말 것(빼면 오히려 flash 재발 위험).
- 새 에이전트 훅 추가 시 `.knowledge/에이전트추가규칙.md` 와 함께 이 문서 참고.

## 진단 팁
- "외부에서만 창 최소화" + "특정 두 시점" → 전역 훅 + `-WindowStyle Hidden` + 콘솔 상속 스폰 3박자.
- `~/.codex/hooks.json` 의 `command` 에 `-WindowStyle Hidden` 이 있는지 확인.
- DevezCode 안에서 안 보이는 이유: ConPTY 는 헤드리스 conhost(실제 HWND 없음)라 `SW_HIDE` 가 무효.
