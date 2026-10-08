[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$validator = Join-Path $PSScriptRoot 'Verify-GeneralLogReportBoundary.ps1'
if (-not (Test-Path -LiteralPath $validator)) {
    throw 'Developer reporting boundary validator is missing.'
}

# First verify the report-text rule itself.
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('tvair-report-boundary-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    $clean = Join-Path $tempRoot 'clean.txt'
    $blockedSingle = Join-Path $tempRoot 'blocked-single.txt'
    $blockedChain = Join-Path $tempRoot 'blocked-chain.txt'

    # Keep this gate script ASCII-only so Windows PowerShell 5.1 parses it identically
    # regardless of the host ANSI code page. The validator behavior under test is the
    # R-number boundary, so Japanese sample text is not required here.
    Set-Content -LiteralPath $clean -Encoding UTF8 -Value 'WOWOW Cinema / Legal High #9 / 12:59:30-13:48:15 completed normally.'
    Set-Content -LiteralPath $blockedSingle -Encoding UTF8 -Value 'R681 completed normally.'
    Set-Content -LiteralPath $blockedChain -Encoding UTF8 -Value 'R681->R682->R683 completed as a three-program chain.'

    & $validator -ReportPath $clean

    foreach ($blocked in @($blockedSingle, $blockedChain)) {
        $blockedAsExpected = $false
        try {
            & $validator -ReportPath $blocked
        }
        catch {
            $blockedAsExpected = $true
        }
        if (-not $blockedAsExpected) {
            throw 'Developer reporting boundary must reject internal reservation R-number identifiers.'
        }
    }
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# Then verify the architectural boundary: this guard may exist only as a
# Developer source/build workflow and must not be wired into Public runtime code.
$targetsPath = Join-Path $root 'Directory.Build.targets'
$targets = Get-Content -LiteralPath $targetsPath -Raw -Encoding UTF8
if ($targets -notmatch "\$\(TvAIrDeveloperDiagnostics\)' == 'true'") {
    throw 'Developer regression gates must remain conditional on TvAIrDeveloperDiagnostics=true.'
}

$runtimeRoots = @('TvAIr', 'TvAIrEpgRec', 'TvAIrPlugin', 'Shared', 'AribDecodeBridge')
$forbiddenMarkers = @(
    'ASSISTANT_REPORTING_CONTRACT',
    'Verify-GeneralLogReportBoundary',
    'Verify-AssistantReportingBoundary'
)

foreach ($runtimeRoot in $runtimeRoots) {
    $path = Join-Path $root $runtimeRoot
    if (-not (Test-Path -LiteralPath $path)) { continue }

    $files = Get-ChildItem -LiteralPath $path -File -Recurse -ErrorAction Stop |
        Where-Object { $_.Extension -in @('.cs', '.cpp', '.h', '.hpp', '.js', '.html', '.json', '.xml', '.props', '.targets') }

    foreach ($file in $files) {
        $content = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
        if ($null -eq $content) { continue }
        foreach ($marker in $forbiddenMarkers) {
            if ($content.IndexOf($marker, [System.StringComparison]::Ordinal) -ge 0) {
                $relative = $file.FullName.Substring($root.Length).TrimStart([char[]]@('\','/'))
                throw "Developer reporting guard leaked into runtime source: $relative marker=$marker"
            }
        }
    }
}

Write-Host 'Developer reporting boundary gate: OK; Public runtime contamination=0'
