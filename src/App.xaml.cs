using System;
using System.Threading;
using System.Windows;

namespace ClipboardAtlas
{
    public partial class App : Application
    {
        Mutex mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool created;
            mutex = new Mutex(true, "ClipboardAtlas.SingleInstance", out created);
            if (!created)
            {
                Shutdown();
                return;
            }

            base.OnStartup(e);
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { mutex?.ReleaseMutex(); } catch { }
            mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
