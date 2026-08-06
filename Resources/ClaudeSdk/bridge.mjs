import readline from "node:readline";
import { query } from "@anthropic-ai/claude-agent-sdk";

const write = (value) => process.stdout.write(`${JSON.stringify(value)}\n`);

class PromptQueue {
  constructor() {
    this.values = [];
    this.waiters = [];
    this.closed = false;
  }

  push(value) {
    if (this.closed) return;
    const waiter = this.waiters.shift();
    if (waiter) waiter({ value, done: false });
    else this.values.push(value);
  }

  close() {
    this.closed = true;
    for (const waiter of this.waiters.splice(0)) waiter({ value: undefined, done: true });
  }

  [Symbol.asyncIterator]() { return this; }

  next() {
    if (this.values.length) return Promise.resolve({ value: this.values.shift(), done: false });
    if (this.closed) return Promise.resolve({ value: undefined, done: true });
    return new Promise((resolve) => this.waiters.push(resolve));
  }
}

const prompts = new PromptQueue();
const permissions = new Map();
let conversation = null;
let started = false;
let sessionLoop = null;
let shuttingDown = false;
let sessionId = "";
let currentModel = "";
let currentEffort = "high";
let currentPermissionMode = "acceptEdits";
let knownCommandNames = new Set();
let knownSkillNames = new Set();
let latestContextTokens = 0;
let currentContextWindow = 1_000_000;
let partialMessageId = "";
let lastRateLimitNotice = "";
let resultErrorAt = 0;
const partialBlocks = new Map();
const streamedMessageIds = new Set();
const SAFE_EFFORT_LEVELS = new Set(["low", "medium", "high", "xhigh", "max"]);
const SAFE_PERMISSION_MODES = new Set(["acceptEdits", "plan", "auto", "bypassPermissions"]);
const SAFE_IMAGE_TYPES = new Set(["image/jpeg", "image/png", "image/gif", "image/webp"]);
const MAX_IMAGES = 20;
const SHUTDOWN_GUARD_MS = 6000;
const MAX_IMAGE_BASE64_LENGTH = 10 * 1024 * 1024;
const MAX_TOTAL_IMAGE_BASE64_LENGTH = 28 * 1024 * 1024;
const DEVEZCODE_GUI_INSTRUCTIONS = [
  "DevezCode GUI 응답 규칙:",
  "- 서론이나 인사 없이 결론부터 답한다.",
  "- 기본 답변은 짧고 명확하게 작성하며, 사용자가 자세한 설명을 요청하거나 정확한 전달에 필요한 경우에만 늘린다.",
  "- 산문보다 짧은 불릿과 필요한 코드 블록을 우선한다.",
  "- 코드 변경 결과는 핵심 내용과 검증 결과만 알리고, 내부 도구 호출이나 불필요한 작업 중계를 장황하게 설명하지 않는다.",
  "- 작업을 완료하면 사용자의 요청을 기준으로 무엇을 완료했는지 분명하게 알린다.",
  "- 코드, 명령어, 경로, 파일명, 제품명 같은 기술 식별자는 번역하지 않는다.",
  "작업 단계 규칙:",
  "- 질문에만 답하는 경우를 제외하고 코드 확인, 원인 분석, 수정처럼 실행이 필요한 작업은 제공되는 TodoWrite 또는 Task 관리 도구로 간단한 계획을 먼저 만들고 계속 갱신한다.",
  "- 계획은 작업 규모에 맞는 최소 단계로 구성하고, 각 단계 제목은 `1. `, `2. `처럼 번호로 시작하는 자연스러운 한국어로 작성한다.",
  "- 동시에 진행 중인 단계는 하나만 두고, 착수할 때 in_progress로 바꾸며 끝나는 즉시 completed로 갱신한다.",
  "- 계획 도구가 제공되지 않은 환경에서는 존재하지 않는 도구를 호출하거나 계획용 표식을 일반 답변에 출력하지 않는다.",
].join("\n");

