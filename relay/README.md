# DevezCode 원격 대시보드 릴레이 (Cloudflare Workers)

Cloudflare Worker + Durable Object 로 동작하는 릴레이. 설계 근거:
`../docs/superpowers/specs/2026-07-15-remote-dashboard-relay-design.md`

현재 서비스는 `https://devezcode-relay.devez.workers.dev`에 배포되어 있다.
로컬 수정 후에는 `CF_WORKERS_TOKEN`과 `CLOUDFLARE_ACCOUNT_ID`를 설정한 뒤 `npm run deploy`로 반영한다.

## 1. 사전 준비

```powershell
cd relay
npm install
npx wrangler login
```

## 2. KV 네임스페이스 생성

```powershell
npx wrangler kv namespace create DEVICES
```

출력된 `id` 값을 `wrangler.jsonc` 의 `kv_namespaces[0].id` 에 넣는다.

## 3. Secret 등록

```powershell
npx wrangler secret put SESSION_SIGNING_KEY   # 예: openssl rand -base64 32 결과 붙여넣기
npx wrangler secret put GOOGLE_CLIENT_ID
npx wrangler secret put GOOGLE_CLIENT_SECRET
```

## 4. Google OAuth 클라이언트 설정

1. Google Cloud Console → API 및 서비스 → OAuth 동의 화면 → **테스트(외부)** 모드로 생성, 테스트 사용자에 본인 이메일 추가.
2. 사용자 인증 정보 → OAuth 클라이언트 ID(웹 애플리케이션) 생성.
3. **승인된 리디렉션 URI**에 다음을 등록:
   - 로컬 개발: `http://localhost:8787/auth/callback`
   - 배포 후: `https://<워커 도메인>/auth/callback`
4. 클라이언트 ID/시크릿을 3단계의 secret 으로 등록.

## 5. 허용 이메일(allowlist) 등록

```powershell
npx wrangler kv key put --binding=DEVICES allowlist '["you@gmail.com","teammate@gmail.com"]'
```

## 6. 로컬 실행 (사용자가 직접, 본 세션에서는 실행하지 않음)

```powershell
npm run dev
```

## 7. 배포 (사용자가 직접, 본 세션에서는 실행하지 않음)

```powershell
npm run deploy
```

배포 후 `wrangler.jsonc` 의 `compatibility_date` 를 배포일 기준 최신 날짜로 갱신할 것.

## 구조

```
relay/
  wrangler.jsonc        Worker 설정(assets/DO/KV 바인딩, secret 목록 주석)
  src/
    index.js            라우터 진입점
    device-hub.js        Durable Object(DeviceHub): clientId 봉투 브리지, WS hibernation
    kv.js                 device/owner/pair/allowlist KV 스키마 접근
    rateLimit.js          로그인/페어링 rate-limit
    util.js               코드/토큰 생성, 해시, PKCE
    auth/
      session.js          세션 JWT(HS256) 서명/검증 + 쿠키
      oauth.js             Google OAuth 로그인/콜백(state+PKCE)
    routes/
      devices.js           /api/devices, /api/session
      pairing.js            /api/pair/start, /poll, /approve
      ws.js                 /client/{deviceId}, /device/{deviceId} 인증 게이트 + DO 라우팅
  public/                 정적 자산(Cloudflare Workers Assets)
    index.html/js/css     로그인 후 PC 목록 포털
    pair.html/js          PC 페어링 승인 화면
    app/                  대시보드 SPA(/app/{deviceId}) — 원본 Resources/Dashboard/web 복사·수정본
    terminal-assets/      xterm.js/css, Pretendard 폰트(원본에서 복사)
    agent-assets/         에이전트 아이콘(원본에서 복사)
```

## PC(DevezCode) 쪽 계약

- 아웃바운드 연결: `wss://<relay>/device/{deviceId}`, 헤더 `X-Device-Secret: <deviceSecret 평문>`.
- 페어링: `POST /api/pair/start` → `{userCode, verificationUrl}` 수신 → 사용자가 `verificationUrl` 에서 로그인·승인 →
  `POST /api/pair/poll {userCode}` 폴링 → `{status:"approved", deviceId, deviceSecret}` 1회 수신 후 로컬 저장.
- 봉투(스펙 4.1): PC→DO 는 `{clientId, kind:"msg", payload}`(`clientId:"*"` 는 전체 브로드캐스트),
  DO→PC 는 `{clientId, kind:"join"|"leave"|"msg", payload}`.

## 미결/후속 필요사항

- 실제 Google OAuth 클라이언트 ID/시크릿 발급 및 등록(현재 자리표시자 없음, secret 미설정 상태로는 로그인 불가).
- KV 네임스페이스 실제 생성 및 `wrangler.jsonc` 의 `id` 교체.
- allowlist 이메일 등록.
- 로컬 `wrangler dev` / 실배포 검증(스펙 9절) — 사용자 복귀 후 진행.
- 데스크톱 쪽 `RelayConnector`가 `X-Device-Secret` 헤더로 접속하는지 상호 확인 필요(연동 지점).
- 백프레셔/드롭 정책은 초기엔 unbounded(스펙 6절 명시), 필요 시 후속 개선.
