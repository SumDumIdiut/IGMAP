# ===========================================================================
# IGTAP Map Editor Mod - installer
#
# Installs everything the mod needs into the game:
#   1. MapEditorPlugin.dll            -> <game>\IGTAPsnfDemo_Data\Managed\
#                                       (builds from source if dotnet is available,
#                                       otherwise uses the prebuilt DLL)
#   2. Loader hooks                   -> patches ScriptingAssemblies.json +
#                                       RuntimeInitializeOnLoads.json (idempotent,
#                                       .bak backups created once)
#   3. Web editor (server + UI)       -> <game>\MapEditor\webeditor\
#   4. Electron editor app            -> <game>\MapEditor\electron\   (-SkipElectron to omit)
#                                       (assembled from the official Electron runtime,
#                                       downloaded from github.com/electron if needed)
#   5. Sprite library                 -> %AppData%\IGTAPEditor\sprites (-SkipSprites to omit)
#
# If the installer is run on its own (e.g. straight from a downloaded ZIP, where
# Windows extracts only the exe to a temp folder), it downloads the payload
# from GitHub automatically.
#
# Usage:  powershell -ExecutionPolicy Bypass -File install-mod.ps1
#         [-GameDir "D:\Steam\...\IGTAP ... Demo"] [-NoBuild] [-SkipElectron] [-SkipSprites]
# ===========================================================================
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\IGTAP an Incremental Game That's Also a Platformer Demo",
    [switch]$NoBuild,
    [switch]$SkipElectron,
    [switch]$SkipSprites
)

$ErrorActionPreference = 'Stop'

$RepoZipUrl     = 'https://github.com/SumDumIdiut/IGMAP/archive/refs/heads/main.zip'
$ElectronZipUrl = 'https://github.com/electron/electron/releases/download/v32.3.3/electron-v32.3.3-win32-x64.zip'

function Download-File([string]$url, [string]$dst) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $prev = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri $url -OutFile $dst -UseBasicParsing } finally { $ProgressPreference = $prev }
}

# Folder the installer lives in (works both as .ps1 and as a ps2exe-compiled .exe,
# where $MyInvocation has no path and the process path is the exe itself)
$src = if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path }
       else { Split-Path -Parent ([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName) }
# Payload folders (MapEditorPlugin, igtap-editor, ...) may live one level up
# (e.g. when the installer sits inside the IGTAP-Decomp repo folder)
if (-not (Test-Path (Join-Path $src 'MapEditorPlugin'))) {
    $parent = Split-Path -Parent $src
    if ($parent -and (Test-Path (Join-Path $parent 'MapEditorPlugin'))) { $src = $parent }
}
# Still no payload next to the installer? Happens when the exe is run on its own,
# e.g. launched straight from inside a downloaded ZIP (Windows extracts only the
# exe to a temp folder). Fetch the payload from GitHub instead of failing.
if (-not (Test-Path (Join-Path $src 'MapEditorPlugin'))) {
    Write-Host '==> Mod files not found next to the installer - downloading them from GitHub' -ForegroundColor Cyan
    $stage = Join-Path $env:TEMP 'IGTAPMapEditorMod'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null
    $zip = Join-Path $stage 'repo.zip'
    Download-File $RepoZipUrl $zip
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $stage)
    Remove-Item $zip -Force
    $inner = Get-ChildItem $stage -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'MapEditorPlugin') } | Select-Object -First 1
    if (-not $inner) { throw 'Downloaded payload is missing MapEditorPlugin - report this on GitHub.' }
    $src = $inner.FullName
    Write-Host "    payload -> $src" -ForegroundColor Green
}

# Pause before closing when double-clicked (no arguments)
$script:pauseAtEnd = ($PSBoundParameters.Count -eq 0)
function Finish($code) {
    if ($script:pauseAtEnd) { [void](Read-Host 'Press Enter to close') }
    exit $code
}
trap {
    Write-Host ''
    Write-Host "INSTALL FAILED: $_" -ForegroundColor Red
    Finish 1
}

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }

# == 0. sanity ================================================================
if (-not (Test-Path (Join-Path $GameDir 'IGTAPsnfDemo.exe'))) {
    throw "Game not found at '$GameDir' - pass -GameDir with the correct install path."
}
$dataDir    = Join-Path $GameDir 'IGTAPsnfDemo_Data'
$managedDir = Join-Path $dataDir 'Managed'

