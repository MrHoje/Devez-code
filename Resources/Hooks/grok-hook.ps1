# [개발 중단] Grok 통합 보류 — UI 비노출. 재개 시 사용.
# DevezCode grok hook (UserPromptSubmit / Stop / SessionStart / SessionEnd)
# ~/.grok/hooks/devezcode-room-tracker.json 에 등록. stdin 으로 JSON (hookEventName, sessionId, …).
# 세션 추적은 $env:DEVEZCODE_ROOM_ID (앱이 ConPTY/배치 env 로 주입) 로 식별.
#   busy\<room>.txt       = running|idle
#   lastmsg\<room>.txt    = 마지막 user prompt (1줄 요약, 200자)
#   sessions\<room>.txt   = grok session_id (재오픈 시 --resume 용)

$ErrorActionPreference = 'SilentlyContinue'

try {
    $reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
    $raw = $reader.ReadToEnd()
    $reader.Dispose()
    if (-not $raw) { exit 0 }
    $j = $raw | ConvertFrom-Json
} catch { exit 0 }

$room = $env:DEVEZCODE_ROOM_ID
if (-not $room) { exit 0 }

$roomSafe = $room -replace '[^\w\-]', ''
$base = Join-Path $env:APPDATA 'DevezCode\grok'

# Grok: hookEventName (camel) / codex 호환 hook_event_name
$event = $j.hookEventName
if (-not $event) { $event = $j.hook_event_name }
if (-not $event) { $event = $env:GROK_HOOK_EVENT }
$event = [string]$event

# 정규화: session_start / SessionStart / user_prompt_submit 등 → 비교용
$eventKey = ($event -replace '[_\-]', '').ToLowerInvariant()

function Write-Busy([string]$status) {
    $busyDir = Join-Path $base 'busy'
    New-Item -ItemType Directory -Force -Path $busyDir | Out-Null
    Set-Content -LiteralPath (Join-Path $busyDir ($roomSafe + '.txt')) -Value $status -Encoding Ascii -Force
}

function Write-SessionId {
    $sid = $j.sessionId
    if (-not $sid) { $sid = $j.session_id }
    if (-not $sid) { $sid = $env:GROK_SESSION_ID }
    if (-not $sid) { return }
    $sDir = Join-Path $base 'sessions'
    New-Item -ItemType Directory -Force -Path $sDir | Out-Null
    Set-Content -LiteralPath (Join-Path $sDir ($roomSafe + '.txt')) -Value ([string]$sid).Trim() -Encoding Ascii -Force
}

switch -Regex ($eventKey) {
    '^(userpromptsubmit|beforesubmitprompt)$' {
        Write-Busy 'running'
        Write-SessionId

        $prompt = $j.prompt
        if (-not $prompt) { $prompt = $j.userPrompt }
        if (-not $prompt) { $prompt = $j.message }
        if ($prompt -is [System.Array]) {
            $parts = @()
            foreach ($p in $prompt) {
                if ($p -is [string]) { $parts += $p }
                elseif ($p.text) { $parts += [string]$p.text }
                elseif ($p.content) { $parts += [string]$p.content }
            }
            $prompt = ($parts -join ' ')
        }
        if ($prompt) {
            $prompt = ([string]$prompt -replace '\s+', ' ').Trim()
            if ($prompt.Length -gt 200) { $prompt = $prompt.Substring(0, 200) }
            $mDir = Join-Path $base 'lastmsg'
            New-Item -ItemType Directory -Force -Path $mDir | Out-Null
            Set-Content -LiteralPath (Join-Path $mDir ($roomSafe + '.txt')) -Value $prompt -Encoding UTF8 -Force
        }
    }
    '^(stop|stopfailure)$' {
        Write-Busy 'idle'
        Write-SessionId
    }
    '^(sessionstart)$' {
        Write-SessionId
    }
    '^(sessionend)$' {
        Write-Busy 'idle'
        Write-SessionId
    }
}

exit 0
