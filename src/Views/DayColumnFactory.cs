using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace TodoAssistant.Views
{
    /// <summary>
    /// 主窗口的「某一天」列模板（代码生成，天数组数可变，不再是固定 3 列）。
    /// 生成后由 MainWindow 把 ItemsControl / 空提示 / 日期文本 / 计数交给 ColumnHost 使用。
    /// </summary>
    internal static class DayColumnFactory
    {
        /// <summary>一列里需要 MainWindow 后续填充/更新的控件</summary>
        internal sealed class ColumnParts
        {
            public Border Root;
            public Grid Column;
            public ItemsControl List;
            public TextBlock EmptyText;
            public TextBlock DateText;
            public TextBlock TitleText;
            public TextBlock CountText;
            public Border CountBadge;
            public Button AddTopButton;
            public Button AddBottomButton;
            /// <summary>卡片视图（默认）</summary>
            public ScrollViewer CardScroll;
            /// <summary>时间表视图</summary>
            public Controls.DayScheduleView Schedule;
            /// <summary>切换视图按钮（卡片 / 时间表）</summary>
            public Button ViewToggleButton;
            /// <summary>当前是否时间表视图</summary>
            public bool IsScheduleMode;
        }

        public static ColumnParts Create(int offset, int total)
        {
            var parts = new ColumnParts();

            var list = new ItemsControl { Margin = new Thickness(8, 0, 4, 0) };

            var stack = new StackPanel();
            stack.Children.Add(list);
            var empty = new TextBlock
            {
                Text = DayName(offset) + "还没有安排",
                FontSize = 12.5,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stack.Children.Add(empty);

            var listScroll = new ScrollViewer
            {
                Margin = new Thickness(0, 0, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack,
            };

            // 顶部：今天 / 日期 / 计数 / 加号
            var title = new TextBlock { Text = DayName(offset) };
            title.SetResourceReference(TextBlock.StyleProperty, "DayHeaderStyle");

            var date = new TextBlock { Text = "", FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            date.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

            var countText = new TextBlock { Text = "0", FontSize = 11 };
            countText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var badge = new Border
            {
                Background = (System.Windows.Media.Brush)Application.Current.TryFindResource("AccentSoftBrush"),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 1, 7, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Child = countText,
            };

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(title);
            titleRow.Children.Add(date);
            titleRow.Children.Add(badge);

            var addTop = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Content = "+",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                ToolTip = "添加" + DayName(offset) + "的待办",
            };
            addTop.SetResourceReference(FrameworkElement.StyleProperty, "AddButton");

            // 视图切换：卡片 ⇄ 时间表
            var viewToggle = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 30, 0),
                Padding = new Thickness(7, 2, 7, 2),
                FontSize = 10.5,
                Content = "时间表",
                ToolTip = "切换为按时间排布的「时间表」视图",
                Cursor = Cursors.Hand,
            };
            viewToggle.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");

            var header = new Grid { Margin = new Thickness(14, 12, 10, 8) };
            header.Children.Add(titleRow);
            header.Children.Add(viewToggle);
            header.Children.Add(addTop);

            // 底部按钮
            var addBottom = new Button
            {
                Margin = new Thickness(12, 6, 12, 12),
                Content = "+ 添加" + DayName(offset) + "的待办",
            };
            addBottom.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");

            // 整列：上中下三块
            var colGrid = new Grid();
            colGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            colGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            colGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(header, 0);
            Grid.SetRow(listScroll, 1);
            Grid.SetRow(addBottom, 2);
            // 时间表视图：和卡片区占据同一格，靠 Visibility 切换
            var schedule = new Controls.DayScheduleView
            {
                Margin = new Thickness(6, 0, 6, 0),
                Visibility = Visibility.Collapsed,
            };

            colGrid.Children.Add(header);
            colGrid.Children.Add(listScroll);
            Grid.SetRow(schedule, 1);
            colGrid.Children.Add(schedule);
            colGrid.Children.Add(addBottom);

            // 外框：列间距用「外层容器内边距」而不是每个 Border 的外边距，
            // 避免 5 列累计 6×5=30px 把内容撑出视口（横向滚动条就是这样冒出来的）
            var root = new Border
            {
                // 用动态资源，深色模式下才会跟着变（不能写死 White）
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(3, 4, 3, 8),
                Child = colGrid,
            };
            root.SetResourceReference(Border.BackgroundProperty, "CardBrush");
            root.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            // 注意：SetResourceReference 是动态绑定，主题切换时会自动更新

            parts.Root = root;
            parts.Column = colGrid;
            parts.List = list;
            parts.EmptyText = empty;
            parts.DateText = date;
            parts.TitleText = title;
            parts.CountText = countText;
            parts.CountBadge = badge;
            parts.AddTopButton = addTop;
            parts.AddBottomButton = addBottom;
            parts.CardScroll = listScroll;
            parts.Schedule = schedule;
            parts.ViewToggleButton = viewToggle;
            return parts;
        }

        public static string DayName(int offset) => Converters.DayNameConverter.Name(offset);
    }
}
