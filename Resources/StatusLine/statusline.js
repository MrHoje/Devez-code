// DEVEZCODE-STATUSLINE v4 — DevezCode 관리 스크립트. 번들과 내용이 다르면 앱이 동기화한다.
const _fs = require("fs"), _path = require("path"), _os = require("os");
const _cfgFile = _path.join(_os.homedir(), ".claude", "statusline-config.json");
// 직전 정상 출력 캐시. parse 실패/빈 결과로 빈 줄을 뱉으면 세션 진입 시 statusline 이
// 잠깐 비어 보이므로, 그런 렌더에서는 마지막 정상 줄을 대신 출력해 깜빡임을 막는다.
const _lastFile = _path.join(_os.tmpdir(), "claude-statusline-last.txt");
const _emitFallback = () => {
  try { process.stdout.write(_fs.readFileSync(_lastFile, "utf8")); } catch (e) {}
  process.exit(0);
};
const _cfgDefaults = { branch: true, model: true, eff: true, ctx: true, time: true, week: true, tokens: true };
let CFG = Object.assign({}, _cfgDefaults);
try { CFG = Object.assign(CFG, JSON.parse(_fs.readFileSync(_cfgFile, "utf8"))); } catch (e) {}

let d = "";
process.stdin.on("data", c => d += c);
process.stdin.on("end", () => {
  try {
    const j = JSON.parse(d);
    const id = (j.model && j.model.id) || "";
    const cw = j.context_window || {};
    const total = Math.round((cw.context_window_size || 0) / 1000);
    const used  = Math.round((cw.total_input_tokens || 0) / 1000);
    const pct   = Math.round(cw.used_percentage || 0);
    const worktree = j.workspace && j.workspace.git_worktree;
    let gitBranch = "";
    try {
      const fs = require("fs");
      const path = require("path");
      const findGitBranch = (startDir) => {
        let dir = startDir;
        for (let i = 0; i < 10; i++) {
          try {
            const gitPath = path.join(dir, ".git");
            let headContent = "";
            try {
              headContent = fs.readFileSync(path.join(gitPath, "HEAD"), "utf8").trim();
            } catch (e1) {
              try {
                const gitStat = fs.statSync(gitPath);
                if (gitStat.isFile()) {
                  const gitfileContent = fs.readFileSync(gitPath, "utf8").trim();
                  const m = gitfileContent.match(/gitdir:\s*(.+)/);
                  if (m) {
                    const realGitDir = m[1].trim();
                    const headPath = realGitDir.startsWith("/") || realGitDir[1] === ":" ? path.join(realGitDir, "HEAD") : path.join(dir, realGitDir, "HEAD");
                    headContent = fs.readFileSync(headPath, "utf8").trim();
                  }
                }
              } catch (e2) {}
            }
            if (headContent) {
              const match = headContent.match(/refs\/heads\/(.+)/);
              return match ? match[1] : headContent.slice(0, 7);
            }
          } catch (e) {}
          const parent = path.dirname(dir);
          if (parent === dir) break;
          dir = parent;
        }
        return "";
      };
      const startDir = j.cwd || (j.workspace && j.workspace.current_dir);
      if (startDir) gitBranch = findGitBranch(startDir);
    } catch (e) {}
    const rl   = j.rate_limits || {};
    const rl5h = rl.five_hour && rl.five_hour.used_percentage != null ? Math.round(rl.five_hour.used_percentage) : null;
    const rl7d = rl.seven_day && rl.seven_day.used_percentage != null ? Math.round(rl.seven_day.used_percentage) : null;
    const cu   = cw.current_usage || {};
    const effortLevel = j.effort && j.effort.level;

    const counterFile = require("path").join(require("os").tmpdir(), "claude-token-counter-" + (j.session_id || "default") + ".json");
    let counter = { total: 0, lastInput: -1, lastOutput: -1, startTime: 0, lastActivity: 0, displayTotal: 0, displayTime: 0 };
    try { counter = JSON.parse(require("fs").readFileSync(counterFile, "utf8")); } catch (e) {}
    const inputTokens = cu.input_tokens || 0;
    const outputTokens = cu.output_tokens || 0;
    if (inputTokens !== counter.lastInput || outputTokens !== counter.lastOutput) {
      counter.total += inputTokens + outputTokens;
      counter.lastInput = inputTokens;
      counter.lastOutput = outputTokens;
      counter.lastActivity = Date.now();
      try { require("fs").writeFileSync(counterFile, JSON.stringify(counter)); } catch (e) {}
    }
    const totalTokens = counter.displayTotal || 0;
    const elapsedSec  = counter.displayTime  || 0;

    // DevezCode 기본 색감: 베이스 색을 따뜻한 다크(#2A2620) 쪽으로 16% 블렌드.
    // 다크 배경에서도 충분히 또렷하고, 라이트 테마에서도 가독성을 챙기는 톤다운 값.
    const TINT = [42, 38, 32], K = 0.16;
    const fg = (r, g, b) =>
      "\x1b[38;2;" + Math.round(r + (TINT[0] - r) * K)
            + ";" + Math.round(g + (TINT[1] - g) * K)
            + ";" + Math.round(b + (TINT[2] - b) * K) + "m";

    const R = "\x1b[0m";
    const MAIN   = fg(229, 231, 235);
    const SEP    = fg(147, 164, 184);
    const SOFT   = fg(203, 213, 225);
    const HAIKU  = fg(0, 255, 255);
    const OPUS   = fg(248, 113, 113);
    const SONNET = fg(250, 204, 21);
    const CTX    = fg(52, 211, 153);
    const TIME   = fg(96, 165, 250);
    const WEEK   = fg(167, 139, 250);
    const TOK    = fg(226, 232, 240);
    const E_LOW  = fg(220, 172, 18);
    const E_MED  = fg(63, 157, 99);
    const E_HIGH = fg(177, 185, 249);
    const E_XH   = fg(175, 135, 255);
    const E_MAX  = fg(248, 113, 113);
    const PIPE   = SEP + " | " + R;

    let mc, ml;
    if (id.includes("haiku"))       { mc = HAIKU;  ml = "Haiku 4.5"; }
    else if (id.includes("sonnet")) { mc = SONNET; const sv = id.match(/sonnet-(\d+)-(\d+)/); ml = sv ? "Sonnet " + sv[1] + "." + sv[2] : "Sonnet"; }
    else if (id.includes("opus"))   { mc = OPUS;   const ov = id.match(/opus-(\d+)-(\d+)/); const ovStr = ov ? ov[1] + "." + ov[2] : ""; ml = (total >= 900 ? "Opus " + ovStr + " 1M" : "Opus " + ovStr).trim(); }
    else                            { mc = MAIN;   ml = id || "unknown"; }

    const parts = [];
    if (CFG.branch && gitBranch) parts.push(fg(147, 197, 253) + gitBranch + R);
    if (CFG.model) parts.push(mc + ml + R);
    if (CFG.eff && effortLevel) {
      let ec = SOFT;
      if (effortLevel === "low")    ec = E_LOW;
      else if (effortLevel === "medium") ec = E_MED;
      else if (effortLevel === "high")   ec = E_HIGH;
      else if (effortLevel === "xhigh")  ec = E_XH;
      else if (effortLevel === "max")    ec = E_MAX;
      parts.push(ec + "eff: " + effortLevel + R);
    }
    if (CFG.ctx && total) parts.push(CTX + "ctx: " + used + "k/" + total + "k (" + pct + "%)" + R);
    if (CFG.time && rl5h !== null) {
      const resetsAt = rl.five_hour.resets_at;
      let label = "5h";
      if (resetsAt) {
        const diffMs = resetsAt * 1000 - Date.now();
        if (diffMs > 0) {
          const m = Math.ceil(diffMs / 60000);
          label = Math.floor(m / 60) + "h" + (m % 60 < 10 ? "0" : "") + (m % 60) + "m";
        }
      }
      parts.push(TIME + label + ": " + rl5h + "%" + R);
    }
    if (CFG.week && rl7d !== null) parts.push(WEEK + "week: " + rl7d + "%" + R);
    if (worktree) parts.push(MAIN + worktree + R);
    const stale = counter.processing && counter.lastActivity > 0 && (Date.now() - counter.lastActivity) > 15000;
    if (CFG.tokens && counter.processing && !stale) {
      const dots = Math.floor((Date.now() % 3000) / 1000) + 1;
      parts.push(SOFT + "Working" + ".".repeat(dots) + R);
    } else if (CFG.tokens && totalTokens > 0) {
      const ts = totalTokens >= 1000 ? (totalTokens / 1000).toFixed(1).replace(/\.0$/, "") + "k" : String(totalTokens);
      let disp = "";
      if (elapsedSec > 0) {
        disp = (elapsedSec >= 60 ? Math.floor(elapsedSec / 60) + "m " + (elapsedSec % 60) + "s" : elapsedSec + "s") + " · ";
      }
      parts.push(TOK + disp + ts + " tokens" + R);
    }
    if (parts.length === 0) return _emitFallback(); // 표시할 게 없으면 직전 줄 유지
    const line = " " + parts.join(PIPE) + "\n";
    try { _fs.writeFileSync(_lastFile, line); } catch (e) {}
    process.stdout.write(line);
  } catch (e) { _emitFallback(); }
});
