# Claude/Codex 사용량: 예정 전 즉시 초기화·인증 전환·신선도 처리

## 문제의 본질

Claude/Anthropic 또는 Codex/OpenAI가 공지된 `reset_at` 이전에 한도를 즉시 초기화할 수 있다. 따라서 다음 가정은 안전하지 않다.

- 같은 윈도우에서는 사용률이 항상 증가한다.
- `reset_at`은 항상 미래 방향으로만 이동한다.
- 사용률이 크게 감소한 응답은 오래된 응답이다.

이 가정으로 `Math.Max` 누적 또는 reset 시각 방향 비교를 사용하면, 공급자가 `78% → 2%`로 실제 초기화해도 이전 값이 고정된다. 반대로 단순히 마지막 도착 응답만 쓰면 계정 전환 중 늦게 도착한 이전 계정 응답이 최신값을 덮을 수 있다.

## 핵심 원칙

### 1. 정상 API 응답은 공급자의 현재 상태로 신뢰한다

성공한 최신 요청의 사용률과 `reset_at`을 방향과 무관하게 그대로 채택한다.

- 사용률 감소 허용
- reset 시각 앞당김·뒤로 이동 허용
- `1%` 같은 실제 값을 `0%`로 보정하지 않음
- 단조 증가 안정화(`Math.Max`) 금지

공급자 즉시 초기화와 오래된 응답을 구분하는 기준은 값의 방향이 아니라 **요청 순서와 인증 세대**다.

### 1-1. 이전 윈도우가 유효한 동안의 단발성 급락은 연속 응답으로 확인한다

정상값 사이에 큰 급락이 한 번만 끼고 다음 응답에서 원복되는 공급자 응답이 관측될 수 있다.
`reset_at`이 같은 경우뿐 아니라 **`reset_at`이 바뀐(윈도우 교체를 주장하는) 급락**도 마찬가지다
— 서버가 같은 계정에 두 사용량 레코드를 갖고 간헐적으로 다른 쪽을 반환하는 사례가 실제로
관측됐다(아래 "서버 이중 레코드" 참고). 이 경우에도 `Math.Max`로 값을 고정하면 안 된다.

- 이전에 채택한 윈도우의 `reset_at`이 **아직 미래**인데 사용률이 10%p 이상 급락한 첫 응답은
  보류한다(새 응답의 `reset_at`이 같든 다르든).
- 다음 **새 응답**이 같은 윈도우 주장(`reset_at` 5초 이내 일치, 둘 다 없음 포함)으로 급락을
  유지하면 실제 조기 초기화로 채택
- 다음 응답이 원복되면 보류값을 폐기하고 원복값 채택
- 이전 윈도우의 `reset_at`이 **이미 경과**했거나 없으면 정상 윈도우 전환이므로 즉시 채택
- 같은 파일을 재독한 것은 연속 확인으로 세지 않음

이 방식은 값의 단조 증가를 가정하지 않으며, 실제 조기 초기화(초기화권 사용 포함)는 최대 한
폴링 주기만 확인을 위해 지연한다. Codex API, Claude API, Claude statusLine 훅에 동일하게 적용한다.

### 1-2. 재시작 직후에도 기준값이 있어야 한다 (스냅샷 시드)

드롭 가드 상태가 메모리 전용이면 앱 재시작 직후 첫 응답이 하필 가짜 급락일 때 기준값이 없어
그대로 표시된다(실행 시 사용량이 틀리게 보였다가 다음 폴링에 정상화되는 증상). 마지막 성공
게시값을 디스크에 남기고 시작 시 가드 기준값으로 시드한다.

- Codex: `%AppData%\DevezCode\codex-usage.json` (`fetched_at`, `account_fingerprint`,
  `five_hour`/`weekly`의 `used_percent`+`resets_at`) — 게시 성공마다 임시 파일 후 교체로 기록
- Claude: 기존 `api-usage.json` 폴백 파일을 그대로 시드에 재활용
- 지문이 현재 인증과 일치하고 `fetched_at`이 48시간 이내일 때만 시드.
  reset이 이미 지난 기준값은 가드가 자연히 무시하므로(1-1) 표시엔 영향 없다.
- 시드는 표시용이 아니다 — UI에 직접 그리지 않고 가드 기준값으로만 쓴다.
- **지문은 토큰이 아니라 계정 기준으로 만든다.** 토큰은 CLI refresh 로 수시로 회전하므로 토큰
  지문을 쓰면 같은 계정인데 시드가 무시되고 가드가 리셋된다. Codex 는 JWT 의
  `chatgpt_account_id` 지문(없으면 토큰 지문 폴백)을 쓴다. Claude 토큰은 opaque 라 계정 ID 를
  얻을 수 없어 토큰 지문을 유지한다(회전 후엔 시드가 조용히 생략됨 — 보수적으로 동작).
