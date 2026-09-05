using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace ClipboardAtlas
{
    sealed class HistoryStore
    {
        public const long MaxAge = 48L * 60 * 60 * 1000;
        readonly string filePath;
        readonly string imageDir;

        public List<ClipEntry> Entries { get; private set; } = new List<ClipEntry>();
        public bool Paused { get; set; }
        public DockState Dock { get; private set; } = new DockState();
        public AppSettings Settings { get; private set; } = new AppSettings();

        public event Action Changed;

        public HistoryStore()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kinolincopy");
            Directory.CreateDirectory(root);
            MigrateLegacyData(root);
            imageDir = Path.Combine(root, "images");
            Directory.CreateDirectory(imageDir);
            filePath = Path.Combine(root, "clipboard-history.json");
        }

        static void MigrateLegacyData(string root)
        {
            try
            {
                var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "clipboard-atlas");
                if (!Directory.Exists(legacy)) return;
                var legacyHistory = Path.Combine(legacy, "clipboard-history.json");
                var targetHistory = Path.Combine(root, "clipboard-history.json");
                if (File.Exists(legacyHistory) && !File.Exists(targetHistory))
                    File.Copy(legacyHistory, targetHistory);
                var legacyImages = Path.Combine(legacy, "images");
                var targetImages = Path.Combine(root, "images");
                if (!Directory.Exists(legacyImages)) return;
                Directory.CreateDirectory(targetImages);
                foreach (var file in Directory.GetFiles(legacyImages))
                {
                    var dest = Path.Combine(targetImages, Path.GetFileName(file));
                    if (!File.Exists(dest)) File.Copy(file, dest);
                }
            }
            catch
            {
                // Legacy migration is best-effort.
            }
        }

        public string ImageDirectory => imageDir;

        public void Load()
        {
            try
            {
                if (!File.Exists(filePath)) return;
                using (var stream = File.OpenRead(filePath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(AppStore));
                    var store = serializer.ReadObject(stream) as AppStore;
                    if (store == null) return;
                    Entries = store.Entries ?? new List<ClipEntry>();
                    Paused = store.Paused;
                    Dock = store.Dock ?? new DockState();
                    Settings = store.Settings ?? new AppSettings();
                    if (Dock.Side != "left") Dock.Side = "right";
                    Dock.VerticalRatio = Clamp(Dock.VerticalRatio, 0, 1);
                    Dock.Expanded = Dock.Pinned;
                }
                MigrateImages();
            }
            catch
            {
                Entries = new List<ClipEntry>();
            }
            Prune();
            Coalesce();
        }

        public void Save()
        {
            var store = new AppStore
            {
                Entries = Entries,
                Paused = Paused,
                Dock = Dock,
                Settings = Settings
            };
            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
            using (var stream = File.Create(filePath))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, true, true))
            {
                var serializer = new DataContractJsonSerializer(typeof(AppStore));
                serializer.WriteObject(writer, store);
            }
        }

        public void Notify()
        {
            Changed?.Invoke();
        }

        public void Persist()
        {
            Save();
            Notify();
        }

        public ClipEntry Upsert(ClipEntry entry, string signature)
        {
            entry.StoredSignature = signature;
            var index = Entries.FindIndex(item => SignatureOf(item) == signature);
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (index >= 0)
            {
                var existing = Entries[index];
                existing.Type = entry.Type;
                existing.Title = entry.Title;
                existing.Preview = entry.Preview;
                existing.Text = entry.Text;
                existing.Html = entry.Html;
                existing.DataUrl = "";
                existing.Value = entry.Value;
                existing.Source = entry.Source;
                existing.MediaUrl = entry.MediaUrl;
                existing.ImagePath = string.IsNullOrWhiteSpace(entry.ImagePath) ? existing.ImagePath : entry.ImagePath;
                existing.Files = entry.Files;
                existing.StoredSignature = signature;
                existing.CreatedAt = now;
                Entries.RemoveAt(index);
                Entries.Insert(0, existing);
                return existing;
            }

            entry.Id = now + "-" + Guid.NewGuid().ToString("n").Substring(0, 8);
            entry.CreatedAt = now;
            entry.Locked = false;
            Entries.Insert(0, entry);
            return entry;
        }

        public void Prune()
        {
            var threshold = DateTimeOffset.Now.ToUnixTimeMilliseconds() - MaxAge;
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Entries = Entries.Where(entry =>
            {
                var keep = entry.Locked || entry.CreatedAt > threshold;
                if (keep && !string.IsNullOrWhiteSpace(entry.ImagePath)) kept.Add(entry.ImagePath);
                return keep;
            }).ToList();

            try
            {
                foreach (var file in Directory.GetFiles(imageDir))
                {
                    if (!kept.Contains(file)) File.Delete(file);
                }
            }
            catch
            {
                // Image cleanup is best-effort.
            }
        }

        public void Coalesce()
        {
            var seen = new Dictionary<string, int>();
            var next = new List<ClipEntry>();
            foreach (var entry in Entries)
            {
                var signature = SignatureOf(entry);
                if (!seen.TryGetValue(signature, out var index))
                {
                    seen[signature] = next.Count;
                    next.Add(entry);
                    continue;
                }

                var existing = next[index];
                var newer = entry.CreatedAt >= existing.CreatedAt ? entry : existing;
                var older = newer == entry ? existing : entry;
                newer.Id = older.Id;
                newer.Locked = existing.Locked || entry.Locked;
                newer.CreatedAt = Math.Max(existing.CreatedAt, entry.CreatedAt);
                next[index] = newer;
            }
            Entries = next.OrderByDescending(entry => entry.CreatedAt).ToList();
        }

        public List<ClipEntry> ClearUnlocked()
        {
            Entries = Entries.Where(entry => entry.Locked).ToList();
            Persist();
            return Entries;
        }

        public ClipEntry ToggleLock(string id)
        {
            var entry = Entries.FirstOrDefault(item => item.Id == id);
            if (entry == null) return null;
            entry.Locked = !entry.Locked;
            Persist();
            return entry;
        }

        public void TouchCopied(ClipEntry entry)
        {
            entry.CreatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            Entries = new List<ClipEntry> { entry }.Concat(Entries.Where(item => item.Id != entry.Id)).ToList();
            Persist();
        }

        public static string SignatureOf(ClipEntry entry)
        {
            if (entry == null) return "";
            if (!string.IsNullOrWhiteSpace(entry.StoredSignature)) return entry.StoredSignature;
            if (entry.Files.Count > 0)
                return "files:" + string.Join("|", entry.Files.Select(file => (file.Path ?? "").ToLowerInvariant()));
            if (entry.Type == "image")
            {
                var data = entry.ImagePath ?? entry.DataUrl ?? entry.Value ?? "";
                return "image:" + data.Length + ":" + Head(data, 160) + ":" + Tail(data, 160);
            }
            return "text:" + (entry.Text ?? entry.Value ?? entry.Preview ?? "");
        }

        public string SavePng(byte[] bytes, string preferredId)
        {
            var name = (preferredId ?? Guid.NewGuid().ToString("n")) + ".png";
            foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            var path = Path.Combine(imageDir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        void MigrateImages()
        {
            foreach (var entry in Entries)
            {
                if (entry.Type != "image") continue;
                if (!string.IsNullOrWhiteSpace(entry.ImagePath) && File.Exists(entry.ImagePath)) continue;
                var dataUrl = entry.DataUrl ?? entry.Value ?? "";
                var comma = dataUrl.IndexOf(',');
                if (comma < 0 || !dataUrl.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var bytes = Convert.FromBase64String(dataUrl.Substring(comma + 1));
                    entry.ImagePath = SavePng(bytes, entry.Id);
                    entry.DataUrl = "";
                    entry.Value = "";
                }
                catch
                {
                    // Leave the original payload if it cannot be decoded.
                }
            }
        }

        static string Head(string value, int count)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= count ? value : value.Substring(0, count);
        }

        static string Tail(string value, int count)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= count ? value : value.Substring(value.Length - count);
        }

        static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0.5;
            return Math.Min(max, Math.Max(min, value));
        }
    }

}
