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

    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'codex'
    $codexChildSid = [Guid]::NewGuid().ToString()
    $childRollout = Join-Path $rolloutDir ("rollout-$codexChildSid.jsonl")
    '{"payload":{"id":"' + $codexChildSid + '"}}' | Set-Content -LiteralPath $childRollout -Encoding UTF8
    $codexChildStart = @{
        hook_event_name = 'SessionStart'
        source = 'startup'
        session_id = $codexChildSid
        transcript_path = $childRollout
    } | ConvertTo-Json -Compress
    Invoke-PowerShellHook 'Resources\Hooks\codex-hook.ps1' $codexStart
    Invoke-PowerShellHook 'Resources\Hooks\codex-hook.ps1' $codexChildStart
    Assert-Value (Join-Path $testAppData "DevezCode\codex\sessions\$room.txt") $codexSid 'codex root fence'
    $codexChildPrompt = @{
        hook_event_name = 'UserPromptSubmit'
        session_id = $codexChildSid
        transcript_path = $childRollout
        turn_id = 'turn-child'
        prompt = 'nested codex prompt'
    } | ConvertTo-Json -Compress
    Invoke-PowerShellHook 'Resources\Hooks\codex-hook.ps1' $codexChildPrompt
    Assert-Missing (Join-Path $testAppData "DevezCode\codex\lastmsg\$room.txt") 'codex child prompt'

    $kimiStart = '{"hook_event_name":"SessionStart","session_id":"session_root"}'
    $kimiPrompt = '{"hook_event_name":"UserPromptSubmit","session_id":"session_root","prompt":[{"type":"text","text":"child prompt"}]}'
    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'claude'
    Invoke-PowerShellHook 'Resources\Hooks\kimi-hook.ps1' $kimiStart
    Invoke-PowerShellHook 'Resources\Hooks\kimi-hook.ps1' $kimiPrompt
    Assert-Missing (Join-Path $testAppData "DevezCode\kimi\busy\$room.txt") 'kimi hook'

    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'kimi'
    Invoke-PowerShellHook 'Resources\Hooks\kimi-hook.ps1' $kimiStart
    Invoke-PowerShellHook 'Resources\Hooks\kimi-hook.ps1' (
        '{"hook_event_name":"SessionStart","session_id":"session_child"}')
    Assert-Value (Join-Path $testAppData "DevezCode\kimi\sessions\$room.txt") 'session_root' 'kimi root fence'
    Invoke-PowerShellHook 'Resources\Hooks\kimi-hook.ps1' (
        '{"hook_event_name":"UserPromptSubmit","session_id":"session_child","prompt":[{"type":"text","text":"nested kimi prompt"}]}')
    Assert-Missing (Join-Path $testAppData "DevezCode\kimi\lastmsg\$room.txt") 'kimi child prompt'
    Invoke-PowerShellHook 'Resources\Hooks\kimi-hook.ps1' $kimiPrompt
    $kimiWaiting = Join-Path $testAppData "DevezCode\kimi\waiting\$room.txt"
    Remove-Item -LiteralPath $kimiWaiting -Force -ErrorAction SilentlyContinue
    Invoke-CmdHook 'Resources\Hooks\kimi-state-hook.cmd' 'waiting-on' (
        '{"session_id":"session_child"}')
    Assert-Missing $kimiWaiting 'kimi child state hook'
    Invoke-CmdHook 'Resources\Hooks\kimi-state-hook.cmd' 'waiting-on' (
        '{"session_id":"session_root"}')
    Assert-Value $kimiWaiting 'waiting' 'kimi root state hook'

    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'claude'
    $grokSid = [Guid]::NewGuid().ToString()
    Invoke-PowerShellHook 'Resources\Hooks\grok-hook.ps1' (
        '{"hookEventName":"SessionStart","sessionId":"' + $grokSid + '"}')
    Assert-Missing (Join-Path $testAppData "DevezCode\grok\sessions\$room.txt") 'grok hook'

    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'grok'
    $grokChildSid = [Guid]::NewGuid().ToString()
    Invoke-PowerShellHook 'Resources\Hooks\grok-hook.ps1' (
        '{"hookEventName":"SessionStart","sessionId":"' + $grokSid + '"}')
    Invoke-PowerShellHook 'Resources\Hooks\grok-hook.ps1' (
        '{"hookEventName":"SessionStart","sessionId":"' + $grokChildSid + '"}')
    Assert-Value (Join-Path $testAppData "DevezCode\grok\sessions\$room.txt") `
        $grokSid 'grok root fence'

    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'claude'
    $env:ANTIGRAVITY_CONVERSATION_ID = [Guid]::NewGuid().ToString()
    Invoke-CmdHook 'Resources\Hooks\antigravity-hook.cmd' 'SessionStart' '{}'
    Assert-Missing (Join-Path $testAppData "DevezCode\antigravity\sessions\$room.txt") 'antigravity hook'

    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'antigravity'
    $antigravityRootSid = [Guid]::NewGuid().ToString()
    $env:ANTIGRAVITY_CONVERSATION_ID = $antigravityRootSid
    Invoke-CmdHook 'Resources\Hooks\antigravity-hook.cmd' 'SessionStart' '{}'
    $env:ANTIGRAVITY_CONVERSATION_ID = [Guid]::NewGuid().ToString()
    Invoke-CmdHook 'Resources\Hooks\antigravity-hook.cmd' 'SessionStart' '{}'
    Assert-Value (Join-Path $testAppData "DevezCode\antigravity\sessions\$room.txt") `
        $antigravityRootSid 'antigravity root fence'

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

    Reset-State
    $env:DEVEZCODE_TRACKING_AGENT = 'opencode'
    $pluginModule = Join-Path $testRoot 'opencode-room-tracker.mjs'
    @'
