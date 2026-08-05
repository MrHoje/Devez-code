(() => {
  "use strict";

  const bridge = window.chrome?.webview;
  const conversation = document.getElementById("conversation");
  const messages = document.getElementById("messages");
  const emptyState = document.getElementById("empty-state");
  const typing = document.getElementById("typing");
  const workingTime = document.getElementById("working-time");
  const prompt = document.getElementById("prompt");
  const action = document.getElementById("action");
  const composer = document.querySelector(".composer");
  const attachControl = document.getElementById("attach-control");
  const attachmentStrip = document.getElementById("attachment-strip");
  const attachmentError = document.getElementById("attachment-error");
  const dropOverlay = document.getElementById("drop-overlay");
  const commandMenu = document.getElementById("command-menu");
  const controlMenu = document.getElementById("control-menu");
  const composerShell = document.querySelector(".composer-shell");
  const modelControl = document.getElementById("model-control");
  const effortControl = document.getElementById("effort-control");
  const permissionControl = document.getElementById("permission-control");
  const vibeControl = document.getElementById("vibe-control");
  const contextControl = document.getElementById("context-control");
  const modelLabel = document.getElementById("model-label");
  const effortLabel = document.getElementById("effort-label");
  const permissionLabel = document.getElementById("permission-label");
  const vibeLabel = document.getElementById("vibe-label");
  const contextLabel = document.getElementById("context-label");

  let roomId = "";
  let busy = false;
  let nearBottom = true;
  let commands = [];
  let models = [];
  let currentModel = "";
  let defaultResolvedModel = "";
  let currentEffort = "high";
  let currentPermissionMode = "acceptEdits";
  let vibeMode = true;
  let currentContextTokens = 0;
  let currentContextWindow = 1_000_000;
  let commandSelection = 0;
  let visibleCommands = [];
  let openControl = "";
  let attachments = [];
  let attachmentErrorTimer = 0;
  let dragDepth = 0;
  let workingStartedAt = 0;
  let workingTimer = 0;
  let scrollFrame = 0;
  let currentToolGroup = null;
  let compactMarker = null;
  let pendingLocalCommand = "";
  let quietCommandInFlight = false;
  const markdownTemplate = document.createElement("template");
  const markdownBlockCache = new Map();
  const tools = new Map();
  const tasks = new Map();
  const toolGroups = new Set();
  const assistantStreams = new Map();
  const thinkingStreams = new Map();
  const imageTypes = new Set(["image/jpeg", "image/png", "image/gif", "image/webp"]);
  const maxAttachments = 20;
  const maxImageEncodedBytes = 10 * 1024 * 1024;
  const maxTotalEncodedBytes = 28 * 1024 * 1024;
  const maxImageDimension = 8000;
  const hiddenSlashCommands = new Set([
    "__remote-workflow", "agents", "color", "effort", "extra-usage", "heapdump",
    "model", "rename", "workflow-launch-exec",
  ]);
  const quietSlashCommands = new Set([
    "clear", "color", "compact", "config", "context", "effort", "fast", "mcp",
    "model", "reload-skills", "rename", "usage", "usage-credits",
  ]);

  const permissionModes = [
    { id: "acceptEdits", label: "Accept edits", description: "파일 수정을 자동으로 허용합니다" },
    { id: "plan", label: "Plan mode", description: "변경 없이 계획만 작성합니다" },
    { id: "auto", label: "Auto mode", description: "Claude가 필요한 권한을 판단합니다" },
    { id: "bypassPermissions", label: "Bypass permissions", description: "권한 확인 없이 작업을 실행합니다" },
  ];
  const effortLevels = [
    { value: "low", displayName: "Low", description: "빠른 응답이 필요한 가벼운 작업" },
    { value: "medium", displayName: "Medium", description: "속도와 추론의 균형" },
    { value: "high", displayName: "High", description: "복잡한 작업을 위한 깊은 추론" },
    { value: "xhigh", displayName: "XHigh", description: "더 오래 검토하는 확장 추론" },
    { value: "max", displayName: "Max", description: "가장 깊고 긴 추론" },
  ];
  const statusPalettes = {
    dark: { text: "#C7C8CB", haiku: "#07DCDB", sonnet: "#D9B117", opus: "#D76564", fable: "#CA6CD6", low: "#C09714", medium: "#3C8A58", high: "#9BA1D6", xhigh: "#9A77DB", max: "#D76564", acceptEdits: "#32B786", plan: "#5791D7", auto: "#D9B117", bypassPermissions: "#D76564" },
    soft: { text: "#16120C", haiku: "#00CCCC", sonnet: "#C97C1A", opus: "#CC0000", fable: "#B100CC", low: "#CC9C00", medium: "#00A33F", high: "#4338CA", xhigh: "#7900CC", max: "#CC0000", acceptEdits: "#008B58", plan: "#005BCC", auto: "#C97C1A", bypassPermissions: "#CC0000" },
    minimal: { text: "#0F1422", haiku: "#00D1D1", sonnet: "#CA8A04", opus: "#D10000", fable: "#B500D1", low: "#D19F00", medium: "#00A740", high: "#4338CA", xhigh: "#7C00D1", max: "#D10000", acceptEdits: "#008E5A", plan: "#005ED1", auto: "#CA8A04", bypassPermissions: "#D10000" },
    gray: { text: "#1F2937", haiku: "#326AA5", sonnet: "#A16207", opus: "#C2413E", fable: "#76558F", low: "#A16207", medium: "#15803D", high: "#76558F", xhigh: "#65497B", max: "#C2413E", acceptEdits: "#15803D", plan: "#326AA5", auto: "#A16207", bypassPermissions: "#C2413E" },
    softpink: { text: "#3B2931", haiku: "#326A9F", sonnet: "#9A650B", opus: "#C2413E", fable: "#84588F", low: "#9A650B", medium: "#25723C", high: "#84588F", xhigh: "#70467E", max: "#C2413E", acceptEdits: "#25723C", plan: "#326A9F", auto: "#9A650B", bypassPermissions: "#C2413E" },
  };
  statusPalettes.midnight = statusPalettes.dark;
  let statusPalette = statusPalettes.dark;

  function post(value) { bridge?.postMessage(value); }

  function formatElapsed(milliseconds) {
    const seconds = Math.max(0, Math.floor(milliseconds / 1000));
    const minutes = Math.floor(seconds / 60);
    const remainder = seconds % 60;
    return minutes ? `${minutes}m ${remainder}s` : `${remainder}s`;
  }

  function updateWorkingTime() {
    workingTime.textContent = `(${formatElapsed(Date.now() - workingStartedAt)})`;
  }

  function startWorkingTimer() {
    if (workingStartedAt) return;
    workingStartedAt = Date.now();
    updateWorkingTime();
    clearInterval(workingTimer);
    workingTimer = setInterval(updateWorkingTime, 1000);
  }

  function finishWorkingTimer(showComplete) {
    if (!workingStartedAt) return;
    const elapsed = formatElapsed(Date.now() - workingStartedAt);
    workingStartedAt = 0;
    clearInterval(workingTimer);
    workingTimer = 0;
    workingTime.textContent = "(0s)";
    if (!showComplete) return;
    const line = document.createElement("div");
    line.className = "completion-line";
    line.textContent = `Complete (${elapsed})`;
    append(line, true);
    requestAnimationFrame(() => line.classList.add("is-visible"));
  }

  function resetWorkingTimer() {
    workingStartedAt = 0;
    clearInterval(workingTimer);
    workingTimer = 0;
    workingTime.textContent = "(0s)";
  }

  function showAttachmentError(message) {
    clearTimeout(attachmentErrorTimer);
    attachmentError.textContent = message || "파일을 첨부하지 못했습니다.";
    attachmentError.hidden = false;
    attachmentErrorTimer = setTimeout(() => { attachmentError.hidden = true; }, 4200);
  }

  function fileIcon() {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("aria-hidden", "true");
    svg.innerHTML = '<path d="M14.5 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7.5L14.5 2Z"/><path d="M14 2v6h6"/>';
    return svg;
  }

  function renderAttachments() {
    attachmentStrip.replaceChildren();
    for (const item of attachments) {
      const card = document.createElement("div");
      card.className = `attachment-item ${item.kind}`;
      card.title = item.name;
      if (item.kind === "image") {
        const image = document.createElement("img");
        image.src = item.preview || `data:${item.mediaType};base64,${item.data}`;
        image.alt = item.name;
        card.appendChild(image);
      } else card.appendChild(fileIcon());
      const name = document.createElement("span");
      name.className = "attachment-name";
      name.textContent = item.name;
      const remove = document.createElement("button");
      remove.className = "attachment-remove";
      remove.type = "button";
      remove.textContent = "×";
      remove.title = `${item.name} 제거`;
      remove.setAttribute("aria-label", `${item.name} 제거`);
      remove.addEventListener("click", () => {
        attachments = attachments.filter(value => value.id !== item.id);
        renderAttachments();
        prompt.focus();
      });
      card.append(name, remove);
      attachmentStrip.appendChild(card);
    }
    attachmentStrip.hidden = attachments.length === 0;
    composer.classList.toggle("has-attachments", attachments.length > 0);
    updateAction();
  }

  function decodedSize(data) {
    if (!data) return 0;
    return Math.max(0, Math.floor(data.length * 3 / 4) - (data.endsWith("==") ? 2 : data.endsWith("=") ? 1 : 0));
  }

  function currentEncodedSize() {
    return attachments.reduce((total, item) => total + (item.kind === "image" ? item.data.length : 0), 0);
  }

  function readDataUrl(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result || ""));
      reader.onerror = () => reject(reader.error || new Error("파일을 읽지 못했습니다."));
      reader.readAsDataURL(file);
    });
  }

  async function imageInfo(blob, dataUrl) {
    let bitmap;
    try { bitmap = await createImageBitmap(blob); }
    catch { throw new Error("이미지 내용을 읽을 수 없습니다."); }
    try {
      const width = bitmap.width;
      const height = bitmap.height;
      if (!width || !height || width > maxImageDimension || height > maxImageDimension)
        throw new Error(`이미지는 ${maxImageDimension}×${maxImageDimension}px 이하여야 합니다.`);
      const scale = Math.min(1, 180 / Math.max(width, height));
      const canvas = document.createElement("canvas");
      canvas.width = Math.max(1, Math.round(width * scale));
      canvas.height = Math.max(1, Math.round(height * scale));
      canvas.getContext("2d", { alpha: true })?.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
      return { width, height, preview: canvas.toDataURL("image/webp", .82) || dataUrl };
    } finally { bitmap.close?.(); }
  }

  async function addImageFile(file) {
    const mediaType = String(file.type || "").toLowerCase();
    if (!imageTypes.has(mediaType)) throw new Error("PNG, JPEG, GIF, WebP 이미지만 첨부할 수 있습니다.");
    if (attachments.length >= maxAttachments) throw new Error(`첨부 파일은 최대 ${maxAttachments}개까지 추가할 수 있습니다.`);
    const dataUrl = await readDataUrl(file);
    const marker = dataUrl.indexOf(",");
    const data = marker >= 0 ? dataUrl.slice(marker + 1) : "";
    if (!data || data.length > maxImageEncodedBytes) throw new Error("이미지 한 장은 Base64 기준 10MB 이하여야 합니다.");
    if (currentEncodedSize() + data.length > maxTotalEncodedBytes) throw new Error("첨부 이미지의 전체 크기가 너무 큽니다.");
    const info = await imageInfo(file, dataUrl);
    attachments.push({ id: crypto.randomUUID(), kind: "image", name: file.name || "붙여넣은 이미지", mediaType, data, size: file.size || decodedSize(data), ...info });
    renderAttachments();
  }

  async function addImageFiles(files) {
    let added = 0;
    for (const file of files) {
      try { await addImageFile(file); added += 1; }
      catch (error) { showAttachmentError(error?.message || String(error)); }
    }
    if (added) prompt.focus();
  }

  async function addHostAttachments(items) {
    for (const source of Array.isArray(items) ? items : []) {
      try {
        if (attachments.length >= maxAttachments) throw new Error(`첨부 파일은 최대 ${maxAttachments}개까지 추가할 수 있습니다.`);
        if (source.kind === "file") {
          attachments.push({ id: crypto.randomUUID(), kind: "file", name: source.name || "파일", path: source.path || "", size: Number(source.size) || 0 });
          renderAttachments();
          continue;
        }
        const mediaType = String(source.mediaType || "").toLowerCase();
        const data = String(source.data || "");
        if (!imageTypes.has(mediaType) || !data) throw new Error("지원하지 않는 이미지입니다.");
        if (data.length > maxImageEncodedBytes || currentEncodedSize() + data.length > maxTotalEncodedBytes) throw new Error("첨부 이미지의 크기가 너무 큽니다.");
        const dataUrl = `data:${mediaType};base64,${data}`;
        const blob = await (await fetch(dataUrl)).blob();
        const info = await imageInfo(blob, dataUrl);
        attachments.push({ id: crypto.randomUUID(), kind: "image", name: source.name || "이미지", mediaType, data, size: Number(source.size) || decodedSize(data), ...info });
        renderAttachments();
      } catch (error) { showAttachmentError(error?.message || String(error)); }
    }
    prompt.focus();
  }

  function clearAttachments() {
    attachments = [];
    renderAttachments();
  }

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>"']/g, char => ({
      "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
    })[char]);
  }

  function inlineMarkdown(value) {
    const code = [];
    let text = String(value ?? "").replace(/`([^`\n]+)`/g, (_, body) => `\u0000C${code.push(escapeHtml(body)) - 1}\u0000`);
    text = escapeHtml(text);
    text = text.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    text = text.replace(/__([^_]+)__/g, "<strong>$1</strong>");
    text = text.replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<em>$2</em>");
    text = text.replace(/(^|[^_])_([^_\n]+)_/g, "$1<em>$2</em>");
    text = text.replace(/\u0000C(\d+)\u0000/g, (_, index) => `<code>${code[Number(index)]}</code>`);
    return text;
  }

  function splitTableRow(line) {
    let value = String(line ?? "").trim();
    if (value.startsWith("|")) value = value.slice(1);
    if (value.endsWith("|")) value = value.slice(0, -1);
    const cells = [];
    let cell = "";
    let inCode = false;
    for (let index = 0; index < value.length; index++) {
      const char = value[index];
      if (char === "\\") {
        const next = value[index + 1];
        if (next === "|" || next === "\\" || next === "`") {
          cell += next;
          index++;
        } else cell += char;
        continue;
      }
      if (char === "`") { inCode = !inCode; cell += char; continue; }
      if (char === "|" && !inCode) { cells.push(cell.trim()); cell = ""; continue; }
      cell += char;
    }
    cells.push(cell.trim());
    return cells;
  }

  function tableAlignments(line) {
    const cells = splitTableRow(line);
    if (!cells.length || !cells.every(cell => /^:?-{3,}:?$/.test(cell))) return null;
    return cells.map(cell => cell.startsWith(":") && cell.endsWith(":") ? "center" : cell.endsWith(":") ? "right" : "left");
  }

  const codeKeywords = new Set([
    "as", "async", "await", "bool", "break", "byte", "case", "catch", "char", "class", "const", "continue",
    "decimal", "double",
    "def", "default", "do", "else", "enum", "export", "extends", "false", "finally",
    "float", "fn", "for", "from", "function", "if", "implements", "import", "in", "int", "interface",
    "let", "long", "match", "namespace", "new", "null", "object", "of", "override", "package", "private",
    "protected", "public", "return", "static", "struct", "super", "switch", "this", "throw",
    "trait", "true", "try", "type", "typeof", "uint", "ulong", "undefined", "use", "using", "ushort", "var", "void",
    "while", "with", "yield", "short", "string",
  ]);

  function highlightCode(source, language) {
    const text = String(source ?? "");
    const lang = String(language ?? "").toLowerCase();
    const hashComments = /^(py|python|rb|ruby|sh|bash|shell|zsh|fish|ps1|powershell|ya?ml|toml|r)$/.test(lang);
    const dashComments = /^(sql|lua|hs|haskell)$/.test(lang);
    let html = "";
    let index = 0;
    const token = (kind, value) => `<span class="tok-${kind}">${escapeHtml(value)}</span>`;
    while (index < text.length) {
      if (text.startsWith("<!--", index)) {
        const end = text.indexOf("-->", index + 4);
        const next = end < 0 ? text.length : end + 3;
        html += token("comment", text.slice(index, next)); index = next; continue;
      }
      if (text.startsWith("/*", index)) {
        const end = text.indexOf("*/", index + 2);
        const next = end < 0 ? text.length : end + 2;
        html += token("comment", text.slice(index, next)); index = next; continue;
      }
      if (text.startsWith("//", index) || (dashComments && text.startsWith("--", index))
          || (hashComments && text[index] === "#")) {
        const end = text.indexOf("\n", index);
        const next = end < 0 ? text.length : end;
        html += token("comment", text.slice(index, next)); index = next; continue;
      }
      const char = text[index];
      if (char === "\"" || char === "'" || char === "`") {
        let next = index + 1;
        while (next < text.length) {
          if (text[next] === "\\") { next += 2; continue; }
          if (text[next++] === char) break;
        }
        html += token("string", text.slice(index, next)); index = next; continue;
      }
      const number = /^(?:0x[\da-f]+|\d+(?:\.\d+)?(?:e[+-]?\d+)?)/i.exec(text.slice(index));
      if (number) { html += token("number", number[0]); index += number[0].length; continue; }
      const identifier = /^[A-Za-z_$][\w$-]*/.exec(text.slice(index));
      if (identifier) {
        const value = identifier[0];
        const rest = text.slice(index + value.length);
        const kind = codeKeywords.has(value)
          ? "keyword"
          : /^\s*\(/.test(rest)
            ? "function"
            : /^[A-Z]/.test(value)
              ? "type"
              : "";
        html += kind ? token(kind, value) : escapeHtml(value);
        index += value.length;
        continue;
      }
      html += escapeHtml(char);
      index++;
    }
    return html;
  }

  function splitMarkdownBlocks(source) {
    const text = String(source ?? "").replace(/\r\n?/g, "\n");
    if (!text) return [];
    const blocks = [];
    let lines = [];
    let fence = "";
    let separated = false;

    for (const line of text.split("\n")) {
      if (!fence && !line.trim()) {
        if (lines.length) separated = true;
        continue;
      }
      if (!fence && separated) {
        blocks.push(lines.join("\n"));
        lines = [];
        separated = false;
      }
      lines.push(line);

      const match = /^(?: {0,3})(`{3,}|~{3,})/.exec(line);
      if (!match) continue;
      if (!fence) fence = match[1];
      else if (match[1][0] === fence[0] && match[1].length >= fence.length) fence = "";
    }
    if (lines.length) blocks.push(lines.join("\n"));
    return blocks;
  }

  function renderMarkdownBlock(source) {
    const lines = source.split("\n");
    const html = [];
    for (let index = 0; index < lines.length;) {
      const line = lines[index];
      if (/^```/.test(line)) {
        const language = line.slice(3).trim() || "code";
        const body = [];
        index++;
        while (index < lines.length && !/^```/.test(lines[index])) body.push(lines[index++]);
        if (index < lines.length) index++;
        html.push(`<section class="code-block"><div class="code-head"><span>${escapeHtml(language)}</span><button class="copy-code" type="button">복사</button></div><pre><code>${highlightCode(body.join("\n"), language)}</code></pre></section>`);
        continue;
      }
      if (!line.trim()) { index++; continue; }
      const alignments = index + 1 < lines.length ? tableAlignments(lines[index + 1]) : null;
      if (alignments && line.includes("|")) {
        const headers = splitTableRow(line);
        if (headers.length !== alignments.length) {
          const paragraph = [line];
          index++;
          while (index < lines.length && lines[index].trim()) paragraph.push(lines[index++]);
          html.push(`<p>${paragraph.map(inlineMarkdown).join("<br>")}</p>`);
          continue;
        }
        const rows = [];
        index += 2;
        while (index < lines.length && lines[index].trim() && lines[index].includes("|"))
          rows.push(splitTableRow(lines[index++]));
        const head = headers.map((cell, cellIndex) => `<th data-align="${alignments[cellIndex] || "left"}">${inlineMarkdown(cell)}</th>`).join("");
        const body = rows.map(row => `<tr>${headers.map((_, cellIndex) => `<td data-align="${alignments[cellIndex] || "left"}">${inlineMarkdown(row[cellIndex] || "")}</td>`).join("")}</tr>`).join("");
        html.push(`<div class="table-wrap"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`);
        continue;
      }
      const heading = /^(#{1,4})\s+(.+)$/.exec(line);
      if (heading) { const level = heading[1].length; html.push(`<h${level}>${inlineMarkdown(heading[2])}</h${level}>`); index++; continue; }
      if (/^\s*([-*_])(?:\s*\1){2,}\s*$/.test(line)) { html.push("<hr>"); index++; continue; }
      if (/^>\s?/.test(line)) {
        const body = [];
        while (index < lines.length && /^>\s?/.test(lines[index])) body.push(lines[index++].replace(/^>\s?/, ""));
        html.push(`<blockquote>${body.map(inlineMarkdown).join("<br>")}</blockquote>`);
        continue;
      }
      const unordered = /^\s*[-+*]\s+/.test(line);
      const ordered = /^\s*\d+[.)]\s+/.test(line);
      if (unordered || ordered) {
        const tag = ordered ? "ol" : "ul";
        const pattern = ordered ? /^\s*\d+[.)]\s+/ : /^\s*[-+*]\s+/;
        const items = [];
        while (index < lines.length && pattern.test(lines[index])) items.push(`<li>${inlineMarkdown(lines[index++].replace(pattern, ""))}</li>`);
        html.push(`<${tag}>${items.join("")}</${tag}>`);
        continue;
      }
      const paragraph = [line];
      index++;
      while (index < lines.length && lines[index].trim() && !/^(#{1,4})\s+|^```|^>\s?|^\s*[-+*]\s+|^\s*\d+[.)]\s+/.test(lines[index])) paragraph.push(lines[index++]);
      html.push(`<p>${paragraph.map(inlineMarkdown).join("<br>")}</p>`);
    }
    return html.join("");
  }

  function renderMarkdown(source) {
    return splitMarkdownBlocks(source).map(block => {
      const cached = markdownBlockCache.get(block);
      if (cached !== undefined) return cached;
      const html = renderMarkdownBlock(block);
      if (markdownBlockCache.size >= 256)
        markdownBlockCache.delete(markdownBlockCache.keys().next().value);
      markdownBlockCache.set(block, html);
      return html;
    }).join("");
  }

  function patchMarkdownNode(current, next) {
    if (current.nodeType === Node.TEXT_NODE && next.nodeType === Node.TEXT_NODE) {
      current.nodeValue = next.nodeValue;
      return true;
    }
    if (!(current instanceof HTMLElement) || !(next instanceof HTMLElement)) return false;
    if (current.tagName !== next.tagName || current.className !== next.className) return false;

    for (const attribute of Array.from(current.attributes))
      if (!next.hasAttribute(attribute.name)) current.removeAttribute(attribute.name);
    for (const attribute of Array.from(next.attributes))
      if (current.getAttribute(attribute.name) !== attribute.value)
        current.setAttribute(attribute.name, attribute.value);
    current.replaceChildren(...Array.from(next.childNodes, node => node.cloneNode(true)));
    return true;
  }

  function reconcileMarkdown(container, source) {
    markdownTemplate.innerHTML = renderMarkdown(source);
    const currentNodes = Array.from(container.childNodes);
    const nextNodes = Array.from(markdownTemplate.content.childNodes);
    let stableCount = 0;
    while (
      stableCount < currentNodes.length &&
      stableCount < nextNodes.length &&
      currentNodes[stableCount].isEqualNode(nextNodes[stableCount])
    ) stableCount++;

    while (
      stableCount < currentNodes.length &&
      stableCount < nextNodes.length &&
      patchMarkdownNode(currentNodes[stableCount], nextNodes[stableCount])
    ) stableCount++;

    for (let index = currentNodes.length - 1; index >= stableCount; index--)
      currentNodes[index].remove();
    if (stableCount < nextNodes.length)
      container.append(...nextNodes.slice(stableCount));
  }

  function prettyInput(input) {
    if (input == null) return "";
    try { return JSON.stringify(input, null, 2); } catch { return String(input); }
  }

  function firstString(input, ...keys) {
    for (const key of keys) {
      const value = input && typeof input[key] === "string" ? input[key] : "";
      if (value) return value;
    }
    return "";
  }

  function shortPath(path) {
    const value = String(path || "").replaceAll("\\", "/");
    const parts = value.split("/").filter(Boolean);
    return parts.slice(-2).join("/") || value;
  }

  function buildDiff(input) {
    const supplied = firstString(input, "diff", "patch", "unifiedDiff");
    if (supplied) return supplied;
    const oldText = firstString(input, "old_string", "oldString");
    const newText = firstString(input, "new_string", "newString", "content");
    if (!oldText && !newText) return prettyInput(input);
    const path = firstString(input, "file_path", "filePath", "path") || "file";
    const removed = oldText.split("\n").map(line => `-${line}`);
    const added = newText.split("\n").map(line => `+${line}`);
    return [`--- a/${path}`, `+++ b/${path}`, ...removed, ...added].join("\n");
  }

  function toolPresentation(event) {
    const name = String(event.toolName || "도구");
    const normalized = name.toLowerCase();
    const input = event.input || {};
    if (normalized === "bash" || normalized === "shell" || normalized.includes("terminal")) {
      return { title: "Shell", body: firstString(input, "command", "cmd") || prettyInput(input), kind: "shell", resourceKey: "" };
    }
    if (["edit", "write", "multiedit", "apply_patch", "notebookedit"].some(value => normalized.includes(value))) {
      const path = firstString(input, "file_path", "filePath", "path", "notebook_path");
      return { title: path ? `Diff · ${shortPath(path)}` : "Diff", body: buildDiff(input), kind: "diff", resourceKey: path };
    }
    if (normalized === "read") {
      const path = firstString(input, "file_path", "filePath", "path");
      return { title: path ? `Read · ${shortPath(path)}` : "Read", body: path || prettyInput(input), kind: "read", resourceKey: path };
    }
    if (normalized === "glob" || normalized === "grep" || normalized.includes("search")) {
      return { title: "Search", body: firstString(input, "pattern", "query") || prettyInput(input), kind: "search", resourceKey: "" };
    }
    if (normalized === "todowrite" || normalized.startsWith("taskcreate") || normalized.startsWith("taskupdate") || normalized.startsWith("tasklist")) {
      const todos = Array.isArray(input.todos) ? input.todos : [input];
      const body = todos.map(todo => {
        const status = String(todo?.status || "pending").toLowerCase();
        const marker = status === "completed" ? "✓" : status === "in_progress" ? "◐" : "○";
        return `${marker} ${todo?.content || todo?.subject || todo?.description || "작업"}`;
      }).join("\n");
      return { title: "Plan", body, kind: "plan", resourceKey: "" };
    }
    return { title: name, body: prettyInput(input), kind: "tool", resourceKey: "" };
  }

  function renderActivityBody(pre, text, kind) {
    if (kind !== "diff") { pre.textContent = text || ""; return; }
    pre.replaceChildren();
    for (const line of String(text || "").split("\n")) {
      const span = document.createElement("span");
      span.className = `diff-line ${line.startsWith("+++") || line.startsWith("---") || line.startsWith("@@") ? "meta" : line.startsWith("+") ? "add" : line.startsWith("-") ? "remove" : ""}`;
      span.textContent = line || " ";
      pre.appendChild(span);
    }
  }

  function updateScrollBoundary() {
    composerShell.classList.toggle("has-scroll", conversation.scrollHeight > conversation.clientHeight + 2);
  }

  function updateEmpty() {
    emptyState.hidden = messages.childElementCount > 0;
    requestAnimationFrame(updateScrollBoundary);
  }

  function scrollAfterAppend(force = false) {
    if (!force && !nearBottom) return;
    if (scrollFrame) return;
    scrollFrame = requestAnimationFrame(() => {
      scrollFrame = 0;
      conversation.scrollTop = conversation.scrollHeight;
      updateScrollBoundary();
    });
  }

  function append(element, forceScroll = false) {
    messages.appendChild(element);
    updateEmpty();
    scrollAfterAppend(forceScroll);
  }

  function setStatus() {}

  function setBusy(value) {
    if (busy === value) return;
    busy = value;
    typing.hidden = !busy;
    action.classList.toggle("busy", busy);
    action.setAttribute("aria-label", busy ? "응답 중지" : "보내기");
    updateAction();
    requestAnimationFrame(updateScrollBoundary);
  }

  function updateAction() { action.disabled = !busy && !prompt.value.trim() && attachments.length === 0; }

  function makeTurn(role, text, attached = []) {
    const turn = document.createElement("article");
    turn.className = `turn ${role}`;
    const bubble = document.createElement("div");
    bubble.className = "bubble";
    if (role === "assistant") {
      bubble.classList.add("markdown");
      reconcileMarkdown(bubble, text);
    } else {
      if (Array.isArray(attached) && attached.length) {
        const gallery = document.createElement("div");
        gallery.className = "turn-attachments";
        for (const item of attached) {
          if (item.kind === "image" && item.preview) {
            const image = document.createElement("img");
            image.className = "turn-image";
            image.src = item.preview;
            image.alt = item.name || "첨부 이미지";
            gallery.appendChild(image);
          } else {
            const file = document.createElement("div");
            file.className = "turn-file";
            file.append(fileIcon());
            const name = document.createElement("span");
            name.textContent = item.name || "첨부 파일";
            file.appendChild(name);
            gallery.appendChild(file);
          }
        }
        bubble.appendChild(gallery);
      }
      if (text) {
        const copy = document.createElement("div");
        copy.textContent = text;
        bubble.appendChild(copy);
      }
    }
    turn.appendChild(bubble);
    return turn;
  }

  function startAssistantStream(streamId) {
    if (!streamId || assistantStreams.has(streamId)) return;
    const turn = makeTurn("assistant", "");
    turn.classList.add("stream-enter");
    const state = {
      bubble: turn.querySelector(".bubble"), text: "", pending: "", frame: 0, ended: false,
      lastPaint: 0,
    };
    assistantStreams.set(streamId, state);
    append(turn);
    requestAnimationFrame(() => turn.classList.add("is-visible"));
  }

  function streamChunkLength(state, backlog, reduceMotion) {
    if (reduceMotion) return backlog;
    if (state.ended) return Math.min(backlog, Math.max(10, Math.ceil(backlog * .42)));
    const minimum = state.text.length < 80 ? 2 : 3;
    const maximum = state.text.length > 12000 ? 96 : 160;
    return Math.min(backlog, maximum, Math.max(minimum, Math.ceil(backlog * .18)));
  }

  function safeStreamCut(text, count) {
    let cut = Math.min(text.length, Math.max(0, count));
    if (cut > 0 && cut < text.length && /[\uD800-\uDBFF]/.test(text[cut - 1])) cut++;
    if (cut > 0 && cut < text.length && text[cut - 1] === "\r" && text[cut] === "\n") cut++;
    while (cut < text.length && /\p{Mark}/u.test(text[cut])) cut++;
    return cut;
  }

  function scheduleAssistantFrame(streamId, state) {
    if (state.frame) return;
    state.frame = requestAnimationFrame(now => {
      state.frame = 0;
      if (!assistantStreams.has(streamId)) return;
      const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;
      const backlog = state.pending.length;
      if (!backlog) {
        if (state.ended) assistantStreams.delete(streamId);
        return;
      }

      const paintInterval = state.text.length > 12000 ? 34 : state.text.length > 4000 ? 28 : 22;
      if (!state.ended && !reduceMotion && now - state.lastPaint < paintInterval) {
        scheduleAssistantFrame(streamId, state);
        return;
      }

      const count = safeStreamCut(state.pending, streamChunkLength(state, backlog, reduceMotion));
      if (count > 0) {
        state.text += state.pending.slice(0, count);
        state.pending = state.pending.slice(count);
      }
      state.lastPaint = now;
      reconcileMarkdown(state.bubble, state.text);
      scrollAfterAppend();
      if (state.pending.length) scheduleAssistantFrame(streamId, state);
      else if (state.ended) assistantStreams.delete(streamId);
    });
  }

  function appendAssistantDelta(streamId, text) {
    if (!assistantStreams.has(streamId)) startAssistantStream(streamId);
    const state = assistantStreams.get(streamId);
    if (!state) return;
    state.pending += text || "";
    scheduleAssistantFrame(streamId, state);
  }

  function endAssistantStream(streamId, immediate = false) {
    const state = assistantStreams.get(streamId);
    if (!state) return;
    state.ended = true;
    if (immediate) {
      if (state.frame) cancelAnimationFrame(state.frame);
      state.frame = 0;
      state.text += state.pending;
      state.pending = "";
      reconcileMarkdown(state.bubble, state.text);
      assistantStreams.delete(streamId);
      scrollAfterAppend();
      return;
    }
    scheduleAssistantFrame(streamId, state);
  }

  function startThinkingStream(streamId) {
    if (!streamId || thinkingStreams.has(streamId)) return;
    const details = makeActivity("생각", "", "", true);
    details.classList.remove("success");
    details.classList.add("running");
    details.open = true;
    details.querySelector(".activity-state").textContent = "생각 중";
    thinkingStreams.set(streamId, { details, text: "", frame: 0 });
    append(details);
  }

  function appendThinkingDelta(streamId, text) {
    if (!thinkingStreams.has(streamId)) startThinkingStream(streamId);
    const state = thinkingStreams.get(streamId);
    if (!state) return;
    state.text += text || "";
    if (state.frame) return;
    state.frame = requestAnimationFrame(() => {
      state.frame = 0;
      state.details.querySelector("pre").textContent = state.text;
      scrollAfterAppend();
    });
  }

  function endThinkingStream(streamId) {
    const state = thinkingStreams.get(streamId);
    if (!state) return;
    if (state.frame) cancelAnimationFrame(state.frame);
    state.details.querySelector("pre").textContent = state.text;
    state.details.classList.remove("running");
    state.details.classList.add("success");
    state.details.querySelector(".activity-state").textContent = "완료";
    state.details.open = false;
    thinkingStreams.delete(streamId);
  }

  function makeActivity(title, body, id, thinking = false, kind = "tool") {
    const details = document.createElement("details");
    details.className = thinking ? "activity thinking success" : "activity running";
    details.dataset.kind = thinking ? "thinking" : kind;
    details.open = !thinking;
    details.innerHTML = `<summary><span class="activity-icon"></span><span class="activity-title"></span><span class="activity-state"></span></summary><div class="activity-body"><pre></pre></div>`;
    details.querySelector(".activity-title").textContent = title;
    details.querySelector(".activity-state").textContent = thinking ? "완료" : "실행 중";
    renderActivityBody(details.querySelector("pre"), body, kind);
    if (id) tools.set(id, details);
    return details;
  }

  function createToolGroup() {
    const element = document.createElement("details");
    element.className = "tool-group running";
    element.innerHTML = `<summary><span class="tool-group-icon" aria-hidden="true"></span><span class="tool-group-title"></span><span class="tool-group-state"></span></summary><div class="tool-group-body"></div>`;
    const group = {
      element,
      commands: 0,
      editedFiles: new Set(),
      readFiles: new Set(),
      searches: 0,
      plans: 0,
      otherTools: 0,
      running: 0,
      errors: 0,
      canceled: 0,
      summary: "",
    };
    toolGroups.add(group);
    currentToolGroup = group;
    updateToolGroup(group);
    append(element);
    return group;
  }

  function toolGroupSummary(group) {
    if (vibeMode && group.plans > 0)
      return group.plans === 1 ? "계획" : `계획 ${group.plans}`;
    const parts = [
      ["파일 변경", group.editedFiles.size],
      ["명령", group.commands],
      ["파일 읽기", group.readFiles.size],
      ["검색", group.searches],
      ["계획", group.plans],
      ["기타", group.otherTools],
    ];
    return group.summary || parts.filter(([, count]) => count > 0).map(([label, count]) => `${label} ${count}`).join(" · ") || "작업";
  }

  function updateToolGroup(group) {
    group.element.querySelector(".tool-group-title").textContent = toolGroupSummary(group);
    const state = group.element.querySelector(".tool-group-state");
    group.element.classList.remove("running", "success", "error", "canceled");
    if (group.running > 0) {
      group.element.classList.add("running");
      state.textContent = "실행 중";
    } else if (group.errors > 0) {
      group.element.classList.add("error");
      state.textContent = `오류 ${group.errors}`;
    } else if (group.canceled > 0) {
      group.element.classList.add("canceled");
      state.textContent = "중단됨";
    } else {
      group.element.classList.add("success");
      state.textContent = "완료";
    }
  }

  function countTool(group, presentation) {
    if (presentation.kind === "shell") group.commands++;
    else if (presentation.kind === "diff") group.editedFiles.add(presentation.resourceKey || `diff:${group.editedFiles.size}`);
    else if (presentation.kind === "read") group.readFiles.add(presentation.resourceKey || `read:${group.readFiles.size}`);
    else if (presentation.kind === "search") group.searches++;
    else if (presentation.kind === "plan") {
      group.plans++;
      group.element.dataset.hasPlan = "true";
    }
    else group.otherTools++;
  }

  function groupedToolLabel(presentation) {
    if (presentation.kind !== "shell" && presentation.kind !== "search") return presentation.title;
    const firstLine = String(presentation.body || "").split("\n")[0].trim();
    return firstLine ? `${presentation.title} · ${firstLine}` : presentation.title;
  }

  function addGroupedActivity(event) {
    const group = currentToolGroup || createToolGroup();
    const presentation = toolPresentation(event);
    const element = document.createElement("details");
    element.className = "tool-item running";
    element.dataset.kind = presentation.kind;
    element.innerHTML = `<summary><span class="tool-item-title"></span><span class="tool-item-state">실행 중</span></summary><div class="tool-item-body"><pre></pre></div>`;
    element.querySelector(".tool-item-title").textContent = groupedToolLabel(presentation);
    renderActivityBody(element.querySelector("pre"), presentation.body, presentation.kind);
    group.element.querySelector(".tool-group-body").appendChild(element);
    countTool(group, presentation);
    group.running++;
    updateToolGroup(group);
    const state = { element, group, presentation, status: "running" };
    if (event.toolUseId) tools.set(event.toolUseId, state);
    scrollAfterAppend();
    return state;
  }

  function completeToolState(state, event) {
    if (!state || state.status !== "running") return;
    state.status = event.isError ? "error" : event.canceled ? "canceled" : "success";
    state.group.running = Math.max(0, state.group.running - 1);
    if (event.isError) state.group.errors++;
    if (event.canceled) state.group.canceled++;
    state.element.classList.remove("running", "success", "error", "canceled");
    state.element.classList.add(state.status);
    state.element.querySelector(".tool-item-state").textContent = event.isError ? "오류" : event.canceled ? "중단됨" : "완료";
    const output = state.element.querySelector("pre");
    if (event.text) {
      const combined = `${output.textContent ? `${output.textContent}\n\n` : ""}${event.text}`;
      renderActivityBody(output, combined, state.presentation.kind);
    }
    state.element.open = event.isError;
    if (event.isError) state.group.element.open = true;
    updateToolGroup(state.group);
  }

  function finishActivity(event) {
    let state = event.toolUseId ? tools.get(event.toolUseId) : null;
    if (!state) state = addGroupedActivity({ toolName: "도구 실행 결과", toolUseId: event.toolUseId, input: {} });
    completeToolState(state, event);
  }

  function updateToolProgress(event) {
    const state = event.toolUseId ? tools.get(event.toolUseId) : null;
    if (!state || state.status !== "running") return;
    const seconds = Math.max(0, Number(event.input?.elapsedSeconds) || 0);
    state.element.querySelector(".tool-item-state").textContent = seconds
      ? `실행 중 · ${formatElapsed(seconds * 1000)}`
      : "실행 중";
  }

  function applyToolSummary(event) {
    const ids = Array.isArray(event.input?.toolUseIds) ? event.input.toolUseIds : [];
    const state = ids.map(id => tools.get(id)).find(Boolean);
    if (!state?.group || !event.text) return;
    state.group.summary = event.text.trim();
    updateToolGroup(state.group);
  }

  function taskState(event) {
    const taskId = String(event.input?.taskId || "");
    return (taskId && tasks.get(taskId)) || (event.toolUseId ? tools.get(event.toolUseId) : null);
  }

  function startTask(event) {
    const state = event.toolUseId ? tools.get(event.toolUseId) : null;
    const taskId = String(event.input?.taskId || "");
    if (state) {
      state.element.dataset.kind = "task";
      state.element.querySelector(".tool-item-title").textContent = `Agent · ${event.text || "하위 작업"}`;
      if (taskId) tasks.set(taskId, state);
      return;
    }
    const sheet = makeSystemOutput({ text: event.text || "하위 작업", input: { kind: "task", state: "running" } });
    if (sheet && taskId) tasks.set(taskId, { element: sheet, standalone: true, status: "running" });
  }

  function updateTask(event) {
    const state = taskState(event);
    if (!state) return;
    const durationMs = Number(event.input?.durationMs) || 0;
    const toolUses = Number(event.input?.toolUses) || 0;
    const status = [toolUses ? `도구 ${toolUses}` : "", durationMs ? formatElapsed(durationMs) : ""].filter(Boolean).join(" · ");
    if (state.standalone) {
      state.element.querySelector(".system-sheet-state").textContent = status || "작업 중";
      const body = state.element.querySelector("pre");
      if (body && event.text) body.textContent = stripTerminalFormatting(event.text);
      return;
    }
    state.element.querySelector(".tool-item-state").textContent = status || "작업 중";
  }

  function finishTask(event) {
    const state = taskState(event);
    if (!state) return;
    const taskId = String(event.input?.taskId || "");
    if (taskId) tasks.delete(taskId);
    if (state.standalone) {
      state.status = event.isError ? "error" : "success";
      state.element.classList.remove("running", "success", "error");
      state.element.classList.add(state.status);
      state.element.querySelector(".system-sheet-state").textContent = event.isError ? "실패" : "완료";
      const body = state.element.querySelector("pre");
      if (body && event.text) body.textContent = stripTerminalFormatting(event.text);
      return;
    }
    completeToolState(state, { isError: event.isError, canceled: event.input?.status === "stopped", text: event.text || "" });
  }

  function sealToolGroup() {
    currentToolGroup = null;
  }

  function finalizeRunningTools(canceled = false) {
    for (const state of tools.values())
      completeToolState(state, { isError: false, canceled, text: "" });
    tools.clear();
    sealToolGroup();
  }

  function makeError(title, text) {
    const card = document.createElement("section");
    card.className = "error-card";
    const heading = document.createElement("h2");
    heading.className = "card-title";
    heading.textContent = title;
    const body = document.createElement("p");
    body.className = "card-body";
    body.textContent = text;
    card.append(heading, body);
    return card;
  }

  function compactTokenLabel(value) {
    const tokens = Math.max(0, Number(value) || 0);
    if (tokens >= 1_000_000) return `${(tokens / 1_000_000).toFixed(tokens % 1_000_000 ? 1 : 0)}m`;
    if (tokens >= 1_000) return `${Math.round(tokens / 1_000)}k`;
    return String(Math.round(tokens));
  }

  function ensureCompactMarker() {
    if (compactMarker?.isConnected) return compactMarker;
    const element = document.createElement("section");
    element.className = "compact-marker running";
    element.setAttribute("role", "status");
    element.innerHTML = `<span class="compact-line"></span><span class="compact-copy"><svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="6" cy="7" r="3"/><path d="m8.7 8.3 10.4 6.2M8.7 15.7 19.1 9.5"/><circle cx="6" cy="17" r="3"/></svg><span class="compact-title">Compact 진행 중</span><span class="compact-meta"></span></span><span class="compact-line"></span>`;
    compactMarker = element;
    sealToolGroup();
    append(element, true);
    return element;
  }

  function renderCompactStatus(event) {
    const element = ensureCompactMarker();
    const state = event.input?.state || (event.isError ? "error" : "success");
    element.classList.remove("running", "success", "error");
    element.classList.add(state);
    element.querySelector(".compact-title").textContent = state === "running"
      ? "Compact 진행 중"
      : state === "error" ? "Compact 실패" : "Compact 완료";
    element.querySelector(".compact-meta").textContent = event.text || "";
  }

  function renderCompactBoundary(event) {
    const element = ensureCompactMarker();
    const input = event.input || {};
    const preTokens = Number(input.preTokens) || 0;
    const postTokens = Number(input.postTokens) || 0;
    const durationMs = Number(input.durationMs) || 0;
    const parts = [];
    if (preTokens) parts.push(postTokens
      ? `${compactTokenLabel(preTokens)} → ${compactTokenLabel(postTokens)}`
      : `${compactTokenLabel(preTokens)} context`);
    if (durationMs) parts.push(formatElapsed(durationMs));
    element.classList.remove("running", "error");
    element.classList.add("success");
    element.querySelector(".compact-title").textContent = input.trigger === "auto" ? "자동 Compact 완료" : "Compact 완료";
    element.querySelector(".compact-meta").textContent = parts.join(" · ");
  }

  function resetConversationView() {
    for (const state of assistantStreams.values()) if (state.frame) cancelAnimationFrame(state.frame);
    for (const state of thinkingStreams.values()) if (state.frame) cancelAnimationFrame(state.frame);
    messages.replaceChildren();
    tools.clear();
    tasks.clear();
    toolGroups.clear();
    currentToolGroup = null;
    compactMarker = null;
    pendingLocalCommand = "";
    quietCommandInFlight = false;
    assistantStreams.clear();
    thinkingStreams.clear();
    resetWorkingTimer();
    setBusy(false);
    updateEmpty();
  }

  function stripTerminalFormatting(text) {
    return String(text || "")
      .replace(/[\u001B\u009B][[\]()#;?]*(?:(?:(?:[a-zA-Z\d]*(?:;[-a-zA-Z\d\/#&.:=?%@~_]+)*)?\u0007)|(?:(?:\d{1,4}(?:[;:]\d{0,4})*)?[\dA-PR-TZcf-nq-uy=><~]))/g, "")
      .replace(/<local-command-(?:stdout|stderr)>|<\/local-command-(?:stdout|stderr)>/g, "")
      .trim();
  }

  function makeSystemOutput(event) {
    const text = stripTerminalFormatting(event.text);
    if (!text) return null;
    let kind = event.input?.kind || "information";
    if (pendingLocalCommand && kind === "information") kind = "command";
    if (kind === "information" || kind === "notification") {
      const notice = document.createElement("section");
      notice.className = `system-notice ${event.input?.level || "notice"}`;
      notice.textContent = text;
      append(notice);
      return notice;
    }
    const sheet = document.createElement("details");
    sheet.className = `system-sheet ${kind === "task" ? "task-sheet running" : "command-sheet"}`;
    sheet.open = true;
    const title = kind === "task" ? "Agent" : pendingLocalCommand ? `/${pendingLocalCommand}` : "Claude Code";
    sheet.innerHTML = `<summary><span class="system-sheet-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M4 17 10 11 4 5"/><path d="M12 19h8"/></svg></span><span class="system-sheet-title"></span><span class="system-sheet-state"></span></summary><div class="system-sheet-body"><pre></pre></div>`;
    sheet.querySelector(".system-sheet-title").textContent = title;
    sheet.querySelector(".system-sheet-state").textContent = kind === "task" ? "작업 중" : "";
    sheet.querySelector("pre").textContent = text;
    append(sheet);
    if (kind !== "task") pendingLocalCommand = "";
    return sheet;
  }

  function denyTool(event) {
    const state = event.toolUseId ? tools.get(event.toolUseId) : null;
    if (state) {
      completeToolState(state, { isError: true, text: event.text || "권한이 허용되지 않았습니다." });
      return;
    }
    append(makeError(`권한 거부 · ${event.toolName || "도구"}`, event.text || "권한이 허용되지 않았습니다."), true);
  }

  function makePermission(event) {
    const card = document.createElement("section");
    card.className = "permission";
    card.dataset.requestId = event.requestId || "";
    const title = document.createElement("h2");
    title.className = "card-title";
    const questions = event.toolName === "AskUserQuestion" && Array.isArray(event.input?.questions)
      ? event.input.questions.filter(question => question && typeof question.question === "string")
      : [];
    title.textContent = questions.length ? "Claude의 질문" : `권한 요청 · ${event.toolName || "도구"}`;
    card.appendChild(title);

    const questionFields = [];
    if (questions.length) {
      card.classList.add("structured");
      const list = document.createElement("div");
      list.className = "question-list";
      for (const question of questions) {
        const section = document.createElement("section");
        section.className = "question-item";
        if (question.header) {
          const header = document.createElement("div");
          header.className = "question-header";
          header.textContent = question.header;
          section.appendChild(header);
        }
        const copy = document.createElement("p");
        copy.className = "question-copy";
        copy.textContent = question.question;
        section.appendChild(copy);
        const selected = new Set();
        const options = document.createElement("div");
        options.className = "question-options";
        for (const option of Array.isArray(question.options) ? question.options : []) {
          if (!option?.label) continue;
          const button = document.createElement("button");
          button.type = "button";
          button.className = "question-option";
          button.innerHTML = `<span class="question-option-check" aria-hidden="true"></span><span class="question-option-copy"><strong></strong><small></small></span>`;
          button.querySelector("strong").textContent = option.label;
          button.querySelector("small").textContent = option.description || "";
          button.addEventListener("click", () => {
            if (question.multiSelect !== true) {
              selected.clear();
              for (const sibling of options.querySelectorAll(".question-option")) sibling.classList.remove("selected");
            }
            if (button.classList.contains("selected")) {
              button.classList.remove("selected");
              selected.delete(option.label);
            } else {
              button.classList.add("selected");
              selected.add(option.label);
            }
          });
          options.appendChild(button);
        }
        section.appendChild(options);
        const custom = document.createElement("textarea");
        custom.className = "permission-input question-custom";
        custom.rows = 1;
        custom.placeholder = "직접 입력";
        section.appendChild(custom);
        questionFields.push({ question: question.question, selected, custom, section });
        list.appendChild(section);
      }
      card.appendChild(list);
    } else {
      const body = document.createElement("p");
      body.className = "card-body";
      body.textContent = event.question || prettyInput(event.input);
      card.appendChild(body);
    }
    const actions = document.createElement("div");
    actions.className = "permission-actions";
    const allow = document.createElement("button");
    allow.className = "allow";
    allow.type = "button";
    allow.textContent = questions.length ? "답변 보내기" : "허용";
    const deny = document.createElement("button");
    deny.type = "button";
    deny.textContent = questions.length ? "취소" : "거부";
    actions.append(allow, deny);
    card.appendChild(actions);

    const respond = approved => {
      const answers = {};
      if (approved && questionFields.length) {
        let incomplete = null;
        for (const field of questionFields) {
          const custom = field.custom.value.trim();
          const value = custom || [...field.selected].join(", ");
          field.section.classList.toggle("invalid", !value);
          if (!value && !incomplete) incomplete = field;
          if (value) answers[field.question] = value;
        }
        if (incomplete) {
          incomplete.custom.focus();
          return;
        }
      }
      allow.disabled = deny.disabled = true;
      for (const control of card.querySelectorAll("button, textarea")) control.disabled = true;
      post({ type: "permission", requestId: event.requestId || "", allow: approved, answers });
    };
    allow.addEventListener("click", () => respond(true));
    deny.addEventListener("click", () => respond(false));
    return card;
  }

  function resolvePermission(requestId, allowed) {
    const card = [...document.querySelectorAll(".permission")].find(node => node.dataset.requestId === requestId);
    if (!card) return;
    card.querySelector(".card-title").textContent = allowed ? "응답을 전달했습니다" : "요청을 거부했습니다";
    for (const control of card.querySelectorAll("button, textarea")) control.disabled = true;
  }

  function compactModelName(model) {
    const sources = [model?.displayName, model?.value, model?.resolvedModel, typeof model === "string" ? model : ""]
      .filter(Boolean)
      .map(value => String(value).replace(/\([^)]*(?:context|ctx)[^)]*\)/gi, "").replace(/\b1m\s*(?:context|ctx)?\b/gi, ""));
    const identity = sources.join(" ");
    const family = ["opus", "sonnet", "haiku", "fable"].find(name => identity.toLowerCase().includes(name));
    if (family) {
      const version = sources.map(value => value.match(new RegExp(`${family}[^0-9]*(\\d+)(?:[.\\-](\\d+))?`, "i"))).find(Boolean);
      const label = family[0].toUpperCase() + family.slice(1);
      return version ? `${label} ${version[1]}${version[2] ? `.${version[2]}` : ""}` : label;
    }
    return identity
      .replace(/^claude\s+/i, "")
      .replace(/\s*\([^)]*(?:context|ctx)[^)]*\)/gi, "")
      .trim() || "Model";
  }

  function modelDescription(model) {
    const identity = [model?.displayName, model?.value, model?.resolvedModel].filter(Boolean).join(" ").toLowerCase();
    if (identity.includes("opus")) return "가장 깊은 추론과 복잡한 작업에 적합";
    if (identity.includes("sonnet")) return "성능과 속도의 균형이 좋은 범용 모델";
    if (identity.includes("haiku")) return "빠른 응답과 가벼운 작업에 적합";
    if (identity.includes("fable")) return "창의적인 작업과 자연스러운 표현에 적합";
    return "Claude Code에서 제공하는 모델";
  }

  function modelName(value) {
    if (!value || value.trim().toLowerCase() === "default") return "Model";
    const match = models.find(model => model.value === value || model.resolvedModel === value);
    return compactModelName(match || value);
  }

  function isDefaultModel(model) {
    const value = typeof model?.value === "string" ? model.value.trim().toLowerCase() : "";
    const name = typeof model?.displayName === "string" ? model.displayName.trim().toLowerCase() : "";
    return value === "default" || name === "default" || name.startsWith("default (");
  }

  function normalizeModel(value) {
    const model = typeof value === "string" ? value.trim() : "";
    return model && model.toLowerCase() !== "default" ? model : defaultResolvedModel;
  }

  function permissionName(value) {
    return permissionModes.find(mode => mode.id === value)?.label || "Accept edits";
  }

  function effortName(value) {
    return effortLevels.find(level => level.value === value)?.displayName || "High";
  }

  function formatContextTokens(value) {
    return `${Math.max(0, Math.round(Number(value || 0) / 1000))}k`;
  }

  function updateContextUsage(input) {
    if (input && typeof input === "object") {
      if (Number.isFinite(input.usedTokens)) currentContextTokens = Math.max(0, input.usedTokens);
      if (Number.isFinite(input.contextWindow) && input.contextWindow > 0)
        currentContextWindow = input.contextWindow;
    }
    const label = `${formatContextTokens(currentContextTokens)}/${formatContextTokens(currentContextWindow)}`;
    contextLabel.textContent = label;
    contextControl.setAttribute("aria-label", `Context 사용량 ${label}`);
  }

  function updateVibeMode() {
    document.body.dataset.vibe = vibeMode ? "on" : "off";
    vibeLabel.textContent = vibeMode ? "Vibe On" : "Vibe Off";
    vibeControl.setAttribute("aria-pressed", String(vibeMode));
    vibeControl.setAttribute("aria-label", vibeLabel.textContent);
    vibeControl.style.setProperty("--control-color", vibeMode ? statusPalette.plan : statusPalette.text);
    for (const group of toolGroups) {
      if (group.element.isConnected) updateToolGroup(group);
      else toolGroups.delete(group);
    }
    requestAnimationFrame(updateScrollBoundary);
  }

  function selectedModelInfo() {
    return models.find(model => model.value === currentModel || model.resolvedModel === currentModel);
  }

  function supportedEffortEntries() {
    const model = selectedModelInfo();
    if (!model) return models.length ? [] : effortLevels;
    if (model.supportsEffort !== true) return [];
    const supported = Array.isArray(model?.supportedEffortLevels)
      ? new Set(model.supportedEffortLevels)
      : null;
    return supported?.size
      ? effortLevels.filter(level => supported.has(level.value))
      : effortLevels;
  }

  function updateControlLabels() {
    const effortSupported = supportedEffortEntries().length > 0;
    modelLabel.textContent = modelName(currentModel);
    effortLabel.textContent = effortName(currentEffort);
    permissionLabel.textContent = permissionName(currentPermissionMode);
    const modelInfo = selectedModelInfo();
    const modelIdentity = `${currentModel} ${modelInfo?.displayName || ""}`.toLowerCase();
    const modelFamily = ["haiku", "sonnet", "opus", "fable"].find(name => modelIdentity.includes(name));
    modelControl.style.setProperty("--control-color", statusPalette[modelFamily] || statusPalette.text);
    effortControl.style.setProperty("--control-color", statusPalette[currentEffort] || statusPalette.text);
    permissionControl.style.setProperty("--control-color", statusPalette[currentPermissionMode] || statusPalette.text);
    effortControl.hidden = !effortSupported;
    effortControl.disabled = !effortSupported;
    modelControl.setAttribute("aria-label", `모델 변경 · ${modelLabel.textContent}`);
    effortControl.setAttribute("aria-label", effortSupported
      ? `Effort 변경 · ${effortLabel.textContent}`
      : "현재 모델은 Effort를 지원하지 않습니다");
    permissionControl.setAttribute("aria-label", `권한 모드 변경 · ${permissionLabel.textContent}`);
  }

  function applyCapabilities(input) {
    if (!input || typeof input !== "object") return;
    if (Array.isArray(input.models)) {
      const defaultModel = input.models.find(isDefaultModel);
      defaultResolvedModel = typeof defaultModel?.resolvedModel === "string"
        ? defaultModel.resolvedModel.trim()
        : "";
      const seen = new Set();
      models = input.models.filter(model => {
        if (!model || typeof model.value !== "string" || isDefaultModel(model) || seen.has(model.value)) return false;
        seen.add(model.value);
        return true;
      });
      const modelIdentity = model => `${model.value || ""} ${model.resolvedModel || ""} ${model.displayName || ""}`.toLowerCase();
      const opusIndex = models.findIndex(model => modelIdentity(model).includes("opus"));
      const fableIndex = models.findIndex(model => modelIdentity(model).includes("fable"));
      if (opusIndex >= 0 && fableIndex >= 0)
        [models[opusIndex], models[fableIndex]] = [models[fableIndex], models[opusIndex]];
    }
    if (Array.isArray(input.commands)) {
      const seen = new Set();
      commands = input.commands.filter(command => {
        const name = typeof command?.name === "string" ? command.name.trim() : "";
        const key = name.toLowerCase();
        const description = typeof command?.description === "string" ? command.description.trim() : "";
        if (!name || hiddenSlashCommands.has(key) || description.toLowerCase().startsWith("(removed)") || seen.has(key)) return false;
        seen.add(key);
        return true;
      });
    }
    if (typeof input.currentModel === "string") currentModel = normalizeModel(input.currentModel);
    if (typeof input.currentEffort === "string") currentEffort = input.currentEffort;
    if (typeof input.currentPermissionMode === "string") currentPermissionMode = input.currentPermissionMode;
    updateControlLabels();
    updateCommandMenu();
  }

  function applyConfiguration(input) {
    if (!input || typeof input !== "object") return;
    if (typeof input.model === "string") currentModel = normalizeModel(input.model);
    if (typeof input.effort === "string") currentEffort = input.effort;
    if (typeof input.permissionMode === "string") currentPermissionMode = input.permissionMode;
    updateControlLabels();
  }

  function closeMenus() {
    commandMenu.hidden = true;
    controlMenu.hidden = true;
    openControl = "";
    delete controlMenu.dataset.control;
    modelControl.setAttribute("aria-expanded", "false");
    effortControl.setAttribute("aria-expanded", "false");
    permissionControl.setAttribute("aria-expanded", "false");
  }

  function slashRange() {
    const cursor = prompt.selectionStart ?? prompt.value.length;
    const before = prompt.value.slice(0, cursor);
    for (let slash = before.lastIndexOf("/"); slash >= 0; slash = slash === 0 ? -1 : before.lastIndexOf("/", slash - 1)) {
      if (slash > 0 && !/\s/.test(before[slash - 1])) continue;
      const query = before.slice(slash + 1);
      if (/[/\s\n\r\t"']/.test(query)) continue;
      return { start: slash, end: cursor, query: query.toLowerCase() };
    }
    return null;
  }

  function commandMatches(command, query) {
    if (!query) return true;
    const fields = [command.name, ...(Array.isArray(command.aliases) ? command.aliases : [])];
    return fields.some(value => String(value || "").toLowerCase().includes(query));
  }

  function isQuietSlashCommand(text) {
    const match = /^\/([^\s]+)/.exec(String(text || "").trim());
    if (!match) return false;
    const name = match[1].toLowerCase();
    return quietSlashCommands.has(name) || ["cost", "stats", "settings", "reset", "new"].includes(name);
  }

  function updateCommandMenu() {
    if (openControl) return;
    const range = slashRange();
    if (!range) { commandMenu.hidden = true; return; }
    visibleCommands = commands.filter(command => commandMatches(command, range.query));
    commandSelection = Math.min(commandSelection, Math.max(0, visibleCommands.length - 1));
    commandMenu.replaceChildren();
    const heading = document.createElement("div");
    heading.className = "menu-heading";
    heading.textContent = "Claude 명령어";
    commandMenu.appendChild(heading);
    if (!commands.length) {
      const empty = document.createElement("div");
      empty.className = "menu-empty";
      empty.textContent = "명령어를 불러오는 중…";
      commandMenu.appendChild(empty);
      commandMenu.hidden = false;
      return;
    }
    if (!visibleCommands.length) {
      const empty = document.createElement("div");
      empty.className = "menu-empty";
      empty.textContent = "일치하는 명령어가 없습니다";
      commandMenu.appendChild(empty);
      commandMenu.hidden = false;
      return;
    }
    visibleCommands.forEach((command, index) => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "menu-item";
      button.setAttribute("role", "option");
      button.setAttribute("aria-selected", String(index === commandSelection));
      button.dataset.index = String(index);
      const copy = document.createElement("span");
      copy.className = "menu-copy";
      const title = document.createElement("span");
      title.className = "menu-title";
      const code = document.createElement("code");
      code.textContent = `/${command.name}`;
      title.appendChild(code);
      if (command.argumentHint) {
        const hint = document.createElement("span");
        hint.className = "command-argument";
        hint.textContent = command.argumentHint;
        title.appendChild(hint);
      }
      copy.appendChild(title);
      button.appendChild(copy);
      commandMenu.appendChild(button);
    });
    commandMenu.hidden = false;
    commandMenu.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" });
  }

  function selectCommand(index) {
    const command = visibleCommands[index];
    const range = slashRange();
    if (!command || !range) return;
    const replacement = `/${command.name} `;
    prompt.setRangeText(replacement, range.start, range.end, "end");
    commandMenu.hidden = true;
    commandSelection = 0;
    resizePrompt();
    prompt.focus();
  }

  function openControlMenu(type) {
    const next = openControl === type ? "" : type;
    closeMenus();
    if (!next) { prompt.focus(); return; }
    openControl = next;
    controlMenu.dataset.control = type;
    const isModel = type === "model";
    const isEffort = type === "effort";
    const entries = isModel
      ? models
      : isEffort
        ? supportedEffortEntries()
        : permissionModes.map(mode => ({ value: mode.id, displayName: mode.label, description: mode.description }));
    const selected = isModel ? currentModel : isEffort ? currentEffort : currentPermissionMode;
    controlMenu.replaceChildren();
    const heading = document.createElement("div");
    heading.className = "menu-heading";
    heading.textContent = isModel ? "모델" : isEffort ? "Effort" : "권한 모드";
    controlMenu.appendChild(heading);
    for (const entry of entries) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "menu-item";
      button.dataset.value = entry.value;
      button.dataset.selected = String(entry.value === selected || (isModel && entry.resolvedModel === selected));
      const copy = document.createElement("span");
      copy.className = "menu-copy";
      const title = document.createElement("span");
      title.className = "menu-title";
      title.textContent = isModel ? compactModelName(entry) : entry.displayName || entry.value;
      const description = document.createElement("span");
      description.className = "menu-description";
      description.textContent = isModel ? modelDescription(entry) : entry.description || "";
      copy.append(title, description);
      const check = document.createElement("span");
      check.className = "menu-check";
      check.textContent = button.dataset.selected === "true" ? "✓" : "";
      button.append(copy, check);
      controlMenu.appendChild(button);
    }
    if (isModel && models.length === 0) {
      const empty = document.createElement("div");
      empty.className = "menu-empty";
      empty.textContent = "모델 목록을 불러오는 중…";
      controlMenu.appendChild(empty);
      post({ type: "refreshCapabilities" });
    } else if (isEffort && entries.length === 0) {
      const empty = document.createElement("div");
      empty.className = "menu-empty";
      empty.textContent = "현재 모델은 Effort를 지원하지 않습니다";
      controlMenu.appendChild(empty);
    }
    controlMenu.hidden = false;
    modelControl.setAttribute("aria-expanded", String(isModel));
    effortControl.setAttribute("aria-expanded", String(isEffort));
    permissionControl.setAttribute("aria-expanded", String(!isModel && !isEffort));
  }

  function selectControl(value) {
    if (openControl === "model") {
      currentModel = value;
      post({ type: "setModel", model: value });
      setStatus("모델을 변경하는 중…");
    } else if (openControl === "effort") {
      currentEffort = value;
      post({ type: "setEffort", effort: value });
      setStatus("Effort를 변경하는 중…");
    } else if (openControl === "permission") {
      currentPermissionMode = value;
      post({ type: "setPermissionMode", permissionMode: value });
      setStatus("권한 모드를 변경하는 중…");
    }
    updateControlLabels();
    closeMenus();
    prompt.focus();
  }

  function renderEvent(event, live = true) {
    if (!event || !event.type) return;
    switch (event.type) {
      case "ready": setStatus("SDK 브리지 준비됨"); break;
      case "starting": setStatus("Claude에 연결하는 중…", "busy"); break;
      case "session": setStatus("대화 준비됨", "ready"); break;
      case "capabilities": applyCapabilities(event.input); break;
      case "capabilities_error":
        setStatus("모델·명령 목록을 불러오지 못했습니다", "error");
        break;
      case "config_changed":
        applyConfiguration(event.input);
        if (live) setStatus("설정이 적용되었습니다", "ready");
        break;
      case "config_error":
        applyConfiguration(event.input);
        setStatus(event.text || "설정을 변경하지 못했습니다", "error");
        break;
      case "context_usage": updateContextUsage(event.input); break;
      case "conversation_reset":
        resetConversationView();
        updateContextUsage({ usedTokens: 0, contextWindow: currentContextWindow });
        break;
      case "compact_status": renderCompactStatus(event); break;
      case "compact_boundary": renderCompactBoundary(event); break;
      case "system_output":
        sealToolGroup();
        makeSystemOutput(event);
        break;
      case "user":
        if (compactMarker && !compactMarker.classList.contains("running")) compactMarker = null;
        sealToolGroup();
        setBusy(true); setStatus("Claude가 작업 중…", "busy");
        if (live) startWorkingTimer();
        quietCommandInFlight = isQuietSlashCommand(event.text);
        if (quietCommandInFlight) pendingLocalCommand = /^\/([^\s]+)/.exec(String(event.text || "").trim())?.[1] || "";
        if (!isQuietSlashCommand(event.text)) append(makeTurn("user", event.text || "", event.attachments), true);
        break;
      case "assistant":
        sealToolGroup();
        setBusy(true); setStatus("Claude가 응답하는 중…", "busy");
        append(makeTurn("assistant", event.text || ""));
        break;
      case "assistant_stream_start":
        sealToolGroup();
        setBusy(true); setStatus("Claude가 응답하는 중…", "busy");
        startAssistantStream(event.streamId);
        break;
      case "assistant_delta":
        setBusy(true); setStatus("Claude가 응답하는 중…", "busy");
        appendAssistantDelta(event.streamId, event.text);
        break;
      case "assistant_stream_end": endAssistantStream(event.streamId, !live); break;
      case "thinking":
        setBusy(true); setStatus("Claude가 생각하는 중…", "busy");
        break;
      case "thinking_stream_start":
        setBusy(true); setStatus("Claude가 생각하는 중…", "busy");
        break;
      case "thinking_delta":
        setBusy(true); setStatus("Claude가 생각하는 중…", "busy");
        break;
      case "thinking_stream_end": break;
      case "tool":
        setBusy(true);
        addGroupedActivity(event);
        break;
      case "tool_result": finishActivity(event); break;
      case "tool_progress": updateToolProgress(event); break;
      case "tool_summary": applyToolSummary(event); break;
      case "task_started": startTask(event); break;
      case "task_progress": updateTask(event); break;
      case "task_finished": finishTask(event); break;
      case "permission_denied": denyTool(event); break;
      case "permission":
        sealToolGroup();
        setBusy(true); setStatus("사용자 확인 대기", "waiting");
        append(makePermission(event), true);
        break;
      case "permission_resolved": resolvePermission(event.requestId || "", event.text === "allow"); break;
      case "interrupting": setBusy(false); setStatus("응답 중지 요청됨"); break;
      case "result":
        finalizeRunningTools(false);
        pendingLocalCommand = "";
        setBusy(false);
        if (live) finishWorkingTimer(!event.isError && !quietCommandInFlight);
        quietCommandInFlight = false;
        setStatus(event.isError ? "요청 실패" : "대화 준비됨", event.isError ? "error" : "ready");
        if (event.isError) append(makeError("요청 실패", event.text || "알 수 없는 오류"), true);
        break;
      case "error":
        finalizeRunningTools(false);
        pendingLocalCommand = "";
        quietCommandInFlight = false;
        setBusy(false); setStatus("Claude SDK 오류", "error");
        if (live) finishWorkingTimer(false);
        append(makeError("Claude SDK 오류", event.text || "알 수 없는 오류"), true);
        break;
      case "stopped":
        finalizeRunningTools(true);
        pendingLocalCommand = "";
        quietCommandInFlight = false;
        setBusy(false);
        setStatus("Claude SDK 연결 종료");
        break;
    }
    if (!live) nearBottom = true;
  }

  function loadSession(message) {
    roomId = message.roomId || "";
    defaultResolvedModel = "";
    currentModel = normalizeModel(message.model);
    currentEffort = typeof message.effort === "string" && message.effort ? message.effort : "high";
    currentPermissionMode = typeof message.permissionMode === "string" ? message.permissionMode : "acceptEdits";
    vibeMode = message.vibeMode !== false;
    currentContextTokens = 0;
    currentContextWindow = 1_000_000;
    commands = [];
    models = [];
    clearAttachments();
    resetWorkingTimer();
    closeMenus();
    updateControlLabels();
    updateContextUsage();
    updateVibeMode();
    messages.replaceChildren();
    tools.clear();
    tasks.clear();
    toolGroups.clear();
    currentToolGroup = null;
    compactMarker = null;
    pendingLocalCommand = "";
    quietCommandInFlight = false;
    assistantStreams.clear();
    thinkingStreams.clear();
    setBusy(false);
    setStatus("Claude SDK를 준비하는 중…");
    const events = message.events || [];
    for (const event of events) renderEvent(event, false);
    setBusy(message.busy === true);
    if (message.busy) startWorkingTimer();
    if (message.waiting) setStatus("사용자 확인 대기", "waiting");
    else if (message.busy) setStatus("Claude가 작업 중…", "busy");
    else {
      const last = events[events.length - 1];
      const failed = last?.type === "error" || (last?.type === "result" && last?.isError);
      if (last?.type === "starting") setStatus("Claude에 연결하는 중…", "busy");
      else if (last?.type === "ready") setStatus("SDK 브리지 준비됨");
      else if (message.alive && !failed) setStatus("대화 준비됨", "ready");
    }
    updateEmpty();
    requestAnimationFrame(() => {
      conversation.scrollTop = conversation.scrollHeight;
      updateScrollBoundary();
    });
    post({ type: "refreshCapabilities" });
  }

  function applyTheme(message) {
    const root = document.documentElement;
    const values = {
      "--bg": message.bg, "--panel": message.panel, "--panel-soft": message.panelSoft,
      "--line": message.line, "--text": message.text, "--muted": message.muted,
      "--primary": message.primary, "--primary-soft": message.primarySoft,
      "--danger": message.danger, "--success": message.success,
      "--code-bg": message.codeBg, "--code-panel": message.codePanel,
      "--code-border": message.codeBorder, "--code-text": message.codeText,
      "--code-muted": message.codeMuted, "--code-label-bg": message.codeLabelBg,
      "--code-label-border": message.codeLabelBorder, "--code-label-text": message.codeLabelText,
      "--syntax-keyword": message.syntaxKeyword, "--syntax-string": message.syntaxString,
      "--syntax-number": message.syntaxNumber, "--syntax-function": message.syntaxFunction,
      "--syntax-type": message.syntaxType
    };
    for (const [name, value] of Object.entries(values)) if (value) root.style.setProperty(name, value);
    statusPalette = statusPalettes[message.theme] || statusPalettes.dark;
    updateControlLabels();
    updateVibeMode();
    root.dataset.dark = String(message.dark === true);
    root.style.colorScheme = message.dark ? "dark" : "light";
  }

  function insertText(text) {
    const spacer = prompt.value && !/\s$/.test(prompt.value) ? " " : "";
    prompt.value += spacer + (text || "");
    resizePrompt();
    prompt.focus();
  }

  function send() {
    const text = prompt.value.trim();
    if (!text && attachments.length === 0) return;
    const attached = attachments.map(item => ({
      kind: item.kind, name: item.name, mediaType: item.mediaType || "", data: item.data || "",
      preview: item.preview || "", path: item.path || "", size: item.size || 0,
      width: item.width || 0, height: item.height || 0,
    }));
    closeMenus();
    prompt.value = "";
    clearAttachments();
    resizePrompt();
    setBusy(true);
    startWorkingTimer();
    post({ type: "send", text, attachments: attached });
  }

  function resizePrompt() {
    prompt.style.height = "auto";
    prompt.style.height = `${Math.min(prompt.scrollHeight, 147)}px`;
    prompt.style.overflowY = prompt.scrollHeight > 147 ? "auto" : "hidden";
    updateAction();
  }

  action.addEventListener("click", () => busy ? post({ type: "stop" }) : send());
  modelControl.addEventListener("click", () => openControlMenu("model"));
  effortControl.addEventListener("click", () => openControlMenu("effort"));
  permissionControl.addEventListener("click", () => openControlMenu("permission"));
  vibeControl.addEventListener("click", () => {
    vibeMode = !vibeMode;
    closeMenus();
    updateVibeMode();
    post({ type: "setVibeMode", enabled: vibeMode });
    prompt.focus();
  });
  attachControl.addEventListener("click", () => post({ type: "pickAttachments" }));
  controlMenu.addEventListener("click", event => {
    const button = event.target.closest(".menu-item[data-value]");
    if (button) selectControl(button.dataset.value || "");
  });
  commandMenu.addEventListener("pointerdown", event => event.preventDefault());
  commandMenu.addEventListener("click", event => {
    const button = event.target.closest(".menu-item[data-index]");
    if (button) selectCommand(Number(button.dataset.index));
  });
  prompt.addEventListener("input", () => { resizePrompt(); commandSelection = 0; updateCommandMenu(); });
  prompt.addEventListener("paste", event => {
    const images = [...(event.clipboardData?.items || [])]
      .filter(item => item.kind === "file" && item.type.startsWith("image/"))
      .map(item => item.getAsFile()).filter(Boolean);
    if (!images.length) return;
    event.preventDefault();
    void addImageFiles(images);
  });
  prompt.addEventListener("keyup", event => {
    if (!["ArrowUp", "ArrowDown", "Enter", "Tab", "Escape"].includes(event.key)) updateCommandMenu();
  });
  prompt.addEventListener("keydown", event => {
    if (!commandMenu.hidden && visibleCommands.length) {
      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        const direction = event.key === "ArrowDown" ? 1 : -1;
        commandSelection = (commandSelection + direction + visibleCommands.length) % visibleCommands.length;
        updateCommandMenu();
        return;
      }
      if (event.key === "Enter" || event.key === "Tab") {
        event.preventDefault();
        selectCommand(commandSelection);
        return;
      }
      if (event.key === "Escape") {
        event.preventDefault();
        commandMenu.hidden = true;
        return;
      }
    }
    if (event.key === "Escape" && !controlMenu.hidden) { event.preventDefault(); closeMenus(); prompt.focus(); return; }
    if (event.key === "Escape" && busy) { event.preventDefault(); post({ type: "stop" }); return; }
    if (event.key === "Enter" && !event.shiftKey && !event.isComposing) { event.preventDefault(); send(); }
  });
  prompt.addEventListener("focus", () => {
    if (openControl) closeMenus();
    post({ type: "interact" });
    updateCommandMenu();
  });
  conversation.addEventListener("scroll", () => {
    nearBottom = conversation.scrollHeight - conversation.scrollTop - conversation.clientHeight < 120;
  }, { passive: true });
  new ResizeObserver(updateScrollBoundary).observe(conversation);
  window.addEventListener("resize", updateScrollBoundary, { passive: true });
  messages.addEventListener("click", event => {
    const button = event.target.closest(".copy-code");
    if (!button) return;
    const text = button.closest(".code-block")?.querySelector("code")?.textContent || "";
    post({ type: "copy", text });
    button.textContent = "복사됨";
    setTimeout(() => button.textContent = "복사", 1200);
  });
  document.addEventListener("pointerdown", event => {
    if (!commandMenu.hidden && !commandMenu.contains(event.target)) commandMenu.hidden = true;
    if (!controlMenu.hidden && !controlMenu.contains(event.target)
        && !modelControl.contains(event.target) && !effortControl.contains(event.target)
        && !permissionControl.contains(event.target)) closeMenus();
  });
  document.addEventListener("keydown", event => {
    if (event.key !== "Escape" || !openControl) return;
    event.preventDefault();
    closeMenus();
    prompt.focus();
  });
  document.addEventListener("dragenter", event => {
    if (![...(event.dataTransfer?.types || [])].includes("Files")) return;
    event.preventDefault();
    dragDepth += 1;
    dropOverlay.hidden = false;
  });
  document.addEventListener("dragover", event => {
    if (![...(event.dataTransfer?.types || [])].includes("Files")) return;
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = "copy";
  });
  document.addEventListener("dragleave", event => {
    if (!dragDepth) return;
    dragDepth = Math.max(0, dragDepth - 1);
    if (!dragDepth) dropOverlay.hidden = true;
  });
  document.addEventListener("drop", event => {
    const files = [...(event.dataTransfer?.files || [])];
    if (!files.length) return;
    event.preventDefault();
    dragDepth = 0;
    dropOverlay.hidden = true;
    const images = files.filter(file => imageTypes.has(String(file.type || "").toLowerCase()));
    if (images.length !== files.length) showAttachmentError("일반 파일은 첨부 버튼으로 추가해 주세요.");
    if (images.length) void addImageFiles(images);
  });

  bridge?.addEventListener("message", ({ data }) => {
    switch (data?.type) {
      case "session": loadSession(data); break;
      case "event": if (data.roomId === roomId) renderEvent(data.event); break;
      case "setTheme": applyTheme(data); break;
      case "focus": prompt.focus(); break;
      case "insertText": insertText(data.text); break;
      case "addAttachments": void addHostAttachments(data.items); break;
      case "attachmentError": showAttachmentError(data.message); break;
      case "sendFailed": insertText(data.text); void addHostAttachments(data.attachments); setBusy(false); finishWorkingTimer(false); showAttachmentError(data.message || "요청 전송 실패"); break;
      case "hostError": setBusy(false); finishWorkingTimer(false); showAttachmentError(data.message || "화면 오류"); break;
    }
  });

  resizePrompt();
  updateEmpty();
  updateControlLabels();
  post({ type: "pageReady" });
})();
