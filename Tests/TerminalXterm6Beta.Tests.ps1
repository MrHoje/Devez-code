$ErrorActionPreference = 'Stop'

$webRoot = Join-Path $PSScriptRoot '..\Resources\Terminal\web'
$browser = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $browser) { throw 'Chrome or Microsoft Edge was not found.' }

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("DevezCode-Xterm6Beta-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    foreach ($name in @('xterm6.min.js', 'addon-fit6.min.js', 'addon-webgl6.min.js', 'xterm.min.css')) {
        Copy-Item -LiteralPath (Join-Path $webRoot $name) -Destination (Join-Path $tempDir $name)
    }

    $fixturePath = Join-Path $tempDir 'fixture.html'
    @'
<!doctype html>
<meta charset="utf-8">
<link rel="stylesheet" href="xterm.min.css">
<style>
  body { margin: 0; background: #111; }
  .term { display: inline-block; width: 640px; height: 280px; }
</style>
<body data-result="pending">
<div id="a" class="term"></div><div id="b" class="term"></div>
<script>
window.addEventListener('error', function (event) {
  document.body.dataset.result = 'load-error|' + String(event.message || event.error || 'unknown')
    + '|' + String(event.filename || 'inline') + ':' + event.lineno + ':' + event.colno;
});
</script>
<script src="xterm6.min.js"></script>
<script src="addon-fit6.min.js"></script>
<script src="addon-webgl6.min.js"></script>
<script>
(async function () {
  let contextLosses = 0;
  try {
    const options = { cols: 64, rows: 16, fontFamily: 'monospace', fontSize: 14, allowProposedApi: true };
    const termA = new Terminal(options), termB = new Terminal(options);
    const fitA = new FitAddon.FitAddon(), fitB = new FitAddon.FitAddon();
    termA.loadAddon(fitA); termB.loadAddon(fitB);
    termA.open(document.getElementById('a')); termB.open(document.getElementById('b'));
    fitA.fit(); fitB.fit();
    const webglA = new WebglAddon.WebglAddon({ preserveDrawingBuffer: true });
    const webglB = new WebglAddon.WebglAddon({ preserveDrawingBuffer: true });
    webglA.onContextLoss(function () { contextLosses++; });
    webglB.onContextLoss(function () { contextLosses++; });
    termA.loadAddon(webglA); termB.loadAddon(webglB);

    const write = (term, data) => new Promise(resolve => term.write(data, resolve));
    const paint = () => new Promise(resolve => {
      let done = false;
      const finish = () => { if (!done) { done = true; resolve(); } };
      setTimeout(finish, 100);
      requestAnimationFrame(() => requestAnimationFrame(finish));
    });
    const content = Array.from({ length: 900 }, (_, i) => String.fromCodePoint(0x400 + i)).join('');
    await write(termA, content);
    await write(termB, 'shared atlas regression\r\n' + content);
    await paint();

    const atlasA = webglA._renderer && webglA._renderer._charAtlas;
    const atlasB = webglB._renderer && webglB._renderer._charAtlas;
    const betaCore = termA._core;
    const mouseCompat = betaCore && betaCore.mouseStateService && betaCore._mouseService
      && typeof betaCore._mouseService._triggerMouseEvent === 'function'
      && betaCore._mouseService._mouseCoordsService
      && typeof betaCore._mouseService._mouseCoordsService.getMouseReportCoords === 'function';
    const before = atlasA && atlasA.pageLayoutVersion;
    webglA.clearTextureAtlas();
    await write(termB, '\x1b[1;1Hcheck');
    const after = atlasA && atlasA.pageLayoutVersion;
    let seen;
    for (let attempt = 0; attempt < 10; attempt++) {
      termB.refresh(0, termB.rows - 1);
      await paint();
      const glyphB = webglB._renderer && webglB._renderer._glyphRenderer
        && webglB._renderer._glyphRenderer.value;
      seen = glyphB && glyphB._lastSeenPageLayoutVersion;
      if (seen === after) break;
    }
    const canvases = document.querySelectorAll('.xterm-screen canvas');
    const ok = atlasA && atlasA === atlasB && mouseCompat && Number.isFinite(before) && after > before
      && (!Number.isFinite(seen) || seen === after) && canvases.length >= 2 && contextLosses === 0;
    document.body.dataset.result = ok
      ? 'ok|' + before + '|' + after + '|' + canvases.length
      : 'fail|' + [!!atlasA, atlasA === atlasB, !!mouseCompat, before, after, seen, canvases.length, contextLosses].join('|');
    termA.dispose(); termB.dispose();
  } catch (error) {
    document.body.dataset.result = 'error|' + String(error && error.stack || error).replace(/[\r\n]+/g, ' ');
  }
})();
</script>
'@ | Set-Content -LiteralPath $fixturePath -Encoding utf8

    $profile = Join-Path $tempDir 'browser-profile'
    $dumpPath = Join-Path $tempDir 'dump.txt'
    $errorPath = Join-Path $tempDir 'browser-error.txt'
    $fixtureUri = ([uri]$fixturePath).AbsoluteUri
    $process = Start-Process -FilePath $browser -WindowStyle Hidden -Wait -PassThru `
        -ArgumentList @(
            '--headless=new', '--no-first-run', '--enable-webgl', '--ignore-gpu-blocklist',
            '--use-angle=swiftshader', '--allow-file-access-from-files', '--disable-background-timer-throttling',
            '--run-all-compositor-stages-before-draw', '--virtual-time-budget=10000', "--user-data-dir=$profile",
            '--dump-dom', $fixtureUri
        ) `
        -RedirectStandardOutput $dumpPath -RedirectStandardError $errorPath
    if ($process.ExitCode -ne 0) { throw "Browser xterm6 beta fixture failed with exit code $($process.ExitCode)." }

    $dump = Get-Content -LiteralPath $dumpPath -Raw
    $result = [regex]::Match($dump, 'data-result="([^"]+)"').Groups[1].Value
    if ($result -notmatch '^ok\|') {
        $browserError = Get-Content -LiteralPath $errorPath -Raw -ErrorAction SilentlyContinue
        $dumpSample = $dump.Substring(0, [Math]::Min(4000, $dump.Length))
        throw "xterm6 beta WebGL smoke test failed: '$result'. $browserError $dumpSample"
    }
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'PASS: xterm6 beta core, fit, and WebGL load together and propagate a shared atlas clear.'
