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

function Write-Waiting([string]$status) {
    $waitingDir = Join-Path $base 'waiting'
    Write-State (Join-Path $waitingDir ($roomSafe + '.txt')) $status 'Ascii'
}

function Get-SessionId {
    $sid = $j.sessionId
    if (-not $sid) { $sid = $j.session_id }
    if (-not $sid) { $sid = $env:GROK_SESSION_ID }
    if (-not $sid) { return $null }
    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParse(([string]$sid).Trim(), [ref]$parsed)) { return $null }
    return $parsed.ToString()
}

function Read-SessionId([string]$path) {
    try {
        if (-not (Test-Path -LiteralPath $path)) { return $null }
        $value = (Get-Content -LiteralPath $path -Raw).Trim()
        $parsed = [Guid]::Empty
        if ([Guid]::TryParse($value, [ref]$parsed)) { return $parsed.ToString() }
    } catch { }
    return $null
}

# Only the room's root Grok session may own sessions\<room>.txt.
# A Grok process launched by a tool inherits DEVEZCODE_ROOM_ID, so blindly accepting every
# SessionStart can replace the resumable parent with a child/internal session. A different ID is
# accepted only after SessionEnd for the currently tracked root (normal /new or /clear transition).
function Write-SessionId([bool]$allowEndedTransition = $false) {
    $sid = Get-SessionId
    if (-not $sid) { return }
    $sDir = Join-Path $base 'sessions'
    $path = Join-Path $sDir ($roomSafe + '.txt')
    $prevPath = Join-Path $sDir ($roomSafe + '.prev.txt')
    $rootPath = Join-Path $sDir ($roomSafe + '.root.txt')
    $endedPath = Join-Path $sDir ($roomSafe + '.ended.txt')
    $current = Read-SessionId $path

    if ($current -and $current -ne $sid) {
        $ended = if ($allowEndedTransition) { Read-SessionId $endedPath } else { $null }
        if (-not $ended -or $ended -ne $current) { return }
        Write-State $prevPath $current 'Ascii'
    }

    # Root marker first. If process dies between writes, app sees marker/current mismatch and
    # falls back to saved/previous instead of trusting a half-committed transition.
    Write-State $rootPath $sid 'Ascii'
    Write-State $path $sid 'Ascii'
    if (Test-Path -LiteralPath $endedPath) { Remove-Item -LiteralPath $endedPath -Force }
}

function Mark-SessionEnded {
    $sid = Get-SessionId
    if (-not $sid) { return }
    $sDir = Join-Path $base 'sessions'
    $path = Join-Path $sDir ($roomSafe + '.txt')
    $current = Read-SessionId $path
    if ($current -and $current -eq $sid) {
        Write-State (Join-Path $sDir ($roomSafe + '.ended.txt')) $current 'Ascii'
    }
}

function Test-CurrentRoomSession {
    $sid = Get-SessionId
    if (-not $sid) { return $false }
    $sDir = Join-Path $base 'sessions'
    $current = Read-SessionId (Join-Path $sDir ($roomSafe + '.txt'))
    $root = Read-SessionId (Join-Path $sDir ($roomSafe + '.root.txt'))
    return $current -and $root -and $current -eq $sid -and $root -eq $sid
}

function Get-CompletedPath {
    return Join-Path (Join-Path $base 'completed') ($roomSafe + '.flag')
}

function Mark-Completed {
    Write-State (Get-CompletedPath) 'done' 'Ascii'
}

function Clear-Completed {
    $path = Get-CompletedPath
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}

function Test-Completed {
    return Test-Path -LiteralPath (Get-CompletedPath)
}

