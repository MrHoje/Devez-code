# 에이전트 채팅방 GUI 설계 (claude / codex)

작성일: 2026-07-22

## 1. 목표와 범위

DevezCode 안에 **채팅 메신저 형태의 새 뷰**를 만든다. 사용자는 채팅 입력창에 프롬프트를 치고,
AI 응답·도구 실행 내역이 말풍선/카드로 렌더된다. 뒤에서는 CLI 에이전트(claude, codex)를
**헤드리스 스트리밍 모드**로 상주 실행하며, 터미널(TUI) 화면은 노출하지 않는다.

- 대상 에이전트(빌드1): **claude, codex** 둘.
- 통합 위치: 기존 DevezCode 내부의 **새 뷰**(기존 세션·테마·워크스페이스 인프라 재사용).
- 표시 수준: **A+** — 답변 텍스트 + 도구 호출을 접이식 카드로(파일 수정은 `+n −m`, 클릭 시 diff/내용).
- 인증: **claude/codex 구독 로그인 그대로 사용**(API 종량과금 금지). 프로세스는 CLI 자체 인증을 승계.
- 권한: **`--permission-mode auto` 전용**으로 실행(자동 승인). per-call 승인 버튼은 범위 밖(§12).

## 2. 핵심 결정 (스파이크로 실증됨)

claude 2.1.217 실측 기준.

| 항목 | 결정 | 근거 |
|---|---|---|
| 실행 모델 | 방당 **상주 프로세스** + stream-json 양방향 | `--input-format stream-json`=realtime, 멀티턴 확인 |
| 출력 | `--output-format stream-json --verbose` | 구조화 이벤트로 A+ 렌더 |
| 인증 | 구독(CLI 로그인) — API키 미사용 | 스폰 env에서 `ANTHROPIC_API_KEY` 제거로 강제 |
| 권한 | `--permission-mode auto` | `auto`/`acceptEdits`에서 편집 자동승인·파일생성 성공 확인 |
| 승인 버튼 | **미지원(범위 밖)** | `manual`은 control_request 미발생(0개), `--permission-prompt-tool` 없음. SDK만 가능하나 SDK=API과금 |
| SDK 사용 | **안 함** | Agent SDK는 Pro/Max 구독 불허(정책), API키 강제 |

## 3. 아키텍처

```
ChatRoomView (WebView2 HTML 채팅 UI)
    │  사용자 입력 / 승인·취소 / 이미지 첨부
    ▼
AgentChatSession (방당 1개, 상주 프로세스 래퍼)
    │  stdin: stream-json user 메시지 / stdout: JSONL 이벤트
    ├── IAgentStreamAdapter
    │     ├── ClaudeStreamAdapter   (검증됨)
    │     └── CodexStreamAdapter    (구현 시 codex 스파이크로 확정)
    ▼
공통 ChatEvent 모델  →  UI 렌더러(말풍선/카드 매핑)
```

- **AgentChatSession**: `System.Diagnostics.Process`로 CLI를 상주 실행(리다이렉트 stdin/stdout).
  대화형 TUI가 아니므로 **ConPTY 불필요**. 기존 `TerminalSession`과 별개 경로.
- **IAgentStreamAdapter**: 에이전트별 커맨드라인 구성 + JSONL → `ChatEvent` 정규화.
  (에이전트별 분기는 `.knowledge/에이전트추가규칙.md` 패턴 준수.)
- **ChatRoomView**: 렌더는 WebView2 HTML 페이지(기존 markdown 렌더러 재사용 → 답변 마크다운·코드 하이라이트).

## 4. 실행/프로토콜 상세

### claude (검증됨)
```
claude -p --input-format stream-json --output-format stream-json --verbose \
       --permission-mode auto [--allowedTools "..."] [--resume <session_id>]
```
- 프로세스 상주. 매 사용자 턴마다 stdin에 한 줄 JSON:
  ```json
  {"type":"user","message":{"role":"user","content":"<사용자 메시지>"}}
  ```
- 이미지: content를 블록 배열로(텍스트 + image 블록). 세부 형식은 구현 시 확정.
- stdout 이벤트(관측됨): `system/init`(session_id·tools), `assistant`(text/tool_use),
  `user`(tool_result), `rate_limit_event`, `result`(result·usage·total_cost_usd·num_turns),
  `system/hook_*`.
- session_id는 모든 이벤트에 포함 → `init`에서 캡처해 방에 저장.

### codex (구현 시 스파이크로 확정)
- 후보: `codex exec "<msg>" --json --sandbox workspace-write [-i <img>]`, 이어가기 `codex exec resume <id> --json`.
- 상주 스트리밍은 `codex proto`가 후보이나 **미검증**. 코덱스 어댑터 착수 시 30분 스파이크로
  (a) 멀티턴 이어가기 (b) --json 이벤트 스키마 (c) 자동승인 sandbox 동작을 확정한 뒤 구현.

## 5. 이벤트 → UI 매핑 (A+)

