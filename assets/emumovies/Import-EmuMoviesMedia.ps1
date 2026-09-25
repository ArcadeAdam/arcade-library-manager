#requires -Version 5.1
<#
.SYNOPSIS
Plans or imports staged EmuMovies Sync media for a LaunchBox platform. Never downloads media.
.DESCRIPTION
MappingPath is a JSON object: relative source subfolder -> exact LaunchBox media type.
No media is copied unless -Apply is supplied. Unknown files remain in the source and
are listed in Quarantine.csv. TeknoParrot Arcade/MAME fallback imports require a
reviewed CSV with a RomStem column. Use -VideoOnly for the current video workflow.
Filenames renamed by Sync do not prove correct game matches.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$Catalog,
    [Parameter(Mandatory = $true)][string]$MappingPath,
    [string]$LaunchBoxRoot = 'N:\LaunchBox',
    [string]$PlatformName = 'TeknoParrot',
    [string]$ProfilesRoot,
    [string]$IncludeStemsPath,
    [string]$VerifiedArcadeStemsPath,
    [string]$ReportRoot,
    [switch]$Apply,
    [string]$CancelRequestPath,
    [switch]$VideoOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:knownIdSuffixes = @{}
if ($Catalog -eq 'ArcadePC') { $Catalog = 'Arcade_PC' }
if ([string]::IsNullOrWhiteSpace($Catalog)) { throw 'Catalog must not be blank.' }
if ([string]::IsNullOrWhiteSpace($PlatformName) -or
    $PlatformName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
    $PlatformName -eq '.' -or $PlatformName -eq '..' -or
    $PlatformName.EndsWith('.') -or $PlatformName.EndsWith(' ')) {
    throw 'PlatformName must be an exact, valid LaunchBox platform name and data filename.'
}
$requiresArcadeVerification = $PlatformName -ieq 'TeknoParrot' -and $Catalog -in @('Arcade', 'MAME')
if (-not $PSBoundParameters.ContainsKey('ProfilesRoot') -and $PlatformName -ieq 'TeknoParrot') {
    $ProfilesRoot = 'N:\Emulators\TeknoParrot\UserProfiles'
}

function Get-FullPath([string]$Path, [string]$Base) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'An empty path is not allowed.' }
    if (-not [IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $Base $Path }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}
