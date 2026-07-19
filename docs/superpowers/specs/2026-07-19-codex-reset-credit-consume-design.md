# Codex 초기화권 소비 (계정 사용량 패널) — 설계

- 날짜: 2026-07-19
- 상태: 승인됨(구현 대기)
- 수준: **안전 우선 소비**

## 목표

계정 사용량 패널의 codex 초기화권(뱅크드 리셋)을 **읽기 전용 표시**에서
**사용자가 직접 1회 소비**할 수 있게 확장한다. 중복 소비를 구조적으로 차단하고,
소비 결과를 메시지박스로 확실히 고지하며 사용량을 즉시 갱신한다.

## 배경 / 현재 상태

- `CodexUsageService.FetchResetCreditsAsync` → `GET /wham/rate-limit-reset-credits` 로 목록만 조회.
  파싱 항목: `title`, `granted_at`, `expires_at` (**`id` 미파싱**).
- UI(`MainWindow.xaml` ~682): "초기화 N회 가능" 헤더 + 만료일/잔여기간 행만 표시. 소비 수단 없음.
- 인증/헤더/HTTP 인프라는 GET 에서 이미 검증됨(Bearer 토큰 / `ChatGPT-Account-Id` / `OpenAI-Beta: codex-1`).

## 소비 API (레퍼런스 + codex.exe + OpenAI PR 로 확정)

```
POST https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume
Authorization: Bearer <token>            (기존 GET 과 동일)
ChatGPT-Account-Id: <accountId>          (기존 GET 과 동일)
OpenAI-Beta: codex-1                     (기존 GET 과 동일)
Body(JSON): { "credit_id": "<id>", "redeem_request_id": "<UUID>" }
```

- `credit_id`: 생략 시 백엔드 auto-select. **본 설계는 명시 지정**(만료 최빠름 크레딧).
- `redeem_request_id`: 멱등키(UUID). 같은 논리 시도 내 재사용 → 서버가 `already_redeemed` 반환.
- 응답 `code` 값: `reset` / `nothing_to_reset` / `no_credit` / `already_redeemed`.
  "200 이면 크레딧은 소모된 것."

## 구성 요소

### ① 모델 (`Models/ProviderUsage.cs`, `Models/UsageCardVM.cs`)

- `ResetCredit.Id`(string?) 추가.
- `ResetCreditRowVM` 는 헤더 버튼 방식이라 행별 변경 없음. 카드 VM 에 소비 커맨드/상태 노출:
  - `ResetCreditCommand`(ICommand), `CanConsumeResetCredit`(bool), `ResetCreditTooltip`(string?).

### ② 서비스 (`Services/CodexUsageService.cs`)

- `FetchResetCreditsAsync`: 각 항목의 `id` 파싱. **구현 시 검증**: 실제 응답에 `id` 존재 확인
  (raw 로깅). 없으면 `credit_id` 생략(auto-select)으로 graceful 폴백 + 경고 로그.
- `ConsumeResetCreditAsync(string? creditId, string redeemRequestId)` 신설:
  - `_pollGate` 로 폴링과 **직렬화**(single-flight).
  - 전송 직전 `ReadAuth` 재확인(TOCTOU 최소화). 토큰/계정 불일치면 폐기.
  - `POST .../consume`, body `{credit_id, redeem_request_id}`.
  - 응답 `code` → enum `ConsumeOutcome { Reset, NothingToReset, NoCredit, AlreadyRedeemed, Unknown }`.
    모르는 값/오류 = `Unknown`(성공 처리 안 함).
  - **자동 재시도 없음.**
  - 성공(`Reset`/`AlreadyRedeemed`) 시 `_dropGuard` 우회/시드 → 조기 초기화가 다음 폴링에
    보류되지 않게 한 뒤 `RefreshNow()` 로 **즉시 갱신**.
- 만료 최빠름 선택 헬퍼: `ExpiresAt` 오름차순, null(만료정보 없음)은 맨 뒤. 모두 null 이면 auto-select.

### ③ UI (`MainWindow.xaml` + `.xaml.cs`)

- "초기화 N회 가능" 헤더 오른쪽 `[사용]` 버튼. `AppStyles.xaml` 전역 스타일, 커서 규칙 준수.
- 클릭 →
  `ConfirmDialog.Show("초기화권 사용",
    "만료가 가장 빠른 초기화권(~M월 d일 HH:mm)을 사용합니다.\n사용량 한도가 즉시 초기화되며 되돌릴 수 없습니다.",
    okLabel: "사용", danger: true)`