# == 1. plugin DLL ===========================================================
$pluginProj = Join-Path $src 'MapEditorPlugin'
$pluginDll  = Join-Path $pluginProj 'bin\Release\net48\MapEditorPlugin.dll'
$prebuiltDll = Join-Path $pluginProj 'prebuilt\MapEditorPlugin.dll'

$hasDotnet = [bool](Get-Command dotnet -ErrorAction SilentlyContinue)
if (-not $NoBuild -and $hasDotnet -and (Test-Path (Join-Path $pluginProj 'MapEditorPlugin.csproj'))) {
    Step 'Building MapEditorPlugin'
    Push-Location $pluginProj
    try { dotnet build -c Release | Select-Object -Last 2 | Out-Host } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
}
if (-not (Test-Path $pluginDll) -and (Test-Path $prebuiltDll)) {
    $pluginDll = $prebuiltDll   # no dotnet SDK (or build skipped) - ship the prebuilt copy
}
if (-not (Test-Path $pluginDll)) { throw "Plugin DLL not found ($pluginDll). Install the .NET SDK to build it, or re-download the mod - a prebuilt copy ships in MapEditorPlugin\prebuilt." }

Step 'Installing plugin DLL'
Copy-Item $pluginDll (Join-Path $managedDir 'MapEditorPlugin.dll') -Force
Ok "MapEditorPlugin.dll -> Managed\"