import { createRequire } from "node:module";
const require = createRequire(import.meta.url);
'@ + $plugin | Set-Content -LiteralPath $pluginModule -Encoding UTF8
    $nodeRunner = Join-Path $testRoot 'opencode-owner-runner.mjs'
    @'
import fs from "node:fs";
import { pathToFileURL } from "node:url";
const [pluginPath, sessionId, holdMs, readyPath] = process.argv.slice(2);
const module = await import(pathToFileURL(pluginPath).href + `?pid=${process.pid}`);
const hooks = await module.DevezCodeRoomTracker({});
if (hooks.event) {
  await hooks.event({ event: {
    type: "session.created",
    properties: { info: { id: sessionId } }
  }});
}
if (readyPath) fs.writeFileSync(readyPath, "ready");
await new Promise(resolve => setTimeout(resolve, Number(holdMs || 0)));
'@ | Set-Content -LiteralPath $nodeRunner -Encoding UTF8

    $readyFile = Join-Path $testRoot 'opencode-root.ready'
    $nodeErrorFile = Join-Path $testRoot 'opencode-root.error'
    $nodeArgs = '"{0}" "{1}" open_root 3000 "{2}"' -f $nodeRunner, $pluginModule, $readyFile
    $rootNode = Start-Process -FilePath 'node.exe' -ArgumentList $nodeArgs `
        -PassThru -WindowStyle Hidden -RedirectStandardError $nodeErrorFile
    if ($null -eq $rootNode) {
        $failures.Add('opencode root test process did not start')
    } else {
        $readyDeadline = [DateTime]::UtcNow.AddSeconds(5)
        while (-not (Test-Path -LiteralPath $readyFile) -and
               [DateTime]::UtcNow -lt $readyDeadline -and
               -not $rootNode.HasExited) {
            Start-Sleep -Milliseconds 50
        }
        if (-not (Test-Path -LiteralPath $readyFile)) {
            $nodeError = if (Test-Path -LiteralPath $nodeErrorFile) {
                (Get-Content -LiteralPath $nodeErrorFile -Raw).Trim()
            } else { '' }
            $failures.Add("opencode root test process was not ready: $nodeError")
        } else {
            & node.exe $nodeRunner $pluginModule 'open_child' '0' | Out-Null
            Assert-Value (Join-Path $testAppData "DevezCode\opencode\sessions\$room.txt") `
                'open_root' 'opencode process owner fence'
        }
        if (-not $rootNode.WaitForExit(5000)) {
            $failures.Add('opencode root test process did not exit')
        }
        $rootNode.Dispose()
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
