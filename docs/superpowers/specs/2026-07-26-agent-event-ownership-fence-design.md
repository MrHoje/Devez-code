# 에이전트 상태 이벤트 소유권 격리 설계

## 배경

DevezCode 안의 Claude Code 세션이 셸 도구로 `codex exec`를 실행하면 자식 Codex가
부모의 `DEVEZCODE_ROOM_ID`를 상속한다. 현재 Codex 훅은 이 room ID로 Codex 상태 파일을
쓰고, `MainWindow`는 이벤트를 발생시킨 에이전트와 방의 실제 `AgentId`가 일치하는지
확인하지 않는다. 그 결과 자식 Codex의 프롬프트와 busy→idle 전이가 부모 Claude 방의
헤더, 스피너, 완료기록을 변경했다.

같은 구조는 다른 에이전트 조합에서도 발생할 수 있다. 일부 에이전트는 루트 세션 필터가
있지만 적용 범위와 강도가 서로 다르며, room ID만으로 상태를 연결하는 앱 수신부에는
공통 방어가 없다.

## 목표

- 다른 에이전트로 실행된 자식 CLI가 부모 방의 상태를 변경하지 못하게 한다.
- 동일 에이전트를 중첩 실행해도 루트 CLI가 아닌 세션의 이벤트를 무시한다.
- resume, 새 대화, 자동 재진입 후에도 정상적인 루트 세션 전환은 허용한다.
- 오래되거나 순서가 뒤바뀐 종료 이벤트가 현재 턴의 스피너를 끄지 못하게 한다.
- 거부된 이벤트를 진단 로그에서 확인할 수 있게 한다.

## 비목표

- 각 CLI가 자체적으로 표시하는 작업 상태나 프롬프트 UI를 변경하지 않는다.
- 정상적인 완료기록의 보존 개수, 카드 디자인, 알림 설정은 변경하지 않는다.
- 훅을 제공하지 않는 외부 CLI의 내부 동작을 추측해 완료 판정을 새로 만들지 않는다.

## 방어 계층

### 1. 실행 소유 에이전트 표식

DevezCode가 최상위 CLI를 실행할 때 기존 `DEVEZCODE_ROOM_ID`와 함께
`DEVEZCODE_TRACKING_AGENT=<agentId>`를 주입한다. 내부 터미널, 외부 터미널,
resume, 포크, 자동 재진입 등 DevezCode가 만드는 모든 실행 경로에 같은 값을 넣는다.

Claude, Codex, OpenCode, Grok, Antigravity, Kimi 및 Devez CLI의 훅·플러그인·자체
상태 기록기는 다음 규칙을 적용한다.

- room ID가 없으면 기존처럼 아무것도 기록하지 않는다.
- room ID가 있는데 tracking agent가 없거나 자신과 다르면 stdin만 안전하게 비우고 종료한다.
- tracking agent가 자신과 같은 경우에만 방별 상태 파일을 기록한다.

따라서 Claude 방에서 실행된 자식 Codex는 `DEVEZCODE_TRACKING_AGENT=claude`를
상속하며 Codex 훅 입구에서 거부된다.

### 2. 앱 수신부의 에이전트 소유권 게이트

`MainWindow`에 room ID와 예상 agent ID를 함께 검사하는 공통 게이트를 둔다.
모든 Message, Busy, Waiting, SessionChanged 처리기는 상태를 변경하기 전에 이 게이트를
통과해야 한다.

게이트는 다음 조건을 모두 만족할 때만 이벤트를 허용한다.

1. room ID에 해당하는 `SessionItem`이 존재한다.
2. `SessionItem.AgentId`가 이벤트 소스의 agent ID와 대소문자 무시 비교로 일치한다.
3. 세션이 삭제 또는 추적 해제 중이 아니다.

불일치 이벤트는 헤더, 스피너, 대기 표시, 세션 ID 저장, 완료기록 어느 것도 변경하지
않는다. 최초 불일치와 일정 시간마다의 반복 불일치만 `diag.log`에 남겨 로그 폭주를 막는다.

working directory 단위로 동작하는 `AgentLastMessageService`도 이벤트에 agent ID를 포함하거나
담당 에이전트만 갱신하도록 제한하여 같은 프로젝트의 다른 에이전트 헤더를 건드리지 않게 한다.

### 3. 루트 세션 소유권

에이전트 종류가 일치하더라도 중첩 실행된 동일 CLI를 구분하기 위해 방마다 루트 session ID를
둔다. 각 추적기는 native hook 또는 transcript가 제공하는 session ID를 기준으로 아래 규칙을
적용한다.

- 최상위 실행 직전에 해당 실행 세대의 루트 후보 상태를 초기화하거나, resume할 ID로 시드한다.
- 실행 세대에서 최초로 확인된 정상 세션만 루트로 확정한다.
- Message, Busy, Waiting, Stop 이벤트는 루트 session ID와 일치할 때만 처리한다.
- 자식 세션의 SessionStart는 현재 루트 값을 덮지 못한다.
- 정상적인 새 대화 또는 자동 재진입은 에이전트가 제공하는 명시적 전환 신호와 기존 세션 종료
  상태를 확인한 후에만 루트 값을 교체한다.
- 앱 재시작 시 resume 가능한 기존 루트 ID는 보존하고, 실행 중 turn 상태만 초기화한다.

