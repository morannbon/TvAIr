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

# Keep this script ASCII-only for Windows PowerShell 5.1 compatibility.
# This gate protects the public-facing report-copy boundary from raw developer diagnostics.

$reservations = Read-Utf8 'TvAIr\wwwroot\reservations.html'
$copyFunction = Slice-Between $reservations 'async function copyUserEventReport(btn)' 'async function clearLogReservations(btn)' 'copyUserEventReport'
if ($copyFunction -notmatch [regex]::Escape('/api/user-events/report?hours=24&count=200')) {
    Fail 'Report-copy UI must fetch only /api/user-events/report.'
}
if ($copyFunction -match '/api/log') {
    Fail 'Report-copy UI must never fetch /api/log developer diagnostics.'
}

$program = Read-Utf8 'TvAIr\Program.cs'
$reportRoute = Slice-Between $program 'app.MapGet("/api/user-events/report"' 'app.MapDelete("/api/user-events"' 'user-events report route'
if ($reportRoute -notmatch 'UserEventLogService\s+userEvents') {
    Fail 'Report endpoint must be owned by UserEventLogService.'
}
if ($reportRoute -notmatch 'userEvents\.BuildReportText') {
    Fail 'Report endpoint must use UserEventLogService.BuildReportText.'
}
if ($reportRoute -match 'LogRepository|/api/log|GetRecent\(') {
    Fail 'Report endpoint must not read raw developer diagnostics.'
}

$service = Read-Utf8 'TvAIr\Core\UserEventLogService.cs'
$reportMethod = Slice-Between $service 'public string BuildReportText(TimeSpan window, int maxRows, string tvairVersion)' 'private static UserEventLogEntry[] SelectReportEntries' 'BuildReportText'
if ($reportMethod -notmatch 'GetReportEntries\(') {
    Fail 'BuildReportText must use the user-event report store.'
}
if ($reportMethod -match 'LogRepository|LogEntry|/api/log|USER_MODEL_|NORMALIZATION_LEARNER_|SearchCast|CONTRIBUTOR_RULE_SAMPLE') {
    Fail 'BuildReportText must not depend on raw developer diagnostic sources or preference diagnostics.'
}
if ($reportMethod -notmatch 'SanitizeReportValue') {
    Fail 'BuildReportText must pass report values through the report sanitizer.'
}

$sanitizer = Slice-Between $service 'private static string SanitizeReportValue(string? value)' 'private static void AppendReportLine' 'SanitizeReportValue'
foreach ($required in @('<local-path>', '<email>', '<ip>', '<private>')) {
    if ($sanitizer.IndexOf($required, [System.StringComparison]::Ordinal) -lt 0) {
        Fail "Report sanitizer is missing required privacy token: $required"
    }
}

if ($errors.Count -gt 0) {
    throw ("Report privacy boundary gate failed.`r`n - " + ($errors -join "`r`n - "))
}

Write-Host 'Report privacy boundary gate: OK'
