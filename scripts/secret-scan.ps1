[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Do not print matching lines: CI logs must not become another secret exposure.
$patterns = @(
    'GOCSPX-[A-Za-z0-9_-]{10,}',
    'gh[pousr]_[A-Za-z0-9_]{20,}',
    'github_pat_[A-Za-z0-9_]{20,}',
    'sk-[A-Za-z0-9_-]{16,}',
    'AKIA[0-9A-Z]{16}',
    'AIza[0-9A-Za-z_-]{20,}',
    'xox[baprs]-[A-Za-z0-9-]{10,}',
    'eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}',
    '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '(?i)(?:oauthclientsecret|client_secret|service_role|private_key)\s*[:=]\s*["''][^"'']{8,}'
)

$extensions = @('.cs', '.csproj', '.json', '.xml', '.xaml', '.md', '.ps1', '.cmd', '.bat', '.js', '.html', '.yml', '.yaml', '.toml', '.ini', '.config')
$vendoredPrefixes = @('Resources/Markdown/web/', 'Resources/Monaco/web/', 'Resources/Terminal/web/')
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$paths = git ls-files | Where-Object {
    $candidate = $_
    $isVendored = $vendoredPrefixes | Where-Object {
        $candidate.StartsWith($_, [StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1
    $candidate -ne 'scripts/secret-scan.ps1' -and
    -not $isVendored -and
    $extensions -contains [IO.Path]::GetExtension($candidate).ToLowerInvariant() -and
    (Test-Path -LiteralPath (Join-Path $repositoryRoot $candidate) -PathType Leaf)
}

$hits = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($path in $paths) {
    $text = [IO.File]::ReadAllText((Join-Path $repositoryRoot $path))
    foreach ($pattern in $patterns) {
        if ($text -match $pattern) {
            [void]$hits.Add($path)
            break
        }
    }
}

if ($hits.Count -gt 0) {
    throw "Potential secret detected in tracked file(s): $($hits -join ', ')"
}

Write-Output 'Secret scan passed.'
