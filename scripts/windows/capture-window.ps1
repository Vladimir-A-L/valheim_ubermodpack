param([Parameter(Mandatory = $true)][string]$OutputDirectory)
__WINDOW_COMMON__

$result = [ordered]@{ schema_version = 1; status = 'failed'; input_performed = $false }
$bitmap = $null
$graphics = $null
$previousDpi = [ValheimWindow]::SetThreadDpiAwarenessContext([IntPtr](-4))
try {
    if ($previousDpi -eq [IntPtr]::Zero) { throw 'physical_dpi_context_unavailable' }
    $before = Get-Target
    $beforeJson = $before | ConvertTo-Json -Compress
    $result.captured_at_utc = [DateTime]::UtcNow.ToString('o')
    $result.capture_started_qpc = [Diagnostics.Stopwatch]::GetTimestamp().ToString()
    $result.qpc_frequency = [Diagnostics.Stopwatch]::Frequency.ToString()
    $bitmap = New-Object Drawing.Bitmap($before.width, $before.height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($before.x, $before.y, 0, 0, $bitmap.Size, [Drawing.CopyPixelOperation]::SourceCopy)
    $result.capture_finished_qpc = [Diagnostics.Stopwatch]::GetTimestamp().ToString()
    $after = Get-Target
    if ($beforeJson -cne ($after | ConvertTo-Json -Compress)) { throw 'target_changed_during_capture' }
    $imagePath = Join-Path $OutputDirectory 'frame.png'
    $bitmap.Save($imagePath, [Drawing.Imaging.ImageFormat]::Png)
    $result.target = $after
    $result.image_sha256 = (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $result.status = 'captured'
    # Захват пикселей не подтверждает содержание кадра, отсутствие перекрытия или результат игрового действия.
    $result.visual_verdict = 'unreviewed'
} catch {
    $result.error = $_.Exception.Message
} finally {
    if ($null -ne $graphics) { $graphics.Dispose() }
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if ($previousDpi -ne [IntPtr]::Zero) {
        [void][ValheimWindow]::SetThreadDpiAwarenessContext($previousDpi)
    }
    $destination = Join-Path $OutputDirectory 'result.json'
    $temporary = $destination + '.tmp'
    [IO.File]::WriteAllText($temporary, ($result | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $destination
}
