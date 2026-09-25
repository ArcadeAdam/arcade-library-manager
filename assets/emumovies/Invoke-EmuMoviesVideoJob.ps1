[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$config=Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
function Say([string]$Message) { Write-Host ('EMUMOVIES_PROGRESS '+([pscustomobject]@{Status=$Message;Download='';Total=''}|ConvertTo-Json -Compress)) }
function Videos {
    if (-not (Test-Path -LiteralPath $config.DownloadFolder -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $config.DownloadFolder -File -Recurse | Where-Object {
        $_.Extension -in @('.mp4','.m4v','.mkv','.webm','.avi','.wmv','.mov') -and $_.FullName -match '[\\/]Video_MP4_HI_QUAL[\\/]'
    })
}
function Check-Cancel { if (Test-Path -LiteralPath $config.CancelRequestPath) { throw [OperationCanceledException]::new('Video run cancelled.') } }
[void][IO.Directory]::CreateDirectory($config.ReportDirectory)
[void][IO.Directory]::CreateDirectory($config.DownloadFolder)
$before=@{}
foreach($file in @(Videos)) { $before[$file.FullName]="$($file.Length):$($file.LastWriteTimeUtc.Ticks)" }
Check-Cancel
if ($config.Download) {
    Say "Starting $($config.System) HQ videos."
    $syncResult = & (Join-Path $PSScriptRoot 'Start-EmuMoviesSyncJob.ps1') -System $config.System -MatchFolder $config.MatchFolder -DownloadFolder $config.DownloadFolder -WaitTimeoutMinutes 1440 -CancelRequestPath $config.CancelRequestPath -OwnershipPath $config.OwnershipPath
    if ($syncResult.State -ne 'Complete') { throw 'Sync did not complete this video job; import was not started.' }
}
Check-Cancel
$videos=@(Videos)
$newCount=@($videos | Where-Object { -not $before.ContainsKey($_.FullName) -or $before[$_.FullName] -ne "$($_.Length):$($_.LastWriteTimeUtc.Ticks)" }).Count
$stems=@{}
foreach($video in $videos) { if($video.Length -gt 0) { $stems[$video.BaseName]=$true } }
$inputs=@(Import-Csv -LiteralPath $config.IncludeStemsPath)
$missing=@($inputs | Where-Object { -not $stems.ContainsKey($_.RomStem) } | ForEach-Object { [pscustomobject]@{RomStem=$_.RomStem;Catalog=$config.System;Reason='No staged HQ video for this input stem'} })
if($missing.Count) { $missing | Export-Csv -LiteralPath (Join-Path $config.ReportDirectory 'Missing.csv') -NoTypeInformation -Encoding UTF8 }
else { '"RomStem","Catalog","Reason"' | Set-Content -LiteralPath (Join-Path $config.ReportDirectory 'Missing.csv') -Encoding UTF8 }
$result=[ordered]@{Downloaded=$newCount;Imported=0;Skipped=0;Missing=$missing.Count;ReportDirectory=$config.ReportDirectory;Details=@()}
$mapping=@{}
foreach($dir in @(Get-ChildItem -LiteralPath $config.DownloadFolder -Directory -Recurse | Where-Object Name -eq 'Video_MP4_HI_QUAL')) {
    $relative=$dir.FullName.Substring($config.DownloadFolder.TrimEnd('\').Length+1)
    $mapping[$relative]='Video'
}
if($mapping.Count -gt 0 -and $videos.Count -gt 0) {
    $sourceRoot=$config.DownloadFolder
    if($mapping.Count -eq 1) {
        $sourceRoot=Join-Path $config.DownloadFolder @($mapping.Keys)[0]
        $mapping=@{'.'='Video'}
    }
    $mappingPath=Join-Path $config.ReportDirectory 'VideoMapping.json'
    $mapping | ConvertTo-Json | Set-Content -LiteralPath $mappingPath -Encoding UTF8
    $args=@{SourceRoot=$sourceRoot;Catalog=$config.System;MappingPath=$mappingPath;LaunchBoxRoot=$config.LaunchBoxRoot;PlatformName=$config.PlatformName;ProfilesRoot=$config.ProfilesRoot;IncludeStemsPath=$config.IncludeStemsPath;ReportRoot=(Join-Path $config.ReportDirectory 'Import');VideoOnly=$true;CancelRequestPath=$config.CancelRequestPath}
    if($config.VerifiedStemsPath) { $args.VerifiedArcadeStemsPath=$config.VerifiedStemsPath }
    if($config.Import) { $args.Apply=$true }
    Say $(if($config.Import){'Importing missing videos without overwriting existing media.'}else{'Planning staged video import; no library media will be copied.'})
    Check-Cancel
    $summary = & (Join-Path $PSScriptRoot 'Import-EmuMoviesMedia.ps1') @args
    $result.Imported=$summary.Copied
    $result.Skipped=$summary.Skipped+$summary.Quarantined
    $result.Details+= "Planned: $($summary.Planned); copied: $($summary.Copied); existing/skipped: $($summary.Skipped); quarantined: $($summary.Quarantined)."
} else { $result.Details+='No staged HQ videos are available; see Missing.csv.' }
$result.Details+="Catalog: $($config.System); matching inputs: $($inputs.Count); staged videos: $($videos.Count)."
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $config.ResultPath -Encoding UTF8
Say 'Video job report saved.'


