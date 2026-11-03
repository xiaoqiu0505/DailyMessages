using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TodoAssistant.Models;
using TodoAssistant.Services;

namespace TodoAssistant.Views.Dialogs
{
    /// <summary>列出所有未完成的 DDL，按截止时间排序；点一条可直接跳到主窗口编辑</summary>
    public partial class DdlListWindow : Window
    {
        private readonly App _app;

        public DdlListWindow(App app)
        {
            _app = app;
            InitializeComponent();
            Background = (Brush)FindResource("CardBrush");
            RefreshList();
        }

        private void RefreshList()
        {
            ItemsPanel.Children.Clear();
            var items = _app.Deadline?.GetOpenDeadlines() ?? new System.Collections.Generic.List<TodoItem>();
            var now = DateTime.Now;

            int overdue = items.Count(i => i.DeadlineAt.Value < now);
            int today = items.Count(i => i.DeadlineAt.Value >= now && i.DeadlineAt.Value.Date == now.Date);
            SubtitleText.Text = items.Count == 0
                ? "还没有设置 DDL"
                : $"共 {items.Count} 个，{(overdue > 0 ? overdue + " 个已过期，" : "")}今天到期 {today} 个" +
                  $"　·　每天 {(string.IsNullOrWhiteSpace(_app.Settings.Current.DdlDigestMorning) ? "09:00" : _app.Settings.Current.DdlDigestMorning)} / " +
                  $"{((string.IsNullOrWhiteSpace(_app.Settings.Current.DdlDigestEvening) ? "21:00" : _app.Settings.Current.DdlDigestEvening))} 汇总提醒";

            EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var it in items)
            {
                ItemsPanel.Children.Add(BuildRow(it, now));
            }
        }

        private Border BuildRow(TodoItem item, DateTime now)
        {
            var at = item.DeadlineAt.Value;
            bool isOverdue = at < now;
            bool isToday = !isOverdue && at.Date == now.Date;

            var card = new Border
            {
                Background = (Brush)FindResource(isOverdue ? "PastBgBrush" : isToday ? "SoonBgBrush" : "ChipBgBrush"),
                BorderBrush = (Brush)FindResource(isOverdue ? "RedBrush" : isToday ? "AmberBrush" : "LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                Tag = item,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextBrush"),
            });
            var sub = new TextBlock
            {
                Text = $"截止 {at:MM月dd日 HH:mm}　·　{DeadlineService.RelativeText(at, now)}",
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = (Brush)FindResource(isOverdue ? "RedBrush" : "MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
            };
            stack.Children.Add(sub);
            if (!string.IsNullOrWhiteSpace(item.Notes))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = item.Notes,
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0),
                    Foreground = (Brush)FindResource("MutedBrush"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            Grid.SetColumn(stack, 0);
            grid.Children.Add(stack);

            var status = new TextBlock
            {
                Text = isOverdue ? "已过期" : isToday ? "今天" : "",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                Foreground = (Brush)FindResource(isOverdue ? "RedBrush" : "AmberBrush"),
                FontWeight = FontWeights.SemiBold,
            };
            Grid.SetColumn(status, 1);
            grid.Children.Add(status);

            card.Child = grid;
            card.MouseLeftButtonUp += (s, e) =>
            {
                if (card.Tag is TodoItem target)
                {
                    DialogResult = false;
                    Close();
                    _app.EditItem(target);
                }
            };
            return card;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { DragMove(); } catch { }
            }
        }
    }
}
