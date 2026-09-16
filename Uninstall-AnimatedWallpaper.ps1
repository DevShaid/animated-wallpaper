[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'AnimatedWallpaper'
$installedExe = Join-Path $installDir 'AnimatedWallpaper.exe'
$desktopKey = 'HKCU:\Control Panel\Desktop'

if (Test-Path -LiteralPath $installedExe) {
    & $installedExe /close
    Start-Sleep -Milliseconds 750
}

# Legacy helper from earlier builds; replaced by TranslucentTB and no longer installed.
Get-Process -Name 'TaskbarStyler' -ErrorAction SilentlyContinue | Stop-Process -Force

$taskbarWasRunning = [bool](Get-Process -Name 'TranslucentTB' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith((Join-Path $installDir 'taskbar'), [StringComparison]::OrdinalIgnoreCase) })
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

# The installer writes TranslucentTB's configuration to %LOCALAPPDATA%\TranslucentTB, which lives
# outside the install directory removed above. Leave a user-installed TranslucentTB alone.
$taskbarConfigDir = Join-Path $env:LOCALAPPDATA 'TranslucentTB'
if ((Test-Path -LiteralPath $taskbarConfigDir) -and -not (Get-Process -Name 'TranslucentTB' -ErrorAction SilentlyContinue)) {
    Remove-Item -LiteralPath $taskbarConfigDir -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath (Join-Path $env:TEMP 'TranslucentTB') -Recurse -Force -ErrorAction SilentlyContinue

# ExplorerTAP stays loaded in explorer.exe until it restarts, so the taskbar keeps the transparent
# styling even after TranslucentTB is gone. Restarting Explorer restores the default appearance.
if ($taskbarWasRunning) {
    Write-Host 'Restarting Explorer to restore the default taskbar appearance...'
    Stop-Process -Name 'explorer' -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    if (-not (Get-Process -Name 'explorer' -ErrorAction SilentlyContinue)) { Start-Process 'explorer.exe' }
}

Write-Host 'Animated Wallpaper and transparent-taskbar solution was removed. The generated lock-screen still may remain selected in Windows; change it under Settings > Personalization > Lock screen.'
