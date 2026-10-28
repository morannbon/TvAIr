param([Parameter(Mandatory=$true)][ValidateSet('x64','x86')][string]$Platform,[Parameter(Mandatory=$true)][string]$RefName)
$ErrorActionPreference='Stop'
$spec=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package-spec.json') -Raw|ConvertFrom-Json
if($RefName.StartsWith('v') -and $RefName -cne "v$($spec.Version)"){throw 'Tag/version mismatch'}
if((Get-FileHash README.md).Hash -ne (Get-FileHash README.txt).Hash){throw 'README mismatch'}
$bin=Join-Path "$($spec.Project)/bin/$Platform/Release" $spec.Framework
$bin=(Resolve-Path -LiteralPath $bin).Path
$bin=(Resolve-Path -LiteralPath $bin).Path
$dll=Join-Path $bin $spec.Dll
if(-not(Test-Path -LiteralPath $dll)){throw 'Required DLL missing'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($dll)).FileVersion -ne "$($spec.Version).0"){throw 'Binary version mismatch'}
$out="package/$($spec.Name)_v$($spec.Version)_$Platform"
$out=Join-Path (Get-Location).Path $out
if(Test-Path -LiteralPath $out){throw 'Use clean package staging'}
New-Item -ItemType Directory -Path $out -Force|Out-Null
if($spec.Name -eq 'TvAIr'){
 foreach($f in @('TvAIr.exe','TvAIr.dll','TvAIrPlugin.dll','TvAIrEpgRec.exe','TvAIrEpgRec.dll','AribDecodeBridge.dll','wwwroot/index.html')){if(-not(Test-Path -LiteralPath (Join-Path $bin $f))){throw "Required output missing: $f"}}
 $base=[IO.Path]::GetFullPath($bin)
 Get-ChildItem -LiteralPath $base -File -Recurse|ForEach-Object{
  $rel=[IO.Path]::GetRelativePath($base,$_.FullName)
  $include=$_.Extension -notin @('.pdb','.lib','.exp','.log','.tmp','.bak','.xml')
  if($rel -match '^runtimes[\\/]([^\\/]+)[\\/]'){$include=$include -and ($Matches[1] -in @("win-$Platform",'win'))}
  if($rel -match '\.staticwebassets\.runtime\.json$'){$include=$false}
  if($include){$to=Join-Path $out $rel;New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($to))) -Force|Out-Null;Copy-Item -LiteralPath $_.FullName -Destination $to}
 }
}else{Copy-Item -LiteralPath $dll -Destination $out}
foreach($doc in @('README.md','README.txt','LICENSE')){Copy-Item -LiteralPath $doc -Destination $out}
foreach($doc in @('THIRD_PARTY_NOTICES.txt','SDK_LICENSE.txt')){if(Test-Path -LiteralPath $doc){Copy-Item -LiteralPath $doc -Destination $out}}
if($spec.Name -eq 'AI-rhythm'){Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SDK_LICENSE.txt') -Destination $out}
$instructions=if($spec.Name -eq 'TvAIr'){'ZIPを展開したフォルダー一式を配置してTvAIr.exeを起動してください。READMEの動作環境と初回設定に従ってください。'}else{"TvAIr v1.2.14と同じアーキテクチャを使用してください。TvAIrを終了し、$($spec.Dll)をTvAIr.exeと同じ場所のPluginsフォルダーへ配置してください。"}
@"
$($spec.Name) v$($spec.Version) ($Platform) - ビルド済み配布

Visual Studioによるビルドは不要です。必要なランタイムと利用条件はREADMEをご確認ください。
$instructions
更新時はTvAIrを終了して差し替え、既存の設定・保存データは保持してください。
外部アプリやプラグインが必要な機能はREADMEの条件に従って設定してください。
"@|Set-Content -LiteralPath (Join-Path $out 'INSTALL.txt') -Encoding utf8
Compress-Archive -Path "$out/*" -DestinationPath "package/$($spec.Name)_v$($spec.Version)_$Platform.zip"