using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TodoAssistant.Models;
using TodoAssistant.ViewModels;

namespace TodoAssistant.Controls
{
    /// <summary>
    /// 每日时间表视图（类似课程表）：
    /// 左列是「节次 + 时间段」时间轴，右侧按任务的开始/结束时间把任务块铺在对应时间段上。
    /// 没有结束时间的任务默认占 1 小时；时间重叠的块会横向并排，不会互相遮挡。
    /// </summary>
    public sealed class DayScheduleView : UserControl
    {
        /// <summary>时间轴范围（小时）</summary>
        private const int StartHour = 6;
        private const int EndHour = 24;
        /// <summary>每小时的像素高度</summary>
        private const double HourHeight = 58;
        /// <summary>左侧时间轴宽度</summary>
        private const double AxisWidth = 54;

        private readonly Canvas _canvas;
        private readonly ScrollViewer _scroll;
        private readonly List<TodoItem> _items = new List<TodoItem>();
        private bool _scrolledToNow;

        /// <summary>点击任务块（参数为对应的待办）</summary>
        public event Action<TodoItem> ItemClicked;

        public DayScheduleView()
        {
            Focusable = false;

            _canvas = new Canvas { Background = Brushes.Transparent };
            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _canvas,
            };
            Content = _scroll;
            SizeChanged += (s, e) => Rebuild();
        }