이미 루트 필터가 있는 OpenCode, Grok, Antigravity는 기존 동작을 공통 규칙과 비교해 빠진
경로만 보강한다. Codex, Claude, Kimi, Gajae 및 Devez CLI는 각자의 native session ID 규약에
맞춰 같은 소유권 규칙을 적용한다.

### 4. 턴 종료 울타리

turn ID를 제공하는 Codex는 기존 active turn 마커를 유지하되 루트 session ID까지 함께
검증한다.

- UserPromptSubmit이 현재 루트 세션의 새 turn ID를 활성화한다.
- Tool/Waiting 이벤트는 활성 turn ID가 일치할 때만 상태를 갱신한다.
- Stop은 루트 session ID와 활성 turn ID가 모두 일치할 때만 idle을 기록한다.
- 이전 턴의 늦은 Stop은 새 턴의 active 마커와 불일치하므로 무시한다.

turn ID가 없는 에이전트는 해당 에이전트의 권위 있는 완료 신호를 유지한다. 예를 들어
Claude의 턴 종료 마커, Grok의 `turn_ended`, Gajae의 runtime sidecar 완료 상태를 사용하며
단순한 파일 mtime만으로 정상 완료를 발행하지 않는다.

## 데이터 흐름

정상 경로:

1. DevezCode가 room ID와 tracking agent를 주입해 최상위 CLI를 실행한다.
2. 추적기가 tracking agent와 루트 session ID를 검증한다.
3. 검증된 이벤트만 에이전트별 상태 파일에 원자적으로 기록된다.
4. 서비스가 파일 변경을 읽어 agent ID가 고정된 이벤트를 발생시킨다.
5. `MainWindow` 소유권 게이트가 방의 실제 agent ID를 다시 검증한다.
6. 현재 루트 턴의 busy→idle만 완료기록과 알림을 만든다.

중첩 경로:

1. 부모 CLI가 셸에서 다른 또는 동일 CLI를 실행한다.
2. 다른 CLI는 tracking agent 불일치로 훅 입구에서 거부된다.
3. 동일 CLI는 tracking agent는 일치하지만 session ID가 루트와 달라 거부된다.
4. 훅 또는 플러그인 오류로 파일 이벤트가 새더라도 앱 소유권 게이트가 교차 에이전트 오염을
   최종 차단한다.

## 실패 처리

- tracking agent가 누락된 상태에서 room ID만 존재하면 fail-closed로 상태 기록을 생략한다.
- session ID를 파싱할 수 없거나 루트가 확정되지 않은 이벤트는 완료 판정에 사용하지 않는다.
- 앱 또는 CLI가 강제 종료되어 정상 Stop이 없으면 정상 완료 카드를 만들지 않는다.
- 원자적 상태 파일 쓰기(temp + rename)는 그대로 유지한다.
- 거부 로그에는 room ID, 예상 agent, 실제 이벤트 소스, event 종류만 기록하고 프롬프트 본문은
  기록하지 않는다.

## 테스트 전략

### 소유권 단위 테스트

- Claude 방에 들어온 Codex Message/Busy/Session 이벤트가 모두 거부된다.
- Codex 방에 들어온 Codex 이벤트는 허용된다.
- 존재하지 않거나 삭제 중인 방 이벤트가 무시된다.
- working directory가 같은 서로 다른 에이전트의 last message가 섞이지 않는다.

### 훅 회귀 테스트

- `DEVEZCODE_TRACKING_AGENT`가 없거나 다른 경우 상태 파일이 생성·변경되지 않는다.
- tracking agent가 일치하면 기존 상태 파일이 정상적으로 갱신된다.
- 동일 에이전트의 두 session ID 중 루트만 busy/lastmsg/Stop을 기록한다.
- 자식 SessionStart가 루트 session ID를 덮지 않는다.
- 이전 turn ID의 Stop이 현재 turn을 idle로 바꾸지 않는다.

### 전환 테스트

- 새 세션 최초 실행
- 기존 세션 resume
- CLI 정상 종료 후 자동 재진입
- CLI가 지원하는 새 대화/clear
- 앱 재시작 후 기존 세션 복원

### 수동 통합 시나리오

- Claude에서 `codex exec "say ok"` 실행 중 Claude 스피너가 유지되고 완료카드가 추가되지 않는다.
- Codex에서 다른 Codex를 중첩 실행해도 바깥 Codex의 헤더와 스피너가 변하지 않는다.
- 각 에이전트의 정상 프롬프트는 기존처럼 스피너 ON → 실제 완료 → 카드 1장으로 끝난다.
- resume된 세션에서도 첫 프롬프트와 이후 프롬프트가 동일하게 추적된다.

## 완료 기준

- 모든 에이전트 이벤트 수신 경로에 앱 소유권 게이트가 적용된다.
- room ID를 사용하는 모든 훅·플러그인·자체 기록기에 tracking agent 검사가 적용된다.
- native session ID를 제공하는 모든 에이전트에서 중첩 세션이 루트 상태를 덮지 못한다.
- 교차 에이전트, 동일 에이전트 중첩, 이전 턴 Stop 회귀 테스트가 통과한다.
- 정상 실행, resume, 새 대화, 자동 재진입 동작이 유지된다.
