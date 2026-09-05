using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ClipboardAtlas
{
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Kinolincopy";
        static readonly string[] LegacyValueNames = { "ClipboardAtlas", "复制档案" };

        public static string ExePath
        {
            get
            {
                try
                {
                    return Process.GetCurrentProcess().MainModule?.FileName
                        ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Kinolincopy.exe");
                }
                catch
                {
                    return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Kinolincopy.exe");
                }
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    var value = key?.GetValue(ValueName) as string;
                    return !string.IsNullOrWhiteSpace(value);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    foreach (var legacy in LegacyValueNames)
                        key.DeleteValue(legacy, false);
                    if (enabled) key.SetValue(ValueName, "\"" + ExePath + "\"");
                    else key.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法更新开机自启设置：\n" + ex.Message, "Kinolincopy", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
