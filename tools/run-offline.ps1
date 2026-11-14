# Offline determinism run (no server): one game instance, a queue of scenarios, its own data folder.
#
#   powershell -ExecutionPolicy Bypass -File tools\run-offline.ps1 -Queue raid,raid -Ticks 8000 -Name tst018
#   ... -Queue none,ui -Ticks 3000 -Name tst024
#
# With -PerProcess (default on) the game restarts itself between queue items. The script waits until
# automation-log.txt says "queue: finished" and no game process of this data folder is left, then copies
# the results to <game data>\RWMLWC\<Name>\ . Your normal game data is only read (Config and one save).
#
# Messages are ASCII on purpose: PowerShell 5.1 misreads UTF-8 scripts without BOM.

param(
    [string[]]$Queue = @('none', 'none'),
    [int]$Ticks = 10000,
    [string]$Save = '',
    [string]$Name = 'offline',
    [int]$TimeoutMin = 30,
    [bool]$PerProcess = $true,
    [switch]$Trace,
    [int]$DenseFrom = -1,
    [int]$DenseTo = -1,
    [switch]$Camera,
    [switch]$HostSession,       # TST-080: open a session in this game for a Steam friend
    [int]$HoldSec = 180,        #   ... and hold the start until the friend is in
    [switch]$JoinSteamFriend,   # TST-080: from the main menu, join the Steam friend who hosts a session
    [int]$ConnectDelaySec = 20
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestWindow.ps1')
function Step($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Fail($t) { Write-Host "FAILED: $t" -ForegroundColor Red; exit 1 }

$game = [Environment]::GetEnvironmentVariable('RIMWORLD_DIR', 'User')
if (-not $game) { $game = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld' }
$exe = Join-Path $game 'RimWorld.exe'
if (-not (Test-Path $exe)) { Fail "RimWorld.exe not found in '$game'." }
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) {
    Fail 'Steam is not running. Start Steam (Harmony comes from the Workshop), then run again.'
}

$data = Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios'
if (-not $Save) { $Save = (-join [char[]](0x41F, 0x43E, 0x441, 0x435, 0x43B, 0x435, 0x43D, 0x438, 0x435)) + '5' }
$saveFile = Join-Path $data "Saves\$Save.rws"
if (-not (Test-Path -LiteralPath $saveFile)) { Fail "Save not found: $saveFile" }

$dir = Join-Path $env:LOCALAPPDATA "RWMLWC-test\$Name"
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $dir 'Saves'), (Join-Path $dir 'RWMLWC') | Out-Null
Copy-Item (Join-Path $data 'Config') (Join-Path $dir 'Config') -Recurse

# Vanilla + DLC + Harmony + RWMLWC (copy only), game keeps running without focus.
$mc = Join-Path $dir 'Config\ModsConfig.xml'
$xml = [xml](Get-Content -LiteralPath $mc -Raw -Encoding UTF8)
$active = $xml.ModsConfigData.activeMods
foreach ($li in @($active.li)) {
    if ($li -notmatch '^(brrainz\.harmony|ludeon\.rimworld(\..+)?)$') {
        foreach ($n in @($active.SelectNodes('li') | Where-Object { $_.InnerText -eq $li })) { [void]$active.RemoveChild($n) }
    }
}
$add = $xml.CreateElement('li'); $add.InnerText = 'mikisnikirs.rwmlwc'; [void]$active.AppendChild($add)
$xml.Save($mc)
$pf = Join-Path $dir 'Config\Prefs.xml'
if (Test-Path -LiteralPath $pf) {
    $px = [xml](Get-Content -LiteralPath $pf -Raw -Encoding UTF8)
    $node = $px.PrefsData.SelectSingleNode('runInBackground')
    if (-not $node) { $node = $px.CreateElement('runInBackground'); [void]$px.PrefsData.AppendChild($node) }
    $node.InnerText = 'True'
    # Test instances are silent (the person may be playing something else meanwhile).
    $vol = $px.PrefsData.SelectSingleNode('volumeMaster')
    if (-not $vol) { $vol = $px.CreateElement('volumeMaster'); [void]$px.PrefsData.AppendChild($vol) }
    $vol.InnerText = '0'
    $px.Save($pf)
}
Set-TestWindow $pf (0)   # RWMLWC_TEST_MONITOR: windows on another monitor (tools/TestWindow.ps1)
Copy-Item -LiteralPath $saveFile (Join-Path $dir 'Saves\offline.rws')

$arm = @('armed=1', 'save=offline', "scenarios=$($Queue -join ',')", "ticks=$Ticks", "trace=$(if ($Trace) { 1 } else { 0 })",
         'quit=1', "perprocess=$(if ($PerProcess) { 1 } else { 0 })", "camera=$(if ($Camera) { 1 } else { 0 })")
if ($DenseFrom -ge 0 -and $DenseTo -gt $DenseFrom) { $arm += "densefrom=$DenseFrom"; $arm += "denseto=$DenseTo" }
# Two machines, no server program (docs/TESTS.md, TST-080): the host opens a session inside its game and waits
# for a Steam friend; the friend starts in the main menu and joins whoever of its Steam friends hosts a session.
if ($HostSession) { $arm += 'hostsession=1'; $arm += "holdsec=$HoldSec"; $arm += 'name=hostA' }
if ($JoinSteamFriend) {
    $arm = @($arm | Where-Object { $_ -ne 'save=offline' -and $_ -notlike 'perprocess=*' }) + @('save=', 'steamhost=auto', "connectdelay=$ConnectDelaySec", 'name=friendB')
}
[IO.File]::WriteAllLines((Join-Path $dir 'RWMLWC\arm-determinism-run.txt'), $arm)
[IO.File]::WriteAllText((Join-Path $dir 'RWMLWC\automation-progress.txt'), '0')

. (Join-Path $PSScriptRoot 'StartQuiet.ps1')
$blockers = Get-GameBlockers
if ($blockers.Count -gt 0) {
    Fail "Not starting: $($blockers -join ', ') is running (a fullscreen game would minimize). Run again later; see RWMLWC_TEST_BLOCKERS."
}

Step "Starting the game: queue $($Queue -join ','), $Ticks ticks each"
# Without taking focus from the person's own game (tools/StartQuiet.ps1).
. (Join-Path $PSScriptRoot 'StartQuiet.ps1')
[RwmlwcQuiet]::Guard()   # remember the person's window before the game takes it
$p = Start-GameQuiet $exe "-savedatafolder=$dir" $game
Write-Host "pid $($p.Id), data $dir"
[void](Show-GameQuiet $p)

function GameProcs {
    @(Get-CimInstance Win32_Process -Filter "Name='RimWorld.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.CommandLine -and $_.CommandLine.Contains($dir) })
}

Step "Waiting for the queue to finish (up to $TimeoutMin min)"
$log = Join-Path $dir 'RWMLWC\automation-log.txt'
$deadline = (Get-Date).AddMinutes($TimeoutMin)
$finished = $false
while ((Get-Date) -lt $deadline) {
    Wait-GuardingFocus 3
    $finished = (Test-Path $log) -and (Select-String -Path $log -Pattern 'queue: finished' -Quiet)
    if ($finished -and (GameProcs).Count -eq 0) { break }
    # A restart has a short gap with no process; give up only when there is none for a while.
    if (-not $finished -and (GameProcs).Count -eq 0) {
        Wait-GuardingFocus 20
        if ((GameProcs).Count -eq 0 -and -not (Select-String -Path $log -Pattern 'queue: finished' -Quiet)) { break }
    }
}
$timedOut = (Get-Date) -ge $deadline
foreach ($gp in GameProcs) { Stop-Process -Id $gp.ProcessId -Force }

Step 'Collecting results'
$dest = Join-Path $data "RWMLWC\$Name"
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Copy-Item (Join-Path $dir 'RWMLWC') $dest -Recurse
Get-Content (Join-Path $dest 'automation-log.txt') -ErrorAction SilentlyContinue | Select-Object -Last 15
if ($timedOut) { Write-Host "`nTIMED OUT: the queue did not finish; the game was stopped." -ForegroundColor Yellow }
elseif (-not $finished) { Write-Host "`nThe game closed before the queue finished." -ForegroundColor Yellow }
Write-Host "`nDONE. Results: $dest" -ForegroundColor Green