- 계정 키가 바뀌면(진짜 계정 전환) 가드 상태를 비워, 이전 계정 기준값으로 새 계정의 정상값을
  보류하지 않는다. 이 리셋은 계정 키가 안정적인 Codex 에만 있다.
- Claude statusLine 훅은 `ratelimit.json` 파일 자체가 재시작을 넘어 남으므로 별도 시드 불필요.

### 서버 이중 레코드(가짜 프로필) 관측 기록 — 2026-07-12

같은 토큰(지문 동일)·같은 `ChatGPT-Account-Id` 헤더로 16초 간격 요청에 OpenAI `wham/usage`가
서로 다른 두 프로필을 반환했다: 정상(5h=100%/주간 23%, reset 02:01 KST)과 가짜(5h=6%/주간 1%,
reset 02:15 KST). 두 프로필의 주간 윈도우가 같은 날 오후 4시경 8분 차이로 시작 — 플랜
전환/초기화권류 이벤트로 사용량 버킷이 두 개 공존하며 간헐적으로(관측상 요청의 5~10%) 낮은
쪽이 반환되는 것으로 추정. 헤더 유무·새 TLS 연결 여부와 무관하게 재현 실험(15회)에서는 일관된
응답이라 클라이언트 요인은 배제됨. 가짜 프로필은 `reset_at`이 달라서 "같은 reset 구간" 조건의
가드를 우회했었다 — 1-1을 "이전 윈도우 유효 중" 기준으로 확장한 이유.

### 2. 폴링은 single-flight로 직렬화한다

타이머 폴링과 로그인 직후 `RefreshNow()`가 겹치면 이전 요청이 나중에 완료될 수 있다.

- 정기 타이머 중복은 생략
- 수동 새로고침은 진행 중 요청 뒤에서 반드시 실행
- 성공 응답 게시 직전에 현재 인증을 다시 읽어 요청 시작 때 인증과 비교
- 토큰 또는 account ID가 바뀌었으면 응답 폐기

### 3. refresh 결과도 조건부 저장한다

토큰 refresh HTTP 요청 도중 사용자가 로그아웃하거나 다른 계정으로 로그인할 수 있다. 응답을 무조건 저장하면 연결 해제한 계정을 되살리거나 새 계정을 덮는다.

CredentialStore에서 다음을 같은 lock 안에서 처리한다.

1. refresh 시작 시 읽은 access/refresh와 현재 저장값 비교
2. 연결 해제 마커 확인
3. 모두 동일할 때만 새 토큰 저장

`TrySaveIfCurrent(...)` 형태의 조건부 저장을 사용한다.

### 4. 인증 후보는 유효한 것만 제한적으로 시도한다

- CLI 인증을 사용하려면 실제 CLI 파일 형식을 지원한다.
  - Claude: `~/.claude/.credentials.json`
  - Codex: `~/.codex/auth.json`의 `tokens.access_token`
- 파일의 만료 시각 또는 JWT `exp`가 지났으면 다음 후보로 이동
- 401이면 해당 후보만 이번 폴링 주기에서 제외하고 다음 후보를 한 번 시도
- 모든 후보를 한 번씩 시도했으면 종료하고 다음 정기 주기까지 대기
- 후보 두 개가 번갈아 401을 내는 무한 재시도 구조를 만들지 않음

## Claude API와 statusLine 훅의 역할

### API가 계정 기준값

장기 실행 Claude daemon은 계정 전환 후에도 이전 계정의 `rate_limits`를 내보낼 수 있다. 현재 OAuth 인증으로 성공 수집한 API 값이 신선하면 API 값을 기준으로 사용한다.

### fallback 파일

`%AppData%\DevezCode\claude\api-usage.json`에는 최소한 다음을 기록한다.

- `fetched_at`: 실제 API 성공 시각
- `account_fingerprint`: 토큰 원문이 아닌 SHA-256 기반 짧은 지문
- `five_hour`, `seven_day`

처리 규칙:

- 현재 인증 후보 중 어느 것과도 지문이 맞지 않으면 삭제
- 특정 인증이 401이면 그 인증이 만든 fallback만 삭제
- 신선도는 파일 mtime보다 `fetched_at`을 우선
- 10분 이상 지난 fallback은 statusLine에서 사용하지 않음
- fallback이 신선하면 오래된 daemon live 값보다 우선

