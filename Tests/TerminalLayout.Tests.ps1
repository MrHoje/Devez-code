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
<div id="target" class="term-container agent-devezcli active"></div>
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

    if ($right -ne '10px') {
        throw "DevezCLI right safe area must be 10px; actual: '$right'."
    }
    if ($bottom -ne '14px') {
        throw "DevezCLI bottom padding must preserve the base 6px plus 8px; actual: '$bottom'."
    }

    Write-Output 'PASS: DevezCLI terminal keeps 10px right safe area and 8px additional bottom spacing.'
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
