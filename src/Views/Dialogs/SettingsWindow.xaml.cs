using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using TodoAssistant.Models;
using TodoAssistant.Services;
using TodoAssistant.ViewModels;

namespace TodoAssistant.Views.Dialogs
{
    public partial class SettingsWindow : Window
    {
        private readonly LeadOption[] _leads =
        {
            new LeadOption(0, "不提前（仅到点）"),
            new LeadOption(5, "提前 5 分钟"),
            new LeadOption(10, "提前 10 分钟"),
            new LeadOption(15, "提前 15 分钟"),
            new LeadOption(20, "提前 20 分钟"),
            new LeadOption(30, "提前 30 分钟"),
            new LeadOption(45, "提前 45 分钟"),
            new LeadOption(60, "提前 1 小时"),
            new LeadOption(120, "提前 2 小时"),
            new LeadOption(1440, "提前 1 天"),
        };

        public AppSettings Result { get; }
        public bool TestRequested { get; private set; }

        public SettingsWindow(AppSettings current, string dataDir)
        {
            InitializeComponent();
            Result = current.Clone();

            LeadCombo.ItemsSource = _leads;
            LeadCombo.SelectedItem = Array.Find(_leads, l => l.Minutes == current.LeadMinutes) ?? _leads[5];

            // 主题
            ThemeCombo.ItemsSource = new[] { "跟随系统", "浅色", "深色" };
            ThemeCombo.SelectedIndex = Math.Max(0, Math.Min(2, current.ThemeMode));

            // DDL
            DdlDigestCheck.IsChecked = current.DdlDigestEnabled;
            DdlMorningBox.Text = string.IsNullOrWhiteSpace(current.DdlDigestMorning) ? "09:00" : current.DdlDigestMorning;
            DdlEveningBox.Text = string.IsNullOrWhiteSpace(current.DdlDigestEvening) ? "21:00" : current.DdlDigestEvening;
            DdlDaysCombo.ItemsSource = new[] { "3 天内", "7 天内", "14 天内", "30 天内" };
            DdlDaysCombo.SelectedIndex = current.DdlDigestDays <= 3 ? 0 : current.DdlDigestDays <= 7 ? 1
                                       : current.DdlDigestDays <= 14 ? 2 : 3;

            NotifyAtStartCheck.IsChecked = current.NotifyAtStart;
            ToastCheck.IsChecked = current.SystemToast;
            BeepCheck.IsChecked = current.SystemBeep;
            SpeakCheck.IsChecked = current.SpeakContent;
            StartupCheck.IsChecked = ShortcutHelper.StartupShortcutExists;
            ShowOnStartupCheck.IsChecked = current.ShowOnStartup;
            HotkeyCheck.IsChecked = true;
            HotkeyCheck.IsEnabled = false;   // 快捷键始终开启（若被占用会提示）

            int idx = Math.Max(0, Math.Min(3, current.BeepRepeat - 1));
            RepeatBeepCombo.SelectedIndex = idx;

            DataPathText.Text = Path.Combine(dataDir, App.DataFileName);
            AboutText.Text =
                "待办小助手 v1.0 · .NET " + Environment.Version.ToString(2) + "\n" +
                "• 开机自启，常驻系统托盘，左键图标打开窗口\n" +
                "• 到点前 30 分钟弹 Windows 通知 + 响铃 + 语音播报\n" +
                "• 手机同一 WiFi 下打开局域网地址，或使用 ntfy 离线推送（见“手机同步”）\n" +
                "程序位置：" + ShortcutHelper.ExePath();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            Result.LeadMinutes = (LeadCombo.SelectedItem as LeadOption)?.Minutes ?? 30;
            Result.NotifyAtStart = NotifyAtStartCheck.IsChecked == true;
            Result.SystemToast = ToastCheck.IsChecked == true;
            Result.SystemBeep = BeepCheck.IsChecked == true;
            Result.SpeakContent = SpeakCheck.IsChecked == true;
            Result.BeepRepeat = RepeatBeepCombo.SelectedIndex < 0 ? 2 : RepeatBeepCombo.SelectedIndex + 1;
            // 默认保持开机自启；只有用户明确取消时才关闭
            Result.RunAtStartup = StartupCheck.IsChecked != false;
            Result.ShowOnStartup = ShowOnStartupCheck.IsChecked == true;

            // 主题
            Result.ThemeMode = Math.Max(0, Math.Min(2, ThemeCombo.SelectedIndex));

            // DDL
            Result.DdlDigestEnabled = DdlDigestCheck.IsChecked == true;
            if (DeadlineService.TryParseHm(DdlMorningBox.Text?.Trim(), out var m))
                Result.DdlDigestMorning = m.ToString(@"hh\:mm");
            if (DeadlineService.TryParseHm(DdlEveningBox.Text?.Trim(), out var e2))
                Result.DdlDigestEvening = e2.ToString(@"hh\:mm");
            Result.DdlDigestDays = DdlDaysCombo.SelectedIndex switch { 0 => 3, 1 => 7, 2 => 14, 3 => 30, _ => 7 };

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Test_Click(object sender, RoutedEventArgs e)
        {
            TestRequested = true;
            DialogResult = true;
        }

        private void OpenData_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = Path.GetDirectoryName(DataPathText.Text.Trim());
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error("打开数据文件夹失败", ex); }
        }

        private void Backup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = Path.GetDirectoryName(DataPathText.Text.Trim());
                string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "待办小助手备份", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(target);
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                    File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "备份失败：" + ex.Message, "备份", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
