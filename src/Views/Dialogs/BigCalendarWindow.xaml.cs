using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TodoAssistant.Models;
using TodoAssistant.Services;

namespace TodoAssistant.Views.Dialogs
{
    /// <summary>
    /// 大日历：按月展示，可切换年月；重点用粉色标记与右侧列表展示 DDL。
    /// </summary>
    public partial class BigCalendarWindow : Window
    {
        private readonly App _app;
        private DateTime _month;       // 当月 1 号

        public BigCalendarWindow(App app, DateTime? focus = null)
        {
            _app = app;
            InitializeComponent();
            var d = (focus ?? DateTime.Today).Date;
            _month = new DateTime(d.Year, d.Month, 1);
            RebuildDdlIndex();
            RefreshCalendar();
        }

        // ---------------- 数据 ----------------

        /// <summary>
        /// 取某天要显示的内容：
        /// todos = 那天安排的任务（含重复展开）；
        /// ddl   = 截止时间落在这一天的未完成任务。
        /// 注意 DDL 日期可能和任务日期不同（比如 11月7日的任务、11月9日才到期），
        /// 所以 DDL 必须单独按「截止日期」查询，不能只在当天任务里找，否则会漏掉。
        /// </summary>
        private (List<TodoItem> todos, List<TodoItem> ddl) LoadDay(DateTime day)
        {
            var todos = _app.Store.GetRange(day, day).ToList();
            var ddl = _ddlByDate.TryGetValue(day.Date, out var list) ? list : new List<TodoItem>();
            return (todos, ddl);
        }

        /// <summary>所有未完成 DDL，按「截止日期」分组（构造时算一次，翻月时重算）</summary>
        private Dictionary<DateTime, List<TodoItem>> _ddlByDate = new Dictionary<DateTime, List<TodoItem>>();

        private void RebuildDdlIndex()
        {
            var dict = new Dictionary<DateTime, List<TodoItem>>();
            foreach (var it in _app.Deadline?.GetOpenDeadlines() ?? new List<TodoItem>())
            {
                var at = it.DeadlineAt;
                if (!at.HasValue) continue;
                var d = at.Value.Date;
                if (!dict.TryGetValue(d, out var lst)) dict[d] = lst = new List<TodoItem>();
                lst.Add(it);
            }
            _ddlByDate = dict;
        }

        // ---------------- 渲染 ----------------

        private void RefreshCalendar()
        {
            RebuildDdlIndex();
            MonthTitle.Text = _month.ToString("yyyy 年 M 月");

            CalendarGrid.Children.Clear();
            // 周一为一周开始
            int offset = ((int)_month.DayOfWeek + 6) % 7;   // 周日=0 -> 6；周一=1 -> 0
            DateTime start = _month.AddDays(-offset);
            var today = DateTime.Today;
            var allDdl = new List<(DateTime day, TodoItem item)>();

            for (int i = 0; i < 42; i++)
            {
                var day = start.AddDays(i);
                var (todos, ddl) = LoadDay(day);
                foreach (var d in ddl) allDdl.Add((day, d));
                CalendarGrid.Children.Add(BuildDayCell(day, todos, ddl, today));
            }

            // 右侧 DDL 列表
            BuildDdlList(allDdl);
            DdlCountText.Text = $"本月 DDL {allDdl.Count} 个";
        }