async function publishCapabilities() {
  if (!conversation) return;
  try {
    const modelsPromise = conversation.supportedModels();
    let skills = [];
    try {
      const response = await conversation.reloadSkills();
      skills = Array.isArray(response?.skills) ? response.skills : [];
      knownSkillNames = new Set(skills
        .map((skill) => String(skill?.name || "").trim().toLowerCase())
        .filter(Boolean));
    } catch { }
    const [models, supportedCommands] = await Promise.all([
      modelsPromise,
      conversation.supportedCommands(),
    ]);
    const commands = Array.isArray(supportedCommands) ? [...supportedCommands] : [];
    const commandNames = new Set(commands
      .map((command) => String(command?.name || "").trim().toLowerCase())
      .filter(Boolean));
    for (const skill of skills) {
      const name = String(skill?.name || "").trim().toLowerCase();
      if (name && !commandNames.has(name)) {
        commands.push(skill);
        commandNames.add(name);
      }
    }
    knownCommandNames = commandNames;
    write({
      type: "capabilities",
      input: {
        models: Array.isArray(models) ? models : [],
        commands,
        skillNames: [...knownSkillNames],
        permissionModes: [...SAFE_PERMISSION_MODES],
        currentModel,
        currentEffort,
        currentPermissionMode,
      },
    });
  } catch (error) {
    write({ type: "capabilities_error", text: error?.message ?? String(error) });
  }
}

function markMessageStreamed(messageId) {
  if (!messageId) return;
  streamedMessageIds.add(messageId);
  if (streamedMessageIds.size > 128) streamedMessageIds.delete(streamedMessageIds.values().next().value);
}

function normalizeImages(values) {
  const result = [];
  let total = 0;
  for (const value of Array.isArray(values) ? values.slice(0, MAX_IMAGES) : []) {
    const mediaType = typeof value?.mediaType === "string" ? value.mediaType.toLowerCase() : "";
    const data = typeof value?.data === "string" ? value.data : "";
    if (!SAFE_IMAGE_TYPES.has(mediaType) || !data || data.length % 4 !== 0 || !/^[A-Za-z0-9+/]+={0,2}$/.test(data)) continue;
    total += data.length;
    if (data.length > MAX_IMAGE_BASE64_LENGTH || total > MAX_TOTAL_IMAGE_BASE64_LENGTH) continue;
    result.push({ name: typeof value.name === "string" ? value.name.slice(0, 180) : "이미지", mediaType, data });
  }
  return result;
}

function normalizeFiles(values) {
  return (Array.isArray(values) ? values : [])
    .filter(value => typeof value?.path === "string" && value.path.trim())
    .slice(0, 20)
    .map(value => ({ name: typeof value.name === "string" ? value.name.slice(0, 180) : "파일", path: value.path.trim() }));
}

function userMessage(text, imageValues, fileValues) {
  const images = normalizeImages(imageValues);
  const files = normalizeFiles(fileValues);
  const content = [];
  for (const [index, image] of images.entries()) {
    if (images.length > 1) content.push({ type: "text", text: `Image ${index + 1}: ${image.name}` });
    content.push({ type: "image", source: { type: "base64", media_type: image.mediaType, data: image.data } });
  }
  if (files.length) {
    content.push({
      type: "text",
      text: [
        "The user attached the following local files. Use Claude Code tools to inspect them when relevant:",
        ...files.map(file => `- ${JSON.stringify(file.path)} (${file.name})`),
      ].join("\n"),
    });
  }
  const promptText = typeof text === "string" ? text.trim() : "";
  if (promptText) content.push({ type: "text", text: promptText });
  return {
    type: "user",
    message: { role: "user", content },
    parent_tool_use_id: null,
    session_id: sessionId,
    origin: { kind: "human" },
  };
}

function firstQuestion(input) {
  const questions = Array.isArray(input?.questions) ? input.questions : [];
  const item = questions[0];
  return item && typeof item.question === "string" ? item.question : "";
}

