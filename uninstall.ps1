# Removes Surface Keyboard Backlight Keeper for the current user: stops it, removes the start-at-sign-in entry,
# deletes the installed copy, its settings and its log folder.
#   powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
$ErrorActionPreference = 'SilentlyContinue'
Get-Process SurfaceKeyboardBacklightKeeper | Stop-Process -Force
Get-Process SurfaceBacklightKeeper | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'SurfaceBacklightKeeper'
Remove-Item -Force (Join-Path ([Environment]::GetFolderPath('Programs')) 'Surface Keyboard Backlight Keeper.lnk')
Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA 'SurfaceKeyboardBacklightKeeper')
Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA 'SurfaceBacklightKeeper')   # settings/log folder
Remove-Item -Recurse -Force 'HKCU:\Software\SurfaceBacklightKeeper'                     # settings
Write-Host "Surface Keyboard Backlight Keeper removed. The keyboard backlight now behaves as Windows ships it."
