param(
    [Parameter(Mandatory = $true)][ValidateSet('x64','x86')][string]$Platform,
    [Parameter(Mandatory = $true)][string]$RefName
)
$ErrorActionPreference = 'Stop'
[xml]$props = Get-Content -LiteralPath 'Directory.Build.props'
$version = [string]$props.Project.PropertyGroup.TvAIrProductVersion
if ($RefName.StartsWith('v') -and $RefName -ne "v$version") { throw 'Tag and product version differ.' }
$source = "TvAIr/bin/$Platform/Release/net8.0-windows"
foreach ($required in @('TvAIr.exe','TvAIr.dll','TvAIrPlugin.dll','AribDecodeBridge.dll','TvAIrEpgRec.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $source $required))) { throw "Required build output missing: $required" }
}
if ((Get-FileHash README.md).Hash -ne (Get-FileHash README.txt).Hash) { throw 'README files differ.' }
$out = "package/TvAIr_v${version}_${Platform}"
if (Test-Path -LiteralPath $out) { throw 'Package staging directory already exists. Use a clean checkout.' }
New-Item -ItemType Directory -Path $out -Force | Out-Null
$sourceRoot = [IO.Path]::GetFullPath($source)
Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | ForEach-Object {
    $relative = $_.FullName.Substring($sourceRoot.Length + 1)
    $include = $_.Extension -notin @('.pdb','.lib','.exp','.log','.tmp','.bak')
    if ($relative -match '^runtimes[\\/]([^\\/]+)[\\/]') { $include = $include -and ($Matches[1] -in @("win-$Platform",'win')) }
    if ($include) {
        $destination = Join-Path $out $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($destination))) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
}
foreach ($doc in @('README.md','README.txt','LICENSE')) { Copy-Item -LiteralPath $doc -Destination $out }
@"
TvAIr v$version ($Platform) - ビルド済み配布

このZIPはビルド済みです。Visual Studioによるビルドは不要です。
READMEの動作環境を確認し、ZIPを展開したフォルダー一式を配置してください。
TvAIr.exeを起動し、READMEのインストール方法の設定・初回確認を行ってください。
更新時はTvAIrを終了し、既存の設定・データを保持してアプリ本体を更新してください。
ソースからのビルド手順はREADMEに記載しています。
"@ | Set-Content -LiteralPath (Join-Path $out 'INSTALL.txt') -Encoding UTF8
Compress-Archive -Path "$out/*" -DestinationPath "package/TvAIr_v${version}_${Platform}.zip"
