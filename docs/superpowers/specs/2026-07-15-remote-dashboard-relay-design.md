# 원격 대시보드 릴레이 설계 (A-2)

작성일: 2026-07-15
상태: 설계 승인됨(접근 1) — 구현 진행 중, **빌드/배포 보류**(사용자 복귀 후 점검)

## 1. 목표

DevezCode 의 웹 대시보드(현재 같은 LAN 에서만 접근 가능)를 **외부 인터넷에서 로그인 인증을 거쳐** 접근 가능하게 한다.

- 각 사용자는 **자기 PC**에 DevezCode 를 설치하고, 자기 PC 의 대시보드를 외부에서 접속한다. (남의 PC 에 들어오는 구조가 아님)
- 접근은 **소유자 본인 1명만**(1:1). 터미널 입력 = 명령 실행이므로 소유자 외 접근 차단이 최우선.
- 로그인은 **OAuth(Google/GitHub)** — 비밀번호를 직접 저장/관리하지 않는다.
- 릴레이는 **Cloudflare Workers + Durable Objects** 에 호스팅.
- 규모: 본인/소규모(~10명). Google OAuth 는 test 모드/미심사(허용 이메일 명시).

## 2. 핵심 결정 요약

| 항목 | 결정 |
|---|---|
| 인증 | OAuth (Google 우선, GitHub 옵션) |
| 릴레이 호스팅 | Cloudflare Worker + Durable Object (WS Hibernation) |
| 접근 범위 | 소유자 1:1 (device ↔ ownerSub 바인딩) |
| UI 위치 | 릴레이가 대시보드 UI 서빙(엣지 캐시), PC 는 헤드리스 WS 백엔드 |
| 데이터 저장 | 디바이스↔소유자 매핑은 Cloudflare KV, 세션키는 Worker secret |
| 터미널 코어 | **무변경** — 대시보드 계층만 리팩터 |

## 3. 아키텍처

```
[브라우저]  --WSS-->  [CF Worker] --(routes)--> [Durable Object: device별]  <--WSS(outbound)--  [DevezCode PC]
    |  OAuth 로그인(쿠키)         |  ownerSub==device.owner 검증           |  device token 검증        |
    |  대시보드 UI(엣지)           |                                        |  clientId 봉투 멀티플렉스  |  기존 TerminalDisplayOutputHub 재사용
```

세 파트:

### 3.1 Cloudflare 릴레이 (그린필드, `relay/` 폴더)
- **Worker (라우터/포털/인증)**
  - `/` `/app/*` — 웹 포털 + 대시보드 UI 정적 자산 서빙(엣지 캐시).
  - `/auth/login` `/auth/callback` — OAuth(Google) 흐름. 성공 시 서명된 세션 JWT 를 HttpOnly Secure 쿠키로 발급(`ownerSub`, `email` 클레임).
  - `/api/devices` — 로그인 사용자 소유 디바이스 목록 + 온라인 상태.
  - `/client/{deviceId}` (WS upgrade) — 브라우저 대시보드 연결. **세션쿠키 검증 → ownerSub 가 deviceId 소유자와 일치하는지 확인**(RCE 게이트) → 해당 device DO 로 라우팅.
  - `/device/{deviceId}` (WS upgrade) — DevezCode PC 아웃바운드 연결. device secret 검증 → DO 로 라우팅.
  - `/api/pair` — 디바이스 페어링(아래 4.2).
- **Durable Object (`DeviceHub`, deviceId 당 1 인스턴스)**
  - PC 아웃바운드 WS 1개(백엔드) + 브라우저 WS N개(프론트) 보관.
  - WebSocket Hibernation 으로 유휴 시 비용 최소화.
  - **clientId 봉투 브리지**: 브라우저→PC 는 `{clientId, payload}` 로 감싸 PC 로, PC→브라우저 는 `clientId` 로 대상 브라우저에 라우팅(또는 broadcast).
  - PC 연결 없으면 브라우저에 `offline` 통지.

