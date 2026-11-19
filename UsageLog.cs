using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AppUsage
{
    internal sealed class ApplicationInfo
    {
        internal readonly string Id;
        internal readonly string Name;
        internal readonly string ImagePath;

        internal ApplicationInfo(string id, string name, string imagePath = null)
        {
            Id = id;
            Name = name;
            ImagePath = imagePath;
        }
    }

    internal sealed class AppTotal
    {
        internal string Id;
        internal string Name;
        internal double Seconds;
    }

    internal sealed class UsageReport
    {
        internal DateTime Start;
        internal DateTime End;
        internal double[] Trend;
        internal double Seconds;
        internal AppTotal[] Apps;
    }

    internal sealed class UsageLog
    {
        // Local clock changes can put more than one elapsed hour into one local hour.
        private const double MaximumHourSeconds = 3 * 60 * 60;
        private const double HourRoundingToleranceSeconds = 0.000001;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private sealed class HourUsage
        {
            internal readonly Dictionary<string, AppTotal> Apps = new Dictionary<string, AppTotal>(StringComparer.Ordinal);
            internal double Seconds;
        }

        private enum ReadState { Missing, Invalid, Unavailable, Valid }

        private Dictionary<DateTime, HourUsage> entries = new Dictionary<DateTime, HourUsage>();
        private readonly string path;
        private bool loaded;
        private bool repairFromBackup;
        private bool dirty;
        internal bool NeedsSave { get { return dirty; } }
        internal string LoadError { get; private set; }

        internal UsageLog(string directory)
        {
            path = Path.Combine(directory, "usage.tsv");
            TryLoadHistory();
        }

        internal void Add(DateTime startUtc, DateTime endUtc, ApplicationInfo app)
        {
            while (startUtc < endUtc)
            {
                DateTime local = startUtc.ToLocalTime();
                DateTime hour = DateTime.SpecifyKind(local.Date.AddHours(local.Hour), DateTimeKind.Unspecified);
                DateTime next = startUtc.AddTicks(TimeSpan.TicksPerHour - (local.Ticks % TimeSpan.TicksPerHour));
                DateTime end = next < endUtc ? next : endUtc;
                dirty |= AddHour(entries, hour, app.Id, app.Name, (end - startUtc).TotalSeconds);
                startUtc = end;
            }
        }

        internal UsageReport GetReport(DateTime date, int range)
        {
            DateTime today = date.Date;
            DateTime start = today;
            DateTime end = today.AddDays(1);
            if (range == 1)
            {
                start = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
                end = start.AddDays(7);
            }
            else if (range == 2)
            {
                start = new DateTime(today.Year, today.Month, 1);
                end = start.AddMonths(1);
            }

            var totals = new Dictionary<string, AppTotal>();
            var trend = new double[range == 0 ? 24 : (end - start).Days];
            double totalSeconds = 0;
            foreach (var hour in entries)
            {
                if (hour.Key < start || hour.Key >= end)
                    continue;

                int bucket = range == 0 ? hour.Key.Hour : (hour.Key.Date - start).Days;
                foreach (var row in hour.Value.Apps)
                {
                    double seconds = row.Value.Seconds;
                    AppTotal total;
                    if (!totals.TryGetValue(row.Key, out total))
                    {
                        total = new AppTotal { Id = row.Key, Name = row.Value.Name };
                        totals.Add(row.Key, total);
                    }
                    total.Seconds += seconds;
                    trend[bucket] += seconds;
                    totalSeconds += seconds;
                }
            }
            return new UsageReport
            {
                Start = start, End = end, Trend = trend, Seconds = totalSeconds,
                Apps = totals.Values.OrderByDescending(x => x.Seconds).ThenBy(x => x.Name).ToArray()
            };
        }

        internal void Save()
        {
            if (!loaded && !TryLoadHistory())
                throw new IOException(LoadError);
            if (!dirty)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporaryPath = path + ".tmp";
            using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(file, new UTF8Encoding(false), 4096, true))
                {
                    writer.WriteLine("app-usage-v1");
                    foreach (var hour in entries.OrderBy(x => x.Key))
                    {
                        foreach (var row in hour.Value.Apps.OrderBy(x => x.Key))
                        {
                            writer.Write(hour.Key.ToString("yyyyMMddHH", CultureInfo.InvariantCulture));
                            writer.Write('\t');
                            writer.Write(Encode(row.Key));
                            writer.Write('\t');
                            writer.Write(Encode(row.Value.Name));
                            writer.Write('\t');
                            writer.WriteLine(row.Value.Seconds.ToString("R", CultureInfo.InvariantCulture));
                        }
                    }
                }
                file.Flush(true);
            }
            if (File.Exists(path))
            {
                string previous = repairFromBackup ? ".corrupt." + Guid.NewGuid().ToString("N") : ".bak";
                File.Replace(temporaryPath, path, path + previous, true);
            }
            else
                File.Move(temporaryPath, path);
            repairFromBackup = false;
            dirty = false;
        }

        private bool TryLoadHistory()
        {
            Dictionary<DateTime, HourUsage> history;
            ReadState primary = ReadFile(path, out history);
            if (primary == ReadState.Unavailable)
            {
                LoadError = "暂时无法读取使用记录，正在重试";
                return false;
            }

            if (primary != ReadState.Valid)
            {
                ReadState backup = ReadFile(path + ".bak", out history);
                if (backup == ReadState.Unavailable)
                {
                    LoadError = "暂时无法读取备份，正在重试";
                    return false;
                }
                if (backup == ReadState.Valid)
                {
                    repairFromBackup = true;
                    dirty = true;
                }
                else if (primary != ReadState.Missing || backup != ReadState.Missing)
                {
                    LoadError = "使用记录无法恢复，原始文件已保留";
                    return false;
                }
            }

            if (history != null)
            {
                foreach (var hour in entries)
                    foreach (var row in hour.Value.Apps)
                        AddHour(history, hour.Key, row.Key, row.Value.Name, row.Value.Seconds);
                entries = history;
            }
            loaded = true;
            LoadError = null;
            return true;
        }

        private static ReadState ReadFile(string file, out Dictionary<DateTime, HourUsage> history)
        {
            history = null;
            try
            {
                using (var reader = new StreamReader(file, StrictUtf8, false))
                {
                    string header = reader.ReadLine();
                    if (header != "app-usage-v1" && header != "\uFEFFapp-usage-v1")
                        return ReadState.Invalid;

                    var result = new Dictionary<DateTime, HourUsage>();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] columns = line.Split('\t');
                        DateTime hour;
                        double seconds;
                        if (columns.Length != 4 ||
                            !DateTime.TryParseExact(columns[0], "yyyyMMddHH", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out hour) ||
                            !double.TryParse(columns[3], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
                            !(seconds > 0 && seconds <= MaximumHourSeconds + HourRoundingToleranceSeconds))
                            return ReadState.Invalid;

                        string id = Decode(columns[1]);
                        string name = Decode(columns[2]);
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                            return ReadState.Invalid;

                        HourUsage bucket;
                        if (!result.TryGetValue(hour, out bucket))
                        {
                            bucket = new HourUsage();
                            result.Add(hour, bucket);
                        }
                        if (bucket.Apps.ContainsKey(id) ||
                            bucket.Seconds + seconds > MaximumHourSeconds + HourRoundingToleranceSeconds)
                            return ReadState.Invalid;

                        bucket.Apps.Add(id, new AppTotal { Name = name, Seconds = seconds });
                        bucket.Seconds += seconds;
                    }
                    history = result;
                }
                return ReadState.Valid;
            }
            catch (FileNotFoundException) { return ReadState.Missing; }
            catch (DirectoryNotFoundException) { return ReadState.Missing; }
            catch (IOException) { return ReadState.Unavailable; }
            catch (UnauthorizedAccessException) { return ReadState.Unavailable; }
            catch (FormatException) { return ReadState.Invalid; }
            catch (DecoderFallbackException) { return ReadState.Invalid; }
        }

        private static bool AddHour(Dictionary<DateTime, HourUsage> data, DateTime hour, string id, string name, double seconds)
        {
            HourUsage bucket;
            if (!data.TryGetValue(hour, out bucket))
            {
                bucket = new HourUsage();
                data.Add(hour, bucket);
            }
            double accepted = Math.Min(seconds, MaximumHourSeconds - bucket.Seconds);
            if (accepted <= 0)
                return false;
            AppTotal app;
            if (!bucket.Apps.TryGetValue(id, out app))
            {
                app = new AppTotal();
                bucket.Apps.Add(id, app);
            }
            app.Name = name;
            app.Seconds += accepted;
            bucket.Seconds = Math.Min(MaximumHourSeconds, bucket.Seconds + accepted);
            return true;
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string Decode(string value)
        {
            return StrictUtf8.GetString(Convert.FromBase64String(value));
        }
    }
}
