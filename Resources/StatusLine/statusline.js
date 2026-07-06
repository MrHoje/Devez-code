// DEVEZCODE-STATUSLINE v6 — DevezCode 관리 스크립트. 번들과 내용이 다르면 앱이 동기화한다.
const _fs = require("fs"), _path = require("path"), _os = require("os");
const _cfgFile = _path.join(_os.homedir(), ".claude", "statusline-config.json");
// DevezCode 현재 테마("dark"|"soft"|"minimal"). 앱이 %AppData%\DevezCode\theme.txt 에 떨군다.
// statusline 은 별도 node 프로세스라 이 파일로만 테마를 안다. 없으면 dark 로 폴백.
let THEME = "dark";
try { if (process.env.APPDATA) THEME = (_fs.readFileSync(_path.join(process.env.APPDATA, "DevezCode", "theme.txt"), "utf8").trim() || "dark"); } catch (e) {}
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
    // rate_limits: claude 가 넘긴 live 값 우선. 없으면(세션 외/미지원) DevezCode 가 OAuth API 로
    // 3분마다 떨군 폴백 파일을 읽어 5h/주간 표시를 유지한다(같은 모양: used_percentage + resets_at(unix초)).
    let rl = j.rate_limits || {};
    if (!(rl.five_hour && rl.five_hour.used_percentage != null) && process.env.APPDATA) {
      try {
        const _apiRl = JSON.parse(_fs.readFileSync(_path.join(process.env.APPDATA, "DevezCode", "claude", "api-usage.json"), "utf8"));
        if (_apiRl && (_apiRl.five_hour || _apiRl.seven_day)) rl = _apiRl;
      } catch (e) {}
    }
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

    // 테마별 색감. statusline 은 터미널 배경 위에 그려지므로 밝은 테마(soft/minimal)에선
    // 밝은 팔레트를 배경 텍스트색 쪽으로 강하게 블렌드해 어둡게(=가독) 만든다. 의미색(모델/effort
    // 등)의 색상(hue)은 유지되고 명도만 낮아져 라이트 배경에서도 또렷하다.
    //   dark:    따뜻한 다크(#2A2620) 쪽 16% — 어두운 배경, 밝은 글자 유지.
    //   soft:    본문색(#2A2620) 쪽 62% — 크림 배경에서 톤다운.
    //   minimal: 본문색(#0F172A) 쪽 62% — 화이트 배경에서 톤다운.
    const _tints = {
      dark:    { tint: [42, 38, 32],  k: 0.16 },
      soft:    { tint: [42, 38, 32],  k: 0.62 },
      minimal: { tint: [15, 23, 42],  k: 0.62 },
    };
    const _t = _tints[THEME] || _tints.dark;
    const TINT = _t.tint, K = _t.k;
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
    const FABLE  = fg(232, 121, 249);
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

    // 버전 정규식: 메이저(-마이너)? 만 잡고 날짜 접미사(-20250929 등)는 제외.
    // (?!\d) 로 마이너 뒤에 숫자가 더 붙으면(=날짜) 마이너로 인정하지 않음.
    const verRe = (name) => id.match(new RegExp(name + "-(\\d{1,2})(?!\\d{6})(?:-(\\d{1,2})(?!\\d))?"));
    let mc, ml;
    if (id.includes("haiku"))       { mc = HAIKU;  const hv = verRe("haiku");  ml = hv ? "Haiku "  + hv[1] + (hv[2] ? "." + hv[2] : "") : "Haiku"; }
    else if (id.includes("sonnet")) { mc = SONNET; const sv = verRe("sonnet"); ml = sv ? "Sonnet " + sv[1] + (sv[2] ? "." + sv[2] : "") : "Sonnet"; }
    else if (id.includes("opus"))   { mc = OPUS;   const ov = verRe("opus");   const ovStr = ov ? ov[1] + (ov[2] ? "." + ov[2] : "") : ""; ml = (total >= 900 ? "Opus " + ovStr + " 1M" : "Opus " + ovStr).trim(); }
    else if (id.includes("fable"))  { mc = FABLE;  const fv = verRe("fable");  ml = fv ? "Fable "  + fv[1] + (fv[2] ? "." + fv[2] : "") : "Fable"; }
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
