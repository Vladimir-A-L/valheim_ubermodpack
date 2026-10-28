param([Parameter(Mandatory = $true)][string]$OutputDirectory)
__WINDOW_COMMON__
Add-Type -TypeDefinition ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__NATIVE_BASE64__')))
$request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__REQUEST_BASE64__')) | ConvertFrom-Json
$result = [ordered]@{schema_version=1;status='refused';input_performed=$false;action_id=$request.action_id}
$mutex = New-Object Threading.Mutex($false, 'Local\ValheimVisionInput')
$owned = $false
$held = New-Object 'Collections.Generic.List[int]'
$mouseHeld = $false
$dpi = [ValheimWindow]::SetThreadDpiAwarenessContext([IntPtr](-4))
function Assert-Target {
    if (Test-Path (Join-Path $env:LOCALAPPDATA 'ValheimVision\cancel-input')) { throw 'input_cancelled' }
    $current = Get-Target
    foreach ($property in $source.target.PSObject.Properties) {
        if ([string]$current[$property.Name] -cne [string]$property.Value) { throw 'target_changed' }
    }
}
function Wait-Checked([int]$milliseconds) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do { Assert-Target; Start-Sleep -Milliseconds 20 } while ($watch.ElapsedMilliseconds -lt $milliseconds)
}
try {
    if ($dpi -eq [IntPtr]::Zero) { throw 'physical_dpi_context_unavailable' }
    $owned = $mutex.WaitOne(0)
    if (-not $owned) { throw 'input_executor_busy' }
    if ($request.observation_run_id -cnotmatch '^[0-9a-f]{32}$' -or
        $request.action_id -cnotmatch '^[0-9a-f]{32}$') { throw 'invalid_request_id' }
    $sourceRoot = Join-Path $env:LOCALAPPDATA ('ValheimVision\' + $request.observation_run_id)
    $source = Get-Content (Join-Path $sourceRoot 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($source.status -ne 'captured' -or $source.image_sha256 -cne $request.image_sha256) {
        throw 'source_observation_mismatch'
    }
    $age = ([Diagnostics.Stopwatch]::GetTimestamp() - [long]$source.capture_finished_qpc) /
        [double]$source.qpc_frequency
    if ($request.max_age_seconds -le 0 -or $request.max_age_seconds -gt 120 -or
        $age -lt 0 -or $age -gt $request.max_age_seconds) { throw 'observation_expired' }
    Assert-Target
    for ($key = 1; $key -lt 256; $key++) {
        if ([ValheimInput]::GetAsyncKeyState($key) -lt 0) { throw 'user_key_held' }
    }
    if ([ValheimInput]::IdleMilliseconds() -lt 500) { throw 'recent_manual_input' }
    $action = $request.action
    if ($action.kind -notin @('click','move','look','key','text')) { throw 'unknown_action' }
    if ($action.kind -in @('click','move') -and ($action.x -lt 0 -or $action.y -lt 0 -or
        $action.x -ge $source.target.width -or $action.y -ge $source.target.height)) { throw 'outside_client' }
    if ($action.kind -eq 'look' -and ($null -eq $action.dx -or $null -eq $action.dy -or
        [Math]::Abs($action.dx) -gt 200 -or [Math]::Abs($action.dy) -gt 200)) { throw 'invalid_look_action' }
    if ($action.kind -eq 'key' -and ($action.hold_ms -lt 20 -or $action.hold_ms -gt 1500 -or
        @($action.keys).Count -lt 1 -or @($action.keys).Count -gt 2)) { throw 'invalid_key_action' }
    if ($action.kind -eq 'key') {
        $allowed = @(8,9,13,16,17,27,32,37,38,39,40,112,113,114,115,116) + @(48..90)
        foreach ($key in $action.keys) { if ($key -notin $allowed) { throw 'key_not_allowed' } }
        if (@($action.keys | Select-Object -Unique).Count -ne @($action.keys).Count) { throw 'duplicate_key' }
        if (17 -in $action.keys -and 27 -in $action.keys) { throw 'system_shortcut_not_allowed' }
    }
    if ($action.kind -eq 'text' -and ($action.text.Length -lt 1 -or $action.text.Length -gt 128 -or
        $action.text -match '[\x00-\x1f\x7f]')) { throw 'invalid_text_action' }
    # Запись плана и одноразовой квитанции до первого события; повтор после обрыва запрещён.
    $receipt = Join-Path $env:LOCALAPPDATA ('ValheimVision\action-' + $request.action_id + '.json')
    if (Test-Path -LiteralPath $receipt) { throw 'action_already_attempted' }
    $request | Add-Member -NotePropertyName run_id -NotePropertyValue (Split-Path $OutputDirectory -Leaf)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 8))
    $file = [IO.File]::Open($receipt, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
    $result.status = 'inconclusive'
    $result.started_at_utc = [DateTime]::UtcNow.ToString('o')
    Assert-Target
    $result.input_performed = $true
    switch ($action.kind) {
        'look' {
            [ValheimInput]::Look($action.dx, $action.dy)
            Wait-Checked 250
        }
        'move' {
            if (-not [ValheimInput]::SetCursorPos(($source.target.x + $action.x),
                ($source.target.y + $action.y))) { throw 'cursor_move_failed' }
            Wait-Checked 250
        }
        'click' {
            if (-not [ValheimInput]::SetCursorPos(($source.target.x + $action.x),
                ($source.target.y + $action.y))) { throw 'cursor_move_failed' }
            Wait-Checked 250
            $mouseHeld = $true
            [ValheimInput]::Button($false)
            Wait-Checked 180
            [ValheimInput]::Button($true)
            $mouseHeld = $false
        }
        'key' {
            foreach ($key in $action.keys) {
                Assert-Target
                $held.Add([int]$key)
                [ValheimInput]::Key($key, $false, $false)
            }
            Wait-Checked $action.hold_ms
        }
        'text' {
            foreach ($character in $action.text.ToCharArray()) {
                Assert-Target
                try { [ValheimInput]::Key([uint16]$character, $false, $true) }
                finally { [ValheimInput]::Key([uint16]$character, $true, $true) }
            }
        }
    }
    Assert-Target
    $result.status = 'sent'
    $result.visual_verdict = 'unreviewed'
} catch { $result.error = $_.Exception.Message }
finally {
    $releaseErrors = @()
    foreach ($key in $held) {
        try { [ValheimInput]::Key($key, $true, $false) } catch { $releaseErrors += $_.Exception.Message }
    }
    if ($mouseHeld) { try { [ValheimInput]::Button($true) } catch { $releaseErrors += $_.Exception.Message } }
    if ($releaseErrors.Count) { $result.status='inconclusive'; $result.release_errors=$releaseErrors }
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    if ($dpi -ne [IntPtr]::Zero) { [void][ValheimWindow]::SetThreadDpiAwarenessContext($dpi) }
    $result.finished_at_utc = [DateTime]::UtcNow.ToString('o')
    $path = Join-Path $OutputDirectory 'result.json'
    [IO.File]::WriteAllText(($path + '.tmp'), ($result | ConvertTo-Json -Depth 6),
        (New-Object Text.UTF8Encoding($false)))
    Move-Item ($path + '.tmp') $path
}
