using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AnimatedWallpaper
{
    internal static class Program
    {
        private const string DesktopMutexName = @"Local\AnimatedWallpaper.Desktop";
        private const string DesktopCloseEventName = @"Local\AnimatedWallpaper.Close";

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                // A direct launch toggles the wallpaper. Startup explicitly uses /desktop,
                // which is idempotent and does not accidentally stop an existing player.
                string first = args.Length == 0 ? "/toggle" : args[0].ToLowerInvariant();
                string defaultVideo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wallpaper.mp4");

                if (first == "/close")
                {
                    try
                    {
                        using (EventWaitHandle closeEvent = EventWaitHandle.OpenExisting(DesktopCloseEventName))
                            closeEvent.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                    return 0;
                }

                if (first == "/capture")
                {
                    if (args.Length < 3)
                        throw new ArgumentException("Usage: AnimatedWallpaper.exe /capture VIDEO OUTPUT.png [WIDTH HEIGHT]");
                    int width = args.Length > 3 ? int.Parse(args[3]) : 1920;
                    int height = args.Length > 4 ? int.Parse(args[4]) : 1080;
                    return RunCapture(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), width, height);
                }

                if (first.StartsWith("/p") || first.StartsWith("-p") || first.StartsWith("/c") || first.StartsWith("-c"))
                    return 0;

                string mediaPath = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
                    ? Path.GetFullPath(args[1])
                    : defaultVideo;
                if (!File.Exists(mediaPath))
                    throw new FileNotFoundException("Wallpaper video was not found.", mediaPath);

                if (first == "/s" || first == "-s" || first == "/screensaver")
                    return RunScreenSaver(mediaPath);

                return RunDesktop(mediaPath, first == "/toggle");
            }
            catch (Exception ex)
            {
                Log("Fatal: " + ex);
                return 1;
            }
        }

        private static int RunDesktop(string mediaPath, bool toggle)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, DesktopMutexName, out created))
            {
                if (!created)
                {
                    if (toggle)
                    {
                        // The first process can still be initializing its event. Retry
                        // briefly so a quick second launch still reliably exits it.
                        for (int attempt = 0; attempt < 20; attempt++)
                        {
                            try
                            {
                                using (EventWaitHandle closeEvent = EventWaitHandle.OpenExisting(DesktopCloseEventName))
                                    closeEvent.Set();
                                break;
                            }
                            catch (WaitHandleCannotBeOpenedException)
                            {
                                Thread.Sleep(100);
                            }
                        }
                    }
                    return 0;
                }

                using (EventWaitHandle closeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, DesktopCloseEventName))
                {
                    Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    VideoWindow window = new VideoWindow(mediaPath, false);
                    window.Closed += delegate { app.Shutdown(); };
                    RegisteredWaitHandle waiter = ThreadPool.RegisterWaitForSingleObject(
                        closeEvent,
                        delegate { app.Dispatcher.BeginInvoke(new Action(window.Close)); },
                        null,
                        Timeout.Infinite,
                        true);

                    window.Show();
                    int result = app.Run();
                    waiter.Unregister(null);
                    return result;
                }
            }
        }

        private static int RunScreenSaver(string mediaPath)
        {
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            VideoWindow window = new VideoWindow(mediaPath, true);
            window.Closed += delegate { app.Shutdown(); };
            window.Show();
            return app.Run();
        }

        private static int RunCapture(string mediaPath, string outputPath, int width, int height)
        {
            if (!File.Exists(mediaPath))
                throw new FileNotFoundException("Video was not found.", mediaPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            int exitCode = 1;
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            CaptureWindow window = new CaptureWindow(mediaPath, outputPath, width, height, delegate(bool ok)
            {
                exitCode = ok ? 0 : 1;
                app.Shutdown();
            });
            window.Show();
            app.Run();
            return exitCode;
        }

        internal static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AnimatedWallpaper");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "AnimatedWallpaper.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    internal sealed class VideoWindow : Window
    {
        private readonly MediaElement media;
        private readonly bool screenSaver;
        private readonly string mediaPath;
        private readonly DateTime shownAt = DateTime.UtcNow;
        private Point initialMouse;
        private bool haveInitialMouse;
        private IntPtr desktopParent;
        private DispatcherTimer desktopCheckTimer;
        private Process mpvProcess;
        private bool shuttingDown;
        private bool rendererPaused;

        public VideoWindow(string mediaPath, bool isScreenSaver)
        {
            screenSaver = isScreenSaver;
            this.mediaPath = mediaPath;
            Title = isScreenSaver ? "Animated Wallpaper Screen Saver" : "Animated Desktop Wallpaper";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Brushes.Black;
            Focusable = isScreenSaver;
            Topmost = isScreenSaver;
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            if (screenSaver)
            {
                media = new MediaElement
                {
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Manual,
                    Stretch = Stretch.UniformToFill,
                    Volume = 0,
                    IsMuted = true,
                    ScrubbingEnabled = true,
                    Source = new Uri(mediaPath, UriKind.Absolute)
                };
                RenderOptions.SetBitmapScalingMode(media, BitmapScalingMode.HighQuality);
                Content = media;

                media.MediaOpened += delegate
                {
                    Program.Log(string.Format(
                        "Screen saver opened: {0}x{1}, duration={2}, file={3}",
                        media.NaturalVideoWidth,
                        media.NaturalVideoHeight,
                        media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan.ToString() : "unknown",
                        mediaPath));
                    media.Play();
                };
                media.MediaEnded += delegate
                {
                    media.Position = TimeSpan.Zero;
                    media.Play();
                };
                media.MediaFailed += delegate(object sender, ExceptionRoutedEventArgs e)
                {
                    Program.Log("Screen-saver playback failed: " + e.ErrorException);
                };
            }
            else
            {
                Content = new Border { Background = Brushes.Black };
            }

            Loaded += delegate
            {
                if (screenSaver)
                {
                    media.Play();
                    Activate();
                    Focus();
                }
                else
                {
                    StartDesktopRenderer();
                }
            };
            Closed += delegate
            {
                shuttingDown = true;
                if (desktopCheckTimer != null)
                    desktopCheckTimer.Stop();
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                StopDesktopRenderer();
                if (media != null)
                {
                    media.Stop();
                    media.Close();
                }
            };

            if (screenSaver)
            {
                Cursor = Cursors.None;
                KeyDown += delegate { Close(); };
                MouseDown += delegate { Close(); };
                MouseWheel += delegate { Close(); };
                MouseMove += OnScreenSaverMouseMove;
            }
            else
            {
                SourceInitialized += delegate { AttachToDesktop(); };
                SystemEvents.SessionSwitch += OnSessionSwitch;
                desktopCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                desktopCheckTimer.Tick += delegate
                {
                    IntPtr handle = new WindowInteropHelper(this).Handle;
                    if (!NativeMethods.IsWindow(desktopParent) || NativeMethods.GetParent(handle) != desktopParent)
                        AttachToDesktop();
                    if (mpvProcess == null || mpvProcess.HasExited)
                        StartDesktopRenderer();
                };
                desktopCheckTimer.Start();
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (e.Reason == SessionSwitchReason.SessionLock ||
                    e.Reason == SessionSwitchReason.SessionLogoff ||
                    e.Reason == SessionSwitchReason.ConsoleDisconnect ||
                    e.Reason == SessionSwitchReason.RemoteDisconnect)
                {
                    if (screenSaver)
                        media.Pause();
                    else
                    {
                        rendererPaused = true;
                        StopDesktopRenderer();
                    }
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock ||
                         e.Reason == SessionSwitchReason.SessionLogon ||
                         e.Reason == SessionSwitchReason.ConsoleConnect ||
                         e.Reason == SessionSwitchReason.RemoteConnect)
                {
                    if (screenSaver)
                        media.Play();
                    else
                    {
                        rendererPaused = false;
                        StartDesktopRenderer();
                    }
                }
            }));
        }

        private void StartDesktopRenderer()
        {
            if (screenSaver || shuttingDown || rendererPaused || (mpvProcess != null && !mpvProcess.HasExited))
                return;

            string mpvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "mpv.exe");
            if (!File.Exists(mpvPath))
            {
                Program.Log("MPV renderer was not found: " + mpvPath);
                return;
            }

            IntPtr host = new WindowInteropHelper(this).Handle;
            string arguments = string.Join(" ", new[]
            {
                "--no-config",
                "--loop-file=inf",
                "--no-audio",
                "--hwdec=auto-safe",
                "--vo=gpu-next",
                "--gpu-api=d3d11",
                "--video-sync=display-resample",
                "--scale=ewa_lanczossharp",
                "--cscale=ewa_lanczossharp",
                "--dscale=mitchell",
                "--correct-downscaling=yes",
                "--deband=yes",
                "--no-osc",
                "--no-input-default-bindings",
                "--cursor-autohide=always",
                "--keep-open=yes",
                "--no-terminal",
                "--really-quiet",
                "--wid=" + host,
                QuoteArgument(mediaPath)
            });

            ProcessStartInfo startInfo = new ProcessStartInfo(mpvPath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(mpvPath)
            };
            mpvProcess = Process.Start(startInfo);
            if (mpvProcess != null)
            {
                mpvProcess.EnableRaisingEvents = true;
                mpvProcess.Exited += delegate
                {
                    Program.Log("MPV renderer exited.");
                    if (!shuttingDown && !rendererPaused)
                        Dispatcher.BeginInvoke(new Action(StartDesktopRenderer));
                };
                Program.Log(string.Format("Started MPV renderer pid={0}, host hwnd={1}, file={2}.", mpvProcess.Id, host, mediaPath));
            }
        }

        private void StopDesktopRenderer()
        {
            if (mpvProcess == null)
                return;
            try
            {
                if (!mpvProcess.HasExited)
                    mpvProcess.Kill();
            }
            catch (Exception ex)
            {
                Program.Log("Could not stop MPV renderer: " + ex.Message);
            }
            finally
            {
                mpvProcess.Dispose();
                mpvProcess = null;
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private void OnScreenSaverMouseMove(object sender, MouseEventArgs e)
        {
            Point current = e.GetPosition(this);
            if (!haveInitialMouse)
            {
                initialMouse = current;
                haveInitialMouse = true;
                return;
            }
            if ((DateTime.UtcNow - shownAt).TotalMilliseconds < 900)
            {
                initialMouse = current;
                return;
            }
            if (Math.Abs(current.X - initialMouse.X) > 8 || Math.Abs(current.Y - initialMouse.Y) > 8)
                Close();
        }

        private void AttachToDesktop()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            IntPtr shellView;
            IntPtr worker;
            bool raisedDesktop;
            desktopParent = NativeMethods.FindWallpaperHost(out shellView, out worker, out raisedDesktop);
            if (desktopParent == IntPtr.Zero)
            {
                Program.Log("No WorkerW wallpaper host was found; using Progman.");
                desktopParent = NativeMethods.FindWindow("Progman", null);
            }
            if (desktopParent == IntPtr.Zero)
            {
                Program.Log("Desktop wallpaper host was not found.");
                return;
            }

            long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_STYLE).ToInt64();
            style &= ~(NativeMethods.WS_POPUP | NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME);
            style |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_STYLE, new IntPtr(style));

            if (raisedDesktop)
            {
                long extendedStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
                extendedStyle |= NativeMethods.WS_EX_LAYERED;
                NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(extendedStyle));
            }

            NativeMethods.SetParent(handle, desktopParent);

            if (raisedDesktop)
            {
                NativeMethods.SetLayeredWindowAttributes(handle, 0, 255, NativeMethods.LWA_ALPHA);
                NativeMethods.SetWindowPos(
                    worker,
                    NativeMethods.HWND_BOTTOM,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            NativeMethods.RECT rect;
            if (NativeMethods.GetClientRect(desktopParent, out rect))
            {
                NativeMethods.SetWindowPos(
                    handle,
                    raisedDesktop && shellView != IntPtr.Zero ? shellView : NativeMethods.HWND_TOP,
                    0,
                    0,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);
                Program.Log(string.Format(
                    "Attached wallpaper hwnd={0} to desktop hwnd={1}, size={2}x{3}, raisedDesktop={4}.",
                    handle,
                    desktopParent,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top,
                    raisedDesktop));
            }
        }
    }

    internal sealed class CaptureWindow : Window
    {
        private readonly MediaElement media;
        private readonly string outputPath;
        private readonly int outputWidth;
        private readonly int outputHeight;
        private readonly Action<bool> completed;
        private bool finished;

        public CaptureWindow(string mediaPath, string destination, int width, int height, Action<bool> callback)
        {
            outputPath = destination;
            outputWidth = width;
            outputHeight = height;
            completed = callback;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Left = -30000;
            Top = -30000;
            Width = width;
            Height = height;
            Background = Brushes.Black;

            media = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Stretch = Stretch.UniformToFill,
                Volume = 0,
                IsMuted = true,
                ScrubbingEnabled = true,
                Source = new Uri(mediaPath, UriKind.Absolute)
            };
            RenderOptions.SetBitmapScalingMode(media, BitmapScalingMode.HighQuality);
            Content = media;

            media.MediaOpened += delegate
            {
                Program.Log(string.Format(
                    "Capture opened: {0}x{1}, duration={2}.",
                    media.NaturalVideoWidth,
                    media.NaturalVideoHeight,
                    media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan.ToString() : "unknown"));
                TimeSpan position = TimeSpan.FromSeconds(2);
                if (media.NaturalDuration.HasTimeSpan && media.NaturalDuration.TimeSpan < TimeSpan.FromSeconds(4))
                    position = TimeSpan.FromTicks(media.NaturalDuration.TimeSpan.Ticks / 2);
                media.Position = position;
                media.Play();
                DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                timer.Tick += delegate
                {
                    timer.Stop();
                    Capture();
                };
                timer.Start();
            };
            media.MediaFailed += delegate(object sender, ExceptionRoutedEventArgs e)
            {
                Program.Log("Frame capture playback failed: " + e.ErrorException);
                Finish(false);
            };
            Loaded += delegate { media.Play(); };

            DispatcherTimer timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            timeout.Tick += delegate
            {
                timeout.Stop();
                if (!finished)
                {
                    Program.Log("Frame capture timed out.");
                    Finish(false);
                }
            };
            timeout.Start();
        }

        private void Capture()
        {
            try
            {
                media.Pause();
                UpdateLayout();
                RenderTargetBitmap bitmap = new RenderTargetBitmap(
                    outputWidth,
                    outputHeight,
                    96,
                    96,
                    PixelFormats.Pbgra32);
                bitmap.Render(this);
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream stream = File.Create(outputPath))
                    encoder.Save(stream);
                Program.Log("Captured lock-screen frame: " + outputPath);
                Finish(true);
            }
            catch (Exception ex)
            {
                Program.Log("Frame capture failed: " + ex);
                Finish(false);
            }
        }

        private void Finish(bool ok)
        {
            if (finished)
                return;
            finished = true;
            try { media.Stop(); media.Close(); } catch { }
            completed(ok);
        }
    }

    internal static class NativeMethods
    {
        internal const int GWL_STYLE = -16;
        internal const int GWL_EXSTYLE = -20;
        internal const long WS_CHILD = 0x40000000L;
        internal const long WS_VISIBLE = 0x10000000L;
        internal const long WS_POPUP = 0x80000000L;
        internal const long WS_CAPTION = 0x00C00000L;
        internal const long WS_THICKFRAME = 0x00040000L;
        internal const long WS_EX_LAYERED = 0x00080000L;
        internal const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
        internal const uint LWA_ALPHA = 0x00000002;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const uint SWP_FRAMECHANGED = 0x0020;
        internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
        internal static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        private const uint WM_SPAWN_WORKER = 0x052C;
        private const uint SMTO_NORMAL = 0x0000;

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hwnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out IntPtr result);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetParent(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(
            IntPtr hwnd,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetLayeredWindowAttributes(
            IntPtr hwnd,
            uint colorKey,
            byte alpha,
            uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern IntPtr GetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern IntPtr SetWindowLong32(IntPtr hwnd, int index, IntPtr value);

        internal static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);
        }

        internal static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
        {
            return IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, value);
        }

        internal static IntPtr FindWallpaperHost(out IntPtr shellView, out IntPtr worker, out bool raisedDesktop)
        {
            shellView = IntPtr.Zero;
            worker = IntPtr.Zero;
            raisedDesktop = false;
            IntPtr progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
                return IntPtr.Zero;

            IntPtr ignored;
            SendMessageTimeout(progman, WM_SPAWN_WORKER, new IntPtr(0xD), new IntPtr(1), SMTO_NORMAL, 1000, out ignored);

            raisedDesktop =
                (GetWindowLongPtr(progman, GWL_EXSTYLE).ToInt64() & WS_EX_NOREDIRECTIONBITMAP) != 0;
            shellView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (raisedDesktop)
            {
                worker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                return progman;
            }

            IntPtr detectedShellView = shellView;
            IntPtr shellViewHost = IntPtr.Zero;
            EnumWindows(delegate(IntPtr top, IntPtr data)
            {
                IntPtr candidate = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (candidate != IntPtr.Zero)
                {
                    detectedShellView = candidate;
                    shellViewHost = top;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            shellView = detectedShellView;
            worker = shellViewHost != IntPtr.Zero
                ? FindWindowEx(IntPtr.Zero, shellViewHost, "WorkerW", null)
                : IntPtr.Zero;
            return worker != IntPtr.Zero ? worker : progman;
        }
    }
}
