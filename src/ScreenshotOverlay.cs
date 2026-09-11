using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using ShapeRectangle = System.Windows.Shapes.Rectangle;
using WpfImage = System.Windows.Controls.Image;

namespace ClipboardAtlas
{
    enum ShotTool
    {
        None,
        Rect,
        Ellipse,
        Arrow,
        Pen
    }

    sealed class ScreenshotOverlay : Window
    {
        const double HandleSize = 8;
        const double MinSelection = 8;
        const double ToolbarGap = 8;

        readonly BitmapSource desktop;
        readonly Canvas stage;
        readonly WpfImage backdrop;
        readonly Path dimPath;
        readonly ShapeRectangle selectionBox;
        readonly TextBlock sizeLabel;
        readonly TextBlock hintLabel;
        readonly Canvas annotateLayer;
        readonly Border toolbar;
        readonly List<FrameworkElement> handles = new List<FrameworkElement>();
        readonly List<FrameworkElement> strokes = new List<FrameworkElement>();
        readonly ToggleButton[] toolButtons;
        readonly Border[] colorDots;

        ShotTool tool = ShotTool.None;
        MediaColor strokeColor = MediaColor.FromRgb(0xE8, 0x4A, 0x3C);
        double strokeThickness = 2.5;
        bool editing;
        bool completed;
        bool creatingSelection;
        Point? dragStart;
        Rect selection;
        string resizeMode;
        Point resizeOrigin;
        Rect resizeStart;
        Point? drawStart;
        FrameworkElement draftShape;
        Polyline draftPen;

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
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            RenderOptions.SetBitmapScalingMode(backdrop, BitmapScalingMode.NearestNeighbor);

            dimPath = new Path
            {
                Fill = new SolidColorBrush(MediaColor.FromArgb(0x99, 0, 0, 0)),
                Data = BuildDim(Rect.Empty),
                IsHitTestVisible = false
            };

            selectionBox = new ShapeRectangle
            {
                Stroke = new SolidColorBrush(MediaColor.FromRgb(0x4C, 0xC2, 0xFF)),
                StrokeThickness = 1.5,
                Fill = MediaBrushes.Transparent,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };

            annotateLayer = new Canvas
            {
                ClipToBounds = true,
                Visibility = Visibility.Collapsed,
                Background = MediaBrushes.Transparent
            };

            sizeLabel = new TextBlock
            {
                Background = new SolidColorBrush(MediaColor.FromArgb(0xCC, 0x18, 0x22, 0x1F)),
                Foreground = MediaBrushes.White,
                FontSize = 12,
                Padding = new Thickness(8, 4, 8, 4),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };

            hintLabel = new TextBlock
            {
                Text = "拖动选择区域 · 松手后可编辑标注 · Esc 取消",
                Foreground = MediaBrushes.White,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 36, 0, 0),
                Background = new SolidColorBrush(MediaColor.FromArgb(0xAA, 0x18, 0x22, 0x1F)),
                Padding = new Thickness(14, 8, 14, 8),
                IsHitTestVisible = false
            };

            toolbar = BuildToolbar(out toolButtons, out colorDots);
            toolbar.Visibility = Visibility.Collapsed;

            stage = new Canvas { Width = Width, Height = Height };
            stage.Children.Add(backdrop);
            stage.Children.Add(dimPath);
            stage.Children.Add(selectionBox);
            stage.Children.Add(annotateLayer);
            stage.Children.Add(sizeLabel);
            stage.Children.Add(toolbar);
            CreateHandles();

            var root = new Grid { Width = Width, Height = Height };
            root.Children.Add(stage);
            root.Children.Add(hintLabel);
            Content = root;

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
            PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            PreviewMouseMove += OnPreviewMouseMove;
            PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
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

        Border BuildToolbar(out ToggleButton[] tools, out Border[] colors)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            tools = new[]
            {
                MakeToolButton("\uEA86", ShotTool.Rect, "矩形"),
                MakeToolButton("\uEA3B", ShotTool.Ellipse, "椭圆"),
                MakeToolButton("\uE72A", ShotTool.Arrow, "箭头"),
                MakeToolButton("\uE70F", ShotTool.Pen, "画笔")
            };
            foreach (var button in tools) panel.Children.Add(button);

