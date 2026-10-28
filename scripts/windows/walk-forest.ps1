param([Parameter(Mandatory = $true)][string]$OutputDirectory)
__WINDOW_COMMON__
Add-Type -TypeDefinition ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__NATIVE_BASE64__')))
$plan = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__PLAN_BASE64__')) | ConvertFrom-Json
$mutex = New-Object Threading.Mutex($false, 'Local\ValheimVisionInput')
$owned = $false
$held = $false
$dpi = [ValheimWindow]::SetThreadDpiAwarenessContext([IntPtr](-4))
$timer = [Diagnostics.Stopwatch]::StartNew()
$events = New-Object 'Collections.Generic.List[object]'
$index = [int]$plan.start
$serial = 0
$frameNumber = 0
$expected = $null
$result = @{status='inconclusive';input_performed=$false;next_index=$index}

function Assert-Window {
    if (Test-Path (Join-Path $env:LOCALAPPDATA 'ValheimVision\cancel-input')) { throw 'input_cancelled' }
    $target = Get-Target
    if ($target.executable -ine $plan.executable -or $target.width -ne 1280 -or
        $target.height -ne 720 -or $target.dpi -ne 96) { throw 'profile_mismatch' }
    $identity = $target | ConvertTo-Json -Compress
    if ($null -ne $script:expected -and $identity -cne $script:expected) { throw 'window_changed' }
    $script:expected = $identity
    return $target
}

function Frame {
    $target = Assert-Window
    $bitmap = New-Object Drawing.Bitmap(1280,720)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $begin = [Diagnostics.Stopwatch]::GetTimestamp()
    try {
        $graphics.CopyFromScreen($target.x,$target.y,0,0,$bitmap.Size,[Drawing.CopyPixelOperation]::SourceCopy)
        $null = Assert-Window
        $arms = @(@(639,354,639,352),@(640,354,640,352),@(639,365,639,367),@(640,365,640,367),
            @(634,359,632,359),@(634,360,632,360),@(645,359,647,359),@(645,360,647,360))
        $hits = 0; $contrast = 0
        foreach ($a in $arms) {
            $p = $bitmap.GetPixel($a[0],$a[1]); $q = $bitmap.GetPixel($a[2],$a[3])
            $minimum = [Math]::Min($p.R,[Math]::Min($p.G,$p.B))
            $maximum = [Math]::Max($p.R,[Math]::Max($p.G,$p.B))
            if ($minimum -ge 70 -and $maximum-$minimum -le 60) { $hits++ }
            if (($p.R+$p.G+$p.B-$q.R-$q.G-$q.B)/3.0 -ge 25) { $contrast++ }
        }
        $script:frameNumber++
        $file = 'frame-{0:D3}.png' -f $script:frameNumber
        $path = Join-Path $OutputDirectory $file
        $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
        Copy-Item $path (Join-Path $OutputDirectory 'frame.png') -Force
        $observation = @{file=$file;sha256=(Get-FileHash $path).Hash.ToLower();target=$target;
            captured_at_utc=[DateTime]::UtcNow.ToString('o');capture_started_qpc=$begin;
            capture_finished_qpc=[Diagnostics.Stopwatch]::GetTimestamp();
            qpc_frequency=[Diagnostics.Stopwatch]::Frequency;
            method='calibrated-crosshair-v1';hits=$hits;contrast_hits=$contrast}
        $observation | ConvertTo-Json -Depth 6 | Set-Content ($path+'.json') -Encoding UTF8
        if ($hits -lt 7 -or $contrast -lt 6) { throw 'unknown_screen' }
        return $observation
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

function Request([string]$action, [hashtable]$extra = @{}) {
    $script:serial++
    $id = ('walk-'+(Split-Path $OutputDirectory -Leaf)+'-'+$script:serial)
    $folder = Join-Path (Split-Path $plan.executable -Parent) 'BepInEx\config\ForestCryptQA'
    $path = Join-Path $folder 'request.json'; $answer = Join-Path $folder ($id+'.json')
    if ((Test-Path $path) -or (Test-Path $answer)) { throw 'fixture_request_conflict' }
    $command = @{id=$id;action=$action}
    foreach ($key in $extra.Keys) { $command[$key]=$extra[$key] }
    [IO.File]::WriteAllText(($path+'.tmp'),($command | ConvertTo-Json -Compress),
        (New-Object Text.UTF8Encoding($false)))
    Move-Item ($path+'.tmp') $path
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path $answer) -and $wait.ElapsedMilliseconds -lt 2500) {
        $null = Assert-Window
        Start-Sleep -Milliseconds 30
    }
    if (!(Test-Path $answer)) { throw 'fixture_timeout' }
    $value = Get-Content $answer -Raw | ConvertFrom-Json
    if (!$value.result.ok) { throw ('fixture_error: '+$value.result.error) }
    return $value.result.value
}