# == 2. loader hooks ==========================================================
function Write-Utf8NoBom([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

Step 'Patching ScriptingAssemblies.json'
$saPath = Join-Path $dataDir 'ScriptingAssemblies.json'
$sa = Get-Content $saPath -Raw | ConvertFrom-Json
if ($sa.names -notcontains 'MapEditorPlugin.dll') {
    if (-not (Test-Path "$saPath.bak")) { Copy-Item $saPath "$saPath.bak" }
    $sa.names += 'MapEditorPlugin.dll'
    $sa.types += 16
    Write-Utf8NoBom $saPath ($sa | ConvertTo-Json -Compress -Depth 5)
    Ok 'entry added'
} else { Ok 'already present' }

Step 'Patching RuntimeInitializeOnLoads.json'
$rilPath = Join-Path $dataDir 'RuntimeInitializeOnLoads.json'
$ril = Get-Content $rilPath -Raw | ConvertFrom-Json
$has = $false
foreach ($e in $ril.root) { if ($e.assemblyName -eq 'MapEditorPlugin') { $has = $true; break } }
if (-not $has) {
    if (-not (Test-Path "$rilPath.bak")) { Copy-Item $rilPath "$rilPath.bak" }
    $entry = [pscustomobject]@{
        assemblyName = 'MapEditorPlugin'
        nameSpace    = 'IGTAPMapEditor'
        className    = 'Bootstrap'
        methodName   = 'Init'
        loadTypes    = 4
        isUnityClass = $false
    }
    $ril.root += $entry
    Write-Utf8NoBom $rilPath ($ril | ConvertTo-Json -Compress -Depth 5)
    Ok 'entry added'
} else { Ok 'already present' }

# == 3. web editor ============================================================
$editorSrc = Join-Path $src 'igtap-editor'
$editorDst = Join-Path $GameDir 'MapEditor\webeditor'
if (Test-Path $editorSrc) {
    Step 'Installing web editor'
    New-Item -ItemType Directory -Force $editorDst | Out-Null
    Copy-Item (Join-Path $editorSrc 'server.js')    $editorDst -Force
    if (Test-Path (Join-Path $editorSrc 'gen_packs.py')) { Copy-Item (Join-Path $editorSrc 'gen_packs.py') $editorDst -Force }
    Copy-Item (Join-Path $editorSrc 'public')       $editorDst -Recurse -Force
    if (Test-Path (Join-Path $editorSrc 'node_modules')) {
        Copy-Item (Join-Path $editorSrc 'node_modules') $editorDst -Recurse -Force
    }
    if (Test-Path (Join-Path $editorSrc 'package.json')) { Copy-Item (Join-Path $editorSrc 'package.json') $editorDst -Force }
    Ok "web editor -> MapEditor\webeditor\ (needs Node.js on PATH for the standalone server)"
} else {
    Write-Warning "Web editor source not found at $editorSrc - skipped."
}

# == 4. Electron app ==========================================================
if (-not $SkipElectron) {
    $electronSrc = Join-Path $src 'IGTAP Map Editor'
    $electronDst = Join-Path $GameDir 'MapEditor\electron'
    $electronExe = Join-Path $electronDst 'IGTAP Map Editor.exe'

    if (Test-Path (Join-Path $electronSrc 'IGTAP Map Editor.exe')) {
        # full prebuilt app shipped next to the installer (dev machines)
        Step 'Installing Electron editor (the in-game embedded editor - this is the big one)'
        New-Item -ItemType Directory -Force $electronDst | Out-Null
        robocopy $electronSrc $electronDst /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed copying Electron app (code $LASTEXITCODE)" }
        $global:LASTEXITCODE = 0
        Ok "Electron app -> MapEditor\electron\"
    } elseif (-not (Test-Path $electronExe)) {
        # assemble it: official Electron runtime + the editor app from igtap-editor
        Step 'Downloading Electron runtime (~110 MB, one time) and assembling the in-game editor'
        $ezip = Join-Path $env:TEMP 'igtap-electron-runtime.zip'
        Download-File $ElectronZipUrl $ezip
        New-Item -ItemType Directory -Force $electronDst | Out-Null
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($ezip, $electronDst)
        Remove-Item $ezip -Force
        Rename-Item (Join-Path $electronDst 'electron.exe') 'IGTAP Map Editor.exe' -Force
        Remove-Item (Join-Path $electronDst 'resources\default_app.asar') -Force -ErrorAction SilentlyContinue
        Ok "Electron runtime -> MapEditor\electron\"
    } else {
        Step 'Electron editor'
        Ok 'runtime already installed'
    }

    # (re)copy the editor app itself into the runtime so it stays in sync
    if ((Test-Path $electronExe) -and (Test-Path $editorSrc)) {
        $appDst = Join-Path $electronDst 'resources\app'
        New-Item -ItemType Directory -Force $appDst | Out-Null
        foreach ($f in 'server.js', 'electron-main.js', 'package.json') {
            if (Test-Path (Join-Path $editorSrc $f)) { Copy-Item (Join-Path $editorSrc $f) $appDst -Force }
        }
        foreach ($d in 'public', 'node_modules') {
            if (Test-Path (Join-Path $editorSrc $d)) { Copy-Item (Join-Path $editorSrc $d) $appDst -Recurse -Force }
        }
        Ok "editor app -> MapEditor\electron\resources\app\"
    } elseif (-not (Test-Path $electronExe)) {
        Write-Warning 'Electron editor could not be installed - the in-game editor will fall back to the classic IMGUI editor.'
    }
}

# == 5. sprite library ========================================================
if (-not $SkipSprites) {
    $spriteDst = Join-Path $env:APPDATA 'IGTAPEditor\sprites'
    $existing = 0
    if (Test-Path $spriteDst) { $existing = (Get-ChildItem $spriteDst -Filter *.png -ErrorAction SilentlyContinue).Count }
    if ($existing -lt 100) {
        # payload folder shipped next to the script (full AssetRipper dump)
        $spriteSrc = Join-Path $src 'sprites'
        if (Test-Path $spriteSrc) {
            Step 'Installing sprite library'
            New-Item -ItemType Directory -Force $spriteDst | Out-Null
            robocopy $spriteSrc $spriteDst /E /NFL /NDL /NJH /NJS /NP | Out-Null
            $global:LASTEXITCODE = 0
            Ok "sprites -> $spriteDst"
        } else {
            Write-Warning "Sprite library payload not found ($spriteSrc) and AppData has only $existing sprites - the editor gallery/packs will be sparse until the game exports or the dump is copied."
        }
    } else {
        Step 'Sprite library'
        Ok "$existing sprites already present in $spriteDst"
    }
}

Write-Host ''
Write-Host '====================================================' -ForegroundColor Yellow
Write-Host ' Install complete. Launch the game - F10 opens the editor.' -ForegroundColor Yellow
Write-Host ' Main menu gains: Play <map>, per-map save delete, Load Map / Map Editor buttons.' -ForegroundColor Yellow
Write-Host '====================================================' -ForegroundColor Yellow
Finish 0
