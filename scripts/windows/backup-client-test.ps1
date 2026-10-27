param(
    [string]$StandRoot = (Join-Path $env:LOCALAPPDATA 'ValheimTest\current'),
    [string]$SaveDirectory = (Join-Path $env:USERPROFILE 'AppData\LocalLow\IronGate\Valheim'),
    [string[]]$ExtraDataDirectories = @(),
    [switch]$Pin
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Get-Process valheim -ErrorAction SilentlyContinue) { throw 'game_must_be_closed' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$save = $SaveDirectory
$config = Join-Path $StandRoot 'client\BepInEx\config'
$backups = Join-Path $StandRoot 'backups'
New-Item -ItemType Directory -Path $backups -Force | Out-Null
$mutex = New-Object Threading.Mutex($false, 'Local\ValheimTestBackup')
if (-not $mutex.WaitOne(0)) { $mutex.Dispose(); throw 'backup_already_running' }
$stage = Join-Path $backups ('.pending-' + [Guid]::NewGuid().ToString('N'))
$pendingZip = $stage + '.zip'

function Read-BackupManifest([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $reader = New-Object IO.StreamReader($zip.GetEntry('manifest.json').Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($manifest.kind -ne 'valheim-client-backup' -or $manifest.schema_version -ne 1) {
            throw 'unknown_backup_format'
        }
        return $manifest
    } finally { $zip.Dispose() }
}

function Test-BackupContent([string]$Path) {
    $manifest = Read-BackupManifest $Path
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($file in $manifest.files) {
            $entry = $zip.GetEntry($file.path)
            if ($null -eq $entry -or $entry.Length -ne $file.bytes) { throw 'archive_entry_mismatch' }
            $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $sha.Dispose() }
            if ($hash -ne $file.sha256) { throw 'archive_hash_mismatch' }
        }
    } finally { $zip.Dispose() }
}

try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    $sources = @(@{name='saves'; path=$save}, @{name='config'; path=$config})
    for ($i = 0; $i -lt $ExtraDataDirectories.Count; $i++) {
        $sources += @{name=('extra-{0}' -f $i); path=$ExtraDataDirectories[$i]}
    }
    foreach ($source in $sources) {
        $root = (Resolve-Path -LiteralPath $source.path).Path.TrimEnd('\')
        if ($backups.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'backup_directory_inside_source'
        }
        foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'source_reparse_point' }
            if ($item.PSIsContainer) { continue }
            $relative = $item.FullName.Substring($root.Length + 1)
            if ($source.name -eq 'saves' -and $relative -in @('Player.log', 'Player-prev.log')) { continue }
            $destination = Join-Path (Join-Path $stage $source.name) $relative
            New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
            Copy-Item -LiteralPath $item.FullName -Destination $destination
            if ((Get-FileHash $item.FullName).Hash -ne (Get-FileHash $destination).Hash) {
                throw 'source_changed_during_backup'
            }
        }
    }
    & reg.exe export 'HKCU\Software\IronGate\Valheim' (Join-Path $stage 'settings.reg') /y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'registry_export_failed' }
    Copy-Item -LiteralPath (Join-Path $StandRoot 'appmanifest_892970.acf') (Join-Path $stage 'build.acf')
    $release = @(Get-ChildItem (Join-Path $StandRoot 'client\BepInEx\plugins') -Recurse -File -Filter '*.dll' |
        Sort-Object FullName | ForEach-Object {
            [ordered]@{path=$_.FullName.Substring($StandRoot.Length + 1); sha256=(Get-FileHash $_.FullName).Hash}
        })
    $release | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $stage 'plugins.json') -Encoding UTF8
    $files = @(Get-ChildItem $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
        @{path=$_.FullName.Substring($stage.Length + 1).Replace('\', '/');
          sha256=(Get-FileHash $_.FullName).Hash; bytes=$_.Length}
    })
    $fingerprint = ($files | ForEach-Object { $_.path + '|' + $_.sha256 + '|' + $_.bytes }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $contentKey = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($fingerprint)))
    } finally { $sha.Dispose() }
    $archives = @(Get-ChildItem $backups -Filter 'client-*.zip' -File | Sort-Object Name -Descending)
    $pinned = @($archives | Where-Object { Test-Path -LiteralPath ($_.FullName + '.keep') })
    if ($archives.Count -gt 0 -and (Read-BackupManifest $archives[0].FullName).content_key -eq $contentKey) {
        $final = $archives[0].FullName
        Test-BackupContent $final
        $status = 'unchanged'
    } else {
        if ($pinned.Count -ge 5) { throw 'five_pinned_backups_require_review' }
        @{schema_version=1; kind='valheim-client-backup'; utc=[DateTime]::UtcNow.ToString('o');
          content_key=$contentKey; files=$files} | ConvertTo-Json -Depth 5 |
            Set-Content (Join-Path $stage 'manifest.json') -Encoding UTF8
        $zip = [IO.Compression.ZipFile]::Open($pendingZip, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($item in Get-ChildItem $stage -Recurse -File) {
                $name = $item.FullName.Substring($stage.Length + 1).Replace('\', '/')
                $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $zip, $item.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $zip.Dispose() }
        Test-BackupContent $pendingZip
        $final = Join-Path $backups ('client-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.zip')
        Move-Item -LiteralPath $pendingZip -Destination $final
        $status = 'created'
    }
    if ($Pin) { [IO.File]::WriteAllText($final + '.keep', 'Protected restore point') }
    $archives = @(Get-ChildItem $backups -Filter 'client-*.zip' -File | Sort-Object Name -Descending)
    $pinned = @($archives | Where-Object { Test-Path -LiteralPath ($_.FullName + '.keep') })
    $unprotected = @($archives | Where-Object { -not (Test-Path -LiteralPath ($_.FullName + '.keep')) })
    foreach ($old in @($unprotected | Select-Object -Skip (5 - $pinned.Count))) {
        $null = Read-BackupManifest $old.FullName
        Remove-Item -LiteralPath $old.FullName
    }
    @{status=$status; archive=$final; sha256=(Get-FileHash $final).Hash; content_key=$contentKey;
      utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if (Test-Path -LiteralPath $pendingZip) { Remove-Item -LiteralPath $pendingZip -Force }
    $mutex.ReleaseMutex(); $mutex.Dispose()
}
