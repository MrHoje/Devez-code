// DevezCode OpenCode room session tracker + last message
// 채팅방(roomId)별 현재 세션 ID 와 마지막 user prompt 를 기록해
// 1) 재오픈 시 같은 세션 복원 (sessions\<room>.txt)
// 2) 헤더 타이틀에 마지막 메시지 즉시 표시 (lastmsg\<room>.txt, claude busy hook 과 동일 패턴)
// DEVEZCODE_ROOM_ID 가 없으면(사용자가 직접 쓰는 opencode) 아무 동작도 하지 않는다.
//
// 진단이 필요할 때만 DEVEZCODE_OPENCODE_PLUGIN_DEBUG=1 로 실행하면
// %APPDATA%\DevezCode\opencode\plugin-debug.log 에 이벤트 흐름을 기록한다.
//
// busy 규칙 (claude SubagentStart/Stop 과 동형):
//   room busy = (루트 턴 진행중) OR (살아 있는 child session > 0)
// oh-my-openagent task/explore 등은 parentID 있는 child 를 만들고, 그 동안 parent 가
// session.idle 을 낼 수 있다. child 가 살아 있으면 idle 확정을 막고 스피너를 유지한다.
export const DevezCodeRoomTracker = async (_ctx) => {
  const room = process.env.DEVEZCODE_ROOM_ID;
  const safe = String(room || "").replace(/[^\w\-]/g, "");
  // 플러그인은 OpenCode 전역 설정에서 로드된다. DevezCode가 띄운 세션이 아니면
  // 파일 모듈 로드·클라이언트 조회·이벤트 핸들러 등록·진단 로그를 전부 생략한다.
  if (!safe) return {};

  const client = _ctx && _ctx.client;
  const fs = require("node:fs");
  const path = require("node:path");
  const os = require("node:os");
  const base = process.env.APPDATA || path.join(os.homedir(), "AppData", "Roaming");
  const logPath = path.join(base, "DevezCode", "opencode", "plugin-debug.log");
  const debugEnabled = process.env.DEVEZCODE_OPENCODE_PLUGIN_DEBUG === "1";

  // 로그 상한 1MB — message.part.updated 가 스트리밍 청크마다 발화해 무한 증식하므로
  // 초과 시 .1 로 로테이션(직전 1MB 는 진단용으로 보존, 그 이전은 폐기).
  const MAX_LOG_BYTES = 1024 * 1024;
  const debug = (msg) => {
    if (!debugEnabled) return;
    try {
      fs.mkdirSync(path.dirname(logPath), { recursive: true });
      try {
        if (fs.existsSync(logPath) && fs.statSync(logPath).size > MAX_LOG_BYTES) {
          fs.rmSync(logPath + ".1", { force: true });
          fs.renameSync(logPath, logPath + ".1");
        }
      } catch (e) {}
      fs.appendFileSync(logPath, `[${new Date().toISOString()}] ${msg}\n`);
    } catch (e) {}
  };

  debug(`plugin loaded; room=${safe}`);

  // Every state/tracking write uses temp+rename. Direct writeFileSync truncates the
  // destination first; if OpenCode exits in that window a permanent zero-byte file
  // can leave the spinner or attention mark stuck until another event arrives.
  const writeAtomic = (target, value) => {
    const tmp = `${target}.${process.pid}.${Date.now()}.${Math.random().toString(16).slice(2)}.tmp`;
    try {
      fs.writeFileSync(tmp, String(value));
      fs.renameSync(tmp, target);
    } finally {
      try { fs.rmSync(tmp, { force: true }); } catch (e) {}
    }
  };

  const writeId = (id) => {
    try {
      if (!safe || !id) return;
      const dir = path.join(base, "DevezCode", "opencode", "sessions");
      fs.mkdirSync(dir, { recursive: true });
      writeAtomic(path.join(dir, safe + ".txt"), String(id));
    } catch (e) { debug(`writeId failed: ${e.message}`); }
  };

  // task/explore 서브에이전트도 부모 프로세스의 DEVEZCODE_ROOM_ID를 상속하고 이 플러그인의
  // 이벤트 스트림에 나타난다. parentID가 있는 세션은 방의 루트 대화가 아니므로
  // lastmsg/세션 추적에는 쓰지 않고, busy 만 "살아 있는 child" 로 합산한다.
  let rootSessionId = null;
  const nestedSessionIds = new Set();
  // childId → lastSeenMs. 훅 누락/크래시 잔재는 CHILD_STALE_MS 후 폐기.
  const liveChildren = new Map();
  const childSessionById = new Map();
  const CHILD_STALE_MS = 30 * 60 * 1000;

  const parentOf = (info) => {
    if (!info) return null;
    const p = info.parentID || info.parentId || info.parent_id;
    return p ? String(p) : null;
  };

  const pruneStaleChildren = () => {
    if (liveChildren.size === 0) return;
    const now = Date.now();
    for (const [id, seen] of liveChildren) {
      if (now - seen > CHILD_STALE_MS) {
        liveChildren.delete(id);
        debug(`child stale pruned: ${id}`);
      }
    }
  };

  // child 생존 중에는 방 스피너를 켠 채로 유지 (claude busy = main OR subruns).
  const markChildAlive = (childId, parentId) => {
    if (!childId) return;
    const cid = String(childId);
    const pid = parentId ? String(parentId) : null;
    nestedSessionIds.add(cid);
    childSessionById.set(cid, true);
    if (pid) {
      if (!rootSessionId) {
        rootSessionId = pid;
        writeId(rootSessionId);
      }
      // 우리 루트의 자식만 합산. 다른 방/세션 child 는 무시.
      if (rootSessionId && pid !== rootSessionId) {
        debug(`child ignored (other parent): ${cid} parent=${pid} root=${rootSessionId}`);
        return;
      }
    }
    const wasEmpty = liveChildren.size === 0;
    liveChildren.set(cid, Date.now());
    // 서브에이전트 가동 = 원 프롬프트 미완료. 조기 idle 펜스를 풀어 스피너를 유지/재무장.
    turnCompleted = false;
    rootIdle = false;
    cancelIdle();
    writeBusy("running");
    debug(`child alive: ${cid} (live=${liveChildren.size}${wasEmpty ? ", re-arm" : ""})`);
  };

  const markChildDone = (childId) => {
    if (!childId) return;
    const cid = String(childId);
    if (!liveChildren.has(cid)) return;
    liveChildren.delete(cid);
    debug(`child done: ${cid} (live=${liveChildren.size})`);
    // 부모가 이미 idle 이고 마지막 child 가 끝났을 때만 완료 후보.
    if (liveChildren.size === 0 && rootIdle) scheduleIdle();
  };

  const acceptRootSession = (info) => {
    if (!info || !info.id) return false;
    const parentId = parentOf(info);
    if (parentId) {
      nestedSessionIds.add(String(info.id));
      markChildAlive(info.id, parentId);
      debug(`nested session ignored: ${info.id} (root=${rootSessionId})`);
      return false;
    }
    if (nestedSessionIds.has(String(info.id))) return false;
    rootSessionId = String(info.id);
    return true;
  };

  // sessionID 가 없거나 root 미확정이면 보수적으로 root 후보로 통과시키되,
  // root 가 확정된 뒤 다른 id 는 isChildSession / nested 로 걸러진다.
  const isRootSession = (sessionId) => {
    if (!sessionId) return true;
    if (nestedSessionIds.has(String(sessionId)) || liveChildren.has(String(sessionId))) return false;
    if (!rootSessionId) return true;
    return String(sessionId) === rootSessionId;
  };

  // busy\<room>.txt = running|idle — 요청 처리중 스피너. claude busy hook 과 동일 패턴.
  // user 프롬프트 전송 → running, session.idle/error → idle (child 없으면).
  const writeBusy = (state) => {
    try {
      if (!safe) return;
      const dir = path.join(base, "DevezCode", "opencode", "busy");
      fs.mkdirSync(dir, { recursive: true });
      writeAtomic(path.join(dir, safe + ".txt"), state);
      debug(`busy=${state}`);
    } catch (e) { debug(`writeBusy failed: ${e.message}`); }
  };

  // opencode 는 한 턴 안에서도 스텝(도구 실행) 사이마다 session.idle 을 (연달아) 쏘고 곧바로 다시
  // 생성을 재개한다. 그때마다 idle 을 쓰면 좌측 스피너가 깜빡이고, 무엇보다 DevezCode 가 busy→idle
  // 전이를 "응답 완료" 로 잡아 한 턴에 알림이 여러 번 뜬다. → idle 을 디바운스한다: session.idle 은
  // 타이머만 (재)설정하고, 그 사이 running(사용자/재개) 이 오면 취소한다. 잠깐 쉬었다 재개하는 중간
  // idle 은 모두 흡수되고, 진짜 턴 종료(이후 재개 없음)에서만 idle 이 한 번 기록된다.
  // + child 가 살아 있으면 idle 확정을 보류한다 (서브에이전트 대기 중 조기 소등 방지).
  const IDLE_DEBOUNCE_MS = 2500;
  let idleTimer = null;
  let turnCompleted = false;
  let rootIdle = true; // 루트 세션이 idle 신호를 낸 상태 (child 와 독립)
  let lastStartedUserMessageId = null;
  const cancelIdle = () => { if (idleTimer) { clearTimeout(idleTimer); idleTimer = null; } };

  // 검증된 root user message 로만 새 턴을 연다. 같은 messageId 의 중복 part 는 무시.
  const beginTurn = (messageId) => {
    if (messageId && turnCompleted && String(messageId) === lastStartedUserMessageId) return;
    if (messageId) lastStartedUserMessageId = String(messageId);
    turnCompleted = false;
    rootIdle = false;
    cancelIdle();
    writeBusy("running");
  };

  // session.status busy/retry — 중간 idle 뒤 재개. 조기 turnCompleted 펜스를 풀고 재무장한다.
  // (과거: turnCompleted 면 무시 → 서브 대기 중 한 번 idle 확정되면 스피너가 영구 소등)
  const continueTurn = () => {
    turnCompleted = false;
    rootIdle = false;
    cancelIdle();
    writeBusy("running");
  };

  const scheduleIdle = () => {
    pruneStaleChildren();
    rootIdle = true;
    if (liveChildren.size > 0) {
      // 부모는 쉬어도 child 가 돌면 방 busy 유지.
      cancelIdle();
      turnCompleted = false;
      writeBusy("running");
      debug(`idle deferred: liveChildren=${liveChildren.size}`);
      return;
    }
    cancelIdle();
    idleTimer = setTimeout(() => {
      idleTimer = null;
      pruneStaleChildren();
      // 디바운스 중 child 가 생기거나 루트가 재개되면 확정하지 않음.
      if (liveChildren.size > 0 || !rootIdle) {
        debug(`idle commit skipped: live=${liveChildren.size} rootIdle=${rootIdle}`);
        if (liveChildren.size > 0) writeBusy("running");
        return;
      }
      turnCompleted = true;
      writeBusy("idle");
    }, IDLE_DEBOUNCE_MS);
  };

  // waiting\<room>.txt = waiting|idle — 선택지(question.asked) 응답 대기 ❗. busy 와 동일 파일 패턴.
  let awaitingAnswer = false;
  const writeWaiting = (state) => {
    try {
      if (!safe) return;
      const dir = path.join(base, "DevezCode", "opencode", "waiting");
      fs.mkdirSync(dir, { recursive: true });
      writeAtomic(path.join(dir, safe + ".txt"), state);
      debug(`waiting=${state}`);
    } catch (e) { debug(`writeWaiting failed: ${e.message}`); }
  };
  const setWaiting = () => { awaitingAnswer = true; writeWaiting("waiting"); };
  const clearWaiting = () => { if (awaitingAnswer) { awaitingAnswer = false; writeWaiting("idle"); } };

  const writeLastmsg = (text) => {
    try {
      if (!safe || !text) return;
      const dir = path.join(base, "DevezCode", "opencode", "lastmsg");
      fs.mkdirSync(dir, { recursive: true });
      let compact = String(text).replace(/\s+/g, " ").trim();
      if (compact.length > 200) compact = compact.substring(0, 200);
      if (compact) {
        writeAtomic(path.join(dir, safe + ".txt"), compact);
        debug(`lastmsg saved (${compact.length} chars)`);
      }
    } catch (e) { debug(`writeLastmsg failed: ${e.message}`); }
  };

  // lastreply\<room>.txt = 마지막 assistant 답변 텍스트
  // (claude 의 lastreply hook 과 동일 패턴). 한 assistant 메시지의 text part 들을 합쳐 기록.
  const writeLastreply = (text) => {
    try {
      if (!safe) return;
      const dir = path.join(base, "DevezCode", "opencode", "lastreply");
      fs.mkdirSync(dir, { recursive: true });
      writeAtomic(path.join(dir, safe + ".txt"), String(text || ""));
    } catch (e) { debug(`writeLastreply failed: ${e.message}`); }
  };

  // 현재 assistant 메시지의 text part 누적(part.id → text). 새 assistant 메시지가 오면 비운다.
  let asstMsgId = null;
  const asstParts = {};

  // 새 세션(/clear·/new) → lastmsg 를 빈 문자열로 덮어써 헤더를 세션명으로 되돌린다.
  // (claude 는 "/clear" 텍스트가 user 메시지로 흘러 자동 처리되지만 opencode 는 안 흘러서 명시적으로 비움)
  const clearLastmsg = () => {
    try {
      if (!safe) return;
      const dir = path.join(base, "DevezCode", "opencode", "lastmsg");
      fs.mkdirSync(dir, { recursive: true });
      writeAtomic(path.join(dir, safe + ".txt"), "");
      debug("lastmsg cleared (new session)");
    } catch (e) { debug(`clearLastmsg failed: ${e.message}`); }
  };

  // message.updated 에서 본 messageID → "user"/"assistant" 매핑. message.part.updated 가
  // user prompt 텍스트를 따로 떨어뜨릴 때 부모가 user 인지 빠르게 판별하는 데 쓴다.
  // (opencode 1.17.x 는 message.updated 의 info 에 role 이 있고 텍스트는 별도 part 이벤트로 흐름)
  const messageRole = {};

  // oh-my-opencode style tools spawn child sessions that emit the same idle,
  // permission and message events as the room's root session. Filter them so a
  // child completion cannot turn off the parent's spinner or overwrite its prompt.
  // Older OpenCode versions may call the plugin factory without a client; in that
  // compatibility mode preserve the old behavior instead of dropping every event.
  let warnedChildLookup = false;
  const isChildSession = async (sessionID) => {
    if (!sessionID) return false;
    const sid = String(sessionID);
    if (nestedSessionIds.has(sid) || liveChildren.has(sid)) return true;
    if (rootSessionId && sid === rootSessionId) return false;
    if (!client || !client.session || !client.session.list) {
      // 클라이언트 없으면 root 확정 후 다른 id 는 child 로 간주 (idle 누수 방지).
      return !!(rootSessionId && sid !== rootSessionId);
    }
    if (childSessionById.has(sid)) return childSessionById.get(sid);
    try {
      const result = await client.session.list();
      const sessions = Array.isArray(result && result.data) ? result.data :
        (Array.isArray(result) ? result : []);
      const session = sessions.find((entry) => entry && entry.id === sid);
      // list 레이스: root 확정 뒤 미지 id 는 fail-closed (child 로 취급) — 조기 idle 이
      // 한 건의 이벤트 누락보다 치명적이다. 다음 이벤트에서 list 가 채워지면 재평가.
      if (!session) {
        if (rootSessionId && sid !== rootSessionId) return true;
        return false;
      }
      const isChild = !!(session.parentID || session.parentId || session.parent_id);
      if (childSessionById.size >= 128) {
        const first = childSessionById.keys().next().value;
        if (first !== undefined) childSessionById.delete(first);
      }
      childSessionById.set(sid, isChild);
      // nested 표식만. live 합산은 created/status busy/message 활동에서만 한다
      // (list 에 남은 종료 child 를 다시 live 로 올리면 스피너가 안 꺼짐).
      if (isChild) nestedSessionIds.add(sid);
      return isChild;
    } catch (e) {
      // With a modern SDK, fail closed for status accuracy: an unknown child idle
      // is more damaging than dropping one transient event. A later event retries.
      if (!warnedChildLookup) {
        warnedChildLookup = true;
        debug(`child session lookup failed: ${e && e.message ? e.message : e}`);
      }
      return true;
    }
  };

  // child 전용 이벤트: lastmsg/세션 파일은 건드리지 않고 생존 집합만 갱신.
  const handleChildEvent = (t, sessionID, props) => {
    if (!sessionID) return;
    const sid = String(sessionID);
    if (t === "session.status") {
      const statusType = (props.status && props.status.type) || null;
      if (statusType === "busy" || statusType === "retry") markChildAlive(sid, rootSessionId);
      else if (statusType === "idle") markChildDone(sid);
      return;
    }
    if (t === "session.idle" || t === "session.error") {
      markChildDone(sid);
      return;
    }
    if (t === "session.deleted") {
      markChildDone(sid);
      nestedSessionIds.add(sid); // 재사용 방지
      return;
    }
    // 메시지/파트 활동 = child 가 아직 작업 중. created 를 놓친 경우에도 live 합산.
    if (t === "message.updated" || t === "message.part.updated" || t === "chat.message") {
      markChildAlive(sid, rootSessionId);
    }
  };

  const eventSessionId = (type, props) => {
    const info = props.info || {};
    const part = props.part || {};
    return props.sessionID || info.sessionID || part.sessionID ||
      ((type === "session.created" || type === "session.updated" || type === "session.deleted") ? info.id : null);
  };

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
      writeAtomic(path.join(dir, safe + ".json"), JSON.stringify(todos, null, 2));
      debug(`todos saved (${Array.isArray(todos) ? todos.length : 0} items)`);
    } catch (e) { debug(`writeTodos failed: ${e.message}`); }
  };

  // part 이벤트 진단 로그 상한 — 스트리밍 청크마다 오는 message.part.updated 는
  // 처음 50건만 로깅(구조 파악엔 충분, 이후는 디스크 churn 만 유발).
  let partEvLogged = 0;
  const PART_EV_LOG_MAX = 50;

  return {
    event: async ({ event }) => {
      try {
        if (!event || !event.type) return;
        const props = event.properties || {};
        const t = event.type;

        const info = props.info || {};
        const part = props.part || {};

        // Cache the role before the async child-session lookup. OpenCode emits
        // message.updated and message.part.updated back-to-back; awaiting first
        // would let the part race ahead and lose its user/assistant identity.
        if (t === "message.updated") {
          if (info.id && info.role) messageRole[info.id] = info.role;
        }

        const sessionID = eventSessionId(t, props);

        // Prefer parentID carried by created/updated (works on old SDKs), then
        // use the client lookup for child events that arrive without creation.
        if (t === "session.created" || t === "session.updated") {
          if (!acceptRootSession(info)) return; // child: markChildAlive 후 종료
        } else if (t === "session.deleted") {
          const delId = info.id || sessionID;
          if (delId && (nestedSessionIds.has(String(delId)) || liveChildren.has(String(delId)) ||
              (rootSessionId && String(delId) !== rootSessionId && await isChildSession(delId)))) {
            handleChildEvent(t, delId, props);
            return;
          }
          // root 삭제 처리는 아래에서
        } else {
          if (sessionID && (nestedSessionIds.has(String(sessionID)) || liveChildren.has(String(sessionID)))) {
            handleChildEvent(t, sessionID, props);
            return;
          }
          if (sessionID && await isChildSession(sessionID)) {
            handleChildEvent(t, sessionID, props);
            return;
          }
          if (!isRootSession(sessionID)) return;
        }

        // 선택지와 툴 권한은 실제 사용자 입력 경계다. 완료 전 turn에서만 waiting을
        // 무장하고, 완료 뒤 늦게 도착한 동일 이벤트는 무시한다.
        if (t === "question.asked" || t === "permission.asked") {
          if (!turnCompleted) setWaiting();
          return;
        }
        // 진단 모드에서만 실제 한 턴의 event 순서/sessionID/role/completed를 기록한다.
        const logPart = debugEnabled && t === "message.part.updated" && partEvLogged < PART_EV_LOG_MAX;
        if (debugEnabled && (t.startsWith("session.") || t === "message.updated" || logPart)) {
          if (logPart) partEvLogged++;
          const sid = sessionID || "?";
          const role = info.role || messageRole[part.messageID] || "-";
          const done = info.time && info.time.completed ? "completed" : "-";
          debug(`EV ${t} sid=${sid} role=${role} time=${done}` +
                 (part.type ? ` partType=${part.type}` : "") +
                 (liveChildren.size ? ` liveChildren=${liveChildren.size}` : ""));
        }

        // todowrite 업데이트 — 태스크 목록을 JSON 파일로 기록. DevezCode Task View 에서 사용.
        if (event.type === "todo.updated") {
          writeTodos(props.todos);
        }
        // OpenCode 1.17+의 권위 있는 재개 신호. 중간 session.idle 뒤 busy/retry가
        // 오면 2.5초 idle 타이머를 취소해 작업 중 스피너가 꺼지는 것을 막는다.
        if (event.type === "session.status") {
          const statusType = (props.status && props.status.type) ||
            (event.status && event.status.type) || null;
          if (statusType === "busy" || statusType === "retry") {
            clearWaiting();
            continueTurn();
          } else if (statusType === "idle") {
            scheduleIdle();
          }
        }
        // 세션 처리 종료 신호 → 스피너 끄기. session.idle = 응답 완료, session.error = 실패.
        // (live child 가 있으면 scheduleIdle 이 확정을 보류한다)
        if (event.type === "session.idle" || event.type === "session.error") {
          scheduleIdle();
          clearWaiting(); // 턴 종료 = 더 이상 선택지 대기 아님
        }
        // 세션 삭제 — 추적 중인 세션이 지워졌으면 추적 파일을 비운다.
        // 그대로 두면 다음 실행이 `--session <삭제된 id>` 로 실패 후에야 폴백해 오류가 스치고,
        // 비워두면 launch 가 cwd 매칭 폴백으로 같은 폴더의 남은 최신 대화를 바로 복원한다.
        if (event.type === "session.deleted") {
          try {
            const delId = props.info && props.info.id;
            if (safe && delId) {
              const p = path.join(base, "DevezCode", "opencode", "sessions", safe + ".txt");
              if (fs.existsSync(p) && fs.readFileSync(p, "utf8").trim() === String(delId)) {
                writeAtomic(p, "");
                debug(`tracked session cleared (deleted: ${delId})`);
              }
              if (rootSessionId && String(delId) === rootSessionId) {
                liveChildren.clear();
                cancelIdle();
                turnCompleted = true;
                rootIdle = true;
                writeBusy("idle");
              }
            }
          } catch (e) { debug(`session.deleted handling failed: ${e.message}`); }
        }
        // 세션 생성/갱신 이벤트 — 최신 ID 덮어씀 (--clear·새 대화 시작 시 자동 갱신).
        if (event.type === "session.created" || event.type === "session.updated") {
          if (info && info.id) {
            // 같은 방에서 새 세션이 생성되면(/clear·/new) 이전 todos·lastmsg 를 초기화한다.
            if (event.type === "session.created") {
              cancelIdle();
              turnCompleted = true;
              rootIdle = true;
              lastStartedUserMessageId = null;
              liveChildren.clear();
              nestedSessionIds.clear();
              writeBusy("idle");
              writeTodos([]);
              clearLastmsg();
              clearWaiting();
            }
            writeId(info.id);
          }
        }
        // message.updated — 메시지 메타( role, id ) 캐시. 텍스트 본문은 여기에 없음.
        else if (event.type === "message.updated") {
          if (info && info.id) {
            if (info.sessionID) writeId(info.sessionID);
            if (info.role) messageRole[info.id] = info.role;
          }
        }
        // message.part.updated — 실제 텍스트 본문이 여기 도착.
        //   type="text" + 부모 메시지가 user 일 때만 lastmsg 로 저장.
        else if (event.type === "message.part.updated") {
          if (!part) return;
          // 답변 후 generation 재개(reasoning/text/step-start) = 선택지 대기 해제. (tool part 는 ask 자체의 잔여라 제외)
          if (awaitingAnswer && part.type !== "tool") clearWaiting();
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
            beginTurn(part.messageID);
          } else if (role === "assistant") {
            // assistant 텍스트 누적 → lastreply 에 합쳐 기록(스트리밍 중 계속 덮어써 최종본 보존).
            if (part.messageID !== asstMsgId) { asstMsgId = part.messageID; for (const k in asstParts) delete asstParts[k]; }
            asstParts[part.id || "_"] = part.text;
            const joined = Object.values(asstParts).join("\n").trim();
            writeLastreply(joined);
          }
        }
      } catch (e) { debug(`event handler error: ${e.message}`); }
    },
    "chat.message": async (input) => {
      try {
        const sid = input && input.sessionID;
        if (sid && (nestedSessionIds.has(String(sid)) || liveChildren.has(String(sid)))) {
          handleChildEvent("chat.message", sid, {});
          return;
        }
        if (!isRootSession(sid)) return;
        if (sid && await isChildSession(sid)) {
          handleChildEvent("chat.message", sid, {});
          return;
        }
        if (sid) writeId(sid);
        if (input && input.message && input.message.role === "user") {
          // chat.message 는 message.updated 와 동시 또는 직전에 옴 → role 캐시도 함께 갱신.
          if (input.message.id) messageRole[input.message.id] = "user";
          const text = extractTextFromParts(input.message.parts);
          if (text) writeLastmsg(text);
          beginTurn(input.message.id); // user 프롬프트 전송 → 처리 시작 → 스피너 켜기
        }
      } catch (e) { debug(`chat.message error: ${e.message}`); }
    },
  };
};