        private Border BuildDayCell(DateTime day, List<TodoItem> todos, List<TodoItem> ddl, DateTime today)
        {
            bool inMonth = day.Month == _month.Month && day.Year == _month.Year;
            bool isToday = day.Date == today;
            bool hasDdl = ddl.Count > 0;
            bool ddlOverdue = ddl.Any(d => d.DeadlineAt.Value < DateTime.Now);
            int openCount = todos.Count(t => !t.Done);

            var cell = new Border
            {
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(hasDdl ? 1.5 : 1),
                Padding = new Thickness(6, 4, 6, 4),
                Cursor = Cursors.Hand,
                Tag = day.Date,
                // 有 DDL 的日期用醒目的红色覆盖（与卡片 DDL 标签同色号 #EF4444）
                Background = hasDdl
                    ? new SolidColorBrush(Color.FromArgb(ddlOverdue ? (byte)0x3A : (byte)0x26, 0xEF, 0x44, 0x44))
                    : (Brush)FindResource(inMonth ? "CardBrush" : "WinBackgroundBrush"),
                BorderBrush = (Brush)FindResource(
                    hasDdl ? "RedBrush" :
                    isToday ? "AccentBrush" : "LineBrush"),
                Opacity = inMonth ? 1.0 : 0.45,
            };

            var stack = new StackPanel();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dayText = new TextBlock
            {
                Text = day.Day.ToString(),
                FontSize = 13,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                Foreground = (Brush)FindResource(isToday ? "AccentBrush" : inMonth ? "TextBrush" : "MutedBrush"),
            };
            head.Children.Add(dayText);

            if (isToday)
            {
                var tag = new Border
                {
                    Background = (Brush)FindResource("AccentBrush"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(5, 0, 5, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Child = new TextBlock { Text = "今天", FontSize = 9.5, Foreground = Brushes.White },
                };
                Grid.SetColumn(tag, 1);
                head.Children.Add(tag);
            }
            else if (hasDdl)
            {
                var tag = new Border
                {
                    Background = (Brush)FindResource("RedBrush"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(5, 0, 5, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Child = new TextBlock
                    {
                        Text = "DDL" + (ddl.Count > 1 ? " " + ddl.Count : ""),
                        FontSize = 9.5,
                        Foreground = Brushes.White,
                    },
                };
                Grid.SetColumn(tag, 1);
                head.Children.Add(tag);
            }
            stack.Children.Add(head);

            // 最多显示 2 条摘要
            foreach (var t in todos.Where(x => !x.Done).Take(2))
            {
                bool isDdlItem = ddl.Contains(t);
                stack.Children.Add(new TextBlock
                {
                    Text = (isDdlItem ? "⏳ " : "· ") + t.Title,
                    FontSize = 10.5,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    Foreground = (Brush)FindResource(isDdlItem ? "RedBrush" : "MutedBrush"),
                    FontWeight = isDdlItem ? FontWeights.SemiBold : FontWeights.Normal,
                });
            }
            if (openCount > 2)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"+{openCount - 2} 条",
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0),
                    Foreground = (Brush)FindResource("FaintBrush"),
                });
            }

            cell.Child = stack;
            cell.MouseLeftButtonUp += DayCell_Click;
            cell.ToolTip = ddl.Count > 0
                ? "DDL：" + string.Join("\n", ddl.Select(d => "· " + d.Title + "  " + d.DeadlineText))
                : (todos.Count > 0 ? string.Join("\n", todos.Select(t => "· " + t.Start + " " + t.Title)) : "没有安排");
            return cell;
        }

        private void BuildDdlList(List<(DateTime day, TodoItem item)> ddls)
        {
            DdlListPanel.Children.Clear();
            var now = DateTime.Now;
            var ordered = ddls.OrderBy(x => x.day).ToList();
            DdlEmptyText.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var (day, item) in ordered)
            {
                var at = item.DeadlineAt.Value;
                bool overdue = at < now;
                var card = new Border
                {
                    Background = (Brush)FindResource(overdue ? "PastBgBrush" : "ChipBgBrush"),
                    BorderBrush = (Brush)FindResource("RedBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 8),
                    Cursor = Cursors.Hand,
                    Tag = item,
                };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock
                {
                    Text = item.Title,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                sp.Children.Add(new TextBlock
                {
                    Text = $"{at:MM月dd日 HH:mm} · {DeadlineService.RelativeText(at, now)}",
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0),
                    Foreground = (Brush)FindResource(overdue ? "RedBrush" : "MutedBrush"),
                });
                card.Child = sp;
                card.MouseLeftButtonUp += DdlItem_Click;
                DdlListPanel.Children.Add(card);
            }
        }

        // ---------------- 交互 ----------------

        private void PrevMonth_Click(object sender, RoutedEventArgs e) { _month = _month.AddMonths(-1); RefreshCalendar(); }
        private void NextMonth_Click(object sender, RoutedEventArgs e) { _month = _month.AddMonths(1); RefreshCalendar(); }
        private void PrevYear_Click(object sender, RoutedEventArgs e) { _month = _month.AddYears(-1); RefreshCalendar(); }
        private void NextYear_Click(object sender, RoutedEventArgs e) { _month = _month.AddYears(1); RefreshCalendar(); }

        private void Today_Click(object sender, RoutedEventArgs e)
        {
            _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            RefreshCalendar();
        }

        private void DayCell_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is DateTime day)
            {
                int offset = (int)(day.Date - DateTime.Today).TotalDays;
                if (offset >= 0 && offset < 5)
                {
                    // 在 5 天范围内：直接关掉日历，主窗口已能看到
                    Close();
                }
                else
                {
                    Close();
                    _app.EditItemForDay(day.Date);
                }
            }
        }

        private void DdlItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is TodoItem item)
            {
                Close();
                _app.EditItem(item);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>按 Esc 也能关闭（点击按钮失效时的兜底）</summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                // 双击标题栏 = 最大化 / 还原
                if (e.ClickCount == 2)
                {
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    return;
                }
                try { DragMove(); } catch { }
            }
        }
    }
}