토큰 원문, refresh token, 전체 JWT는 로그나 fallback에 절대 기록하지 않는다.

## 훅 파일 경합

`StatusLineService`는 FileSystemWatcher와 폴링 타이머가 동시에 `Emit()`을 호출할 수 있다.

안전한 순서:

1. lock으로 Emit 직렬화
2. 파일 쓰기 시각 확인
3. JSON 파싱 및 짧은 재시도
4. **파싱 성공 후에만** `_lastWriteTicks` 갱신
5. 스냅샷 발행

파싱 전에 쓰기 시각을 소비하면 훅이 쓰는 도중 읽어 실패한 갱신을 다음 폴링에서도 건너뛴다.

훅 스냅샷의 `CapturedAt`은 객체 생성 시각이 아니라 실제 파일 쓰기 시각을 사용해야 오래된 파일을 최신값으로 오인하지 않는다.

## 사용량 표시 방식

설정의 `사용량 남은 수치로 표시`가 켜지면 공급자 원본 사용률은 변경하지 않고 렌더링할 때만
`100 - clamp(used, 0, 100)`으로 변환한다.

- 계정 사용량 사이드바 카드의 수치와 막대 폭
- 하단 푸터의 수치와 막대 폭
- Claude, Codex, OpenCode Go, Grok 툴팁의 사용량 수치

한도 도달 예상 계산과 경고 색상은 반드시 원본 사용률을 사용한다. 따라서 남은 사용량 막대가
짧아질수록 위험 색상이 유지되고, 표시 방식 전환 때문에 예상 소진 계산이 뒤집히지 않는다.
DeepSeek처럼 퍼센트가 아닌 금액 잔액은 변환하지 않는다.

관련 설정은 `SettingsService.ShowRemainingUsage`이며 기본값은 `false`다.

## UI 신선도 및 만료 처리

- 스냅샷에 실제 `CapturedAt` 저장
- 30초 정도의 UI 타이머로 이벤트가 없어도 신선도와 윈도우 만료를 재평가
- 10분 이상 성공 갱신이 없으면 `갱신 지연` 표시
- 패널의 `HH:mm 기준`은 렌더링 시각이 아니라 실제 수집 시각
- 여러 카드가 있으면 가장 오래된 카드 시각을 표시해 전체 화면이 그 시각보다 새롭다고 오해하지 않게 함
- `resets_at <= now`인 윈도우는 새 응답이 없어도 제거
- stale 값은 필요하면 유지하되 툴팁에 마지막 성공 시각과 갱신 지연을 명시

## 피해야 할 구현

```csharp
UsedPercent = Math.Max(previous.UsedPercent, candidate.UsedPercent);
```

```csharp
if (newReset < oldReset) return previous;
```

```csharp
if (usedPercent <= 1) usedPercent = 0;
```

```csharp
catch { /* 직전 값 유지 */ }
```

마지막 형태처럼 오류를 전부 숨기면 0% 또는 이전 계정 값이 최신처럼 무기한 남는다. HTTP 상태, 파싱 실패, 인증 후보, 성공한 사용률은 토큰 없이 `DiagLog`에 기록한다.

## 초기화권 소비(consume)

- `POST /wham/rate-limit-reset-credits/consume`, body `{credit_id, redeem_request_id}`(snake_case),
  헤더는 credits GET과 동일(`Authorization`/`User-Agent`/`OpenAI-Beta: codex-1`/`ChatGPT-Account-Id`).
- 명시 `credit_id`(만료 최빠름) + `redeem_request_id`(UUID) 재사용 + 버튼 in-flight 락 +
  1시간 쿨다운으로 중복 소비를 차단한다. **자동 재시도 금지.**
- **서버 전파 지연 주의**: consume 성공 직후에도 `wham/usage`가 수십 초~수 분간 소비 이전의
  옛 사용량을 반환한다(실측: 즉시 재폴링이 옛 99%를 받고, 새 0%는 2분 20초 뒤 도착).
  따라서 `_dropGuard.Reset()`(기준값 비우기)만으로는 즉시 반영이 안 된다 — 빈 가드가 옛 값을
  새 기준값으로 채택해 재무장하고, 실제 급락은 다음 정규 3분 폴링까지 보류된다.
