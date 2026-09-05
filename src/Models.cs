using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace ClipboardAtlas
{
    [DataContract]
    public sealed class AppStore
    {
        [DataMember(Name = "entries")]
        public List<ClipEntry> Entries { get; set; } = new List<ClipEntry>();

        [DataMember(Name = "paused")]
        public bool Paused { get; set; }

        [DataMember(Name = "dock")]
        public DockState Dock { get; set; } = new DockState();
    }

    [DataContract]
    public sealed class DockState
    {
        [DataMember(Name = "side")]
        public string Side { get; set; } = "right";

        [DataMember(Name = "pinned")]
        public bool Pinned { get; set; }

        [DataMember(Name = "expanded")]
        public bool Expanded { get; set; } = true;

        [DataMember(Name = "verticalRatio")]
        public double VerticalRatio { get; set; } = 0.5;
    }

    [DataContract]
    public sealed class FileRecord
    {
        [DataMember(Name = "path")]
        public string Path { get; set; } = "";

        [DataMember(Name = "name")]
        public string Name { get; set; } = "";

        [DataMember(Name = "extension")]
        public string Extension { get; set; } = "";

        [DataMember(Name = "kind")]
        public string Kind { get; set; } = "file";

        [DataMember(Name = "size")]
        public long Size { get; set; }

        [DataMember(Name = "mediaUrl")]
        public string MediaUrl { get; set; } = "";
    }

    [DataContract]
    public sealed class ClipEntry : INotifyPropertyChanged
    {
        string id = "";
        string type = "text";
        string title = "";
        string preview = "";
        string text = "";
        string html = "";
        string dataUrl = "";
        string value = "";
        string source = "";
        string mediaUrl = "";
        string imagePath = "";
        string signature = "";
        long createdAt;
        bool locked;
        bool menuOpen;
        bool copied;
        bool selected;
        List<FileRecord> files = new List<FileRecord>();

        [DataMember(Name = "id")]
        public string Id { get => id; set => Set(ref id, value); }

        [DataMember(Name = "type")]
        public string Type { get => type; set { Set(ref type, value); Raise(nameof(TypeLabel), nameof(TypeClass), nameof(IsText), nameof(IsImage), nameof(IsVideo), nameof(ShowTitle), nameof(MediaPath), nameof(MediaUri), nameof(ShowPlaceholder)); } }

        [DataMember(Name = "title")]
        public string Title { get => title; set { Set(ref title, value); Raise(nameof(ShowTitle)); } }

        [DataMember(Name = "preview")]
        public string Preview { get => preview; set => Set(ref preview, value); }

        [DataMember(Name = "text")]
        public string Text { get => text; set => Set(ref text, value); }

        [DataMember(Name = "html")]
        public string Html { get => html; set => Set(ref html, value); }

        [DataMember(Name = "dataUrl")]
        public string DataUrl { get => dataUrl; set { Set(ref dataUrl, value); Raise(nameof(MediaPath), nameof(MediaUri), nameof(ShowPlaceholder)); } }

        [DataMember(Name = "value")]
        public string Value { get => value; set => Set(ref this.value, value); }

        [DataMember(Name = "source")]
        public string Source { get => source; set => Set(ref source, value); }

        [DataMember(Name = "mediaUrl")]
        public string MediaUrl { get => mediaUrl; set { Set(ref mediaUrl, value); Raise(nameof(MediaPath), nameof(MediaUri), nameof(ShowPlaceholder)); } }

        [DataMember(Name = "imagePath")]
        public string ImagePath { get => imagePath; set { Set(ref imagePath, value); Raise(nameof(MediaPath), nameof(MediaUri), nameof(ShowPlaceholder)); } }

        [DataMember(Name = "signature")]
        public string StoredSignature { get => signature; set => Set(ref signature, value); }

        [DataMember(Name = "createdAt")]
        public long CreatedAt { get => createdAt; set { Set(ref createdAt, value); Raise(nameof(DateText), nameof(ClockText)); } }

        [DataMember(Name = "locked")]
        public bool Locked { get => locked; set => Set(ref locked, value); }

        [DataMember(Name = "files")]
        public List<FileRecord> Files { get => files ?? (files = new List<FileRecord>()); set { files = value ?? new List<FileRecord>(); Raise(nameof(Files), nameof(ShowTitle), nameof(MediaPath), nameof(MediaUri), nameof(HasFiles), nameof(ShowPlaceholder)); } }

        public bool IsMenuOpen { get => menuOpen; set => Set(ref menuOpen, value); }
        public bool IsCopied { get => copied; set => Set(ref copied, value); }
        public bool IsSelected { get => selected; set => Set(ref selected, value); }

        public bool IsText => Type == "text";
        public bool IsImage => Type == "image";
        public bool IsVideo => Type == "video";
        public bool HasFiles => Files.Count > 0;
        public bool ShowPlaceholder => !IsText && string.IsNullOrWhiteSpace(MediaPath);
        public bool ShowTitle => Type != "text" && !(Type == "image" && Files.Count == 0) && !string.IsNullOrWhiteSpace(Title);
        public string TypeLabel => Type == "image" ? "图片" : Type == "video" ? "视频" : Type == "file" ? "文件" : "文本";
        public string TypeClass => Type == "image" || Type == "video" || Type == "file" ? Type : "text";
        public string DateText => FormatDate(CreatedAt);
        public string ClockText => FormatClock(CreatedAt);
        public string CopyLabel => IsCopied ? "已粘贴" : "粘贴并隐藏";
        public string LockLabel => Locked ? "取消锁定" : "锁定保留";

        public string MediaPath
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ImagePath) && File.Exists(ImagePath)) return ImagePath;
                var media = Files.FirstOrDefault(file => file.Kind == "image" || file.Kind == "video");
                if (media != null && !string.IsNullOrWhiteSpace(media.Path) && File.Exists(media.Path)) return media.Path;
                return "";
            }
        }

        public Uri MediaUri
        {
            get
            {
                var path = MediaPath;
                if (string.IsNullOrWhiteSpace(path)) return null;
                try { return new Uri(path); }
                catch { return null; }
            }
        }

        public string SearchText
        {
            get
            {
                var names = string.Join(" ", Files.Select(file => file.Name + " " + file.Path));
                return ((Title ?? "") + " " + (Preview ?? "") + " " + (Text ?? "") + " " + (Source ?? "") + " " + names).ToLowerInvariant();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name == nameof(IsCopied)) Raise(nameof(CopyLabel));
            if (name == nameof(Locked)) Raise(nameof(LockLabel));
            return true;
        }

        void Raise(params string[] names)
        {
            foreach (var name in names)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public static string FormatClock(long timestamp)
        {
            var time = FromUnix(timestamp);
            return time.ToString("HH:mm:ss");
        }

        public static string FormatDate(long timestamp)
        {
            var time = FromUnix(timestamp);
            return time.ToString("MM/dd");
        }

        public static DateTime FromUnix(long timestamp)
        {
            if (timestamp <= 0) return DateTime.Now;
            if (timestamp > 9999999999) return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime;
            return DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime;
        }
    }

    public static class FileKinds
    {
        static readonly HashSet<string> Images = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".apng", ".avif", ".bmp", ".gif", ".heic", ".heif", ".jpeg", ".jpg", ".png", ".svg", ".tif", ".tiff", ".webp"
        };

        static readonly HashSet<string> Videos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".3g2", ".3gp", ".avi", ".flv", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".mts", ".ogv", ".ts", ".webm", ".wmv"
        };

        public static string KindOf(string path)
        {
            var ext = System.IO.Path.GetExtension(path);
            if (Videos.Contains(ext)) return "video";
            if (Images.Contains(ext)) return "image";
            try
            {
                if (Directory.Exists(path)) return "folder";
            }
            catch
            {
                // Ignore invalid paths from the clipboard.
            }
            return "file";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            var index = 0;
            while (value >= 1024 && index < units.Length - 1)
            {
                value /= 1024;
                index++;
            }
            return (value >= 10 || index == 0 ? value.ToString("0") : value.ToString("0.0")) + " " + units[index];
        }
    }
}
