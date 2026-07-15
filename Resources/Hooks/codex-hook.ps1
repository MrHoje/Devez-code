# DevezCode codex hook (UserPromptSubmit / Stop / SessionStart)
# codex 훅 시스템: ~/.codex/hooks.json 에 등록. stdin 으로 JSON 받음.
# 세션 추적은 $env:DEVEZCODE_ROOM_ID (앱이 ConPTY env 로 주입) 로 식별.
# 동작: Claude 의 busy-hook.ps1 / room-hook.ps1 패턴 그대로 — 방별 파일 4종.
#   busy\<room>.txt       = running|idle
#   waiting\<room>.txt    = waiting|idle
#   lastmsg\<room>.txt    = 마지막 user prompt (1줄 요약, 200자 제한)
#   sessions\<room>.txt   = codex session_id (재오픈 시 --resume 용)

$ErrorActionPreference = 'SilentlyContinue'

# codex 는 stdin 으로 UTF-8 JSON 을 보낸다. Windows PowerShell 5.1 의 [Console]::In 은 콘솔 입력
# 코드페이지(OEM/ANSI)로 디코딩해 한글 등 비ASCII 가 깨진다 → StreamReader 로 UTF-8 명시 디코딩.
try {
    $reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
    $raw = $reader.ReadToEnd()
    $reader.Dispose()
    if (-not $raw) { exit 0 }
    $j = $raw | ConvertFrom-Json
} catch { exit 0 }

$room = $env:DEVEZCODE_ROOM_ID
if (-not $room) { exit 0 }

# 파일명 안전: 비-워드문자 제거 (codex 의 -replace 와 동일 규칙)
$roomSafe = $room -replace '[^\w\-]', ''

$base = Join-Path $env:APPDATA 'DevezCode\codex'
$event = $j.hook_event_name

# 원자적 상태 기록 — claude busy-hook 의 Write-State 와 동일 패턴.
# 직접 Set-Content 는 파일을 0바이트로 truncate 후 값을 쓰므로, truncate 직후 훅 프로세스가
# (codex 훅 타임아웃/종료 레이스 등으로) 중단되면 0바이트 빈 파일이 남아 idle 전이를 영영 놓친다
# (스피너 stuck-ON). temp 에 완전히 쓴 뒤 Move-Item 으로 원자적 rename 하면 중간 0바이트 상태가 없다.
function Write-State($path, $value, $encoding = 'Ascii') {
    try {
        $tmp = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        Set-Content -LiteralPath $tmp -Value $value -Encoding $encoding -Force
        Move-Item -LiteralPath $tmp -Destination $path -Force
    } catch { try { Set-Content -LiteralPath $path -Value $value -Encoding $encoding -Force } catch { } }
}

