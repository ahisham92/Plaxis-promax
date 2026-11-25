# Installs Plaxis ProMax for the current Windows user (no admin needed):
#   %LOCALAPPDATA%\PlaxisProMax\app             the window and the Plaxis worker
#   Documents\Plaxis ProMax\Plaxis ProMax.py     what Plaxis runs (Expert > Python > Run script)
#   Start menu > Plaxis ProMax                   to open it outside Plaxis
# It also finds Plaxis's own Python and checks for Claude Code.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$dist = if (Test-Path "$here\dist\app") { "$here\dist" } else { throw "No build here: run build.ps1 first, or use a release zip." }
$appDir = Join-Path $env:LOCALAPPDATA 'PlaxisProMax\app'
$docs = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Plaxis ProMax'

# A running window keeps its files open: close it first.
Get-Process PlaxisProMax -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Closing the open Plaxis ProMax window..."
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(5000)) { $_.Kill() }
}
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
New-Item -ItemType Directory -Force $appDir, $docs | Out-Null
Copy-Item "$dist\app\*" $appDir -Recurse -Force
Copy-Item "$dist\Plaxis ProMax.py" $docs -Force
Write-Host "Installed to $appDir"
Write-Host "Plaxis launcher: $docs\Plaxis ProMax.py"

# Plaxis's own Python: Bentley's PLAXIS Python Distribution, the newest first.
$python = @(
    "$env:ProgramData\Bentley\Geotechnical", "$env:ProgramFiles\Bentley\Geotechnical"
) | Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem $_ -Directory -Filter '*Python*' } |
    Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'python\python.exe' } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
$settingsDir = Join-Path $env:APPDATA 'PlaxisProMax'
$settingsFile = Join-Path $settingsDir 'settings.json'
New-Item -ItemType Directory -Force $settingsDir | Out-Null
$settings = if (Test-Path $settingsFile) { Get-Content $settingsFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($python) {
    $settings | Add-Member -NotePropertyName PlaxisPython -NotePropertyValue $python -Force
    $settings | ConvertTo-Json -Depth 5 | Set-Content $settingsFile -Encoding utf8
    Write-Host "Plaxis's Python: $python"
} else {
    Write-Host "Plaxis's Python wasn't found. Start Plaxis ProMax from Plaxis the first time (Expert > Python > Run script); it is remembered then."
}

# Start menu shortcut.
$programs = [Environment]::GetFolderPath('Programs')
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $programs 'Plaxis ProMax.lnk'))
$link.TargetPath = Join-Path $appDir 'PlaxisProMax.exe'
$link.WorkingDirectory = $appDir
$link.Description = 'AI assistant for PLAXIS 2D and 3D'
$link.Save()

# The Modeller and the Viewer: on the desktop and in the Start menu's Plaxis ProMax folder. Neither uses Plaxis or its
# licence; only Calculate in Plaxis does, for as long as the run takes. (The program makes them again after an update.)
$exe = Join-Path $appDir 'PlaxisProMax.exe'
foreach ($folder in @([Environment]::GetFolderPath('DesktopDirectory'), (Join-Path $programs 'Plaxis ProMax'))) {
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    foreach ($s in @(@('Plaxis ProMax Modeller', '--modeller', 'Build Plaxis models with no Plaxis open and no licence'),
                     @('Plaxis ProMax Viewer', '--viewer', 'Open Plaxis results (.ppr) with no Plaxis and no licence'))) {
        $l = $shell.CreateShortcut((Join-Path $folder ($s[0] + '.lnk')))
        $l.TargetPath = $exe
        $l.Arguments = $s[1]
        $l.WorkingDirectory = $appDir
        $l.Description = $s[2]
        $l.IconLocation = "$exe,0"
        $l.Save()
    }
}
Set-Content (Join-Path $env:LOCALAPPDATA 'PlaxisProMax\shortcuts.txt') ($exe + '|2') -NoNewline

# Claude Code: the chat's engine, on your Claude plan.
$claude = @("$env:USERPROFILE\.local\bin\claude.exe", "$env:LOCALAPPDATA\Programs\claude\claude.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $claude) { $claude = (Get-Command claude.exe, claude.cmd -ErrorAction SilentlyContinue | Select-Object -First 1).Source }
if ($claude) { Write-Host "Claude Code: $claude" }
else { Write-Host "Claude Code isn't installed yet: the Plaxis ProMax window offers to install it the first time it opens." }

Write-Host ""
Write-Host "Done. In Plaxis Input: Expert > Configure remote scripting server (start it once), then Expert > Python > Run script > '$docs\Plaxis ProMax.py'."
