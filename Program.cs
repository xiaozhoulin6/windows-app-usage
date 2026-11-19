using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AppUsage
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string dataOverride = Environment.GetEnvironmentVariable("APP_USAGE_DATA_DIR");
            bool isolated = !string.IsNullOrWhiteSpace(dataOverride);
            string dataDirectory = isolated ? Path.GetFullPath(dataOverride) :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppUsage");
            string key = @"Local\AppUsage-" + ForegroundApplication.Hash(dataDirectory.ToUpperInvariant());
            bool background = Array.Exists(args, arg => arg == "--background");

            using (var activate = new EventWaitHandle(false, EventResetMode.AutoReset, key + "-Show"))
            using (var instance = new Mutex(false, key))
            {
                bool acquired;
                try { acquired = instance.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }

                if (!acquired)
                {
                    if (!background)
                        activate.Set();
                    return;
                }
                try
                {
                    new UsageApplication(dataDirectory, isolated, activate).RunApp(background);
                }
                finally
                {
                    instance.ReleaseMutex();
                }
            }
        }
    }

    internal sealed class UsageApplication : Application
    {
        private readonly UsageLog log;
        private readonly ApplicationIcons icons;
        private readonly ActivityTracker tracker;
        private readonly StartupSetting startup;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ContextMenuStrip trayMenu;
        private readonly Icon trayIcon;
        private readonly DispatcherTimer timer;
        private readonly RegisteredWaitHandle activationWait;
        private readonly Stopwatch sinceSave = Stopwatch.StartNew();
        private bool exiting;
        private bool ranIconLookup;
        private bool storageWarningActive;
        private string saveError;
        private Dashboard window;
        private int selectedRange;
        private Rect windowBounds = Rect.Empty;
        private bool windowMaximized;

        internal UsageApplication(string dataDirectory, bool isolated, EventWaitHandle activate)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            log = new UsageLog(dataDirectory);
            icons = new ApplicationIcons(dataDirectory);
            tracker = new ActivityTracker(log);
            startup = new StartupSetting(isolated);
            trayIcon = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetEntryAssembly().Location) ??
                (Icon)SystemIcons.Application.Clone();

            trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("打开使用情况", null, delegate { ShowWindow(); });
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add("退出并停止记录", null, delegate { ExitApp(); });
            tray = new Forms.NotifyIcon
            {
                Icon = trayIcon,
                Text = "应用使用情况",
                ContextMenuStrip = trayMenu
            };
            tray.DoubleClick += delegate { ShowWindow(); };

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SessionEnding += delegate { Save(); };

            timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += OnTick;

            activationWait = ThreadPool.RegisterWaitForSingleObject(activate, delegate
            {
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(ShowWindow));
            }, null, Timeout.Infinite, false);
        }

        internal void RunApp(bool background)
        {
            tray.Visible = true;
            UpdateStorageStatus();
            tracker.Sample();
            timer.Start();
            if (!background)
                ShowWindow();

            try { Run(); }
            finally
            {
                timer.Stop();
                activationWait.Unregister(null);
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                Save();
                tray.Dispose();
                trayMenu.Dispose();
                trayIcon.Dispose();
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            tracker.Sample();
            ApplicationInfo app = tracker.CurrentApp;
            if (app != null)
                icons.Remember(app.Id, app.ImagePath);
            if (sinceSave.Elapsed.TotalSeconds >= 30)
            {
                Save();
                sinceSave.Restart();
            }
            if (window != null && window.IsVisible)
                window.UpdateStatus(tracker.Status, log.LoadError ?? saveError);
        }

        private void Save()
        {
            try
            {
                log.Save();
                saveError = null;
            }
            catch (IOException)
            {
                saveError = "暂时无法保存使用记录，正在重试";
            }
            catch (UnauthorizedAccessException)
            {
                saveError = "无法写入使用记录，请检查保存目录的权限";
            }
            UpdateStorageStatus();
        }

        private void UpdateStorageStatus()
        {
            string issue = log.LoadError ?? saveError;
            if (window != null && window.IsVisible)
                window.UpdateStatus(tracker.Status, issue);

            bool failed = issue != null;
            if (failed == storageWarningActive)
                return;
            storageWarningActive = failed;
            tray.Text = failed ? "应用使用情况 · 记录异常" : "应用使用情况";
            if (failed && tray.Visible)
                tray.ShowBalloonTip(5000, "使用记录异常", issue + "。新记录暂存在内存，退出前请留意保存状态。",
                    Forms.ToolTipIcon.Warning);
        }

        private void ShowWindow()
        {
            if (exiting)
                return;
            if (window == null)
                window = CreateWindow();

            window.UpdateStatus(tracker.Status, log.LoadError ?? saveError);
            window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            LookUpExistingIcons();
        }

        private Dashboard CreateWindow()
        {
            var dashboard = new Dashboard(log, startup, icons, selectedRange);
            if (!windowBounds.IsEmpty)
            {
                dashboard.WindowStartupLocation = WindowStartupLocation.Manual;
                dashboard.Left = windowBounds.Left;
                dashboard.Top = windowBounds.Top;
                dashboard.Width = windowBounds.Width;
                dashboard.Height = windowBounds.Height;
            }
            if (windowMaximized)
                dashboard.WindowState = WindowState.Maximized;

            dashboard.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                trayIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            dashboard.Icon.Freeze();
            dashboard.Closing += delegate
            {
                selectedRange = dashboard.SelectedRange;
                windowBounds = dashboard.RestoreBounds;
                windowMaximized = dashboard.WindowState == WindowState.Maximized;
            };
            dashboard.Closed += delegate
            {
                MainWindow = null;
                window = null;
            };
            MainWindow = dashboard;
            return dashboard;
        }

        private void LookUpExistingIcons()
        {
            if (ranIconLookup)
                return;
            ranIconLookup = true;

            var today = DateTime.Today;
            var apps = log.GetReport(today, 1).Apps.Concat(log.GetReport(today, 2).Apps);
            var missing = new HashSet<string>(apps.Select(app => app.Id).Where(id => icons.Get(id) == null),
                StringComparer.Ordinal);
            if (missing.Count == 0)
                return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                var matches = ApplicationIcons.FindRunning(missing);
                if (matches.Count == 0 || Dispatcher.HasShutdownStarted)
                    return;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (exiting)
                        return;
                    bool changed = false;
                    foreach (var match in matches)
                        changed |= icons.Remember(match.Key, match.Value);
                    if (changed && window != null && window.IsVisible)
                        window.Refresh();
                }));
            });
        }

        private void ExitApp()
        {
            tracker.Sample();
            Save();
            if (log.NeedsSave)
            {
                const string text = "最新记录未能保存。仍要退出并停止记录吗？";
                const string title = "退出应用使用情况";
                var result = window != null && window.IsVisible
                    ? MessageBox.Show(window, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    : MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                    return;
            }

            exiting = true;
            Shutdown();
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.RemoteDisconnect ||
                e.Reason == SessionSwitchReason.ConsoleDisconnect)
                tracker.SetSessionLocked(true);
            else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.RemoteConnect ||
                e.Reason == SessionSwitchReason.ConsoleConnect)
                tracker.SetSessionLocked(false);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
                tracker.SetSuspended(true);
            else if (e.Mode == PowerModes.Resume)
                tracker.SetSuspended(false);
        }

    }

    internal sealed class StartupSetting
    {
        private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Entry = "LocalAppUsage";
        private readonly string startupCommand = "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\" --background";
        internal readonly bool Editable;

        internal StartupSetting(bool isolated)
        {
            Editable = !isolated;
        }

        internal bool IsEnabled()
        {
            if (!Editable)
                return false;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                return key != null && string.Equals(key.GetValue(Entry) as string,
                    startupCommand, StringComparison.OrdinalIgnoreCase);
        }

        internal void SetEnabled(bool enable)
        {
            if (!Editable)
                return;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                if (key == null)
                    throw new IOException("Unable to open the Windows startup registry key.");
                if (enable)
                    key.SetValue(Entry, startupCommand, RegistryValueKind.String);
                else
                    key.DeleteValue(Entry, false);
            }
        }
    }
}
