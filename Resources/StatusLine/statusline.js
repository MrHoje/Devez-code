// DEVEZCODE-STATUSLINE v10 — Devez/DevezCode 공용 관리 스크립트. 번들과 내용이 다르면 앱이 동기화한다.
// 두 앱(devez, DevezCode)이 같은 사용자 ~/.claude 를 공유하므로 이 파일도 공유·동일 내용으로 관리된다.
const _fs = require("fs"), _path = require("path"), _os = require("os");
const _cfgFile = _path.join(_os.homedir(), ".claude", "statusline-config.json");
// 테마 추종은 devez/DevezCode 안에서 뜬 세션만. 각 앱은 자기 세션에만 각자의 ROOM_ID 를
// 자식 트리에 상속시킨다(외부 Windows Terminal 등에는 없음). 외부 세션은 앱 테마와 무관하게
// 항상 dark 로 고정해, 라이트 테마가 외부 터미널 statusline 색을 바꾸지 않게 한다.
//   DevezCode 안: DEVEZCODE_ROOM_ID + %AppData%\DevezCode\theme.txt
//   devez     안: DEVEZ_ROOM_ID     + %AppData%\Devez\theme.txt
// 둘 다 "dark"|"soft"|"minimal" 값을 쓰므로 이후 로직은 앱 구분 없이 THEME 하나로 처리된다.
let THEME = "dark";
const _themeSrc = process.env.DEVEZCODE_ROOM_ID ? "DevezCode"
                 : process.env.DEVEZ_ROOM_ID     ? "Devez"
                 : null;