            panel.Children.Add(MakeSeparator());

            var palette = new[]
            {
                MediaColor.FromRgb(0xE8, 0x4A, 0x3C),
                MediaColor.FromRgb(0xF5, 0xC5, 0x42),
                MediaColor.FromRgb(0x3D, 0xC4, 0x7E),
                MediaColor.FromRgb(0x4C, 0xC2, 0xFF),
                MediaColor.FromRgb(0xFF, 0xFF, 0xFF),
                MediaColor.FromRgb(0x18, 0x22, 0x1F)
            };
            colors = new Border[palette.Length];
            for (var i = 0; i < palette.Length; i++)
            {
                var color = palette[i];
                var dot = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(color),
                    BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xD0, 0xD0, 0xD0)),
                    BorderThickness = new Thickness(color == strokeColor ? 2 : 1),
                    Margin = new Thickness(3, 0, 3, 0),
                    Cursor = Cursors.Hand,
                    Tag = color,
                    VerticalAlignment = VerticalAlignment.Center
                };
                dot.MouseLeftButtonDown += (_, e) =>
                {
                    strokeColor = (MediaColor)dot.Tag;
                    RefreshColorDots();
                    e.Handled = true;
                };
                ToolTipService.SetToolTip(dot, ColorTip(color));
                ToolTipService.SetInitialShowDelay(dot, 200);
                ToolTipService.SetShowDuration(dot, 4000);
                colors[i] = dot;
                panel.Children.Add(dot);
            }

            panel.Children.Add(MakeSeparator());
            panel.Children.Add(MakeActionButton("\uE7A7", "撤销", UndoStroke, false));
            panel.Children.Add(MakeActionButton("\uE711", "取消", Cancel, false));
            panel.Children.Add(MakeActionButton("\uE73E", "完成", Confirm, true));

            return new Border
            {
                Tag = "toolbar",
                Background = new SolidColorBrush(MediaColor.FromRgb(0xF7, 0xF8, 0xF5)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xDC, 0xE2, 0xDB)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 8, 6),
                Child = panel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 2,
                    Opacity = 0.22
                }
            };
        }

        static readonly System.Windows.Media.FontFamily IconFont = new System.Windows.Media.FontFamily("Segoe MDL2 Assets");

        ToggleButton MakeToolButton(string glyph, ShotTool value, string tip)
        {
            var button = new ToggleButton
            {
                Content = new TextBlock
                {
                    Text = glyph,
                    FontFamily = IconFont,
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Width = 30,
                Height = 28,
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = tip,
                Tag = value,
                Cursor = Cursors.Hand,
                Background = MediaBrushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            ToolTipService.SetInitialShowDelay(button, 200);
            ToolTipService.SetShowDuration(button, 4000);
            button.Checked += (_, __) =>
            {
                foreach (var other in toolButtons)
                {
                    if (!ReferenceEquals(other, button)) other.IsChecked = false;
                }
                tool = value;
                Cursor = value == ShotTool.None ? Cursors.Arrow : Cursors.Cross;
            };
            button.Unchecked += (_, __) =>
            {
                if (toolButtons.All(item => item.IsChecked != true))
                {
                    tool = ShotTool.None;
                    Cursor = Cursors.SizeAll;
                }
            };
            return button;
        }

        Button MakeActionButton(string glyph, string tip, Action action, bool primary)
        {
            var foreground = primary
                ? new SolidColorBrush(MediaColor.FromRgb(0xFF, 0xF9, 0xE8))
                : new SolidColorBrush(MediaColor.FromRgb(0x18, 0x22, 0x1F));
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = glyph,
                    FontFamily = IconFont,
                    FontSize = 14,
                    Foreground = foreground,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Width = 30,
                Height = 28,
                Margin = new Thickness(2, 0, 2, 0),
                Padding = new Thickness(0),
                ToolTip = tip,
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(0),
                Background = primary
                    ? new SolidColorBrush(MediaColor.FromRgb(0x23, 0x33, 0x2D))
                    : MediaBrushes.Transparent,
                Foreground = foreground
            };
            ToolTipService.SetInitialShowDelay(button, 200);
            ToolTipService.SetShowDuration(button, 4000);
            button.Click += (_, e) =>
            {
                action();
                e.Handled = true;
            };
            return button;
        }

        static string ColorTip(MediaColor color)
        {
            if (color.R == 0xE8 && color.G == 0x4A) return "红色";
            if (color.R == 0xF5 && color.G == 0xC5) return "黄色";
            if (color.R == 0x3D && color.G == 0xC4) return "绿色";
            if (color.R == 0x4C && color.G == 0xC2) return "蓝色";
            if (color.R == 0xFF && color.G == 0xFF) return "白色";
            return "黑色";
        }

        static Border MakeSeparator()
        {
            return new Border
            {
                Width = 1,
                Height = 18,
                Background = new SolidColorBrush(MediaColor.FromRgb(0xDC, 0xE2, 0xDB)),
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        void CreateHandles()
        {
            string[] modes = { "nw", "n", "ne", "e", "se", "s", "sw", "w" };
            foreach (var mode in modes)
            {
                var handle = new ShapeRectangle
                {
                    Width = HandleSize,
                    Height = HandleSize,
                    Fill = MediaBrushes.White,
                    Stroke = new SolidColorBrush(MediaColor.FromRgb(0x4C, 0xC2, 0xFF)),
                    StrokeThickness = 1,
                    Visibility = Visibility.Collapsed,
                    Tag = mode,
                    Cursor = CursorForHandle(mode)
                };
                handle.MouseLeftButtonDown += OnHandleDown;
                handles.Add(handle);
                stage.Children.Add(handle);
            }
        }

        static Cursor CursorForHandle(string mode)
        {
            switch (mode)
            {
                case "n":
                case "s":
                    return Cursors.SizeNS;
                case "e":
                case "w":
                    return Cursors.SizeWE;
                case "nw":
                case "se":
                    return Cursors.SizeNWSE;
                default:
                    return Cursors.SizeNESW;
            }
        }

        void RefreshColorDots()
        {
            foreach (var dot in colorDots)
            {
                var color = (MediaColor)dot.Tag;
                dot.BorderThickness = new Thickness(ColorsEqual(color, strokeColor) ? 2 : 1);
                dot.BorderBrush = new SolidColorBrush(
                    ColorsEqual(color, strokeColor)
                        ? MediaColor.FromRgb(0x18, 0x22, 0x1F)
                        : MediaColor.FromRgb(0xD0, 0xD0, 0xD0));
            }
        }

        static bool ColorsEqual(MediaColor a, MediaColor b)
        {
            return a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;
        }

        Geometry BuildDim(Rect hole)
        {
            var outer = new RectangleGeometry(new Rect(0, 0, Width, Height));
            if (hole.Width <= 0 || hole.Height <= 0)
                return outer;
            return new CombinedGeometry(GeometryCombineMode.Exclude, outer, new RectangleGeometry(hole));
        }

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || e.SystemKey == Key.Escape)
            {
                e.Handled = true;
                Cancel();
                return;
            }

            if (!editing) return;

            if ((e.Key == Key.Enter || e.Key == Key.Return) && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                e.Handled = true;
                Confirm();
                return;
            }

            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                UndoStroke();
            }
        }

        void OnHandleDown(object sender, MouseButtonEventArgs e)
        {
            if (!editing || !(sender is FrameworkElement element)) return;
            resizeMode = element.Tag as string;
            resizeOrigin = e.GetPosition(this);
            resizeStart = selection;
            CaptureMouse();
            e.Handled = true;
        }

        void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (completed) return;
            if (IsFromToolbar(e.OriginalSource as DependencyObject)) return;

            var pos = e.GetPosition(this);

            if (!editing)
            {
                creatingSelection = true;
                dragStart = pos;
                CaptureMouse();
                hintLabel.Visibility = Visibility.Collapsed;
                ApplySelection(new Rect(pos, new Size(0, 0)));
                e.Handled = true;
                return;
            }

            if (!string.IsNullOrEmpty(resizeMode)) return;

            if (tool != ShotTool.None && selection.Contains(pos))
            {
                BeginDraw(pos);
                e.Handled = true;
                return;
            }

            if (selection.Contains(pos) && tool == ShotTool.None)
            {
                resizeMode = "move";
                resizeOrigin = pos;
                resizeStart = selection;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            // Click outside while editing: restart selection if not drawing.
            if (tool == ShotTool.None)
            {
                ClearAnnotations();
                editing = false;
                toolbar.Visibility = Visibility.Collapsed;
                SetHandlesVisible(false);
                annotateLayer.Visibility = Visibility.Collapsed;
                creatingSelection = true;
                dragStart = pos;
                CaptureMouse();
                Cursor = Cursors.Cross;
                ApplySelection(new Rect(pos, new Size(0, 0)));
                e.Handled = true;
            }
        }

        void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (completed) return;
            var pos = e.GetPosition(this);

            if (creatingSelection && dragStart.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                ApplySelection(Normalize(dragStart.Value, pos));
                return;
            }

            if (!string.IsNullOrEmpty(resizeMode) && e.LeftButton == MouseButtonState.Pressed)
            {
                ApplySelection(ResizeSelection(resizeStart, resizeOrigin, pos, resizeMode));
                return;
            }

            if (drawStart.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateDraft(pos);
                return;
            }

            if (editing && tool == ShotTool.None)
                Cursor = selection.Contains(pos) ? Cursors.SizeAll : Cursors.Arrow;
        }

        void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (completed) return;
            var pos = e.GetPosition(this);

            if (creatingSelection && dragStart.HasValue)
            {
                if (IsMouseCaptured) ReleaseMouseCapture();
                creatingSelection = false;
                dragStart = null;
                var rect = Normalize(selection.TopLeft, selection.BottomRight);
                if (rect.Width < MinSelection || rect.Height < MinSelection)
                {
                    Cancel();
                    return;
                }
                EnterEdit(rect);
                e.Handled = true;
                return;
            }

            if (!string.IsNullOrEmpty(resizeMode))
            {
                if (IsMouseCaptured) ReleaseMouseCapture();
                resizeMode = null;
                e.Handled = true;
                return;
            }

            if (drawStart.HasValue)
            {
                CommitDraft(pos);
                if (IsMouseCaptured) ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        void EnterEdit(Rect rect)
        {
            editing = true;
            ApplySelection(rect);
            annotateLayer.Visibility = Visibility.Visible;
            toolbar.Visibility = Visibility.Visible;
            SetHandlesVisible(true);
            tool = ShotTool.None;
            foreach (var button in toolButtons) button.IsChecked = false;
            Cursor = Cursors.SizeAll;
            hintLabel.Visibility = Visibility.Collapsed;
            PlaceToolbar();
        }

        void ApplySelection(Rect rect)
        {
            selection = ClampRect(rect);
            dimPath.Data = BuildDim(selection);
            selectionBox.Visibility = Visibility.Visible;
            Canvas.SetLeft(selectionBox, selection.Left);
            Canvas.SetTop(selectionBox, selection.Top);
            selectionBox.Width = Math.Max(0, selection.Width);
            selectionBox.Height = Math.Max(0, selection.Height);

            Canvas.SetLeft(annotateLayer, selection.Left);
            Canvas.SetTop(annotateLayer, selection.Top);
            annotateLayer.Width = Math.Max(0, selection.Width);
            annotateLayer.Height = Math.Max(0, selection.Height);
            annotateLayer.Clip = new RectangleGeometry(new Rect(0, 0, annotateLayer.Width, annotateLayer.Height));

            UpdateSizeLabel();
            LayoutHandles();
            if (editing) PlaceToolbar();
        }

        Rect ClampRect(Rect rect)
        {
            var maxW = ActualWidth > 1 ? ActualWidth : Width;
            var maxH = ActualHeight > 1 ? ActualHeight : Height;
            var x = Math.Max(0, Math.Min(rect.X, maxW - 1));
            var y = Math.Max(0, Math.Min(rect.Y, maxH - 1));
            var w = Math.Max(1, Math.Min(rect.Width, maxW - x));
            var h = Math.Max(1, Math.Min(rect.Height, maxH - y));
            return new Rect(x, y, w, h);
        }

        void UpdateSizeLabel()
        {
            var scale = GetPixelScale();
            var pxW = Math.Max(1, (int)Math.Round(selection.Width * scale.X));
            var pxH = Math.Max(1, (int)Math.Round(selection.Height * scale.Y));
            sizeLabel.Text = pxW + " × " + pxH;
            sizeLabel.Visibility = Visibility.Visible;
            var labelX = selection.Left;
            var labelY = selection.Top - 28;
            if (labelY < 8) labelY = selection.Top + 8;
            var maxX = (ActualWidth > 1 ? ActualWidth : Width) - 98;
            if (labelX > maxX) labelX = Math.Max(8, maxX);
            Canvas.SetLeft(sizeLabel, labelX);
            Canvas.SetTop(sizeLabel, labelY);
        }

        void LayoutHandles()
        {
            if (!editing)
            {
                SetHandlesVisible(false);
                return;
            }

            SetHandlesVisible(true);
            var map = new Dictionary<string, Point>
            {
                ["nw"] = new Point(selection.Left, selection.Top),
                ["n"] = new Point(selection.Left + selection.Width / 2, selection.Top),
                ["ne"] = new Point(selection.Right, selection.Top),
                ["e"] = new Point(selection.Right, selection.Top + selection.Height / 2),
                ["se"] = new Point(selection.Right, selection.Bottom),
                ["s"] = new Point(selection.Left + selection.Width / 2, selection.Bottom),
                ["sw"] = new Point(selection.Left, selection.Bottom),
                ["w"] = new Point(selection.Left, selection.Top + selection.Height / 2)
            };

            foreach (var handle in handles)
            {
                var mode = handle.Tag as string;
                if (mode == null || !map.ContainsKey(mode)) continue;
                var point = map[mode];
                Canvas.SetLeft(handle, point.X - HandleSize / 2);
                Canvas.SetTop(handle, point.Y - HandleSize / 2);
            }
        }

        void SetHandlesVisible(bool visible)
        {
            var state = visible ? Visibility.Visible : Visibility.Collapsed;
            foreach (var handle in handles) handle.Visibility = state;
        }

        void PlaceToolbar()
        {
            toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = toolbar.DesiredSize;
            var maxW = ActualWidth > 1 ? ActualWidth : Width;
            var maxH = ActualHeight > 1 ? ActualHeight : Height;

            var x = selection.Left;
            if (x + size.Width > maxW - 8) x = Math.Max(8, maxW - size.Width - 8);
            if (x < 8) x = 8;

            var y = selection.Bottom + ToolbarGap;
            if (y + size.Height > maxH - 8)
                y = selection.Top - size.Height - ToolbarGap;
            if (y < 8) y = Math.Max(8, Math.Min(selection.Bottom + ToolbarGap, maxH - size.Height - 8));

            Canvas.SetLeft(toolbar, x);
            Canvas.SetTop(toolbar, y);
        }

        static Rect Normalize(Point a, Point b)
        {
            return new Rect(
                Math.Min(a.X, b.X),
                Math.Min(a.Y, b.Y),
                Math.Abs(a.X - b.X),
                Math.Abs(a.Y - b.Y));
        }

        static Rect ResizeSelection(Rect start, Point origin, Point current, string mode)
        {
            var dx = current.X - origin.X;
            var dy = current.Y - origin.Y;
            var left = start.Left;
            var top = start.Top;
            var right = start.Right;
            var bottom = start.Bottom;

            if (mode == "move")
            {
                left = start.Left + dx;
                top = start.Top + dy;
                right = left + start.Width;
                bottom = top + start.Height;
            }
            else
            {
                if (mode.Contains("w")) left = start.Left + dx;
                if (mode.Contains("e")) right = start.Right + dx;
                if (mode.Contains("n")) top = start.Top + dy;
                if (mode.Contains("s")) bottom = start.Bottom + dy;
            }

            if (right - left < MinSelection)
            {
                if (mode.Contains("w")) left = right - MinSelection;
                else right = left + MinSelection;
            }
            if (bottom - top < MinSelection)
            {
                if (mode.Contains("n")) top = bottom - MinSelection;
                else bottom = top + MinSelection;
            }

            return new Rect(new Point(left, top), new Point(right, bottom));
        }

        void BeginDraw(Point screenPos)
        {
            drawStart = ToLocal(screenPos);
            CaptureMouse();
            var brush = new SolidColorBrush(strokeColor);

            switch (tool)
            {
                case ShotTool.Rect:
                    draftShape = new ShapeRectangle
                    {
                        Stroke = brush,
                        StrokeThickness = strokeThickness,
                        Fill = MediaBrushes.Transparent
                    };
                    annotateLayer.Children.Add(draftShape);
                    break;
                case ShotTool.Ellipse:
                    draftShape = new Ellipse
                    {
                        Stroke = brush,
                        StrokeThickness = strokeThickness,
                        Fill = MediaBrushes.Transparent
                    };
                    annotateLayer.Children.Add(draftShape);
                    break;
                case ShotTool.Arrow:
                    draftShape = new System.Windows.Shapes.Path
                    {
                        Stroke = brush,
                        StrokeThickness = strokeThickness,
                        Fill = brush,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    annotateLayer.Children.Add(draftShape);
                    break;
                case ShotTool.Pen:
                    draftPen = new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = strokeThickness,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round
                    };
                    draftPen.Points.Add(drawStart.Value);
                    annotateLayer.Children.Add(draftPen);
                    draftShape = draftPen;
                    break;
            }
        }

        void UpdateDraft(Point screenPos)
        {
            if (!drawStart.HasValue) return;
            var local = ToLocal(screenPos);
            var start = drawStart.Value;

            if (draftShape is ShapeRectangle || draftShape is Ellipse)
            {
                var rect = Normalize(start, local);
                Canvas.SetLeft(draftShape, rect.Left);
                Canvas.SetTop(draftShape, rect.Top);
                draftShape.Width = Math.Max(1, rect.Width);
                draftShape.Height = Math.Max(1, rect.Height);
            }
            else if (tool == ShotTool.Arrow && draftShape is System.Windows.Shapes.Path path)
            {
                path.Data = BuildArrowGeometry(start, local);
            }
            else if (draftPen != null)
            {
                draftPen.Points.Add(local);
            }
        }

        void CommitDraft(Point screenPos)
        {
            UpdateDraft(screenPos);
            if (draftShape != null)
            {
                if (draftShape is ShapeRectangle || draftShape is Ellipse)
                {
                    if (draftShape.Width < 2 || draftShape.Height < 2)
                        annotateLayer.Children.Remove(draftShape);
                    else
                        strokes.Add(draftShape);
                }
                else if (tool == ShotTool.Arrow)
                {
                    strokes.Add(draftShape);
                }
                else if (draftPen != null)
                {
                    if (draftPen.Points.Count < 2)
                        annotateLayer.Children.Remove(draftPen);
                    else
                        strokes.Add(draftPen);
                }
            }

            draftShape = null;
            draftPen = null;
            drawStart = null;
        }

        Point ToLocal(Point screenPos)
        {
            return new Point(
                Math.Max(0, Math.Min(screenPos.X - selection.Left, selection.Width)),
                Math.Max(0, Math.Min(screenPos.Y - selection.Top, selection.Height)));
        }

        static Geometry BuildArrowGeometry(Point start, Point end)
        {
            var group = new GeometryGroup();
            group.Children.Add(new LineGeometry(start, end));

            var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
            const double head = 12;
            var left = new Point(
                end.X - head * Math.Cos(angle - Math.PI / 6),
                end.Y - head * Math.Sin(angle - Math.PI / 6));
            var right = new Point(
                end.X - head * Math.Cos(angle + Math.PI / 6),
                end.Y - head * Math.Sin(angle + Math.PI / 6));
            var tip = new PathGeometry();
            var figure = new PathFigure { StartPoint = end, IsClosed = true };
            figure.Segments.Add(new LineSegment(left, true));
            figure.Segments.Add(new LineSegment(right, true));
            tip.Figures.Add(figure);
            group.Children.Add(tip);
            return group;
        }

        void UndoStroke()
        {
            if (strokes.Count == 0) return;
            var last = strokes[strokes.Count - 1];
            strokes.RemoveAt(strokes.Count - 1);
            annotateLayer.Children.Remove(last);
        }

        void ClearAnnotations()
        {
            strokes.Clear();
            annotateLayer.Children.Clear();
            draftShape = null;
            draftPen = null;
            drawStart = null;
        }

        void Confirm()
        {
            if (completed) return;
            completed = true;
            try
            {
                Result = RenderResult();
                Result?.Freeze();
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

        BitmapSource RenderResult()
        {
            var scale = GetPixelScale();
            var x = Clamp((int)Math.Round(selection.X * scale.X), 0, desktop.PixelWidth - 1);
            var y = Clamp((int)Math.Round(selection.Y * scale.Y), 0, desktop.PixelHeight - 1);
            var w = Clamp((int)Math.Round(selection.Width * scale.X), 1, desktop.PixelWidth - x);
            var h = Clamp((int)Math.Round(selection.Height * scale.Y), 1, desktop.PixelHeight - y);

            var crop = new CroppedBitmap(desktop, new Int32Rect(x, y, w, h));
            if (strokes.Count == 0) return crop;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(crop, new Rect(0, 0, w, h));
                var sx = w / Math.Max(1.0, selection.Width);
                var sy = h / Math.Max(1.0, selection.Height);
                dc.PushTransform(new ScaleTransform(sx, sy));
                foreach (var stroke in strokes)
                    DrawStroke(dc, stroke);
                dc.Pop();
            }

            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            return bitmap;
        }

        static void DrawStroke(DrawingContext dc, FrameworkElement stroke)
        {
            if (stroke is ShapeRectangle rect)
            {
                var left = Canvas.GetLeft(rect);
                var top = Canvas.GetTop(rect);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;
                dc.DrawRectangle(
                    null,
                    new System.Windows.Media.Pen(rect.Stroke, rect.StrokeThickness),
                    new Rect(left, top, rect.Width, rect.Height));
            }
            else if (stroke is Ellipse ellipse)
            {
                var left = Canvas.GetLeft(ellipse);
                var top = Canvas.GetTop(ellipse);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;
                dc.DrawEllipse(
                    null,
                    new System.Windows.Media.Pen(ellipse.Stroke, ellipse.StrokeThickness),
                    new Point(left + ellipse.Width / 2, top + ellipse.Height / 2),
                    ellipse.Width / 2,
                    ellipse.Height / 2);
            }
            else if (stroke is Polyline line && line.Points.Count > 1)
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(line.Points[0], false, false);
                    ctx.PolyLineTo(line.Points.Skip(1).ToList(), true, true);
                }
                geo.Freeze();
                var pen = new System.Windows.Media.Pen(line.Stroke, line.StrokeThickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                dc.DrawGeometry(null, pen, geo);
            }
            else if (stroke is System.Windows.Shapes.Path path && path.Data != null)
            {
                var pen = new System.Windows.Media.Pen(path.Stroke, path.StrokeThickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                dc.DrawGeometry(path.Fill, pen, path.Data);
            }
        }

        Point GetPixelScale()
        {
            var width = ActualWidth > 1 ? ActualWidth : Width;
            var height = ActualHeight > 1 ? ActualHeight : Height;
            return new Point(
                desktop.PixelWidth / Math.Max(1.0, width),
                desktop.PixelHeight / Math.Max(1.0, height));
        }

        static bool IsFromToolbar(DependencyObject source)
        {
            while (source != null)
            {
                if (source is FrameworkElement fe && Equals(fe.Tag, "toolbar")) return true;
                if (source is ButtonBase) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
