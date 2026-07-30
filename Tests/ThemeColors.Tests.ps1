$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure([string]$message) {
    $script:failures.Add($message)
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) {
        Add-Failure $message
    }
}

function Convert-HexToRgb([string]$hex) {
    if ($hex -notmatch '^#[0-9A-Fa-f]{6}$') {
        throw "Invalid HEX color: $hex"
    }

    return @(
        [Convert]::ToInt32($hex.Substring(1, 2), 16) / 255.0
        [Convert]::ToInt32($hex.Substring(3, 2), 16) / 255.0
        [Convert]::ToInt32($hex.Substring(5, 2), 16) / 255.0
    )
}

function Convert-ToLinear([double]$channel) {
    if ($channel -le 0.04045) {
        return $channel / 12.92
    }
    return [Math]::Pow(($channel + 0.055) / 1.055, 2.4)
}

function Get-RelativeLuminance([string]$hex) {
    $rgb = @(Convert-HexToRgb $hex)
    $red = Convert-ToLinear $rgb[0]
    $green = Convert-ToLinear $rgb[1]
    $blue = Convert-ToLinear $rgb[2]
    return 0.2126 * $red + 0.7152 * $green + 0.0722 * $blue
}

function Get-ContrastRatio([string]$foreground, [string]$background) {
    $first = Get-RelativeLuminance $foreground
    $second = Get-RelativeLuminance $background
    $lighter = [Math]::Max($first, $second)
    $darker = [Math]::Min($first, $second)
    return ($lighter + 0.05) / ($darker + 0.05)
}

function Convert-ToOklab([string]$hex) {
    $rgb = @(Convert-HexToRgb $hex)
    $red = Convert-ToLinear $rgb[0]
    $green = Convert-ToLinear $rgb[1]
    $blue = Convert-ToLinear $rgb[2]

    $l = 0.4122214708 * $red + 0.5363325363 * $green + 0.0514459929 * $blue
    $m = 0.2119034982 * $red + 0.6806995451 * $green + 0.1073969566 * $blue
    $s = 0.0883024619 * $red + 0.2817188376 * $green + 0.6299787005 * $blue
    $lRoot = [Math]::Pow($l, 1.0 / 3.0)
    $mRoot = [Math]::Pow($m, 1.0 / 3.0)
    $sRoot = [Math]::Pow($s, 1.0 / 3.0)

    return @(
        0.2104542553 * $lRoot + 0.7936177850 * $mRoot - 0.0040720468 * $sRoot
        1.9779984951 * $lRoot - 2.4285922050 * $mRoot + 0.4505937099 * $sRoot
        0.0259040371 * $lRoot + 0.7827717662 * $mRoot - 0.8086757660 * $sRoot
    )
}

function Get-OklabDistance([string]$first, [string]$second) {
    $a = @(Convert-ToOklab $first)
    $b = @(Convert-ToOklab $second)
    return 100.0 * [Math]::Sqrt(
        [Math]::Pow($a[0] - $b[0], 2) +
        [Math]::Pow($a[1] - $b[1], 2) +
        [Math]::Pow($a[2] - $b[2], 2))
}

function Get-OklabChroma([string]$hex) {
    $lab = @(Convert-ToOklab $hex)
    return 100.0 * [Math]::Sqrt(
        [Math]::Pow($lab[1], 2) +
        [Math]::Pow($lab[2], 2))
}

function Assert-MinimumContrast(
    [string]$foreground,
    [string]$background,
    [string]$label,
    [double]$minimum = 4.5
) {
    $ratio = Get-ContrastRatio $foreground $background
    if ($ratio -lt $minimum) {
        Add-Failure ("{0}: contrast {1:N2}:1 is below {2:N1}:1" -f $label, $ratio, $minimum)
    }
}

function Assert-Maximum([double]$actual, [double]$maximum, [string]$label) {
    if ($actual -gt $maximum) {
        Add-Failure ("{0}: {1:N2} exceeds maximum {2:N2}" -f $label, $actual, $maximum)
    }
}

function Assert-Minimum([double]$actual, [double]$minimum, [string]$label) {
    if ($actual -lt $minimum) {
        Add-Failure ("{0}: {1:N2} is below minimum {2:N2}" -f $label, $actual, $minimum)
    }
}

