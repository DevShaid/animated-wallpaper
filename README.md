# Animated Wallpaper + Transparent Taskbar (Windows 11)

Looping video wallpaper behind the desktop icons, plus a genuinely transparent Windows 11 taskbar.
No admin rights, no subscription, no browser running in the background.

Built from a small C# controller (`AnimatedWallpaper.cs`) that hosts the official portable
[mpv](https://github.com/mpv-player/mpv) renderer, with taskbar transparency handled by
[TranslucentTB](https://github.com/TranslucentTB/TranslucentTB).

---

## Setup on a new PC

```powershell
git clone https://github.com/DevShaid/animated-wallpaper.git
cd animated-wallpaper
Set-ExecutionPolicy -Scope Process Bypass -Force
.\Install-AnimatedWallpaper.ps1
```

That's it. The installer copies everything to `%LOCALAPPDATA%\AnimatedWallpaper`, registers
both auto-start entries, and starts playback immediately.

To use a different video:

```powershell
.\Install-AnimatedWallpaper.ps1 -Wallpaper "C:\path\to\video.mp4" -ScreenSaverTimeoutSeconds 300
```

**Requirements:** Windows 11 x64, and a restart pending from Windows Update must be completed
first (see Troubleshooting). Nothing else.

---

## What it sets up

- The MP4 loops behind the desktop icons, muted, and pauses while the session is locked.
- Auto-starts at sign-in (two `HKCU\...\Run` entries: `AnimatedWallpaper`, `AnimatedWallpaperTaskbar`).
- The whole taskbar, including the tray background, is transparent — icons and clock stay visible.
- The same MP4 is registered as the screen saver, with `ScreenSaverIsSecure=1` so it asks for
  credentials on resume. An `Animated Lock` desktop shortcut triggers it on demand.
- A still frame is applied to the real Windows lock screen.

### Windows lock screen limitation

The real lock/sign-in screen is a protected surface — Windows only accepts Picture, Slideshow, or
Spotlight there, never video. The animated screen saver is the supported approximation. `Win+L`
still shows the static lock screen; use the `Animated Lock` shortcut for the animated version.

Do not install anything claiming to inject video into `LockApp.exe`, `LogonUI.exe`, or a
credential provider — those modify the boundary that collects your password.

---

## Where the config actually lives

| What | Path |
|---|---|
| Installed files | `%LOCALAPPDATA%\AnimatedWallpaper` |
| Playback log | `%LOCALAPPDATA%\AnimatedWallpaper\AnimatedWallpaper.log` |
| **TranslucentTB config** | **`%LOCALAPPDATA%\TranslucentTB\settings.json`** |

> The TranslucentTB config path is easy to get wrong. The `settings.json` sitting next to
> `taskbar\TranslucentTB.exe` is **not read at runtime** — it's only a reference copy. Editing it
> does nothing. TranslucentTB reads `%LOCALAPPDATA%\TranslucentTB\settings.json`, and if that file
> is missing it silently falls back to its built-in default (`blur`), which looks like a flat
> tinted bar rather than a transparent one.

The shipped config uses accent `clear` with `#00000000`, and disables the visible-window,
maximized-window, Start, Search, Task View and Battery Saver overrides so the bar stays
transparent in every state. For a tinted bar instead, change `color` to something like
`#071D3A80`; TranslucentTB reloads on save.

---

## Encoding a new wallpaper

The right ffmpeg settings depend entirely on the source material. Getting this wrong is the
single biggest quality mistake.

**Normal video / photographic footage** — scale with lanczos, keep debanding on:

```bash
ffmpeg -i input.mp4 -vf "scale=1920:1080:flags=lanczos,setsar=1,format=yuv420p" \
  -c:v libx264 -crf 16 -preset slow -movflags +faststart -an output.mp4
```

**Pixel art** — never use lanczos; it smears the hard pixel edges into mush. Use integer
nearest-neighbour scaling so every source pixel becomes an exact block:

```bash
# 800x336 source -> exact 3x -> crop to 1920 wide -> pad to 1080 with edge smear
ffmpeg -i input.gif -vf "scale=2400:1008:flags=neighbor,crop=1920:1008:240:0,\
pad=1920:1080:0:36,fillborders=top=36:bottom=36:mode=smear,setsar=1,format=yuv444p" \
  -c:v libx264 -qp 0 -preset veryslow -fps_mode vfr -movflags +faststart -an output.mp4
```

Pixel art also wants mpv's `--scale=nearest` and `--deband=no`; both are set in
`AnimatedWallpaper.cs` (search for `--deband`). The defaults in this repo are tuned for
**video** (`ewa_lanczossharp`, `--deband=yes`), because debanding adds dither that ruins flat
pixel-art colors while being essential for smooth sky gradients in real footage.

**Watch for broken color metadata.** Some stock clips ship bogus tags — check with:

```bash
ffprobe -v error -select_streams v:0 -show_entries stream=color_space,color_transfer,color_range -of default=noprint_wrappers=1 input.mp4
```

If you see `color_space=ycgco` or `color_transfer=log316`, the tags are wrong and mpv will render
washed-out, flat colors. Force correct ones by prefixing your filter chain with:

```
setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709,
```

Also check the dimensions — clips that are 1920x1078 or similar need rescaling to exactly
1920x1080 rather than letterboxing.

---

## Troubleshooting

**Taskbar is solid white.** Something is using the legacy `SetWindowCompositionAttribute` accent
API. On Windows 11 that call *reports success* while blanking the legacy backdrop, leaving the
XAML layer to repaint in its default light brush. Windows 11 requires `ExplorerTAP.dll` injection,
which is what TranslucentTB does. Make sure nothing else is trying to style the taskbar.

**"Open File - Security Warning / The publisher could not be verified" on every sign-in.** Files
extracted from a downloaded ZIP carry Mark of the Web, and the player auto-starts from the Run key,
so the prompt reappears at every login. The installer now strips it automatically; to clear it by
hand:

```powershell
Get-ChildItem "$env:LOCALAPPDATA\AnimatedWallpaper" -Recurse -File | Unblock-File
```

Cloning with `git clone` instead of downloading the ZIP avoids the mark entirely, since git writes
the files locally rather than extracting them from a downloaded archive.

**Taskbar is a flat tinted bar, not transparent.** TranslucentTB is running its default `blur`.
Check `%LOCALAPPDATA%\TranslucentTB\settings.json` exists and has `"accent": "clear"`, then
restart TranslucentTB.

**"Failed to initialize XAML Diagnostics. 0x800401E3: Operation unavailable"** — TranslucentTB dies
at startup with a fatal error dialog. Same underlying cause as the restart prompt below: a Windows
update has been applied but not completed, so `ExplorerTAP` cannot attach to explorer's XAML tree.
Install pending updates and **Restart** (not Shut down — Fast Startup means shutdown does not fully
reinitialize the kernel). This is the fix confirmed by multiple users in
[TranslucentTB#1128](https://github.com/TranslucentTB/TranslucentTB/issues/1128).

Do **not** use the UAC-disabling registry workaround circulating in
[TranslucentTB#1109](https://github.com/TranslucentTB/TranslucentTB/issues/1109) — it permanently
weakens system security to work around a transient state that a restart clears.

**TranslucentTB says a restart is needed.** It's telling the truth. ExplorerTAP cannot inject into
an `explorer.exe` whose taskbar binaries are mid-update. Verify with:

```powershell
Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
```

If either is `True`, restart Windows and finish the update.

**Check injection actually happened:**

```powershell
Get-Process explorer | ForEach-Object { $_.Modules } | Where-Object ModuleName -match 'ExplorerTAP'
```

**Manual control:**

```powershell
& "$env:LOCALAPPDATA\AnimatedWallpaper\AnimatedWallpaper.exe" /close
& "$env:LOCALAPPDATA\AnimatedWallpaper\AnimatedWallpaper.exe" /desktop "$env:LOCALAPPDATA\AnimatedWallpaper\wallpaper.mp4"
.\Uninstall-AnimatedWallpaper.ps1
```

---

## Included wallpapers

| File | Source |
|---|---|
| `wallpaper.mp4` | Vecteezy stock clip (cherry blossom / mountain), 1920x1080, 12s, seamless loop |
| `wallpaper.pixelart.mp4` | Pixel-art forest scene, integer 3x nearest-neighbour, lossless 4:4:4 |
| `wallpaper.gif` | Source GIF for the pixel-art build (800x336, 8 frames) |

**These media files are included for personal use across my own machines and are not licensed for
redistribution.** The Vecteezy clip is covered by Vecteezy's license, and the pixel art is by an
unidentified artist. If you're not me: bring your own video and pass it with `-Wallpaper`.

---

## Third-party components

| Component | Version | License |
|---|---|---|
| [mpv](https://github.com/mpv-player/mpv) | 0.41.0 x64 | LGPL-2.1-or-later |
| [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB) | 2026.2 portable x64 | GPL-3.0 |
| Microsoft VC++ runtime (`msvcp140*.dll`, `vcruntime140*.dll`) | 14.x | MS redistributable |

The VC++ runtime DLLs are deployed app-locally in `taskbar/` because TranslucentTB needs them and
they are not guaranteed to be present on a clean Windows install. All seven are Authenticode-signed
by Microsoft Corporation.

Full license texts, upstream source links and SHA-256 verification hashes are in
`runtime/NOTICE.txt`, `runtime/LICENSE.LGPL.txt`, `taskbar/NOTICE.txt` and
`taskbar/LICENSE.GPL-3.0.md`. Both binaries are redistributed unmodified.
