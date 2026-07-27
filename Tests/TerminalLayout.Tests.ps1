$ErrorActionPreference = 'Stop'

$terminalHtml = Join-Path $PSScriptRoot '..\Resources\Terminal\web\terminal.html'
$source = Get-Content -LiteralPath $terminalHtml -Raw
$style = [regex]::Match($source, '(?s)<style>(.*?)</style>').Groups[1].Value
if (-not $style) { throw 'terminal.html style block was not found.' }

$browser = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $browser) { throw 'Chrome or Microsoft Edge was not found.' }

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("DevezCode-TerminalLayout-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    $fixturePath = Join-Path $tempDir 'fixture.html'
    @"
<!doctype html>
<style>$style</style>
<div id="target" class="term-container agent-devezvibe active"></div>
<script>
const style = getComputedStyle(document.getElementById('target'));
document.body.dataset.right = style.paddingRight;
document.body.dataset.bottom = style.paddingBottom;
</script>
"@ | Set-Content -LiteralPath $fixturePath -Encoding utf8

    $profile = Join-Path $tempDir 'edge-profile'
    $fixtureUri = ([uri]$fixturePath).AbsoluteUri
    $dumpPath = Join-Path $tempDir 'dump.txt'
    $errorPath = Join-Path $tempDir 'browser-error.txt'
    $process = Start-Process -FilePath $browser -WindowStyle Hidden -Wait -PassThru `
        -ArgumentList @('--headless=new', '--disable-gpu', '--no-first-run', "--user-data-dir=$profile", '--dump-dom', $fixtureUri) `
        -RedirectStandardOutput $dumpPath -RedirectStandardError $errorPath
    if ($process.ExitCode -ne 0) { throw "Browser layout fixture failed with exit code $($process.ExitCode)." }
    $dump = Get-Content -LiteralPath $dumpPath -Raw

    $right = [regex]::Match($dump, 'data-right="([^"]+)"').Groups[1].Value
    $bottom = [regex]::Match($dump, 'data-bottom="([^"]+)"').Groups[1].Value
    if (-not $right -or -not $bottom) {
        throw "Browser fixture did not expose computed padding. DOM: $($dump.Substring(0, [Math]::Min(500, $dump.Length)))"
    }

    # 우측 여백은 dvz 가 마지막 셀을 스스로 보호한다 — 호스트가 한 번 더 빼면 컴포저 경계가
    # 본문보다 일찍 끝나므로 0 이어야 한다. 하단은 기본 6px + 6px 유지.
    if ($right -ne '0px') {
        throw "Devez Vibe right padding must stay 0px (dvz protects its own last cell); actual: '$right'."
    }
    if ($bottom -ne '12px') {
        throw "Devez Vibe bottom padding must preserve the base 6px plus 6px; actual: '$bottom'."
    }

    Write-Output 'PASS: Devez Vibe terminal adds no right padding and keeps 6px additional bottom spacing.'
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