# SessionStart 는 사용자 대화뿐 아니라 Codex 내부 Memory Writing Agent 같은 별도 thread 에서도
# 발화한다. 내부 thread 도 부모 프로세스의 DEVEZCODE_ROOM_ID 를 상속하므로 session_id 를 그대로
# 쓰면 방의 실제 대화 ID가 resume 불가능한 내부 ID로 오염된다. codex resume 이 읽는 영속
# transcript(~/.codex/sessions)와 실제로 연결된 ID만 방 추적값으로 인정한다.
function Test-ResumableSession($sid, $transcriptPath) {
    $parsed = [Guid]::Empty
    if (-not $sid -or -not [Guid]::TryParse([string]$sid, [ref]$parsed)) { return $false }

    $sessionRoot = Join-Path $HOME '.codex\sessions'
    if (-not (Test-Path -LiteralPath $sessionRoot)) { return $false }

    try {
        if ($transcriptPath -and (Test-Path -LiteralPath $transcriptPath)) {
            $rootFull = [IO.Path]::GetFullPath($sessionRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            $pathFull = [IO.Path]::GetFullPath([string]$transcriptPath)
            if ($pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
                $first = Get-Content -LiteralPath $pathFull -TotalCount 1 -Encoding UTF8
                if ($first) {
                    $meta = $first | ConvertFrom-Json
                    $metaId = if ($meta.payload.id) { $meta.payload.id } else { $meta.payload.session_id }
                    if ($metaId -and [string]::Equals([string]$metaId, [string]$sid,
                            [StringComparison]::OrdinalIgnoreCase)) { return $true }
                }
            }
        }
    } catch { }

    # transcript_path 가 없는 구버전/엣지에서도 영속 rollout 파일이 있으면 허용.
    try {
        $match = Get-ChildItem -LiteralPath $sessionRoot -Recurse -File -Filter ('*' + $sid + '*.jsonl') |
            Select-Object -First 1
        return $null -ne $match
    } catch { return $false }
}

# UserPromptSubmit/Stop 은 현재 DevezCode 방에서 SessionStart 로 확정한 사용자 세션만 허용한다.
# transcript 가 존재하는 다른 사용자 thread 가 같은 DEVEZCODE_ROOM_ID를 상속해도 방 상태를 못 덮는다.
function Test-CurrentRoomSession($sid, $transcriptPath) {
    if (-not (Test-ResumableSession $sid $transcriptPath)) { return $false }
    try {
        $trackedPath = Join-Path (Join-Path $base 'sessions') ($roomSafe + '.txt')
        if (-not (Test-Path -LiteralPath $trackedPath)) { return $false }
        $tracked = (Get-Content -LiteralPath $trackedPath -Raw -Encoding UTF8).Trim()
        return [string]::Equals([string]$tracked, [string]$sid, [StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}

function Write-Waiting([string]$status) {
    $waitingDir = Join-Path $base 'waiting'
    New-Item -ItemType Directory -Force -Path $waitingDir | Out-Null
    Write-State (Join-Path $waitingDir ($roomSafe + '.txt')) $status
}

# Codex turn-scoped hooks carry turn_id. session_id alone is insufficient because
# subagent hooks intentionally report the parent session_id. Keep the active root
# turn separately so late/internal tool events cannot re-arm a completed room.
function Get-TurnId {
    $turnId = [string]$j.turn_id
    if (-not $turnId -or $turnId -notmatch '^[A-Za-z0-9_-]{1,128}$') { return $null }
    return $turnId
}

function Get-ActiveTurnPath {
    $activeDir = Join-Path $base 'active'
    New-Item -ItemType Directory -Force -Path $activeDir | Out-Null
    return Join-Path $activeDir ($roomSafe + '.txt')
}

function Read-ActiveTurn {
    try {
        $path = Get-ActiveTurnPath
        if (-not (Test-Path -LiteralPath $path)) { return $null }
        $value = (Get-Content -LiteralPath $path -Raw -Encoding UTF8).Trim()
        if ($value -match '^[A-Za-z0-9_-]{1,128}$') { return $value }
    } catch { }
    return $null
}

function Set-ActiveTurn([string]$turnId) {
    if (-not $turnId) { return }
    Write-State (Get-ActiveTurnPath) $turnId
}

function Test-ActiveTurn([string]$turnId) {
    if (-not $turnId) { return $false }
    $active = Read-ActiveTurn
    return $active -and [string]::Equals($active, $turnId, [StringComparison]::Ordinal)
}

function Clear-ActiveTurn([string]$turnId) {
    try {
        $path = Get-ActiveTurnPath
        if ((Test-ActiveTurn $turnId) -and (Test-Path -LiteralPath $path)) {
            Remove-Item -LiteralPath $path -Force
        }
    } catch { }
}

# Codex launches matching hooks as separate processes. Serialize the active-turn marker and
# busy/waiting transition per room so an old Stop cannot pass validation, pause, then delete a
# newer UserPromptSubmit marker (TOCTOU). AbandonedMutexException means this process owns it.
$turnMutex = $null
$turnMutexHeld = $false
function Enter-TurnLock {
    try {
        $script:turnMutex = New-Object System.Threading.Mutex($false, ('Local\DevezCode.CodexTurn.' + $roomSafe))
        try {
            $script:turnMutexHeld = $script:turnMutex.WaitOne(10000)
        } catch [System.Threading.AbandonedMutexException] {
            $script:turnMutexHeld = $true
        }
        return $script:turnMutexHeld
    } catch {
        $script:turnMutexHeld = $false
        return $false
    }
}

function Exit-TurnLock {
    try {
        if ($script:turnMutexHeld -and $script:turnMutex) { $script:turnMutex.ReleaseMutex() }
    } catch { }
    try { if ($script:turnMutex) { $script:turnMutex.Dispose() } } catch { }
    $script:turnMutexHeld = $false
    $script:turnMutex = $null
}

function Write-Busy([string]$status) {
    $busyDir = Join-Path $base 'busy'
    New-Item -ItemType Directory -Force -Path $busyDir | Out-Null
    Write-State (Join-Path $busyDir ($roomSafe + '.txt')) $status
}

switch ($event) {
    'UserPromptSubmit' {
        # Memory Writing Agent 같은 내부 thread 도 부모 프로세스의 DEVEZCODE_ROOM_ID 를 상속하고
        # UserPromptSubmit 을 발생시킨다. 내부 프롬프트를 사용자가 보낸 메시지로 오인해 헤더
        # 타이틀과 busy 상태를 덮지 않도록, 실제 영속 transcript 에 연결된 사용자 세션만 처리한다.
        if (-not (Test-CurrentRoomSession $j.session_id $j.transcript_path)) { break }

        # turn_id 없는 turn-scoped payload는 fail-closed. 상태 추적을 생략하는 편이
        # 내부/오래된 이벤트로 영구 스피너를 만드는 것보다 안전하다.
        $turnId = Get-TurnId
        if (-not $turnId) { break }
        if (-not (Enter-TurnLock)) { break }
        try {
            Set-ActiveTurn $turnId

            # 1) busy=running
            Write-Busy 'running'
            Write-Waiting 'idle'
        } finally {
            Exit-TurnLock
        }

        # 2) 마지막 프롬프트 (1줄, 200자)
        $prompt = $j.prompt
        if ($prompt) {
            $prompt = ($prompt -replace '\s+', ' ').Trim()
            if ($prompt.Length -gt 200) { $prompt = $prompt.Substring(0, 200) }
            $mDir = Join-Path $base 'lastmsg'
            New-Item -ItemType Directory -Force -Path $mDir | Out-Null
            Write-State (Join-Path $mDir ($roomSafe + '.txt')) $prompt 'UTF8'
        }
    }
    'Stop' {
        # 내부 thread 종료가 실제 사용자 turn 의 busy 상태를 조기 해제하지 않게 한다.
        if (-not (Test-CurrentRoomSession $j.session_id $j.transcript_path)) { break }

        # 오래된 turn/internal subagent Stop이 새 사용자 턴을 끄지 못하게 한다.
        # active marker가 아예 없으면 구버전 훅으로 시작된 in-flight turn의 Stop으로 보고
        # idle은 허용한다. 새 훅으로 시작한 turn은 항상 marker가 있어 mismatch가 차단된다.
        $turnId = Get-TurnId
        if (-not $turnId) { break }
        if (-not (Enter-TurnLock)) { break }
        try {
            $activeTurn = Read-ActiveTurn
            if ($activeTurn -and -not [string]::Equals($activeTurn, $turnId, [StringComparison]::Ordinal)) { break }

            # marker 확인/삭제와 idle 기록은 한 임계구역이다. 다음 Prompt가 먼저 잡으면
            # mismatch로 이 Stop이 폐기되고, Stop이 먼저 잡으면 다음 Prompt가 마지막 running을 쓴다.
            if ($activeTurn) { Clear-ActiveTurn $turnId }
            Write-Busy 'idle'
            Write-Waiting 'idle'
        } finally {
            Exit-TurnLock
        }
    }
    'SessionStart' {
        # codex session_id 기록 — 다음 실행 때 --resume <id> 로 이어가기.
        # SessionStart 의 source(startup/resume/clear/compact) 는 매칭 안 함 → 모든 사용자 세션 시작에서 갱신.
        # transcript 없는 내부 Memory Writing Agent ID는 Test-ResumableSession 에서 차단.
        $sid = $j.session_id
        if ($sid -and (Test-ResumableSession $sid $j.transcript_path)) {
            $sDir = Join-Path $base 'sessions'
            New-Item -ItemType Directory -Force -Path $sDir | Out-Null
            Write-State (Join-Path $sDir ($roomSafe + '.txt')) $sid
        }
    }
}

exit 0