### 3.2 DevezCode 데스크톱 (in-repo 리팩터)
현재 `Services/LanDashboardService.cs` 한 클래스가 [Kestrel 호스팅 + WS 수락 + 프로토콜 로직 + Client(소켓)]를 다 함. 이를 전송-무관하게 쪼갠다:

- **`DashboardHub`** (신규, 전송-무관 코어): `HandleClientMessage`/`Subscribe`/`Broadcast`/controller 로직을 이동. `IClientSink` 위에서 동작.
- **`IClientSink`** (신규 인터페이스): `void Queue(object message)` + `string? SelectedRoomId` 등 클라이언트 상태. 
  - `LanClientSink` — 기존 실제 WebSocket 구현(LAN).
  - `RelayClientSink` — 릴레이 논리 브라우저 1명. `Queue` 는 `{clientId, payload}` 봉투로 감싸 단일 아웃바운드 소켓에 씀.
- **`LanDashboardService`** — Kestrel 인바운드 어댑터로 축소, `DashboardHub` 위임.
- **`RelayConnector`** (신규): `ClientWebSocket` 으로 `wss://<relay>/device/{deviceId}` 아웃바운드 연결. device secret 인증, **자동 재연결(지수 백오프)**, 봉투 역다중화(들어온 `{clientId, payload}` 를 논리 `RelayClientSink` 로 분배, 신규 clientId → 새 sink 생성, close → sink 정리).
- **페어링/설정 UI**: 원격 활성 토글 + OAuth device flow 로 소유자 바인딩(4.2). device secret 은 로컬 `%AppData%\DevezCode` 에 저장.

**터미널 계층(`TerminalSession`/`TerminalSessionManager`/`TerminalDisplayOutputHub`/ConPTY/`WorkspaceStore`) 무변경.** 릴레이는 기존 허브의 세 번째 소비자일 뿐.

### 3.3 웹 포털/대시보드 클라이언트 (릴레이가 서빙)
- 로그인 화면(OAuth 버튼) → 디바이스 목록/온라인 표시 → 디바이스 선택 시 기존 `Resources/Dashboard/web` 대시보드 로드하되 WS 대상만 `/client/{deviceId}` 로 변경.
- 기존 `dashboard.js` 프로토콜 재사용(hello/sessions/subscribe/input/snapshot/output/size/control). 봉투는 릴레이/커넥터가 처리하므로 브라우저 프로토콜은 사실상 동일.

## 4. 프로토콜 계약 (양쪽 공유)

### 4.1 봉투(envelope)
릴레이 구간(DO↔PC)에서만 사용. 브라우저↔DO 는 기존 평문 프로토콜 유지(DO 가 clientId 부착/제거).

```jsonc
// DO -> PC
{ "clientId": "<uuid>", "kind": "join" }                 // 새 브라우저 접속
{ "clientId": "<uuid>", "kind": "leave" }                // 브라우저 종료
{ "clientId": "<uuid>", "kind": "msg", "payload": { /* 기존 브라우저->서버 메시지: subscribe/input/claimControl/refresh */ } }

// PC -> DO
{ "clientId": "<uuid>", "kind": "msg", "payload": { /* 기존 서버->브라우저 메시지: hello/sessions/snapshot/output/size/control/error/starting */ } }
{ "clientId": "*",      "kind": "msg", "payload": { /* broadcast (sessions/control 등) */ } }
```

### 4.2 페어링(디바이스 ↔ 소유자 바인딩)
1. DevezCode 설정에서 "원격 접속 켜기" → 앱이 `/api/pair/start` 호출 → 릴레이가 `userCode`(짧은 코드) + `verificationUrl` 반환(OAuth device flow 유사).
2. 앱이 사용자에게 `verificationUrl` + `userCode` 표시(브라우저 열기). 사용자가 그 URL 에서 **OAuth 로그인** → 코드 승인.
3. 승인되면 릴레이가 `deviceId` + `deviceSecret` 발급, KV 에 `device:{deviceId} = {ownerSub, secretHash, name}` 저장. 앱은 폴링(`/api/pair/poll`)으로 수령해 로컬 저장.
4. 이후 앱은 `deviceSecret` 로 `/device/{deviceId}` 에 상시 아웃바운드 연결.

