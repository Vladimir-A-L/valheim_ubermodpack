param(
    [string]$StandRoot = (Join-Path $env:LOCALAPPDATA 'ValheimTest\current'),
    [switch]$NoWait
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Get-Process valheim -ErrorAction SilentlyContinue) { throw 'game_already_running' }
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { throw 'start_steam_first' }
$client = Join-Path $StandRoot 'client'
$save = Join-Path $env:USERPROFILE 'AppData\LocalLow\IronGate\Valheim'
if (-not (Test-Path (Join-Path $save 'characters_local'))) { throw 'local_character_store_missing' }
$os = Get-CimInstance Win32_OperatingSystem
$disk = Get-PSDrive -Name ([IO.Path]::GetPathRoot($StandRoot).Substring(0, 1))
if ($os.FreePhysicalMemory -lt (8GB / 1KB) -or $disk.Free -lt 10GB) { throw 'insufficient_free_resources' }
$backupScript = Join-Path $PSScriptRoot 'backup-client-test.ps1'
$extraFile = Join-Path $StandRoot 'backup-extra.json'
$extra = @()
if (Test-Path $extraFile) { $extra = @(Get-Content $extraFile -Raw | ConvertFrom-Json) }
$backup = & $backupScript -StandRoot $StandRoot -ExtraDataDirectories $extra | ConvertFrom-Json
$log = Join-Path $StandRoot 'client.log'
if (Test-Path $log) { Move-Item $log ($log + '.previous') -Force }
$arguments = '-screen-width 1280 -screen-height 720 -screen-fullscreen 0 -console -logFile "{0}"' -f $log
$game = Start-Process (Join-Path $client 'valheim.exe') -WorkingDirectory $client -ArgumentList $arguments -PassThru
@{status='launched'; pid=$game.Id; save_directory=$save; backup=$backup.archive;
  started_at_utc=$game.StartTime.ToUniversalTime().ToString('o')} | ConvertTo-Json
if (-not $NoWait) {
    $game.WaitForExit()
    & $backupScript -StandRoot $StandRoot -ExtraDataDirectories $extra
}
