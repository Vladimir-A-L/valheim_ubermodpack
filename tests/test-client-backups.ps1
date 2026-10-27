param([Parameter(Mandatory = $true)][string]$BackupScript)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$fixture = Join-Path $env:TEMP ('ValheimBackupTest-' + [Guid]::NewGuid().ToString('N'))
$save = Join-Path $fixture 'source'
$stand = Join-Path $fixture 'stand'
try {
    foreach ($path in @($save, (Join-Path $stand 'client\BepInEx\config'),
        (Join-Path $stand 'client\BepInEx\plugins'))) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $stand 'appmanifest_892970.acf'), 'fixture')
    $data = Join-Path $save 'test-save.txt'
    [IO.File]::WriteAllText($data, 'version-0')
    $first = & $BackupScript -StandRoot $stand -SaveDirectory $save -Pin | ConvertFrom-Json
    $again = & $BackupScript -StandRoot $stand -SaveDirectory $save | ConvertFrom-Json
    if ($again.status -ne 'unchanged' -or $again.archive -ne $first.archive) { throw 'dedup_failed' }
    for ($i = 1; $i -le 7; $i++) {
        [IO.File]::WriteAllText($data, ('version-' + $i))
        $latest = & $BackupScript -StandRoot $stand -SaveDirectory $save | ConvertFrom-Json
    }
    $archives = @(Get-ChildItem (Join-Path $stand 'backups') -Filter 'client-*.zip' -File)
    if ($archives.Count -ne 5 -or -not (Test-Path $first.archive)) { throw 'retention_or_pin_failed' }
    $restored = Join-Path $fixture 'restore'
    [IO.Compression.ZipFile]::ExtractToDirectory($latest.archive, $restored)
    if ((Get-FileHash $data).Hash -ne (Get-FileHash (Join-Path $restored 'saves\test-save.txt')).Hash) {
        throw 'restore_failed'
    }
    $zip = [IO.Compression.ZipFile]::Open($latest.archive, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $zip.GetEntry('saves/test-save.txt').Delete()
        $writer = New-Object IO.StreamWriter($zip.CreateEntry('saves/test-save.txt').Open())
        try { $writer.Write('corrupt') } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    $refused = $false
    try { $null = & $BackupScript -StandRoot $stand -SaveDirectory $save }
    catch { $refused = $_.Exception.Message -like 'archive_*mismatch' }
    if (-not $refused) { throw 'corrupted_archive_was_reused' }
    if (@(Get-ChildItem (Join-Path $stand 'backups') -Filter 'client-*.zip' -File).Count -ne 5) {
        throw 'failed_backup_changed_retention'
    }
    @{status='passed'; checks=@('unchanged_dedup', 'five_archives_with_pin', 'restore', 'corruption_refusal')} |
        ConvertTo-Json
} finally {
    if (Test-Path $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
