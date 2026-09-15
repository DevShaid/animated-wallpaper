$signature = @'
using System;
using System.Runtime.InteropServices;
public static class ScreenSaverLauncher {
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@
Add-Type $signature
[ScreenSaverLauncher]::SendMessage([IntPtr]0xffff, 0x0112, [IntPtr]0xF140, [IntPtr]0) | Out-Null
