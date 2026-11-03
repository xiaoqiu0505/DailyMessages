using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using TodoAssistant.Models;

namespace TodoAssistant.ViewModels
{
    /// <summary>三列（今天/明天/后天）中的一列</summary>
    public sealed class DayColumnViewModel
    {
        public int Offset { get; set; }              // 0=今天 1=明天 2=后天
        public DateTime Day { get; set; }
        public string DateText => Day.ToString("MM月dd日");
        public string WeekText => "周" + WeekNames[(int)Day.DayOfWeek];
        public string HeaderTitle => TodoAssistant.Converters.DayNameConverter.Name(Offset);
        public string Summary { get; set; } = "";
        public ObservableCollection<TodoRowViewModel> Items { get; } = new ObservableCollection<TodoRowViewModel>();

        private static readonly string[] WeekNames = { "日", "一", "二", "三", "四", "五", "六" };
        public string EmptyText => Offset == 0 ? "今天还没有安排" : "点右上角 + 添加";
    }

    /// <summary>列表中的一行</summary>
    public sealed class TodoRowViewModel : INotifyPropertyChanged
    {
        public TodoItem Model { get; }

        public TodoRowViewModel(TodoItem model, int dayOffset)
        {
            Model = model;
            DayOffset = dayOffset;
        }