async function requestPermission(toolName, input, context) {
  const requestId = crypto.randomUUID();
  write({
    type: "permission",
    requestId,
    toolName,
    input,
    question: firstQuestion(input),
    suggestions: context?.suggestions ?? [],
  });

  return new Promise((resolve) => {
    permissions.set(requestId, { resolve, toolName, input });
    context?.signal?.addEventListener("abort", () => {
      if (!permissions.delete(requestId)) return;
      resolve({ behavior: "deny", message: "요청이 취소되었습니다." });
    }, { once: true });
  });
}

function emitAssistant(message) {
  const blocks = Array.isArray(message?.message?.content) ? message.message.content : [];
  const wasStreamed = streamedMessageIds.delete(message?.message?.id ?? "");
  for (const block of blocks) {
    if (block?.type === "text" && block.text && !wasStreamed)
      write({ type: "assistant", text: block.text });
    else if (block?.type === "thinking" && block.thinking && !wasStreamed)
      write({ type: "thinking", text: block.thinking });
    else if (block?.type === "tool_use")
      write({ type: "tool", toolName: block.name ?? "도구", toolUseId: block.id ?? "", input: block.input ?? {} });
  }
  if (!message?.parent_tool_use_id && message?.message?.usage) {
    const usage = message.message.usage;
    latestContextTokens = [
      usage.input_tokens,
      usage.cache_creation_input_tokens,
      usage.cache_read_input_tokens,
      usage.output_tokens,
    ].reduce((total, value) => total + (Number.isFinite(value) ? value : 0), 0);
    write({ type: "context_usage", input: { usedTokens: latestContextTokens, contextWindow: currentContextWindow } });
  }
}

function resolveContextWindow(modelUsage) {
  const entries = Object.entries(modelUsage && typeof modelUsage === "object" ? modelUsage : {});
  if (!entries.length) return currentContextWindow;
  const selected = String(currentModel || "").toLowerCase();
  const match = entries.find(([model, usage]) => {
    const identity = `${model} ${usage?.canonicalModel || ""}`.toLowerCase();
    return selected && (identity.includes(selected) || selected.includes(model.toLowerCase()));
  });
  const usage = match?.[1] || entries[0][1];
  return Number.isFinite(usage?.contextWindow) && usage.contextWindow > 0
    ? usage.contextWindow
    : currentContextWindow;
}

function endPartialStreams() {
  for (const block of partialBlocks.values())
    write({ type: `${block.type === "text" ? "assistant" : "thinking"}_stream_end`, streamId: block.streamId });
  partialBlocks.clear();
  partialMessageId = "";
}

function emitPartial(message) {
  const event = message?.event;
  if (!event || typeof event.type !== "string") return;

  if (event.type === "message_start") {
    endPartialStreams();
    partialMessageId = event.message?.id ?? message.uuid ?? crypto.randomUUID();
    return;
  }

  if (event.type === "content_block_start") {
    const blockType = event.content_block?.type;
    if (blockType !== "text" && blockType !== "thinking") return;
    const streamId = `${partialMessageId || message.uuid || "message"}:${event.index}`;
    markMessageStreamed(partialMessageId || message.uuid || "");
    partialBlocks.set(event.index, { type: blockType, streamId });
    write({ type: `${blockType === "text" ? "assistant" : "thinking"}_stream_start`, streamId });
    const initial = blockType === "text" ? event.content_block?.text : event.content_block?.thinking;
    if (initial) write({ type: `${blockType === "text" ? "assistant" : "thinking"}_delta`, streamId, text: initial });
    return;
  }

  if (event.type === "content_block_delta") {
    const deltaType = event.delta?.type;
    const blockType = deltaType === "text_delta" ? "text" : deltaType === "thinking_delta" ? "thinking" : "";
    if (!blockType) return;
    let block = partialBlocks.get(event.index);
    if (!block) {
      block = { type: blockType, streamId: `${partialMessageId || message.uuid || "message"}:${event.index}` };
      partialBlocks.set(event.index, block);
      markMessageStreamed(partialMessageId || message.uuid || "");
      write({ type: `${blockType === "text" ? "assistant" : "thinking"}_stream_start`, streamId: block.streamId });
    }
    const text = blockType === "text" ? event.delta?.text : event.delta?.thinking;
    if (text) write({ type: `${blockType === "text" ? "assistant" : "thinking"}_delta`, streamId: block.streamId, text });
    return;
  }

  if (event.type === "content_block_stop") {
    const block = partialBlocks.get(event.index);
    if (!block) return;
    write({ type: `${block.type === "text" ? "assistant" : "thinking"}_stream_end`, streamId: block.streamId });
    partialBlocks.delete(event.index);
    return;
  }

  if (event.type === "message_stop") {
    endPartialStreams();
  }
}

