using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using TodoAssistant.Controls;
using TodoAssistant.Models;
using TodoAssistant.Services;
using TodoAssistant.ViewModels;
using TodoAssistant.Views;

namespace TodoAssistant
{
    /// <summary>
    /// 程序入口与「总控」：
    /// - 单实例（重复启动只是把已有窗口叫出来）
    /// - 托盘图标（左键 → 主窗口，右键 → 菜单）
    /// - 提醒调度 → 系统通知 + 响铃 + 手机推送
    /// - 手机端网页服务器
    /// </summary>
    public sealed class App : AppUI
    {
        public const string DataFileName = "todos.json";

        private TaskbarIcon _tray;
        private MainWindow _mainWindow;
        private HotkeyManager _hotkey;

        private bool _exiting;

        public string DataDirectory { get; private set; }
        public string SettingsDataDir => DataDirectory;

        public TodoStore Store { get; private set; }
        public SettingsStore Settings { get; private set; }
        public ReminderScheduler Scheduler { get; private set; }

        /// <summary>DDL 提醒服务（早/晚汇总 + 单条提前提醒）</summary>
        public DeadlineService Deadline { get; private set; }
        public Notifier Notifier { get; private set; }
        public PhoneServer PhoneServer { get; private set; }
        public AlarmState AlarmState { get; } = new AlarmState();

        public bool PhoneServerRunning => PhoneServer != null && PhoneServer.IsRunning;
        public string PhoneServerError => PhoneServer?.LastError;
        public string PhoneUrl => PhoneServerRunning
            ? $"http://{PhoneServer.GetBestLanIp()}:{PhoneServer.Port}/"
            : "(手机同步未启动)";

        /// <summary>本次启动参数是否含 --autostart</summary>
        public bool IsAutostart { get; private set; }

        public static new App Current => (App)System.Windows.Application.Current;

        // ------------------------------------------------------------
        // 初始化
        // ------------------------------------------------------------

        public void InitializeComponents(string[] args)
        {
            IsAutostart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase) ||
                                        a.Equals("/autostart", StringComparison.OrdinalIgnoreCase));

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            DataDirectory = Path_DefaultDataDir();
            Log.Init(DataDirectory);
            Log.Write("===== 启动 TodoAssistant v1.0，参数: " + string.Join(" ", args).Trim() + " =====");
            try { Directory.CreateDirectory(DataDirectory); }
            catch (Exception ex) { Log.Error("创建数据目录失败: " + DataDirectory, ex); }
            if (args.Length > 0 && args[0] == "--selfcheck") { SelfCheck(); return; }

            ShortcutHelper.SetProcessAumid("TodoAssistant");

            Settings = new SettingsStore(DataDirectory);
            Settings.Load();
            Store = new TodoStore(DataDirectory) { Dispatch = a => Dispatcher.BeginInvoke(a) };
            Store.Load();
            Store.PurgeOld(DateTime.Now);

            Notifier = new Notifier();
            Notifier.AttachTray(null);

            // 主题：启动时按设置应用（跟随系统 / 浅色 / 深色）
            try { ThemeManager.Apply((AppTheme)Settings.Current.ThemeMode); }
            catch (Exception ex) { Log.Error("应用主题失败", ex); }
            ThemeManager.Changed += (s, e) => _mainWindow?.OnThemeChanged();

            Scheduler = new ReminderScheduler(Store, Settings);
            Scheduler.Fire += OnReminder;

            // DDL 提醒：每天的早/晚汇总 + 单条提前提醒，挂在调度器的每次扫描里
            Deadline = new DeadlineService(Store, Settings);
            Deadline.Fire += OnReminder;
            Scheduler.DeadlineTick += () => Deadline.Tick();

            Store.Changed += (s, e) => { /* UI 由 MainWindow 自行刷新 */ };

