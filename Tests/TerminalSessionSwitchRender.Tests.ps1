$ErrorActionPreference = 'Stop'

$terminalHtml = Join-Path $PSScriptRoot '..\Resources\Terminal\web\terminal.html'
$source = Get-Content -LiteralPath $terminalHtml -Raw

function Assert-Match([string]$text, [string]$pattern, [string]$message) {
    if ($text -notmatch $pattern) { throw $message }
}

function Assert-Order([string]$text, [string[]]$needles, [string]$message) {
    $offset = 0
    foreach ($needle in $needles) {
        $index = $text.IndexOf($needle, $offset, [StringComparison]::Ordinal)
        if ($index -lt 0) { throw $message }
        $offset = $index + $needle.Length
    }
}

$helper = [regex]::Match(
    $source,
    '(?s)function refreshShownTerminal\(entry, roomId\) \{(.*?)\r?\n  \}\r?\n\r?\n  function show'
).Groups[1].Value
if (-not $helper) { throw 'The session-return repaint helper was not found.' }

Assert-Match $helper 'entry\._showPaintGen\s*=\s*gen' `
    'Rapid switching must invalidate stale repaint callbacks with a generation.'
Assert-Match $helper 'entry\._showPaintGen\s*!==\s*gen[\s\S]*?terms\[roomId\]\s*!==\s*entry[\s\S]*?activeRoomId\s*!==\s*roomId[\s\S]*?classList\.contains\(''active''\)' `
    'Deferred repaint callbacks must validate the generation, room, entry, and active element.'
Assert-Match $helper 'term\.refresh\(0,\s*Math\.max\(0,\s*entry\.term\.rows\s*-\s*1\)\)' `
    'Session return must repaint every terminal row.'
Assert-Match $helper 'requestAnimationFrame\(function \(\) \{[\s\S]*?if \(!repaint\(\)\) return;[\s\S]*?requestAnimationFrame\(repaint\)' `
    'Session return must repaint through two visible animation frames.'
if ($helper -match '\.fit\(|\.resize\(|\.scroll|\.focus\(|\.write\(|post\(') {
    throw 'The repaint helper must not change terminal size, scroll, focus, input, or PTY state.'
}

$showStart = $source.IndexOf('function show(roomId, agent, fontSizeOverride, reemit)', [StringComparison]::Ordinal)
$showEnd = if ($showStart -ge 0) {
    $source.IndexOf('function resyncViewportScroll(roomId)', $showStart, [StringComparison]::Ordinal)
} else { -1 }
if ($showStart -lt 0 -or $showEnd -le $showStart) { throw 'The terminal show function was not found.' }
$show = $source.Substring($showStart, $showEnd - $showStart)

Assert-Order $show @(
    "classList.toggle('active', id === roomId)",
    'activeRoomId = roomId',
    'entry.fit.fit()',
    'refreshShownTerminal(entry, roomId)',
    'entry.term.focus()'
) 'Activation, fit, repaint, and focus must stay in that order.'
Assert-Match $show 'if \(!isNew && plainShow\) refreshShownTerminal\(entry, roomId\);' `
    'Only existing sessions on the plain-show path should receive this repaint.'

$helperFunction = $source.Substring(
    $source.IndexOf('function refreshShownTerminal(entry, roomId)', [StringComparison]::Ordinal),
    $showStart - $source.IndexOf('function refreshShownTerminal(entry, roomId)', [StringComparison]::Ordinal)
).Trim()

$browser = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $browser) { throw 'Chrome or Microsoft Edge was not found.' }

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("DevezCode-SessionSwitchRender-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    $fixturePath = Join-Path $tempDir 'fixture.html'
    @"
<!doctype html>
<div id="a" class="active"></div><div id="b"></div>
<script>
const pendingFrames = [];
window.requestAnimationFrame = function (callback) { pendingFrames.push(callback); return pendingFrames.length; };
function makeEntry(id) {
  return {
    el: document.getElementById(id),
    term: {
      rows: 42,
      refreshCount: 0,
      refresh: function (first, last) {
        this.refreshCount++;
        this.lastRange = first + ':' + last;
      }
    }
  };
}
const a = makeEntry('a');
const b = makeEntry('b');
const terms = { a: a, b: b };
let activeRoomId = 'a';
$helperFunction

refreshShownTerminal(a, 'a');
a.el.classList.remove('active'); b.el.classList.add('active'); activeRoomId = 'b';
refreshShownTerminal(b, 'b');
b.el.classList.remove('active'); a.el.classList.add('active'); activeRoomId = 'a';
refreshShownTerminal(a, 'a');
while (pendingFrames.length) {
  const frame = pendingFrames.splice(0, pendingFrames.length);
  frame.forEach(function (callback) { callback(0); });
}
document.body.dataset.result = a.term.refreshCount + '|' + b.term.refreshCount + '|' + a.term.lastRange;
</script>
"@ | Set-Content -LiteralPath $fixturePath -Encoding utf8

    $profile = Join-Path $tempDir 'edge-profile'
    $fixtureUri = ([uri]$fixturePath).AbsoluteUri
    $dumpPath = Join-Path $tempDir 'dump.txt'
    $errorPath = Join-Path $tempDir 'browser-error.txt'
    $process = Start-Process -FilePath $browser -WindowStyle Hidden -Wait -PassThru `
        -ArgumentList @('--headless=new', '--disable-gpu', '--no-first-run', "--user-data-dir=$profile", '--dump-dom', $fixtureUri) `
        -RedirectStandardOutput $dumpPath -RedirectStandardError $errorPath
    if ($process.ExitCode -ne 0) { throw "Browser repaint fixture failed with exit code $($process.ExitCode)." }
    $dump = Get-Content -LiteralPath $dumpPath -Raw
    $result = [regex]::Match($dump, 'data-result="([^"]+)"').Groups[1].Value
    if ($result -ne '4|1|0:41') {
        throw "Stale session-switch callbacks were not cancelled correctly; actual: '$result'."
    }
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'PASS: rapid session switching repaints only the active terminal and cancels stale callbacks.'