- 확인 시 버튼 **즉시 비활성화**(single-flight 락). UUID 는 확인 시점 1회 생성·재사용.
- 결과 메시지박스(`ConfirmDialog.Alert`):
  - `Reset`/`AlreadyRedeemed` → "초기화권을 사용했습니다. 사용량 한도가 초기화되었습니다."
  - `NothingToReset` → "초기화할 사용량이 없어 초기화권이 소모되지 않았습니다."
  - `NoCredit` → "사용 가능한 초기화권이 없습니다."
  - `Unknown`/오류 → "결과를 확인하지 못했습니다. 사용량을 새로고침해 확인하세요."

### ④ 1시간 쿨다운

- **실제 소비된 경우만**(`Reset`/`AlreadyRedeemed`) 버튼을 1시간 비활성화.
- 마지막 사용 시각을 `%AppData%\DevezCode\codex-reset-last-used.json` 에 계정 지문과 함께 기록 →
  앱 재시작해도 쿨다운 유지(재시작 우회 방지). 지문 불일치(다른 계정)면 무시.
- 1시간 경과 시 기존 30초 UI 타이머가 재평가해 자동 재활성화.
- 호버 툴팁(경과+잔여 동적):
  > 약 {n}분 전 초기화권을 사용했습니다.
  > 중복 사용을 막기 위해 사용 후 1시간 동안 잠깁니다. (약 {m}분 후 다시 사용 가능)
  > 지금 더 필요하면 ChatGPT 웹사이트나 데스크톱 앱에서 사용할 수 있습니다.
- 성격: 실수 중복 방지용 클라이언트 UX 가드(서버 강제 아님).

## 중복 소비 방지 (핵심 보장)

1. **명시 credit_id** — 항상 특정 1개 대상. auto-select 의 "다른 크레딧으로 번짐" 경로 없음.
2. **redeem_request_id 재사용** — 서버 멱등. 같은 UUID 재전송 → `already_redeemed`.
3. **크레딧 status=redeemed** — 명시 id 라 새 UUID 로도 이미 소진 크레딧 재소비 안 됨(멱등 윈도우 backstop).
4. **버튼 락 + single-flight** — 두 번째 요청 발사 불가. 확인 다이얼로그 모달로 재클릭 차단.
5. **1시간 쿨다운(영속)** — 성공 후 재클릭 자체 차단.
6. **성공 메시지박스 + 즉시 갱신** — "안 됐나?" 오인 재클릭 방지.

→ 한 클릭으로 2개 소비: 구조적 불가. 같은 크레딧 재소비: 이중 서버 가드로 불가.
   다른 크레딧 2개: 쿨다운 해제 후 사용자가 다시 확인해야만 가능(의도된 행동).

## 통제 밖 잔여 리스크 (수용)

- ToS/계정 리스크(비공개 백엔드 쓰기 자동화).
- 비문서 엔드포인트 스펙 변경 가능(모르는 `code` 는 성공 처리 안 함으로 방어).
- 서버 eventual consistency/이중 레코드 → POST outcome 만 신뢰, 직후 GET 값 불신.
- `redeem_request_id` 멱등 윈도우 길이 미상 → 자동재시도 금지 + 명시 id status backstop 으로 회피.

## 검증 항목 (구현 중)

- GET credits 응답에 `id` 필드가 실제 존재하는가(없으면 auto-select 폴백 경로 확인).
- 소비 성공 후 `RefreshNow` 로 "N-1회 가능" 및 사용량 초기화가 즉시 반영되는가(dropGuard 우회 확인).
- 쿨다운이 앱 재시작을 넘어 유지되고, 다른 계정에서는 무시되는가.
- 더블클릭/연타로 두 번째 POST 가 발사되지 않는가.
- `nothing_to_reset`/`no_credit` 은 쿨다운을 시작하지 않는가.

## 관련 코드

- `Services/CodexUsageService.cs`, `Services/UsageDropGuard.cs`, `Services/CodexCredentialStore.cs`
- `Models/ProviderUsage.cs`, `Models/UsageCardVM.cs`
- `MainWindow.xaml`, `MainWindow.xaml.cs`
- `Views/ConfirmDialog.xaml.cs`
- `.knowledge/claude-codex-사용량-즉시초기화-인증-신선도.md`
