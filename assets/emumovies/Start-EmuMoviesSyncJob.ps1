# Starts one video-only job in an already open, signed-in EmuMovies Sync 2.71 window.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$System,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$MatchFolder,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$DownloadFolder,
    [ValidateSet('VideoHQ')][string]$Media = 'VideoHQ',
    [ValidateNotNullOrEmpty()][string[]]$Extensions = @('.xml'),
    [ValidateRange(0,1440)][int]$WaitTimeoutMinutes = 0,
    [string]$CancelRequestPath,
    [string]$OwnershipPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$System = $System.Trim()
if ([string]::IsNullOrWhiteSpace($System) -or $System.Contains('|') -or $System -match '[\x00-\x1f]') {
    throw 'System must be an exact non-empty catalog display name from Sync.'
}
$extensionsToMatch = @($Extensions | ForEach-Object {
    $extension = $_.Trim()
    if ($extension -notmatch '^\.[A-Za-z0-9]+$') { throw 'Each Extensions value must be a simple extension including its dot, such as .xml, .zip or .cue.' }
    $extension.ToLowerInvariant()
} | Select-Object -Unique)
if ($extensionsToMatch.Count -eq 0) { throw 'At least one matching-file extension is required.' }
if ($MatchFolder -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+(?:\\|$))' -or $DownloadFolder -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+(?:\\|$))') {
    throw 'MatchFolder and DownloadFolder must be absolute filesystem paths.'
}
$matchItem = Get-Item -LiteralPath $MatchFolder
if (-not $matchItem.PSIsContainer -or $matchItem.PSProvider.Name -ne 'FileSystem') { throw 'MatchFolder must be a filesystem directory.' }
$matchPath = [IO.Path]::GetFullPath($matchItem.FullName).TrimEnd('\')
$downloadPath = [IO.Path]::GetFullPath($DownloadFolder).TrimEnd('\')
foreach ($path in @($matchPath,$downloadPath)) {
    if ($path.Equals([IO.Path]::GetPathRoot($path).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use dedicated input and download directories, not a drive or share root.'
    }
}
if ($downloadPath.Equals($matchPath,[StringComparison]::OrdinalIgnoreCase) -or
    $downloadPath.StartsWith($matchPath+'\',[StringComparison]::OrdinalIgnoreCase) -or
    $matchPath.StartsWith($downloadPath+'\',[StringComparison]::OrdinalIgnoreCase)) {
    throw 'DownloadFolder and MatchFolder must be separate, non-overlapping directories.'
}
$fileCount = @(Get-ChildItem -LiteralPath $matchPath -File | Where-Object { $_.Extension.ToLowerInvariant() -in $extensionsToMatch }).Count
if ($fileCount -eq 0) { throw 'MatchFolder has no top-level files with the configured extensions; refusing an empty or unsuitable input.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function Get-SyncWindow {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'EmuMovies Sync (version 2.71)')
    $lookupDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)
        if ($windows.Count -gt 1) { throw 'Multiple EmuMovies Sync 2.71 windows are open; refusing an ambiguous operation.' }
        if ($windows.Count -eq 1) { return $windows[0] }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $lookupDeadline)
    throw 'EmuMovies Sync (version 2.71) stayed unavailable for five seconds. Open it and sign in manually first.'
}
function Get-Control([string]$Id,[switch]$Optional) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
    $control = (Get-SyncWindow).FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    if ($null -eq $control -and -not $Optional) { throw "Sync control '$Id' is unavailable; check the application window." }
    return $control
}
function Get-Pattern($Control,$PatternId) {
    $pattern = $null
    if (-not $Control.TryGetCurrentPattern($PatternId,[ref]$pattern)) { throw "Required UI Automation pattern is unavailable for '$($Control.Current.AutomationId)'." }
    return $pattern
}
function Get-ComboText([string]$Id) {
    $combo = Get-Control $Id
    $pattern = $null
    if ($combo.TryGetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern,[ref]$pattern)) {
        $selection = $pattern.Current.GetSelection()
        if ($selection.Count -eq 1) { return $selection[0].Current.Name }
    }
    if ($combo.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern,[ref]$pattern)) { return $pattern.Current.Value }
    throw "Cannot verify the selected value of '$Id'."
}
function Select-ComboItem([string]$Id,[string]$Wanted,[switch]$SystemItem) {
    $combo = Get-Control $Id
    if (-not $combo.Current.IsEnabled) { throw "Sync selector '$Id' is disabled; the application may be busy." }
    $expand = $null
    if ($combo.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern,[ref]$expand)) { $expand.Expand() }
    try {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)
        $items = (Get-Control $Id).FindAll([Windows.Automation.TreeScope]::Descendants,$condition)
        $matches = @($items | Where-Object {
            $name = $_.Current.Name
            if ($SystemItem) { $name = ($name -split '\|\|',2)[0].Trim() }
            $name -ceq $Wanted
        })
        if ($matches.Count -ne 1) { throw "Expected one '$Wanted' item in '$Id'; sign in and let Sync finish loading its catalogs." }
        (Get-Pattern $matches[0] ([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    } finally {
        $fresh = Get-Control $Id
        $collapse = $null
        if ($fresh.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern,[ref]$collapse)) { $collapse.Collapse() }
    }
}
function Set-FolderValue([string]$Id,[string]$Path) {
    $control = Get-Control $Id
    if (-not $control.Current.IsEnabled) { throw "Sync path field '$Id' is disabled." }
    (Get-Pattern $control ([Windows.Automation.ValuePattern]::Pattern)).SetValue($Path)
}
function Assert-Ready {
    if (-not (Get-Control 'Button3').Current.IsEnabled) { throw 'Sync is busy or Go is unavailable. No new job was started.' }
}
function Get-JobStatus {
    $result = [ordered]@{}
    foreach ($id in @('lblStatus','lblStatusDownload','lblStatusTotal')) {
        $control = Get-Control $id -Optional
        $result[$id] = if ($null -eq $control) { '' } else { $control.Current.Name }
    }
    $result['IsBusy'] = -not (Get-Control 'Button3').Current.IsEnabled
    return [pscustomobject]$result
}
# Selecting the Download tab leaves all existing options unchanged.
$tabCondition = [Windows.Automation.AndCondition]::new(
    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::TabItem),
    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Download'))
