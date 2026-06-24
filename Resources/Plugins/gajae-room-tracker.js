// DevezCode 가재코드(gjc) room tracker extension
// 채팅방(roomId)별 busy 상태·todo 목록·마지막 user 메시지를 파일로 떨궈 앱이
// 좌측 스피너·Task 뷰·헤더 타이틀에 반영한다.
// lastmsg 를 이벤트로 직접 떨구는 이유: gjc /new 는 새 세션을 메모리에만 만들고 .jsonl 을
// 지연 기록(첫 메시지 전까지 파일 없음)해서, 앱의 jsonl 폴링으로는 /new 직후 헤더를
// 세션명으로 되돌릴 신호를 얻지 못한다. session_switch(reason=new) 로 즉시 빈값을 쓴다.
// DEVEZCODE_ROOM_ID 가 없으면(사용자가 직접 쓰는 gjc) 아무 동작도 하지 않는다.
//
// gjc 가 `-e <path>` 로 로드. 이벤트 API: pi.on("before_agent_start"|"session_switch"|...).
import * as fs from "node:fs";
import * as path from "node:path";
import * as os from "node:os";

export default function (pi) {
  const room = process.env.DEVEZCODE_ROOM_ID;
  if (!room) return; // 사용자 자신의 gjc — no-op

  const base = process.env.APPDATA || path.join(os.homedir(), "AppData", "Roaming");
  const safe = String(room).replace(/[^\w\-]/g, "");
  const busyDir = path.join(base, "DevezCode", "gajae", "busy");
  const lastmsgDir = path.join(base, "DevezCode", "gajae", "lastmsg");
  // todo 는 앱의 기존 Task 리더(TaskTrackingService)가 읽는 opencode\todos 경로에 같은 shape 로 쓴다.
  // roomId 키라 opencode 방과 충돌 없음(한 방=한 에이전트). 별도 리더 추가 없이 Task 뷰 즉시 연동.
  const todoDir = path.join(base, "DevezCode", "opencode", "todos");

  // lastmsg\<room>.txt = 마지막 보낸 user 프롬프트(1줄). 빈 문자열이면 앱이 세션명으로 표시.
  const writeLast = (text) => {
    try {
      let s = String(text == null ? "" : text).replace(/\s+/g, " ").trim();
      if (s.length > 200) s = s.slice(0, 200);
      fs.mkdirSync(lastmsgDir, { recursive: true });
      fs.writeFileSync(path.join(lastmsgDir, safe + ".txt"), s);
    } catch (e) {}
  };

  // 세션 엔트리에서 마지막 user 메시지 텍스트를 뽑는다(없으면 빈 문자열).
  const lastUserText = (entries) => {
    if (!Array.isArray(entries)) return "";
    for (let i = entries.length - 1; i >= 0; i--) {
      const e = entries[i];
      if (!e || e.type !== "message" || !e.message || e.message.role !== "user") continue;
      const c = e.message.content;
      if (typeof c === "string") return c;
      if (Array.isArray(c)) {
        const t = c.find((b) => b && b.type === "text" && b.text);
        if (t) return t.text;
      }
    }
    return "";
  };

  // 세션 시작/전환 → 헤더 동기화. reason=new(/new) 는 즉시 빈값, resume/fork·초기시작은 엔트리에서 복원.
  const syncLast = (event, ctx) => {
    try {
      if (event && event.reason === "new") { writeLast(""); return; }
      const sm = ctx && ctx.sessionManager;
      writeLast(sm && typeof sm.getEntries === "function" ? lastUserText(sm.getEntries()) : "");
    } catch (e) {}
  };

  // busy\<room>.txt = running|idle — claude/opencode busy 파일과 동일 규약.
  const writeBusy = (state) => {
    try {
      fs.mkdirSync(busyDir, { recursive: true });
      fs.writeFileSync(path.join(busyDir, safe + ".txt"), state);
    } catch (e) {}
  };

  // todos\<room>.json — opencode TodoRaw 와 동일 shape([{content,status}]) 로 평탄화.
  // gjc 의 todo_write 결과(details.phases[].tasks[]) 를 펼쳐 앱 Task 뷰가 그대로 읽게 한다.
  const writeTodos = (phases) => {
    try {
      const flat = [];
      for (const ph of phases || [])
        for (const t of (ph && ph.tasks) || [])
          if (t && t.content) flat.push({ content: t.content, status: t.status });
      fs.mkdirSync(todoDir, { recursive: true });
      fs.writeFileSync(path.join(todoDir, safe + ".json"), JSON.stringify(flat, null, 2));
    } catch (e) {}
  };

  // 한 user 프롬프트에 대한 에이전트 루프 시작/종료 → 스피너 on/off + 마지막 메시지 기록.
  pi.on("before_agent_start", (event) => { writeBusy("running"); writeLast(event && event.prompt); });
  pi.on("agent_start", () => writeBusy("running"));
  pi.on("agent_end", () => writeBusy("idle"));
  pi.on("session_shutdown", () => writeBusy("idle"));

  // 세션 전환/시작 → 헤더 동기화(/new 즉시 빈값, resume 복원). autoresearch 확장과 동일한 두 이벤트 구독.
  pi.on("session_start", syncLast);
  pi.on("session_switch", syncLast);

  // todo_write 결과 스냅샷(details.phases) 기록. op 기반이라 결과 phases 를 그대로 쓴다.
  pi.on("tool_result", (event) => {
    try {
      if (event && event.toolName === "todo_write" && event.details && Array.isArray(event.details.phases))
        writeTodos(event.details.phases);
    } catch (e) {}
  });
}
