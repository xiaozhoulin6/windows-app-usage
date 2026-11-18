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
        private readonly Dictionary<DateTime, Dictionary<string, AppTotal>> entries =
            new Dictionary<DateTime, Dictionary<string, AppTotal>>();
        private readonly string path;
        private bool dirty;
        internal bool NeedsSave { get { return dirty; } }
        internal string LoadError { get; private set; }

        internal UsageLog(string directory)
        {
            path = Path.Combine(directory, "usage.tsv");
            if (!File.Exists(path) && !File.Exists(path + ".bak"))
                return;

            if (!TryLoad(path))
            {
                entries.Clear();
                if (!TryLoad(path + ".bak"))
                {
                    entries.Clear();
                    LoadError = "无法读取使用记录";
                }
            }
        }

        internal void Add(DateTime startUtc, DateTime endUtc, ApplicationInfo app)
        {
            while (startUtc < endUtc)
            {
                DateTime local = startUtc.ToLocalTime();
                DateTime hour = DateTime.SpecifyKind(local.Date.AddHours(local.Hour), DateTimeKind.Unspecified);
                DateTime next = startUtc.AddTicks(TimeSpan.TicksPerHour - (local.Ticks % TimeSpan.TicksPerHour));
                DateTime end = next < endUtc ? next : endUtc;
                AddHour(hour, app.Id, app.Name, (end - startUtc).TotalSeconds);
                startUtc = end;
            }
            dirty = true;
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
                foreach (var row in hour.Value)
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
            if (!dirty)
                return;
            if (LoadError != null)
                throw new IOException(LoadError);

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporaryPath = path + ".tmp";
            using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(file, new UTF8Encoding(false), 4096, true))
                {
                    writer.WriteLine("app-usage-v1");
                    foreach (var hour in entries.OrderBy(x => x.Key))
                    {
                        foreach (var row in hour.Value.OrderBy(x => x.Key))
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
                File.Replace(temporaryPath, path, path + ".bak", true);
            else
                File.Move(temporaryPath, path);
            dirty = false;
        }

        private bool TryLoad(string file)
        {
            try { return ReadFile(file); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private bool ReadFile(string file)
        {
            if (!File.Exists(file))
                return false;

            using (var reader = new StreamReader(file))
            {
                if (reader.ReadLine() != "app-usage-v1")
                    return false;

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
                        double.IsInfinity(seconds) || double.IsNaN(seconds) || seconds <= 0)
                        return false;

                    try
                    {
                        AddHour(hour, Decode(columns[1]), Decode(columns[2]), seconds);
                    }
                    catch (FormatException)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private void AddHour(DateTime hour, string id, string name, double seconds)
        {
            Dictionary<string, AppTotal> apps;
            if (!entries.TryGetValue(hour, out apps))
            {
                apps = new Dictionary<string, AppTotal>(StringComparer.Ordinal);
                entries.Add(hour, apps);
            }
            AppTotal app;
            if (!apps.TryGetValue(id, out app))
            {
                app = new AppTotal();
                apps.Add(id, app);
            }
            app.Name = name;
            app.Seconds += seconds;
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
    }
}
