using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipboardAtlas
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        readonly HistoryStore store = new HistoryStore();
        readonly ClipboardService clipboard;
        readonly DockController dock;
        readonly DispatcherTimer pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        readonly DispatcherTimer pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        readonly DispatcherTimer copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        readonly ObservableCollection<ClipEntry> visible = new ObservableCollection<ClipEntry>();
        TrayService tray;
        string query = "";
        bool privacyLocked;
        bool settingsOpen;
        bool quitting;
        uint lastSequence;
        ClipEntry copiedEntry;
        HwndSource hookSource;

        public MainWindow()
        {
            clipboard = new ClipboardService(store);
            dock = new DockController(this, store);
            InitializeComponent();
            DataContext = this;
            store.Load();
            ApplySettings(true);
            RefreshList();
            store.Changed += () => Dispatcher.Invoke(RefreshList);
            pollTimer.Tick += (_, __) => PollClipboard();
            pruneTimer.Tick += (_, __) =>
            {
                store.Prune();
                store.Coalesce();
                store.Persist();
            };
            copiedTimer.Tick += (_, __) =>
            {
                copiedTimer.Stop();
                if (copiedEntry != null) copiedEntry.IsCopied = false;
                copiedEntry = null;
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            tray = new TrayService(ActivateFromExternal, OpenSettings, RequestQuit);
        }

        public ObservableCollection<ClipEntry> VisibleEntries => visible;
        public string Query { get => query; set { query = value ?? ""; Raise(); RefreshList(); } }
        public bool PrivacyLocked { get => privacyLocked; set { privacyLocked = value; Raise(); RaiseAll(nameof(PrivacyIcon), nameof(PrivacyTip), nameof(EmptyVisible)); } }
        public bool SettingsOpen { get => settingsOpen; set { settingsOpen = value; Raise(); } }
        public bool Paused => store.Paused;
        public bool Pinned => store.Dock.Pinned;
        public string DockSide => store.Dock.Side == "left" ? "left" : "right";
        public string CountText => visible.Count + "条 · 2天";
        public bool EmptyVisible => !privacyLocked && visible.Count == 0;
        public string PauseIcon => store.Paused ? "\uE768" : "\uE769";
        public string PauseTip => store.Paused ? "继续记录" : "暂停记录";
        public string SideIcon => DockSide == "left" ? "\uE76C" : "\uE76B";
        public string SideTip => DockSide == "left" ? "停靠到右侧" : "停靠到左侧";
        public string PinIcon => store.Dock.Pinned ? "\uE77A" : "\uE718";
        public string PinTip => store.Dock.Pinned ? "取消固定展开" : "固定展开";
        public string PrivacyIcon => privacyLocked ? "\uE785" : "\uE72E";
        public string PrivacyTip => privacyLocked ? "显示内容" : "隐藏内容";
        public string VersionText => AppVersion.Display;

        public bool AutoStart
        {
            get => store.Settings.AutoStart;
            set
            {
                if (store.Settings.AutoStart == value) return;
                store.Settings.AutoStart = value;
                Autostart.SetEnabled(value);
                store.Save();
                Raise();
            }
        }

        public bool ShowInTaskbarSetting
        {
            get => store.Settings.ShowInTaskbar;
            set
            {
                if (store.Settings.ShowInTaskbar == value) return;
                store.Settings.ShowInTaskbar = value;
                ShowInTaskbar = value;
                store.Save();
                Raise();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public void ActivateFromExternal()
        {
            if (quitting) return;
            SettingsOpen = false;
            Show();
            if (IsIconicCompat()) NativeMethods.ShowWindow(NativeMethods.HandleOf(this), NativeMethods.SwRestore);
            dock.SetExpanded(true, true);
            Activate();
            NativeMethods.SetForegroundWindow(NativeMethods.HandleOf(this));
            RaiseDock();
        }

        void ApplySettings(bool syncAutostart)
        {
            ShowInTaskbar = store.Settings.ShowInTaskbar;
            if (syncAutostart)
            {
                if (store.Settings.AutoStart) Autostart.SetEnabled(true);
                else if (Autostart.IsEnabled()) Autostart.SetEnabled(false);
            }
            RaiseAll(nameof(AutoStart), nameof(ShowInTaskbarSetting));
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() => dock.Apply(false));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = NativeMethods.HandleOf(this);
            NativeMethods.AddClipboardFormatListener(hwnd);
            hookSource = HwndSource.FromHwnd(hwnd);
            hookSource?.AddHook(WndProc);
            lastSequence = NativeMethods.GetClipboardSequenceNumber();
            dock.Start();
            pollTimer.Start();
            pruneTimer.Start();
            clipboard.Capture(false);
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            if (!store.Dock.Pinned) dock.SetExpanded(false);
            RaiseDock();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!quitting)
            {
                e.Cancel = true;
                SettingsOpen = false;
                dock.SetExpanded(false, true);
                RaiseDock();
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            pollTimer.Stop();
            pruneTimer.Stop();
            dock.Stop();
            tray?.Dispose();
            tray = null;
            if (hookSource != null)
            {
                hookSource.RemoveHook(WndProc);
                NativeMethods.RemoveClipboardFormatListener(hookSource.Handle);
            }
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            base.OnClosed(e);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WmClipboardUpdate)
            {
                lastSequence = NativeMethods.GetClipboardSequenceNumber();
                Dispatcher.BeginInvoke(new Action(() => clipboard.Capture(true)), DispatcherPriority.Background);
            }
            return IntPtr.Zero;
        }

        void PollClipboard()
        {
            var next = NativeMethods.GetClipboardSequenceNumber();
            if (next == lastSequence) return;
            lastSequence = next;
            clipboard.Capture(true);
        }

        void RefreshList()
        {
            var needle = (query ?? "").Trim().ToLowerInvariant();
            var items = store.Entries.Where(entry => string.IsNullOrEmpty(needle) || entry.SearchText.Contains(needle)).ToList();
            visible.Clear();
            foreach (var item in items) visible.Add(item);
            RaiseAll(nameof(CountText), nameof(EmptyVisible), nameof(Paused), nameof(PauseIcon), nameof(PauseTip), nameof(Pinned), nameof(PinIcon), nameof(PinTip), nameof(DockSide), nameof(SideIcon), nameof(SideTip));
        }

        void RaiseDock()
        {
            RaiseAll(nameof(DockSide), nameof(SideIcon), nameof(SideTip), nameof(Pinned), nameof(PinIcon), nameof(PinTip));
        }

        void TopbarDrag(object sender, MouseButtonEventArgs e)
        {
            if (FindParent<Button>(e.OriginalSource as DependencyObject) != null) return;
            if (e.ChangedButton != MouseButton.Left) return;
            DragMove();
            dock.RememberDrag();
            RaiseDock();
        }

        void TogglePaused(object sender, RoutedEventArgs e)
        {
            store.Paused = !store.Paused;
            store.Save();
            RaiseAll(nameof(Paused), nameof(PauseIcon), nameof(PauseTip));
        }

        void ClearEntries(object sender, RoutedEventArgs e)
        {
            if (!store.Entries.Any(entry => !entry.Locked)) return;
            if (MessageBox.Show(this, "清空未锁定的复制记录？已锁定的记录会保留。", "kinolincopy", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            store.ClearUnlocked();
        }

        void SwitchSide(object sender, RoutedEventArgs e)
        {
            dock.SetSide(DockSide == "left" ? "right" : "left");
            RaiseDock();
        }

        void TogglePinned(object sender, RoutedEventArgs e)
        {
            dock.SetPinned(!store.Dock.Pinned);
            RaiseDock();
        }

        void TogglePrivacy(object sender, RoutedEventArgs e)
        {
            PrivacyLocked = !PrivacyLocked;
        }

        void ToggleSettings(object sender, RoutedEventArgs e)
        {
            SettingsOpen = !SettingsOpen;
            if (SettingsOpen) dock.SetExpanded(true, true);
        }

        void OpenSettings()
        {
            ActivateFromExternal();
            SettingsOpen = true;
        }

        void CloseSettings(object sender, RoutedEventArgs e)
        {
            SettingsOpen = false;
        }

        void CloseSettingsBackdrop(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender) SettingsOpen = false;
        }

        void SettingsCardClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        void Collapse(object sender, RoutedEventArgs e)
        {
            SettingsOpen = false;
            dock.SetExpanded(false, true);
            RaiseDock();
        }

        void Quit(object sender, RoutedEventArgs e)
        {
            RequestQuit();
        }

        void RequestQuit()
        {
            quitting = true;
            SettingsOpen = false;
            tray?.Dispose();
            tray = null;
            dock.Stop();
            Application.Current.Shutdown();
        }

        void CardEnter(object sender, MouseEventArgs e)
        {
            var entry = EntryFrom(sender);
            if (entry == null) return;
            foreach (var item in store.Entries) item.IsSelected = item == entry;
        }

        void CardCopy(object sender, MouseButtonEventArgs e)
        {
            if (FindParent<Button>(e.OriginalSource as DependencyObject) != null) return;
            if (FindParent<MediaElement>(e.OriginalSource as DependencyObject) != null) return;
            CopyEntry(EntryFrom(sender));
        }

        void ToggleMenu(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var entry = EntryFrom(sender);
            if (entry == null) return;
            var open = !entry.IsMenuOpen;
            foreach (var item in store.Entries) item.IsMenuOpen = false;
            entry.IsMenuOpen = open;
        }

        void ToggleLock(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var entry = EntryFrom(sender);
            if (entry == null) return;
            entry.IsMenuOpen = false;
            store.ToggleLock(entry.Id);
        }

        void MenuCopy(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            CopyEntry(EntryFrom(sender));
        }

        void MenuOpen(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var entry = EntryFrom(sender);
            entry.IsMenuOpen = false;
            var path = entry?.Files.FirstOrDefault()?.Path;
            if (!string.IsNullOrWhiteSpace(path)) OpenPath(path);
        }

        void OpenFile(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            OpenPath((sender as FrameworkElement)?.Tag as string);
        }

        void VideoOpened(object sender, RoutedEventArgs e)
        {
            var media = sender as MediaElement;
            if (media == null) return;
            try
            {
                media.Position = TimeSpan.FromMilliseconds(80);
                media.Pause();
                media.Tag = "pause";
            }
            catch
            {
                // Some clipboard videos are not decodable by the Windows media stack.
            }
        }

        void VideoClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var media = sender as MediaElement;
            if (media == null) return;
            try
            {
                if (Equals(media.Tag, "play"))
                {
                    media.Pause();
                    media.Tag = "pause";
                }
                else
                {
                    media.Play();
                    media.Tag = "play";
                }
            }
            catch
            {
                // Ignore playback failures and keep the timeline click from pasting.
            }
        }

        async void CopyEntry(ClipEntry entry)
        {
            if (entry == null) return;
            foreach (var item in store.Entries) item.IsMenuOpen = false;
            if (!clipboard.Write(entry)) return;
            store.TouchCopied(entry);
            if (copiedEntry != null && copiedEntry != entry) copiedEntry.IsCopied = false;
            entry.IsCopied = true;
            copiedEntry = entry;
            copiedTimer.Stop();
            copiedTimer.Start();
            SettingsOpen = false;
            dock.HideAfterCopy();
            RaiseDock();
            await Task.Delay(160);
            dock.PasteAtSavedPoint();
        }

        void OpenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }

        static ClipEntry EntryFrom(object sender)
        {
            return (sender as FrameworkElement)?.DataContext as ClipEntry;
        }

        static T FindParent<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        bool IsIconicCompat()
        {
            var hwnd = NativeMethods.HandleOf(this);
            return hwnd != IntPtr.Zero && NativeMethods.IsIconic(hwnd);
        }

        void RaiseAll(params string[] names)
        {
            foreach (var name in names)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        void Raise([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDown(e);
            if (FindParent<Button>(e.OriginalSource as DependencyObject) != null) return;
            if (FindParent<CheckBox>(e.OriginalSource as DependencyObject) != null) return;
            foreach (var item in store.Entries) item.IsMenuOpen = false;
        }
    }
}
