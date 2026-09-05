using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using DrawingIcon = System.Drawing.Icon;

namespace ClipboardAtlas
{
    sealed class TrayService : IDisposable
    {
        readonly NotifyIcon notify;
        readonly Action showPanel;
        readonly Action openSettings;
        readonly Action quit;

        public TrayService(Action showPanel, Action openSettings, Action quit)
        {
            this.showPanel = showPanel;
            this.openSettings = openSettings;
            this.quit = quit;

            var menu = new ContextMenuStrip();
            menu.Items.Add("显示面板", null, (_, __) => this.showPanel?.Invoke());
            menu.Items.Add("设置", null, (_, __) => this.openSettings?.Invoke());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (_, __) => this.quit?.Invoke());

            notify = new NotifyIcon
            {
                Text = "kinolincopy",
                Icon = LoadIcon(),
                Visible = true,
                ContextMenuStrip = menu
            };
            notify.DoubleClick += (_, __) => this.showPanel?.Invoke();
            notify.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) this.showPanel?.Invoke();
            };
        }

        static DrawingIcon LoadIcon()
        {
            try
            {
                var exe = Autostart.ExePath;
                var associated = DrawingIcon.ExtractAssociatedIcon(exe);
                if (associated != null) return associated;
            }
            catch
            {
                // Fall through to embedded resource.
            }

            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream("kinolincopy.app.ico"))
            {
                if (stream != null)
                {
                    using (var loaded = new DrawingIcon(stream))
                        return (DrawingIcon)loaded.Clone();
                }
            }

            return SystemIcons.Application;
        }

        public void Dispose()
        {
            notify.Visible = false;
            notify.Dispose();
        }
    }
}
