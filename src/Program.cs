using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using TodoAssistant.Services;

namespace TodoAssistant
{
    /// <summary>
    /// 程序入口。用独立类承载 Main，App（UI 总控）继承 Application 并通过 AppUI.xaml 加载资源字典。
    /// </summary>
    public static class Program
    {
        private const string MutexName = @"Global\TodoAssistant_SingleInstance_9F2C";
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        public static int Main(string[] args)
        {
            // ---- 命令行维护命令 ----
            if (args.Any(a => a.Equals("--repair", StringComparison.OrdinalIgnoreCase)))
                return RepairRegistration();
            if (args.Any(a => a.Equals("--uninstall-startup", StringComparison.OrdinalIgnoreCase)))
            {
                ShortcutHelper.RemoveStartupShortcut();
                ShortcutHelper.SetRunKey(false, ShortcutHelper.ExePath());
                Console.WriteLine("已取消开机自启");
                return 0;
            }
            // 开发用：离屏渲染对话框截图（不进入托盘 / 单实例流程）
            int snapIdx = Array.FindIndex(args, a => a.Equals("--ui-snapshot", StringComparison.OrdinalIgnoreCase));
            if (snapIdx >= 0)
            {
                var snapApp = new App();
                snapApp.InitializeComponent();
                string dir = snapIdx + 1 < args.Length
                    ? args[snapIdx + 1]
                    : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TodoAssistant_shots");
                int rc = Tools.UiSnapshot.Run(dir);
                snapApp.Shutdown();
                return rc;
            }

            // ---- 单实例 ----
            Mutex mutex = null;
            bool createdNew = false;
            try
            {
                mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (Exception ex)
            {
                Log.Write("创建单实例互斥量失败: " + ex.Message);
                createdNew = true;
            }

            if (!createdNew)
            {
                // 已有实例在运行：把它叫出来
                ActivateExistingInstance();
                return 0;
            }

            // 控制台类命令：输出到当前控制台
            bool consoleMode = args.Any(a => a.StartsWith("--") &&
                (a.Equals("--selfcheck", StringComparison.OrdinalIgnoreCase) ||
                 a.Equals("--log", StringComparison.OrdinalIgnoreCase)));
            if (consoleMode)
            {
                try
                {
                    Console.OutputEncoding = System.Text.Encoding.UTF8;
                    AttachConsole(-1);
                }
                catch { }
            }

            // ---- 主流程 ----
            int exitCode = 0;
            App app = null;
            try
            {
                app = new App();
                app.InitializeComponent();          // 加载 AppUI.xaml 资源字典
                app.InitializeComponents(args);     // 数据、设置、托盘、提醒、手机服务

                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log.Error("未处理的界面异常", e.Exception);
                    e.Handled = true;
                    MessageBox.Show("程序遇到一个问题，已记录日志：\n" + e.Exception.Message +
                                    "\n\n日志位置：" + Log.FilePath, "待办小助手",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Log.Error("未处理的异常", e.ExceptionObject as Exception);
                };

                app.Startup += (s, e) => { };
                app.StartServices();
                exitCode = app.Run();
            }
            catch (Exception ex)
            {
                Log.Error("启动失败", ex);
                MessageBox.Show("待办小助手启动失败：\n" + ex.Message + "\n\n日志：" + Log.FilePath,
                    "待办小助手", MessageBoxButton.OK, MessageBoxImage.Error);
                exitCode = 1;
            }
            finally
            {
                try { GC.KeepAlive(mutex); } catch { }
            }
            return exitCode;
        }

        private static void ActivateExistingInstance()
        {
            try
            {
                // 通过进程查找已运行实例的主窗口
                var self = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(self.ProcessName))
                {
                    if (p.Id == self.Id) continue;
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(p.MainWindowHandle);
                        return;
                    }
                }
                // 窗口被隐藏（最小化到托盘）时：发一条自定义消息/用名称查找
                IntPtr hwnd = FindWindow(null, "待办小助手");
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
            }
            catch (Exception ex)
            {
                Log.Write("唤出已有实例失败: " + ex.Message);
            }
        }

        /// <summary>重新注册通知所需组件（快捷方式 / AUMID）。供「修复」使用。</summary>
        private static int RepairRegistration()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Log.Init(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TodoAssistant"));
            string exe = ShortcutHelper.ExePath();
            bool ok = ShortcutHelper.EnsureAppRegistration(exe);
            Console.WriteLine((ok ? "成功" : "失败") + "：" + ShortcutHelper.StartMenuShortcutPath);
            Console.WriteLine("如果 Windows 通知仍然不出现，请到 设置 → 系统 → 通知 中确认「待办小助手」已开启通知。");
            return ok ? 0 : 1;
        }
    }
}