$tab = (Get-SyncWindow).FindFirst([Windows.Automation.TreeScope]::Descendants,$tabCondition)
if ($null -eq $tab) { throw 'The Download tab is unavailable.' }
(Get-Pattern $tab ([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
Assert-Ready
[void][IO.Directory]::CreateDirectory($downloadPath)
Select-ComboItem 'DropSystem' $System -SystemItem
Start-Sleep -Milliseconds 250
Select-ComboItem 'DropMedia' 'Video_MP4_HI_QUAL'
Set-FolderValue 'txtFolderRom' $matchPath
Set-FolderValue 'txtFolderDownload' $downloadPath
# Fresh reads prevent a stale selection or incomplete path edit from starting the wrong job.
Assert-Ready
if (((Get-ComboText 'DropSystem') -split '\|\|',2)[0].Trim() -cne $System) { throw 'Selected system verification failed.' }
if ((Get-ComboText 'DropMedia').Trim() -cne 'Video_MP4_HI_QUAL') { throw 'Selected HQ video type verification failed.' }
foreach ($pair in @(@('txtFolderRom',$matchPath),@('txtFolderDownload',$downloadPath))) {
    $value = (Get-Pattern (Get-Control $pair[0]) ([Windows.Automation.ValuePattern]::Pattern)).Current.Value
    if (-not [IO.Path]::GetFullPath($value).TrimEnd('\').Equals($pair[1],[StringComparison]::OrdinalIgnoreCase)) { throw "Path verification failed for '$($pair[0])'." }
}
function Test-CancelRequested { return $CancelRequestPath -and (Test-Path -LiteralPath $CancelRequestPath -PathType Leaf) }
function Save-Ownership([string]$State) {
    if ($OwnershipPath) {
        [pscustomobject]@{ ProcessId=$syncProcessId; System=$System; MatchFolder=$matchPath; DownloadFolder=$downloadPath; State=$State; StartedUtc=$started.ToString('o') } |
            ConvertTo-Json -Compress | Set-Content -LiteralPath $OwnershipPath -Encoding UTF8
    }
}
function Stop-OwnedJob {
    if ((Get-SyncWindow).Current.ProcessId -ne $syncProcessId) { throw 'Sync process changed; refusing to cancel an unrelated job.' }
    if (((Get-ComboText 'DropSystem') -split '\|\|',2)[0].Trim() -cne $System) { throw 'Sync catalog changed; cancellation ownership is uncertain.' }
    foreach ($pair in @(@('txtFolderRom',$matchPath),@('txtFolderDownload',$downloadPath))) {
        $value = (Get-Pattern (Get-Control $pair[0]) ([Windows.Automation.ValuePattern]::Pattern)).Current.Value
        if (-not [IO.Path]::GetFullPath($value).TrimEnd('\').Equals($pair[1],[StringComparison]::OrdinalIgnoreCase)) { throw 'Sync paths changed; cancellation ownership is uncertain.' }
    }
    $current = Get-JobStatus
    if ($current.IsBusy) {
        $cancel = Get-Control 'CmdCancel'
        if ($cancel.Current.Name -cne 'Cancel' -or -not $cancel.Current.IsEnabled) { throw 'Owned Sync job could not be cancelled; use Cancel in Sync before starting another run.' }
        (Get-Pattern $cancel ([Windows.Automation.InvokePattern]::Pattern)).Invoke()
        $cancelDeadline = [DateTime]::UtcNow.AddSeconds(60)
        do {
            Start-Sleep -Milliseconds 200
            $current = Get-JobStatus
            if (-not $current.IsBusy) { Save-Ownership 'Cancelled'; throw [OperationCanceledException]::new('Owned Sync video job cancelled and stopped.') }
        } while ([DateTime]::UtcNow -lt $cancelDeadline)
        throw 'Cancel was sent, but Sync has not stopped. Check Sync before starting another run.'
    }
    Save-Ownership 'Cancelled'
    throw [OperationCanceledException]::new('Sync video run cancelled; Sync is idle.')
}
if (Test-CancelRequested) { throw [OperationCanceledException]::new('Cancelled before starting Sync.') }
$started = [DateTime]::UtcNow
$syncProcessId = (Get-SyncWindow).Current.ProcessId
$beforeStatus = Get-JobStatus
Save-Ownership 'Starting'
(Get-Pattern (Get-Control 'Button3') ([Windows.Automation.InvokePattern]::Pattern)).Invoke()
$observedStart = $false
$lastProgress = ''
$startDeadline = $started.AddSeconds(20)
$deadline = if ($WaitTimeoutMinutes -gt 0) { $started.AddMinutes($WaitTimeoutMinutes) } else { $startDeadline }
do {
    $status = Get-JobStatus
    if ($status.IsBusy -or ($status.lblStatus -ne $beforeStatus.lblStatus -and $status.lblStatus.Trim() -ine 'Complete')) {
        if (-not $observedStart) { $observedStart = $true; Save-Ownership 'Running' }
    }
    if (Test-CancelRequested) { Stop-OwnedJob }
    $progressText = "$($status.lblStatus) | $($status.lblStatusDownload) | $($status.lblStatusTotal)"
    if ($progressText -ne $lastProgress) {
        Write-Host ('EMUMOVIES_PROGRESS ' + ([pscustomobject]@{ Status=$status.lblStatus; Download=$status.lblStatusDownload; Total=$status.lblStatusTotal } | ConvertTo-Json -Compress))
        $lastProgress = $progressText
    }
    if ($observedStart -and $WaitTimeoutMinutes -eq 0) {
        [pscustomobject]@{ State='Started'; System=$System; Media='Video_MP4_HI_QUAL'; InputFileCount=$fileCount; MatchFolder=$matchPath; DownloadFolder=$downloadPath; StartedUtc=$started }
        return
    }
    if ($observedStart -and -not $status.IsBusy -and $status.lblStatus.Trim() -ieq 'Complete') {
        Save-Ownership 'Complete'
        [pscustomobject]@{ State='Complete'; System=$System; Media='Video_MP4_HI_QUAL'; InputFileCount=$fileCount; DownloadFolder=$downloadPath; Status=$status.lblStatus; Progress=$status.lblStatusTotal }
        return
    }
    if (-not $observedStart -and [DateTime]::UtcNow -ge $startDeadline) {
        Save-Ownership 'Indeterminate'
        throw 'No new Sync job start was observed; an old Complete status is not accepted. Inspect Sync before retrying.'
    }
    if ($observedStart -and -not $status.IsBusy -and ([DateTime]::UtcNow-$started).TotalSeconds -gt 5) {
        Save-Ownership 'Failed'
        throw "Sync stopped without Complete: $($status.lblStatus). No follow-up job was started."
    }
    Start-Sleep -Milliseconds 200
} while ([DateTime]::UtcNow -lt $deadline)
Save-Ownership 'TimedOut'
throw "Wait timed out; Sync may still be running. No cancellation was sent. Status: $($status.lblStatus); $($status.lblStatusDownload); $($status.lblStatusTotal)"