switch -Regex ($eventKey) {
    '^(userpromptsubmit|beforesubmitprompt)$' {
        Write-SessionId
        if (-not (Test-CurrentRoomSession)) { break }
        Clear-Completed
        Write-Busy 'running'
        Write-Waiting 'idle'

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
            $prompt = [string]$prompt
            # Grok가 사용자 질문 앞에 붙이는 내부 시스템 리마인더는 헤더/완료 기록에 남기지 않는다.
            $prompt = $prompt -replace '(?is)<system-remi(?:n)?der\b[^>]*>.*?</system-remi(?:n)?der\s*>', ' '
            $prompt = $prompt -replace '(?is)<system-remi(?:n)?der\b[^>]*>.*$', ' '
            $prompt = $prompt -replace '(?is)</?system-remi(?:n)?der\b[^>]*>', ' '
            # Grok 0.2.93은 일부 훅 payload의 prompt를
            # <user_query> ... </user_query>… 로 감싼다. 헤더에는 실제 질문만 표시.
            if ($prompt -match '(?is)^\s*<user_query>\s*(.*?)\s*</user_query>\s*(?:…|\.\.\.)?\s*$') {
                $prompt = $Matches[1]
            }
            $prompt = ($prompt -replace '\s+', ' ').Trim()
            if ($prompt.Length -gt 200) { $prompt = $prompt.Substring(0, 200) }
            $mDir = Join-Path $base 'lastmsg'
            Write-State (Join-Path $mDir ($roomSafe + '.txt')) $prompt 'UTF8'
        }
    }
    '^(notification)$' {
        if (-not (Test-CurrentRoomSession)) { break }
        $noticeType = $j.notificationType
        if (-not $noticeType) { $noticeType = $j.notification_type }
        if (-not $noticeType) { $noticeType = $j.type }
        $noticeMessage = [string]$j.message
        if (-not $noticeMessage) { $noticeMessage = [string]$env:GROK_MESSAGE }
        $noticeLevel = [string]$j.level
        $typeKey = ([string]$noticeType -replace '[_\-]', '').ToLowerInvariant()
        $messageKey = $noticeMessage.Trim().ToLowerInvariant()
        $levelKey = $noticeLevel.Trim().ToLowerInvariant()

        # Grok sends this informational notification before every tool even when
        # permissions are bypassed/auto-approved. It is progress, not human input.
        if (($typeKey -eq 'permissionprompt' -or $messageKey -eq 'tool permission requested') -and
            (-not $levelKey -or $levelKey -eq 'info')) {
            exit 0
        }

        # Interrupt/return-to-prompt fallback: some Grok paths emit no Stop but do
        # announce the input prompt again.
        if ($messageKey -match '(type your message|enter send|shift-tab normal)') {
            Mark-Completed
            Write-Busy 'idle'
            Write-Waiting 'idle'
            exit 0
        }

        $permissionNotice = (
            $typeKey -eq 'permissionprompt' -or
            $messageKey -match '(permission|approval|approve|user input|needs your|requires your|feedback|clarif|question)'
        )
        if ($permissionNotice -and -not (Test-Completed)) {
            Write-Waiting 'waiting'
        }
    }
    '^(pretooluse)$' {
        # 조기 Stop/Notification idle 후에도 도구가 이어지면 스피너를 다시 켠다.
        # (completed 플래그만 막으면 장시간 멀티루프 턴에서 스피너가 중간에 영구 소등된다.)
        if (-not (Test-CurrentRoomSession)) { break }
        Clear-Completed
        Write-Busy 'running'
        $toolName = $j.toolName
        if (-not $toolName) { $toolName = $j.tool_name }
        if (-not $toolName) { $toolName = $j.name }
        $toolKey = ([string]$toolName -replace '[^a-zA-Z0-9]', '').ToLowerInvariant()
        # ask_user_question is auto-allowed and therefore arrives as PreToolUse,
        # not PermissionRequest/Notification. This is the real human-input boundary.
        if ($toolKey -eq 'askuserquestion') { Write-Waiting 'waiting' }
        else { Write-Waiting 'idle' }
    }
    '^(posttooluse|posttoolusefailure)$' {
        if (-not (Test-CurrentRoomSession)) { break }
        # 장시간 도구 도중 조기 idle 이 와도 Post 시점에 복구. 진짜 종료 후 late Post 는
        # 앱측 events.jsonl 폴러(turn_ended)가 다시 idle 로 수렴한다.
        Clear-Completed
        Write-Busy 'running'
        Write-Waiting 'idle'
    }
    '^(stop|stopfailure)$' {
        if (-not (Test-CurrentRoomSession)) { break }
        Mark-Completed
        Write-Busy 'idle'
        Write-Waiting 'idle'
        Write-SessionId
    }
    '^(sessionstart)$' {
        Write-SessionId $true
    }
    '^(sessionend)$' {
        if (-not (Test-CurrentRoomSession)) { break }
        Mark-Completed
        Write-Busy 'idle'
        Write-Waiting 'idle'
        Mark-SessionEnded
    }
}

exit 0
