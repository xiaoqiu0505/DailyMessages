using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using TodoAssistant.Models;
using TodoAssistant.Services;
using TodoAssistant.ViewModels;
using TodoAssistant.Views.Dialogs;

namespace TodoAssistant.Views
{
    public partial class MainWindow : Window
    {
        private readonly App _app;
        private readonly DispatcherTimer _uiTimer;
        private bool _refreshing;
        private bool _reallyClose;

        /// <summary>显示的天数（今天起共几天）。改这里就能变 5 天 / 7 天。</summary>
        private const int DayCount = 5;

        private readonly List<DayColumnFactory.ColumnParts> _columns = new List<DayColumnFactory.ColumnParts>();

        public MainWindow(App app)
        {
            _app = app;
            InitializeComponent();

            BuildDayColumns();

            // 定时刷新时钟与「还有 N 分钟」
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += (s, e) => TickUi();
            _uiTimer.Start();

            // 托盘左键/快捷键打开时刷新
            Activated += (s, e) => Refresh();

            // 窗口大小变化时重算列最小宽度（保证 5 列都能看到）
            SizeChanged += (s, e) => UpdateColumnMinWidth();
            // 首次布局完成后再校正一次（此时 ScrollViewer 的视口宽度才是真实值）
            Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(UpdateColumnMinWidth),
                System.Windows.Threading.DispatcherPriority.Loaded);

