using System;
using CommunityToolkit.WinUI.Notifications;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using TodoAssistant.Controls;

namespace TodoAssistant.Services
{
    /// <summary>
    /// Windows 通知：优先用「通知中心」Toast（CommunityToolkit 的 Compat 版，
    /// 无需应用打包、也不依赖写快捷方式），失败时退回旧式托盘气泡通知。
    /// </summary>
    public sealed class Notifier
    {
        private const string AppName = "待办小助手";

        private TaskbarIcon _tray;

        public Notifier()
        {
            PhoneNotifier.TagGenerator = TagFor;
        }

        /// <summary>最近一次通知是否通过通知中心显示</summary>
        public bool LastUsedToast { get; private set; }

        /// <summary>用户点击通知后触发（参数为 launch 参数）</summary>
        public event Action<string> Activated;

        public void AttachTray(TaskbarIcon tray) => _tray = tray;

        /// <summary>
        /// 预初始化：建立一次 notifier 并清空旧通知（Compat 版借此把自身注册进开始菜单，
        /// 之后再弹通知系统才会真正显示）。
        /// </summary>
        public void Initialize(string exePath)
        {
            try
            {
                ToastNotificationManagerCompat.OnActivated += e =>
                {
                    string arg = e.Argument ?? "";
                    Log.Write("通知被点击: " + arg);
                    var app = System.Windows.Application.Current;
                    if (app == null) return;
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { Activated?.Invoke(arg); } catch (Exception ex) { Log.Write("处理通知点击失败: " + ex.Message); }
                    }));
                };
            }
            catch (Exception ex) { Log.Error("注册通知点击回调失败", ex); }

            try
            {
                ToastNotificationManagerCompat.CreateToastNotifier();
                ToastNotificationManagerCompat.History.Clear();
                Log.Write("Windows 通知中心(Compat) 已就绪");
            }
            catch (Exception ex)
            {
                Log.Error("Windows 通知中心初始化失败，将退回托盘气泡通知", ex);
            }

            try { ShortcutHelper.EnsureAppRegistration(exePath); }
            catch (Exception ex) { Log.Write("开始菜单快捷方式创建失败（不影响通知）: " + ex.Message); }
        }

        /// <summary>弹出通知（优先通知中心，失败退回托盘气泡）</summary>
        public void Show(string title, string body)
        {
            LastUsedToast = TryToast(title, body);
            if (!LastUsedToast) TryTrayBalloon(title, body);
        }

        /// <summary>生成 ntfy 用的 tag（只允许字母数字）</summary>
        public static string TagFor(string tag)
        {
            uint h = 2166136261;
            foreach (char c in tag ?? "") { h ^= c; h *= 16777619; }
            return "ta" + h.ToString("x8");
        }

        private bool TryToast(string title, string body)
        {
            try
            {
                var builder = new ToastContentBuilder()
                    .AddText(title ?? AppName)
                    .AddText(body ?? "")
                    .AddAttributionText(AppName)
                    .AddButton(new ToastButton().SetContent("打开待办").SetBackgroundActivation())
                    .AddAudio(new ToastAudio
                    {
                        Src = new Uri("ms-winsoundevent:Notification.Reminder"),
                        Loop = false,
                        Silent = false,
                    });

                builder.GetToastContent();
                builder.Show(t =>
                {
                    t.Tag = "todo";
                    t.Group = "todogroup";
                });
                Log.Write("已发送 Windows 通知: " + title + " | " + body);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("发送 Windows 通知失败，改为托盘气泡", ex);
                return false;
            }
        }

        private void TryTrayBalloon(string title, string body)
        {
            if (_tray == null) return;
            void show()
            {
                try
                {
                    _tray.ForceCreate(false);
                    _tray.ShowNotification(title ?? AppName, body ?? "", NotificationIcon.Info);
                    Log.Write("已发送托盘气泡通知");
                }
                catch (Exception ex) { Log.Error("托盘气泡通知失败", ex); }
            }
            var app = System.Windows.Application.Current;
            if (app != null) app.Dispatcher.BeginInvoke(new Action(show));
            else show();
        }
    }
}