function emitToolResults(message) {
  const blocks = Array.isArray(message?.message?.content) ? message.message.content : [];
  for (const block of blocks) {
    if (block?.type !== "tool_result") continue;
    const text = typeof block.content === "string"
      ? block.content
      : JSON.stringify(block.content ?? "");
    write({ type: "tool_result", toolUseId: block.tool_use_id ?? "", text, isError: block.is_error === true });
  }
}

function emitSystemEvent(message) {
  const subtype = message?.subtype ?? "";
  if (subtype === "status") {
    if (message.permissionMode) {
      currentPermissionMode = message.permissionMode;
      write({ type: "config_changed", input: { model: currentModel, effort: currentEffort, permissionMode: currentPermissionMode, persist: false } });
    }
    if (message.status === "compacting")
      write({ type: "compact_status", input: { state: "running" } });
    if (message.compact_result)
      write({
        type: "compact_status",
        text: message.compact_error ?? "",
        isError: message.compact_result === "failed",
        input: { state: message.compact_result === "failed" ? "error" : "success" },
      });
    return;
  }
  if (subtype === "compact_boundary") {
    const metadata = message.compact_metadata ?? {};
    write({
      type: "compact_boundary",
      input: {
        trigger: metadata.trigger ?? "manual",
        preTokens: metadata.pre_tokens ?? 0,
        postTokens: metadata.post_tokens ?? 0,
        durationMs: metadata.duration_ms ?? 0,
      },
    });
    return;
  }
  if (subtype === "local_command_output") {
    write({ type: "system_output", text: message.content ?? "", input: { kind: "command" } });
    return;
  }
  if (subtype === "informational") {
    write({ type: "system_output", text: message.content ?? "", input: { kind: "information", level: message.level ?? "notice" } });
    return;
  }
  if (subtype === "notification") {
    write({ type: "system_output", text: message.text ?? "", input: { kind: "notification", level: message.priority ?? "low" } });
    return;
  }
  if (subtype === "api_retry") {
    const seconds = Math.max(1, Math.ceil((Number(message.retry_delay_ms) || 0) / 1000));
    write({
      type: "system_output",
      text: `Claude 연결을 다시 시도합니다 · ${message.attempt ?? 1}/${message.max_retries ?? 1} · ${seconds}s`,
      input: { kind: "notification", level: "warning" },
    });
    return;
  }
  if (subtype === "plugin_install" && (message.status === "failed" || message.status === "completed")) {
    write({
      type: "system_output",
      text: message.status === "failed"
        ? `플러그인 설치 실패${message.name ? ` · ${message.name}` : ""}${message.error ? `\n${message.error}` : ""}`
        : "플러그인 설치가 완료되었습니다.",
      input: { kind: "notification", level: message.status === "failed" ? "warning" : "notice" },
    });
    return;
  }
  if (subtype === "commands_changed") {
    const commands = Array.isArray(message.commands) ? message.commands : [];
    const changedNames = new Set(commands
      .map((command) => String(command?.name || "").trim().toLowerCase())
      .filter(Boolean));
    for (const name of changedNames) if (!knownCommandNames.has(name)) knownSkillNames.add(name);
    knownCommandNames = changedNames;
    write({
      type: "capabilities",
      input: {
        commands,
        skillNames: [...knownSkillNames],
        currentModel,
        currentEffort,
        currentPermissionMode,
      },
    });
    return;
  }
  if (subtype === "task_started" && message.skip_transcript !== true) {
    write({
      type: "task_started",
      text: message.description ?? "하위 작업",
      toolUseId: message.tool_use_id ?? "",
      input: { taskId: message.task_id ?? "", subagentType: message.subagent_type ?? "" },
    });
    return;
  }
  if (subtype === "task_progress") {
    write({
      type: "task_progress",
      text: message.summary ?? message.description ?? "",
      toolUseId: message.tool_use_id ?? "",
      input: {
        taskId: message.task_id ?? "",
        durationMs: message.usage?.duration_ms ?? 0,
        toolUses: message.usage?.tool_uses ?? 0,
        totalTokens: message.usage?.total_tokens ?? 0,
      },
    });
    return;
  }
  if (subtype === "task_notification" && message.skip_transcript !== true) {
    write({
      type: "task_finished",
      text: message.summary ?? "",
      isError: message.status === "failed",
      toolUseId: message.tool_use_id ?? "",
      input: {
        taskId: message.task_id ?? "",
        status: message.status ?? "completed",
        durationMs: message.usage?.duration_ms ?? 0,
        toolUses: message.usage?.tool_uses ?? 0,
        totalTokens: message.usage?.total_tokens ?? 0,
      },
    });
    return;
  }
  if (subtype === "permission_denied") {
    write({
      type: "permission_denied",
      text: message.reason ?? "권한이 허용되지 않았습니다.",
      toolName: message.tool_name ?? "도구",
      toolUseId: message.tool_use_id ?? "",
      isError: true,
    });
  }
}

