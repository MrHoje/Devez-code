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

  // 어떤 event 가 오는지 + role 필드 위치를 파악하기 위한 카운터
  let eventCount = 0;

  return {
    event: async ({ event }) => {
      try {
        if (!event || !event.type) return;
        const props = event.properties || {};
        // 처음 20개 event 만 구조 로깅 (이후 노이즈 방지)
        if (eventCount < 20) {
          const info = props.info || {};
          const roleInfo = info.role ?? info.roleName ?? info.roleType ?? "<no-role>";
          const keys = Object.keys(props).slice(0, 8).join(",");
          debug(`event[${eventCount}] type=${event.type} props=[${keys}] info.role=${roleInfo}` +
                (info.id ? ` id=${info.id}` : "") +
                (props.sessionID ? ` sessionID=${props.sessionID}` : ""));
          eventCount++;
        }

        // 세션 생성/갱신 이벤트 — 최신 ID 덮어씀 (--clear·새 대화 시작 시 자동 갱신).
        if (event.type === "session.created" || event.type === "session.updated") {
          const info = props.info;
          if (info && info.id) writeId(info.id);
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
          if (role === "user") writeLastmsg(part.text);
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
        }
      } catch (e) { debug(`chat.message error: ${e.message}`); }
    },
  };
};
