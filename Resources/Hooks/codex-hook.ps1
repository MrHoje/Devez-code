# DevezCode codex hook (UserPromptSubmit / Stop / SessionStart)
# codex 훅 시스템: ~/.codex/hooks.json 에 등록. stdin 으로 JSON 받음.
# 세션 추적은 $env:DEVEZCODE_ROOM_ID (앱이 ConPTY env 로 주입) 로 식별.
# 동작: Claude 의 busy-hook.ps1 / room-hook.ps1 패턴 그대로 — 방별 파일 3종.
#   busy\<room>.txt       = running|idle
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

switch ($event) {
    'UserPromptSubmit' {
        # 1) busy=running
        $busyDir = Join-Path $base 'busy'
        New-Item -ItemType Directory -Force -Path $busyDir | Out-Null
        Write-State (Join-Path $busyDir ($roomSafe + '.txt')) 'running'

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
        # 턴 종료 → idle
        $busyDir = Join-Path $base 'busy'
        New-Item -ItemType Directory -Force -Path $busyDir | Out-Null
        Write-State (Join-Path $busyDir ($roomSafe + '.txt')) 'idle'
    }
    'SessionStart' {
        # codex session_id 기록 — 다음 실행 때 --resume <id> 로 이어가기.
        # SessionStart 의 source(startup/resume/clear/compact) 는 매칭 안 함 → 모든 세션 시작에서 덮어씀.
        $sid = $j.session_id
        if ($sid) {
            $sDir = Join-Path $base 'sessions'
            New-Item -ItemType Directory -Force -Path $sDir | Out-Null
            Write-State (Join-Path $sDir ($roomSafe + '.txt')) $sid
        }
    }
}

exit 0
