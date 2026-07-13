# DevezCode antigravity(agy) hook (SessionStart / PreToolUse / PostToolUse / Stop / SessionEnd)
# ~/.gemini/antigravity-cli/hooks.json 에 등록(merge). stdin 으로 JSON (hook_event_name, conversation_id, …).
# 세션 추적은 $env:DEVEZCODE_ROOM_ID (앱이 ConPTY/배치 env 로 주입) 로 식별.
#   busy\<room>.txt     = running|idle
#   sessions\<room>.txt = agy conversation_id (재오픈 시 --conversation 용)
# 주의: agy 에는 UserPromptSubmit 훅이 없어 busy-ON 은 PreToolUse 시점(도구 첫 사용)이다.
#       순수 텍스트 응답은 busy 가 안 켜질 수 있음 — Stop 이 idle 확정만 담당.

$ErrorActionPreference = 'SilentlyContinue'

try {
    $reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
    $raw = $reader.ReadToEnd()
    $reader.Dispose()
    if ($raw) { $j = $raw | ConvertFrom-Json }
} catch { }

$room = $env:DEVEZCODE_ROOM_ID
if (-not $room) { exit 0 }

$roomSafe = $room -replace '[^\w\-]', ''
$base = Join-Path $env:APPDATA 'DevezCode\antigravity'

# hook_event_name (snake) / hookEventName (camel) 모두 대응
$event = $null
if ($j) {
    $event = $j.hook_event_name
    if (-not $event) { $event = $j.hookEventName }
}
if (-not $event) { $event = $env:ANTIGRAVITY_HOOK_EVENT }
$event = [string]$event
$eventKey = ($event -replace '[_\-]', '').ToLowerInvariant()

# truncate 직후 프로세스 종료 시 0바이트 파일이 남는 것 방지 — temp + 원자적 rename (claude Write-State 정합).
function Write-State([string]$path, [string]$value, [string]$encoding = 'UTF8') {
    $dir = Split-Path -Parent $path
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $tmp = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Set-Content -LiteralPath $tmp -Value $value -Encoding $encoding -Force
        Move-Item -LiteralPath $tmp -Destination $path -Force
    } finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    }
}

function Write-Busy([string]$status) {
    $busyDir = Join-Path $base 'busy'
    Write-State (Join-Path $busyDir ($roomSafe + '.txt')) $status 'Ascii'
}

function Write-SessionId {
    $sid = $null
    if ($j) {
        $sid = $j.conversation_id
        if (-not $sid) { $sid = $j.conversationId }
        if (-not $sid) { $sid = $j.session_id }
        if (-not $sid) { $sid = $j.sessionId }
    }
    if (-not $sid) { $sid = $env:ANTIGRAVITY_CONVERSATION_ID }
    if (-not $sid) { return }
    $sDir = Join-Path $base 'sessions'
    Write-State (Join-Path $sDir ($roomSafe + '.txt')) ([string]$sid).Trim() 'Ascii'
}

switch -Regex ($eventKey) {
    '^(sessionstart)$' {
        Write-SessionId
    }
    '^(pretooluse)$' {
        Write-Busy 'running'
        Write-SessionId
    }
    '^(posttooluse|posttoolusefailure)$' {
        # 도구 사이 구간 — 여전히 작업 중으로 유지 (Stop 이 idle 확정).
        Write-Busy 'running'
    }
    '^(stop|stophook|stopfailure)$' {
        Write-Busy 'idle'
        Write-SessionId
    }
    '^(sessionend)$' {
        Write-Busy 'idle'
        Write-SessionId
    }
}

exit 0
