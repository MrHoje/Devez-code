$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('devezcode-tracking-' + [Guid]::NewGuid().ToString('N'))
$testAppData = Join-Path $testRoot 'appdata'
$testHome = Join-Path $testRoot 'home'
$room = 'room-test'
$failures = [System.Collections.Generic.List[string]]::new()

$saved = @{
    APPDATA = $env:APPDATA
    HOME = $env:HOME
    USERPROFILE = $env:USERPROFILE
    DEVEZCODE_ROOM_ID = $env:DEVEZCODE_ROOM_ID
    DEVEZCODE_TRACKING_AGENT = $env:DEVEZCODE_TRACKING_AGENT
    ANTIGRAVITY_CONVERSATION_ID = $env:ANTIGRAVITY_CONVERSATION_ID
}

function Reset-State {
    $stateRoot = Join-Path $testAppData 'DevezCode'
    if (Test-Path -LiteralPath $stateRoot) {
        Remove-Item -LiteralPath $stateRoot -Recurse -Force
    }
}

function Invoke-PowerShellHook([string]$relativePath, [string]$json) {
    $json | & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot $relativePath) | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("$relativePath exited with $LASTEXITCODE")
    }
}

function Invoke-CmdHook([string]$relativePath, [string]$argument, [string]$json) {
    $json | & cmd.exe /d /c call (Join-Path $repoRoot $relativePath) $argument | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("$relativePath exited with $LASTEXITCODE")
    }
}

function Assert-Missing([string]$path, [string]$name) {
    if (Test-Path -LiteralPath $path) {
        $failures.Add("$name wrote state for a mismatched agent")
    }
}

function Assert-Value([string]$path, [string]$expected, [string]$name) {
    if (-not (Test-Path -LiteralPath $path)) {
        $failures.Add("$name did not create expected state")
        return
    }
    $actual = (Get-Content -LiteralPath $path -Raw).Trim()
    if ($actual -ne $expected) {
        $failures.Add("$name expected '$expected', got '$actual'")
    }
}

try {
    New-Item -ItemType Directory -Force -Path $testAppData, $testHome | Out-Null
    $env:APPDATA = $testAppData
    $env:HOME = $testHome
    $env:USERPROFILE = $testHome
    $env:DEVEZCODE_ROOM_ID = $room

    $codexSid = [Guid]::NewGuid().ToString()
    $codexTurn = 'turn-root'
    $rolloutDir = Join-Path $testHome '.codex\sessions\2026\07\26'
    New-Item -ItemType Directory -Force -Path $rolloutDir | Out-Null
    $rollout = Join-Path $rolloutDir ("rollout-$codexSid.jsonl")
    '{"payload":{"id":"' + $codexSid + '"}}' | Set-Content -LiteralPath $rollout -Encoding UTF8
    $codexStart = @{
        hook_event_name = 'SessionStart'
        session_id = $codexSid
        transcript_path = $rollout
    } | ConvertTo-Json -Compress
    $codexPrompt = @{
        hook_event_name = 'UserPromptSubmit'
        session_id = $codexSid
        transcript_path = $rollout
        turn_id = $codexTurn
        prompt = 'child prompt'
    } | ConvertTo-Json -Compress

    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'claude'
    Invoke-PowerShellHook 'Resources\Hooks\codex-hook.ps1' $codexStart
    Invoke-PowerShellHook 'Resources\Hooks\codex-hook.ps1' $codexPrompt
    Assert-Missing (Join-Path $testAppData "DevezCode\codex\busy\$room.txt") 'codex hook'

    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'codex'
    Invoke-PowerShellHook 'Resources\Hooks\codex-hook.ps1' $codexStart
    Invoke-PowerShellHook 'Resources\Hooks\codex-hook.ps1' $codexPrompt
    Assert-Value (Join-Path $testAppData "DevezCode\codex\busy\$room.txt") 'running' 'codex hook match'

    $kimiStart = '{"hook_event_name":"SessionStart","session_id":"session_root"}'
    $kimiPrompt = '{"hook_event_name":"UserPromptSubmit","session_id":"session_root","prompt":[{"type":"text","text":"child prompt"}]}'
    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'claude'
    Invoke-PowerShellHook 'Resources\Hooks\kimi-hook.ps1' $kimiStart
    Invoke-PowerShellHook 'Resources\Hooks\kimi-hook.ps1' $kimiPrompt
    Assert-Missing (Join-Path $testAppData "DevezCode\kimi\busy\$room.txt") 'kimi hook'

    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'claude'
    $grokSid = [Guid]::NewGuid().ToString()
    Invoke-PowerShellHook 'Resources\Hooks\grok-hook.ps1' (
        '{"hookEventName":"SessionStart","sessionId":"' + $grokSid + '"}')
    Assert-Missing (Join-Path $testAppData "DevezCode\grok\sessions\$room.txt") 'grok hook'

    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'claude'
    $env:ANTIGRAVITY_CONVERSATION_ID = [Guid]::NewGuid().ToString()
    Invoke-CmdHook 'Resources\Hooks\antigravity-hook.cmd' 'SessionStart' '{}'
    Assert-Missing (Join-Path $testAppData "DevezCode\antigravity\sessions\$room.txt") 'antigravity hook'

    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'claude'
    $activeDir = Join-Path $testAppData 'DevezCode\codex\active'
    New-Item -ItemType Directory -Force -Path $activeDir | Out-Null
    Set-Content -LiteralPath (Join-Path $activeDir "$room.txt") -Value $codexTurn -Encoding Ascii
    Invoke-CmdHook 'Resources\Hooks\codex-state-hook.cmd' 'waiting' (
        '{"turn_id":"' + $codexTurn + '"}')
    Assert-Missing (Join-Path $testAppData "DevezCode\codex\waiting\$room.txt") 'codex state hook'

    $plugin = Get-Content -LiteralPath (Join-Path $repoRoot 'Resources\Plugins\opencode-room-tracker.js') -Raw
    if ($plugin -notmatch 'DEVEZCODE_TRACKING_AGENT' -or $plugin -notmatch '"opencode"') {
        $failures.Add('opencode plugin has no tracking-agent guard')
    }
}
finally {
    foreach ($name in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
    }
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Error $failure -ErrorAction Continue }
    exit 1
}

Write-Output 'PASS: tracking hook ownership'
