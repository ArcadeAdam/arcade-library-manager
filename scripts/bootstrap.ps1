$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$required=(Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$meta=Invoke-RestMethod ('https://builds.dotnet.microsoft.com/dotnet/release-metadata/'+(($required -split '\.')[0])+'.0/releases.json')
$sdk=$meta.releases | ForEach-Object { @($_.sdks)+@($_.sdk) } | Where-Object {$_.version -eq $required} | Select-Object -First 1
$asset=$sdk.files | Where-Object {$_.rid -eq 'win-x64' -and $_.name -like '*.zip'} | Select-Object -First 1
if(-not $asset){throw ('Official manifest has no Windows x64 SDK '+$required)}
$destination=Join-Path $projectRoot '.tools\dotnet'
if(-not(Test-Path -LiteralPath (Join-Path $destination 'dotnet.exe'))){
 New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot '.tools') | Out-Null
 $zip=Join-Path $projectRoot '.tools\dotnet-sdk.zip'
 Invoke-WebRequest -Uri $asset.url -OutFile $zip
 if((Get-FileHash -LiteralPath $zip -Algorithm SHA512).Hash -ne $asset.hash){throw 'SDK checksum mismatch'}
 Expand-Archive -LiteralPath $zip -DestinationPath $destination -Force
}
& (Join-Path $destination 'dotnet.exe') --version
