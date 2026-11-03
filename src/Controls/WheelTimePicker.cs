using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TodoAssistant.Controls
{
    /// <summary>
    /// 时间选择器：大字显示当前时间，时/分各自带 ▲▼ 按钮，
    /// 支持鼠标滚轮和按住连续点（长按可以快速跳），不做滚轮列表，画面干净不重叠。
    /// </summary>
    public class WheelTimePicker : UserControl
    {
        public static readonly DependencyProperty TimeMinutesProperty =
            DependencyProperty.Register(nameof(TimeMinutes), typeof(int), typeof(WheelTimePicker),
                new FrameworkPropertyMetadata(9 * 60, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        public static readonly DependencyProperty AllowEmptyProperty =
            DependencyProperty.Register(nameof(AllowEmpty), typeof(bool), typeof(WheelTimePicker),
                new PropertyMetadata(false, OnValueChanged));

        public static readonly DependencyProperty IsEmptyProperty =
            DependencyProperty.Register(nameof(IsEmpty), typeof(bool), typeof(WheelTimePicker),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        private static Brush TextBrush => ThemeBrush("TextBrush", 0x11, 0x18, 0x27);
        private static Brush MutedBrush => ThemeBrush("MutedBrush", 0x64, 0x74, 0x8B);
        private static Brush HintBrush => ThemeBrush("FaintBrush", 0x9C, 0xA3, 0xAF);
        // 边框/背景跟随主题（不用静态字段，否则深色模式下仍是浅色）
        private static Brush FrameBrush => ThemeBrush("LineBrush", 0xE6, 0xEB, 0xF3);
        private static Brush FrameBgBrush => ThemeBrush("CardBrush", 0xFF, 0xFF, 0xFF);
        private static Brush HotBrush => ThemeBrush("AccentBrush", 0x3B, 0x82, 0xF6);

        private readonly TextBlock _hourText;
        private readonly TextBlock _minuteText;
        private readonly Button _hourUp, _hourDown, _minuteUp, _minuteDown;
        private readonly Border _frame;
        private readonly TextBlock _emptyLabel;
        private readonly Border _emptyEllipse;
        private readonly StackPanel _pickerPanel;
        private bool _syncing;
        private bool _hourActive;

        /// <summary>用户改变时间后触发</summary>
        public event EventHandler ValueChanged;

        public WheelTimePicker()
        {
            Focusable = false;
            FontFamily = new FontFamily("Microsoft YaHei UI");

            _hourText = BigText();
            _hourText.Margin = new Thickness(0, 2, 0, 2);
            _minuteText = BigText();
            _minuteText.Margin = new Thickness(0, 2, 0, 2);

            var colon = new TextBlock
            {
                Text = ":",
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                IsHitTestVisible = false,
            };
            colon.VerticalAlignment = VerticalAlignment.Center;

            _hourUp = ArrowButton(true);
            _hourDown = ArrowButton(false);
            _minuteUp = ArrowButton(true);
            _minuteDown = ArrowButton(false);
            _hourUp.Click += (s, e) => { SetActive(true); Step(+1); };
            _hourDown.Click += (s, e) => { SetActive(true); Step(-1); };
            _minuteUp.Click += (s, e) => { SetActive(false); Step(+1); };
            _minuteDown.Click += (s, e) => { SetActive(false); Step(-1); };

            var hourCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            hourCol.Children.Add(_hourUp);
            hourCol.Children.Add(_hourText);
            hourCol.Children.Add(_hourDown);

            var minCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            minCol.Children.Add(_minuteUp);
            minCol.Children.Add(_minuteText);
            minCol.Children.Add(_minuteDown);

            _hourText.GotFocus += (s, e) => SetActive(true);
            _minuteText.GotFocus += (s, e) => SetActive(false);
            // 点大数字本身也能切换时/分
            _hourText.MouseLeftButtonDown += (s, e) => { SetActive(true); e.Handled = true; };
            _minuteText.MouseLeftButtonDown += (s, e) => { SetActive(false); e.Handled = true; };

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(hourCol);
            row.Children.Add(colon);
            row.Children.Add(minCol);

            _pickerPanel = row;

            _frame = new Border
            {
                BorderBrush = FrameBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4, 4, 4, 4),
                Child = row,
                Background = FrameBgBrush,
            };

            _emptyLabel = new TextBlock
            {
                Text = "点这里设置",
                FontSize = 13,
                Foreground = HintBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
            };
            // 空状态也画一个和滚轮同样大小的框，左右两边视觉对称
            _emptyEllipse = new Border
            {
                BorderBrush = FrameBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Background = FrameBgBrush,
                Cursor = Cursors.Hand,
                Child = _emptyLabel,
            };
            _emptyEllipse.MouseLeftButtonUp += (s, e) => { IsEmpty = false; };

            var root = new Grid();
            root.Children.Add(_frame);
            root.Children.Add(_emptyEllipse);
            Content = root;

            MouseWheel += OnWheel;
            Loaded += (s, e) => SyncFromValue();
        }

        /// <summary>取主题画刷；找不到时退回默认颜色</summary>
        private static Brush ThemeBrush(string key, byte r, byte g, byte b)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is Brush br) return br;
            }
            catch { }
            return Freeze(r, g, b);
        }

        private static Brush Freeze(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        private TextBlock BigText() => new TextBlock
        {
            Text = "00",
            FontSize = 30,
            FontWeight = FontWeights.Bold,
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Cursor = Cursors.Hand,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
        };

        private Button ArrowButton(bool up)
        {
            var path = new Path
            {
                Data = Geometry.Parse(up ? "M 0,5 L 5,0 L 10,5" : "M 0,0 L 5,5 L 10,0"),
                Stroke = MutedBrush,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var b = new Button
            {
                Content = path,
                Width = 56,
                Height = 15,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 长按连续变化
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += (s, e) =>
            {
                if (Mouse.LeftButton != MouseButtonState.Pressed) { timer.Stop(); return; }
                if (up) Step(+1); else Step(-1);
            };
            b.PreviewMouseLeftButtonDown += (s, e) =>
            {
                SetActive(ReferenceEquals(b, _hourUp) || ReferenceEquals(b, _hourDown));
                timer.Stop();
                timer.Start();
            };
            b.PreviewMouseLeftButtonUp += (s, e) => timer.Stop();
            b.MouseLeave += (s, e) => timer.Stop();
            var template = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            template.VisualTree = bd;
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF8)), "bd"));
            template.Triggers.Add(hover);
            b.Template = template;
            return b;
        }

        // ---------------- 属性 ----------------

        /// <summary>一天中的分钟数（0 - 1439）</summary>
        public int TimeMinutes
        {
            get => (int)GetValue(TimeMinutesProperty);
            set => SetValue(TimeMinutesProperty, value);
        }

        public bool AllowEmpty
        {
            get => (bool)GetValue(AllowEmptyProperty);
            set => SetValue(AllowEmptyProperty, value);
        }

        public bool IsEmpty
        {
            get => (bool)GetValue(IsEmptyProperty);
            set => SetValue(IsEmptyProperty, value);
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((WheelTimePicker)d).SyncFromValue();

        /// <summary>把当前值刷新到界面</summary>
        public void SyncFromValue()
        {
            bool empty = AllowEmpty && IsEmpty;
            _frame.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            _emptyEllipse.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (empty) return;

            int total = ((TimeMinutes % 1440) + 1440) % 1440;
            _hourText.Text = (total / 60).ToString("00");
            _minuteText.Text = (total % 60).ToString("00");
            SetActive(_hourActive);
        }

        /// <summary>高亮当前在调的是「时」还是「分」</summary>
        private void SetActive(bool hour)
        {
            _hourActive = hour;
            _hourText.Foreground = hour ? HotBrush : MutedBrush;
            _minuteText.Foreground = hour ? MutedBrush : HotBrush;
            _hourUp.Opacity = _hourDown.Opacity = hour ? 1 : 0.45;
            _minuteUp.Opacity = _minuteDown.Opacity = hour ? 0.45 : 1;
        }

        /// <summary>步进：时列 ±1 小时，分列 ±5 分钟</summary>
        private void Step(int direction)
        {
            if (AllowEmpty && IsEmpty) return;
            int total = ((TimeMinutes % 1440) + 1440) % 1440;
            if (_hourActive)
                total = ((total + direction * 60) % 1440 + 1440) % 1440;
            else
                total = ((total + direction * 5) % 1440 + 1440) % 1440;

            _syncing = true;
            try { TimeMinutes = total; }
            finally { _syncing = false; }
            SyncFromValue();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            Step(e.Delta > 0 ? +1 : -1);
        }
    }
}
