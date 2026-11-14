# TST-014: one or two game clients driven by the session server, on one machine.
#
#   powershell -ExecutionPolicy Bypass -File tools\run-tst014.ps1            (one client)
#   powershell -ExecutionPolicy Bypass -File tools\run-tst014.ps1 -Clients 2
#   ... -Clients 2 -Scenario designate -Name tst020      (TST-020: client c1 issues designations)
#   ... -Clients 2 -Scenario join -Name tst045           (TST-045: c2 joins a running game from the main menu)
#
# Builds the mod, starts the server, starts the game with a separate data folder per client
# (-savedatafolder), waits until the game closes itself, stops the server and copies the results
# to <game data>\RWMLWC\<Name>\ . Your normal game data is only read (Config and one save).
#
# Messages are ASCII on purpose: PowerShell 5.1 misreads UTF-8 scripts without BOM.

param(
    [int]$Clients = 1,
    [int]$Ticks = 3000,
    [int]$StartTick = 14998,
    [string]$Save = '',
    [int]$HoldSec = 60,
    [int]$TimeoutMin = 15,
    [string]$Scenario = 'none',
    [string]$Name = 'tst014',
    [int]$HostTicks = 40000,
    [int]$ConnectDelaySec = 45,
    [int[]]$Tpf = @(),          # ticks per frame cap per client, e.g. -Tpf 1,30 (0 = normal)
    [switch]$Trace,             # Rand trace (slow); with -Tpf the tempo stays fixed
    [int]$DenseFrom = -1,       # hash every tick in this window (ticks from run start)
    [int]$DenseTo = -1,
    [int]$Camera = -1,          # camera wander for c2: 1/0 (default 1)
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
function Step($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Fail($t) { Write-Host "FAILED: $t" -ForegroundColor Red; exit 1 }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (-not $NoBuild) {
    Step 'Building (tools\setup.ps1)'
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'setup.ps1')
    if ($LASTEXITCODE -ne 0) { Fail 'Build failed. Send build.log to the agent.' }
}

$game = [Environment]::GetEnvironmentVariable('RIMWORLD_DIR', 'User')
if (-not $game) { $game = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld' }
$exe = Join-Path $game 'RimWorld.exe'
if (-not (Test-Path $exe)) { Fail "RimWorld.exe not found in '$game'." }
# Without a running Steam client the game does not see Workshop mods (Harmony), drops the mod list
# and the run hangs in the main menu until the timeout.
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) {
    Fail 'Steam is not running. Start Steam (Harmony comes from the Workshop), then run again.'
}

$data = Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios'
if (-not $Save) {
    # Default test save name, spelled by character codes to keep this file ASCII.
    $Save = (-join [char[]](0x41F, 0x43E, 0x441, 0x435, 0x43B, 0x435, 0x43D, 0x438, 0x435)) + '5'
}
$saveFile = Join-Path $data "Saves\$Save.rws"
if (-not (Test-Path -LiteralPath $saveFile)) { Fail "Save not found: $saveFile" }

$serverDll = Get-ChildItem (Join-Path $repo 'Source\Server\bin') -Recurse -Filter 'RWMLWC.Server.dll' -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -match '\\Release\\' } | Select-Object -First 1
if (-not $serverDll) { Fail 'RWMLWC.Server.dll not found (build first).' }

$work = Join-Path $env:LOCALAPPDATA "RWMLWC-test\$Name"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null

