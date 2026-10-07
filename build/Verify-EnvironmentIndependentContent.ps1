[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$errors = New-Object 'System.Collections.Generic.List[string]'

function Fail([string]$message) {
    $errors.Add($message)
}

function Read-Utf8([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path)) {
        Fail "Missing file: $relativePath"
        return ''
    }
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8
}

function U([string]$escaped) {
    return [regex]::Unescape($escaped)
}

# Keep this script ASCII-only so Windows PowerShell 5.1 cannot misdecode the script itself.
# Product files are explicitly read as UTF-8, and non-ASCII checks use \uXXXX escapes.

# Product-facing examples are intentionally confined to the help page.
# Operational pages must explain semantics without embedding concrete sample values.
$wwwroot = Join-Path $root 'TvAIr\wwwroot'
$webFiles = Get-ChildItem -LiteralPath $wwwroot -Recurse -File |
    Where-Object { $_.Extension -in '.html', '.js' }
foreach ($file in $webFiles) {
    $relative = $file.FullName.Substring($root.Length).TrimStart('\','/')
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    if ($relative -ne 'TvAIr\wwwroot\help.html') {
        # Ignore source-only comments. The gate targets text/literals that can become product-facing.
        $productFacing = [regex]::Replace($text, '(?s)<!--.*?-->', '')
        $productFacing = [regex]::Replace($productFacing, '(?s)/\*.*?\*/', '')
        $productFacing = [regex]::Replace($productFacing, '(?m)^\s*//.*$', '')
        if ($productFacing -match '\u4F8B\s*[:\uFF1A]') {
            Fail "User-facing example text is forbidden outside help.html: $relative"
        }
    }
}

$rules = Read-Utf8 'TvAIr\wwwroot\program-rules.html'
if ($rules -match '\u4F8B\s*[:\uFF1A]') {
    Fail 'program-rules.html must not contain sample/example values; it may only link to help.'
}
if ($rules -match '(?s)id=["'']pattern-hint["''][^>]*>[^<]*(?:\u8A9EA|\u8A9EB|ABC|\d\+|\(\?=)') {
    Fail 'program-rules.html pattern hint contains a concrete search sample.'
}
if ($rules -match '(?s)id=["'']exclude-hint["''][^>]*>[^<]*(?:\u8A9EA|\u8A9EB|ABC|\d\+|\(\?=)') {
    Fail 'program-rules.html exclude hint contains a concrete search sample.'
}

$help = Read-Utf8 'TvAIr\wwwroot\help.html'
$sectionMatch = [regex]::Match($help, '(?s)<section class="help-section" id="help-keyword">.*?</section>')
if (-not $sectionMatch.Success) {
    Fail 'help-keyword section is missing from help.html.'
} else {
    $section = $sectionMatch.Value

    # Every code sample in this section must be built only from the approved abstract vocabulary.
    # The allow-list is deliberately environment-independent.
    $allowedCode = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    @(
        ';','|','()',(U '\u8A9EA;\u8A9EB'),(U '\u8A9EA|\u8A9EB'),(U '\u8A9EA'),(U '\u8A9EB'),
        (U '(\u8A9EA|\u8A9EB)\u8A9EC'),(U '(?=.*\u8A9EA)(?=.*\u8A9EB).*'),
        (U '\u8A9EA.*\u8A9EB'),'\d','\d+','.*',
        '. ^ $ * + ? { } [ ] \ | ( )','\','\[ABC\]','ABC123',(U '\uFF21\uFF22\uFF23\uFF11\uFF12\uFF13')
    ) | ForEach-Object { [void]$allowedCode.Add($_) }

    foreach ($m in [regex]::Matches($section, '(?s)<code>(.*?)</code>')) {
        $value = [System.Net.WebUtility]::HtmlDecode($m.Groups[1].Value).Trim()
        if (-not $allowedCode.Contains($value)) {
            Fail "Unapproved help code sample in help-keyword: <$value>"
        }
    }

    # Keep prose quoted values on a small allow-list as well.
    $allowedQuoted = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    @(
        (U '\u30AD\u30FC\u30EF\u30FC\u30C9'),
        (U '\u9664\u5916\u30AD\u30FC\u30EF\u30FC\u30C9'),
        (U '\u30BF\u30A4\u30C8\u30EB'),
        (U '\u6982\u8981'),
        (U '\u8A73\u7D30'),
        (U '\u51FA\u6F14\u8005'),
        (U '\u8A9EA'),
        (U '\u8A9EB'),
        '[ABC]'
    ) | ForEach-Object { [void]$allowedQuoted.Add($_) }

    foreach ($m in [regex]::Matches($section, '\u300C([^\u300D]+)\u300D')) {
        $value = $m.Groups[1].Value
        if (-not $allowedQuoted.Contains($value)) {
            Fail "Unapproved quoted example/value in help-keyword: <$value>"
        }
    }
}

# The standalone regex manual must stay retired; help.html is the single product-facing source.
$legacyManual = Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'Regex_Manual.txt' -ErrorAction SilentlyContinue
if ($legacyManual) {
    foreach ($file in $legacyManual) {
        Fail "Legacy Regex_Manual.txt must not be distributed: $($file.FullName)"
    }
}

if ($errors.Count -gt 0) {
    throw ("Environment-independent content gate failed.`r`n - " + ($errors -join "`r`n - "))
}

Write-Host 'Environment-independent content gate: OK'
