[CmdletBinding()]
param(
    [Parameter()]
    [string]$Wallpaper,

    [Parameter()]
    [ValidateRange(60, 86400)]
    [int]$ScreenSaverTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$solutionDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Wallpaper) { $Wallpaper = Join-Path $solutionDir 'wallpaper.mp4' }
$sourceExe = Join-Path $solutionDir 'AnimatedWallpaper.exe'
$sourceCode = Join-Path $solutionDir 'AnimatedWallpaper.cs'
$sourceLauncher = Join-Path $solutionDir 'Start-AnimatedLock.ps1'
$sourceRuntime = Join-Path $solutionDir 'runtime'
$sourceTaskbarDir = Join-Path $solutionDir 'taskbar'
$installDir = Join-Path $env:LOCALAPPDATA 'AnimatedWallpaper'
$installedExe = Join-Path $installDir 'AnimatedWallpaper.exe'
$screenSaver = Join-Path $installDir 'AnimatedWallpaper.scr'
$installedVideo = Join-Path $installDir 'wallpaper.mp4'
$lockStill = Join-Path $installDir 'lock-screen.png'
$taskbarDir = Join-Path $installDir 'taskbar'
$taskbarExe = Join-Path $taskbarDir 'TranslucentTB.exe'
$log = Join-Path $installDir 'install.log'

New-Item -ItemType Directory -Force -Path $installDir | Out-Null

function Write-InstallLog {
    param([string]$Message)
    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $Message
    Add-Content -LiteralPath $log -Value $line
    Write-Host $Message
}

if (-not (Test-Path -LiteralPath $Wallpaper -PathType Leaf)) {
    throw "Wallpaper file not found: $Wallpaper"
}
if ([IO.Path]::GetExtension($Wallpaper).ToLowerInvariant() -notin '.mp4', '.wmv', '.avi', '.mpeg', '.mpg') {
    throw 'This Windows-native build expects an MP4/WMV/AVI/MPEG video. Convert other formats to H.264 MP4 first.'
}

if (-not (Test-Path -LiteralPath (Join-Path $sourceTaskbarDir 'TranslucentTB.exe') -PathType Leaf)) {
    throw 'The bundled TranslucentTB taskbar tool is missing from the solution taskbar folder.'
}

if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (-not (Test-Path -LiteralPath $csc)) {
        $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    }
    if (-not (Test-Path -LiteralPath $csc)) {
        throw 'The built executable is missing and the Windows .NET Framework C# compiler was not found.'
    }
    $frameworkDir = Split-Path -Parent $csc
    $wpfDir = Join-Path $frameworkDir 'WPF'
    Write-InstallLog 'Compiling the Windows-native animated wallpaper player...'
    & $csc /nologo /target:winexe /optimize+ /platform:anycpu /out:"$sourceExe" `
        /reference:"$(Join-Path $wpfDir 'PresentationCore.dll')" `
        /reference:"$(Join-Path $wpfDir 'PresentationFramework.dll')" `
        /reference:"$(Join-Path $wpfDir 'WindowsBase.dll')" `
        /reference:"$(Join-Path $frameworkDir 'System.Xaml.dll')" `
        /reference:"$(Join-Path $frameworkDir 'System.dll')" `
        /reference:"$(Join-Path $frameworkDir 'System.Core.dll')" "$sourceCode"
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed with exit code $LASTEXITCODE."
    }
}

