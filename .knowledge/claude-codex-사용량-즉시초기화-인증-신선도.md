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

## 관련 코드

- `Services/UsageApiService.cs`: Claude 인증 선택, 폴링, fallback, 진단
- `Services/CodexUsageService.cs`: Codex CLI 인증, 후보별 401 처리, 공급자 초기화 반영
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
- 예정일 전 `80% → 0%` 및 reset 시각 변경을 즉시 반영하는가
- 이전 계정의 늦은 응답을 폐기하는가
- refresh 도중 로그아웃/재로그인 시 새 인증을 덮지 않는가
- 만료/401 인증 다음 후보를 한 번만 시도하는가
- 모든 인증이 401이어도 무한 요청하지 않는가
- fallback 계정 지문과 현재 인증이 일치하는가
- 10분 경과 후 `갱신 지연`이 표시되는가
- reset 경과 후 이벤트가 없어도 해당 윈도우가 제거되는가
- 패널 시간이 실제 마지막 성공 시각인가
- 훅 부분 쓰기 실패 후 다음 폴링에서 재시도되는가
