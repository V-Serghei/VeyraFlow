param(
    [switch]$FailOnIssues
)

$ErrorActionPreference = "Stop"

function Read-JsonMap {
    param([string]$Path)

    $obj = Get-Content -Path $Path -Raw | ConvertFrom-Json
    $map = @{}
    foreach ($p in $obj.PSObject.Properties) {
        $map[$p.Name] = [string]$p.Value
    }

    return $map
}

function Add-MatchesToSet {
    param(
        [System.Collections.Generic.HashSet[string]]$Set,
        [string]$Path,
        [string]$Pattern,
        [int]$GroupIndex
    )

    Select-String -Path $Path -Pattern $Pattern -AllMatches | ForEach-Object {
        foreach ($m in $_.Matches) {
            $value = $m.Groups[$GroupIndex].Value
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                [void]$Set.Add($value)
            }
        }
    }
}

$enPath = "src/Veyra.Desktop/Localization/en.json"
$ruPath = "src/Veyra.Desktop/Localization/ru.json"

$en = Read-JsonMap -Path $enPath
$ru = Read-JsonMap -Path $ruPath

$used = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
$pluralBases = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)

Get-ChildItem -Path "src/Veyra.Desktop" -Recurse -Filter "*.axaml" -File | ForEach-Object {
    Add-MatchesToSet -Set $used -Path $_.FullName -Pattern "\{loc:Tr\s+([^}\s]+)" -GroupIndex 1
}

Get-ChildItem -Path "src" -Recurse -Filter "*.cs" -File | ForEach-Object {
    Add-MatchesToSet -Set $used -Path $_.FullName -Pattern 'Loc\.(T|F)\("([^"]+)"' -GroupIndex 2
    Add-MatchesToSet -Set $pluralBases -Path $_.FullName -Pattern 'Loc\.P\("([^"]+)"' -GroupIndex 1
}

$missingEn = @()
$missingRu = @()
foreach ($k in $used) {
    if (-not $en.ContainsKey($k)) { $missingEn += $k }
    if (-not $ru.ContainsKey($k)) { $missingRu += $k }
}

$pluralMissingEn = @()
$pluralMissingRu = @()
foreach ($baseKey in $pluralBases) {
    foreach ($k in @("$baseKey.one", "$baseKey.other")) {
        if (-not $en.ContainsKey($k)) { $pluralMissingEn += $k }
    }

    foreach ($k in @("$baseKey.one", "$baseKey.few", "$baseKey.many", "$baseKey.other")) {
        if (-not $ru.ContainsKey($k)) { $pluralMissingRu += $k }
    }
}

$extraRu = @($ru.Keys | Where-Object { -not $en.ContainsKey($_) } | Sort-Object)
$extraEn = @($en.Keys | Where-Object { -not $ru.ContainsKey($_) } | Sort-Object)

$literalPatterns = @(
    'Text="(?!\{)([^"]+)"',
    'Content="(?!\{)([^"]+)"',
    'Title="(?!\{)([^"]+)"',
    'Watermark="(?!\{)([^"]+)"',
    'ToolTip\.Tip="(?!\{)([^"]+)"',
    'AutomationProperties\.Name="(?!\{)([^"]+)"'
)

$allowedLiteralValues = @("◈", "⚙", "|", "x")
$literalFindings = @()

Get-ChildItem -Path "src/Veyra.Desktop/Views" -Recurse -Filter "*.axaml" -File | ForEach-Object {
    $file = $_.FullName
    $lines = Get-Content -Path $file

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        foreach ($pattern in $literalPatterns) {
            foreach ($m in [regex]::Matches($line, $pattern)) {
                $value = $m.Groups[1].Value.Trim()
                if ([string]::IsNullOrWhiteSpace($value)) { continue }
                if ($allowedLiteralValues -contains $value) { continue }

                $literalFindings += [pscustomobject]@{
                    File  = $file
                    Line  = $i + 1
                    Value = $value
                }
            }
        }
    }
}

Write-Host ("Used keys:             {0}" -f $used.Count)
Write-Host ("Plural key bases:      {0}" -f $pluralBases.Count)
Write-Host ("Missing in en.json:    {0}" -f $missingEn.Count)
Write-Host ("Missing in ru.json:    {0}" -f $missingRu.Count)
Write-Host ("Missing EN plural:     {0}" -f $pluralMissingEn.Count)
Write-Host ("Missing RU plural:     {0}" -f $pluralMissingRu.Count)
Write-Host ("Extra in ru vs en:     {0}" -f $extraRu.Count)
Write-Host ("Extra in en vs ru:     {0}" -f $extraEn.Count)
Write-Host ("Static literals in UI: {0}" -f $literalFindings.Count)
Write-Host ""

if ($missingEn.Count -gt 0) {
    Write-Host "Missing keys in en.json:"
    $missingEn | Sort-Object -Unique | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
}

if ($missingRu.Count -gt 0) {
    Write-Host "Missing keys in ru.json:"
    $missingRu | Sort-Object -Unique | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
}

if ($pluralMissingEn.Count -gt 0) {
    Write-Host "Missing plural keys in en.json:"
    $pluralMissingEn | Sort-Object -Unique | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
}

if ($pluralMissingRu.Count -gt 0) {
    Write-Host "Missing plural keys in ru.json:"
    $pluralMissingRu | Sort-Object -Unique | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
}

if ($extraRu.Count -gt 0) {
    Write-Host "Extra keys in ru.json:"
    $extraRu | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
}

if ($extraEn.Count -gt 0) {
    Write-Host "Extra keys in en.json:"
    $extraEn | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
}

if ($literalFindings.Count -gt 0) {
    Write-Host "Potential non-localized literals in XAML:"
    $literalFindings | ForEach-Object {
        Write-Host ("  - {0}:{1}: {2}" -f $_.File, $_.Line, $_.Value)
    }
    Write-Host ""
}

$issueCount = $missingEn.Count +
    $missingRu.Count +
    $pluralMissingEn.Count +
    $pluralMissingRu.Count +
    $extraRu.Count +
    $extraEn.Count +
    $literalFindings.Count

if ($FailOnIssues -and $issueCount -gt 0) {
    Write-Error "Localization health check failed. Issues found: $issueCount"
    exit 1
}

exit 0