            PhoneServer = new PhoneServer(Store, Settings);
            PhoneServer.DataChangedByPhone += () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _mainWindow?.Refresh();
                    Scheduler.OnDataChanged();
                }));
            };

            // 通知点击 → 打开窗口
            Notifier.Activated += tag =>
            {
                ShowMainWindow();
            };
        }

        public void StartServices()
        {
            CreateTray();

            // 通知（AUMID 快捷方式注册等）
            try { Notifier.Initialize(ResolveExePath()); }
            catch (Exception ex) { Log.Error("通知模块初始化失败", ex); }
            if (_tray != null) Notifier.AttachTray(_tray);

            // 手机服务器
            if (Settings.Current.PhoneServerEnabled) StartPhoneServer();

            // 首次运行开机自启
            try
            {
                if (Settings.Current.RunAtStartup && !ShortcutHelper.StartupShortcutExists)
                {
                    ShortcutHelper.CreateStartupShortcut(ShortcutHelper.ExePath());
                    Log.Write("已创建开机自启快捷方式");
                }
                if (!Settings.Current.RunAtStartup && ShortcutHelper.StartupShortcutExists)
                    ShortcutHelper.RemoveStartupShortcut();
            }
            catch (Exception ex) { Log.Error("设置开机自启失败", ex); }

            // 全局快捷键需要窗口句柄：先隐式创建主窗口
            EnsureMainWindow();
            try
            {
                _hotkey = new HotkeyManager(_mainWindow);
                _hotkey.Pressed += () => Dispatcher.BeginInvoke(new Action(ShowMainWindow));
            }
            catch (Exception ex) { Log.Error("注册全局快捷键失败", ex); }

            // 提醒补发（错过的时间点）
            try
            {
                Scheduler.PrepareCatchUp(DateTime.Now);
            }
            catch (Exception ex) { Log.Error("准备补发失败", ex); }
            Scheduler.Start();

            // 是否打开主窗口
            bool show = Settings.Current.ShowOnStartup || !IsAutostart;
            if (show) ShowMainWindow();
            else FlashTrayHint();

            Log.Write("启动完成，托盘图标已就绪");
        }

        /// <summary>
        /// 鏁版嵁鐩綍锛氶粯璁?%LOCALAPPDATA%\TodoAssistant锛?        /// 渚挎惡妯″紡锛坋xe 鍚岀洰褰曞瓨鍦?portable.txt锛夋垨鐜鍙橀噺 TODOASSISTANT_DATA 鏃舵敼鐢ㄦ寚瀹氱洰褰曘€?        /// 鐜鍙橀噺涔熺敤浜庢祴璇曘€?        /// </summary>
        /// <summary>
        /// 诊断自检（--selfcheck）：不启动界面，依次验证 存储 / 通知 / 响铃 / 手机服务器 / 二维码 是否正常，
        /// 结果写到控制台与日志。用于安装后排查「没有通知」「手机连不上」等问题。
        /// </summary>
        private void SelfCheck()
        {
            var sb = new System.Text.StringBuilder();
            void Line(string s)
            {
                Console.WriteLine(s);
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(DataDirectory, "selfcheck.txt"), s + Environment.NewLine); } catch { }
            }
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(DataDirectory, "selfcheck.txt"), ""); } catch { }

            Line("待办小助手 自检  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Line("数据目录: " + DataDirectory);
            Line("程序路径: " + ShortcutHelper.ExePath());

            // 1. 存储
            try
            {
                Store = new TodoStore(DataDirectory);
                Store.Load();
                var probe = new TodoItem { Date = DateTime.Today.ToString("yyyy-MM-dd"), Start = "23:59", Title = "自检临时条目" };
                Store.Upsert(probe);
                bool persisted = Store.GetAllRaw().Any(i => i.Title == "自检临时条目");
                Store.Delete(probe.Id);
                Line("[存储] " + (persisted ? "正常" : "异常") + "，当前共 " + Store.GetAllRaw().Count + " 条待办");
            }
            catch (Exception ex) { Line("[存储] 失败: " + ex.Message); }

            // 2. 通知（与正式运行时走同一条代码路径）
            try
            {
                if (Notifier == null) Notifier = new Notifier();
                Notifier.Initialize(ShortcutHelper.ExePath());
                Line("[通知] 初始化完成；开始菜单快捷方式: " +
                     (System.IO.File.Exists(ShortcutHelper.StartMenuShortcutPath) ? "已创建" : "创建失败（不影响通知）"));
                Notifier.Show("待办小助手 · 自检通知", "如果你在右下角看到这条通知，说明系统通知正常～");
                Line("[通知] 已发送一条测试通知: " + (Notifier.LastUsedToast ? "通知中心" : "托盘气泡"));
            }
            catch (Exception ex) { Line("[通知] 失败: " + ex.Message); }

            // 3. 响铃
            try
            {
                Alarm.PlayTest(1, false);
                Line("[响铃] 已播放测试铃声（如没听到请检查系统音量）");
            }
            catch (Exception ex) { Line("[响铃] 失败: " + ex.Message); }

            // 4. 手机服务器
            try
            {
                Settings = new SettingsStore(DataDirectory);
                Settings.Load();
                if (Store == null) { Store = new TodoStore(DataDirectory); Store.Load(); }
                PhoneServer = new PhoneServer(Store, Settings);
                bool ok = PhoneServer.Start(Settings.Current.PhonePort);
                if (!ok)
                {
                    Line("[手机] 端口 " + Settings.Current.PhonePort + " 启动失败: " + PhoneServer.LastError);
                    ok = PhoneServer.Start(18974);
                }
                if (ok)
                {
                    string ip = PhoneServer.GetBestLanIp();
                    Line("[手机] 服务器已启动 → http://" + ip + ":" + PhoneServer.Port + "/  配对PIN=" + Settings.Current.PhonePin);
                    Line("[手机] 本机可用地址: " + string.Join(", ", PhoneServer.GetAllLanIps()));
                    // 自测一次 HTTP 请求
                    try
                    {
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                        var resp = http.GetAsync($"http://127.0.0.1:{PhoneServer.Port}/api/config").GetAwaiter().GetResult();
                        string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        Line("[手机] 本地接口自测: HTTP " + (int)resp.StatusCode + " " + body.Trim());
                    }
                    catch (Exception ex) { Line("[手机] 本地接口自测失败: " + ex.Message); }
                }
                else Line("[手机] 服务器启动失败: " + PhoneServer.LastError);
            }
            catch (Exception ex) { Line("[手机] 失败: " + ex.Message); }

            // 5. 二维码
            try
            {
                var qr = QrCode.Encode("http://127.0.0.1:1/");
                Line("[二维码] 生成正常，尺寸 " + qr.GetLength(0) + "x" + qr.GetLength(1));
            }
            catch (Exception ex) { Line("[二维码] 失败: " + ex.Message); }

            // 6. 推送配置
            try
            {
                var s = Settings?.Current ?? new AppSettings();
                Line("[手机推送] ntfy " + (s.NtfyEnabled ? "已启用" : "未启用") + "，主题=" + s.NtfyTopic + "，服务器=" + s.NtfyServer);
            }
            catch { }

            Line("自检结束");
            try { PhoneServer?.Stop(); } catch { }
        }

        private static string Path_DefaultDataDir()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("TODOASSISTANT_DATA");
                if (!string.IsNullOrWhiteSpace(env)) return env;
                string exeDir = System.IO.Path.GetDirectoryName(ShortcutHelper.ExePath() ?? "") ?? "";
                if (!string.IsNullOrEmpty(exeDir) && System.IO.File.Exists(System.IO.Path.Combine(exeDir, "portable.txt")))
                    return System.IO.Path.Combine(exeDir, "data");
            }
            catch { }
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return System.IO.Path.Combine(root, "TodoAssistant");
        }

        // ------------------------------------------------------------
        // 托盘
        // ------------------------------------------------------------

        private void CreateTray()
        {
            try
            {
                _tray = new TaskbarIcon
                {
                    ToolTipText = "待办小助手",
                    NoLeftClickDelay = true,
                };
                RestoreTrayIcon(false);

                // 左键：打开主窗口
                _tray.TrayLeftMouseUp += (s, e) => ShowMainWindow();
                _tray.TrayMouseDoubleClick += (s, e) => ShowMainWindow();

                _tray.ContextMenu = BuildTrayMenu();

                _tray.ForceCreate(false);
                Log.Write("托盘图标创建完成 (IsCreated=" + _tray.IsCreated + ")");
            }
            catch (Exception ex)
            {
                Log.Error("创建托盘图标失败", ex);
                Log.Write("  提示：托盘需要 Windows 资源管理器（explorer.exe）正在运行");
                // 托盘失败不致命：窗口仍可打开（例如被 explorer 未启动时的开机场景）
                if (_tray != null) { try { _tray.Dispose(); } catch { } _tray = null; }
            }
        }

        private void RestoreTrayIcon(bool alert)
        {
            if (_tray == null) return;
            try
            {
                using var icon = TrayIconFactory.GetTrayIcon(alert);
                if (icon == null) return;
                _tray.Icon = icon;
            }
            catch (Exception ex)
            {
                Log.Error("设置托盘图标失败", ex);
            }
        }

        private ContextMenu BuildTrayMenu()
        {
            var menu = new ContextMenu();
            // 跟随主题：深色模式下托盘菜单也变深
            try
            {
                menu.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "CardBrush");
                menu.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextBrush");
                menu.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "LineBrush");
            }
            catch { }
            menu.Padding = new Thickness(4);

            MenuItem Add(string header, Action action, bool bold = false, FontFamily iconFont = null)
            {
                var mi = new MenuItem
                {
                    Header = header,
                    FontSize = 13,
                    Padding = new Thickness(10, 6, 18, 6),
                };
                if (bold) mi.FontWeight = FontWeights.SemiBold;
                try { mi.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextBrush"); } catch { }
                mi.Click += (s, e) => { try { action(); } catch (Exception ex) { Log.Error("菜单操作失败", ex); } };
                menu.Items.Add(mi);
                return mi;
            }

            Add("打开待办窗口", ShowMainWindow, true);
            menu.Items.Add(new Separator());
            Add("＋ 添加待办…", () =>
            {
                ShowMainWindow();
                _mainWindow?.OpenEditor(null, 0);
            });
            Add("测试提醒（通知 + 铃声）", TestAlert);
            menu.Items.Add(new Separator());

            var today = Add("今日待办", () => ShowMainWindow());
            today.Items.Clear();
            today.SubmenuOpened += (s, e) => PopulateTodaySubmenu(today);

            Add("手机同步 / 推送设置…", () =>
            {
                EnsureMainWindow();
                var dlg = new TodoAssistant.Views.Dialogs.PhoneWindow(this) { Owner = _mainWindow };
                dlg.ShowDialog();
                _mainWindow.Refresh();
            });
            Add("设置…", () =>
            {
                EnsureMainWindow();
                var dlg = new TodoAssistant.Views.Dialogs.SettingsWindow(Settings.Current, DataDirectory) { Owner = _mainWindow };
                if (dlg.ShowDialog() == true) ApplySettings(dlg.Result);
                _mainWindow.Refresh();
            });
            Add("打开数据文件夹", () =>
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + DataDirectory + "\"") { UseShellExecute = true });
            });
            menu.Items.Add(new Separator());
            Add("退出", ExitApp);

            return menu;
        }

        private void PopulateTodaySubmenu(MenuItem parent)
        {
            parent.Items.Clear();
            var items = Store.GetRange(DateTime.Today, DateTime.Today);
            if (items.Count == 0)
            {
                parent.Items.Add(new MenuItem { Header = "今天没有待办", IsEnabled = false, FontSize = 12.5 });
                return;
            }
            foreach (var it in items.Take(20))
            {
                var mi = new MenuItem
                {
                    Header = $"{it.Start}  {it.Title}",
                    IsChecked = it.Done,
                    FontSize = 12.5,
                    FontWeight = it.Done ? FontWeights.Normal : FontWeights.SemiBold,
                };
                var captured = it;
                mi.Click += (s, e) =>
                {
                    ToggleDone(captured, !captured.Done);
                    parent.IsSubmenuOpen = false;
                };
                parent.Items.Add(mi);
            }
        }

        /// <summary>首次启动时提示托盘位置</summary>
        public void FlashTrayHint()
        {
            try
            {
                _tray?.ShowNotification("待办小助手已在托盘运行",
                    "左键点击托盘图标打开待办窗口，右键更多操作。到点前 30 分钟会提醒你。",
                    NotificationIcon.Info);
            }
            catch (Exception ex) { Log.Error("托盘提示失败", ex); }
        }

        // ------------------------------------------------------------
        // 主窗口
        // ------------------------------------------------------------

        /// <summary>当前 exe 路径（单文件发布时也可用）</summary>
        private static string ResolveExePath()
        {
            string p = "";
            try { p = ShortcutHelper.ExePath(); } catch { }
            if (!string.IsNullOrEmpty(p)) return p;
            try { p = Environment.ProcessPath; } catch { }
            return p ?? "";
        }

        public void EnsureMainWindow()
        {
            if (_mainWindow != null) return;
            _mainWindow = new MainWindow(this);
            Store.Changed += (s, e) => _mainWindow?.Refresh();
            // 关键：立即创建原生窗口句柄（窗口仍隐藏），否则注册全局快捷键会失败、单实例也无法唤出窗口
            try
            {
                new System.Windows.Interop.WindowInteropHelper(_mainWindow).EnsureHandle();
                Log.Write("主窗口句柄已创建");
            }
            catch (Exception ex) { Log.Error("创建主窗口句柄失败", ex); }
        }

        public void ShowMainWindow()
        {
            EnsureMainWindow();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!_mainWindow.IsVisible) _mainWindow.Show();
                    if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
                    _mainWindow.Activate();
                    _mainWindow.Topmost = true;
                    _mainWindow.Topmost = false;
                    _mainWindow.Focus();
                    _mainWindow.Refresh();
                }
                catch (Exception ex) { Log.Error("显示主窗口失败", ex); }
            }));
        }

        // ------------------------------------------------------------
        // 提醒
        // ------------------------------------------------------------

        private void OnReminder(ReminderEvent ev)
        {
            var s = Settings.Current;
            var item = ev.Item;

            // 1) 系统通知
            if (s.SystemToast)
            {
                Notifier.Show(ev.Title, ev.Body);
            }

            // 2) 响铃 + 语音
            if (s.SystemBeep || s.SpeakContent)
            {
                bool speak = s.SpeakContent && item.Speak;
                Alarm.Play(speak ? ev.Speech : null, Math.Max(1, s.BeepRepeat), speak);
                AlarmState.MarkPlaying(ev.Title);
                UpdateTrayAlert(true);
            }

            // 3) 手机推送
            bool phone = item.PhoneNotify ?? true;
            if (phone && s.NtfyEnabled && !string.IsNullOrWhiteSpace(s.NtfyTopic))
            {
                _ = PhoneNotifier.PushAsync(s, "⏰ " + ev.Title, ev.Body, ev.Kind == ReminderKind.Starting, "时间提醒|" + item.Id);
            }

            Log.Write("已提醒: " + ev.Title);
        }

        private Timer _alertTimer;

        public void UpdateTrayAlert(bool alert)
        {
            try
            {
                RestoreTrayIcon(alert);
                _alertTimer?.Dispose();
                if (alert)
                {
                    _alertTimer = new Timer(_ => Dispatcher.BeginInvoke(new Action(() => RestoreTrayIcon(false))),
                        null, 60000, Timeout.Infinite);
                }
            }
            catch (Exception ex) { Log.Error("更新托盘图标失败", ex); }
        }

        /// <summary>下一个提醒（用于界面显示）</summary>
        public (DateTime, string)? NextReminder()
        {
            try
            {
                var now = DateTime.Now;
                var items = Store.GetRange(now.Date, now.Date.AddDays(4));
                ReminderScheduler scheduler = Scheduler;
                DateTime best = DateTime.MaxValue;
                string text = null;
                foreach (var it in items)
                {
                    if (it.Done) continue;
                    var start = it.StartTime;
                    if (!start.HasValue || start.Value <= now) continue;
                    var at = start.Value.AddMinutes(-it.EffectiveLead);
                    if (at < now) at = now;   // 已经进入提醒窗口
                    if (it.ReminderSent && !it.Done) { at = start.Value; }
                    if (at < best)
                    {
                        best = at;
                        var span = start.Value - now;
                        string when = span.TotalMinutes < 60
                            ? $"{(int)Math.Round(span.TotalMinutes)} 分钟后"
                            : $"{(int)span.TotalHours} 小时 {(int)(span.Minutes)} 分后";
                        text = $"下一项：{it.Start} {it.Title}（{when}）";
                    }
                }
                if (best == DateTime.MaxValue) return null;
                return (best, text);
            }
            catch
            {
                return null;
            }
        }

        public void TestAlert()
        {
            var s = Settings.Current;
            try
            {
                if (s.SystemToast)
                    Notifier.Show("测试提醒 · 30 分钟后有安排", $"现在 {DateTime.Now:HH:mm}，这是一次测试通知");
                Alarm.PlayTest(Math.Max(1, s.BeepRepeat), s.SpeakContent);
                bool phone = s.NtfyEnabled && !string.IsNullOrWhiteSpace(s.NtfyTopic);
                if (phone)
                    _ = PhoneNotifier.PushTestAsync(s);
                Log.Write("已发送测试提醒（手机推送=" + phone + "）");
            }
            catch (Exception ex)
            {
                Log.Error("测试提醒失败", ex);
            }
        }

        // ------------------------------------------------------------
        // 业务操作
        // ------------------------------------------------------------

        public void SaveFromEditor(TodoEditorViewModel vm, TodoItem original)
        {
            try
            {
                if (vm == null) return;
                var item = vm.Working;

                // 重复待办要注意：界面上看到的是 Expand() 生成的临时实例，
                // 它的 Date 是「展开到的那一天」，不是主记录的起始日期。
                // 如果直接存回去，主记录的起始日期就被改乱，导致重复标签/展开异常。
                // 这里以主记录为基准，只合并用户改动的内容。
                TodoItem master = original != null ? Store.FindRaw(original.Id) : null;
                bool isRecurring = master != null && master.Repeat != RepeatMode.None;
                if (isRecurring)
                {
                    item.Id = master.Id;
                    item.CreatedAt = master.CreatedAt;
                    if (item.Repeat == master.Repeat)
                        item.Date = master.Date;   // 重复规则没变，起始日期不能动
                }

                if (original != null && item.Date == original.Date && item.Repeat == original.Repeat &&
                    item.Start == original.Start && item.LeadMinutes == original.LeadMinutes)
                {
                    // 时间没变时保留原有提醒状态，不重复提醒
                    item.ReminderSent = original.ReminderSent;
                    item.StartNotificationSent = original.StartNotificationSent;
                }
                Store.Upsert(item, master?.Id ?? original?.Id);
                Scheduler.OnDataChanged();
                Log.Write("保存待办: " + item.Date + " " + item.Start + " " + item.Title +
                          (item.Repeat != RepeatMode.None ? "（重复：" + item.RepeatText + "）" : ""));
            }
            catch (Exception ex)
            {
                Log.Error("保存待办失败", ex);
                MessageBox.Show("保存失败：" + ex.Message, "待办小助手", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public void ToggleDone(TodoItem item, bool done)
        {
            try
            {
                if (item.Repeat != RepeatMode.None)
                    Store.CompleteRecurringInstance(item, done, DateTime.Today);
                else
                    Store.SetDone(item.Id, done);
                Scheduler.OnDataChanged();
            }
            catch (Exception ex) { Log.Error("勾选失败", ex); }
        }

        /// <summary>切换主题并保存到设置</summary>
        public void ApplyTheme(AppTheme theme)
        {
            try
            {
                Settings.Current.ThemeMode = (int)theme;
                Settings.Save();
                ThemeManager.Apply(theme);
                _mainWindow?.OnThemeChanged();
            }
            catch (Exception ex) { Log.Error("切换主题失败", ex); }
        }

        /// <summary>按日期新建待办（日历里点某一天时用）</summary>
        public void EditItemForDay(DateTime day)
        {
            try
            {
                EnsureMainWindow();
                ShowMainWindow();
                int offset = (int)(day.Date - DateTime.Today).TotalDays;
                if (offset >= 0 && offset < 5)
                {
                    _mainWindow?.OpenEditor(null, offset);
                }
                else
                {
                    _mainWindow?.OpenEditorOnDate(day.Date);
                }
            }
            catch (Exception ex) { Log.Error("按日期新建待办失败", ex); }
        }
        /// <summary>打开某条待办的编辑框（供 DDL 列表等入口调用）</summary>
        public void EditItem(Models.TodoItem item)
        {
            try
            {
                EnsureMainWindow();
                ShowMainWindow();
                int offset = (item.StartTime?.Date ?? DateTime.Today).Date == DateTime.Today
                    ? 0
                    : (int)((item.StartTime?.Date ?? DateTime.Today).Date - DateTime.Today).TotalDays;
                _mainWindow?.OpenEditor(item, Math.Max(0, Math.Min(4, offset)));
            }
            catch (Exception ex) { Log.Error("打开待办编辑框失败", ex); }
        }

        public void ApplySettings(AppSettings updated, bool restartPhone = false)
        {
            var s = Settings.Current;
            bool portChanged = s.PhonePort != updated.PhonePort;
            bool enabledChanged = s.PhoneServerEnabled != updated.PhoneServerEnabled;
            bool startupChanged = s.RunAtStartup != updated.RunAtStartup;
            bool themeChanged = s.ThemeMode != updated.ThemeMode;

            Settings.Current = updated;
            Settings.Save();

            // 主题变化：立即应用（无需重启）
            if (themeChanged)
            {
                try { ThemeManager.Apply((AppTheme)updated.ThemeMode); }
                catch (Exception ex) { Log.Error("应用主题失败", ex); }
            }

            try
            {
                if (startupChanged)
                {
                    ShortcutHelper.SetRunKey(updated.RunAtStartup, ShortcutHelper.ExePath());
                    if (updated.RunAtStartup) ShortcutHelper.CreateStartupShortcut(ShortcutHelper.ExePath());
                    else ShortcutHelper.RemoveStartupShortcut();
                }
            }
            catch (Exception ex) { Log.Error("应用开机自启设置失败", ex); }

            if (restartPhone || portChanged || enabledChanged) ReloadPhoneServer();

            Scheduler.OnDataChanged();
            _mainWindow?.Refresh();
            Log.Write("设置已保存");
        }

        public void ReloadPhoneServer()
        {
            PhoneServer?.Stop();
            if (Settings.Current.PhoneServerEnabled) StartPhoneServer();
        }

        private void StartPhoneServer()
        {
            try
            {
                int port = Settings.Current.PhonePort;
                bool ok = PhoneServer.Start(port);
                if (!ok && port != 18974)
                {
                    // 默认端口失败时自动换一个
                    Log.Write("端口 " + port + " 启动失败，尝试 18974");
                    if (PhoneServer.Start(18974))
                    {
                        Settings.Current.PhonePort = 18974;
                        Settings.Save();
                        ok = true;
                    }
                }
                if (!ok) Log.Write("手机服务器未能启动: " + PhoneServer.LastError);
            }
            catch (Exception ex) { Log.Error("启动手机服务器失败", ex); }
        }

        public bool RestartPhoneServer()
        {
            ReloadPhoneServer();
            return PhoneServerRunning;
        }

        public void OpenPhonePage()
        {
            try
            {
                if (!PhoneServerRunning)
                {
                    MessageBox.Show("手机同步服务器未启动，请先在“手机同步”里检查端口。", "手机同步",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Process.Start(new ProcessStartInfo(PhoneUrl) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error("打开手机网页失败", ex); }
        }

        // ------------------------------------------------------------
        // 退出
        // ------------------------------------------------------------

        public void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            Log.Write("正在退出…");
            try { _hotkey?.Dispose(); } catch { }
            try { Scheduler?.Stop(); } catch { }
            try { PhoneServer?.Stop(); } catch { }
            try
            {
                _tray?.Dispose();
            }
            catch { }
            try { _mainWindow?.ForceClose(); } catch { }
            Store?.Save();
            Log.Write("已退出");
            Shutdown();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _tray?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }

    /// <summary>解析命令行参数（用于自检等命令）</summary>
    public sealed class CommandLineArgs
    {
        public string[] Raw { get; }
        public CommandLineArgs(string[] raw) { Raw = raw ?? Array.Empty<string>(); }
        public bool Has(string name) => Array.Exists(Raw, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>响铃状态（托盘图标变红等）</summary>
    public sealed class AlarmState
    {
        public string PlayingText { get; private set; }
        public DateTime? StartedAt { get; private set; }
        public event EventHandler Changed;

        public void MarkPlaying(string text)
        {
            PlayingText = text;
            StartedAt = DateTime.Now;
            Changed?.Invoke(this, EventArgs.Empty);
            var timer = new Timer(_ =>
            {
                PlayingText = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }, null, 20000, Timeout.Infinite);
        }
    }
}
