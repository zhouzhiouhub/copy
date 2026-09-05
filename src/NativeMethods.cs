using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipboardAtlas
{
    static class NativeMethods
    {
        public const int GwlExstyle = -20;
        public const int WsExTransparent = 0x00000020;
        public const int WsExLayered = 0x00080000;
        public const int SwRestore = 9;
        public const int WmClipboardUpdate = 0x031D;
        public const uint KeyeventfKeyup = 2;
        public const byte VkControl = 0x11;
        public const byte VkV = 0x56;
        public const uint MonitorDefaultToNearest = 2;
        public const int MdtdEffectiveDpi = 0;
        public static readonly IntPtr HwndTopmost = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GuiThreadInfo
        {
            public int Size;
            public int Flags;
            public IntPtr HwndActive;
            public IntPtr HwndFocus;
            public IntPtr HwndCapture;
            public IntPtr HwndMenuOwner;
            public IntPtr HwndMoveSize;
            public IntPtr HwndCaret;
            public Rect Caret;
        }

        [DllImport("user32.dll")]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(Point point, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint attach, uint attachTo, bool connect);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        public static IntPtr HandleOf(Window window)
        {
            return window == null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        }

        public static IntPtr Root(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            var ancestor = GetAncestor(hwnd, 2);
            return ancestor == IntPtr.Zero ? hwnd : ancestor;
        }

        public static bool IsOurs(IntPtr hwnd, IntPtr ours)
        {
            if (hwnd == IntPtr.Zero || ours == IntPtr.Zero) return hwnd == ours;
            return hwnd == ours || Root(hwnd) == ours;
        }

        public static void SetClickThrough(Window window, bool enabled)
        {
            var hwnd = HandleOf(window);
            if (hwnd == IntPtr.Zero) return;
            var style = GetWindowLong(hwnd, GwlExstyle);
            if (enabled) style |= WsExTransparent | WsExLayered;
            else style &= ~WsExTransparent;
            SetWindowLong(hwnd, GwlExstyle, style);
        }

        public static Point CursorPoint()
        {
            GetCursorPos(out var point);
            return point;
        }

        public static MonitorArea MonitorFromCursor()
        {
            return ReadMonitor(MonitorFromPoint(CursorPoint(), MonitorDefaultToNearest));
        }

        public static MonitorArea MonitorFromHwnd(IntPtr hwnd)
        {
            return ReadMonitor(MonitorFromWindow(hwnd, MonitorDefaultToNearest));
        }

        public static MonitorArea ReadMonitor(IntPtr monitor)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            GetMonitorInfo(monitor, ref info);
            uint dpiX = 96;
            uint dpiY = 96;
            try
            {
                GetDpiForMonitor(monitor, MdtdEffectiveDpi, out dpiX, out dpiY);
            }
            catch
            {
                dpiX = 96;
                dpiY = 96;
            }
            if (dpiX == 0) dpiX = 96;
            return new MonitorArea
            {
                PixelBounds = info.Monitor,
                PixelWork = info.Work,
                Scale = dpiX / 96.0
            };
        }

        public static void ActivateAndPaste(IntPtr ours, IntPtr saved, int x, int y)
        {
            var target = Resolve(ours, saved, x, y);
            Activate(target);
            System.Threading.Thread.Sleep(40);
            keybd_event(VkControl, 0, 0, UIntPtr.Zero);
            keybd_event(VkV, 0, 0, UIntPtr.Zero);
            keybd_event(VkV, 0, KeyeventfKeyup, UIntPtr.Zero);
            keybd_event(VkControl, 0, KeyeventfKeyup, UIntPtr.Zero);
        }

        static IntPtr Resolve(IntPtr ours, IntPtr saved, int x, int y)
        {
            if (x >= 0 && y >= 0)
            {
                var hit = Root(WindowFromPoint(new Point { X = x, Y = y }));
                if (!IsOurs(hit, ours)) return hit;
            }
            if (saved != IntPtr.Zero && IsWindow(saved) && !IsOurs(saved, ours)) return Root(saved);
            var foreground = Root(GetForegroundWindow());
            return IsOurs(foreground, ours) ? IntPtr.Zero : foreground;
        }

        static void Activate(IntPtr root)
        {
            if (root == IntPtr.Zero) return;
            if (IsIconic(root)) ShowWindow(root, SwRestore);
            var foreground = GetForegroundWindow();
            var current = GetCurrentThreadId();
            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var targetThread = GetWindowThreadProcessId(root, out _);
            AttachThreadInput(current, foregroundThread, true);
            AttachThreadInput(current, targetThread, true);
            BringWindowToTop(root);
            SetForegroundWindow(root);
            var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if (GetGUIThreadInfo(targetThread, ref info) && info.HwndFocus != IntPtr.Zero)
                SetFocus(info.HwndFocus);
            AttachThreadInput(current, foregroundThread, false);
            AttachThreadInput(current, targetThread, false);
        }
    }

    sealed class MonitorArea
    {
        public NativeMethods.Rect PixelBounds;
        public NativeMethods.Rect PixelWork;
        public double Scale = 1;

        public double WorkX => PixelWork.Left / Scale;
        public double WorkY => PixelWork.Top / Scale;
        public double WorkWidth => (PixelWork.Right - PixelWork.Left) / Scale;
        public double WorkHeight => (PixelWork.Bottom - PixelWork.Top) / Scale;
        public double BoundsX => PixelBounds.Left / Scale;
        public double BoundsY => PixelBounds.Top / Scale;
        public double BoundsWidth => (PixelBounds.Right - PixelBounds.Left) / Scale;
        public double BoundsHeight => (PixelBounds.Bottom - PixelBounds.Top) / Scale;
    }
}
