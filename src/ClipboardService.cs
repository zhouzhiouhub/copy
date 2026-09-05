using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ClipboardAtlas
{
    sealed class ClipboardService
    {
        readonly HistoryStore store;
        string lastSignature = "";
        long suppressUntil;

        public ClipboardService(HistoryStore store)
        {
            this.store = store;
        }

        public void Suppress(int milliseconds = 1600)
        {
            suppressUntil = Environment.TickCount + milliseconds;
        }

        public void Capture(bool fromSequenceChange)
        {
            if (store.Paused) return;
            if (unchecked(Environment.TickCount - suppressUntil) < 0) return;

            var snapshot = ReadSnapshot();
            var entry = BuildEntry(snapshot, out var signature);
            if (entry == null) return;
            if (!fromSequenceChange && signature == lastSignature) return;
            lastSignature = signature;
            store.Upsert(entry, signature);
            store.Prune();
            store.Persist();
        }

        public bool Write(ClipEntry entry)
        {
            if (entry == null) return false;
            Suppress();
            lastSignature = HistoryStore.SignatureOf(entry);
            var copied = false;
            if (entry.Files.Count > 0) copied = WriteFiles(entry.Files.Select(file => file.Path).ToList());
            else if (entry.Type == "image") copied = WriteImage(entry);
            else copied = WriteText(entry);
            return copied;
        }

        ClipSnapshot ReadSnapshot()
        {
            var snapshot = new ClipSnapshot();
            Retry(() =>
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return;

                if (data.GetDataPresent(DataFormats.UnicodeText))
                    snapshot.Text = Convert.ToString(data.GetData(DataFormats.UnicodeText)) ?? "";
                else if (data.GetDataPresent(DataFormats.Text))
                    snapshot.Text = Convert.ToString(data.GetData(DataFormats.Text)) ?? "";

                if (data.GetDataPresent(DataFormats.Html))
                    snapshot.Html = Convert.ToString(data.GetData(DataFormats.Html)) ?? "";

                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null) snapshot.Files.AddRange(files);
                }

                if (data.GetDataPresent(DataFormats.Bitmap))
                    snapshot.Image = data.GetData(DataFormats.Bitmap) as BitmapSource;
            });

            if (snapshot.Files.Count == 0)
            {
                try
                {
                    var drop = Forms.Clipboard.GetFileDropList();
                    if (drop != null)
                    {
                        foreach (string file in drop) snapshot.Files.Add(file);
                    }
                }
                catch
                {
                    // WinForms clipboard access can fail if another app still owns it.
                }
            }

            snapshot.Files.AddRange(ExtractPaths(snapshot.Text));
            if (!string.IsNullOrWhiteSpace(snapshot.Html))
            {
                foreach (var match in System.Text.RegularExpressions.Regex.Matches(snapshot.Html, @"file://[^""'<>\s)]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    snapshot.Files.Add(match.ToString());
            }
            snapshot.Files = UniqueExisting(snapshot.Files);
            return snapshot;
        }

        ClipEntry BuildEntry(ClipSnapshot snapshot, out string signature)
        {
            signature = "";
            if (snapshot.Files.Count > 0)
            {
                var files = snapshot.Files.Select(ToRecord).ToList();
                var hasVideo = files.Any(file => file.Kind == "video");
                var allImages = files.Count > 0 && files.All(file => file.Kind == "image");
                var primary = files.FirstOrDefault(file => file.Kind == "video") ?? files.FirstOrDefault(file => file.Kind == "image") ?? files[0];
                var entry = new ClipEntry
                {
                    Type = hasVideo ? "video" : allImages ? "image" : "file",
                    Title = primary?.Name ?? "文件",
                    Preview = files.Count > 1 ? (primary?.Name ?? "文件") + " 等 " + files.Count + " 个文件" : primary?.Name ?? "文件",
                    Text = string.Join("\n", snapshot.Files),
                    Value = string.Join("\n", snapshot.Files),
                    Source = "文件剪贴板",
                    Files = files,
                    MediaUrl = primary?.Path ?? ""
                };
                signature = "files:" + string.Join("|", files.Select(file => (file.Path ?? "").ToLowerInvariant()));
                return entry;
            }

            if (snapshot.Image != null)
            {
                var png = EncodePng(snapshot.Image);
                if (png != null && png.Length > 0)
                {
                    var path = store.SavePng(png, DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString());
                    var entry = new ClipEntry
                    {
                        Type = "image",
                        Title = "",
                        Preview = "图片内容",
                        ImagePath = path,
                        Source = "系统剪贴板"
                    };
                    signature = "image:" + png.Length + ":" + Head(png, 80) + ":" + Tail(png, 80);
                    return entry;
                }
            }

            var text = snapshot.Text ?? "";
            if (string.IsNullOrWhiteSpace(text)) return null;
            var textEntry = new ClipEntry
            {
                Type = "text",
                Title = "",
                Preview = text,
                Text = text,
                Html = snapshot.Html ?? "",
                Value = text,
                Source = "系统剪贴板"
            };
            signature = "text:" + text;
            return textEntry;
        }

        static FileRecord ToRecord(string path)
        {
            long size = 0;
            try
            {
                if (File.Exists(path)) size = new FileInfo(path).Length;
            }
            catch
            {
                size = 0;
            }

            return new FileRecord
            {
                Path = path,
                Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                Extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                Kind = FileKinds.KindOf(path),
                Size = size
            };
        }

        static bool WriteText(ClipEntry entry)
        {
            var text = entry.Text ?? entry.Value ?? entry.Preview ?? "";
            return Retry(() =>
            {
                var data = new DataObject();
                data.SetText(text, TextDataFormat.UnicodeText);
                if (!string.IsNullOrWhiteSpace(entry.Html))
                    data.SetText(entry.Html, TextDataFormat.Html);
                Clipboard.SetDataObject(data, true);
            });
        }

        static bool WriteImage(ClipEntry entry)
        {
            BitmapSource image = null;
            if (!string.IsNullOrWhiteSpace(entry.ImagePath) && File.Exists(entry.ImagePath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(entry.ImagePath);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            if (image == null) return false;
            return Retry(() => Clipboard.SetImage(image));
        }

        static bool WriteFiles(List<string> paths)
        {
            var existing = UniqueExisting(paths);
            if (existing.Count == 0) return false;
            return Retry(() =>
            {
                var collection = new StringCollection();
                collection.AddRange(existing.ToArray());
                Forms.Clipboard.SetFileDropList(collection);
            });
        }

        static List<string> ExtractPaths(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("#"))
                .ToList();
            if (lines.Count == 0 || lines.Count > 50) return new List<string>();
            return UniqueExisting(lines);
        }

        static List<string> UniqueExisting(IEnumerable<string> paths)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var raw in paths ?? Enumerable.Empty<string>())
            {
                var path = NormalizePath(raw);
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) continue;
                try
                {
                    if (!File.Exists(path) && !Directory.Exists(path)) continue;
                }
                catch
                {
                    continue;
                }
                result.Add(path);
            }
            return result;
        }

        static string NormalizePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return "";
            var trimmed = filePath.Trim().Trim('"', '\'');
            if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var uri = new Uri(trimmed);
                    return Uri.UnescapeDataString(uri.LocalPath);
                }
                catch
                {
                    return "";
                }
            }
            return trimmed;
        }

        static byte[] EncodePng(BitmapSource source)
        {
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    return stream.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        static string Head(byte[] bytes, int count)
        {
            return Convert.ToBase64String(bytes, 0, Math.Min(count, bytes.Length));
        }

        static string Tail(byte[] bytes, int count)
        {
            var start = Math.Max(0, bytes.Length - count);
            return Convert.ToBase64String(bytes, start, bytes.Length - start);
        }

        static bool Retry(Action action)
        {
            for (var i = 0; i < 6; i++)
            {
                try
                {
                    action();
                    return true;
                }
                catch
                {
                    System.Threading.Thread.Sleep(40);
                }
            }
            return false;
        }

        sealed class ClipSnapshot
        {
            public string Text = "";
            public string Html = "";
            public BitmapSource Image;
            public List<string> Files = new List<string>();
        }
    }
}