Write-InstallLog 'Stopping any earlier copy of the custom wallpaper player...'
& $sourceExe /close
Get-Process -Name 'TaskbarStyler' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name 'TranslucentTB' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($taskbarDir, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force
Start-Sleep -Milliseconds 700

Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force
Copy-Item -LiteralPath $sourceTaskbarDir -Destination $installDir -Recurse -Force
Copy-Item -LiteralPath $sourceExe -Destination $screenSaver -Force
Copy-Item -LiteralPath $Wallpaper -Destination $installedVideo -Force
if (-not (Test-Path -LiteralPath (Join-Path $sourceRuntime 'mpv.exe') -PathType Leaf)) {
    throw 'The bundled MPV renderer is missing from the solution runtime folder.'
}
Copy-Item -LiteralPath $sourceRuntime -Destination $installDir -Recurse -Force

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runKey -Force | Out-Null
New-ItemProperty -Path $runKey -Name 'AnimatedWallpaper' -PropertyType String `
    -Value ('"{0}" /desktop "{1}"' -f $installedExe, $installedVideo) -Force | Out-Null
New-ItemProperty -Path $runKey -Name 'AnimatedWallpaperTaskbar' -PropertyType String `
    -Value ('"{0}"' -f $taskbarExe) -Force | Out-Null

$desktopKey = 'HKCU:\Control Panel\Desktop'
Set-ItemProperty -Path $desktopKey -Name 'SCRNSAVE.EXE' -Value $screenSaver
Set-ItemProperty -Path $desktopKey -Name 'ScreenSaveActive' -Value '1'
Set-ItemProperty -Path $desktopKey -Name 'ScreenSaverIsSecure' -Value '1'
Set-ItemProperty -Path $desktopKey -Name 'ScreenSaveTimeOut' -Value ([string]$ScreenSaverTimeoutSeconds)

if (-not ('Wallpaper.NativeSettings' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Wallpaper {
    public static class NativeSettings {
        [DllImport("user32.dll", SetLastError=true)]
        public static extern bool SystemParametersInfo(uint action, uint parameter, IntPtr data, uint flags);
    }
}
'@
}
[Wallpaper.NativeSettings]::SystemParametersInfo(15, [uint32]$ScreenSaverTimeoutSeconds, [IntPtr]::Zero, 3) | Out-Null
[Wallpaper.NativeSettings]::SystemParametersInfo(17, 1, [IntPtr]::Zero, 3) | Out-Null

$sourceLockStill = Join-Path $solutionDir 'lock-screen.png'
if (Test-Path -LiteralPath $sourceLockStill -PathType Leaf) {
    Write-InstallLog 'Using the pre-rendered 1920x1080 lock/sign-in screen still...'
    Copy-Item -LiteralPath $sourceLockStill -Destination $lockStill -Force
    $captureFailed = $false
} else {
    Write-InstallLog 'Capturing a high-quality 1920x1080 still for the real Windows lock/sign-in screen...'
    $captureProcess = Start-Process -FilePath $installedExe `
        -ArgumentList @('/capture', $installedVideo, $lockStill, '1920', '1080') `
        -WindowStyle Hidden -Wait -PassThru
    $captureFailed = ($captureProcess.ExitCode -ne 0)
}
if ($captureFailed -or -not (Test-Path -LiteralPath $lockStill)) {
    Write-Warning 'The lock-screen still could not be captured. Desktop and animated screen saver setup will continue.'
} else {
    try {
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
        [Windows.System.UserProfile.LockScreen, Windows.System.UserProfile, ContentType = WindowsRuntime] | Out-Null

        function Wait-WinRtOperation {
            param(
                [Parameter(Mandatory)]$Operation,
                [Parameter()] [Type]$ResultType
            )
            if ($ResultType) {
                $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
                    Where-Object {
                        $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and
                        $_.GetParameters().Count -eq 1
                    } | Select-Object -First 1
                $task = $method.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
                $task.Wait()
                return $task.Result
            }
            $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
                Where-Object {
                    $_.Name -eq 'AsTask' -and -not $_.IsGenericMethod -and
                    $_.GetParameters().Count -eq 1 -and
                    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'
                } | Select-Object -First 1
            $task = $method.Invoke($null, @($Operation))
            $task.Wait()
        }

        $storageFile = Wait-WinRtOperation `
            ([Windows.Storage.StorageFile]::GetFileFromPathAsync($lockStill)) `
            ([Windows.Storage.StorageFile])
        Wait-WinRtOperation ([Windows.System.UserProfile.LockScreen]::SetImageFileAsync($storageFile))

        Write-InstallLog 'Applied the generated frame to the real Windows lock screen.'
    } catch {
        Write-Warning "Windows rejected programmatic lock-screen assignment: $($_.Exception.Message)"
        Write-InstallLog 'The still was generated; it can be selected manually under Settings > Personalization > Lock screen.'
    }
}

$launcher = Join-Path $installDir 'Start-AnimatedLock.ps1'
Copy-Item -LiteralPath $sourceLauncher -Destination $launcher -Force

$desktop = [Environment]::GetFolderPath('Desktop')
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $desktop 'Animated Lock.lnk'))
$shortcut.TargetPath = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
$shortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $launcher
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation = "$env:WINDIR\System32\shell32.dll,47"
$shortcut.Description = 'Start the animated screen saver; credentials are required on resume.'
$shortcut.Save()

Write-InstallLog 'Starting animated desktop wallpaper...'
Start-Process -FilePath $installedExe -ArgumentList @('/desktop', $installedVideo) -WindowStyle Hidden
Write-InstallLog 'Applying fully transparent taskbar and system tray...'
Start-Process -FilePath $taskbarExe -WorkingDirectory $taskbarDir -WindowStyle Hidden
Start-Sleep -Seconds 6

$process = Get-Process -Name 'AnimatedWallpaper' -ErrorAction SilentlyContinue
if (-not $process) {
    throw "The wallpaper process did not stay running. Review $installDir\AnimatedWallpaper.log"
}
$taskbarProcess = Get-Process -Name 'TranslucentTB' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($taskbarDir, [StringComparison]::OrdinalIgnoreCase) }
if (-not $taskbarProcess) {
    throw 'The transparent-taskbar process did not stay running.'
}

Write-InstallLog 'Installation complete.'
Write-Host ''
Write-Host "Desktop animation: ACTIVE (process id $($process.Id -join ', '))"
Write-Host "Auto-start: ENABLED"
Write-Host "Taskbar and system tray: FULLY TRANSPARENT"
Write-Host "Animated screen saver: ENABLED after $ScreenSaverTimeoutSeconds seconds"
Write-Host "Lock on resume: ENABLED"
Write-Host "Immediate animated lock shortcut: $(Join-Path $desktop 'Animated Lock.lnk')"
Write-Host "True Windows lock-screen still: $lockStill"