| ChatEvent | 화면 |
|---|---|
| 사용자 입력 | 오른쪽 말풍선 |
| assistant text | 왼쪽 AI 말풍선(markdown 렌더) |
| tool_use (Write/Edit) | 접이식 카드 `🔧 Edit <파일> (+n −m)` — 클릭 시 diff/내용 |
| tool_use (Bash/기타) | 접이식 카드 `🔧 <도구> <요약>` |
| tool_result | 해당 카드에 성공/실패 표시 |
| rate_limit_event | 헤더에 구독 한도 상태(선택) |
| result | 완료 마커 + 토큰 usage(기존 `SessionUsageService` 연동) |

- 스트리밍 델타(`content_block_delta`)로 답변을 **토큰 단위 라이브 렌더**(타이핑 효과).
- 작업 중 스피너 표시, **Stop** 버튼 = 진행 턴 취소(§10).

## 6. 세션 / 영속

- 방 생성 시 새 session_id 없음 → 첫 응답 `init`에서 캡처 → **WorkspaceStore에 방↔session_id 저장**.
- 앱 재시작·방 재진입 시 저장된 id로 `--resume` → 대화 이어짐.
- 기존 대화형(터미널) 세션 id로 이어받기(import)는 범위 밖(§12).

## 7. 환경 위생 (필수)

스폰 env에서 반드시 제거(기존 `TerminalSession` 선례 준수):
- `ANTHROPIC_API_KEY` — 있으면 구독 무시하고 **API 종량과금으로 샘**. 제거해 구독 강제.
- `CLAUDECODE`, `CLAUDE_CODE_CHILD_SESSION`, `CLAUDE_CODE_ENTRYPOINT`,
  `CLAUDE_CODE_SESSION_ID`, `CLAUDE_CODE_SSE_PORT` — 중첩 세션 판정 방지(미제거 시 transcript
  미영속 → resume 불가).
- 색/테마 env는 채팅 UI엔 불필요(터미널 렌더 아님).

## 8. 권한 / 안전

- 실행은 **`--permission-mode auto`** 고정. 파일 편집 자동 승인.
- cwd = 방 작업 디렉토리 → 작업 경계.
- 위험 도구(임의 Bash 등)는 필요 시 `--allowedTools` 화이트리스트로만 열고, 기본은 편집 위주.
- 자동승인이므로 **방 생성 시 작업 디렉토리를 명확히 고지**(사용자가 경계 인지).

## 9. UI 구성

- 방 목록(기존 세션 매니저와 유사) + 방 생성(`ClaudeCodeRoomDialog` 재사용/확장: 이름·디렉토리·에이전트 선택).
- 채팅 화면: 상단 헤더(방 이름·에이전트·usage), 중앙 대화 스크롤, 하단 입력창(멀티라인·전송·이미지 첨부·Stop).
- 스타일: `AppStyles.xaml` 전역 스타일 준수. 텍스트/클릭 컨트롤은 `.knowledge/텍스트렌더링규칙.md`,
  `.knowledge/컨트롤추가규칙.md` 준수.

## 10. 에러 / 취소

- **Stop**: 진행 중 턴 취소 = 프로세스에 중단 신호(또는 프로세스 종료 후 resume로 재개). 상주 프로세스
  interrupt 주입 가능 여부는 구현 시 확인, 안 되면 프로세스 종료→resume 폴백.
- 프로세스 비정상 종료: 방을 오류 상태로 표시, 재시작 버튼 제공(session_id resume).
- `rate_limit_event`에서 한도 초과·overage 거부 감지 시 사용자에게 명확 안내.
- JSONL 파싱 실패 라인은 무시하고 로깅(diag).

## 11. 테스트

- 어댑터 단위: 캡처한 실제 JSONL 픽스처 → `ChatEvent` 정규화 검증(핵심 로직).
- 멀티턴 resume: 2턴 이어짐 + session_id 재사용 확인.
- env 위생: 스폰 프로세스가 API키 미사용(구독)임을 확인.
- 취소: Stop 후 프로세스/상태 정합.

## 12. 범위 밖 / 백로그

- **② 채팅 내 승인 버튼**(per-call allow/deny) — 구독 raw CLI 불가. 도입하려면 해당 방만 API-SDK 경로.
- 읽기전용 자동 / 쓰기·실행만 승인(③).
- 기존 대화형 세션 id import(실시간 인계 아님, resume만).
- 슬래시 커맨드 UI(모델 선택 드롭다운 등 개별 기능은 필요 시 별도).
- `@파일` 자동완성, MCP 도구 상세 표시, 대화 내보내기, 병렬 다중 방 최적화.
- codex `proto` 상주 스트리밍(빌드1은 exec+resume로 시작 가능).

## 13. 미검증 / 리스크

- **codex 경로 전체 미검증** — 착수 시 스파이크 필수(§4).
- claude 이미지 입력의 정확한 stream-json 블록 형식 미확정.
- 상주 프로세스 interrupt(턴 중단) 지원 여부 미확정 → 종료+resume 폴백 설계.
- 자동승인 특성상 방 디렉토리 밖 영향 가능성 → cwd·allowedTools로 제한하되 완전 격리는 아님.
- 구독을 프로그램적으로 구동하는 것은 벤더 ToS 회색지대(기존 앱도 동일 전제).
