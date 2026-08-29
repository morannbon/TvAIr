[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $root 'Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8
$group = $props.Project.PropertyGroup | Select-Object -First 1

$product = [string]$group.TvAIrProductVersion
$fileVersion = [string]$group.TvAIrFileVersion
$assemblyVersion = [string]$group.TvAIrAssemblyVersion
$sdk = [string]$group.TvAIrPluginSdkVersion
$hostContract = [string]$group.TvAIrPluginHostContractVersion
$minimumHost = [string]$group.TvAIrMinimumSupportedPluginHostContractVersion
$compatibilityMajor = [string]$group.TvAIrPluginCompatibilityMajor

$errors = [System.Collections.Generic.List[string]]::new()
function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { $errors.Add($message) }
}
function Read-Text([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path)) {
        $errors.Add("Missing file: $relativePath")
        return ''
    }
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8
}

Assert-True ($product -match '^\d+\.\d+\.\d+$') "Product version must use stable x.y.z format: $product"
Assert-True ($fileVersion -eq "$product.0") "TvAIrFileVersion does not match product version: $fileVersion"
Assert-True ($assemblyVersion -eq "$product.0") "TvAIrAssemblyVersion does not match product version: $assemblyVersion"
Assert-True ($product -notmatch '(?i)(alpha|beta|preview|rc)') "Prerelease label remains in product version: $product"

$versionContract = Read-Text 'TvAIr\Core\TvAIrVersionContract.generated.cs'
Assert-True ($versionContract.Contains("ProductVersion = `"$product`"")) 'TvAIrVersionContract.ProductVersion does not match source of truth.'
Assert-True ($versionContract.Contains("PluginSdkVersion = `"$sdk`"")) 'TvAIrVersionContract.PluginSdkVersion does not match source of truth.'
Assert-True ($versionContract.Contains("PluginHostContractVersion = `"$hostContract`"")) 'TvAIrVersionContract.PluginHostContractVersion does not match source of truth.'
Assert-True ($versionContract.Contains("MinimumSupportedPluginHostContractVersion = `"$minimumHost`"")) 'Minimum host contract version does not match source of truth.'
Assert-True ($versionContract.Contains("PluginCompatibilityMajor = $compatibilityMajor")) 'Compatibility major does not match source of truth.'

$sdkContract = Read-Text 'TvAIrPlugin\TvAIrPluginSdkContract.generated.cs'
Assert-True ($sdkContract.Contains("SdkVersion = `"$sdk`"")) 'TvAIrPluginSdkContract.SdkVersion does not match source of truth.'
Assert-True ($sdkContract.Contains("HostContractVersion = `"$hostContract`"")) 'TvAIrPluginSdkContract.HostContractVersion does not match source of truth.'
Assert-True ($sdkContract.Contains("MinimumSupportedHostContractVersion = `"$minimumHost`"")) 'SDKMinimum host contract version does not match source of truth.'
Assert-True ($sdkContract.Contains("CompatibilityMajor = $compatibilityMajor")) 'SDKCompatibility major does not match source of truth.'

$readmeMd = Read-Text 'README.md'
$readmeTxt = Read-Text 'README.txt'
$releaseNotes = Read-Text 'RELEASE_NOTES.txt'
Assert-True ($readmeMd -match "(?m)^# TvAIr $([regex]::Escape($product))$") 'README.md product version does not match.'
Assert-True ($readmeTxt -match "(?m)^TvAIr $([regex]::Escape($product)) README$") 'README.txt product version does not match.'
Assert-True ($releaseNotes -match "(?m)^TvAIr $([regex]::Escape($product))$") 'RELEASE_NOTES.txt product version does not match.'

$projectFiles = @('TvAIr\TvAIr.csproj', 'TvAIrEpgRec\TvAIrEpgRec.csproj', 'TvAIrPlugin\TvAIrPlugin.csproj')
foreach ($relativePath in $projectFiles) {
    $text = Read-Text $relativePath
    Assert-True ($text -notmatch '<Version>\d+\.\d+\.\d+') "$relativePath contains a hard-coded product version."
    Assert-True ($text -notmatch '<FileVersion>\d+\.\d+\.\d+') "$relativePath contains a hard-coded FileVersion."
    Assert-True ($text -notmatch '<AssemblyVersion>\d+\.\d+\.\d+') "$relativePath contains a hard-coded AssemblyVersion."
}

$webFiles = Get-ChildItem -LiteralPath (Join-Path $root 'TvAIr\wwwroot') -Recurse -File |
    Where-Object { $_.Extension -in '.html', '.js', '.css' }
$projectionFiles = @($webFiles.FullName) + (Join-Path $root 'TvAIr\Program.cs')
foreach ($path in $projectionFiles) {
    $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    foreach ($match in [regex]::Matches($text, '\?v=(\d+\.\d+\.\d+)')) {
        if ($match.Groups[1].Value -ne $product) {
            $errors.Add("Web asset version does not match product version: $path -> $($match.Value)")
            break
        }
    }
}

$index = Read-Text 'TvAIr\wwwroot\index.html'
$settingsHost = Read-Text 'TvAIr\wwwroot\tvair-settings-host.js'
Assert-True ($index.Contains("TVA_IR_UI_VERSION=`"$product`"")) 'TVA_IR_UI_VERSION does not match product version.'
Assert-True ($index.Contains("version:'$product'")) 'TvAIrSettingsModule.version does not match product version.'
Assert-True ($settingsHost.Contains("version:'$product'")) 'TvAIrSettingsHost.version does not match product version.'

if ($errors.Count -gt 0) {
    $message = "TvAIr version consistency check failed.`r`n - " + ($errors -join "`r`n - ")
    throw $message
}

Write-Host "TvAIr version consistency: OK product=$product sdk=$sdk host=$hostContract"
