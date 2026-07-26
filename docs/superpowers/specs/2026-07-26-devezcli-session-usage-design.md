# DevezCLI 세션 토큰·예상 비용 표시 설계

## 목표

DevezCLI 세션 상단에 Codex와 동일한 형식으로 누적 입력 토큰, 출력 토큰, 예상 비용을 표시한다.

표시 형식:

```text
↓입력  ↑출력  ($예상금액)
```

## 범위

- `SessionUsageService`가 `devezcli`를 지원 에이전트로 인식한다.
- DevezCLI 방의 세션 ID는 `SettingsService.LoadDevezCliRoomSession`에서 읽는다.
- DevezCLI와 Codex가 공유하는 rollout JSONL의 마지막 `token_count` 이벤트를 같은 파서로 읽는다.
- 토큰 합계, 캐시 토큰 계산, GPT 모델 단가, 비용 형식, 툴팁, 갱신 주기는 Codex와 동일하게 유지한다.
- 사용량 데이터나 rollout 파일이 없으면 기존 동작대로 상단 사용량 영역을 숨긴다.

## 구조

현재 Codex 전용 `ReadCodex`를 Codex 계열 공용 읽기 함수로 정리한다. 이 함수는 방 ID, 세션 ID, 에이전트 표시명을 받아 rollout JSONL을 파싱한다.

`SessionUsageService.Read`의 분기는 다음과 같다.

- `claude`: 기존 Claude 파서 사용
- `codex`: Codex 세션 ID와 `Codex` 라벨을 공용 파서에 전달
- `devezcli`: DevezCLI 세션 ID와 `Devez CLI` 라벨을 공용 파서에 전달

상단 UI를 담당하는 `WorkspacePaneView`는 이미 `SessionUsageService.IsSupported`와 `Read` 결과만 사용하므로 별도 UI 구조 변경 없이 DevezCLI 표시가 활성화된다.

## 데이터 흐름

1. 활성 방이 DevezCLI인지 확인한다.
2. 저장된 DevezCLI 세션 ID로 Codex rollout 경로를 찾는다.
3. 파일 끝에서 마지막 `token_count.total_token_usage`를 읽는다.
4. 입력 총량에서 캐시 읽기·쓰기 토큰을 분리하고 출력 토큰을 읽는다.
5. 기존 GPT 모델 단가표로 예상 비용을 계산한다.
6. 기존 상단 사용량 영역에 Codex와 동일한 문자열을 표시한다.

## 오류 처리

- 세션 ID, rollout 파일, `token_count` 이벤트가 없으면 마지막 캐시값을 사용한다.
- 캐시값도 없으면 표시를 숨긴다.
- 손상되거나 쓰는 중인 JSONL 줄은 예외를 외부로 전파하지 않고 표시를 생략한다.
- 알 수 없는 모델은 토큰 수만 표시하고 예상 비용은 생략한다.

## 검증

- `claude`, `codex`, `devezcli`가 지원 대상으로 판정되는지 확인한다.
- Codex와 DevezCLI가 같은 토큰 이벤트에서 같은 입력·출력·비용 결과를 내는지 확인한다.
- DevezCLI 결과의 에이전트 라벨이 `Devez CLI`인지 확인한다.
- 미지원 에이전트와 데이터 없는 세션에서 사용량 영역이 숨겨지는 기존 동작을 보존한다.
- 실행 중인 DevezCode 프로세스를 강제 종료하지 않는다. 빌드가 필요하면 프로젝트 종료 규칙을 따른다.

## 제외

- 푸터의 공급자 플랜 사용량 표시
- DevezCLI 자체의 새로운 로그 포맷 추가
- 단가표 변경
- 기존 훅·세션 추적 로직 변경