            // 深色标题栏（Windows 10/11 支持则生效）
            SourceInitialized += (s, e) => TrySetImmersiveDarkMode();

            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        public void Refresh()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                UpdateHeader();
                for (int i = 0; i < _columns.Count; i++)
                    RefreshColumn(i);
            }
            finally
            {
                _refreshing = false;
            }
        }

        /// <summary>
        /// 列宽策略：五列（或任意列数）按比例均分，不设最小宽度。
        /// 这样内容宽度永远等于视口宽度，数学上不可能溢出，也就不会冒出横向滚动条。
        /// 窗口太窄时列会变窄但仍在一屏内（文字自动截断），比出现滚动条更符合预期。
        /// </summary>
        private void UpdateColumnMinWidth()
        {
            if (DayColumnsHost == null) return;
            foreach (var cd in DayColumnsHost.ColumnDefinitions)
            {
                cd.MinWidth = 0;
                cd.Width = new GridLength(1, GridUnitType.Star);
            }
        }

        /// <summary>按 DayCount 动态生成「今天/明天/后天/…」几列</summary>
        private void BuildDayColumns()
        {
            while (DayColumnsHost.ColumnDefinitions.Count > DayCount)
                DayColumnsHost.ColumnDefinitions.RemoveAt(DayColumnsHost.ColumnDefinitions.Count - 1);
            while (DayColumnsHost.ColumnDefinitions.Count < DayCount)
                DayColumnsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (var cd in DayColumnsHost.ColumnDefinitions)
            {
                // 只按比例均分，不设最小宽度，内容不可能超宽
                cd.Width = new GridLength(1, GridUnitType.Star);
                cd.MinWidth = 0;
            }

            _columns.Clear();
            DayColumnsHost.Children.Clear();
            for (int i = 0; i < DayCount; i++)
            {
                var parts = DayColumnFactory.Create(i, DayCount);
                Grid.SetColumn(parts.Root, i);
                parts.List.ItemTemplate = (DataTemplate)FindResource("TodoCardTemplate");
                int offset = i;
                parts.AddTopButton.Click += (s, e) => AddDay(offset);
                parts.AddBottomButton.Click += (s, e) => AddDay(offset);
                // 视图切换：卡片 ⇄ 时间表
                parts.ViewToggleButton.Click += (s, e) => ToggleColumnView(offset);
                // 时间表里点任务块 -> 打开编辑
                parts.Schedule.ItemClicked += item => OpenEditor(item, offset);
                DayColumnsHost.Children.Add(parts.Root);
                _columns.Add(parts);
            }
        }

        private void UpdateHeader()
        {
            var now = DateTime.Now;
            try { ClockText.Text = now.ToString("HH:mm:ss"); } catch { }

            // 快照/预览模式下没有 Scheduler，做个保护
            if (_app?.Scheduler != null)
            {
                var (open, total) = _app.Scheduler.TodaySummary();
                TodayStatText.Text = total == 0 ? "今天还没有待办" : $"今天 {total} 项待办，还有 {open} 项未完成";
            }

            var next = _app?.NextReminder() ?? null;
            if (next.HasValue)
                NextReminderText.Text = next.Value.Item2;
            else
                NextReminderText.Text = $"接下来 {DayCount} 天内没有需要提醒的待办";

            SubtitleText.Text = now.ToString("yyyy 年 M 月 d 日 dddd", new System.Globalization.CultureInfo("zh-CN"));

            bool phoneOn = _app != null && _app.PhoneServerRunning;
            PhoneDot.Fill = phoneOn ? (Brush)FindResource("GreenBrush") : new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            PhoneStatusText.Text = phoneOn
                ? "手机同步：" + _app.PhoneUrl
                : "手机同步未启动";
            PhoneStatusText.Foreground = phoneOn ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush");

            UpdateDdlSummary();
            UpdateThemeButton();
        }

        /// <summary>顶部 DDL 摘要：显示最近的截止时间，过期的醒目提示</summary>
        private void UpdateDdlSummary()
        {
            if (DdlSummaryText == null) return;
            try
            {
                var items = _app?.Deadline?.GetOpenDeadlines();
                if (items == null || items.Count == 0)
                {
                    DdlSummaryText.Text = "无 DDL";
                    return;
                }
                var now = DateTime.Now;
                int overdue = items.Count(i => i.DeadlineAt.Value < now);
                var first = items[0];
                string tip = first.DeadlineAt.Value.ToString("MM月dd日 HH:mm");
                DdlSummaryText.Text = overdue > 0
                    ? $"{overdue} 个 DDL 已过期"
                    : $"最近 DDL：{tip}";
            }
            catch (Exception ex)
            {
                Log.Error("刷新 DDL 摘要失败", ex);
            }
        }

        private void UpdateThemeButton()
        {
            if (ThemeIcon == null) return;
            ThemeIcon.Text = ThemeManager.IsDark ? "☀️" : "🌙";
            ThemeButton.ToolTip = ThemeManager.IsDark ? "切换到浅色模式" : "切换到深色模式";
        }

        /// <summary>点击顶部月亮/太阳图标：浅色/深色切换</summary>
        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            var next = ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark;
            _app.ApplyTheme(next);
        }

        /// <summary>点击顶部 DDL 摘要：列出所有 DDL</summary>
        private void ShowDdl_Click(object sender, MouseButtonEventArgs e)
        {
            var dlg = new DdlListWindow(_app) { Owner = this };
            dlg.ShowDialog();
            Refresh();
        }

        private void RefreshColumn(int offset)
        {
            var day = DateTime.Today.AddDays(offset);
            if (offset < 0 || offset >= _columns.Count) return;
            var col = _columns[offset];
            col.DateText.Text = day.ToString("M月d日") + " 周" + "日一二三四五六"[(int)day.DayOfWeek];

            var raw = _app?.Store != null ? _app.Store.GetRange(day, day) : new List<TodoItem>();
            var rows = raw.Select(m => new TodoRowViewModel(m, offset)).ToList();

            var list = col.List;
            // 内容没变就不重建，避免闪烁
            if (list.Items.Count == rows.Count)
            {
                bool same = true;
                for (int i = 0; i < rows.Count; i++)
                {
                    var cur = list.Items[i] as TodoRowViewModel;
                    if (cur == null || cur.Model.Id != rows[i].Model.Id || cur.Model.Date != rows[i].Model.Date ||
                        cur.Model.Done != rows[i].Model.Done || cur.Title != rows[i].Title ||
                        cur.TimeText != rows[i].TimeText || cur.Notes != rows[i].Notes)
                    { same = false; break; }
                }
                if (same)
                {
                    foreach (var r in rows) r.Refresh();
                }
                else
                {
                    list.ItemsSource = rows;
                }
            }
            else
            {
                list.ItemsSource = rows;
            }

            col.EmptyText.Text = rows.Count == 0 ? DayColumnFactory.DayName(offset) + "还没有安排，点右下角添加" : "";
            col.EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 未完成数量角标（每列都显示）
            int openCount = raw.Count(i => !i.Done);
            col.CountText.Text = openCount.ToString();
            col.CountBadge.Visibility = openCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            // 时间表视图同步（只在该列处于时间表模式时更新，省开销）
            if (col.IsScheduleMode && col.Schedule != null)
                col.Schedule.SetItems(raw, offset == 0);
        }

        /// <summary>切换某列的「卡片 / 时间表」视图</summary>
        /// <summary>供快照/调试用：切换某列的视图模式</summary>
        public void ToggleColumnViewPublic(int offset) => ToggleColumnView(offset);

        private void ToggleColumnView(int offset)
        {
            if (offset < 0 || offset >= _columns.Count) return;
            var col = _columns[offset];
            col.IsScheduleMode = !col.IsScheduleMode;

            if (col.IsScheduleMode)
            {
                col.CardScroll.Visibility = Visibility.Collapsed;
                col.EmptyText.Visibility = Visibility.Collapsed;
                col.Schedule.Visibility = Visibility.Visible;
                col.ViewToggleButton.Content = "卡片";
                col.ViewToggleButton.ToolTip = "切换回卡片列表视图";
                var day = DateTime.Today.AddDays(offset);
                col.Schedule.SetItems(_app.Store.GetRange(day, day), offset == 0);
            }
            else
            {
                col.Schedule.Visibility = Visibility.Collapsed;
                col.CardScroll.Visibility = Visibility.Visible;
                col.ViewToggleButton.Content = "时间表";
                col.ViewToggleButton.ToolTip = "切换为按时间排布的「时间表」视图";
                RefreshColumn(offset);
            }
        }

        private void TickUi()
        {
            try
            {
                if (!IsVisible) return;
                ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
                foreach (var col in _columns)
                {
                    var list = col.List;
                    if (list.ItemsSource == null) continue;
                    foreach (var it in list.ItemsSource)
                        if (it is TodoRowViewModel row) row.Refresh();
                }
                var next = _app.NextReminder();
                NextReminderText.Text = next.HasValue ? next.Value.Item2 : $"接下来 {DayCount} 天内没有需要提醒的待办";
            }
            catch (Exception ex)
            {
                Log.Error("刷新界面失败", ex);
            }
        }

        // ---------------- 交互 ----------------

        private void AddDay(int offset) => OpenEditor(null, offset);

        private void QuickAdd_Click(object sender, RoutedEventArgs e) => AddDay(0);

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is TodoRowViewModel row)
            {
                e.Handled = true;
                OpenEditor(row.Model, row.DayOffset);
            }
        }

        private void DoneCheck_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox cb) || !(cb.Tag is TodoRowViewModel row)) return;
            bool target = !row.Model.Done;
            _app.ToggleDone(row.Model, target);
            e.Handled = true;
        }

        private void DeleteItem_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is TodoRowViewModel row)) return;
            e.Handled = true;
            var result = MessageBox.Show(this, $"删除「{row.Model.Title}」？\n\n{row.Model.Date} {row.Model.Start}",
                "删除待办", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;
            if (row.Model.Repeat != RepeatMode.None && row.Model.Date != DateTime.Today.ToString("yyyy-MM-dd"))
            {
                // 删除重复待办的单次：保留主记录
                _app.Store.Delete(row.Model.Id);
            }
            else
            {
                var confirm = row.Model.Repeat != RepeatMode.None
                    ? MessageBox.Show(this, "这是一条重复待办，删除后所有未来出现都会被取消。确定删除？",
                        "删除重复待办", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                    : MessageBoxResult.Yes;
                if (confirm != MessageBoxResult.Yes) return;
                _app.Store.Delete(row.Model.Id);
            }
            _app.Scheduler.OnDataChanged();
            Refresh();
        }

        public void OpenEditor(TodoItem existing, int offset)
        {
            var settings = _app.Settings.Current;
            var editor = new TodoEditorViewModel();
            editor.Initialize(existing, offset, settings.LeadMinutes);
            var dlg = new TodoEditorWindow(editor) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _app.SaveFromEditor(editor, existing);
                Refresh();
            }
        }

        /// <summary>在指定日期新建待办（超出 5 天范围时用）</summary>
        public void OpenEditorOnDate(DateTime day)
        {
            var settings = _app.Settings.Current;
            var editor = new TodoEditorViewModel();
            editor.Initialize(null, 0, settings.LeadMinutes);
            editor.SelectedDate = day.Date;
            var dlg = new TodoEditorWindow(editor) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _app.SaveFromEditor(editor, null);
                Refresh();
            }
        }

        private void OpenBigCalendar_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new BigCalendarWindow(_app) { Owner = this };
            dlg.ShowDialog();
            Refresh();
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (ctrl && e.Key == Key.N) { AddDay(0); e.Handled = true; }
            else if (e.Key == Key.F5) { _app.Scheduler.OnDataChanged(); Refresh(); e.Handled = true; }
            else if (e.Key == Key.Escape && !ctrl) { Hide(); e.Handled = true; }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow(_app.Settings.Current, _app.SettingsDataDir) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _app.ApplySettings(dlg.Result);
                Refresh();
            }
        }

        private void Phone_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new PhoneWindow(_app) { Owner = this };
            dlg.ShowDialog();
            Refresh();
        }

        private void OpenPhonePage_Click(object sender, RoutedEventArgs e) => _app.OpenPhonePage();

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _app.DataDirectory + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("打开数据文件夹失败", ex); }
        }

        private void TestAlert_Click(object sender, RoutedEventArgs e) => _app.TestAlert();

        private void Minimize_Click(object sender, RoutedEventArgs e) => Hide();

        private void Close_Click(object sender, RoutedEventArgs e) => Hide();

        /// <summary>最大化 / 还原</summary>
        private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            UpdateMaximizeIcon();
        }

        /// <summary>最大化时图标变成「还原」的双框</summary>
        private void UpdateMaximizeIcon()
        {
            if (MaximizeIcon == null) return;
            MaximizeIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
                ? "M 4.5,0.5 L 13.5,0.5 L 13.5,9.5 M 0.5,4.5 L 9.5,4.5 L 9.5,13.5 L 0.5,13.5 Z"
                : "M 2.5,2.5 L 13.5,2.5 L 13.5,13.5 L 2.5,13.5 Z");
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (e.ClickCount == 2)
                {
                    ToggleMaximize();
                    return;
                }
                try { DragMove(); } catch { }
            }
        }

        /// <summary>真正退出时调用</summary>
        public void ForceClose()
        {
            _reallyClose = true;
            _uiTimer.Stop();
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_reallyClose)
            {
                e.Cancel = true;
                Hide();
                _app.FlashTrayHint();
            }
            base.OnClosing(e);
        }

        /// <summary>主题切换后刷新窗口（标题栏深色、卡片样式等）</summary>
        public void OnThemeChanged()
        {
            ApplyWindowTheme();
            Refresh();
        }

        /// <summary>按当前主题给窗口背景和标题栏上色</summary>
        public void ApplyWindowTheme()
        {
            try
            {
                Background = (Brush)FindResource("WinBackgroundBrush");
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int on = ThemeManager.IsDark ? 1 : 0;
                // DWMWA_USE_IMMERSIVE_DARK_MODE = 20（旧版本为 19），失败不影响功能
                DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
                if (on == 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch { }
        }

        private void TrySetImmersiveDarkMode() => ApplyWindowTheme();

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
