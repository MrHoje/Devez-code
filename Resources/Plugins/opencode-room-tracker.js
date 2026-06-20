// DevezCode OpenCode room session tracker
// 채팅방(roomId)별 현재 세션 ID 를 기록해 앱이 방마다 정확한 세션을 복원하게 한다.
// DEVEZCODE_ROOM_ID 가 없으면(사용자가 직접 쓰는 opencode) 아무 동작도 하지 않는다.
export const DevezCodeRoomTracker = async () => {
  const room = process.env.DEVEXCODE_ROOM_ID;
  const write = (id) => {
    try {
      if (!room || !id) return;
      const fs = require("node:fs");
      const path = require("node:path");
      const os = require("node:os");
      const base = process.env.APPDATA || path.join(os.homedir(), "AppData", "Roaming");
      const safe = String(room).replace(/[^\w\-]/g, "");
      const dir = path.join(base, "DevezCode", "opencode", "sessions");
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(path.join(dir, safe + ".txt"), String(id));
    } catch (e) {}
  };
  return {
    event: async ({ event }) => {
      try {
        // 세션 생성/갱신 이벤트 모두 잡아 최신 ID 덮어씀 (--clear·새 대화 시작 시 자동 갱신).
        if (event && (event.type === "session.created" || event.type === "session.updated")) {
          const info = event.properties && event.properties.info;
          if (info && info.id) write(info.id);
        }
        // 메시지 이벤트에도 sessionID 가 들어옴 — 백업 채널.
        else if (event && (event.type === "message.updated" || event.type === "message.part.updated")) {
          const id = event.properties && event.properties.sessionID;
          if (id) write(id);
        }
      } catch (e) {}
    },
    "chat.message": async (input) => {
      try { if (input && input.sessionID) write(input.sessionID); } catch (e) {}
    },
  };
};
