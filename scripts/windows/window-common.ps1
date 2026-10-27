$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ValheimWindow {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(
        IntPtr window, System.Text.StringBuilder name, int capacity);
    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
}
'@

function Get-Target {
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    if ($session -eq 0 -or $session -ne [ValheimWindow]::WTSGetActiveConsoleSessionId()) {
        throw 'interactive_console_required'
    }
    $candidates = @(Get-Process -Name valheim -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $session -and $_.MainWindowHandle -ne [IntPtr]::Zero })
    if ($candidates.Count -ne 1) { throw 'single_valheim_window_required' }
    $game = $candidates[0]
    $window = $game.MainWindowHandle
    $className = New-Object Text.StringBuilder 256
    [void][ValheimWindow]::GetClassName($window, $className, $className.Capacity)
    if ($className.ToString() -ne 'UnityWndClass') { throw 'not_game_window' }
    if ([ValheimWindow]::IsIconic($window) -or -not [ValheimWindow]::IsWindowVisible($window)) {
        throw 'window_not_visible'
    }
    if ([ValheimWindow]::GetForegroundWindow() -ne $window) { throw 'window_not_foreground' }
    $windowProcess = [uint32]0
    [void][ValheimWindow]::GetWindowThreadProcessId($window, [ref]$windowProcess)
    if ($windowProcess -ne $game.Id) { throw 'window_identity_changed' }
    $rect = New-Object ValheimWindow+Rect
    $origin = New-Object ValheimWindow+Point
    if (-not [ValheimWindow]::GetClientRect($window, [ref]$rect) -or
        -not [ValheimWindow]::ClientToScreen($window, [ref]$origin)) { throw 'geometry_unavailable' }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0 -or $width -gt 7680 -or $height -gt 4320) {
        throw 'invalid_geometry'
    }
    $monitor = [Windows.Forms.Screen]::FromHandle($window)
    $bounds = New-Object Drawing.Rectangle($origin.X, $origin.Y, $width, $height)
    if (-not $monitor.Bounds.Contains($bounds)) { throw 'window_outside_single_monitor' }
    return [ordered]@{
        pid = $game.Id
        process_started_at_utc = $game.StartTime.ToUniversalTime().ToString('o')
        executable = $game.Path
        hwnd = $window.ToInt64().ToString()
        session_id = $session
        x = $origin.X; y = $origin.Y; width = $width; height = $height
        dpi = [ValheimWindow]::GetDpiForWindow($window)
        monitor = $monitor.DeviceName
        monitor_x = $monitor.Bounds.X; monitor_y = $monitor.Bounds.Y
        monitor_width = $monitor.Bounds.Width; monitor_height = $monitor.Bounds.Height
        foreground = $true
    }
}

