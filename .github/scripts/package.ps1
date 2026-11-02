param([Parameter(Mandatory=$true)][ValidateSet('x64','x86')][string]$Platform,[Parameter(Mandatory=$true)][string]$RefName)
$ErrorActionPreference='Stop'
$spec=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package-spec.json') -Raw|ConvertFrom-Json
if($RefName.StartsWith('v') -and $RefName -cne "v$($spec.Version)"){throw 'Tag/version mismatch'}
if((Get-FileHash README.md).Hash -ne (Get-FileHash README.txt).Hash){throw 'README mismatch'}
$bin=Join-Path "$($spec.Project)/bin/$Platform/Release" $spec.Framework
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
if($spec.Name -eq 'TvAIr'){
 $licenseDir=Join-Path $out 'licenses'
 New-Item -ItemType Directory -Path $licenseDir -Force|Out-Null
 Copy-Item -Path (Join-Path $PSScriptRoot 'licenses/*') -Destination $licenseDir
 $notices=[Collections.Generic.List[string]]::new()
 $notices.Add('TvAIr uses the following NuGet packages. Package licenses and notices are included in licenses/.')
 $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
 foreach($project in @('TvAIr','TvAIrEpgRec','TvAIrPlugin')){
  $assets=Get-Content (Join-Path $project 'obj/project.assets.json') -Raw|ConvertFrom-Json
  foreach($lib in $assets.libraries.PSObject.Properties){
   if($lib.Value.type -ne 'package' -or -not $seen.Add($lib.Name)){continue}
   $folder=$null
   foreach($base in $assets.packageFolders.PSObject.Properties.Name){$candidate=Join-Path $base $lib.Value.path;if(Test-Path -LiteralPath $candidate){$folder=$candidate;break}}
   if(-not $folder){throw "Package missing: $($lib.Name)"}
   $nuspec=Get-ChildItem -LiteralPath $folder -Filter '*.nuspec' -File|Select-Object -First 1
   [xml]$meta=Get-Content -LiteralPath $nuspec.FullName
   $notices.Add("$($lib.Name): $($meta.package.metadata.license.InnerText); $($meta.package.metadata.copyright)")
   $files=@(Get-ChildItem -LiteralPath $folder -File|Where-Object {$_.Name -match '^(LICENSE|NOTICE|THIRD.PARTY.NOTICES)(\..*)?$'})
   foreach($file in $files){Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $licenseDir ($lib.Name.Replace('/','_')+'_'+$file.Name))}
   if(-not $files.Count -and $lib.Name -notmatch '^(SQLitePCLRaw\.|Microsoft.Data.Sqlite)'){throw "Missing license: $($lib.Name)"}
  }
 }
 $notices.Add('SQLitePCLRaw 2.1.6 license/notice: https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.6')
 $notices.Add('Microsoft.Data.Sqlite 8.0.7 license: https://github.com/dotnet/efcore/tree/0d1256be4658567c8a24b4c027bdbb3dbd6de656')
 $notices|Set-Content -LiteralPath (Join-Path $out 'THIRD_PARTY_NOTICES.txt') -Encoding utf8
}
$instructions=if($spec.Name -eq 'TvAIr'){'ZIPを展開したフォルダー一式を配置してTvAIr.exeを起動してください。v1.2.15へ更新後は設定の「TVTest」で「連携するTVTestプラグインフォルダ」を指定して保存してください。既存INIは移動・削除しないでください。READMEの動作環境と初回設定に従ってください。'}else{"TvAIr v1.2.14と同じアーキテクチャを使用してください。TvAIrを終了し、$($spec.Dll)をTvAIr.exeと同じ場所のPluginsフォルダーへ配置してください。"}
@"
$($spec.Name) v$($spec.Version) ($Platform) - ビルド済み配布

Visual Studioによるビルドは不要です。必要なランタイムと利用条件はREADMEをご確認ください。
$instructions
更新時はTvAIrを終了して差し替え、既存の設定・保存データは保持してください。
外部アプリやプラグインが必要な機能はREADMEの条件に従って設定してください。
"@|Set-Content -LiteralPath (Join-Path $out 'INSTALL.txt') -Encoding utf8
Compress-Archive -Path "$out/*" -DestinationPath "package/$($spec.Name)_v$($spec.Version)_$Platform.zip"