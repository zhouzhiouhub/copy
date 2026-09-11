using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using WpfImage = System.Windows.Controls.Image;

namespace ClipboardAtlas
{
    sealed class ScreenshotOverlay : Window
    {
        readonly BitmapSource desktop;
        readonly WpfImage backdrop;
        readonly Path dimPath;
        readonly System.Windows.Shapes.Rectangle selectionBox;
        readonly TextBlock sizeLabel;
        readonly TextBlock hintLabel;
        Point? dragStart;
        bool completed;

        ScreenshotOverlay(BitmapSource desktop)
        {
            this.desktop = desktop;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = MediaBrushes.Transparent;
            Cursor = Cursors.Cross;
            Focusable = true;
            Title = "Kinolincopy Screenshot";

            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            backdrop = new WpfImage
            {
                Source = desktop,
                Stretch = Stretch.Fill,
                Width = Width,
                Height = Height,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(backdrop, BitmapScalingMode.NearestNeighbor);

            dimPath = new Path
            {
                Fill = new SolidColorBrush(MediaColor.FromArgb(0x99, 0, 0, 0)),
                Data = BuildDim(new Rect(0, 0, 0, 0))
            };

            selectionBox = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(MediaColor.FromRgb(0x4C, 0xC2, 0xFF)),
                StrokeThickness = 1.5,
                Fill = MediaBrushes.Transparent,
                Visibility = Visibility.Collapsed
            };

            sizeLabel = new TextBlock
            {
                Background = new SolidColorBrush(MediaColor.FromArgb(0xCC, 0x18, 0x22, 0x1F)),
                Foreground = MediaBrushes.White,
                FontSize = 12,
                Padding = new Thickness(8, 4, 8, 4),
                Visibility = Visibility.Collapsed
            };

            hintLabel = new TextBlock
            {
                Text = "拖动选择截图区域 · Esc 取消",
                Foreground = MediaBrushes.White,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 36, 0, 0),
                Background = new SolidColorBrush(MediaColor.FromArgb(0xAA, 0x18, 0x22, 0x1F)),
                Padding = new Thickness(14, 8, 14, 8)
            };

            var root = new Grid { Width = Width, Height = Height };
            var canvas = new Canvas { Width = Width, Height = Height };
            canvas.Children.Add(backdrop);
            canvas.Children.Add(dimPath);
            canvas.Children.Add(selectionBox);
            canvas.Children.Add(sizeLabel);
            Canvas.SetLeft(backdrop, 0);
            Canvas.SetTop(backdrop, 0);
            root.Children.Add(canvas);
            root.Children.Add(hintLabel);
            Content = root;

            // Keep pixel-perfect coverage even when WPF DIP mapping differs across monitors.
            SourceInitialized += (_, __) =>
            {
                var hwnd = NativeMethods.HandleOf(this);
                if (hwnd == IntPtr.Zero) return;
                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HwndTopmost,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    0);
            };

