using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AppUsage
{
    internal static class UsageLogTests
    {
        private const string Header = "app-usage-v1\r\n";
        private const string Hour = "2026011509";
        private static readonly DateTime Day = new DateTime(2026, 1, 15);
        private static readonly ApplicationInfo App = new ApplicationInfo("name:TEST", "Test");
        private static readonly ApplicationInfo Other = new ApplicationInfo("name:OTHER", "Other");

        [STAThread]
        private static void Main(string[] args)
        {
            string root = args[0];
            RoundTripAndHourSplit(NewDirectory(root, "round-trip"));
            ForegroundTiming(NewDirectory(root, "foreground"));
            PauseStatus(NewDirectory(root, "pause-status"));
            PeriodBoundaries(NewDirectory(root, "periods"));
            ReadRepeatedHour(NewDirectory(root, "repeated-hour"));
            DurationDisplay();
            int scenario = 0;
            foreach (string corrupt in new[]
            {
                "incorrect header",
                Header + Row("name:TEST", "20") + Row("name:TEST", "30"),
                Header + Row("name:TEST", "NaN"),
                Header + Row("name:TEST", "Infinity"),
                Header + Row("name:TEST", "-1"),
                Header + Row("name:TEST", "10801"),
                Header + Row("name:TEST", "9000") + Row("name:OTHER", "9000"),
                Header + "2026999909\tQQ==\tQQ==\t2\r\n",
                Header + Hour + "\t?\tQQ==\t2\r\n",
                Header + Hour + "\t/w==\tQQ==\t2\r\n"
            })
            {
                RecoverBackup(NewDirectory(root, "recovery-" + ++scenario), corrupt);
            }
            RetryReading(NewDirectory(root, "retry-primary"), false);
            RetryReading(NewDirectory(root, "retry-backup"), true);
            PreserveInvalidFiles(NewDirectory(root, "invalid"));
            Console.WriteLine("Usage log and activity timing tests passed.");
        }

        private static string NewDirectory(string root, string name)
        {
            return Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        }

        private static string Row(string id, string seconds)
        {
            return Hour + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(id)) +
                "\tVGVzdA==\t" + seconds + "\r\n";
        }

        private static void RoundTripAndHourSplit(string directory)
        {
            var log = new UsageLog(directory);
            var start = Day.AddHours(10).ToUniversalTime().AddSeconds(-1.25);
            log.Add(start, start.AddSeconds(4.5), App);
            log.Save();

            var reopened = new UsageLog(directory);
            Equal(null, reopened.LoadError);
            UsageReport report = reopened.GetReport(Day, 0);
            Equal(4.5, report.Seconds);
            Equal(1.25, report.Trend[9]);
            Equal(3.25, report.Trend[10]);
            Equal(4.5, reopened.GetReport(Day, 1).Seconds);
            Equal(4.5, reopened.GetReport(Day, 2).Seconds);
        }

        private static void RecoverBackup(string directory, string corrupt)
        {
            string path = Path.Combine(directory, "usage.tsv");
            string good = Header + Row("name:TEST", "20.25");
            File.WriteAllText(path, corrupt);
            File.WriteAllText(path + ".bak", good, new UTF8Encoding(true));
            string backupBytes = Convert.ToBase64String(File.ReadAllBytes(path + ".bak"));

            var log = new UsageLog(directory);
            Equal(null, log.LoadError);
            Equal(true, log.NeedsSave);
            Equal(20.25, log.GetReport(Day, 0).Seconds);
            AddFiveSeconds(log);
            log.Save();

            Equal(backupBytes, Convert.ToBase64String(File.ReadAllBytes(path + ".bak")));
            string[] snapshots = Directory.GetFiles(directory, "usage.tsv.corrupt.*");
            Equal(1, snapshots.Length);
            Equal(corrupt, File.ReadAllText(snapshots[0]));
            Equal(25.25, new UsageLog(directory).GetReport(Day, 0).Seconds);
        }

        private static void ForegroundTiming(string directory)
        {
            var log = new UsageLog(directory);
            DateTime start = Day.AddHours(9).ToUniversalTime();
            ActivityTracker.RecordInterval(log, start, start.AddSeconds(1), 1, 10, App, .1, Other);
            UsageReport report = log.GetReport(Day, 0);
            Equal(1.0, report.Seconds);
            Equal(App.Id, report.Apps[0].Id);

            start = start.AddSeconds(10);
            ActivityTracker.RecordInterval(log, start, start.AddSeconds(1), 1, 119.75, App, 120.75, null);
            Equal(1.25, log.GetReport(Day, 0).Seconds);

            start = start.AddSeconds(10);
            ActivityTracker.RecordInterval(log, start, start.AddSeconds(1), 1, 121, null, .5, Other);
            Equal(1.75, log.GetReport(Day, 0).Seconds);

            start = start.AddSeconds(10);
            ActivityTracker.RecordInterval(log, start, start.AddSeconds(1), 1, 119.75, App, .5, Other);
            report = log.GetReport(Day, 0);
            Equal(2.5, report.Seconds);
            Equal(1.5, report.Apps[0].Seconds);
            Equal(1.0, report.Apps[1].Seconds);

            start = start.AddSeconds(10);
            ActivityTracker.RecordInterval(log, start, start.AddSeconds(1), 1, 119.75, App, .875, Other);
            ActivityTracker.RecordInterval(log, start, start.AddSeconds(60), 60, 118, App, .5, Other);
            ActivityTracker.RecordInterval(log, start, start.AddSeconds(60), 1, 118, App, .5, Other);
            ActivityTracker.RecordInterval(log, start, start.AddSeconds(-.5), 1, 118, App, .5, Other);
            ActivityTracker.RecordInterval(log, start, start.AddSeconds(1), 1, 10, null, .5, Other);
            report = log.GetReport(Day, 0);
            Equal(3.5, report.Seconds);
            Equal(2.5, report.Apps[0].Seconds);
        }

        private static void PeriodBoundaries(string directory)
        {
            var log = new UsageLog(directory);
            var monday = new DateTime(2026, 6, 1);
            var boundary = monday.ToUniversalTime();
            log.Add(boundary.AddSeconds(-1.5), boundary.AddSeconds(2.25), App);
            for (int range = 0; range <= 2; range++)
            {
                Equal(1.5, log.GetReport(monday.AddDays(-1), range).Seconds);
                Equal(2.25, log.GetReport(monday, range).Seconds);
            }
            Equal(1.5, log.GetReport(monday.AddDays(-1), 0).Trend[23]);
            Equal(2.25, log.GetReport(monday, 0).Trend[0]);
        }

        private static void PauseStatus(string directory)
        {
            var log = new UsageLog(directory);
            var tracker = new ActivityTracker(log);
            tracker.SetSessionLocked(true);
            tracker.Sample();
            Equal("会话锁定或断开", tracker.Status);
            tracker.SetSuspended(true);
            tracker.Sample();
            Equal("系统休眠暂停", tracker.Status);
            tracker.SetSuspended(false);
            tracker.Sample();
            Equal("会话锁定或断开", tracker.Status);
            Equal(false, log.NeedsSave);
        }

        private static void ReadRepeatedHour(string directory)
        {
            string path = Path.Combine(directory, "usage.tsv");
            File.WriteAllText(path, Header + Row(App.Id, "5400") + Row(Other.Id, "1800"));
            var log = new UsageLog(directory);
            Equal(null, log.LoadError);
            Equal(7200.0, log.GetReport(Day, 0).Seconds);
            AddFiveSeconds(log);
            log.Save();
            Equal(7205.0, new UsageLog(directory).GetReport(Day, 0).Seconds);
        }

        private static void DurationDisplay()
        {
            Equal("不足 1 秒", Dashboard.FormatTime(.25));
            Equal("1 分钟 1 秒", Dashboard.FormatTime(61));
            Equal("1 小时 1 分钟 1 秒", Dashboard.FormatTime(3661));
            Equal(false, "无效时长" == Dashboard.FormatTime(TimeSpan.MaxValue.TotalSeconds));
            Equal("无效时长", Dashboard.FormatTime(TimeSpan.MaxValue.TotalSeconds + 1));
            var typeface = new System.Windows.Media.Typeface(
                new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),
                System.Windows.FontStyles.Normal, System.Windows.FontWeights.SemiBold, System.Windows.FontStretches.Normal);
            var text = new System.Windows.Media.FormattedText(Dashboard.FormatTime(744 * 3600 - 1),
                CultureInfo.GetCultureInfo("zh-CN"), System.Windows.FlowDirection.LeftToRight,
                typeface, 13, System.Windows.Media.Brushes.Black, 1);
            Equal(true, text.Width < 152);
        }

        private static void RetryReading(string directory, bool lockBackup)
        {
            string path = Path.Combine(directory, "usage.tsv");
            string lockedPath = lockBackup ? path + ".bak" : path;
            File.WriteAllText(lockedPath, Header + Row("name:TEST", "20"));
            if (!lockBackup)
                File.WriteAllText(path + ".bak", Header + Row("name:TEST", "10"));
            UsageLog log;
            using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                log = new UsageLog(directory);
                Equal(true, log.LoadError != null);
                AddFiveSeconds(log);
                ExpectSaveFailure(log);
                Equal(5.0, log.GetReport(Day, 0).Seconds);
            }

            log.Save();
            log.Save();
            Equal(null, log.LoadError);
            Equal(false, log.NeedsSave);
            Equal(25.0, new UsageLog(directory).GetReport(Day, 0).Seconds);
        }

        private static void PreserveInvalidFiles(string directory)
        {
            string path = Path.Combine(directory, "usage.tsv");
            File.WriteAllText(path, "bad primary");
            File.WriteAllText(path + ".bak", "bad backup");

            var log = new UsageLog(directory);
            AddFiveSeconds(log);
            ExpectSaveFailure(log);
            Equal("bad primary", File.ReadAllText(path));
            Equal("bad backup", File.ReadAllText(path + ".bak"));
            Equal(5.0, log.GetReport(Day, 0).Seconds);

            File.WriteAllText(path + ".bak", Header + Row("name:TEST", "20"));
            Directory.CreateDirectory(path + ".tmp");
            ExpectSaveFailure(log);
            Equal(25.0, log.GetReport(Day, 0).Seconds);
            Directory.Delete(path + ".tmp");
            log.Save();
            Equal(25.0, new UsageLog(directory).GetReport(Day, 0).Seconds);
        }

        private static void AddFiveSeconds(UsageLog log)
        {
            var start = Day.AddHours(9).ToUniversalTime();
            log.Add(start, start.AddSeconds(5), App);
        }

        private static void ExpectSaveFailure(UsageLog log)
        {
            try { log.Save(); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            throw new InvalidOperationException("Expected save to fail.");
        }

        private static void Equal<T>(T expected, T actual)
        {
            if (!object.Equals(expected, actual))
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "Expected {0}, got {1}.", expected, actual));
        }
    }
}
