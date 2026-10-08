[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ReportPath)) {
    throw "Development report not found: $ReportPath"
}

$text = Get-Content -LiteralPath $ReportPath -Raw -Encoding UTF8

# Developer reporting only: internal reservation identifiers must never become
# the user-facing subject. TvAIr runtime/log correlation IDs remain untouched.
$reservationIdPattern = '(?i)(?<![A-Za-z0-9_])R[0-9]+(?![A-Za-z0-9_])'
if ([regex]::IsMatch($text, $reservationIdPattern)) {
    throw 'Developer report boundary failed. Internal reservation R-number identifiers are forbidden in user-facing reports; use station/service + programme title + broadcast time.'
}

Write-Host 'Developer report boundary: OK'
