$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$consoleAccount = (Get-CimInstance Win32_ComputerSystem).UserName
if (-not $consoleAccount -or $account -ine $consoleAccount) { throw 'console_account_mismatch' }
$runId = [Guid]::NewGuid().ToString('N')
$root = Join-Path $env:LOCALAPPDATA ('ValheimVision\' + $runId)
New-Item -ItemType Directory -Path $root | Out-Null
$worker = Join-Path $root 'capture-window.ps1'
$workerBytes = [Convert]::FromBase64String('__WORKER_BASE64__')
# Windows PowerShell 5.1 требует BOM для русских комментариев в файле.
[IO.File]::WriteAllBytes($worker, [byte[]](@(239, 187, 191) + $workerBytes))
$launcher = Join-Path $root 'capture-host.exe'
$source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__HOST_BASE64__'))
Add-Type -TypeDefinition $source -OutputAssembly $launcher -OutputType WindowsApplication
$taskName = 'ValheimVision-' + $runId
$action = New-ScheduledTaskAction -Execute $launcher -Argument ('"{0}" "{1}"' -f $worker, $root)
$principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 45)
$resultPath = Join-Path $root 'result.json'
$registered = $false
try {
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings | Out-Null
    $registered = $true
    Start-ScheduledTask -TaskName $taskName
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $resultPath) -and $timer.Elapsed.TotalSeconds -lt 30) {
        Start-Sleep -Milliseconds 200
    }
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'interactive_capture_timeout' }
    $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $result | Add-Member -NotePropertyName run_id -NotePropertyValue $runId
    $result | Add-Member -NotePropertyName remote_artifacts -NotePropertyValue $root
    if ($result.status -eq 'captured') {
        $png = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root 'frame.png')))
        $result | Add-Member -NotePropertyName image_base64 -NotePropertyValue $png
    }
    $result | ConvertTo-Json -Depth 6 -Compress
} finally {
    if ($registered -and (Test-Path -LiteralPath $resultPath)) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    # При неизвестном результате не убивать исполнителя между key-down и finally/key-up.
    # Задание ограничено по времени; после обрыва его состояние проверяет оператор.
}
