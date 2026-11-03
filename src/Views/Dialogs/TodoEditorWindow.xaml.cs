using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TodoAssistant.Models;
using TodoAssistant.Services;
using TodoAssistant.ViewModels;

namespace TodoAssistant.Views.Dialogs
{
    public partial class TodoEditorWindow : Window
    {
        private readonly TodoEditorViewModel _vm;
        private bool _ready;           // 控件初始化完成后才开始联动

        /// <summary>对话框 OK 后由调用方读取</summary>
        public TodoEditorViewModel Result => _vm;
        public bool DeleteRequested { get; private set; }

        public TodoEditorWindow(TodoEditorViewModel vm)
        {
            _vm = vm;
            InitializeComponent();

            HeaderText.Text = vm.IsNew ? "添加待办" : "编辑待办";
            Title = HeaderText.Text;
            TitleBox.Text = vm.Title;
            NotesBox.Text = vm.Notes;

            LeadCombo.ItemsSource = vm.LeadOptions;
            LeadCombo.SelectedItem = vm.LeadOptions.Find(o => o.Minutes == vm.LeadMinutes)
                                     ?? vm.LeadOptions.Find(o => o.Minutes == 30);
            RepeatCombo.ItemsSource = vm.RepeatOptions;
            RepeatCombo.SelectedItem = vm.RepeatOptions.Find(o => o.Mode == vm.Repeat) ?? vm.RepeatOptions[0];

            DeleteButton.Visibility = vm.IsNew ? Visibility.Collapsed : Visibility.Visible;

            // 日期
            DatePicker.SelectedDate = vm.SelectedDate.Date;

            // 时间（滚轮）
            StartWheel.TimeMinutes = ParseTime(vm.StartText, 9 * 60);
            EndWheel.TimeMinutes = ParseTime(vm.EndText, 0);
            EndWheel.IsEmpty = string.IsNullOrWhiteSpace(vm.EndText);

            // DDL
            HasDeadlineCheck.IsChecked = vm.HasDeadline;
            DeadlineDatePicker.SelectedDate = vm.DeadlineDate.Date;
            DeadlineWheel.TimeMinutes = Math.Max(0, Math.Min(1439, vm.DeadlineMinutes));
            DeadlineLeadCombo.ItemsSource = vm.DeadlineLeadOptions;
            DeadlineLeadCombo.SelectedItem = vm.DeadlineLeadOptions.Find(o => o.Minutes == vm.DeadlineLeadMinutes)
                                             ?? vm.DeadlineLeadOptions[0];
            DeadlinePanel.Visibility = vm.HasDeadline ? Visibility.Visible : Visibility.Collapsed;

            _ready = true;
            UpdateTexts();
            UpdateHint();
            UpdateDeadlineHint();

            Loaded += (s, e) =>
            {
                TitleBox.Focus();
                TitleBox.CaretIndex = TitleBox.Text.Length;
                TitleBox.SelectAll();
            };
        }

        private static int ParseTime(string s, int fallback)
            => TodoItem.TryParseTime(s?.Trim(), out var t) ? (int)t.TotalMinutes : fallback;

        private static string FormatTime(int minutes)
            => (minutes / 60 % 24).ToString("00") + ":" + (minutes % 60).ToString("00");

        private int StartMinutes => StartWheel.TimeMinutes;
        private string StartText => FormatTime(StartMinutes);
        private string EndText => EndWheel.IsEmpty ? "" : FormatTime(EndWheel.TimeMinutes);

        // ---------------- 联动 ----------------

        private void UpdateTexts()
        {
            if (!_ready) return;
            var d = DatePicker.SelectedDate.Date;
            string extra = d < DateTime.Today ? "（已过去的日期）" : "";
            DayText.Text = d.ToString("yyyy 年 M 月 d 日", CultureInfo.CurrentCulture)
                           + " 周" + "日一二三四五六"[(int)d.DayOfWeek] + extra;
            ClearEndButton.Visibility = EndWheel.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            UpdateHint();
        }

        private void UpdateHint()
        {
            if (!_ready) return;
            int lead = (LeadCombo.SelectedItem as LeadOption)?.Minutes ?? 30;
            var date = DatePicker.SelectedDate.Date;
            var startAt = date.AddMinutes(StartMinutes);

            string text;
            if (startAt < DateTime.Now)
            {
                text = $"⚠ {startAt:MM月dd日 HH:mm} 这个时间已经过去了，不会提醒；如果只是记录，忽略即可。";
            }
            else if (lead <= 0)
            {
                text = $"⏰ 会在 {startAt:MM月dd日 HH:mm} 准时提醒你";
            }
            else
            {
                var at = startAt.AddMinutes(-lead);
                text = at <= DateTime.Now
                    ? $"⏰ 现在就在提醒窗口内：保存后会立刻提醒你「{StartText} 有安排」"
                    : $"⏰ 会在 {at:MM月dd日 HH:mm} 提醒你（距开始 {lead} 分钟）";
            }

            if (!EndWheel.IsEmpty)
            {
                int end = EndWheel.TimeMinutes;
                if (end == StartMinutes) text += "　·　结束时间与开始相同";
                else if (end < StartMinutes) text += "　·　结束时间早于开始（按次日算）";
                else text += $"　·　持续 {(end - StartMinutes) % (24 * 60)} 分钟";
            }
            HintText.Text = text;
        }

