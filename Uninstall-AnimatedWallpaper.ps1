[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'AnimatedWallpaper'
$installedExe = Join-Path $installDir 'AnimatedWallpaper.exe'
$taskbarExe = Join-Path $installDir 'TaskbarStyler.exe'
$desktopKey = 'HKCU:\Control Panel\Desktop'

if (Test-Path -LiteralPath $installedExe) {
    & $installedExe /close
    Start-Sleep -Milliseconds 750
}

if (Test-Path -LiteralPath $taskbarExe) {
    & $taskbarExe /close
    Start-Sleep -Milliseconds 750
}

Get-Process -Name 'TranslucentTB' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith((Join-Path $installDir 'taskbar'), [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'AnimatedWallpaper' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'AnimatedWallpaperTaskbar' -ErrorAction SilentlyContinue

$configuredSaver = (Get-ItemProperty -Path $desktopKey -Name 'SCRNSAVE.EXE' -ErrorAction SilentlyContinue).'SCRNSAVE.EXE'
if ($configuredSaver -eq (Join-Path $installDir 'AnimatedWallpaper.scr')) {
    Set-ItemProperty -Path $desktopKey -Name 'ScreenSaveActive' -Value '0'
    Remove-ItemProperty -Path $desktopKey -Name 'SCRNSAVE.EXE' -ErrorAction SilentlyContinue
}

Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Animated Lock.lnk') `
    -Force -ErrorAction SilentlyContinue

$resolved = [IO.Path]::GetFullPath($installDir)
$expected = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AnimatedWallpaper'))
if ($resolved -ne $expected -or -not $resolved.StartsWith([IO.Path]::GetFullPath($env:LOCALAPPDATA), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove unexpected path: $resolved"
}
Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Animated Wallpaper and transparent-taskbar solution was removed. The generated lock-screen still may remain selected in Windows.'
