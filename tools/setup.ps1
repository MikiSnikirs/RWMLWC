# RWMLWC one-step setup for Windows (PowerShell 5.1+).
# Finds the game and Harmony, sets RIMWORLD_DIR / HARMONY_PATH, builds the mod
# and links the Mod folder into the game's Mods folder.
#
# Run from a cloned repo:   powershell -ExecutionPolicy Bypass -File tools\setup.ps1
# Or standalone (no clone): the script fetches the repo into %USERPROFILE%\RWMLWC.
#
# Messages are ASCII on purpose: PowerShell 5.1 misreads UTF-8 scripts without BOM.

param(
    [string]$Branch = 'main',
    [string]$GameDir = $env:RIMWORLD_DIR
)

$ErrorActionPreference = 'Stop'
function Step($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Fail($t) { Write-Host "FAILED: $t" -ForegroundColor Red; exit 1 }

# --- 1. .NET SDK -------------------------------------------------------------
Step 'Checking .NET 10 SDK'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Fail '.NET SDK not found. Install .NET 10 SDK, open a NEW terminal, run again.' }
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    Fail ".NET 10 SDK not found. Installed SDKs:`n$($sdks -join "`n")"
}
Write-Host ($sdks | Where-Object { $_ -match '^10\.' } | Select-Object -First 1)

# --- 2. Repo -----------------------------------------------------------------
Step 'Locating repository'
$repo = $null
if ($PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot '..\RWMLWC.slnx'))) {
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
} else {
    $repo = Join-Path $env:USERPROFILE 'RWMLWC'
    if (-not (Test-Path (Join-Path $repo 'RWMLWC.slnx'))) {
        if (Get-Command git -ErrorAction SilentlyContinue) {
            & git clone --branch $Branch https://github.com/MikiSnikirs/RWMLWC.git $repo
            if ($LASTEXITCODE -ne 0) { Fail 'git clone failed.' }
        } else {
            $zip = Join-Path $env:TEMP 'rwmlwc.zip'
            $tmp = Join-Path $env:TEMP 'rwmlwc_unzip'
            Invoke-WebRequest "https://github.com/MikiSnikirs/RWMLWC/archive/refs/heads/$Branch.zip" -OutFile $zip
            if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
            Expand-Archive $zip -DestinationPath $tmp
            Move-Item (Get-ChildItem $tmp -Directory | Select-Object -First 1).FullName $repo
        }
    }
}
Write-Host $repo

# --- 3. Game -----------------------------------------------------------------
Step 'Locating RimWorld 1.7'
if (-not $GameDir) { $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld' }
if (-not (Test-Path (Join-Path $GameDir 'Assembly-CSharp.dll'))) {
    Fail "Assembly-CSharp.dll not found in '$GameDir'. Pass -GameDir '<path to RimWorld>'."
}
$ver = Get-Content (Join-Path $GameDir 'Version.txt') -ErrorAction SilentlyContinue
Write-Host "$GameDir  (version: $ver)"
if ($ver -notmatch '^1\.7') { Write-Host 'WARNING: game version is not 1.7' -ForegroundColor Yellow }

# --- 4. Harmony --------------------------------------------------------------
Step 'Locating Harmony 3 (0Harmony.dll for 1.7)'
$harmony = $env:HARMONY_PATH
if (-not $harmony -or -not (Test-Path $harmony)) {
    $ws = Join-Path $GameDir '..\..\workshop\content\294100\2009463077'
    $roots = @((Join-Path $ws '1.7'), (Join-Path $GameDir 'Mods'))
    $harmony = $null
    foreach ($r in $roots) {
        if (Test-Path $r) {
            $hit = Get-ChildItem $r -Recurse -Filter '0Harmony.dll' -ErrorAction SilentlyContinue |
                   Where-Object { $_.FullName -match '\\1\.7\\' } | Select-Object -First 1
            if ($hit) { $harmony = $hit.FullName; break }
        }
    }
}
if (-not $harmony) { Fail 'Harmony for 1.7 not found. Subscribe to the Harmony mod in Steam Workshop (id 2009463077) and let it download.' }
Write-Host $harmony

# --- 5. Environment ----------------------------------------------------------
Step 'Saving RIMWORLD_DIR and HARMONY_PATH (user environment)'
[Environment]::SetEnvironmentVariable('RIMWORLD_DIR', $GameDir, 'User')
[Environment]::SetEnvironmentVariable('HARMONY_PATH', $harmony, 'User')
$env:RIMWORLD_DIR = $GameDir
$env:HARMONY_PATH = $harmony

# --- 6. Build ----------------------------------------------------------------
Step 'Building'
$log = Join-Path $repo 'build.log'
& dotnet build (Join-Path $repo 'RWMLWC.slnx') -c Release 2>&1 | Tee-Object -FilePath $log
if ($LASTEXITCODE -ne 0) { Fail "Build failed. Send this file to the agent: $log" }
$dll = Join-Path $repo 'Mod\1.7\Assemblies\RWMLWC.dll'
if (-not (Test-Path $dll)) { Fail "Build reported success but $dll is missing. Send $log to the agent." }

Step 'Running unit tests'
& dotnet test (Join-Path $repo 'Source\Tests\RWMLWC.Tests.csproj') -c Release 2>&1 | Tee-Object -FilePath $log -Append
if ($LASTEXITCODE -ne 0) { Write-Host 'Unit tests failed (see build.log).' -ForegroundColor Yellow }

# --- 7. Link mod into the game ----------------------------------------------
Step 'Linking Mod folder into the game'
$mods = Join-Path $GameDir 'Mods'
if (-not (Test-Path $mods)) { New-Item -ItemType Directory -Path $mods | Out-Null }
$link = Join-Path $mods 'RWMLWC'
if (Test-Path $link) {
    Write-Host "Already exists: $link"
} else {
    # A junction needs no administrator rights.
    New-Item -ItemType Junction -Path $link -Target (Join-Path $repo 'Mod') | Out-Null
    Write-Host "Created: $link"
}

Write-Host "`nDONE." -ForegroundColor Green
Write-Host 'Next: start RimWorld, enable Harmony and "RWMLWC Multiplayer" (Harmony above), restart,'
Write-Host 'then look for "[RWMLWC]" in the game log.'
Write-Host "Build log: $log"
