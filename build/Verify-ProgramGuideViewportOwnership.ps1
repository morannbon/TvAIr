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

function Slice-Between([string]$text, [string]$startMarker, [string]$endMarker, [string]$name) {
    $start = $text.IndexOf($startMarker, [System.StringComparison]::Ordinal)
    if ($start -lt 0) {
        Fail "Missing start marker for $name"
        return ''
    }
    $end = $text.IndexOf($endMarker, $start + $startMarker.Length, [System.StringComparison]::Ordinal)
    if ($end -lt 0) {
        Fail "Missing end marker for $name"
        return ''
    }
    return $text.Substring($start, $end - $start)
}

function Assert-Order([string]$text, [string]$first, [string]$second, [string]$name) {
    $a = $text.IndexOf($first, [System.StringComparison]::Ordinal)
    $b = $text.IndexOf($second, [System.StringComparison]::Ordinal)
    if ($a -lt 0 -or $b -lt 0 -or $a -ge $b) {
        Fail "$name ordering contract is broken: <$first> must precede <$second>."
    }
}

# Keep this script ASCII-only for Windows PowerShell 5.1 compatibility.
# DeveloperRegressionGateContract:
# - ProgramCell action/content code must not own ProgramGuide viewport or full-load behavior.
# - Full ProgramGuide rebuild may collapse scrollWidth transiently.
# - Horizontal restore therefore belongs to the full-load/full-refresh caller after renderGuide.
# - Vertical viewport ownership remains inside renderGuide timeline projection.
# This is a source/build gate only. It is not part of the Public runtime path.

$index = Read-Utf8 'TvAIr\wwwroot\index.html'

$horizontalHelper = Slice-Between $index 'function restoreProgramGuideHorizontalScroll(' '// ' 'restoreProgramGuideHorizontalScroll'
if ($horizontalHelper -notmatch 'scrollLeft\s*=') {
    Fail 'Horizontal restore helper must assign scrollLeft.'
}
foreach ($forbidden in @('scrollTop', 'setProgramGuideVerticalMinute', 'programGuideVerticalMinute=')) {
    if ($horizontalHelper.Contains($forbidden)) {
        Fail "Horizontal restore helper must not mutate vertical viewport state: $forbidden"
    }
}

$loadGuide = Slice-Between $index 'async function loadGuide(' 'let lastGuideRefreshSignature' 'loadGuide'
if ($loadGuide -notmatch 'const\s+savedLeft\s*=') {
    Fail 'loadGuide must capture the horizontal viewport before full render.'
}
if ($loadGuide -notmatch 'restoreProgramGuideHorizontalScroll\(gs,\s*savedLeft,') {
    Fail 'loadGuide must restore saved horizontal viewport through the horizontal-only helper.'
}
Assert-Order $loadGuide 'renderGuide(data, traceReason, requestedVerticalMinute);' "restoreProgramGuideHorizontalScroll(gs, savedLeft, 'loadGuide-horizontal-restore');" 'loadGuide horizontal restore'

$refreshGuide = Slice-Between $index 'async function refreshGuide(' 'function toGuideMinute(' 'refreshGuide'
if ($refreshGuide -notmatch 'const\s+savedLeft\s*=\s*gs\.scrollLeft') {
    Fail 'refreshGuide must capture the horizontal viewport before full render.'
}
if ($refreshGuide -notmatch 'restoreProgramGuideHorizontalScroll\(gs,\s*savedLeft,') {
    Fail 'refreshGuide full-render path must restore saved horizontal viewport through the horizontal-only helper.'
}
Assert-Order $refreshGuide "renderGuide(data, 'refreshGuide-epg-change', savedMinute);" "restoreProgramGuideHorizontalScroll(gs, savedLeft, 'refreshGuide-horizontal-restore');" 'refreshGuide horizontal restore'

$renderGuide = Slice-Between $index 'function renderGuide(' 'function drawNowLine(' 'renderGuide'
if ($renderGuide -match 'restoreProgramGuideHorizontalScroll') {
    Fail 'renderGuide must not own post-rebuild horizontal restore; full-load/full-refresh caller owns it.'
}
if ($renderGuide -match 'horizontal-restore') {
    Fail 'renderGuide contains a horizontal restore path outside the full-load/full-refresh owner.'
}
if ($renderGuide -notmatch 'restoreScroll\(gs,\s*projectedTop,\s*currentLeft\)') {
    Fail 'renderGuide vertical timeline projection contract is missing.'
}

$actionResolver = Slice-Between $index 'function resolveProgramCellActionSetContract(' 'function buildProgramCellActionStateStructureContract(' 'ProgramCell action resolver'
$cellRenderer = Slice-Between $index 'function renderProgramCellFromStructureContract(' 'function updateProgramCellReservationState(' 'ProgramCell renderer'
$programCellSlices = @(
    @{ Name='ProgramCell action resolver'; Text=$actionResolver },
    @{ Name='ProgramCell renderer'; Text=$cellRenderer }
)
$programCellForbidden = @(
    'scrollLeft',
    'scrollTop',
    'guide-scroll',
    'loadGuide(',
    'refreshGuide(',
    'renderGuide(',
    'programGuideVerticalMinute',
    'setCurrentProgramGuideTimelineGeometry',
    'restoreProgramGuideHorizontalScroll'
)
foreach ($slice in $programCellSlices) {
    foreach ($token in $programCellForbidden) {
        if ($slice.Text.Contains($token)) {
            Fail "$($slice.Name) crossed the ProgramGuide viewport/full-load boundary: $token"
        }
    }
}

if ($errors.Count -gt 0) {
    throw ("ProgramGuide viewport ownership gate failed.`r`n - " + ($errors -join "`r`n - "))
}

Write-Host 'ProgramGuide viewport ownership gate: OK'
