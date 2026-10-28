param(
    [string]$GameDirectory,
    [string]$SaveDirectory = (Join-Path $env:USERPROFILE 'AppData\LocalLow\IronGate\Valheim')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Get-Process valheim -ErrorAction SilentlyContinue) { throw 'Полностью закройте Valheim перед установкой.' }
if (-not $GameDirectory) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object Windows.Forms.OpenFileDialog
    $dialog.Title = 'Выберите valheim.exe из тестовой копии ForestCryptQA'
    $dialog.Filter = 'Valheim|valheim.exe'
    try {
        if ($dialog.ShowDialog() -ne 'OK') { throw 'Установка отменена.' }
        $GameDirectory = Split-Path $dialog.FileName
    } finally { $dialog.Dispose() }
}
$game = (Resolve-Path -LiteralPath $GameDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $game 'valheim.exe'))) { throw 'Не найден valheim.exe.' }
$payload = Join-Path $PSScriptRoot 'payload'
$manifest = Get-Content (Join-Path $payload 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$dllRelative = 'BepInEx\plugins\DungeonDepthPrototype\DungeonDepthPrototype.dll'
$configRelative = 'BepInEx\config\forest-manual-test.json'
$dll = Join-Path $game $dllRelative
if (-not (Test-Path -LiteralPath $dll)) { throw 'Нужен ранее установленный клиент ForestCryptQA 0.3.0.' }
$oldHash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
if ($oldHash -notin $manifest.accepted_previous_dll_sha256 -and $oldHash -ne $manifest.dll_sha256) {
    throw 'Неизвестная версия тестового модуля. Установка остановлена.'
}
$managerRelative = 'BepInEx\plugins\ServerManager\ServerManager.dll'
$managerConfigRelative = 'BepInEx\config\sighsorry.ServerManager.cfg'
$requiredFiles = @($dllRelative, $configRelative, $managerRelative, $managerConfigRelative)
$allowed = $requiredFiles + @('BepInEx\config\dungeon-rewards.json', 'BepInEx\config\forest-loadout.json')
$sources = @($manifest.files | ForEach-Object {
    if ($_.relative -notin $allowed -or $_.source -ne [IO.Path]::GetFileName($_.source)) {
        throw 'Неожиданный файл в manifest обновления.'
    }
    @{relative=$_.relative; source=(Join-Path $payload $_.source); hash=$_.sha256}
})
if ($sources.Count -notin @(4, 6) -or @($sources.relative | Sort-Object -Unique).Count -ne $sources.Count -or
    @($requiredFiles | Where-Object { $_ -notin $sources.relative }).Count -ne 0) {
    throw 'Неполный или повторяющийся набор файлов обновления.'
}
foreach ($item in $sources) {
    if ((Get-FileHash -LiteralPath $item.source -Algorithm SHA256).Hash -ne $item.hash) {
        throw 'Контрольная сумма патча не совпала.'
    }
}
$manager = Join-Path $game $managerRelative
if ((Test-Path $manager) -and (Get-FileHash $manager).Hash -ne $manifest.servermanager_sha256) {
    throw 'Уже установлена другая версия ServerManager. Требуется проверка перед заменой.'
}
$manual = Get-Content (Join-Path $payload 'forest-manual-test.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manual.PlayerName -ne $manifest.character -or -not $manual.Enabled -or -not $manual.Record) {
    throw 'Некорректная конфигурация ручной проверки.'
}
if (-not (Test-Path -LiteralPath $SaveDirectory)) { throw 'Не найден каталог сохранений для backup.' }
$backupRoot = Join-Path $game 'ForestCryptQA-private-backups'
$oldBackups = @(Get-ChildItem $backupRoot -Filter 'manual-*.zip' -File -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending)
if ($oldBackups.Count -ge 5) { throw 'Уже есть пять backup патча. Сохраните нужные копии вне папки игры.' }
$volume = Get-Item -LiteralPath $game
$backupSources = @(
    @{name='saves'; path=(Resolve-Path -LiteralPath $SaveDirectory).Path},
    @{name='config'; path=(Join-Path $game 'BepInEx\config')}
)
$steam = Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
if ($null -eq $steam -or -not $steam.SteamPath) {
    throw 'Не найден Steam: невозможно проверить локальный кэш Cloud для backup.'
}
$userData = Join-Path $steam.SteamPath 'userdata'
$cloudIndex = 0
foreach ($account in Get-ChildItem -LiteralPath $userData -Directory -ErrorAction SilentlyContinue) {
    $appData = Join-Path $account.FullName '892970'
    if (Test-Path -LiteralPath $appData) {
        $backupSources += @{name=('steam-cloud-' + $cloudIndex); path=$appData}
        $cloudIndex++
    }
}
$required = 256MB
foreach ($source in $backupSources) {
    $size = Get-ChildItem -LiteralPath $source.path -Recurse -File | Measure-Object Length -Sum
    if ($null -ne $size.Sum) { $required += 3 * $size.Sum }
}
if ($volume.PSDrive.Free -lt $required) { throw 'Недостаточно места для проверяемого backup.' }
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$stage = Join-Path $backupRoot ('.pending-' + $stamp)
$archive = Join-Path $backupRoot ('manual-' + $stamp + '.zip')
New-Item -ItemType Directory -Path $stage | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    foreach ($source in $backupSources) {
        foreach ($file in Get-ChildItem -LiteralPath $source.path -Recurse -Force) {
            if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Найдена ссылка в данных backup.'
            }
            if ($file.PSIsContainer -or $file.Name -in @('Player.log', 'Player-prev.log')) { continue }
            $relative = $file.FullName.Substring($source.path.Length + 1)
            $destination = Join-Path (Join-Path $stage $source.name) $relative
            New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash $destination).Hash) {
                throw 'Данные изменились во время резервирования.'
            }
        }
    }
    foreach ($item in $sources) {
        $previous = Join-Path $game $item.relative
        if (Test-Path -LiteralPath $previous) {
            $destination = Join-Path (Join-Path $stage 'original') $item.relative
            New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
            Copy-Item -LiteralPath $previous -Destination $destination
            if ((Get-FileHash $previous).Hash -ne (Get-FileHash $destination).Hash) {
                throw 'Файл поставки изменился во время резервирования.'
            }
        }
    }
    $files = @(Get-ChildItem $stage -Recurse -File | ForEach-Object {
        @{path=$_.FullName.Substring($stage.Length + 1).Replace('\', '/'); sha256=(Get-FileHash $_.FullName).Hash}
    })
    @{observed_at_utc=[DateTime]::UtcNow.ToString('o'); files=$files; sources=$backupSources} |
        ConvertTo-Json -Depth 5 |
        Set-Content (Join-Path $stage 'manifest.json') -Encoding UTF8
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive)
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($file in $files) {
            $entry = $zip.GetEntry($file.path)
            if ($null -eq $entry) { $entry = $zip.GetEntry($file.path.Replace('/', '\')) }
            if ($null -eq $entry) { throw 'Неполный backup.' }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $sha.Dispose() }
            if ($hash -ne $file.sha256) { throw 'Не прошла проверка SHA256 backup.' }
        }
    } finally { $zip.Dispose() }
    if (Get-Process valheim -ErrorAction SilentlyContinue) { throw 'Игра запущена во время резервирования.' }
    try {
        foreach ($item in $sources) {
            $destination = Join-Path $game $item.relative
            New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
            Copy-Item -LiteralPath $item.source -Destination $destination -Force
            if ((Get-FileHash -LiteralPath $destination).Hash -ne $item.hash) { throw 'Ошибка установки патча.' }
        }
    } catch {
        foreach ($item in $sources) {
            $previous = Join-Path (Join-Path $stage 'original') $item.relative
            $destination = Join-Path $game $item.relative
            if (Test-Path -LiteralPath $previous) { Copy-Item $previous $destination -Force }
            elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
        }
        throw
    }
    Write-Host ('Патч установлен для персонажа ' + $manifest.character + '. Backup: ' + $archive)
    Write-Host 'Запустите тестовую копию игры и войдите на прежний сервер.'
    Write-Host 'Дождитесь сообщения «Тест готов» с подтверждённым счётчиком сервера.'
} finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
}