function Test-Within([string]$Path, [string]$Root) {
    return $Path.Equals($Root, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}
function Assert-NoReparse([string]$Path) {
    $current = $Path
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points (links/junctions) are not accepted: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
function Read-SafeXml([string]$Path) {
    Assert-NoReparse $Path
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    } finally { $reader.Dispose() }
}
function Get-ChildText($Node, [string]$Name) {
    $child = $Node.SelectSingleNode($Name)
    if ($null -eq $child) { return '' }
    return [string]$child.InnerText
}
function Get-SafeFiles([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return }
    Assert-NoReparse $Root
    $queue = New-Object 'Collections.Generic.Queue[string]'
    $queue.Enqueue($Root)
    while ($queue.Count -gt 0) {
        foreach ($entry in @(Get-ChildItem -LiteralPath $queue.Dequeue() -Force)) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse point found during enumeration: $($entry.FullName)"
            }
            if ($entry.PSIsContainer) { $queue.Enqueue($entry.FullName) }
            else { $entry }
        }
    }
}
function Get-Key([string]$Text) {
    # Conservative matching for EXISTING files only: punctuation differences cause
    # a skip rather than risking duplicate/overwritten artwork.
    return [regex]::Replace($Text.ToLowerInvariant(), '[^\p{L}\p{Nd}]', '')
}
function Add-Alias($Record, [string]$Text) {
    if (-not [string]::IsNullOrWhiteSpace($Text)) {
        $key = Get-Key $Text
        if ($key) { [void]$Record.Aliases.Add($key) }
    }
}
function Get-Record([string]$Stem) {
    if ([string]::IsNullOrWhiteSpace($Stem) -or $Stem.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $Stem -eq '.' -or $Stem -eq '..' -or $Stem.EndsWith('.') -or $Stem.EndsWith(' ') -or
        $Stem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
        throw "Unsafe ROM/profile stem: $Stem"
    }
    if (-not $script:records.ContainsKey($Stem)) {
        $script:records[$Stem] = [pscustomobject]@{
            Stem = $Stem
            Titles = New-Object 'Collections.Generic.List[string]'
            Aliases = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
            Imported = $false
        }
        Add-Alias $script:records[$Stem] $Stem
    }
    return $script:records[$Stem]
}
function Get-ExistingKeys([string]$Stem) {
    $candidates = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [void]$candidates.Add($Stem)
    # Recognize LaunchBox's -01 image suffix, never an arbitrary sequel number.
    $withoutIndex = [regex]::Replace($Stem, '-[0-9]{2}$', '')
    if ($withoutIndex) { [void]$candidates.Add($withoutIndex) }
    $guid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'
    foreach ($candidate in @($Stem,$withoutIndex)) {
        $withoutGuid = [regex]::Replace($candidate, '(?i)[ _-]+[\[({]?' + $guid + '[\])}]?$', '')
        if ($withoutGuid) { [void]$candidates.Add($withoutGuid) }
    }
    foreach ($candidate in @($Stem,$withoutIndex)) {
        $databaseSuffix = [regex]::Match($candidate, '^(?<title>.+)-(?<id>[0-9]+)$')
        if ($databaseSuffix.Success) {
            $key = (Get-Key $databaseSuffix.Groups['title'].Value) + '|' + $databaseSuffix.Groups['id'].Value
            if ($script:knownIdSuffixes.ContainsKey($key)) { [void]$candidates.Add($databaseSuffix.Groups['title'].Value) }
        }
    }
    foreach ($candidate in $candidates) { Get-Key $candidate }
}
function Find-Existing($Record, $Index) {
    foreach ($alias in $Record.Aliases) {
        if ($Index.ContainsKey($alias)) { return $Index[$alias] }
    }
    return ''
}
function Add-Existing($Index, $File) {
    foreach ($key in @(Get-ExistingKeys $File.BaseName)) {
        if ($key -and -not $Index.ContainsKey($key)) { $Index[$key] = $File.FullName }
    }
}
function Export-Rows($Rows, [string]$Path) {
    $columns = @('PlatformName','Catalog','Status','Reason','RomStem','ImportedGame','MediaType','Source','Destination','ExistingMedia','Bytes')
    if (@($Rows).Count -gt 0) { $Rows | Select-Object $columns | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8 }
    else { ('"' + ($columns -join '","') + '"') | Set-Content -LiteralPath $Path -Encoding UTF8 }
}

$baseDirectory = (Get-Location).ProviderPath
$SourceRoot = Get-FullPath $SourceRoot $baseDirectory
$LaunchBoxRoot = Get-FullPath $LaunchBoxRoot $baseDirectory
if (-not [string]::IsNullOrWhiteSpace($ProfilesRoot)) { $ProfilesRoot = Get-FullPath $ProfilesRoot $baseDirectory }
$MappingPath = Get-FullPath $MappingPath $baseDirectory
foreach ($directory in @($SourceRoot, $LaunchBoxRoot, $ProfilesRoot) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Directory not found: $directory" }
    Assert-NoReparse $directory
}
Assert-NoReparse $MappingPath
if ($Apply -and $requiresArcadeVerification -and [string]::IsNullOrWhiteSpace($VerifiedArcadeStemsPath)) {
    throw 'TeknoParrot Arcade/MAME -Apply requires -VerifiedArcadeStemsPath, a reviewed CSV with a RomStem column. Sync renamed files can be incorrect fuzzy matches.'
}