        public int DayOffset { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        public string Title => Model.Title;
        public string TimeText => string.IsNullOrEmpty(Model.End) ? Model.Start : Model.Start + " - " + Model.End;
        public string Notes => Model.Notes;
        public bool HasNotes => !string.IsNullOrWhiteSpace(Model.Notes);
        public string RepeatText => Model.Repeat == RepeatMode.None ? "" : Model.RepeatText;
        public bool HasRepeat => Model.Repeat != RepeatMode.None;
        public string LeadText => $"提前 {Model.EffectiveLead} 分钟提醒";
        public bool IsDone => Model.Done;

        // ---------- DDL ----------

        public bool HasDeadline => Model.HasDeadline && Model.DeadlineAt.HasValue;

        /// <summary>DDL 展示文字，如「今天 23:59」「明天 18:00」</summary>
        public string DeadlineText => HasDeadline ? Model.DeadlineText : "";

        /// <summary>DDL 已过期且未完成</summary>
        public bool IsDeadlineOverdue => HasDeadline && !Model.Done && Model.IsDeadlinePassed(DateTime.Now);

        public string StatusText
        {
            get
            {
                if (Model.Done) return "已完成";
                var start = Model.StartTime;
                if (!start.HasValue) return "";
                var now = DateTime.Now;
                if (now < start.Value)
                {
                    var span = start.Value - now;
                    if (span.TotalMinutes < 1) return "马上开始";
                    if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟后";
                    if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分后";
                    return $"{(int)span.TotalDays} 天后";
                }
                var end = Model.EndTime ?? start.Value;
                if (now <= end) return "进行中";
                return "已过时";
            }
        }

        /// <summary>0 = 正常，1 = 30 分钟内（黄），2 = 已过期（红）</summary>
        public int StatusLevel
        {
            get
            {
                if (Model.Done) return 0;
                var start = Model.StartTime;
                if (!start.HasValue) return 0;
                var now = DateTime.Now;
                if (now >= start.Value) return (Model.EndTime ?? start.Value) >= now ? 1 : 2;
                return (start.Value - now).TotalMinutes <= Model.EffectiveLead ? 1 : 0;
            }
        }

        public bool IsSoon => StatusLevel == 1;
        public bool IsPast => StatusLevel == 2;

        public void Refresh()
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusLevel));
            OnPropertyChanged(nameof(IsSoon));
            OnPropertyChanged(nameof(IsPast));
            OnPropertyChanged(nameof(DeadlineText));
            OnPropertyChanged(nameof(IsDeadlineOverdue));
        }

        public void RefreshAll()
        {
            OnPropertyChanged(string.Empty);
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>待办编辑对话框的数据</summary>
    public sealed class TodoEditorViewModel : INotifyPropertyChanged
    {
        public TodoItem Working { get; private set; }
        public bool IsNew { get; private set; }
        public DateTime SelectedDate { get; set; }

        public string Title { get; set; } = "";
        public string Notes { get; set; } = "";
        public string StartText { get; set; } = "09:00";
        public string EndText { get; set; } = "";
        public int LeadMinutes { get; set; } = 30;
        public RepeatMode Repeat { get; set; } = RepeatMode.None;

        // ---------- DDL ----------
        public bool HasDeadline { get; set; }
        public DateTime DeadlineDate { get; set; } = DateTime.Today;
        public int DeadlineMinutes { get; set; } = 23 * 60 + 59;
        /// <summary>DDL 单独提前提醒（0 = 只跟每日汇总）</summary>
        public int DeadlineLeadMinutes { get; set; }

        public List<LeadOption> DeadlineLeadOptions { get; } = new List<LeadOption>
        {
            new LeadOption(0, "只在每天早/晚汇总时提醒"),
            new LeadOption(60, "提前 1 小时"),
            new LeadOption(180, "提前 3 小时"),
            new LeadOption(720, "提前 12 小时"),
            new LeadOption(1440, "提前 1 天"),
            new LeadOption(2880, "提前 2 天"),
        };

        public List<LeadOption> LeadOptions { get; } = new List<LeadOption>
        {
            new LeadOption(0, "不提前（仅到点提醒）"),
            new LeadOption(5, "提前 5 分钟"),
            new LeadOption(10, "提前 10 分钟"),
            new LeadOption(15, "提前 15 分钟"),
            new LeadOption(30, "提前 30 分钟"),
            new LeadOption(45, "提前 45 分钟"),
            new LeadOption(60, "提前 1 小时"),
            new LeadOption(120, "提前 2 小时"),
            new LeadOption(1440, "提前 1 天"),
        };

        public List<RepeatOption> RepeatOptions { get; } = new List<RepeatOption>
        {
            new RepeatOption(RepeatMode.None, "不重复"),
            new RepeatOption(RepeatMode.Daily, "每天"),
            new RepeatOption(RepeatMode.Weekday, "工作日（周一至周五）"),
            new RepeatOption(RepeatMode.Weekly, "每周同一天"),
            new RepeatOption(RepeatMode.Monthly, "每月同一天"),
        };

        public string DialogTitle => IsNew ? "添加待办" : "编辑待办";
        public string DayText => SelectedDate.ToString("yyyy 年 M 月 d 日 · dddd", new CultureInfo("zh-CN"));

        public event PropertyChangedEventHandler PropertyChanged;

        public void Initialize(TodoItem existing, int dayOffset, int defaultLead)
        {
            IsNew = existing == null;
            SelectedDate = DateTime.Today.AddDays(dayOffset);
            if (existing != null)
            {
                Working = existing.Clone();
                SelectedDate = existing.StartTime?.Date ?? SelectedDate;
                Title = existing.Title;
                Notes = existing.Notes;
                StartText = existing.Start;
                EndText = existing.End ?? "";
                LeadMinutes = existing.LeadMinutes;
                Repeat = existing.Repeat;

                // DDL
                HasDeadline = existing.HasDeadline;
                var at = existing.DeadlineAt;
                if (at.HasValue)
                {
                    DeadlineDate = at.Value.Date;
                    DeadlineMinutes = at.Value.Hour * 60 + at.Value.Minute;
                }
                DeadlineLeadMinutes = existing.DeadlineLeadMinutes;
                if (DeadlineLeadMinutes > 0 && DeadlineLeadOptions.FindIndex(o => o.Minutes == DeadlineLeadMinutes) < 0)
                {
                    DeadlineLeadOptions.Add(new LeadOption(DeadlineLeadMinutes, $"提前 {DeadlineLeadMinutes} 分钟"));
                    DeadlineLeadOptions.Sort((a, b) => a.Minutes.CompareTo(b.Minutes));
                }
            }
            else
            {
                Working = new TodoItem
                {
                    Date = SelectedDate.ToString("yyyy-MM-dd"),
                    Start = NextRoundTime().ToString(@"hh\:mm"),
                    LeadMinutes = defaultLead,
                };
                StartText = Working.Start;
                EndText = "";
                LeadMinutes = defaultLead;
                Repeat = RepeatMode.None;
                HasDeadline = false;
                DeadlineDate = SelectedDate.Date;
                DeadlineMinutes = 23 * 60 + 59;
                DeadlineLeadMinutes = 0;
            }
            // 下拉框里没有的提前量，补一个
            if (LeadOptions.FindIndex(o => o.Minutes == LeadMinutes) < 0)
            {
                LeadOptions.Add(new LeadOption(LeadMinutes, $"提前 {LeadMinutes} 分钟"));
                LeadOptions.Sort((a, b) => a.Minutes.CompareTo(b.Minutes));
            }
        }

        private static DateTime NextRoundTime()
        {
            var d = DateTime.Now.AddMinutes(30);
            int m = d.Minute <= 0 ? 0 : d.Minute <= 15 ? 15 : d.Minute <= 30 ? 30 : 45;
            return new DateTime(d.Year, d.Month, d.Day, d.Hour, m, 0).AddMinutes(m == 45 ? 15 : 0);
        }

        /// <summary>校验并生成待办对象；返回错误信息（null 表示成功）</summary>
        public string BuildResult()
        {
            if (string.IsNullOrWhiteSpace(Title)) return "请填写待办内容";
            if (!TodoItem.TryParseTime(StartText?.Trim(), out var st)) return "开始时间格式应为 HH:mm，例如 09:30";
            string end = null;
            if (!string.IsNullOrWhiteSpace(EndText))
            {
                if (!TodoItem.TryParseTime(EndText.Trim(), out var et)) return "结束时间格式应为 HH:mm，例如 10:30";
                end = et.ToString(@"hh\:mm");
            }
            var item = Working ?? new TodoItem();
            item.Date = SelectedDate.ToString("yyyy-MM-dd");
            item.Start = st.ToString(@"hh\:mm");
            item.End = end;
            item.Title = Title.Trim();
            item.Notes = Notes?.Trim() ?? "";
            item.LeadMinutes = LeadMinutes;
            item.Repeat = Repeat;

            // DDL：时间变了才重置「已提醒」标记
            var oldDeadline = item.DeadlineAt;
            item.HasDeadline = HasDeadline;
            if (HasDeadline)
            {
                item.DeadlineDate = DeadlineDate.ToString("yyyy-MM-dd");
                item.DeadlineTime = (DeadlineMinutes / 60).ToString("00") + ":" + (DeadlineMinutes % 60).ToString("00");
                item.DeadlineLeadMinutes = DeadlineLeadMinutes;
                var newDeadline = DeadlineDate.Date.AddMinutes(DeadlineMinutes);
                if (oldDeadline != newDeadline) item.DeadlineNotified = false;
            }
            else
            {
                item.DeadlineDate = null;
                item.DeadlineTime = null;
                item.DeadlineLeadMinutes = 0;
                item.DeadlineNotified = false;
            }

            if (!item.Normalize()) return "日期或时间格式不正确";
            Working = item;
            return null;
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed record LeadOption(int Minutes, string Display)
    {
        public override string ToString() => Display;
    }

    public sealed record RepeatOption(RepeatMode Mode, string Display)
    {
        public override string ToString() => Display;
    }
}
