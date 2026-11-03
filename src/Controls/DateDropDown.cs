using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TodoAssistant.Controls
{
    /// <summary>
    /// 日期选择：显示一个日历图标 + 日期文本的按钮，点开下拉日历（含「今天/明天/后天」快捷项）。
    /// </summary>
    public class DateDropDown : UserControl
    {
        public static readonly DependencyProperty SelectedDateProperty =
            DependencyProperty.Register(nameof(SelectedDate), typeof(DateTime), typeof(DateDropDown),
                new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedDateChanged));

        private readonly Button _button;
        private readonly TextBlock _text;
        private readonly Popup _popup;
        private readonly System.Windows.Controls.Calendar _calendar;
        private readonly TextBlock _errorText;

        public event EventHandler DateChanged;

        public DateDropDown()
        {
            Focusable = false;

            _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5 };
            var icon = new TextBlock
            {
                Text = "📅",
                FontSize = 14,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(icon);
            sp.Children.Add(_text);

            _button = new Button
            {
                Content = sp,
                Height = 38,
                Padding = new Thickness(10, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Background = ThemeBrush("InputBgBrush", 0xF8, 0xFA, 0xFE),
                BorderBrush = ThemeBrush("LineBrush", 0xE6, 0xEB, 0xF3),
                BorderThickness = new Thickness(1),
                ToolTip = "选择日期",
            };
            _button.Template = BuildButtonTemplate();
            _button.Click += (s, e) => TogglePopup();

            _calendar = new System.Windows.Controls.Calendar
            {
                SelectedDate = SelectedDate,
                DisplayDate = SelectedDate,
                Margin = new Thickness(0),
            };
            _calendar.SelectedDatesChanged += (s, e) =>
            {
                if (!_calendar.SelectedDate.HasValue) return;
                Apply(_calendar.SelectedDate.Value, true);
                ClosePopup();
            };

            var quick = new WrapPanel { Margin = new Thickness(4, 2, 4, 0) };
            quick.Children.Add(QuickButton("今天", 0));
            quick.Children.Add(QuickButton("明天", 1));
            quick.Children.Add(QuickButton("后天", 2));
            quick.Children.Add(QuickButton("大后天", 3));
            quick.Children.Add(QuickButton("5 天后", 4));

            var panel = new StackPanel();
            panel.Children.Add(quick);
            panel.Children.Add(_calendar);

            _popup = new Popup
            {
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = ThemeBrush("LineBrush", 0xE6, 0xEB, 0xF3),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(10),
                    Margin = new Thickness(0, 2, 0, 2),
                    Child = panel,
                },
            };
            _popup.Opened += (s, e) => _calendar.DisplayDate = SelectedDate;

            _errorText = new TextBlock
            {
                Text = "日期无效（需在 1900-2100 年之间）",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(2, 3, 0, 0),
            };

            var root = new StackPanel();
            root.Children.Add(_button);
            root.Children.Add(_errorText);
            Content = root;

            UpdateText();
        }

        public DateTime SelectedDate
        {
            get => (DateTime)GetValue(SelectedDateProperty);
            set => SetValue(SelectedDateProperty, value);
        }

        private static void OnSelectedDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (DateDropDown)d;
            self.UpdateText();
            if (self._calendar != null) self._calendar.SelectedDate = (DateTime)e.NewValue;
        }

        private Button QuickButton(string label, int offset)
        {
            var b = new Button
            {
                Content = label,
                Margin = new Thickness(0, 0, 6, 4),
                Padding = new Thickness(12, 4, 12, 4),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF8)),
                Cursor = Cursors.Hand,
                FontSize = 12.5,
            };
            b.Template = BuildButtonTemplate();
            b.Click += (s, e) =>
            {
                Apply(DateTime.Today.AddDays(offset), true);
                ClosePopup();
            };
            return b;
        }

        private void Apply(DateTime d, bool raise)
        {
            SelectedDate = d.Date;
            UpdateText();
            if (raise) DateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void TogglePopup()
        {
            if (_popup.IsOpen) { ClosePopup(); return; }
            _popup.PlacementTarget = _button;
            _popup.Placement = PlacementMode.Bottom;
            _popup.IsOpen = true;
        }

        private void ClosePopup() => _popup.IsOpen = false;

        private void UpdateText()
        {
            var d = SelectedDate.Date;
            int diff = (d - DateTime.Today).Days;
            string rel = diff == 0 ? "（今天）" : diff == 1 ? "（明天）" : diff == 2 ? "（后天）" : diff == -1 ? "（昨天）" : "";
            _text.Text = d.ToString("yyyy年M月d日", CultureInfo.CurrentCulture) + " 周" + "日一二三四五六"[(int)d.DayOfWeek] + rel;
            _text.Foreground = d < DateTime.Today
                ? ThemeBrush("RedBrush", 0xEF, 0x44, 0x44)
                : ThemeBrush("TextBrush", 0x11, 0x18, 0x27);
        }

        /// <summary>取主题画刷；找不到时退回默认颜色（启动早期资源还没准备好也不会崩）</summary>
        private static Brush ThemeBrush(string key, byte r, byte g, byte b)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is Brush br) return br;
            }
            catch { }
            var fallback = new SolidColorBrush(Color.FromRgb(r, g, b));
            fallback.Freeze();
            return fallback;
        }

        private static ControlTemplate BuildButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xC7, 0xD5, 0xE8)), "bd"));
            template.Triggers.Add(hover);
            return template;
        }
    }
}