$platformXml = Read-SafeXml (Join-Path $LaunchBoxRoot 'Data\Platforms.xml')
$gameXml = Read-SafeXml (Join-Path $LaunchBoxRoot ('Data\Platforms\' + $PlatformName + '.xml'))
$platformNodes = @($platformXml.SelectNodes('/LaunchBox/Platform') | Where-Object { (Get-ChildText $_ 'Name') -eq $PlatformName })
if ($platformNodes.Count -ne 1) { throw "Platforms.xml must contain exactly one '$PlatformName' platform." }
$configured = @{}
foreach ($folder in $platformXml.SelectNodes('/LaunchBox/PlatformFolder')) {
    if ((Get-ChildText $folder 'Platform') -ne $PlatformName) { continue }
    $type = Get-ChildText $folder 'MediaType'
    $path = Get-FullPath (Get-ChildText $folder 'FolderPath') $LaunchBoxRoot
    if ($configured.ContainsKey($type)) { throw "Duplicate configured media type: $type" }
    $configured[$type] = $path
}
$mappingObject = Get-Content -LiteralPath $MappingPath -Raw | ConvertFrom-Json
if ($null -eq $mappingObject -or $mappingObject -isnot [pscustomobject]) { throw 'MappingPath must contain a JSON object, not an array or scalar.' }
$imageTypes = @('Box - Front','Box - Front - Reconstructed','Box - Back','Box - Back - Reconstructed','Box - 3D','Box - Spine','Box - Full',
    'Advertisement Flyer - Front','Advertisement Flyer - Back','Arcade - Cabinet','Arcade - Circuit Board','Arcade - Control Panel',
    'Arcade - Controls Information','Arcade - Marquee','Banner','Cart - Front','Cart - Back','Cart - 3D','Clear Logo','Disc',
    'Fanart - Box - Front','Fanart - Box - Back','Fanart - Cart - Front','Fanart - Cart - Back','Fanart - Background','Fanart - Disc',
    'Screenshot - Gameplay','Screenshot - Game Title','Screenshot - Game Select','Screenshot - Game Over','Screenshot - High Scores',
    'Steam Banner','Steam Poster','Steam Screenshot','GOG Poster','GOG Screenshot','Epic Games Background','Epic Games Poster',
    'Epic Games Screenshot','Origin Background','Origin Poster','Origin Screenshot','Uplay Background','Uplay Thumbnail',
    'Amazon Background','Amazon Screenshot','Amazon Poster','Icon','Square','Poster')
$imageExt = @('.png','.jpg','.jpeg','.bmp','.gif','.webp')
$videoExt = @('.mp4','.m4v','.mkv','.webm','.avi','.wmv','.mov')
$musicExt = @('.mp3','.ogg','.flac','.wav','.m4a','.wma','.aac')
$mappings = New-Object 'Collections.Generic.List[object]'
foreach ($property in $mappingObject.PSObject.Properties) {
    $relative = [string]$property.Name
    if ($property.Value -isnot [string]) { throw "Media type must be a string: $relative" }
    $type = [string]$property.Value
    if ($VideoOnly -and $type -notin @('Video', 'Theme Video')) {
        throw "VideoOnly rejects non-video media mapping: $type"
    }
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)' -or $relative.Contains(':')) {
        throw "Source mapping must be a relative folder without traversal: $relative"
    }
    $sourceFolder = Get-FullPath $relative $SourceRoot
    if (-not (Test-Within $sourceFolder $SourceRoot)) { throw "Source mapping escapes SourceRoot: $relative" }
    if (-not (Test-Path -LiteralPath $sourceFolder -PathType Container)) { throw "Mapped source folder not found: $sourceFolder" }
    if (-not $configured.ContainsKey($type)) { throw "Media type is not configured for ${PlatformName}: $type" }
    $extensions = if ($imageTypes -contains $type) { $imageExt } elseif ($type -in @('Video','Theme Video')) { $videoExt }
        elseif ($type -eq 'Music') { $musicExt } elseif ($type -eq 'Manual') { @('.pdf') } else { throw "Unsupported media type: $type" }
    $destinationFolder = $configured[$type]
    if (-not (Test-Within $destinationFolder $LaunchBoxRoot) -or $destinationFolder -eq $LaunchBoxRoot) {
        throw "External/root media folder requires manual handling; this helper only writes below LaunchBoxRoot: $destinationFolder"
    }
    if ((Test-Within $destinationFolder $SourceRoot) -or (Test-Within $SourceRoot $destinationFolder)) { throw 'Source and destination media folders must not overlap.' }
    foreach ($prior in $mappings) {
        if ((Test-Within $sourceFolder $prior.SourceFolder) -or (Test-Within $prior.SourceFolder $sourceFolder)) {
            throw "Mapped source folders overlap: $sourceFolder and $($prior.SourceFolder)"
        }
        if ($prior.Type -ne $type -and $prior.DestinationFolder -eq $destinationFolder) { throw 'Different media types cannot share the same destination folder.' }
    }
    Assert-NoReparse $destinationFolder
    $mappings.Add([pscustomobject]@{SourceFolder=$sourceFolder;Type=$type;DestinationFolder=$destinationFolder;Extensions=@($extensions)})
}
if ($mappings.Count -eq 0) { throw 'The mapping file is empty.' }

