// DevezCode OpenCode room session tracker + last message
// 채팅방(roomId)별 현재 세션 ID 와 마지막 user prompt 를 기록해
// 1) 재오픈 시 같은 세션 복원 (sessions\<room>.txt)
// 2) 헤더 타이틀에 마지막 메시지 즉시 표시 (lastmsg\<room>.txt, claude busy hook 과 동일 패턴)
// DEVEZCODE_ROOM_ID 가 없으면(사용자가 직접 쓰는 opencode) 아무 동작도 하지 않는다.
//
// 진단 로그: %APPDATA%\DevezCode\opencode\plugin-debug.log
//   - 플러그인 로드 시점, 첫 20개 event 의 type+구조, lastmsg 저장 성공/실패를 기록.
export const DevezCodeRoomTracker = async () => {
  const room = process.env.DEVEZCODE_ROOM_ID;
  const fs = require("node:fs");
  const path = require("node:path");
  const os = require("node:os");
  const base = process.env.APPDATA || path.join(os.homedir(), "AppData", "Roaming");
  const safe = String(room || "").replace(/[^\w\-]/g, "");
  const logPath = path.join(base, "DevezCode", "opencode", "plugin-debug.log");

  const debug = (msg) => {
    try {
      fs.mkdirSync(path.dirname(logPath), { recursive: true });
      fs.appendFileSync(logPath, `[${new Date().toISOString()}] ${msg}\n`);
    } catch (e) {}
  };

  debug(`plugin loaded; DEVEZCODE_ROOM_ID=${room ? "set" : "EMPTY"} (safe=${safe})`);

  const writeId = (id) => {
    try {
      if (!safe || !id) return;
      const dir = path.join(base, "DevezCode", "opencode", "sessions");
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(path.join(dir, safe + ".txt"), String(id));
    } catch (e) { debug(`writeId failed: ${e.message}`); }
  };

  // busy\<room>.txt = running|idle — 요청 처리중 스피너. claude busy hook 과 동일 패턴.
  // user 프롬프트 전송 → running, session.idle/error → idle.
  const writeBusy = (state) => {
    try {
      if (!safe) return;
      const dir = path.join(base, "DevezCode", "opencode", "busy");
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(path.join(dir, safe + ".txt"), state);
      debug(`busy=${state}`);
    } catch (e) { debug(`writeBusy failed: ${e.message}`); }
  };

  // opencode 는 한 턴 안에서도 스텝(도구 실행) 사이마다 session.idle 을 (연달아) 쏘고 곧바로 다시
  // 생성을 재개한다. 그때마다 idle 을 쓰면 좌측 스피너가 깜빡이고, 무엇보다 DevezCode 가 busy→idle
  // 전이를 "응답 완료" 로 잡아 한 턴에 알림이 여러 번 뜬다. → idle 을 디바운스한다: session.idle 은
  // 타이머만 (재)설정하고, 그 사이 running(사용자/재개) 이 오면 취소한다. 잠깐 쉬었다 재개하는 중간
  // idle 은 모두 흡수되고, 진짜 턴 종료(이후 재개 없음)에서만 idle 이 한 번 기록된다.
  const IDLE_DEBOUNCE_MS = 2500;
  let idleTimer = null;
  const cancelIdle = () => { if (idleTimer) { clearTimeout(idleTimer); idleTimer = null; } };
  const setRunning = () => { cancelIdle(); writeBusy("running"); };
  const scheduleIdle = () => {
    cancelIdle();
    idleTimer = setTimeout(() => { idleTimer = null; writeBusy("idle"); }, IDLE_DEBOUNCE_MS);
  };

  const writeLastmsg = (text) => {
    try {
      if (!safe || !text) return;
      const dir = path.join(base, "DevezCode", "opencode", "lastmsg");
      fs.mkdirSync(dir, { recursive: true });
      let compact = String(text).replace(/\s+/g, " ").trim();
      if (compact.length > 200) compact = compact.substring(0, 200);
      if (compact) {
        fs.writeFileSync(path.join(dir, safe + ".txt"), compact);
        debug(`lastmsg saved (${compact.length} chars)`);
      }
    } catch (e) { debug(`writeLastmsg failed: ${e.message}`); }
  };

  // 새 세션(/clear·/new) → lastmsg 를 빈 문자열로 덮어써 헤더를 세션명으로 되돌린다.
  // (claude 는 "/clear" 텍스트가 user 메시지로 흘러 자동 처리되지만 opencode 는 안 흘러서 명시적으로 비움)
  const clearLastmsg = () => {
    try {
      if (!safe) return;
      const dir = path.join(base, "DevezCode", "opencode", "lastmsg");
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(path.join(dir, safe + ".txt"), "");
      debug("lastmsg cleared (new session)");
    } catch (e) { debug(`clearLastmsg failed: ${e.message}`); }
  };

  // message.updated 에서 본 messageID → "user"/"assistant" 매핑. message.part.updated 가
  // user prompt 텍스트를 따로 떨어뜨릴 때 부모가 user 인지 빠르게 판별하는 데 쓴다.
  // (opencode 1.17.x 는 message.updated 의 info 에 role 이 있고 텍스트는 별도 part 이벤트로 흐름)
  const messageRole = {};

  // user message 의 parts 들에서 text 추출. opencode export JSON 의 part 구조와 동일.
  const extractTextFromParts = (parts) => {
    if (!Array.isArray(parts)) return "";
    for (const p of parts) {
      if (p && p.type === "text" && p.text) return p.text;
    }
    return "";
  };

  // todo\\<room>.json — todowrite 툴로 생성된 태스크 목록을 기록. DevezCode Task View 에서 읽는다.
  const writeTodos = (todos) => {
    try {
      if (!safe) return;
      const dir = path.join(base, "DevezCode", "opencode", "todos");
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(path.join(dir, safe + ".json"), JSON.stringify(todos, null, 2));
      debug(`todos saved (${Array.isArray(todos) ? todos.length : 0} items)`);
    } catch (e) { debug(`writeTodos failed: ${e.message}`); }
  };

  // 어떤 event 가 오는지 + role 필드 위치를 파악하기 위한 카운터
  let eventCount = 0;

  return {
    event: async ({ event }) => {
      try {
        if (!event || !event.type) return;
        const props = event.properties || {};
        // [진단] session.* / message.updated / message.part.updated 만 항상 로깅(노이즈 제외).
        // 실제 한 턴 동안 어떤 event 가 어떤 순서/sessionID/role/completed 로 흐르는지 확보용.
        const t = event.type;
        if (t.startsWith("session.") || t === "message.updated" || t === "message.part.updated") {
          const info = props.info || {};
          const part = props.part || {};
          const sid = props.sessionID || info.sessionID || part.sessionID || "?";
          const role = info.role || messageRole[part.messageID] || "-";
          const done = info.time && info.time.completed ? "completed" : "-";
          debug(`EV ${t} sid=${sid} role=${role} time=${done}` +
                (part.type ? ` partType=${part.type}` : ""));
        }

        // todowrite 업데이트 — 태스크 목록을 JSON 파일로 기록. DevezCode Task View 에서 사용.
        if (event.type === "todo.updated") {
          writeTodos(props.todos);
        }
        // 세션 처리 종료 신호 → 스피너 끄기. session.idle = 응답 완료, session.error = 실패.
        if (event.type === "session.idle" || event.type === "session.error") {
          scheduleIdle();
        }
        // 세션 생성/갱신 이벤트 — 최신 ID 덮어씀 (--clear·새 대화 시작 시 자동 갱신).
        if (event.type === "session.created" || event.type === "session.updated") {
          const info = props.info;
          if (info && info.id) {
            // 같은 방에서 새 세션이 생성되면(/clear·/new) 이전 todos·lastmsg 를 초기화한다.
            if (event.type === "session.created") { writeTodos([]); clearLastmsg(); }
            writeId(info.id);
          }
        }
        // message.updated — 메시지 메타( role, id ) 캐시. 텍스트 본문은 여기에 없음.
        else if (event.type === "message.updated") {
          const info = props.info;
          if (info && info.id) {
            if (info.sessionID) writeId(info.sessionID);
            if (info.role) messageRole[info.id] = info.role;
          }
        }
        // message.part.updated — 실제 텍스트 본문이 여기 도착.
        //   type="text" + 부모 메시지가 user 일 때만 lastmsg 로 저장.
        else if (event.type === "message.part.updated") {
          const part = props.part;
          if (!part) return;
          // sessionID 백업 채널 — part.sessionID 가 항상 옴.
          if (part.sessionID) writeId(part.sessionID);
          if (part.type !== "text" || !part.text) return;
          // synthetic/ignored part (자동 주입된 메타 텍스트) 는 헤더에 뜨면 노이즈라 무시.
          if (part.synthetic || part.ignored) return;
          const role = messageRole[part.messageID];
          if (role === "user") {
            writeLastmsg(part.text);
            // 스피너 시작 — chat.message 훅은 버전에 따라 안 불려서(lastmsg 도 이 event 경로로 저장됨)
            // 검증된 user-part 경로에서 running 을 쓴다. session.idle/error 가 idle 로 해제.
            setRunning();
          }
        }
      } catch (e) { debug(`event handler error: ${e.message}`); }
    },
    "chat.message": async (input) => {
      try {
        if (input && input.sessionID) writeId(input.sessionID);
        if (input && input.message && input.message.role === "user") {
          // chat.message 는 message.updated 와 동시 또는 직전에 옴 → role 캐시도 함께 갱신.
          if (input.message.id) messageRole[input.message.id] = "user";
          const text = extractTextFromParts(input.message.parts);
          if (text) writeLastmsg(text);
          setRunning(); // user 프롬프트 전송 → 처리 시작 → 스피너 켜기
        }
      } catch (e) { debug(`chat.message error: ${e.message}`); }
    },
  };
};
