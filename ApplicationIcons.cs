using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppUsage
{
    internal sealed class ApplicationIcons
    {
        private readonly string directory;
        private readonly Dictionary<string, ImageSource> images = new Dictionary<string, ImageSource>(StringComparer.Ordinal);
        private readonly HashSet<string> attempted = new HashSet<string>(StringComparer.Ordinal);

        internal ApplicationIcons(string dataDirectory)
        {
            directory = Path.Combine(dataDirectory, "icons");
        }

        internal ImageSource Get(string id)
        {
            ImageSource image;
            if (images.TryGetValue(id, out image))
                return image;

            try
            {
                string file = CacheFile(id);
                if (File.Exists(file))
                {
                    using (var stream = File.OpenRead(file))
                        image = ReadBitmap(stream);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (NotSupportedException) { }
            catch (FormatException) { }
            catch (ArgumentException) { }
            catch (ExternalException) { }

            images.Add(id, image);
            return image;
        }

        internal bool Remember(string id, string executable)
        {
            if (executable == null || Get(id) != null || !attempted.Add(id))
                return false;

            try
            {
                using (Icon icon = Icon.ExtractAssociatedIcon(executable))
                {
                    if (icon == null)
                        return false;
                    using (var bitmap = icon.ToBitmap())
                    using (var stream = new MemoryStream())
                    {
                        bitmap.Save(stream, ImageFormat.Png);
                        stream.Position = 0;
                        images[id] = ReadBitmap(stream);
                        Save(id, stream);
                    }
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (Win32Exception) { return false; }
            catch (ArgumentException) { return false; }
            catch (ExternalException) { return false; }
        }

        internal static List<KeyValuePair<string, string>> FindRunning(HashSet<string> missing)
        {
            var matches = new List<KeyValuePair<string, string>>();
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch (Win32Exception) { return matches; }

            bool matchNames = missing.Any(id => id.StartsWith("name:", StringComparison.Ordinal));
            try
            {
                foreach (Process process in processes)
                {
                    if (missing.Count == 0)
                        break;

                    string path = ForegroundApplication.GetImagePath((uint)process.Id);
                    if (path == null)
                        continue;

                    string id = ForegroundApplication.Identify(path, null);
                    if (missing.Remove(id))
                        matches.Add(new KeyValuePair<string, string>(id, path));
                    if (matchNames)
                    {
                        string name = Path.GetFileNameWithoutExtension(path);
                        id = ForegroundApplication.Identify(null, name);
                        if (missing.Remove(id))
                            matches.Add(new KeyValuePair<string, string>(id, path));
                    }
                }
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
            return matches;
        }

        private string CacheFile(string id)
        {
            return Path.Combine(directory, ForegroundApplication.Hash(id) + ".png");
        }

        private void Save(string id, Stream stream)
        {
            try
            {
                Directory.CreateDirectory(directory);
                stream.Position = 0;
                using (var file = new FileStream(CacheFile(id), FileMode.Create, FileAccess.Write, FileShare.None))
                    stream.CopyTo(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static BitmapImage ReadBitmap(Stream stream)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 48;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }

}