- 성공(`reset`/`already_redeemed`) 시 처리:
  - `_dropGuard.ExpectDrop(now + 5분)` — 기대 시간창 동안 첫 급락을 연속 확인 없이 즉시 채택.
    급락 없이 윈도우 교체(reset 변경)만 관측돼도(원래 저사용) 기대를 해제한다.
    `Reset()`(계정 전환)도 기대를 함께 해제하고, 시간창 만료 후엔 플립 방어가 복원된다.
  - `RefreshNow()` + 버스트 재폴링(3/5/10/15/30/60/60/60초 간격, 기대 해제 시 조기 종료)
    — 서버 전파 완료를 정규 폴링보다 빨리 포착.
  - `_guardSeeded`는 **true 로 유지**. false 로 두면 다음 폴링이 소비 이전 高사용률 스냅샷으로
    가드를 재시드해 새 低값을 보류한다.
- 쿨다운은 `%AppData%\DevezCode\codex-reset-last-used.json`(계정 지문 + `used_at`)로 재시작을
  넘어 유지, 다른 계정이면 무시. 실제 소비(`reset`/`already_redeemed`) 때만 기록.
- 응답 `code`: `reset`/`nothing_to_reset`/`no_credit`/`already_redeemed`. 모르는 값은
  성공 처리하지 않음(`ConsumeOutcome.Unknown`).
- UI: 계정 사용량 패널 "초기화 N회 가능" 헤더 오른쪽 `[사용]` 버튼(쿨다운 중 비활성 +
  `ToolTipService.ShowOnDisabled` 호버 안내). 확인·결과는 `ConfirmDialog.Show`/`Alert`.
- 설계/계획: `docs/superpowers/specs|plans/2026-07-19-codex-reset-credit-consume-*.md`.

## 관련 코드

- `Services/UsageApiService.cs`: Claude 인증 선택, 폴링, fallback(+가드 시드), 진단
- `Services/CodexUsageService.cs`: Codex CLI 인증, 후보별 401 처리, 공급자 초기화 반영,
  `codex-usage.json` 스냅샷 기록·시드
- `Services/UsageDropGuard.cs`: 이전 윈도우 유효 중의 단발성 큰 급락을 연속 응답으로 확인,
  `Seed`(재시작 기준값)·`Reset`(계정 전환)·`ExpectDrop`(초기화권 소비 후 첫 급락 즉시 채택)
- `Services/ClaudeCredentialStore.cs`
- `Services/CodexCredentialStore.cs`
- `Services/StatusLineService.cs`
- `Models/RateLimitSnapshot.cs`
- `Models/ProviderUsage.cs`
- `Models/UsageCardVM.cs`
- `Resources/StatusLine/statusline.js`
- `MainWindow.xaml.cs`: stale·만료 재평가와 표시

## 검증 체크리스트

- 정상 API 응답의 사용률을 그대로 표시하는가
- 이전 윈도우 유효 중의 단발성 급락은(reset 동일/변경 무관) 보류되고 원복 응답은 즉시 채택되는가
- 실제 급락(조기 초기화·초기화권)은 두 번째 새 응답에서 채택되는가
- (Codex) 초기화권 소비 후 기대 시간창의 첫 급락이 확인 없이 즉시 채택되는가
- (Codex) 소비 후 버스트 재폴링이 돌고, 급락 채택·윈도우 교체 관측 시 조기 종료되는가
- (Codex) 기대 시간창 만료 후의 급락은 다시 보류되는가(플립 방어 복원)
- 이전 윈도우의 reset이 경과한 뒤의 낮은 값(정상 롤오버)은 첫 응답에서 즉시 채택되는가
- 재시작 직후 스냅샷 시드로 첫 가짜 급락이 보류되는가(계정 키 일치·48h 이내일 때)
- (Codex) 계정 전환 시 가드가 리셋되어 새 계정 첫 값이 즉시 게시되는가
- (Codex) 토큰 회전만으로는 시드 생략·가드 리셋이 일어나지 않는가(계정 키가 account ID 기반인가)
- 이전 계정의 늦은 응답을 폐기하는가
- refresh 도중 로그아웃/재로그인 시 새 인증을 덮지 않는가
- 만료/401 인증 다음 후보를 한 번만 시도하는가
- 모든 인증이 401이어도 무한 요청하지 않는가
- fallback 계정 지문과 현재 인증이 일치하는가
- 10분 경과 후 `갱신 지연`이 표시되는가
- reset 경과 후 이벤트가 없어도 해당 윈도우가 제거되는가
- 패널 시간이 실제 마지막 성공 시각인가
- 훅 부분 쓰기 실패 후 다음 폴링에서 재시도되는가