### 4.3 인증 게이트 (RCE 방어 — 최우선)
- 브라우저 `/client/{deviceId}`: 세션쿠키 JWT 검증(서명) → `claims.ownerSub === KV.device[deviceId].ownerSub` 아니면 403. 소유자 아니면 절대 연결 불가.
- PC `/device/{deviceId}`: `hash(deviceSecret) === KV.device[deviceId].secretHash` 아니면 401.
- 세션 JWT: Worker secret 로 HMAC 서명, 만료 짧게(예: 12h) + 갱신.
- OAuth 허용 목록: `~10명` 이므로 허용 이메일 allowlist(KV `allowlist`)로 이중 차단.

## 5. 데이터 (Cloudflare KV)
- `device:{deviceId}` → `{ ownerSub, email, secretHash, name, createdAt }`
- `owner:{ownerSub}` → `[deviceId, ...]` (사용자별 디바이스 목록)
- `pair:{userCode}` → `{ status, ownerSub?, deviceId?, deviceSecret?, expiresAt }` (페어링 진행 상태, 단명)
- `allowlist` → `[email, ...]`
- 세션 서명키/OAuth client secret → Worker secret(`wrangler secret`)

## 6. 에러/엣지 처리
- PC 오프라인: 브라우저에 `offline` 상태 표시, 재연결 시 자동 복구.
- PC 재연결: `RelayConnector` 지수 백오프. 재연결 후 각 브라우저에 대해 `GetReplay` 로 스냅샷 재구동(기존 replay 재사용).
- 백프레셔: 단일 아웃바운드 소켓에 전 브라우저 output 집중 → sink 별 채널 + 소켓 쓰기 직렬화, 과도 시 드롭 대신 지연. (초기엔 기존 unbounded 유지, 한도는 후속)
- 봉투 손상/미확인 clientId: 무시 + 진단 로그.
- input 크기 제한 65536 유지. 세션쿠키 없는 WS 업그레이드 거부.

## 7. 보안 체크리스트
- [ ] 소유자 불일치 시 `/client` 403 (핵심 RCE 게이트)
- [ ] deviceSecret 은 해시로만 KV 저장, 평문 미저장
- [ ] 세션 JWT HttpOnly+Secure+SameSite, 서명 검증, 만료
- [ ] OAuth state/PKCE 로 CSRF 방어
- [ ] 이메일 allowlist 이중 차단
- [ ] 전 구간 WSS/HTTPS (평문 금지)
- [ ] rate-limit: 로그인/페어링 시도
- [ ] 감사 로그: 누가(email) 언제 어느 room 에 input (RCE 추적)

## 8. 구현 범위 / 비범위
**범위**: `relay/` CF 프로젝트 전체, `LanDashboardService` 리팩터(DashboardHub/IClientSink 추출), `RelayConnector`, 페어링/설정 UI, 봉투, 웹 포털.
**비범위(YAGNI)**: 초대/협업(viewer/controller), 팀/조직 공유, 결제, per-user 세션 격리(1:1 이라 불필요), P2P/WebRTC.

## 9. 검증 계획 (사용자 복귀 후, 빌드 허용 시)
1. `relay/` 로컬 `wrangler dev` — OAuth 로그인, 디바이스 목록, 오프라인 표시.
2. DevezCode Release 빌드(사용자 승인 후) → 페어링 → 아웃바운드 연결.
3. 외부망 브라우저에서 로그인 → 세션 목록/스냅샷/입력/output 왕복.
4. 소유자 아닌 계정으로 `/client` 접근 → 403 확인(RCE 게이트).
5. PC 강제 오프라인/재연결 → 자동 복구.

## 10. 현재 세션 산출물 (빌드 없음)
- 본 설계 스펙.
- `relay/` Cloudflare 프로젝트 코드(Worker/DO/포털/wrangler) — 배포는 사용자 승인 후.
- DevezCode 데스크톱 리팩터 코드(DashboardHub/IClientSink/RelayConnector/봉투/설정) — **빌드하지 않음**, 복귀 후 점검·빌드.
