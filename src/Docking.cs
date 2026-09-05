using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClipboardAtlas
{
    sealed class DockController
    {
        public const double PanelWidth = 280;
        public const double PanelHeight = 580;
        public const double EdgePeek = 6;
        public const double EdgeHit = 12;
        public const int CollapseDelay = 160;
        public const int ExpandSuppressAfterCopy = 1400;

        readonly Window window;
        readonly HistoryStore store;
        readonly DispatcherTimer edgeTimer;
        readonly DispatcherTimer collapseTimer;
        bool applying;
        bool edgeArmed = true;
        bool pointerSeenInWindow;
        long suppressExpandUntil;
        IntPtr lastTargetHwnd;
        Point? pendingPastePoint;
        long pendingPasteSince;
        Point? pasteTarget;
        long lastHwndCaptureAt;

        public DockController(Window window, HistoryStore store)
        {
            this.window = window;
            this.store = store;
            edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            edgeTimer.Tick += (_, __) => TickEdge();
            collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CollapseDelay) };
            collapseTimer.Tick += (_, __) =>
            {
                collapseTimer.Stop();
                if (!store.Dock.Pinned) SetExpanded(false);
            };
        }

        public void Start()
        {
            edgeTimer.Start();
            Apply(false);
        }

        public void Stop()
        {
            edgeTimer.Stop();
            collapseTimer.Stop();
        }

        public DockState SetExpanded(bool expanded, bool force = false)
        {
            var dock = store.Dock;
            if (dock.Pinned && !expanded && !force) return dock;
            if (expanded && !dock.Expanded) CaptureForeground();
            if (expanded) pointerSeenInWindow = false;
            dock.Expanded = expanded;
            Apply();
            store.Save();
            return dock;
        }

        public DockState SetSide(string side)
        {
            store.Dock.Side = side == "left" ? "left" : "right";
            store.Dock.Expanded = true;
            Apply();
            store.Save();
            return store.Dock;
        }

        public DockState SetPinned(bool pinned)
        {
            store.Dock.Pinned = pinned;
            store.Dock.Expanded = pinned || store.Dock.Expanded;
            Apply();
            store.Save();
            return store.Dock;
        }

        public void HideAfterCopy()
        {
            suppressExpandUntil = Environment.TickCount + ExpandSuppressAfterCopy;
            edgeArmed = false;
            collapseTimer.Stop();
            SetExpanded(false, true);
        }

        public void RememberDrag()
        {
            if (applying) return;
            var hwnd = NativeMethods.HandleOf(window);
            var monitor = hwnd == IntPtr.Zero ? NativeMethods.MonitorFromCursor() : NativeMethods.MonitorFromHwnd(hwnd);
            var size = PanelSize(monitor);
            var availableY = Math.Max(1, monitor.WorkHeight - size.Height);
            var ratio = Clamp((window.Top - monitor.WorkY) / availableY, 0, 1);
            var leftDistance = Math.Abs(window.Left - monitor.WorkX);
            var rightDistance = Math.Abs(monitor.WorkX + monitor.WorkWidth - (window.Left + window.Width));
            var side = leftDistance <= rightDistance ? "left" : "right";
            if (side == store.Dock.Side && Math.Abs(ratio - store.Dock.VerticalRatio) < 0.01)
            {
                Apply(false);
                return;
            }
            store.Dock.Side = side;
            store.Dock.VerticalRatio = ratio;
            store.Save();
            Apply(false);
        }

        public void PasteAtSavedPoint()
        {
            var ours = NativeMethods.HandleOf(window);
            var saved = lastTargetHwnd;
            var x = -1;
            var y = -1;
            if (pasteTarget.HasValue)
            {
                x = (int)Math.Round(pasteTarget.Value.X);
                y = (int)Math.Round(pasteTarget.Value.Y);
            }
            NativeMethods.ActivateAndPaste(ours, saved, x, y);
        }

        public void Apply(bool _animated = true)
        {
            if (window == null) return;
            applying = true;
            var dock = store.Dock;
            var monitor = CurrentMonitor(dock.Expanded);
            var size = PanelSize(monitor);
            var y = DockY(monitor, size.Height);
            var shownX = dock.Side == "left" ? monitor.WorkX : monitor.WorkX + monitor.WorkWidth - size.Width;
            var hiddenX = dock.Side == "left" ? monitor.WorkX - size.Width + EdgePeek : monitor.WorkX + monitor.WorkWidth - EdgePeek;
            window.MinWidth = window.MaxWidth = window.Width = size.Width;
            window.MinHeight = window.MaxHeight = window.Height = size.Height;
            window.Left = dock.Expanded ? shownX : hiddenX;
            window.Top = y;
            window.Topmost = true;
            NativeMethods.SetClickThrough(window, !dock.Expanded);
            window.Dispatcher.BeginInvoke(new Action(() => applying = false), DispatcherPriority.Background);
        }

        MonitorArea CurrentMonitor(bool expanded)
        {
            var hwnd = NativeMethods.HandleOf(window);
            if (hwnd != IntPtr.Zero && expanded) return NativeMethods.MonitorFromHwnd(hwnd);
            return NativeMethods.MonitorFromCursor();
        }

        static Size PanelSize(MonitorArea monitor)
        {
            return new Size(
                Math.Round(Clamp(PanelWidth, EdgePeek, Math.Max(EdgePeek, monitor.WorkWidth - EdgePeek))),
                Math.Round(Clamp(PanelHeight, 1, Math.Max(1, monitor.WorkHeight)))
            );
        }

        double DockY(MonitorArea monitor, double height)
        {
            var available = Math.Max(0, monitor.WorkHeight - height);
            return Math.Round(monitor.WorkY + available * Clamp(store.Dock.VerticalRatio, 0, 1));
        }

        void TickEdge()
        {
            var point = NativeMethods.CursorPoint();
            RememberPasteTarget(point);
            var monitor = NativeMethods.ReadMonitor(NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest));
            var dip = new Point(point.X / monitor.Scale, point.Y / monitor.Scale);
            var edge = CursorEdge(dip, monitor);

            if (edge == null) edgeArmed = true;

            if (!store.Dock.Expanded)
            {
                if (unchecked(Environment.TickCount - suppressExpandUntil) < 0 || !edgeArmed || edge == null) return;
                if (store.Dock.Side != edge)
                {
                    SetSide(edge);
                    return;
                }
                SetExpanded(true);
                return;
            }

            if (store.Dock.Pinned) return;
            if (IsPointInWindow(dip)) pointerSeenInWindow = true;
            if (!pointerSeenInWindow) return;

            var stayOpen = IsPointInWindow(dip) || edge == store.Dock.Side;
            if (stayOpen)
            {
                collapseTimer.Stop();
                return;
            }
            if (!collapseTimer.IsEnabled) collapseTimer.Start();
        }

        string CursorEdge(Point dip, MonitorArea monitor)
        {
            var height = PanelSize(monitor).Height;
            var triggerY = DockY(monitor, height);
            if (dip.Y < triggerY || dip.Y > triggerY + height) return null;
            if (dip.X <= monitor.BoundsX + EdgeHit) return "left";
            if (dip.X >= monitor.BoundsX + monitor.BoundsWidth - EdgeHit) return "right";
            return null;
        }

        bool IsPointInWindow(Point dip)
        {
            return dip.X >= window.Left && dip.X <= window.Left + window.Width &&
                   dip.Y >= window.Top && dip.Y <= window.Top + window.Height;
        }

        void RememberPasteTarget(NativeMethods.Point point)
        {
            var monitor = NativeMethods.ReadMonitor(NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest));
            var dip = new Point(point.X / monitor.Scale, point.Y / monitor.Scale);
            if (CursorEdge(dip, monitor) != null) return;
            if (store.Dock.Expanded && IsPointInWindow(dip)) return;

            if (pendingPastePoint == null ||
                Math.Abs(pendingPastePoint.Value.X - point.X) >= 28 ||
                Math.Abs(pendingPastePoint.Value.Y - point.Y) >= 28)
            {
                pendingPastePoint = new Point(point.X, point.Y);
                pendingPasteSince = Environment.TickCount;
                return;
            }

            if (unchecked(Environment.TickCount - pendingPasteSince) >= 250)
            {
                pasteTarget = new Point(point.X, point.Y);
                if (unchecked(Environment.TickCount - lastHwndCaptureAt) > 400)
                {
                    lastHwndCaptureAt = Environment.TickCount;
                    CaptureForeground();
                }
            }
        }

        void CaptureForeground()
        {
            var ours = NativeMethods.HandleOf(window);
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd != IntPtr.Zero && !NativeMethods.IsOurs(hwnd, ours))
                lastTargetHwnd = NativeMethods.Root(hwnd);
        }

        static double Clamp(double value, double min, double max)
        {
            return Math.Min(max, Math.Max(min, value));
        }
    }
}
