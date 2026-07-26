# DevezCode kimi hook (SessionStart / UserPromptSubmit / Stop / StopFailure)
# Kimi Code CLI 훅: ~/.kimi-code/config.toml 의 [[hooks]] 에 등록. stdin 으로 JSON 받음.
# Kimi 훅 runner 는 camelCase → snake_case 로 변환해 stdin 에 전달하므로 필드는
#   hook_event_name / session_id / cwd / prompt(ContentPart[]) 로 codex/claude 와 동일.
# 세션 추적은 $env:DEVEZCODE_ROOM_ID (앱이 ConPTY env 로 주입) 로 식별.
# 방별 파일:
#   busy\<room>.txt     = running|idle
#   waiting\<room>.txt  = waiting|idle   (Permission* 는 경량 cmd 훅이 소유)
#   lastmsg\<room>.txt  = 마지막 user prompt (1줄, 200자)
#   sessions\<room>.txt = kimi session id (session_<uuid>) — 재오픈 시 -S <id> 용

$ErrorActionPreference = 'SilentlyContinue'

# stdin UTF-8 명시 디코딩 (PS5.1 [Console]::In 은 콘솔 코드페이지로 디코딩해 한글이 깨짐).
try {
    $reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
    $raw = $reader.ReadToEnd()
    $reader.Dispose()
    if (-not $raw) { exit 0 }
    $j = $raw | ConvertFrom-Json
} catch { exit 0 }

$room = $env:DEVEZCODE_ROOM_ID
if (-not $room) { exit 0 }
if ($env:DEVEZCODE_TRACKING_AGENT -ne 'kimi') { exit 0 }

$roomSafe = $room -replace '[^\w\-]', ''
$base = Join-Path $env:APPDATA 'DevezCode\kimi'
$event = $j.hook_event_name

# 원자적 상태 기록 — temp 에 완전히 쓴 뒤 Move-Item rename. 직접 Set-Content 는
# truncate 직후 훅 중단 시 0바이트 파일이 남아 idle 전이를 놓친다(스피너 stuck-ON).
function Write-State($path, $value, $encoding = 'Ascii') {
    try {
        $dir = Split-Path -Parent $path
        if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        $tmp = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        Set-Content -LiteralPath $tmp -Value $value -Encoding $encoding -Force
        Move-Item -LiteralPath $tmp -Destination $path -Force
    } catch { try { Set-Content -LiteralPath $path -Value $value -Encoding $encoding -Force } catch { } }
}

function Write-Busy([string]$status)    { Write-State (Join-Path $base ('busy\'    + $roomSafe + '.txt')) $status }
function Write-Waiting([string]$status) { Write-State (Join-Path $base ('waiting\' + $roomSafe + '.txt')) $status }

function Get-SessionPath([string]$suffix = '') {
    return Join-Path $base ('sessions\' + $roomSafe + $suffix + '.txt')
}

function Read-Session([string]$path) {
    try {
        if (-not (Test-Path -LiteralPath $path)) { return '' }
        return (Get-Content -LiteralPath $path -Raw -Encoding UTF8).Trim()
    } catch { return '' }
}

function Test-CurrentRoomSession([string]$sid) {
    if (-not $sid) { return $false }
    $tracked = Read-Session (Get-SessionPath)
    $root = Read-Session (Get-SessionPath '.root')
    return $tracked -and $root -and
           [string]::Equals($tracked, $sid, [StringComparison]::OrdinalIgnoreCase) -and
           [string]::Equals($root, $sid, [StringComparison]::OrdinalIgnoreCase)
}

function Get-ActivePath {
    return Join-Path $base ('active\' + $roomSafe + '.txt')
}

$rootMutex = $null
$rootMutexHeld = $false
function Enter-RootLock {
    try {
        $script:rootMutex = New-Object System.Threading.Mutex(
            $false, ('Local\DevezCode.KimiRoot.' + $roomSafe))
        try {
            $script:rootMutexHeld = $script:rootMutex.WaitOne(10000)
        } catch [System.Threading.AbandonedMutexException] {
            $script:rootMutexHeld = $true
        }
        return $script:rootMutexHeld
    } catch { return $false }
}

function Exit-RootLock {
    try {
        if ($script:rootMutexHeld -and $script:rootMutex) { $script:rootMutex.ReleaseMutex() }
    } catch { }
    try { if ($script:rootMutex) { $script:rootMutex.Dispose() } } catch { }
    $script:rootMutexHeld = $false
    $script:rootMutex = $null
}

# UserPromptSubmit 의 prompt 는 ContentPart[] ({type,text} 배열). text 파트만 이어붙인다.
# 방어적으로 문자열/기타 형태도 처리.
function Get-PromptText($prompt) {
    if (-not $prompt) { return '' }
    if ($prompt -is [string]) { return $prompt }
    $parts = @()
    foreach ($p in $prompt) {
        if ($p -is [string]) { $parts += $p }
        elseif ($p.text)     { $parts += [string]$p.text }
    }
    return ($parts -join ' ')
}

switch ($event) {
    'SessionStart' {
        # kimi session id 기록 — 다음 실행에 -S <id> 로 이어가기.
        # Kimi 세션 id 형식은 session_<uuid>. 형식 검증만 하고 존재확인은 앱 측에서.
        $sid = [string]$j.session_id
        if ($sid -and $sid -match '^session_[0-9A-Za-z_-]+$') {
            if (-not (Enter-RootLock)) { break }
            try {
                $rootPath = Get-SessionPath '.root'
                $trackedPath = Get-SessionPath
                $root = Read-Session $rootPath
                $source = [string]$j.source
                $allowExplicitTransition = $source -eq 'clear' -or $source -eq 'new'
                if ($root -and -not [string]::Equals($root, $sid,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    if (-not $allowExplicitTransition) { break }
                    Write-State (Get-SessionPath '.prev') $root
                    try { Remove-Item -LiteralPath (Get-ActivePath) -Force -ErrorAction SilentlyContinue } catch { }
                }
                Write-State $rootPath $sid
                Write-State $trackedPath $sid
            } finally {
                Exit-RootLock
            }
        }
    }
    'UserPromptSubmit' {
        $sid = [string]$j.session_id
        if (-not (Test-CurrentRoomSession $sid)) { break }
        Write-Busy 'running'
        Write-Waiting 'idle'
        Write-State (Get-ActivePath) $sid
        $txt = Get-PromptText $j.prompt
        if ($txt) {
            $txt = ($txt -replace '\s+', ' ').Trim()
            if ($txt.Length -gt 200) { $txt = $txt.Substring(0, 200) }
            if ($txt) { Write-State (Join-Path $base ('lastmsg\' + $roomSafe + '.txt')) $txt 'UTF8' }
        }
    }
    'Stop' {
        $sid = [string]$j.session_id
        if (-not (Test-CurrentRoomSession $sid)) { break }
        Write-Busy 'idle'
        Write-Waiting 'idle'
        try { Remove-Item -LiteralPath (Get-ActivePath) -Force -ErrorAction SilentlyContinue } catch { }
    }
    'StopFailure' {
        $sid = [string]$j.session_id
        if (-not (Test-CurrentRoomSession $sid)) { break }
        Write-Busy 'idle'
        Write-Waiting 'idle'
        try { Remove-Item -LiteralPath (Get-ActivePath) -Force -ErrorAction SilentlyContinue } catch { }
    }
}

exit 0