$script:records = @{}
foreach ($game in $gameXml.SelectNodes('/LaunchBox/Game')) {
    $application = Get-ChildText $game 'ApplicationPath'
    if ([string]::IsNullOrWhiteSpace($application)) { continue }
    $record = Get-Record ([IO.Path]::GetFileNameWithoutExtension($application))
    $record.Imported = $true
    $title = Get-ChildText $game 'Title'
    if ($title) { $record.Titles.Add($title); Add-Alias $record $title }
    foreach ($field in @('AlternateTitle','ID','DatabaseID','LaunchBoxDbId')) { Add-Alias $record (Get-ChildText $game $field) }
    # Title/database-ID variants are recognized only from this game's real IDs.
    foreach ($idField in @('ID','DatabaseID','LaunchBoxDbId')) {
        $knownId = Get-ChildText $game $idField
        if ([string]::IsNullOrWhiteSpace($knownId)) { continue }
        foreach ($baseName in @($title,$record.Stem,(Get-ChildText $game 'AlternateTitle'))) {
            if ($baseName) { $script:knownIdSuffixes[(Get-Key $baseName) + '|' + $knownId.ToLowerInvariant()] = $true }
        }
    }
    $gameId = Get-ChildText $game 'ID'
    foreach ($alternate in $gameXml.SelectNodes('/LaunchBox/AlternateName')) {
        if ((Get-ChildText $alternate 'GameID') -eq $gameId) { Add-Alias $record (Get-ChildText $alternate 'Name') }
    }
}
$additionalStemFileCount = 0
if (-not [string]::IsNullOrWhiteSpace($ProfilesRoot)) {
    foreach ($file in @(Get-SafeFiles $ProfilesRoot)) {
        $record = Get-Record $file.BaseName
        $additionalStemFileCount++
        if ($file.Extension -ieq '.xml') {
            # A supplied match folder can contain ROMs or non-TeknoParrot XML.
            # Only valid GameProfile XML contributes an existing-media title alias.
            Assert-NoReparse $file.FullName
            try { $profile = Read-SafeXml $file.FullName }
            catch [System.Xml.XmlException] { continue }
            if ($profile.DocumentElement.Name -eq 'GameProfile') {
                Add-Alias $record (Get-ChildText $profile.DocumentElement 'GameNameInternal')
            }
        }
    }
}
if ($records.Count -eq 0) { throw 'No ROM/profile stems were found.' }
$included = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$useIncludeFilter = -not [string]::IsNullOrWhiteSpace($IncludeStemsPath)
if ($useIncludeFilter) {
    $IncludeStemsPath = Get-FullPath $IncludeStemsPath $baseDirectory
    Assert-NoReparse $IncludeStemsPath
    foreach ($entry in @(Import-Csv -LiteralPath $IncludeStemsPath)) {
        if (-not ($entry.PSObject.Properties.Name -contains 'RomStem')) { throw 'Include CSV must have a RomStem column.' }
        $stem = [string]$entry.RomStem
        if (-not $records.ContainsKey($stem)) { throw "Include CSV contains an unknown ROM/profile stem: $stem" }
        [void]$included.Add($stem)
    }
}
$verified = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
if ($VerifiedArcadeStemsPath) {
    $VerifiedArcadeStemsPath = Get-FullPath $VerifiedArcadeStemsPath $baseDirectory
    Assert-NoReparse $VerifiedArcadeStemsPath
    foreach ($entry in @(Import-Csv -LiteralPath $VerifiedArcadeStemsPath)) {
        if (-not ($entry.PSObject.Properties.Name -contains 'RomStem')) { throw 'Verification CSV must have a RomStem column.' }
        $stem = [string]$entry.RomStem
        if (-not $records.ContainsKey($stem)) { throw "Verification CSV contains an unknown ROM/profile stem: $stem" }
        [void]$verified.Add($stem)
    }
}