        private void DatePicker_DateChanged(object sender, EventArgs e) => UpdateTexts();

        private void StartWheel_ValueChanged(object sender, EventArgs e)
        {
            // 结束时间没设置时，自动给一个 1 小时的结束时间方便查看
            UpdateTexts();
        }

        private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ErrorText == null) return;
            ErrorText.Visibility = Visibility.Collapsed;
        }

        private void QuickTime_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is string tag)) return;
            DateTime target;
            switch (tag)
            {
                case "now":
                    target = DateTime.Now;
                    break;
                case "0900":
                    target = DatePicker.SelectedDate.Date.AddHours(9);
                    break;
                default:
                    target = DateTime.Now.AddMinutes(int.Parse(tag.Substring(1), CultureInfo.InvariantCulture));
                    break;
            }
            StartWheel.TimeMinutes = target.Hour * 60 + target.Minute;
            if (EndWheel.IsEmpty)
            {
                EndWheel.TimeMinutes = target.Hour * 60 + target.Minute + 60;
                EndWheel.IsEmpty = EndWheel.TimeMinutes > 0;
            }
            UpdateTexts();
        }

        private void ClearEnd_Click(object sender, RoutedEventArgs e)
        {
            EndWheel.IsEmpty = true;
            UpdateTexts();
        }

        // ---------------- DDL ----------------

        private void HasDeadline_Changed(object sender, RoutedEventArgs e)
        {
            DeadlinePanel.Visibility = HasDeadlineCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateDeadlineHint();
        }

        private void DeadlineDate_Changed(object sender, EventArgs e) => UpdateDeadlineHint();

        private void DeadlineTime_Changed(object sender, EventArgs e) => UpdateDeadlineHint();

        private void DeadlineLead_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateDeadlineHint();

        private DateTime? BuildDeadline()
        {
            if (HasDeadlineCheck.IsChecked != true) return null;
            return DeadlineDatePicker.SelectedDate.Date.AddMinutes(DeadlineWheel.TimeMinutes);
        }

        private void UpdateDeadlineHint()
        {
            if (!_ready || DeadlineHint == null) return;
            var at = BuildDeadline();
            if (!at.HasValue)
            {
                DeadlineHint.Text = "不设置 DDL";
                return;
            }
            var now = DateTime.Now;
            string rel = DeadlineService.RelativeText(at.Value, now);
            string extra;
            if (at.Value <= now)
                extra = "⚠ 这个截止时间已经过去了";
            else if (at.Value.Date == now.Date)
                extra = "今天的截止时间比较紧，记得留意";
            else
                extra = $"距今 {(at.Value.Date - now.Date).Days} 天";
            int lead = (DeadlineLeadCombo.SelectedItem as LeadOption)?.Minutes ?? 0;
            string leadText = lead > 0
                ? $"到点前 {lead} 分钟会单独提醒一次"
                : "只会在每天早/晚汇总时提醒";
            DeadlineHint.Text = $"⏳ 截止 {at.Value:MM月dd日 HH:mm}（{rel}）· {extra}\n{leadText}";
        }

        // ---------------- 保存 ----------------

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _vm.Title = TitleBox.Text;
            _vm.Notes = NotesBox.Text;
            _vm.SelectedDate = DatePicker.SelectedDate.Date;
            _vm.StartText = StartText;
            _vm.EndText = EndText;
            _vm.LeadMinutes = (LeadCombo.SelectedItem as LeadOption)?.Minutes ?? 30;
            _vm.Repeat = (RepeatCombo.SelectedItem as RepeatOption)?.Mode ?? RepeatMode.None;

            // DDL
            _vm.HasDeadline = HasDeadlineCheck.IsChecked == true;
            _vm.DeadlineDate = DeadlineDatePicker.SelectedDate.Date;
            _vm.DeadlineMinutes = Math.Max(0, Math.Min(1439, DeadlineWheel.TimeMinutes));
            _vm.DeadlineLeadMinutes = (DeadlineLeadCombo.SelectedItem as LeadOption)?.Minutes ?? 0;

            string error = _vm.BuildResult();
            if (error != null)
            {
                ErrorText.Text = "⚠ " + error;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            DialogResult = true;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "确定删除这条待办？", "删除", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            DeleteRequested = true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { DragMove(); } catch { }
            }
        }
    }
}
