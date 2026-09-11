using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace ClipboardAtlas
{
    static class HotkeySpec
    {
        public const int ModAlt = 0x0001;
        public const int ModControl = 0x0002;
        public const int ModShift = 0x0004;
        public const int ModWin = 0x0008;
        public const int ModNorepeat = 0x4000;
        public const int VkA = 0x41;

        public static readonly int DefaultModifiers = ModAlt;
        public static readonly int DefaultKey = VkA;

        public static string Format(int modifiers, int virtualKey)
        {
            var parts = new List<string>();
            if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
            if ((modifiers & ModAlt) != 0) parts.Add("Alt");
            if ((modifiers & ModShift) != 0) parts.Add("Shift");
            if ((modifiers & ModWin) != 0) parts.Add("Win");
            var key = KeyFromVirtual(virtualKey);
            if (key != Key.None) parts.Add(KeyLabel(key));
            else if (virtualKey > 0) parts.Add("0x" + virtualKey.ToString("X2"));
            return parts.Count == 0 ? "未设置" : string.Join("+", parts);
        }

        public static bool TryFromKeyEvent(KeyEventArgs e, out int modifiers, out int virtualKey)
        {
            modifiers = 0;
            virtualKey = 0;
            if (e == null) return false;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (IsModifier(key)) return false;

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= ModControl;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= ModAlt;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= ModShift;
            if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers |= ModWin;
            if (modifiers == 0) return false;

            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0) return false;
            virtualKey = vk;
            return true;
        }

        public static bool IsDefault(int modifiers, int virtualKey)
        {
            return (modifiers & ~ModNorepeat) == DefaultModifiers && virtualKey == DefaultKey;
        }

        static bool IsModifier(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl
                || key == Key.LeftAlt || key == Key.RightAlt
                || key == Key.LeftShift || key == Key.RightShift
                || key == Key.LWin || key == Key.RWin
                || key == Key.System;
        }

        static Key KeyFromVirtual(int virtualKey)
        {
            if (virtualKey <= 0) return Key.None;
            try { return KeyInterop.KeyFromVirtualKey(virtualKey); }
            catch { return Key.None; }
        }

        static string KeyLabel(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
            if (key >= Key.A && key <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
            if (key >= Key.F1 && key <= Key.F24) return "F" + (1 + (key - Key.F1));
            switch (key)
            {
                case Key.OemPlus: return "+";
                case Key.OemMinus: return "-";
                case Key.OemComma: return ",";
                case Key.OemPeriod: return ".";
                case Key.Space: return "Space";
                case Key.PrintScreen: return "PrtSc";
                case Key.Insert: return "Ins";
                case Key.Delete: return "Del";
                case Key.Home: return "Home";
                case Key.End: return "End";
                case Key.PageUp: return "PgUp";
                case Key.PageDown: return "PgDn";
                case Key.Up: return "↑";
                case Key.Down: return "↓";
                case Key.Left: return "←";
                case Key.Right: return "→";
                default:
                    var text = key.ToString();
                    return string.IsNullOrEmpty(text) ? "?" : text;
            }
        }
    }
}
