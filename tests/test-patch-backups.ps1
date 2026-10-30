param([Parameter(Mandatory = $true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$installer = Join-Path $package 'install-forest-manual-patch.ps1'
$manifest = Get-Content (Join-Path $package 'payload\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$fixture = Join-Path $env:TEMP ('ValheimPatchBackupTest-' + [Guid]::NewGuid().ToString('N'))

function Write-TestArchive([string]$Path, [string]$Text) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = New-Object IO.StreamWriter($zip.CreateEntry('marker.txt').Open())
        try { $writer.Write($Text) } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
}

$cases = @(
    @{name='empty'; count=0},
    @{name='five'; count=5},
    @{name='eight'; count=8},
    @{name='pinned'; count=5; pin=$true},
    @{name='collision'; count=5; collision=$true},
    @{name='blocked_then_retry'; count=5; blocked=$true},
    @{name='all_pinned'; count=5; allPinned=$true}
)
$passed = @()
try {
    foreach ($case in $cases) {
        $root = Join-Path $fixture $case.name
        $game = Join-Path $root 'game'
        $save = Join-Path $root 'saves'
        $backups = Join-Path $game 'ForestCryptQA-private-backups'
        $history = Join-Path $backups 'archive'
        $dll = Join-Path $game 'BepInEx\plugins\DungeonDepthPrototype\DungeonDepthPrototype.dll'
        $config = Join-Path $game 'BepInEx\config\forest-manual-test.json'
        foreach ($folder in @((Split-Path $dll), (Split-Path $config), $save, $backups)) {
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
        }
        [IO.File]::WriteAllText((Join-Path $game 'valheim.exe'), 'fixture-not-executable')
        [IO.File]::WriteAllText((Join-Path $save 'test.fch'), 'original-save')
        [IO.File]::WriteAllText($config, '{"Enabled":false}')
        Copy-Item (Join-Path $package 'payload\DungeonDepthPrototype.dll') $dll
        $saveHash = (Get-FileHash (Join-Path $save 'test.fch')).Hash
        $configHash = (Get-FileHash $config).Hash
        $old = @{}
        for ($i = 1; $i -le $case.count; $i++) {
            $name = 'manual-200001{0:D2}T0000000000000Z.zip' -f $i
            $path = Join-Path $backups $name
            Write-TestArchive $path ($case.name + '-' + $i)
            $old[$name] = (Get-FileHash $path).Hash
            if ($case.ContainsKey('allPinned') -or ($case.ContainsKey('pin') -and $i -eq 1)) {
                [IO.File]::WriteAllText(($path + '.keep'), 'protected fixture')
            }
        }
        $collisionPath = $null
        $collisionHash = $null
        if ($case.ContainsKey('collision')) {
            New-Item -ItemType Directory -Path $history | Out-Null
            $collisionPath = Join-Path $history (@($old.Keys | Sort-Object)[0])
            Write-TestArchive $collisionPath 'pre-existing archived copy'
            $collisionHash = (Get-FileHash $collisionPath).Hash
        }
        if ($case.ContainsKey('blocked')) { [IO.File]::WriteAllText($history, 'blocked archive directory') }
        $failed = $false
        try { & $installer -GameDirectory $game -SaveDirectory $save | Out-Null }
        catch { $failed = $true }
        $expectedFailure = $case.ContainsKey('blocked') -or $case.ContainsKey('allPinned')
        if ($failed -ne $expectedFailure) { throw ('unexpected_install_result_' + $case.name) }
        if ($failed) {
            if ((Get-FileHash $config).Hash -ne $configHash) { throw 'failure_modified_game_config' }
            if (Test-Path (Join-Path $game 'BepInEx\plugins\ServerManager\ServerManager.dll')) {
                throw 'failure_installed_partial_payload'
            }
            foreach ($name in $old.Keys) {
                if ((Get-FileHash (Join-Path $backups $name)).Hash -ne $old[$name]) {
                    throw 'failure_changed_previous_backup'
                }
            }
        }
        if ($case.ContainsKey('blocked')) {
            $created = @(Get-ChildItem $backups -Filter 'manual-*.zip' |
                Where-Object { -not $old.ContainsKey($_.Name) })
            if ($created.Count -ne 1) { throw 'missing_verified_backup_before_rotation' }
            $old[$created[0].Name] = (Get-FileHash $created[0].FullName).Hash
            Remove-Item -LiteralPath $history
            & $installer -GameDirectory $game -SaveDirectory $save | Out-Null
        }
        if (-not $case.ContainsKey('allPinned')) {
            foreach ($item in $manifest.files) {
                if ((Get-FileHash (Join-Path $game $item.relative)).Hash -ne $item.sha256) {
                    throw 'installed_hash_mismatch'
                }
            }
            $active = @(Get-ChildItem $backups -Filter 'manual-*.zip' -File)
            if ($active.Count -ne [Math]::Min(5, $old.Count + 1)) { throw 'active_backup_count' }
            $new = @($active | Where-Object { -not $old.ContainsKey($_.Name) })
            if ($new.Count -ne 1) { throw 'new_backup_not_kept_active' }
            $restored = Join-Path $root 'restore'
            [IO.Compression.ZipFile]::ExtractToDirectory($new[0].FullName, $restored)
            if ((Get-FileHash (Join-Path $restored 'saves\test.fch')).Hash -ne $saveHash -or
                (Get-FileHash (Join-Path $restored 'config\forest-manual-test.json')).Hash -ne $configHash) {
                throw 'new_backup_restore_mismatch'
            }
            foreach ($name in $old.Keys) {
                $matches = @(Get-ChildItem $backups -Filter '*.zip' -File -Recurse |
                    Where-Object { (Get-FileHash $_.FullName).Hash -eq $old[$name] })
                if ($matches.Count -ne 1) { throw 'previous_backup_missing_or_duplicated' }
            }
        }
        if ($case.ContainsKey('pin')) {
            $first = Join-Path $backups (@($old.Keys | Sort-Object)[0])
            if (-not (Test-Path $first) -or -not (Test-Path ($first + '.keep'))) { throw 'pinned_backup_moved' }
        }
        if ($collisionPath -and (Get-FileHash $collisionPath).Hash -ne $collisionHash) {
            throw 'archive_collision_overwritten'
        }
        if ((Get-FileHash (Join-Path $save 'test.fch')).Hash -ne $saveHash) { throw 'save_modified' }
        $passed += $case.name
    }
    @{status='passed'; cases=$passed; previous_archives_preserved=$true; save_unchanged=$true} | ConvertTo-Json
} finally {
    if (Test-Path $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
