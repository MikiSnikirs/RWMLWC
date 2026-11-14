# Test game windows on a chosen monitor (so the person can keep using the main one).
# Dot-source and call Set-TestWindow on the COPY of Prefs.xml of a test instance.
# Enabled only when the user environment variable RWMLWC_TEST_MONITOR is set (1 = primary, 2 = second, ...).
# Windowed 960x540; instance 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right of that monitor.
# Messages are ASCII on purpose: PowerShell 5.1 misreads UTF-8 scripts without BOM.

function Get-TestMonitor {
    $v = [Environment]::GetEnvironmentVariable('RWMLWC_TEST_MONITOR', 'User')
    if (-not $v) { $v = $env:RWMLWC_TEST_MONITOR }
    $n = 0
    if (-not $v -or -not [int]::TryParse($v, [ref]$n) -or $n -lt 1) { return $null }
    Add-Type -AssemblyName System.Windows.Forms
    $screens = @([System.Windows.Forms.Screen]::AllScreens | Sort-Object { $_.Bounds.X }, { $_.Bounds.Y })
    $primary = $screens | Where-Object { $_.Primary } | Select-Object -First 1
    $others = @($screens | Where-Object { -not $_.Primary })
    $ordered = @($primary) + $others
    if ($n -gt $ordered.Count) { return $null }
    return $ordered[$n - 1].WorkingArea
}

function Set-TestWindow([string]$PrefsPath, [int]$Index) {
    $area = Get-TestMonitor
    if (-not $area -or -not (Test-Path -LiteralPath $PrefsPath)) { return }
    $w = [Math]::Floor($area.Width / 2); $h = [Math]::Floor($area.Height / 2)
    $x = $area.X + ($Index % 2) * $w; $y = $area.Y + ([Math]::Floor($Index / 2) % 2) * $h
    $px = [xml](Get-Content -LiteralPath $PrefsPath -Raw -Encoding UTF8)
    foreach ($pair in @(@('fullscreen', 'False'), @('screenWidth', "$w"), @('screenHeight', "$h"), @('windowPositionX', "$x"), @('windowPositionY', "$y"))) {
        $node = $px.PrefsData.SelectSingleNode($pair[0])
        if (-not $node) { $node = $px.CreateElement($pair[0]); [void]$px.PrefsData.AppendChild($node) }
        $node.InnerText = $pair[1]
    }
    $px.Save($PrefsPath)
    Write-Host "test window $Index -> ${w}x${h} at $x,$y"
}
