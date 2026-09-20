# Installs Surface Keyboard Backlight Keeper for the current user (no admin rights needed):
#   - copies the exe to %LOCALAPPDATA%\SurfaceKeyboardBacklightKeeper
#   - registers it to start at sign-in
#   - starts it
# Run from the folder that contains SurfaceKeyboardBacklightKeeper.exe (the release zip, or the repo after build.ps1):
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exeName = 'SurfaceKeyboardBacklightKeeper.exe'
$src = Join-Path $here $exeName
if (-not (Test-Path $src)) { $src = Join-Path $here "build\$exeName" }
if (-not (Test-Path $src)) { throw "$exeName not found next to this script or in .\build. Run build.ps1 first or use the release zip." }

$dest = Join-Path $env:LOCALAPPDATA 'SurfaceKeyboardBacklightKeeper'
New-Item -ItemType Directory -Force $dest | Out-Null
Get-Process SurfaceKeyboardBacklightKeeper -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Copy-Item $src (Join-Path $dest $exeName) -Force
$installed = Join-Path $dest $exeName

# Same Run-key value name the app itself uses for its "Start with Windows" menu item.
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'SurfaceBacklightKeeper' -Value ('"' + $installed + '"')

# Start menu shortcut, so it can be launched by typing its name in Start.
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Surface Keyboard Backlight Keeper.lnk'
$sc = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$sc.TargetPath = $installed; $sc.WorkingDirectory = $dest; $sc.Description = 'Keeps the Surface keyboard backlight on'; $sc.Save()

Start-Process -FilePath $installed
Write-Host "Installed to $installed, set to start at sign-in, and added to the Start menu. Look for the keyboard icon in the tray."