if (_themeSrc) {
  try { if (process.env.APPDATA) THEME = (_fs.readFileSync(_path.join(process.env.APPDATA, _themeSrc, "theme.txt"), "utf8").trim() || "dark"); } catch (e) {}
}
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
    // 장기 실행 Claude daemon 은 계정 전환 뒤에도 이전 계정의 live rate_limits 를
    // 내보낼 수 있다. DevezCode가 현재 자격증명으로 성공 수집한 API 값이 10분 이내면
    // 이를 기준값으로 사용한다. UsageApiService가 자격증명 변경/401 때 파일을 무효화한다.
    let rl = j.rate_limits || {};
    if (process.env.APPDATA) {
      try {
        const _apiPath = _path.join(process.env.APPDATA, "DevezCode", "claude", "api-usage.json");
        const _apiRl = JSON.parse(_fs.readFileSync(_apiPath, "utf8"));
        const _fetchedAt = Date.parse(_apiRl.fetched_at || "");
        const _apiAgeMs = Number.isFinite(_fetchedAt)
          ? Date.now() - _fetchedAt
          : Date.now() - _fs.statSync(_apiPath).mtimeMs;
        if (_apiAgeMs >= 0 && _apiAgeMs <= 10 * 60 * 1000
            && (_apiRl.five_hour || _apiRl.seven_day)) {
          rl = _apiRl;
        }
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

    // 테마별 색감. statusline 은 터미널 배경 위에 그려진다.
    //   dark:    따뜻한 다크(#2A2620) 쪽 16% 블렌드 — 어두운 배경, 밝은 글자 유지.
    //   soft/minimal/gray/softpink(라이트): 회색으로 블렌드하면 채도가 죽어 색 구분이 뭉개진다.
    //     대신 명도만 곱연산으로 낮춰(hue/채도 유지) 밝은 배경에서 쨍하게 구분되게 한다.
    //     액센트(ACC)는 살짝만, 일반 텍스트(TXT)는 강하게 낮춰 검정에 가깝게(soft 가 가장 진함).
    const _light = THEME === "soft" || THEME === "minimal" || THEME === "gray" || THEME === "softpink";
    const VF = THEME === "soft" ? 0.80 : THEME === "softpink" ? 0.78 : 0.82;
    const INK = THEME === "soft" ? [22, 18, 12]
      : THEME === "gray" ? [31, 41, 55]
      : THEME === "softpink" ? [59, 41, 49]
      : [15, 20, 34]; // 일반 텍스트 잉크색(진한 검정 계열)
    const TINT = [42, 38, 32], K = 0.16;
    const _cl = v => (v < 0 ? 0 : v > 255 ? 255 : Math.round(v));
    const _esc = (r, g, b) => "\x1b[38;2;" + _cl(r) + ";" + _cl(g) + ";" + _cl(b) + "m";
    // 액센트/의미색.
    //   dark: 따뜻한 다크(#2A2620) 쪽 16% 블렌드 — 밝은 글자 유지.
    //   라이트: 옅은 팔레트를 곱연산만 하면 칙칙해 색 구분이 죽는다. 채도를 최대로 끌어올린 뒤
    //     (min 을 0 으로 당겨 hue 유지) 명도(VF)만 낮춰 밝은 배경에서 쨍하게 구분되게 한다.
    // m: 라이트 전용 추가 명도 배수(색별 미세조정, 기본 1). 다크는 무시.
    const fg = (r, g, b, m = 1) => {
      if (!_light)
        return _esc(r + (TINT[0] - r) * K, g + (TINT[1] - g) * K, b + (TINT[2] - b) * K);
      const mn = Math.min(r, g, b), mx = Math.max(r, g, b);
      if (mx === mn) return _esc(r * 0.34, g * 0.34, b * 0.34); // 무채색은 그냥 어둡게
      const s = 255 / (mx - mn), v = VF * m;
      return _esc((r - mn) * s * v, (g - mn) * s * v, (b - mn) * s * v);
    };
    // 일반 텍스트(브랜치 외 model 폴백/구분자/토큰 등): 라이트=진한 잉크색 고정, 다크는 fg 와 동일.
    const fgText = (r, g, b) => _light ? _esc(INK[0], INK[1], INK[2]) : fg(r, g, b);
    // 채도 최대화를 거치지 않고 지정한 색을 그대로 쓴다. fg() 의 saturate-max 는 인디고 같은
    // (R·G·B 가 가까운) 색을 순수 파랑으로 뭉개버려 다크의 톤과 너무 멀어지는 경우에 사용.
    const fgFixed = (r, g, b) => _esc(r, g, b);
    // gray/softpink은 상태줄도 앱 팔레트의 진한 의미색을 직접 사용한다.
    const STATUS = THEME === "gray"
      ? { info: [50, 106, 165], accent: [118, 85, 143], warning: [161, 98, 7], success: [21, 128, 61], danger: [194, 65, 62], xhigh: [101, 73, 123] }
      : THEME === "softpink"
        ? { info: [50, 106, 159], accent: [132, 88, 143], warning: [154, 101, 11], success: [37, 114, 60], danger: [194, 65, 62], xhigh: [112, 70, 126] }
        : null;
    const semantic = (key, r, g, b, m = 1) => STATUS?.[key] ? fgFixed(...STATUS[key]) : fg(r, g, b, m);

    const R = "\x1b[0m";
    const MAIN   = fgText(229, 231, 235);
    const SEP    = fgText(147, 164, 184);
    const SOFT   = fgText(203, 213, 225);
    const HAIKU  = semantic("info", 0, 255, 255);
    const OPUS   = semantic("danger", 248, 113, 113);
    // 라이트에서는 claude TUI 의 AUTO MODE(warning 토큰) 색과 동일하게 고정(테마별로 다름).
    // 다크는 기존 골드 유지.
    const SONNET = STATUS ? semantic("warning", 202, 138, 4)
      : _light
      ? (THEME === "soft" ? fgFixed(201, 124, 26) : fgFixed(202, 138, 4))
      : fg(250, 204, 21);
    const FABLE  = semantic("accent", 232, 121, 249);
    const CTX    = semantic("success", 52, 211, 153, 0.68); // 라이트에서 더 어두운 녹색
    const TIME   = semantic("info", 96, 165, 250);
    const WEEK   = semantic("accent", 167, 139, 250);
    const TOK    = fgText(226, 232, 240);
    const E_LOW  = semantic("warning", 220, 172, 18);
    const E_MED  = semantic("success", 63, 157, 99, 0.80);   // 라이트에서 더 어두운 녹색
    // high/xhigh: 다크는 원래 색 유지, 라이트만 hue 분리해 구분되게.
    // E_HIGH 는 다크의 옅은 인디고(177,185,249)와 계열을 맞추려 고정 인디고色 사용
    // (saturate-max 를 거치면 인디고가 순수 파랑으로 뭉개져 다크 톤과 너무 멀어짐).
    const E_HIGH = STATUS ? semantic("accent", 67, 56, 202) : _light ? fgFixed(67, 56, 202) : fg(177, 185, 249);
    const E_XH   = STATUS ? semantic("xhigh", 192, 100, 255) : _light ? fg(192, 100, 255) : fg(175, 135, 255);
    const E_MAX  = semantic("danger", 248, 113, 113);
    // 브랜치(첫 세그먼트): 라이트에서만 시안 쪽으로 틀어 더 하늘색 느낌(다크는 기존 파랑 유지).
    const BRANCH = STATUS ? semantic("info", 56, 189, 248) : _light ? fg(56, 189, 248) : fg(147, 197, 253);
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
    if (CFG.branch && gitBranch) parts.push(BRANCH + gitBranch + R);
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
