using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClipboardAtlas
{
    /// <summary>
    /// Material Icons (outline) path data from
    /// https://github.com/material-icons/material-icons and
    /// https://github.com/google/material-design-icons (Apache-2.0).
    /// </summary>
    static class MaterialIcons
    {
        // crop_square — rectangle selection tool
        public const string CropSquare =
            "M18 4H6c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 14H6V6h12v12z";

        // circle — ellipse selection tool
        public const string Circle =
            "M12 2C6.47 2 2 6.47 2 12s4.47 10 10 10s10-4.47 10-10S17.53 2 12 2zm0 18c-4.42 0-8-3.58-8-8s3.58-8 8-8s8 3.58 8 8s-3.58 8-8 8z";

        // north_east — arrow tool
        public const string NorthEast =
            "M9 5v2h6.59L4 18.59L5.41 20L17 8.41V15h2V5H9z";

        // edit — pen tool
        public const string Edit =
            "M14.06 9.02l.92.92L5.92 19H5v-.92l9.06-9.06M17.66 3c-.25 0-.51.1-.7.29l-1.83 1.83l3.75 3.75l1.83-1.83a.996.996 0 0 0 0-1.41l-2.34-2.34c-.2-.2-.45-.29-.71-.29zm-3.6 3.19L3 17.25V21h3.75L17.81 9.94l-3.75-3.75z";

        // undo
        public const string Undo =
            "M12.5 8c-2.65 0-5.05.99-6.9 2.6L2 7v9h9l-3.62-3.62c1.39-1.16 3.16-1.88 5.12-1.88c3.54 0 6.55 2.31 7.6 5.5l2.37-.78C21.08 11.03 17.15 8 12.5 8z";

        // close — cancel
        public const string Close =
            "M19 6.41L17.59 5L12 10.59L6.41 5L5 6.41L10.59 12L5 17.59L6.41 19L12 13.41L17.59 19L19 17.59L13.41 12L19 6.41z";

        // check — confirm
        public const string Check =
            "M9 16.17L4.83 12l-1.42 1.41L9 19L21 7l-1.41-1.41L9 16.17z";

        public static FrameworkElement Create(string pathData, Brush brush, double size = 16)
        {
            var path = new Path
            {
                Data = Geometry.Parse(pathData),
                Fill = brush,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                SnapsToDevicePixels = true
            };
            return new Viewbox
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Child = path,
                IsHitTestVisible = false
            };
        }
    }
}
