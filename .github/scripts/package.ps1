param(
    [Parameter(Mandatory = $true)]
    [string]$Platform,

    [Parameter(Mandatory = $true)]
    [string]$RefName
)

$ErrorActionPreference = "Stop"

[xml]$props = Get-Content "Directory.Build.props"
$version = [string]$props.Project.PropertyGroup.TvAIrProductVersion

$out = "package\TvAIr_v${version}_${Platform}"
$source = "TvAIr\bin\${Platform}\Release\net8.0-windows"
$zip = "package\TvAIr_v${version}_${Platform}.zip"

if (-not (Test-Path $source)) {
    throw "Build output not found: $source"
}

New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item "$source\*" $out -Recurse -Force

Copy-Item "README.md" $out
Copy-Item "README.txt" $out
Copy-Item "LICENSE" $out
Copy-Item "Regex_Manual.txt" $out

Get-ChildItem $out -Recurse -Include *.pdb,*.lib,*.exp |
    Remove-Item -Force

$runtimeRoot = Join-Path $out "runtimes"
if (Test-Path $runtimeRoot) {
    $keep = "win-$Platform"

    Get-ChildItem $runtimeRoot -Directory |
        Where-Object {
            $_.Name -ne $keep -and
            $_.Name -ne "win"
        } |
        Remove-Item -Recurse -Force
}

Compress-Archive -Path "$out\*" -DestinationPath $zip -Force
