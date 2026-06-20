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

  // user message 에서 text 추출 — opencode export JSON 과 동일 구조 가정.
  // (info.parts[].type==="text".text) 또는 (info.content 문자열) 모두 지원.
  const extractUserText = (info) => {
    if (!info) return "";
    if (typeof info.content === "string" && info.content) return info.content;
    if (Array.isArray(info.content)) {
      for (const c of info.content) {
        if (c && (c.type === "text" || c.type === "input_text") && c.text) return c.text;
      }
    }
    if (Array.isArray(info.parts)) {
      for (const p of info.parts) {
        if (p && p.type === "text" && p.text) return p.text;
      }
    }
    return "";
  };

  // 어떤 event 가 오는지 + role 필드 위치를 파악하기 위한 카운터
  let eventCount = 0;
  const roleHit = { user: 0, assistant: 0, other: 0, missing: 0 };

  return {
    event: async ({ event }) => {
      try {
        if (!event || !event.type) return;
        // 처음 20개 event 만 구조 로깅 (이후 노이즈 방지)
        if (eventCount < 20) {
          const props = event.properties || {};
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
          const info = event.properties && event.properties.info;
          if (info && info.id) writeId(info.id);
        }
        // 메시지 이벤트 — sessionID 백업 채널 + user prompt 추출.
        else if (event.type === "message.updated" || event.type === "message.part.updated") {
          const props = event.properties || {};
          if (props.sessionID) writeId(props.sessionID);
          const info = props.info;
          // role 위치 추정: info.role, info.roleName, info.type, 또는 최상위 role
          const role = (info && (info.role || info.roleName || info.type)) || props.role || "<none>";
          if (role === "user" || role === "human") {
            roleHit.user++;
            const text = extractUserText(info);
            if (text) writeLastmsg(text);
            else debug(`role=user but text empty; info keys=${info ? Object.keys(info).join(",") : "null"}`);
          } else if (role === "assistant") {
            roleHit.assistant++;
          } else {
            roleHit.other++;
          }
        }
      } catch (e) { debug(`event handler error: ${e.message}`); }
    },
    "chat.message": async (input) => {
      try {
        if (input && input.sessionID) writeId(input.sessionID);
        if (input && input.message && input.message.role === "user") {
          const text = extractUserText(input.message);
          if (text) writeLastmsg(text);
        }
      } catch (e) { debug(`chat.message error: ${e.message}`); }
    },
  };
};