        /// <summary>设置要显示的任务（已完成的不显示）</summary>
        public void SetItems(IEnumerable<TodoItem> items, bool isToday)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items.Where(i => !i.Done));
            _scrolledToNow = !isToday;
            Rebuild();
        }

        private void Rebuild()
        {
            if (_canvas == null) return;
            double width = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth;
            if (width <= AxisWidth) width = 320;
            double height = (EndHour - StartHour) * HourHeight;

            _canvas.Children.Clear();
            _canvas.Width = width;
            _canvas.Height = height;

            DrawGrid(width, height);
            DrawBlocks(width);

            if (!_scrolledToNow)
            {
                _scrolledToNow = true;
                // 滚到当前时间附近
                double nowY = (DateTime.Now.Hour + DateTime.Now.Minute / 60.0 - StartHour) * HourHeight;
                _scroll.ScrollToVerticalOffset(Math.Max(0, nowY - HourHeight * 1.5));
            }
        }

        private void DrawGrid(double width, double height)
        {
            Brush lineBrush = (Brush)TryFindResource("LineBrush") ?? Brushes.LightGray;
            Brush mutedBrush = (Brush)TryFindResource("MutedBrush") ?? Brushes.Gray;
            Brush faintBrush = (Brush)TryFindResource("FaintBrush") ?? Brushes.Gray;
            Brush softBrush = (Brush)TryFindResource("ChipBgBrush") ?? Brushes.WhiteSmoke;

            for (int h = StartHour; h <= EndHour; h++)
            {
                double y = (h - StartHour) * HourHeight;
                bool isNow = DateTime.Now.Hour == h;

                // 每小时分隔线
                var line = new Border
                {
                    Height = 1,
                    Width = width,
                    Background = lineBrush,
                    Opacity = 0.7,
                };
                Canvas.SetTop(line, y);
                Canvas.SetLeft(line, 0);
                _canvas.Children.Add(line);

                // 左侧时间标签（整点）
                var timeLabel = new TextBlock
                {
                    Text = h.ToString("00") + ":00",
                    FontSize = 10,
                    Foreground = mutedBrush,
                    Width = AxisWidth - 8,
                    TextAlignment = TextAlignment.Right,
                };
                Canvas.SetTop(timeLabel, y - 8);
                Canvas.SetLeft(timeLabel, 0);
                _canvas.Children.Add(timeLabel);

                // 左右分界竖线
                var vline = new Border
                {
                    Width = 1,
                    Height = HourHeight,
                    Background = lineBrush,
                };
                Canvas.SetTop(vline, y);
                Canvas.SetLeft(vline, AxisWidth);
                _canvas.Children.Add(vline);
            }

            // 每半小时的浅色小刻度（视觉更细）
            for (int h = StartHour; h < EndHour; h++)
            {
                double y = (h - StartHour) * HourHeight + HourHeight / 2;
                var half = new Border
                {
                    Height = 1,
                    Width = width - AxisWidth,
                    Background = lineBrush,
                    Opacity = 0.25,
                };
                Canvas.SetTop(half, y);
                Canvas.SetLeft(half, AxisWidth);
                _canvas.Children.Add(half);

                var halfLabel = new TextBlock
                {
                    Text = ":30",
                    FontSize = 8.5,
                    Foreground = faintBrush,
                    Width = AxisWidth - 8,
                    TextAlignment = TextAlignment.Right,
                    Opacity = 0.7,
                };
                Canvas.SetTop(halfLabel, y - 6);
                Canvas.SetLeft(halfLabel, 0);
                _canvas.Children.Add(halfLabel);
            }

            // 当前时间红线
            var now = DateTime.Now;
            if (now.Hour >= StartHour && now.Hour < EndHour)
            {
                double nowY = (now.Hour + now.Minute / 60.0 - StartHour) * HourHeight;
                var nowLine = new Border
                {
                    Height = 2,
                    Width = width - AxisWidth - 4,
                    Background = (Brush)TryFindResource("RedBrush") ?? Brushes.Red,
                    Opacity = 0.75,
                };
                Canvas.SetTop(nowLine, nowY);
                Canvas.SetLeft(nowLine, AxisWidth + 2);
                _canvas.Children.Add(nowLine);
            }
        }

        private void DrawBlocks(double width)
        {
            double laneWidth = width - AxisWidth - 6;
            if (laneWidth <= 40) laneWidth = 200;

            // 计算每个任务的时间区间
            var blocks = new List<(TodoItem item, double startH, double endH)>();
            foreach (var it in _items)
            {
                var start = it.StartTime;
                if (!start.HasValue) continue;
                double sh = start.Value.Hour + start.Value.Minute / 60.0;

                double eh;
                var end = it.EndTime;
                if (end.HasValue)
                {
                    // 跨零点或异常时兜底
                    double raw = (end.Value - start.Value).TotalHours;
                    eh = sh + (raw > 0 && raw <= 24 ? raw : 1);
                }
                else
                {
                    eh = sh + 1;   // 没结束时间默认 1 小时
                }

                if (eh <= StartHour || sh >= EndHour) continue;   // 完全在显示范围外
                blocks.Add((it, Math.Max(sh, StartHour), Math.Min(eh, EndHour)));
            }

            // 重叠检测：把时间上重叠的块分到不同车道
            blocks = blocks.OrderBy(b => b.startH).ThenBy(b => b.endH).ToList();
            var laneEnds = new List<double>();
            var laneOf = new int[blocks.Count];
            for (int i = 0; i < blocks.Count; i++)
            {
                int lane = -1;
                for (int l = 0; l < laneEnds.Count; l++)
                {
                    if (laneEnds[l] <= blocks[i].startH + 1e-6) { lane = l; break; }
                }
                if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(0); }
                laneEnds[lane] = blocks[i].endH;
                laneOf[i] = lane;
            }
            int totalLanes = Math.Max(1, laneEnds.Count);
            double laneW = laneWidth / totalLanes;

            // 配色循环
            var palettes = new[]
            {
                ("#1E3A5F", "#5B9BD5"),   // 蓝
                ("#1F3B33", "#4DB6AC"),   // 青
                ("#3B2B52", "#9C7BD8"),   // 紫
                ("#4A3520", "#E0A458"),   // 橙
                ("#3B2430", "#E07B9B"),   // 粉
            };
            bool dark = Services.ThemeManager.IsDark;

            for (int i = 0; i < blocks.Count; i++)
            {
                var (item, sh, eh) = blocks[i];
                double top = (sh - StartHour) * HourHeight;
                double hgt = Math.Max(24, (eh - sh) * HourHeight - 3);
                double left = AxisWidth + 3 + laneOf[i] * laneW;

                var (bgHex, accentHex) = palettes[i % palettes.Length];
                Brush bg = dark ? HexBrush(bgHex) : TintBrush(accentHex, 0.86);
                Brush accent = HexBrush(accentHex);
                Brush fg = dark ? HexBrush("#EAF0F8") : HexBrush("#1F2937");

                var border = new Border
                {
                    Width = Math.Max(40, laneW - 4),
                    Height = hgt,
                    Background = bg,
                    BorderBrush = accent,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(7, 5, 6, 5),
                    Cursor = Cursors.Hand,
                    Tag = item,
                    ToolTip = item.Title + "\n" + TimeRange(item) +
                              (string.IsNullOrWhiteSpace(item.Notes) ? "" : "\n" + item.Notes),
                };

                var panel = new StackPanel();
                panel.Children.Add(new TextBlock
                {
                    Text = item.Title,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = fg,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 34,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = TimeRange(item),
                    FontSize = 10.5,
                    Foreground = accent,
                    Margin = new Thickness(0, 2, 0, 0),
                });
                if (!string.IsNullOrWhiteSpace(item.Notes) && hgt > 52)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = item.Notes,
                        FontSize = 10,
                        Foreground = fg,
                        Opacity = 0.8,
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxHeight = 26,
                        Margin = new Thickness(0, 2, 0, 0),
                    });
                }
                if (item.HasDeadline && hgt > 66)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "⏳ " + item.DeadlineText,
                        FontSize = 10,
                        Foreground = (Brush)TryFindResource("DdlTextBrush") ?? accent,
                        Margin = new Thickness(0, 2, 0, 0),
                    });
                }
                if (item.Repeat != RepeatMode.None && hgt > 66)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = item.RepeatText,
                        FontSize = 10,
                        Foreground = accent,
                        Opacity = 0.9,
                        Margin = new Thickness(0, 1, 0, 0),
                    });
                }

                border.Child = panel;
                border.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    if (border.Tag is TodoItem t) ItemClicked?.Invoke(t);
                };

                Canvas.SetTop(border, top + 1.5);
                Canvas.SetLeft(border, left);
                _canvas.Children.Add(border);
            }
        }

        private static string TimeRange(TodoItem item)
        {
            var s = item.StartTime;
            if (!s.HasValue) return "";
            var e = item.EndTime;
            return e.HasValue
                ? s.Value.ToString("HH:mm") + " - " + e.Value.ToString("HH:mm")
                : s.Value.ToString("HH:mm");
        }

        private static Brush HexBrush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        /// <summary>把颜色调淡（浅色主题下用浅底深字）</summary>
        private static Brush TintBrush(string hex, double whiteRatio)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            byte Mix(byte v) => (byte)(v * (1 - whiteRatio) + 255 * whiteRatio);
            var b = new SolidColorBrush(Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B)));
            b.Freeze();
            return b;
        }
    }
}
