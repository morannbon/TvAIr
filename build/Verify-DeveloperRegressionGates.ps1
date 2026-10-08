[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$gateScripts = @(
    'Verify-EnvironmentIndependentContent.ps1',
    'Verify-ReportPrivacyBoundary.ps1',
    'Verify-ProgramGuideViewportOwnership.ps1',
    'Verify-AssistantReportingBoundary.ps1'
)

foreach ($gateScript in $gateScripts) {
    $path = Join-Path $PSScriptRoot $gateScript
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Developer regression gate is missing: $gateScript"
    }
    & $path
}

Write-Host ('Developer regression gates: OK count=' + $gateScripts.Count)
