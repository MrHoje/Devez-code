// DevezCode GJC room state hook. Explicitly loaded with: gjc --hook <this file>
// JSONL polling remains the fallback/reconciliation source in the app.
import fs from "node:fs";
import path from "node:path";

export default function (pi) {
	const roomId = (process.env.DEVEZCODE_ROOM_ID || "").replace(/[^A-Za-z0-9_-]/g, "");
	const appData = process.env.APPDATA;
	if (!roomId || !appData) return;

	const stateDir = path.join(appData, "DevezCode", "gajae", "hook-state");
	const statePath = path.join(stateDir, `${roomId}.json`);
	const askCalls = new Set();
	let sequence = 0;

	function sessionFile(ctx) {
		try {
			return ctx?.sessionManager?.getSessionFile?.() || null;
		} catch {
			return null;
		}
	}

	function writeState(busy, waiting, event, ctx, resetMessage = false) {
		let tempPath;
		try {
			fs.mkdirSync(stateDir, { recursive: true });
			const payload = JSON.stringify({
				version: 1,
				roomId,
				busy,
				waiting,
				resetMessage,
				event,
				sessionFile: sessionFile(ctx),
				updatedAt: Date.now(),
				pid: process.pid,
				sequence: ++sequence,
			});
			tempPath = `${statePath}.${process.pid}.${sequence}.tmp`;
			fs.writeFileSync(tempPath, payload, { encoding: "utf8", flag: "wx" });
			try {
				fs.renameSync(tempPath, statePath);
			} catch {
				// libuv rename normally replaces atomically on Windows. Filesystems that reject
				// replacement get a best-effort fallback; JSONL polling still guarantees recovery.
				fs.rmSync(statePath, { force: true });
				fs.renameSync(tempPath, statePath);
			}
		} catch {
			try { if (tempPath) fs.rmSync(tempPath, { force: true }); } catch {}
		}
	}

	// Factory execution itself is a health marker. session_start follows with the real session path.
	writeState(false, false, "hook_loaded", null);

	pi.on("session_start", async (_event, ctx) => {
		askCalls.clear();
		writeState(false, false, "session_start", ctx);
	});

	pi.on("before_agent_start", async (_event, ctx) => {
		askCalls.clear();
		writeState(true, false, "before_agent_start", ctx);
	});

	pi.on("agent_start", async (_event, ctx) => {
		writeState(true, askCalls.size > 0, "agent_start", ctx);
	});

	pi.on("tool_call", async (event, ctx) => {
		if (String(event.toolName || "").toLowerCase() === "ask") askCalls.add(event.toolCallId);
		writeState(true, askCalls.size > 0, "tool_call", ctx);
	});

	pi.on("tool_result", async (event, ctx) => {
		askCalls.delete(event.toolCallId);
		writeState(true, askCalls.size > 0, "tool_result", ctx);
	});

	pi.on("agent_end", async (_event, ctx) => {
		askCalls.clear();
		writeState(false, false, "agent_end", ctx);
	});

	pi.on("session_switch", async (event, ctx) => {
		askCalls.clear();
		writeState(false, false, `session_switch:${event.reason}`, ctx, event.reason === "new");
	});

	pi.on("session_shutdown", async (_event, ctx) => {
		askCalls.clear();
		writeState(false, false, "session_shutdown", ctx);
	});
}
