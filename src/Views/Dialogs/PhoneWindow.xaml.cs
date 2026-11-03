using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using TodoAssistant.Models;
using TodoAssistant.Services;

namespace TodoAssistant.Views.Dialogs
{
    public partial class PhoneWindow : Window
    {
        private readonly App _app;
        private readonly AppSettings _working;

        public PhoneWindow(App app)
        {
            InitializeComponent();
            _app = app;
            _working = app.Settings.Current.Clone();

            NtfyEnableCheck.IsChecked = _working.NtfyEnabled;
            TopicBox.Text = _working.NtfyTopic;
            ServerBox.Text = _working.NtfyServer;
            PinText.Text = _working.PhonePin;
            UrlText.Text = _app.PhoneUrl;
            UpdateServerState();

            try
            {
                QrImage.Source = QrCode.Render(_app.PhoneUrl, 240);
            }
            catch (Exception ex)
            {
                Log.Error("生成二维码失败", ex);
            }
        }

        private void UpdateServerState()
        {
            bool on = _app.PhoneServerRunning;
            ServerDot.Fill = on ? (System.Windows.Media.Brushes.SeaGreen) : System.Windows.Media.Brushes.IndianRed;
            ServerStateText.Text = on
                ? "服务器运行中 · 端口 " + _app.Settings.Current.PhonePort
                : "服务器未运行" + (string.IsNullOrEmpty(_app.PhoneServerError) ? "" : "：" + _app.PhoneServerError);
        }

        private void Url_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                Clipboard.SetText(_app.PhoneUrl);
                MessageBox.Show(this, "地址已复制：" + _app.PhoneUrl, "已复制", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(_app.PhoneUrl); } catch { }
        }

        private void OpenUrl_Click(object sender, RoutedEventArgs e) => _app.OpenPhonePage();

        private void ResetPin_Click(object sender, RoutedEventArgs e)
        {
            _working.PhonePin = AppSettings.RandomCode(6);
            PinText.Text = _working.PhonePin;
        }

        private void RestartServer_Click(object sender, RoutedEventArgs e)
        {
            bool ok = _app.RestartPhoneServer();
            UpdateServerState();
            UrlText.Text = _app.PhoneUrl;
            try { QrImage.Source = QrCode.Render(_app.PhoneUrl, 240); } catch { }
            if (!ok)
                MessageBox.Show(this, "服务器启动失败：" + _app.PhoneServerError +
                    "\n\n常见原因：端口被占用（改端口）、防火墙拦截、或端口需要管理员权限。",
                    "手机同步", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void RandomTopic_Click(object sender, RoutedEventArgs e)
        {
            TopicBox.Text = "tododooo-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        private void OpenTopic_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string server = (ServerBox.Text ?? "").Trim().TrimEnd('/');
                string topic = (TopicBox.Text ?? "").Trim();
                if (string.IsNullOrEmpty(topic)) { MessageBox.Show(this, "请先填写主题", "提示"); return; }
                Process.Start(new ProcessStartInfo(server + "/" + Uri.EscapeDataString(topic)) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error("打开订阅页失败", ex); }
        }

        private async void TestPush_Click(object sender, RoutedEventArgs e)
        {
            ApplyToWorking();
            PushResultText.Text = "发送中…";
            bool ok = await PhoneNotifier.PushTestAsync(_working);
            PushResultText.Text = ok
                ? "✓ 已推送，请看手机 ntfy"
                : "✗ 推送失败，请检查主题与服务器（日志里有详细原因）";
            PushResultText.Foreground = ok ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.IndianRed;
        }

        private void ApplyToWorking()
        {
            _working.NtfyEnabled = NtfyEnableCheck.IsChecked == true;
            _working.NtfyTopic = (TopicBox.Text ?? "").Trim();
            _working.NtfyServer = string.IsNullOrWhiteSpace(ServerBox.Text) ? "https://ntfy.sh" : ServerBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(_working.NtfyTopic) && _working.NtfyEnabled)
            {
                _working.NtfyTopic = "tododooo-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                TopicBox.Text = _working.NtfyTopic;
            }
            _working.PhonePin = PinText.Text;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ApplyToWorking();
            _app.ApplySettings(_working, restartPhone: true);
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