$indexes = @{}
foreach ($mapping in $mappings) {
    if ($indexes.ContainsKey($mapping.Type)) { continue }
    $index = @{}
    foreach ($existing in @(Get-SafeFiles $mapping.DestinationFolder)) {
        $otherCategory = $false
        foreach ($configuredType in $configured.Keys) {
            $otherRoot = $configured[$configuredType]
            if ($configuredType -ne $mapping.Type -and $otherRoot -ne $mapping.DestinationFolder -and
                (Test-Within $otherRoot $mapping.DestinationFolder) -and (Test-Within $existing.FullName $otherRoot)) { $otherCategory = $true; break }
        }
        if (-not $otherCategory -and $mapping.Extensions -contains $existing.Extension.ToLowerInvariant()) { Add-Existing $index $existing }
    }
    $indexes[$mapping.Type] = $index
}

$rows = New-Object 'Collections.Generic.List[object]'
foreach ($file in @(Get-SafeFiles $SourceRoot | Sort-Object FullName)) {
    $row = [pscustomobject]@{PlatformName=$PlatformName;Catalog=$Catalog;Status='Quarantined';Reason='UnmappedSourceFolder';RomStem=$file.BaseName;ImportedGame='';MediaType='';Source=$file.FullName;Destination='';ExistingMedia='';Bytes=$file.Length}
    if ($useIncludeFilter -and -not $included.Contains($file.BaseName)) {
        $row.Status = 'Skipped'
        $row.Reason = 'NotInIncludedStems'
        $rows.Add($row)
        continue
    }
    $matches = @($mappings | Where-Object { Test-Within $file.FullName $_.SourceFolder })
    if ($matches.Count -eq 1) {
        $mapping = $matches[0]
        $row.MediaType = $mapping.Type
        if ($mapping.Extensions -notcontains $file.Extension.ToLowerInvariant()) { $row.Reason = 'UnsupportedExtension' }
        elseif (-not $records.ContainsKey($file.BaseName)) { $row.Reason = 'UnknownExactRomStem' }
        elseif ($requiresArcadeVerification -and -not $verified.Contains($file.BaseName)) { $row.Reason = 'ArcadeStemNotVerified' }
        else {
            $record = $records[$file.BaseName]
            $row.ImportedGame = $record.Imported
            $row.RomStem = $record.Stem
            $row.Destination = Join-Path $mapping.DestinationFolder ($record.Stem + $file.Extension.ToLowerInvariant())
            $existing = Find-Existing $record $indexes[$mapping.Type]
            if ($existing) { $row.Status='Skipped'; $row.Reason='ExistingMediaForGameAndType'; $row.ExistingMedia=$existing }
            elseif ($file.Length -eq 0) { $row.Reason='EmptySourceFile' }
            elseif (Test-Path -LiteralPath $row.Destination) { $row.Status='Skipped'; $row.Reason='DestinationExists'; $row.ExistingMedia=$row.Destination }
            else {
                $row.Status='Planned'; $row.Reason='ExactRomStemAndMissingMedia'
                # Reserve every known alias so one run cannot plan duplicate media
                # for the same game/type across multiple folders or file formats.
                foreach ($alias in $record.Aliases) { $indexes[$mapping.Type][$alias]=$row.Destination }
            }
        }
    }
    $rows.Add($row)
}

