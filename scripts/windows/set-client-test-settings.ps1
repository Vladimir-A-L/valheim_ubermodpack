param(
    [Parameter(Mandatory = $true)][string]$ProfileJson,
    [Parameter(Mandatory = $true)][string]$TestClientDirectory,
    [Parameter(Mandatory = $true)][string]$SettingsBackupDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Настройки Unity общие для пользователя Windows; применять только при закрытой игре.
if (Get-Process valheim -ErrorAction SilentlyContinue) { throw 'game_must_be_closed' }
$client = (Resolve-Path -LiteralPath $TestClientDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $client 'valheim.exe'))) { throw 'client_missing' }
$profile = Get-Content -LiteralPath $ProfileJson -Raw -Encoding UTF8 | ConvertFrom-Json
if ($profile.schema_version -ne 1 -or $profile.valheim -ne 'l-1.0.20') { throw 'unsupported_profile' }

$changes = @()
foreach ($property in $profile.player_prefs.PSObject.Properties) {
    if ($property.Value -isnot [int] -or $property.Value -lt 0) { throw 'non_dword_setting' }
    # Имя PlayerPrefs в Windows: Unity DJB2-XOR, проверено на ключах установленного клиента.
    $hash = [uint64]5381
    foreach ($character in $property.Name.ToCharArray()) {
        $hash = (($hash * 33) -bxor [uint64][char]$character) -band 4294967295
    }
    $changes += @{name=('{0}_h{1}' -f $property.Name, $hash); value=$property.Value}
}

$configPath = Join-Path $client 'BepInEx\config\BepInEx.cfg'
$original = [IO.File]::ReadAllText($configPath)
$updated = $original
foreach ($entry in @(
    @('Caching', 'EnableAssemblyCache', 'true'),
    @('Logging.Console', 'Enabled', 'false'),
    @('Logging.Disk', 'WriteUnityLog', 'false')
)) {
    $pattern = '(?ms)(^\[' + [regex]::Escape($entry[0]) + '\]\r?\n(?:(?!^\[).)*?^' +
        [regex]::Escape($entry[1]) + '\s*=\s*)[^\r\n]*'
    if ([regex]::Matches($updated, $pattern).Count -ne 1) { throw 'unexpected_bepinex_config' }
    $replacement = '${1}' + $entry[2]
    $updated = [regex]::Replace($updated, $pattern, $replacement)
}

# Одна исходная копия настроек, без создания нового backup при каждом применении.
New-Item -ItemType Directory -Path $SettingsBackupDirectory -Force | Out-Null
$registryBackup = Join-Path $SettingsBackupDirectory 'settings-before-tuning.reg'
if (-not (Test-Path -LiteralPath $registryBackup)) {
    & reg.exe export 'HKCU\Software\IronGate\Valheim' $registryBackup /y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'registry_backup_failed' }
}
$configBackup = Join-Path $SettingsBackupDirectory 'BepInEx-before-tuning.cfg'
if (-not (Test-Path -LiteralPath $configBackup)) { Copy-Item -LiteralPath $configPath $configBackup }

$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\IronGate\Valheim', $true)
if ($null -eq $key) { throw 'valheim_registry_missing' }
try {
    foreach ($change in $changes) {
        $key.SetValue($change.name, $change.value, [Microsoft.Win32.RegistryValueKind]::DWord)
    }
    foreach ($change in $changes) {
        if ($key.GetValue($change.name) -ne $change.value) { throw 'setting_verification_failed' }
    }
} finally { $key.Dispose() }
[IO.File]::WriteAllText($configPath, $updated, (New-Object Text.UTF8Encoding($false)))
@{status='applied'; utc=[DateTime]::UtcNow.ToString('o'); settings=$changes.Count} | ConvertTo-Json
