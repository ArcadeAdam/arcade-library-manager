[CmdletBinding()]
param([switch]$SkipTests,[string]$FfmpegPath='',[switch]$SkipPackage)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
Push-Location $projectRoot
try {
 $dotnet=Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
 if(-not(Test-Path -LiteralPath $dotnet)) {
  $command=Get-Command dotnet -ErrorAction SilentlyContinue
  if(-not $command){throw 'Install the .NET SDK version in global.json or run scripts\bootstrap.ps1.'}
  $dotnet=$command.Source
 }
 if($FfmpegPath){$env:ALM_TEST_FFMPEG=[IO.Path]::GetFullPath($FfmpegPath)}
 if(-not $SkipTests){
  $testRun=Join-Path $projectRoot ('artifacts\tests\'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
  & $dotnet run --project '.\tests\ArcadeLibraryManager.Tests\ArcadeLibraryManager.Tests.csproj' -c Release -- $testRun
  if($LASTEXITCODE -ne 0){throw 'Integration checks failed.'}
 }
 $version=([xml](Get-Content -LiteralPath '.\src\ArcadeLibraryManager.Desktop\ArcadeLibraryManager.Desktop.csproj' -Raw)).Project.PropertyGroup.Version
 $name='ArcadeLibraryManager-'+$version+'-win-x64'
 $output=Join-Path $projectRoot ('artifacts\'+$name)
 # Never reuse a user-modified portable folder containing settings or credentials.
 if(Test-Path -LiteralPath (Join-Path $output 'data')){throw 'Publish folder contains user data. Move that portable installation aside before publishing.'}
 & $dotnet publish '.\src\ArcadeLibraryManager.Desktop\ArcadeLibraryManager.Desktop.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $output
 if($LASTEXITCODE -ne 0){throw 'Portable publish failed.'}
 Copy-Item -LiteralPath '.\README.md' -Destination $output -Force
 $publishedDocs=Join-Path $output 'docs'
 New-Item -ItemType Directory -Path $publishedDocs -Force | Out-Null
 Get-ChildItem -LiteralPath '.\docs' -File | Where-Object { $_.Extension -in @('.md','.txt') } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $publishedDocs -Force }
 if(Test-Path -LiteralPath '.\docs\licenses'){Copy-Item -LiteralPath '.\docs\licenses' -Destination $publishedDocs -Recurse -Force}
 $app=Join-Path $output 'ArcadeLibraryManager.exe'
 $settingsCheck=Start-Process -FilePath $app -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
 if($settingsCheck.ExitCode -ne 0){throw 'Published application settings check failed.'}
 $uiOutput=Join-Path $projectRoot 'artifacts\release-smoke\desktop.png'
 $uiCheck=Start-Process -FilePath $app -ArgumentList ('--smoke-test "'+$uiOutput+'"') -WindowStyle Hidden -Wait -PassThru
 if($uiCheck.ExitCode -ne 0){throw 'Published application UI check failed.'}
 if(-not $SkipPackage){
  $zip=Join-Path $projectRoot ('artifacts\'+$name+'.zip')
  Compress-Archive -LiteralPath $output -DestinationPath $zip -CompressionLevel Optimal -Force
  $hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  [IO.File]::WriteAllText($zip+'.sha256',$hash+'  '+[IO.Path]::GetFileName($zip)+[Environment]::NewLine)
  $sourcePayload=Join-Path $projectRoot ('artifacts\source-staging\'+$version+'-'+[guid]::NewGuid().ToString('N')+'\ArcadeLibraryManager-source')
  New-Item -ItemType Directory -Path $sourcePayload -Force | Out-Null
  $sourceFiles=@(Get-Item -LiteralPath '.\README.md','.\global.json','.\.gitignore')
  $sourceFiles+=Get-ChildItem -LiteralPath '.\src','.\tests' -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj|artifacts|\.artifacts)\\' -and $_.Extension -in @('.cs','.csproj','.ico') }
  $sourceFiles+=Get-ChildItem -LiteralPath '.\scripts','.\assets','.\docs' -Recurse -File | Where-Object { $_.FullName -notmatch '\\(__pycache__|\.artifacts)\\' }
  foreach($sourceFile in $sourceFiles) {
   $relative=[IO.Path]::GetRelativePath($projectRoot,$sourceFile.FullName)
   if($relative.StartsWith('..') -or [IO.Path]::IsPathRooted($relative)){throw 'Source file resolved outside the project.'}
   $target=Join-Path $sourcePayload $relative
   New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
   Copy-Item -LiteralPath $sourceFile.FullName -Destination $target
  }
  $sourceZip=Join-Path $projectRoot ('artifacts\ArcadeLibraryManager-'+$version+'-source.zip')
  Compress-Archive -LiteralPath $sourcePayload -DestinationPath $sourceZip -Force -CompressionLevel Optimal
  $sourceHash=(Get-FileHash -LiteralPath $sourceZip -Algorithm SHA256).Hash.ToLowerInvariant()
  [IO.File]::WriteAllText($sourceZip+'.sha256',$sourceHash+'  '+[IO.Path]::GetFileName($sourceZip)+[Environment]::NewLine)
  Write-Host ('Portable ZIP: '+$zip)
  Write-Host ('Source ZIP: '+$sourceZip)
 }
 Write-Host 'Portable build and UI checks passed.'
} finally {Pop-Location}




