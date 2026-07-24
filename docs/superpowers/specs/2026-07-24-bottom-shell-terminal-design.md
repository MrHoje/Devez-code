# 하단 슬라이드 터미널 패널 (에이전트 미연결 빈 터미널) — 설계

2026-07-24 승인. 푸터 버튼으로 토글하는 전역(프로젝트 무관) PowerShell 터미널 패널.

## 요구사항

- 푸터 "깨우기" 버튼 왼쪽에 토글 버튼. 누르면 중앙 세션 영역 아래에서 패널이 슬라이드 업/다운.
- 프로젝트/방과 무관한 독립 터미널 1개. 셸 = pwsh 우선, 없으면 Windows PowerShell.
- 지연 생성 + 유지: 첫 열기에 셸 생성, 닫으면 숨김만(프로세스·출력 유지), 앱 종료 시 정리.
- 시작 폴더 = 사용자 홈(%USERPROFILE%). 앱 시작 시 패널은 항상 닫힘.

## 아키텍처

### 셸 세션
- `AgentRegistry`에 `shell` pseudo-agent 추가 + `HiddenFromUI` 등록(피커·설정 비노출).
- `TerminalSessionManager.LaunchSession`에 `agent.Id == "shell"` 분기:
  `pwsh.exe -NoLogo` 직접 실행(PATH에 없으면 `powershell.exe -NoLogo` 폴백), cmd 래핑 없음.
  startDir = 사용자 홈. resume/훅/세션 추적 없음.
- 고정 roomId `devezcode-shell-terminal`, `SettingsService` 방→에이전트 매핑에 `shell` 저장.

### 격리 근거 (과거 "다른 세션 안 열림/자기 종료" 우려 대응)
- 테마 재시작(`ReloadAllSessionsForThemeOnceAsync`)은 워크스페이스 탭(`SessionItem`)만 수집 → 셸 방 미포함.
- 자동 재진입(`IsAutoReenterRoom`)은 opencode/codex/grok/antigravity/kimi 매핑만 발동 → 셸 방 무관.
- 유일한 전역 상태 `DEVEZCODE_ROOM_ID`는 UI 스레드 직렬 설정·즉시 해제 → 레이스 없음.
- 선례: `WakeTerminal`이 동일 패턴(방 시스템 밖 roomId + 단독 TerminalHostView)으로 공존 중.
- `TrySnapshotRoomSession`·workspace.json 저장/복원은 셸 방에 케이스 없음 → no-op(무해).

### UI
- 푸터 `WakeControlBtn` 왼쪽에 토글 버튼(모노 SVG 터미널 아이콘, 켜짐 상태 표시, 커서=Arrow,
  `DEVEZ_DESIGN_SYSTEM.md` 토큰 준수).
- 중앙 세션 영역 아래 새 Row: 상단 GridSplitter(높이 조절) + 전용 `TerminalHostView` 인스턴스.
  기본 높이 ~260px, 높이는 `SettingsService`에 저장.
- 슬라이드 애니메이션: 전후로 `FreezeWorkspaceTerminalsAsync(webCover:true)` → Row 높이 애니 →
  `UnfreezeWorkspaceTerminals()`. 패널 자신은 애니 완료 후 reveal.
  (근거: `.knowledge/webview2-airspace-패널리사이즈-깜빡임.md` — 리사이즈 경로는 webCover 필수,
  숨김은 `Collapsed`만 허용(`Hidden` 금지)).

### 생명주기
- 첫 토글: `PreloadTerminal(roomId)`로 지연 생성. 이후 토글은 표시/숨김만.
- 셸 `exit` 입력(세션 Exited): 패널 자동 닫힘 + 방 정리, 다음 토글에 새 셸 생성.
- 앱 종료: 기존 정리 경로에서 `DisposeRoom(purgeTracking: false)` (기본 GracefulExitPlan Ctrl+C — PowerShell 무해).

## 오류 처리
- pwsh/powershell 모두 미발견(비현실적): 토글 시 알림 후 패널 미표시.
- 세션 생성 실패: 기존 LaunchSession 오류 경로 재사용(진단 로그 + 패널 닫힘).

## 테스트 (수동)
1. 토글 → 패널 슬라이드 업, pwsh 프롬프트 표시(홈 폴더), 명령 실행 정상.
2. 닫기 → 다시 열기: 이전 출력·실행 중 작업 유지.
3. 패널 열어둔 채: 새 세션 생성 / 세션 종료 / 테마 변경 / 앱 정상 종료 전부 정상(충돌 시나리오 검증).
4. 슬라이드 중 기존 세션 터미널 무플래시.
5. `exit` 입력 → 패널 닫힘 → 재토글 시 새 셸.
6. GridSplitter 높이 조절 + 재시작 후 높이 유지(패널은 닫힌 상태로 시작).
7. 한글 IME 판정(에이전트추가규칙 §11-2): 조합 중 블록커서 깜빡임 여부로 A/B형 판정 후 필요 시 조치.