async function start(command) {
  if (started) return;
  started = true;
  sessionId = command.sessionId ?? "";
  currentModel = command.model ?? "";
  currentEffort = SAFE_EFFORT_LEVELS.has(command.effort) ? command.effort : "high";
  currentPermissionMode = SAFE_PERMISSION_MODES.has(command.permissionMode)
    ? command.permissionMode
    : "acceptEdits";

  const options = {
    cwd: command.cwd,
    includePartialMessages: true,
    permissionMode: currentPermissionMode,
    allowDangerouslySkipPermissions: true,
    canUseTool: requestPermission,
    settingSources: ["user", "project", "local"],
    systemPrompt: {
      type: "preset",
      preset: "claude_code",
      append: DEVEZCODE_GUI_INSTRUCTIONS,
    },
  };
  if (sessionId) options.resume = sessionId;
  if (command.model) options.model = command.model;
  options.effort = currentEffort;
  if (command.claudePath) options.pathToClaudeCodeExecutable = command.claudePath;

  write({ type: "starting" });
  conversation = query({ prompt: prompts, options });
  void publishCapabilities();
  try {
    for await (const message of conversation) {
      if (message?.session_id && message.session_id !== sessionId) {
        sessionId = message.session_id;
        write({ type: "session", sessionId });
      }
      if (message?.type === "system" && message?.subtype === "init") {
        currentModel = message.model ?? currentModel;
        currentPermissionMode = message.permissionMode ?? currentPermissionMode;
        write({ type: "config_changed", input: { model: currentModel, effort: currentEffort, permissionMode: currentPermissionMode, persist: false } });
      } else if (message?.type === "system") emitSystemEvent(message);
      else if (message?.type === "conversation_reset") {
        sessionId = message.new_conversation_id ?? sessionId;
        latestContextTokens = 0;
        write({ type: "conversation_reset", sessionId });
        write({ type: "session", sessionId });
        write({ type: "context_usage", input: { usedTokens: 0, contextWindow: currentContextWindow } });
      } else if (message?.type === "tool_progress") {
        write({
          type: "tool_progress",
          toolName: message.tool_name ?? "도구",
          toolUseId: message.tool_use_id ?? "",
          input: { elapsedSeconds: message.elapsed_time_seconds ?? 0 },
        });
      } else if (message?.type === "tool_use_summary") {
        write({
          type: "tool_summary",
          text: message.summary ?? "",
          input: { toolUseIds: Array.isArray(message.preceding_tool_use_ids) ? message.preceding_tool_use_ids : [] },
        });
      } else if (message?.type === "auth_status" && message.error) {
        write({ type: "system_output", text: message.error, input: { kind: "notification", level: "warning" } });
      } else if (message?.type === "rate_limit_event") {
        const info = message.rate_limit_info ?? {};
        const key = `${info.status}:${info.rateLimitType || ""}:${info.resetsAt || ""}`;
        if (info.status !== "allowed" && key !== lastRateLimitNotice) {
          lastRateLimitNotice = key;
          const utilization = Number(info.utilization);
          const percent = Number.isFinite(utilization)
            ? Math.round((utilization <= 1 ? utilization * 100 : utilization))
            : 0;
          write({
            type: "system_output",
            text: info.status === "rejected"
              ? "Claude 사용 한도에 도달했습니다. 초기화 후 다시 시도하세요."
              : `Claude 사용 한도에 가까워졌습니다${percent ? ` · ${percent}%` : ""}.`,
            input: { kind: "notification", level: "warning" },
          });
        } else if (info.status === "allowed") lastRateLimitNotice = "";
      } else if (message?.type === "stream_event") emitPartial(message);
      else if (message?.type === "assistant") emitAssistant(message);
      else if (message?.type === "user") emitToolResults(message);
      else if (message?.type === "result") {
        currentContextWindow = resolveContextWindow(message.modelUsage);
        write({ type: "context_usage", input: { usedTokens: latestContextTokens, contextWindow: currentContextWindow } });
        resultErrorAt = message.is_error === true ? Date.now() : 0;
        write({
          type: "result",
          text: typeof message.result === "string" ? message.result : "",
          isError: message.is_error === true,
          subtype: message.subtype ?? "",
        });
      }
    }
    write({ type: "stopped" });
  } catch (error) {
    endPartialStreams();
    if (!resultErrorAt || Date.now() - resultErrorAt > 10_000)
      write({ type: "error", text: error?.message ?? String(error) });
  } finally {
    conversation = null;
  }
}

