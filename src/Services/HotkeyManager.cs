using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TodoAssistant.Services
{
    /// <summary>全局快捷键 Ctrl+Alt+T（呼出主窗口）</summary>
    public sealed class HotkeyManager : IDisposable
    {
        private const int HotkeyId = 0xA701;
        private const int MOD_ALT = 0x1;
        private const int MOD_CONTROL = 0x2;
        private const int MOD_NOREPEAT = 0x4000;
        private const int WM_HOTKEY = 0x0312;

        private readonly System.Windows.Interop.HwndSource _source;
        private bool _registered;

        public event Action Pressed;

        public HotkeyManager(System.Windows.Window window)
        {
            // 必须挂在有消息循环的 WPF 窗口上（窗口可以一直隐藏）
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            _source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            if (_source != null) _source.AddHook(WndProc);
            _registered = NativeMethods.RegisterHotKey(hwnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x54 /* T */);
            if (!_registered)
                Log.Write("全局快捷键 Ctrl+Alt+T 注册失败（可能被其他程序占用，或窗口尚未创建）");
            else
                Log.Write("全局快捷键 Ctrl+Alt+T 已注册");
        }

        public bool Registered => _registered;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                Pressed?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            try
            {
                if (_registered) NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            }
            catch { }
            try { _source?.RemoveHook(WndProc); } catch { }
            _registered = false;
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        }
    }

    /// <summary>登录自启方式（启动文件夹快捷方式）</summary>
    public static class RegistryStartup
    {
        public static bool IsEnabled => ShortcutHelper.StartupShortcutExists;

        public static void Set(bool enable, string exePath)
        {
            if (enable) ShortcutHelper.CreateStartupShortcut(exePath);
            else ShortcutHelper.RemoveStartupShortcut();
        }
    }
}
