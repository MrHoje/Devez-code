# UI 점검: 하드코딩 색·인라인 Style 검출. 대상 파일을 인자로 전달.
# 사용: pwsh scripts/ui-lint.ps1 Views/GitScmView.xaml Views/MonacoDiffHostView.xaml ...
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Files)

$allow = @('#3FB950','#F85149','#D29922')   # diff 상태색 예외
$violations = @()

foreach ($f in $Files) {
    if (-not (Test-Path $f)) { continue }
    $n = 0
    foreach ($line in Get-Content $f) {
        $n++
        foreach ($m in [regex]::Matches($line, '#[0-9A-Fa-f]{6,8}')) {
            $hex = $m.Value.Substring(0,7).ToUpper()
            if ($allow -notcontains $hex) {
                $violations += "{0}:{1}  하드코딩 색 {2}" -f $f,$n,$m.Value
            }
        }
        if ($f -like '*.xaml' -and $line -match '<Style ' -and $line -notmatch 'BasedOn') {
            $violations += "{0}:{1}  인라인 <Style> (BasedOn 없음)" -f $f,$n
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "UI 점검 위반 $($violations.Count)건:" -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "UI 점검 통과: 위반 0" -ForegroundColor Green
