using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AppUsage
{
    internal sealed class ActivityTracker
    {
        internal const double IdleLimitSeconds = 120;
        private readonly UsageLog log;
        private readonly ForegroundApplication foreground = new ForegroundApplication();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private TimeSpan previousTick;
        private DateTime previousUtc;
        private double previousIdle;
        private ApplicationInfo previousApp;
        private volatile bool sessionLocked;
        private volatile bool suspended;
        private int resetRequested;

        internal ActivityTracker(UsageLog log)
        {
            this.log = log;
        }

        internal string Status { get; private set; }
        internal ApplicationInfo CurrentApp { get { return previousApp; } }

        internal void SetSessionLocked(bool value)
        {
            sessionLocked = value;
            Interlocked.Exchange(ref resetRequested, 1);
        }

        internal void SetSuspended(bool value)
        {
            suspended = value;
            Interlocked.Exchange(ref resetRequested, 1);
        }

        internal void Sample()
        {
            var tick = clock.Elapsed;
            DateTime now = DateTime.UtcNow;
            bool locked = sessionLocked;
            bool sleeping = suspended;
            bool paused = locked || sleeping;
            double elapsed = (tick - previousTick).TotalSeconds;
            bool reset = Interlocked.Exchange(ref resetRequested, 0) != 0;

            double idle = paused ? IdleLimitSeconds : foreground.IdleSeconds();
            ApplicationInfo app = idle < IdleLimitSeconds ? foreground.Read() : null;
            if (!paused && !reset && previousUtc != default(DateTime))
                RecordInterval(log, previousUtc, now, elapsed, previousIdle, previousApp, idle, app);

            previousIdle = idle;
            previousApp = app;
            previousTick = tick;
            previousUtc = now;

            if (sleeping)
                Status = "系统休眠暂停";
            else if (locked)
                Status = "会话锁定或断开";
            else if (previousIdle >= IdleLimitSeconds)
                Status = "空闲暂停";
            else if (previousApp == null)
                Status = "当前窗口不计时";
            else
                Status = "正在记录";
        }

        internal static void RecordInterval(UsageLog log, DateTime startUtc, DateTime nowUtc, double elapsed,
            double previousIdle, ApplicationInfo previousApp, double idle, ApplicationInfo app)
        {
            double wallElapsed = (nowUtc - startUtc).TotalSeconds;
            if (elapsed <= 0 || elapsed > 5 || wallElapsed <= 0 || Math.Abs(wallElapsed - elapsed) >= 2)
                return;

            double previousActive = Math.Max(0, IdleLimitSeconds - previousIdle);
            double inputOffset = elapsed - idle;
            bool inputDuringInterval = idle >= 0 && inputOffset > 0 && idle < previousIdle;
            if (inputDuringInterval && inputOffset <= previousActive)
                previousActive = elapsed;

            double active = Math.Min(elapsed, previousActive);
            if (previousApp != null && active > 0)
                log.Add(startUtc, startUtc.AddTicks((long)(active * TimeSpan.TicksPerSecond)), previousApp);

            // Resume on the observed app when a new input falls after the previous idle cutoff.
            if (app != null && inputDuringInterval && inputOffset > previousActive && idle > 0)
                log.Add(startUtc.AddTicks((long)(inputOffset * TimeSpan.TicksPerSecond)),
                    startUtc.AddTicks((long)(elapsed * TimeSpan.TicksPerSecond)), app);
        }
    }

    internal sealed class ForegroundApplication
    {
        private readonly int self = Process.GetCurrentProcess().Id;
        private uint lastPid;
        private IntPtr lastWindow;
        private ApplicationInfo lastApplication;

        internal double IdleSeconds()
        {
            var info = new LastInputInfo { Size = Marshal.SizeOf(typeof(LastInputInfo)) };
            if (!GetLastInputInfo(ref info))
                return ActivityTracker.IdleLimitSeconds;

            var elapsed = unchecked((uint)Environment.TickCount - info.Time);
            return elapsed / 1000.0;
        }

        internal ApplicationInfo Read()
        {
            IntPtr window = GetForegroundWindow();
            uint pid;
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out pid) == 0 ||
                pid == 0 || pid == self)
            {
                lastWindow = IntPtr.Zero;
                return null;
            }
            if (window == lastWindow && pid == lastPid && lastApplication != null)
                return lastApplication;

            lastWindow = window;
            lastPid = pid;
            lastApplication = null;

            var className = new StringBuilder(128);
            GetClassName(window, className, className.Capacity);
            string kind = className.ToString();
            if (kind == "Progman" || kind == "WorkerW" || kind == "Shell_TrayWnd" ||
                kind == "Shell_SecondaryTrayWnd")
                return null;

            if (kind == "ApplicationFrameWindow")
            {
                uint childPid = 0;
                EnumChildWindows(window, (handle, state) =>
                {
                    uint candidate;
                    if (GetWindowThreadProcessId(handle, out candidate) != 0 && candidate != pid && candidate != 0)
                    {
                        childPid = candidate;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
                if (childPid != 0)
                    pid = childPid;
            }

            try
            {
                using (var process = Process.GetProcessById((int)pid))
                {
                    string processName = process.ProcessName;
                    string path = GetImagePath(pid);
                    string name = GetDisplayName(path, processName);
                    lastApplication = new ApplicationInfo(Identify(path, processName), name, path);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }

            return lastApplication;
        }

        private static string GetDisplayName(string path, string fallback)
        {
            if (string.Equals(fallback, "explorer", StringComparison.OrdinalIgnoreCase))
                return "文件资源管理器";
            if (path == null)
                return fallback;

            try
            {
                string name = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(name))
                    return name.Trim();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return fallback;
        }

        internal static string Identify(string path, string processName)
        {
            return path == null ? "name:" + processName.ToUpperInvariant() :
                "exe:" + Hash(path.ToUpperInvariant());
        }

        internal static string GetImagePath(uint pid)
        {
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero)
                return null;

            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                return QueryFullProcessImageName(process, 0, buffer, ref size) ?
                    buffer.ToString() : null;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        internal static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            internal int Size;
            internal uint Time;
        }

        private delegate bool EnumWindowCallback(IntPtr handle, IntPtr state);
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int maxCount);
        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr state);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