/// 종료: 즉시 process.exit 하지 않고 SDK 쿼리를 정상 배수한다. prompts 를 닫아야 claude CLI 자식이
/// stdin EOF 를 받고 transcript(.jsonl)를 flush 한 뒤 종료하는데, 예전엔 interrupt 후 50ms 만에
/// exit 해서 마지막 턴이 디스크에 안 남고 다음 resume 에서 통째로 유실됐다.
async function shutdown() {
  if (shuttingDown) return;
  shuttingDown = true;
  prompts.close();
  try { await conversation?.interrupt?.(); } catch { /* 진행 중 턴이 없으면 거부될 수 있다 */ }
  const guard = setTimeout(() => process.exit(0), SHUTDOWN_GUARD_MS); // CLI 무응답 대비 안전망
  try { await sessionLoop; } catch { /* 오류는 start() 가 이미 보고 */ }
  clearTimeout(guard);
  // stdout 이 파이프면 process.exit 가 남은 버퍼를 잘라먹는다 — 배수가 끝났으니 핸들이 정리되면
  // 자연 종료되고, 뭔가 붙잡고 있으면 unref 타이머가 마무리한다.
  process.exitCode = 0;
  input.close();
  setTimeout(() => process.exit(0), 1000).unref();
}

async function handle(command) {
  switch (command?.type) {
    case "start":
      sessionLoop = start(command).catch((error) => {
        write({ type: "error", text: error?.message ?? String(error) });
      });
      break;
    case "prompt":
      resultErrorAt = 0;
      prompts.push(userMessage(command.text ?? "", command.images, command.files));
      break;
    case "permission": {
      const pending = permissions.get(command.requestId);
      if (!pending) break;
      permissions.delete(command.requestId);
      if (!command.allow) {
        pending.resolve({ behavior: "deny", message: command.message || "사용자가 거부했습니다." });
        break;
      }
      let updatedInput = pending.input;
      if (pending.toolName === "AskUserQuestion") {
        const questions = Array.isArray(pending.input?.questions) ? pending.input.questions : [];
        const answers = command.answers && typeof command.answers === "object" && !Array.isArray(command.answers)
          ? Object.fromEntries(Object.entries(command.answers).filter(([key, value]) => key && typeof value === "string"))
          : {};
        if (!Object.keys(answers).length && command.answer) {
          for (const question of questions) {
            if (question?.question) answers[question.question] = command.answer;
          }
        }
        updatedInput = { ...pending.input, answers };
      }
      pending.resolve({ behavior: "allow", updatedInput });
      break;
    }
    case "set_model": {
      if (!conversation) break;
      const model = typeof command.model === "string" ? command.model.trim() : "";
      try {
        await conversation.setModel(model || undefined);
        currentModel = model;
        write({ type: "config_changed", input: { model: currentModel, effort: currentEffort, permissionMode: currentPermissionMode, persist: true } });
      } catch (error) {
        write({ type: "config_error", text: error?.message ?? String(error), input: { control: "model", model: currentModel, effort: currentEffort, permissionMode: currentPermissionMode } });
      }
      break;
    }
    case "set_effort": {
      if (!conversation) break;
      const effort = typeof command.effort === "string" ? command.effort : "";
      if (!SAFE_EFFORT_LEVELS.has(effort)) {
        write({ type: "config_error", text: "지원하지 않는 Effort 수준입니다.", input: { control: "effort", model: currentModel, effort: currentEffort, permissionMode: currentPermissionMode } });
        break;
      }
      try {
        await conversation.applyFlagSettings({ effortLevel: effort });
        currentEffort = effort;
        write({ type: "config_changed", input: { model: currentModel, effort: currentEffort, permissionMode: currentPermissionMode, persist: true } });
      } catch (error) {
        write({ type: "config_error", text: error?.message ?? String(error), input: { control: "effort", model: currentModel, effort: currentEffort, permissionMode: currentPermissionMode } });
      }
      break;
    }
    case "set_permission_mode": {
      if (!conversation) break;
      const mode = command.permissionMode;
      if (!SAFE_PERMISSION_MODES.has(mode)) {
        write({ type: "config_error", text: "지원하지 않는 권한 모드입니다.", input: { control: "permission", model: currentModel, effort: currentEffort, permissionMode: currentPermissionMode } });
        break;
      }
      try {
        await conversation.setPermissionMode(mode);
        currentPermissionMode = mode;
        write({ type: "config_changed", input: { model: currentModel, effort: currentEffort, permissionMode: currentPermissionMode, persist: true } });
      } catch (error) {
        write({ type: "config_error", text: error?.message ?? String(error), input: { control: "permission", model: currentModel, effort: currentEffort, permissionMode: currentPermissionMode } });
      }
      break;
    }
    case "refresh_capabilities":
      await publishCapabilities();
      break;
    case "interrupt":
      await conversation?.interrupt?.();
      break;
    case "shutdown":
      await shutdown();
      break;
  }
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", (line) => {
  let command;
  try { command = JSON.parse(line); }
  catch (error) { write({ type: "error", text: error?.message ?? String(error) }); return; }
  // handle 은 async — void 로 던지면 rejection 이 위 catch 를 지나쳐 프로세스를 즉사시킨다
  // (종료 중이면 transcript flush 전에 죽는다). 반드시 promise 로 받아 잡는다.
  handle(command).catch((error) => write({ type: "error", text: error?.message ?? String(error) }));
});
input.on("close", () => { shutdown().catch(() => process.exit(0)); });
write({ type: "ready" });
