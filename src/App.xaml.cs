using System;
using System.Threading;
using System.Windows;

namespace ClipboardAtlas
{
    public partial class App : Application
    {
        public const string MutexName = "Local\\Kinolincopy.SingleInstance.v1";
        public const string ActivateEventName = "Local\\Kinolincopy.Activate.v1";

        Mutex mutex;
        EventWaitHandle activateEvent;
        Thread activateWatcher;
        volatile bool running = true;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool created;
            mutex = new Mutex(true, MutexName, out created);
            if (!created)
            {
                try
                {
                    using (var signal = EventWaitHandle.OpenExisting(ActivateEventName))
                        signal.Set();
                }
                catch
                {
                    // Existing instance may still be starting.
                }
                Shutdown();
                return;
            }

            activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();

            activateWatcher = new Thread(WatchActivate)
            {
                IsBackground = true,
                Name = "Kinolincopy.ActivateWatcher"
            };
            activateWatcher.Start();
        }

        void WatchActivate()
        {
            while (running)
            {
                try
                {
                    if (!activateEvent.WaitOne(500)) continue;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var window = MainWindow as MainWindow;
                        window?.ActivateFromExternal();
                    }));
                }
                catch
                {
                    break;
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            running = false;
            try { activateEvent?.Set(); } catch { }
            try { mutex?.ReleaseMutex(); } catch { }
            mutex?.Dispose();
            activateEvent?.Dispose();
            base.OnExit(e);
        }
    }
}