function Assert-SemanticColor([string]$hex, [ValidateSet("Added", "Removed")]$kind, [string]$label) {
    $rgb = @(Convert-HexToRgb $hex)
    if ($kind -eq "Added") {
        Assert-True ($rgb[1] -gt $rgb[0] -and $rgb[1] -gt $rgb[2]) "$label`: added color is not green-dominant ($hex)"
    }
    else {
        Assert-True ($rgb[0] -gt $rgb[1] -and $rgb[0] -gt $rgb[2]) "$label`: removed color is not red-dominant ($hex)"
    }
}

function Get-PropertyValue($object, [string]$name) {
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) {
        throw "JSON property not found: $name"
    }
    return $property.Value
}

function Get-RawThemeJsonMap([string]$source) {
    $map = @{}
    $pattern = 'private const string (?<name>\w+)ThemeJson\s*=\s*"""\s*(?<json>\{.*?\})\s*""";'
    foreach ($match in [regex]::Matches($source, $pattern, [Text.RegularExpressions.RegexOptions]::Singleline)) {
        $map[$match.Groups["name"].Value.ToLowerInvariant()] = $match.Groups["json"].Value
    }
    return $map
}

function Add-DerivedThemeJson([hashtable]$map, [string]$source) {
    $themePattern = 'private static readonly string (?<name>\w+)ThemeJson\s*=\s*(?<base>\w+)ThemeJson(?<chain>.*?);'
    $replacePattern = '\.Replace\("(?<from>[^"]*)",\s*"(?<to>[^"]*)",\s*StringComparison\.(?<comparison>OrdinalIgnoreCase|Ordinal)\)'

    foreach ($themeMatch in [regex]::Matches(
        $source,
        $themePattern,
        [Text.RegularExpressions.RegexOptions]::Singleline
    )) {
        $name = $themeMatch.Groups["name"].Value.ToLowerInvariant()
        $baseName = $themeMatch.Groups["base"].Value.ToLowerInvariant()
        Assert-True ($map.ContainsKey($baseName)) "OpenCode $name base theme not found: $baseName"
        if (-not $map.ContainsKey($baseName)) {
            continue
        }

        $json = [string]$map[$baseName]
        foreach ($replaceMatch in [regex]::Matches($themeMatch.Groups["chain"].Value, $replacePattern)) {
            $from = $replaceMatch.Groups["from"].Value
            $to = $replaceMatch.Groups["to"].Value
            if ($replaceMatch.Groups["comparison"].Value -eq "OrdinalIgnoreCase") {
                $json = [regex]::Replace(
                    $json,
                    [regex]::Escape($from),
                    $to,
                    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
            }
            else {
                $json = $json.Replace($from, $to)
            }
        }
        $map[$name] = $json
    }
}

function Resolve-OpenCodeColor($theme, [string]$token, [string]$variant) {
    $themeTokens = Get-PropertyValue $theme "theme"
    $entry = Get-PropertyValue $themeTokens $token
    $value = if ($entry -is [string]) {
        [string]$entry
    }
    else {
        [string](Get-PropertyValue $entry $variant)
    }

    if ($value -match '^#[0-9A-Fa-f]{6}$') {
        return $value.ToUpperInvariant()
    }

    $defs = Get-PropertyValue $theme "defs"
    $resolved = [string](Get-PropertyValue $defs $value)
    if ($resolved -notmatch '^#[0-9A-Fa-f]{6}$') {
        throw "OpenCode color reference cannot be resolved: $token=$value=$resolved"
    }
    return $resolved.ToUpperInvariant()
}

function Get-GajaeThemeMap([string]$source) {
    $themes = @{}
    $blockPattern = '"(?<name>soft|minimal|gray|softpink|midnight)"\s*=>\s*new\(\)\s*\{(?<body>.*?)\n\s*\},'
    foreach ($match in [regex]::Matches(
        $source,
        $blockPattern,
        [Text.RegularExpressions.RegexOptions]::Singleline
    )) {
        $themes[$match.Groups["name"].Value] = Get-GajaeVars $match.Groups["body"].Value
    }

    $darkMatch = [regex]::Match(
        $source,
        '_ => new\(\)\s*// dark\s*\{(?<body>.*?)\n\s*\},',
        [Text.RegularExpressions.RegexOptions]::Singleline)
    Assert-True $darkMatch.Success "Gajae dark palette not found"
    if ($darkMatch.Success) {
        $themes["dark"] = Get-GajaeVars $darkMatch.Groups["body"].Value
    }
    return $themes
}

function Get-GajaeVars([string]$body) {
    $vars = @{}
    $pairPattern = '\["(?<key>[^"]+)"\]\s*=\s*"(?<value>#[0-9A-Fa-f]{6})"'
    foreach ($match in [regex]::Matches($body, $pairPattern)) {
        $vars[$match.Groups["key"].Value] = $match.Groups["value"].Value.ToUpperInvariant()
    }
    return $vars
}

$claudePath = Join-Path $repoRoot "Services\Terminal\ClaudeCustomThemes.cs"
$openCodePath = Join-Path $repoRoot "Services\Terminal\OpenCodeCustomThemes.cs"
$gajaePath = Join-Path $repoRoot "Services\Terminal\GajaeCustomThemes.cs"

$claudeSource = Get-Content -LiteralPath $claudePath -Raw
$claudeJson = Get-RawThemeJsonMap $claudeSource
Assert-True ($claudeJson.Count -eq 5) "Claude custom theme count is not 5: $($claudeJson.Count)"
$claudePalettes = @{}

foreach ($name in @("soft", "minimal", "gray", "softpink", "midnight")) {
    Assert-True ($claudeJson.ContainsKey($name)) "Claude theme missing: $name"
    if (-not $claudeJson.ContainsKey($name)) {
        continue
    }

    try {
        $theme = $claudeJson[$name] | ConvertFrom-Json
        $colors = Get-PropertyValue $theme "overrides"
        $text = [string](Get-PropertyValue $colors "text")
        $addRow = [string](Get-PropertyValue $colors "diffAdded")
        $removeRow = [string](Get-PropertyValue $colors "diffRemoved")
        $addWord = [string](Get-PropertyValue $colors "diffAddedWord")
        $removeWord = [string](Get-PropertyValue $colors "diffRemovedWord")
        $addDimmed = [string](Get-PropertyValue $colors "diffAddedDimmed")
        $removeDimmed = [string](Get-PropertyValue $colors "diffRemovedDimmed")

        Assert-MinimumContrast $text $addRow "Claude $name added row"
        Assert-MinimumContrast $text $removeRow "Claude $name removed row"
        Assert-MinimumContrast $text $addWord "Claude $name added word"
        Assert-MinimumContrast $text $removeWord "Claude $name removed word"
        $addWordDistance = Get-OklabDistance $addRow $addWord
        $removeWordDistance = Get-OklabDistance $removeRow $removeWord
        $addRowDistance = Get-OklabDistance $addDimmed $addRow
        $removeRowDistance = Get-OklabDistance $removeDimmed $removeRow
        Assert-Minimum $addWordDistance 8.0 "Claude $name added word distance"
        Assert-Minimum $removeWordDistance 8.0 "Claude $name removed word distance"
        Assert-Maximum $addWordDistance 18.0 "Claude $name added word distance"
        Assert-Maximum $removeWordDistance 18.0 "Claude $name removed word distance"
        Assert-Minimum $addRowDistance 2.0 "Claude $name added row distance"
        Assert-Minimum $removeRowDistance 2.0 "Claude $name removed row distance"
        Assert-Maximum $addRowDistance 12.0 "Claude $name added row distance"
        Assert-Maximum $removeRowDistance 12.0 "Claude $name removed row distance"
        Assert-SemanticColor $addRow Added "Claude $name added row"
        Assert-SemanticColor $removeRow Removed "Claude $name removed row"
        Assert-SemanticColor $addWord Added "Claude $name added word"
        Assert-SemanticColor $removeWord Removed "Claude $name removed word"

        $claudePalettes[$name] = @{
            AddedBackground = $addRow.ToUpperInvariant()
            RemovedBackground = $removeRow.ToUpperInvariant()
        }
    }
    catch {
        Add-Failure "Claude $name JSON validation failed: $($_.Exception.Message)"
    }
}

$openCodeSource = Get-Content -LiteralPath $openCodePath -Raw
$openCodeJson = Get-RawThemeJsonMap $openCodeSource
Add-DerivedThemeJson $openCodeJson $openCodeSource
Assert-True ($openCodeJson.Count -eq 6) "OpenCode theme count is not 6: $($openCodeJson.Count)"
$openCodePalettes = @{}

foreach ($name in @("dark", "soft", "minimal", "gray", "softpink", "midnight")) {
    Assert-True ($openCodeJson.ContainsKey($name)) "OpenCode theme missing: $name"
    if (-not $openCodeJson.ContainsKey($name)) {
        continue
    }

    try {
        $theme = $openCodeJson[$name] | ConvertFrom-Json
        $variant = if ($name -in @("dark", "midnight")) { "dark" } else { "light" }
        $background = Resolve-OpenCodeColor $theme "background" $variant
        $text = Resolve-OpenCodeColor $theme "text" $variant
        $addText = Resolve-OpenCodeColor $theme "diffAdded" $variant
        $removeText = Resolve-OpenCodeColor $theme "diffRemoved" $variant
        $addHighlight = Resolve-OpenCodeColor $theme "diffHighlightAdded" $variant
        $removeHighlight = Resolve-OpenCodeColor $theme "diffHighlightRemoved" $variant
        $contextText = Resolve-OpenCodeColor $theme "diffContext" $variant
        $hunkHeader = Resolve-OpenCodeColor $theme "diffHunkHeader" $variant
        $addBackground = Resolve-OpenCodeColor $theme "diffAddedBg" $variant
        $removeBackground = Resolve-OpenCodeColor $theme "diffRemovedBg" $variant
        $contextBackground = Resolve-OpenCodeColor $theme "diffContextBg" $variant
        $lineNumber = Resolve-OpenCodeColor $theme "diffLineNumber" $variant
        $addLineBackground = Resolve-OpenCodeColor $theme "diffAddedLineNumberBg" $variant
        $removeLineBackground = Resolve-OpenCodeColor $theme "diffRemovedLineNumberBg" $variant

        Assert-MinimumContrast $text $background "OpenCode $name body"
        Assert-MinimumContrast $addText $addBackground "OpenCode $name added row"
        Assert-MinimumContrast $removeText $removeBackground "OpenCode $name removed row"
        Assert-MinimumContrast $addHighlight $addBackground "OpenCode $name added highlight"
        Assert-MinimumContrast $removeHighlight $removeBackground "OpenCode $name removed highlight"
        Assert-MinimumContrast $contextText $contextBackground "OpenCode $name context"
        Assert-MinimumContrast $hunkHeader $contextBackground "OpenCode $name hunk header"
        Assert-MinimumContrast $addText $background "OpenCode $name added on base"
        Assert-MinimumContrast $removeText $background "OpenCode $name removed on base"
        Assert-MinimumContrast $lineNumber $addLineBackground "OpenCode $name added line number"
        Assert-MinimumContrast $lineNumber $removeLineBackground "OpenCode $name removed line number"
        $addRowDistance = Get-OklabDistance $background $addBackground
        $removeRowDistance = Get-OklabDistance $background $removeBackground
        $addChroma = Get-OklabChroma $addText
        $removeChroma = Get-OklabChroma $removeText
        Assert-Minimum $addRowDistance 2.0 "OpenCode $name added row distance"
        Assert-Minimum $removeRowDistance 2.0 "OpenCode $name removed row distance"
        Assert-Maximum $addRowDistance 12.0 "OpenCode $name added row distance"
        Assert-Maximum $removeRowDistance 12.0 "OpenCode $name removed row distance"
        Assert-Minimum $addChroma 4.0 "OpenCode $name added foreground chroma"
        Assert-Minimum $removeChroma 4.0 "OpenCode $name removed foreground chroma"
        Assert-Maximum $addChroma 13.5 "OpenCode $name added foreground chroma"
        Assert-Maximum $removeChroma 13.5 "OpenCode $name removed foreground chroma"
        Assert-SemanticColor $addText Added "OpenCode $name added foreground"
        Assert-SemanticColor $removeText Removed "OpenCode $name removed foreground"
        Assert-SemanticColor $addBackground Added "OpenCode $name added background"
        Assert-SemanticColor $removeBackground Removed "OpenCode $name removed background"

        $openCodePalettes[$name] = @{
            Added = $addText
            Removed = $removeText
            AddedBackground = $addBackground
            RemovedBackground = $removeBackground
        }
    }
    catch {
        Add-Failure "OpenCode $name JSON validation failed: $($_.Exception.Message)"
    }
}

$gajaeSource = Get-Content -LiteralPath $gajaePath -Raw
$gajaeThemes = Get-GajaeThemeMap $gajaeSource
Assert-True ($gajaeThemes.Count -eq 6) "Gajae theme count is not 6: $($gajaeThemes.Count)"

foreach ($name in @("dark", "soft", "minimal", "gray", "softpink", "midnight")) {
    Assert-True ($gajaeThemes.ContainsKey($name)) "Gajae theme missing: $name"
    if (-not $gajaeThemes.ContainsKey($name)) {
        continue
    }

    try {
        $colors = $gajaeThemes[$name]
        $background = $colors["background"]
        $backgroundDarker = $colors["backgroundDarker"]
        $addText = $colors["diffAdded"]
        $removeText = $colors["diffRemoved"]
        $contextText = $colors["diffContext"]
        $addBackground = $colors["addedBg"]
        $removeBackground = $colors["removedBg"]

        Assert-MinimumContrast $addText $addBackground "Gajae $name added row"
        Assert-MinimumContrast $removeText $removeBackground "Gajae $name removed row"
        Assert-MinimumContrast $addText $background "Gajae $name added on base"
        Assert-MinimumContrast $removeText $background "Gajae $name removed on base"
        Assert-MinimumContrast $contextText $background "Gajae $name context on base"
        Assert-MinimumContrast $contextText $addBackground "Gajae $name context on added row"
        Assert-MinimumContrast $contextText $removeBackground "Gajae $name context on removed row"
        Assert-MinimumContrast $addText $backgroundDarker "Gajae $name staged status"
        $addRowDistance = Get-OklabDistance $background $addBackground
        $removeRowDistance = Get-OklabDistance $background $removeBackground
        $addChroma = Get-OklabChroma $addText
        $removeChroma = Get-OklabChroma $removeText
        Assert-Minimum $addRowDistance 2.0 "Gajae $name added row distance"
        Assert-Minimum $removeRowDistance 2.0 "Gajae $name removed row distance"
        Assert-Maximum $addRowDistance 12.0 "Gajae $name added row distance"
        Assert-Maximum $removeRowDistance 12.0 "Gajae $name removed row distance"
        Assert-Minimum $addChroma 4.0 "Gajae $name added foreground chroma"
        Assert-Minimum $removeChroma 4.0 "Gajae $name removed foreground chroma"
        Assert-Maximum $addChroma 13.5 "Gajae $name added foreground chroma"
        Assert-Maximum $removeChroma 13.5 "Gajae $name removed foreground chroma"
        Assert-SemanticColor $addText Added "Gajae $name added foreground"
        Assert-SemanticColor $removeText Removed "Gajae $name removed foreground"
        Assert-SemanticColor $addBackground Added "Gajae $name added background"
        Assert-SemanticColor $removeBackground Removed "Gajae $name removed background"

        if ($openCodePalettes.ContainsKey($name)) {
            $openCode = $openCodePalettes[$name]
            Assert-True ($addText -eq $openCode.Added) "Gajae/OpenCode $name added foreground mismatch"
            Assert-True ($removeText -eq $openCode.Removed) "Gajae/OpenCode $name removed foreground mismatch"
            Assert-True ($addBackground -eq $openCode.AddedBackground) "Gajae/OpenCode $name added background mismatch"
            Assert-True ($removeBackground -eq $openCode.RemovedBackground) "Gajae/OpenCode $name removed background mismatch"
        }

        if ($claudePalettes.ContainsKey($name)) {
            $claude = $claudePalettes[$name]
            Assert-True ($addBackground -eq $claude.AddedBackground) "Gajae/Claude $name added background mismatch"
            Assert-True ($removeBackground -eq $claude.RemovedBackground) "Gajae/Claude $name removed background mismatch"
        }
    }
    catch {
        Add-Failure "Gajae $name palette validation failed: $($_.Exception.Message)"
    }
}

if ($failures.Count -gt 0) {
    throw "Theme color validation failed ($($failures.Count)):`n- $($failures -join "`n- ")"
}

Write-Output "Theme color validation passed: Claude 5, OpenCode 6, Gajae 6"
