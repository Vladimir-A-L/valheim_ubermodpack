param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
if (-not (Test-Path -LiteralPath "$GamePath\valheim.exe")) { throw 'Valheim executable not found' }
$root = (Resolve-Path -LiteralPath $GamePath).Path
$files = @(foreach ($folder in @('BepInEx\plugins', 'BepInEx\patchers', 'BepInEx\core', 'BepInEx\config')) {
    if (Test-Path -LiteralPath "$root\$folder") {
        Get-ChildItem -LiteralPath "$root\$folder" -Recurse -File | ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
                size = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }
})
$configs = @(if (Test-Path -LiteralPath "$root\BepInEx\config") {
    Get-ChildItem -LiteralPath "$root\BepInEx\config" -Recurse -File |
        Where-Object { $_.Extension -in @('.cfg', '.json', '.yaml', '.yml', '.txt') } | ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
                content = [IO.File]::ReadAllText($_.FullName)
            }
        }
})
$logLines = @(if (Test-Path -LiteralPath "$root\BepInEx\LogOutput.log") {
    Get-Content -LiteralPath "$root\BepInEx\LogOutput.log" |
        Select-String -Pattern 'BepInEx [0-9]|Running under Unity|Loading \[|Valheim version|plugins to load' |
        ForEach-Object { $_.Line }
})
$manifests = @(Get-ChildItem -LiteralPath $root -File |
    Where-Object { $_.Name -match 'lock.*\.json$|manifest.*\.json$' } | ForEach-Object {
        [ordered]@{ path = $_.Name; content = [IO.File]::ReadAllText($_.FullName) }
    })
$steamManifest = Join-Path $root '..\..\appmanifest_892970.acf'
$steam = if (Test-Path -LiteralPath $steamManifest) { [IO.File]::ReadAllText($steamManifest) } else { $null }
$result = [ordered]@{
    schema_version = 1
    captured_at_utc = [DateTime]::UtcNow.ToString('o')
    game_path = $root
    executable_version = (Get-Item -LiteralPath "$root\valheim.exe").VersionInfo.FileVersion
    root_entries = @(Get-ChildItem -LiteralPath $root | Select-Object -ExpandProperty Name)
    game_running = [bool](Get-Process -Name valheim -ErrorAction SilentlyContinue)
    steam_manifest = $steam
    files = $files
    configs = $configs
    manifests = $manifests
    log_versions = $logLines
}
$result | ConvertTo-Json -Depth 8 -Compress