Step 'Starting the session server'
$srvOut = Join-Path $work 'server-out.txt'
$srvErr = Join-Path $work 'server-err.txt'
$srvArgs = @("`"$($serverDll.FullName)`"", '--port', '0', '--data', "`"$(Join-Path $work 'server')`"", '--start-tick', "$StartTick", '--seed', '7')
$srv = Start-Process dotnet -ArgumentList $srvArgs -RedirectStandardOutput $srvOut -RedirectStandardError $srvErr -PassThru -WindowStyle Hidden
$port = $null; $thumb = $null
for ($i = 0; $i -lt 40 -and -not $port; $i++) {
    Start-Sleep -Milliseconds 500
    if (Test-Path $srvOut) {
        $m = Get-Content $srvOut | Select-String '^LISTENING (\d+) (\S+)' | Select-Object -First 1
        if ($m) { $port = $m.Matches[0].Groups[1].Value; $thumb = $m.Matches[0].Groups[2].Value }
    }
    if ($srv.HasExited) { break }
}
if (-not $port) {
    if (-not $srv.HasExited) { Stop-Process -Id $srv.Id -Force }
    Get-Content $srvOut, $srvErr -ErrorAction SilentlyContinue | Select-Object -Last 20
    Fail 'The server did not print LISTENING.'
}
Write-Host "server pid $($srv.Id), port $port"

Step "Starting $Clients game client(s)"
$hold = 0
if ($Clients -gt 1) { $hold = $HoldSec }
$procs = @()
for ($c = 1; $c -le $Clients; $c++) {
    $dir = Join-Path $work "c$c"
    New-Item -ItemType Directory -Path (Join-Path $dir 'Saves'), (Join-Path $dir 'RWMLWC') | Out-Null
    Copy-Item (Join-Path $data 'Config') (Join-Path $dir 'Config') -Recurse
    # Test runs are vanilla + DLC + Harmony + RWMLWC regardless of the mod list of the normal game (copy only).
    $mc = Join-Path $dir 'Config\ModsConfig.xml'
    $xml = [xml](Get-Content -LiteralPath $mc -Raw -Encoding UTF8)
    $active = $xml.ModsConfigData.activeMods
    foreach ($li in @($active.li)) {
        if ($li -notmatch '^(brrainz\.harmony|ludeon\.rimworld(\..+)?)$') {
            $node = $active.SelectNodes('li') | Where-Object { $_.InnerText -eq $li }
            foreach ($n in @($node)) { [void]$active.RemoveChild($n) }
        }
    }
    $add = $xml.CreateElement('li'); $add.InnerText = 'mikisnikirs.rwmlwc'; [void]$active.AppendChild($add)
    $xml.Save($mc)
    # Two windows share the screen: the one without focus must keep running (Options: "Run in background").
    $pf = Join-Path $dir 'Config\Prefs.xml'
    if (Test-Path -LiteralPath $pf) {
        $px = [xml](Get-Content -LiteralPath $pf -Raw -Encoding UTF8)
        $node = $px.PrefsData.SelectSingleNode('runInBackground')
        if (-not $node) { $node = $px.CreateElement('runInBackground'); [void]$px.PrefsData.AppendChild($node) }
        $node.InnerText = 'True'
        $px.Save($pf)
    }
    Copy-Item -LiteralPath $saveFile (Join-Path $dir 'Saves\tst014.rws')
    $armTicks = $Ticks
    $armScenario = $Scenario
    $armHold = $hold
    $extra = @()
    $saveLine = 'save=tst014'
    if ($Scenario -eq 'join') {
        # TST-045: c1 plays alone and serves the world snapshot; c2 joins from the main menu without a save.
        $armScenario = 'none'
        $armHold = 0
        if ($c -eq 1) { $armTicks = $HostTicks; $extra = @('snapsender=1') }
        else { $saveLine = 'save='; $extra = @("connectdelay=$ConnectDelaySec") }
    }
    if ($Scenario -eq 'squads') {
        # TST-060: c1 edits squads and asks for a common snapshot, so c1 must be able to serve it.
        if ($c -eq 1) { $extra = @('snapsender=1') }
    }
    if ($Scenario -eq 'desync') {
        # TST-034: c2 corrupts its own state; c1 holds the snapshot and starts the common reload.
        if ($c -eq 1) { $extra = @('snapsender=1') } else { $extra = @('desync=1') }
    }
    $arm = @(
        'armed=1', $saveLine, "scenarios=$armScenario", "ticks=$armTicks", "trace=$(if ($Trace) { 1 } else { 0 })", 'quit=1',
        "server=127.0.0.1:$port", "thumbprint=$thumb", "name=c$c", "holdsec=$armHold", "issuer=$(if ($c -eq 1) { 1 } else { 0 })",
        "camera=$(if ($c -eq 2 -and $Camera -ne 0) { 1 } else { 0 })"
    ) + $extra
    if ($Tpf.Count -ge $c) { $arm += "tpf=$($Tpf[$c - 1])" }
    if ($DenseFrom -ge 0 -and $DenseTo -gt $DenseFrom) { $arm += "densefrom=$DenseFrom"; $arm += "denseto=$DenseTo" }
    [IO.File]::WriteAllLines((Join-Path $dir 'RWMLWC\arm-determinism-run.txt'), $arm)
    [IO.File]::WriteAllText((Join-Path $dir 'RWMLWC\automation-progress.txt'), '0')
    $procs += Start-Process $exe -ArgumentList "-savedatafolder=$dir" -WorkingDirectory $game -PassThru
    Write-Host "client c$c pid $($procs[-1].Id), data $dir"
}

Step "Waiting for the game to close itself (up to $TimeoutMin min)"
$deadline = (Get-Date).AddMinutes($TimeoutMin)
while ((Get-Date) -lt $deadline -and ($procs | Where-Object { -not $_.HasExited })) { Start-Sleep -Seconds 2 }
$timedOut = $false
foreach ($p in $procs) {
    if (-not $p.HasExited) { $timedOut = $true; Stop-Process -Id $p.Id -Force }
}
Start-Sleep -Seconds 1
if (-not $srv.HasExited) { Stop-Process -Id $srv.Id -Force }

Step 'Collecting results'
$dest = Join-Path $data "RWMLWC\$Name"
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
New-Item -ItemType Directory -Path $dest | Out-Null
Copy-Item $srvOut, $srvErr $dest -ErrorAction SilentlyContinue
for ($c = 1; $c -le $Clients; $c++) {
    $src = Join-Path $work "c$c"
    Copy-Item (Join-Path $src 'RWMLWC') (Join-Path $dest "c$c") -Recurse
    Get-ChildItem $src -Filter '*.log' -ErrorAction SilentlyContinue | Copy-Item -Destination (Join-Path $dest "c$c")
    Write-Host "--- c$c automation-log (tail)"
    Get-Content (Join-Path $dest "c$c\automation-log.txt") -ErrorAction SilentlyContinue | Select-Object -Last 12
}
Write-Host '--- server (tail)'
Get-Content (Join-Path $dest 'server-out.txt') -ErrorAction SilentlyContinue | Select-Object -Last 12

$failedLines = @()
for ($c = 1; $c -le $Clients; $c++) {
    $failedLines += @(Get-Content (Join-Path (Join-Path $dest "c$c") 'automation-log.txt') -ErrorAction SilentlyContinue | Select-String -CaseSensitive 'FAILED|LATE' | ForEach-Object { "c$c " + $_.Line })
}
if ($failedLines.Count -gt 0) {
    Write-Host "`nRUN PROBLEMS:" -ForegroundColor Yellow
    $failedLines | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
}
if ($timedOut) { Write-Host "`nTIMED OUT: the game did not close itself; it was stopped." -ForegroundColor Yellow }
Write-Host "`nDONE. Results: $dest" -ForegroundColor Green
Write-Host 'Tell the agent: gotovo.'