            Loaded += (_, __) =>
            {
                Activate();
                Focus();
                Keyboard.Focus(this);
            };
            PreviewKeyDown += OnPreviewKeyDown;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseLeftButtonUp;
            LostMouseCapture += (_, __) =>
            {
                if (!completed && dragStart.HasValue) Cancel();
            };
        }

        public BitmapSource Result { get; private set; }

        public static BitmapSource CaptureRegion()
        {
            if (!TryCaptureDesktop(out var desktop)) return null;
            var overlay = new ScreenshotOverlay(desktop);
            overlay.ShowDialog();
            return overlay.Result;
        }

        static bool TryCaptureDesktop(out BitmapSource desktop)
        {
            desktop = null;
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (bounds.Width < 2 || bounds.Height < 2) return false;
            try
            {
                using (var bmp = new Bitmap(bounds.Width, bounds.Height, DrawingPixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(bmp))
                        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
                    desktop = ToBitmapSource(bmp);
                    desktop.Freeze();
                }
                return desktop != null;
            }
            catch
            {
                return false;
            }
        }

        static BitmapSource ToBitmapSource(Bitmap bitmap)
        {
            var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
            try
            {
                return BitmapSource.Create(
                    data.Width,
                    data.Height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    data.Scan0,
                    data.Stride * data.Height,
                    data.Stride);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        Geometry BuildDim(Rect hole)
        {
            var outer = new RectangleGeometry(new Rect(0, 0, Width, Height));
            var inner = new RectangleGeometry(hole);
            return new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        }

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || e.SystemKey == Key.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        }

        void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            dragStart = e.GetPosition(this);
            CaptureMouse();
            hintLabel.Visibility = Visibility.Collapsed;
            UpdateSelection(dragStart.Value, dragStart.Value);
            e.Handled = true;
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragStart.HasValue || e.LeftButton != MouseButtonState.Pressed) return;
            UpdateSelection(dragStart.Value, e.GetPosition(this));
        }

        void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!dragStart.HasValue) return;
            var end = e.GetPosition(this);
            ReleaseMouseCapture();
            var rect = Normalize(dragStart.Value, end);
            dragStart = null;
            if (rect.Width < 3 || rect.Height < 3)
            {
                Cancel();
                return;
            }
            Complete(rect);
            e.Handled = true;
        }

        void UpdateSelection(Point a, Point b)
        {
            var rect = Normalize(a, b);
            dimPath.Data = BuildDim(rect);
            selectionBox.Visibility = Visibility.Visible;
            Canvas.SetLeft(selectionBox, rect.Left);
            Canvas.SetTop(selectionBox, rect.Top);
            selectionBox.Width = Math.Max(0, rect.Width);
            selectionBox.Height = Math.Max(0, rect.Height);

            var scaleX = desktop.PixelWidth / Math.Max(1.0, ActualWidth > 1 ? ActualWidth : Width);
            var scaleY = desktop.PixelHeight / Math.Max(1.0, ActualHeight > 1 ? ActualHeight : Height);
            var pxW = Math.Max(1, (int)Math.Round(rect.Width * scaleX));
            var pxH = Math.Max(1, (int)Math.Round(rect.Height * scaleY));
            sizeLabel.Text = pxW + " × " + pxH;
            sizeLabel.Visibility = Visibility.Visible;
            var labelX = rect.Left;
            var labelY = rect.Top - 28;
            if (labelY < 8) labelY = rect.Top + 8;
            var maxX = (ActualWidth > 1 ? ActualWidth : Width) - 98;
            if (labelX > maxX) labelX = Math.Max(8, maxX);
            Canvas.SetLeft(sizeLabel, labelX);
            Canvas.SetTop(sizeLabel, labelY);
        }

        void Complete(Rect rect)
        {
            if (completed) return;
            completed = true;
            try
            {
                var scaleX = desktop.PixelWidth / Math.Max(1.0, ActualWidth > 1 ? ActualWidth : Width);
                var scaleY = desktop.PixelHeight / Math.Max(1.0, ActualHeight > 1 ? ActualHeight : Height);
                var x = Clamp((int)Math.Round(rect.X * scaleX), 0, desktop.PixelWidth - 1);
                var y = Clamp((int)Math.Round(rect.Y * scaleY), 0, desktop.PixelHeight - 1);
                var w = Clamp((int)Math.Round(rect.Width * scaleX), 1, desktop.PixelWidth - x);
                var h = Clamp((int)Math.Round(rect.Height * scaleY), 1, desktop.PixelHeight - y);
                Result = new CroppedBitmap(desktop, new Int32Rect(x, y, w, h));
                Result.Freeze();
            }
            catch
            {
                Result = null;
            }
            Close();
        }

        void Cancel()
        {
            if (completed) return;
            completed = true;
            Result = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
            Close();
        }

        static Rect Normalize(Point a, Point b)
        {
            return new Rect(
                Math.Min(a.X, b.X),
                Math.Min(a.Y, b.Y),
                Math.Abs(a.X - b.X),
                Math.Abs(a.Y - b.Y));
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
