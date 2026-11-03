using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TodoAssistant.Services;
using TodoAssistant.ViewModels;
using TodoAssistant.Views.Dialogs;

namespace TodoAssistant.Tools
{
    /// <summary>
    /// 开发/排障用：把主窗口和「添加待办」对话框离屏渲染成 PNG，浅色/深色各出一张，
    /// 方便在没有鼠标或托盘的自动化环境里核对界面。
    /// 用法：TodoAssistant.exe --ui-snapshot &lt;输出目录&gt;
    /// </summary>
    public static class UiSnapshot
    {
        public static int Run(string outDir)
        {
            Directory.CreateDirectory(outDir);

            // 快照模式下把存储准备好，才能看到真实卡片
            if (App.Current is App a && a.Store == null)
            {
                try { a.InitializeComponents(new string[0]); }
                catch (Exception ex) { Console.WriteLine("初始化数据失败: " + ex.Message); }
            }

            // 大日历单独一张（只拍一次）
            try
            {
                ThemeManager.Apply(AppTheme.Light);
                var cal = new BigCalendarWindow(App.Current);
                cal.Width = 1060; cal.Height = 720;
                cal.Show();
                System.Threading.Tasks.Task.Delay(600).Wait();
                cal.UpdateLayout();
                Snap((FrameworkElement)cal.Content, Path.Combine(outDir, "big_calendar.png"));
                cal.Close();
            }
            catch (Exception ex) { Console.WriteLine("大日历快照失败: " + ex.Message); }

            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                string suffix = theme == AppTheme.Dark ? "_dark" : "";
                try { ThemeManager.Apply(theme); }
                catch (Exception ex) { Console.WriteLine("应用主题失败: " + ex.Message); }
                try
                {
                    var cb = Application.Current.Resources["CardBrush"] as SolidColorBrush;
                    var wb = Application.Current.Resources["WinBackgroundBrush"] as SolidColorBrush;
                    Console.WriteLine($"[THEME-DIAG] {theme} card={cb?.Color} win={wb?.Color} isDark={ThemeManager.IsDark}");
                }
                catch (Exception dex) { Console.WriteLine("[THEME-DIAG] err " + dex.Message); }

                try
                {
                    var main = new Views.MainWindow(App.Current);
                    main.Width = 1440;
                    main.Height = 800;
                    main.Show();
                    main.UpdateLayout();
                    System.Threading.Tasks.Task.Delay(400).Wait();
                    main.Refresh();
                    System.Threading.Tasks.Task.Delay(300).Wait();
                    main.UpdateLayout();
                    Snap((FrameworkElement)main.Content, Path.Combine(outDir, "main_window" + suffix + ".png"));
                    // 第一列切到时间表视图再拍一张
                    try
                    {
                        main.ToggleColumnViewPublic(0);
                        System.Threading.Tasks.Task.Delay(500).Wait();
                        main.UpdateLayout();
                        Snap((FrameworkElement)main.Content, Path.Combine(outDir, "schedule_view" + suffix + ".png"));
                    }
                    catch (Exception ex2) { Console.WriteLine("时间表快照失败: " + ex2.Message); }
                    main.Hide();
                    main.Close();
                }
                catch (Exception ex) { Console.WriteLine("主窗口快照失败: " + ex.Message); }

                try
                {
                    var vm = new TodoEditorViewModel();
                    vm.Initialize(null, 0, 30);
                    var dlg = new TodoEditorWindow(vm);
                    SnapEditor(dlg, Path.Combine(outDir, "dialog_new" + suffix + ".png"), false);
                }
                catch (Exception ex) { Console.WriteLine("新建对话框快照失败: " + ex.Message); }

                try
                {
                    var item = new Models.TodoItem
                    {
                        Date = DateTime.Today.ToString("yyyy-MM-dd"),
                        Start = "14:30",
                        End = "15:45",
                        Title = "项目周会 · 讨论下周排期",
                        Notes = "会议室 3 楼 A301，带上电脑",
                        LeadMinutes = 30,
                        HasDeadline = true,
                        DeadlineDate = DateTime.Today.ToString("yyyy-MM-dd"),
                        DeadlineTime = "23:59",
                        DeadlineLeadMinutes = 180,
                    };
                    item.Normalize();
                    var vm2 = new TodoEditorViewModel();
                    vm2.Initialize(item, 0, 30);
                    var dlg2 = new TodoEditorWindow(vm2);
                    SnapEditor(dlg2, Path.Combine(outDir, "dialog_edit" + suffix + ".png"), true);
                }
                catch (Exception ex) { Console.WriteLine("编辑对话框快照失败: " + ex.Message); }
            }

            Console.WriteLine("快照已保存到 " + outDir);
            return 0;
        }

        private static void SnapEditor(Window w, string path, bool showDdl)
        {
            w.Show();
            w.UpdateLayout();
            var content = (FrameworkElement)w.Content;
            if (showDdl && w.FindName("DeadlinePanel") is StackPanel sp)
                sp.Visibility = Visibility.Visible;
            System.Threading.Tasks.Task.Delay(250).Wait();
            w.UpdateLayout();
            Snap(content, path);
            w.Close();
        }

        private static void Snap(FrameworkElement root, string path)
        {
            double width = root.ActualWidth > 0 ? root.ActualWidth : 480;
            double height = root.ActualHeight > 0 ? root.ActualHeight : 760;
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
    }
}