function State {
    $state = Request 'status'
    if (!$state.exists -or $state.dead -or $state.teleporting -or $state.inventory_open) { throw 'player_not_ready' }
    if (!$state.on_ground -or @($state.ground).Count -ne 3) { throw 'floor_not_confirmed' }
    return $state
}

try {
    if ($dpi -eq [IntPtr]::Zero) { throw 'physical_dpi_context_unavailable' }
    $owned = $mutex.WaitOne(0)
    if (!$owned) { throw 'input_executor_busy' }
    if (@($plan.points).Count -gt 500 -or $index -lt 0 -or $index -gt @($plan.points).Count) {
        throw 'invalid_route'
    }
    $state = $null
    $observation = Frame
    $state = State
    while ($index -lt @($plan.points).Count -and $timer.Elapsed.TotalSeconds -lt 18) {
        $point = $plan.points[$index]; $position = $state.position
        $dx = $point.position[0]-$position[0]; $dz = $point.position[2]-$position[2]
        $distance = [Math]::Sqrt($dx*$dx+$dz*$dz)
        if ($distance -lt 0.6) {
            if ([Math]::Abs($point.position[1]-$position[1]) -gt 1.1) { throw 'waypoint_height_mismatch' }
            $events.Add(@{kind='waypoint';index=$index;state=$state;frame=$observation.file})
            $index++; continue
        }
        $yaw = ([Math]::Atan2($dx,$dz)*180/[Math]::PI+360)%360
        $delta = ($yaw-$state.yaw+540)%360-180
        if ([Math]::Abs($delta) -gt 3) {
            $null = Assert-Window
            $events.Add(@{kind='aim';yaw=$yaw;pitch=8;before=$observation.file})
            $null = Request 'aim' @{yaw=$yaw;pitch=8}
            Start-Sleep -Milliseconds 100
            $observation = Frame
            $state = State
        }
        for ($key=1; $key -lt 256; $key++) {
            if ([ValheimInput]::GetAsyncKeyState($key) -lt 0) { throw 'user_key_held' }
        }
        $idleWait = [Diagnostics.Stopwatch]::StartNew()
        while ([ValheimInput]::IdleMilliseconds() -lt 500) {
            if ($idleWait.ElapsedMilliseconds -gt 1000) { throw 'recent_manual_input' }
            $null = Assert-Window
            Start-Sleep -Milliseconds 30
        }
        if (([Diagnostics.Stopwatch]::GetTimestamp()-$observation.capture_finished_qpc)/
            [double]$observation.qpc_frequency -gt 5) { throw 'observation_expired' }
        $duration = [int][Math]::Max(100,[Math]::Min(1200,($distance-0.2)/2.2*1000))
        $actionId = [Guid]::NewGuid().ToString('N')
        $receipt = @{id=$actionId;key=87;hold_ms=$duration;before=$observation;waypoint=$index;
            started_at_utc=[DateTime]::UtcNow.ToString('o')}
        $receipt | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory ($actionId+'.json')) -Encoding UTF8
        $null = Assert-Window
        $held = $true; $result.input_performed=$true
        [ValheimInput]::Key(87,$false,$false)
        $hold = [Diagnostics.Stopwatch]::StartNew()
        try {
            while ($hold.ElapsedMilliseconds -lt $duration) {
                $null = Assert-Window
                Start-Sleep -Milliseconds 20
            }
        } finally { [ValheimInput]::Key(87,$true,$false); $held=$false }
        Start-Sleep -Milliseconds 250
        $observation = Frame
        $after = State
        $moved = [Math]::Sqrt([Math]::Pow($after.position[0]-$position[0],2)+
            [Math]::Pow($after.position[1]-$position[1],2)+[Math]::Pow($after.position[2]-$position[2],2))
        $events.Add(@{kind='move';action_id=$actionId;index=$index;moved=$moved;state=$after;frame=$observation.file})
        if ($moved -lt 0.1 -or $moved -gt 8) { throw 'movement_anomaly' }
        if ($after.position[1] -lt [Math]::Min($position[1],$point.position[1])-1.2) { throw 'fall' }
        $state = $after
    }
    $result.status = if ($index -eq @($plan.points).Count) { 'passed' } else { 'partial' }
} catch { $result.error=$_.Exception.Message }
finally {
    if ($held) { [ValheimInput]::Key(87,$true,$false) }
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    if ($dpi -ne [IntPtr]::Zero) { [void][ValheimWindow]::SetThreadDpiAwarenessContext($dpi) }
    $result.next_index=$index; $result.events=@($events.ToArray())
    $result.elapsed_seconds=$timer.Elapsed.TotalSeconds
    $result | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputDirectory 'result.json') -Encoding UTF8
}