if (-not $ReportRoot) {
    $reportTag = [regex]::Replace(($PlatformName + '-' + $Catalog), '[^A-Za-z0-9_.-]', '_')
    if ($reportTag.Length -gt 100) { $reportTag = $reportTag.Substring(0,100) }
    $ReportRoot = Join-Path $PSScriptRoot ('Reports\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $reportTag + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
}
$ReportRoot = Get-FullPath $ReportRoot $baseDirectory
if (Test-Path -LiteralPath $ReportRoot) { throw 'ReportRoot must be a new directory, so previous reports are never overwritten.' }
if ((Test-Within $ReportRoot $SourceRoot) -or (Test-Within $SourceRoot $ReportRoot)) { throw 'ReportRoot and SourceRoot must not overlap.' }
foreach ($mapping in $mappings) {
    if ((Test-Within $ReportRoot $mapping.DestinationFolder) -or (Test-Within $mapping.DestinationFolder $ReportRoot)) { throw 'ReportRoot and media destinations must not overlap.' }
}
Assert-NoReparse $ReportRoot
[void][IO.Directory]::CreateDirectory($ReportRoot)
Export-Rows @($rows | Where-Object Status -eq 'Planned') (Join-Path $ReportRoot 'Plan.csv')

if ($Apply) {
    foreach ($row in @($rows | Where-Object Status -eq 'Planned')) {
        $createdDestination = $false
        try {
            if ($CancelRequestPath -and (Test-Path -LiteralPath $CancelRequestPath)) { throw [OperationCanceledException]::new('Video import cancelled before the next copy.') }
            Assert-NoReparse $row.Source
            Assert-NoReparse $row.Destination
            $mapping = @($mappings | Where-Object Type -eq $row.MediaType)[0]
            # Re-enumerate immediately before copying: respect media added since
            # planning, including title-named assets in region subdirectories.
            $fresh = @{}
            foreach ($existing in @(Get-SafeFiles $mapping.DestinationFolder)) {
                $otherCategory = $false
                foreach ($configuredType in $configured.Keys) {
                    $otherRoot = $configured[$configuredType]
                    if ($configuredType -ne $mapping.Type -and $otherRoot -ne $mapping.DestinationFolder -and
                        (Test-Within $otherRoot $mapping.DestinationFolder) -and (Test-Within $existing.FullName $otherRoot)) { $otherCategory=$true; break }
                }
                if (-not $otherCategory -and $mapping.Extensions -contains $existing.Extension.ToLowerInvariant()) { Add-Existing $fresh $existing }
            }
            $existing = Find-Existing $records[$row.RomStem] $fresh
            if ($existing) { $row.Status='Skipped'; $row.Reason='MediaAppearedBeforeCopy'; $row.ExistingMedia=$existing; continue }
            $sourceStream = [IO.File]::Open($row.Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                if ($sourceStream.Length -ne $row.Bytes) { throw 'Source size changed after planning; rerun the helper.' }
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($row.Destination))
                $destinationStream = [IO.File]::Open($row.Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                $createdDestination = $true
                try { $sourceStream.CopyTo($destinationStream); $destinationStream.Flush() }
                finally { $destinationStream.Dispose() }
            } finally { $sourceStream.Dispose() }
            $row.Status='Copied'; $row.Reason='CopiedWithoutOverwrite'
        } catch {
            $row.Status='Failed'; $row.Reason=$_.Exception.Message
            # Only remove the incomplete file created by this very copy attempt.
            if ($createdDestination -and (Test-Within $row.Destination $LaunchBoxRoot)) {
                try { [IO.File]::Delete($row.Destination) } catch { $row.Reason += ' Partial destination could not be removed; inspect it manually.' }
            }
        }
        Export-Rows @($rows | Where-Object { $_.Status -in @('Copied','Failed') }) (Join-Path $ReportRoot 'CopyResults.csv')
        if ($CancelRequestPath -and (Test-Path -LiteralPath $CancelRequestPath)) { break }
    }
}
Export-Rows ($rows.ToArray()) (Join-Path $ReportRoot 'AllResults.csv')
Export-Rows @($rows | Where-Object Status -eq 'Skipped') (Join-Path $ReportRoot 'Skipped.csv')
Export-Rows @($rows | Where-Object Status -eq 'Quarantined') (Join-Path $ReportRoot 'Quarantine.csv')
Export-Rows @($rows | Where-Object { $_.Status -in @('Copied','Failed') }) (Join-Path $ReportRoot 'CopyResults.csv')
$summary = [pscustomobject]@{PlatformName=$PlatformName;Catalog=$Catalog;VideoOnly=[bool]$VideoOnly;ArcadeVerificationRequired=[bool]$requiresArcadeVerification;Apply=[bool]$Apply;SourceRoot=$SourceRoot;LaunchBoxRoot=$LaunchBoxRoot;ProfilesRoot=$ProfilesRoot;IncludeStemsPath=$IncludeStemsPath;IncludeFilterEnabled=$useIncludeFilter;IncludedStemCount=$included.Count;AdditionalStemFileCount=$additionalStemFileCount;ProfileCount=$records.Count;Planned=@($rows|Where-Object Status -eq 'Planned').Count;Copied=@($rows|Where-Object Status -eq 'Copied').Count;Skipped=@($rows|Where-Object Status -eq 'Skipped').Count;Quarantined=@($rows|Where-Object Status -eq 'Quarantined').Count;Failed=@($rows|Where-Object Status -eq 'Failed').Count;ReportRoot=$ReportRoot}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportRoot 'Summary.json') -Encoding UTF8
$summary
if ($summary.Failed -gt 0) { throw "$($summary.Failed) copy operation(s) failed. Inspect CopyResults.csv." }



