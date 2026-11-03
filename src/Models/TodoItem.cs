using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace TodoAssistant.Models
{
    /// <summary>重复方式</summary>
    public enum RepeatMode
    {
        None = 0,
        Daily = 1,
        Weekly = 2,
        Monthly = 3,
        Weekday = 4,     // 工作日（周一至周五）
    }

    /// <summary>
    /// 一条待办：在某一天的某个时间段做某事。
    /// 日期只存日期，时间只存 HH:mm，均为本机本地时间。
    /// </summary>
    public sealed class TodoItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>日期，yyyy-MM-dd</summary>
        public string Date { get; set; }

        /// <summary>开始时间，HH:mm</summary>
        public string Start { get; set; }

        /// <summary>结束时间，HH:mm；可为空</summary>
        public string End { get; set; }

        public string Title { get; set; } = "";

        public string Notes { get; set; } = "";

        public bool Done { get; set; }

        public DateTime? DoneAt { get; set; }

        public RepeatMode Repeat { get; set; } = RepeatMode.None;

        /// <summary>提前提醒分钟数，默认 30。范围 0-1440。</summary>
        public int LeadMinutes { get; set; } = 30;

        /// <summary>提醒时是否朗读内容</summary>
        public bool Speak { get; set; } = true;

        /// <summary>是否额外在手机端推送（使用手机服务器 → ntfy）。null 表示跟随全局设置。</summary>
        public bool? PhoneNotify { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        /// <summary>30 分钟提醒是否已触发（仅当当天未错过）</summary>
        public bool ReminderSent { get; set; }

        /// <summary>开始时刻是否已触发</summary>
        public bool StartNotificationSent { get; set; }

        /// <summary>补提醒：跨过提醒点但未到开始时刻、且程序当时没在运行时，是否补一次</summary>
        public bool CatchUpPending { get; set; }

        // ---------- DDL（截止时间） ----------

        /// <summary>是否设置了 DDL</summary>
        public bool HasDeadline { get; set; }

        /// <summary>DDL 日期，yyyy-MM-dd（为空时沿用 Date）</summary>
        public string DeadlineDate { get; set; }

        /// <summary>DDL 时间，HH:mm（为空时按当天 23:59 算）</summary>
        public string DeadlineTime { get; set; }

        /// <summary>DDL 单独提前提醒分钟数（0 = 不单独提醒，只跟随每天的汇总提醒）</summary>
        public int DeadlineLeadMinutes { get; set; }

        /// <summary>DDL 单独提前提醒是否已触发</summary>
        public bool DeadlineNotified { get; set; }

        // ---------- 派生的只读属性 ----------

        [JsonIgnore]
        public DateTime? StartTime
        {
            get
            {
                if (!TryParseDate(Date, out var d) || !TryParseTime(Start, out var t)) return null;
                return d.Add(t);
            }
        }

        [JsonIgnore]
        public DateTime? EndTime
        {
            get
            {
                if (string.IsNullOrEmpty(End) || !TryParseDate(Date, out var d) || !TryParseTime(End, out var t))
                    return null;
                var dt = d.Add(t);
                // 结束时间早于开始时间：视为跨到次日
                var st = StartTime;
                if (st.HasValue && dt < st.Value) dt = dt.AddDays(1);
                return dt;
            }
        }

        [JsonIgnore]
        public DateTime ReminderAt => StartTime.HasValue ? StartTime.Value.AddMinutes(-EffectiveLead) : DateTime.MinValue;

        [JsonIgnore]
        public int EffectiveLead => LeadMinutes <= 0 ? 30 : Math.Min(1440, LeadMinutes);

        /// <summary>DDL 的完整日期时间；未设置时返回 null</summary>
        [JsonIgnore]
        public DateTime? DeadlineAt
        {
            get
            {
                if (!HasDeadline) return null;
                string ds = string.IsNullOrWhiteSpace(DeadlineDate) ? Date : DeadlineDate;
                if (!TryParseDate(ds, out var d)) return null;
                TimeSpan t = new TimeSpan(23, 59, 0);
                if (!string.IsNullOrWhiteSpace(DeadlineTime) && TryParseTime(DeadlineTime, out var parsed)) t = parsed;
                return d.Date.Add(t);
            }
        }

        /// <summary>距离 DDL 还有多久（小时，可为负 = 已过期）；无 DDL 返回 null</summary>
        public double? HoursUntilDeadline(DateTime now)
        {
            var at = DeadlineAt;
            return at.HasValue ? (at.Value - now).TotalHours : (double?)null;
        }

        /// <summary>是否已过 DDL</summary>
        public bool IsDeadlinePassed(DateTime now)
        {
            var at = DeadlineAt;
            return at.HasValue && at.Value < now;
        }

        /// <summary>DDL 的友好描述，例如「今天 23:59」「明天 18:00」「3 天后」</summary>
        public string DeadlineText
        {
            get
            {
                var at = DeadlineAt;
                if (!at.HasValue) return "";
                var now = DateTime.Now;
                int days = (at.Value.Date - now.Date).Days;
                string dayPart = days switch
                {
                    0 => "今天",
                    1 => "明天",
                    2 => "后天",
                    -1 => "昨天",
                    > 1 => days + " 天后",
                    _ => at.Value.ToString("MM月dd日"),
                };
                return dayPart + " " + at.Value.ToString("HH:mm");
            }
        }

        public bool IsOverdue(DateTime now)
        {
            if (Done) return false;
            var end = EndTime ?? StartTime;
            return end.HasValue && end.Value < now;
        }

        public TodoItem Clone() => (TodoItem)MemberwiseClone();

        // ---------- 解析辅助 ----------

        public static bool TryParseDate(string s, out DateTime d)
            => DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

        public static bool TryParseTime(string s, out TimeSpan t)
            => TimeSpan.TryParseExact(s, new[] { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" },
                CultureInfo.InvariantCulture, out t);

        /// <summary>规范化日期/时间字符串，非法返回 false</summary>
        public bool Normalize()
        {
            if (!TryParseDate(Date, out var d)) return false;
            if (!TryParseTime(Start, out var st)) return false;
            Date = d.ToString("yyyy-MM-dd");
            Start = st.ToString(@"hh\:mm");
            if (!string.IsNullOrEmpty(End))
            {
                if (!TryParseTime(End, out var et)) End = null;
                else End = et.ToString(@"hh\:mm");
            }
            if (string.IsNullOrWhiteSpace(Title)) Title = "未命名待办";
            Title = Title.Trim();
            if (Title.Length > 200) Title = Title.Substring(0, 200);
            Notes ??= "";
            if (LeadMinutes < 0) LeadMinutes = 0;
            if (LeadMinutes > 1440) LeadMinutes = 1440;

            // DDL 规范化
            if (!HasDeadline)
            {
                DeadlineDate = null;
                DeadlineTime = null;
                DeadlineLeadMinutes = 0;
                DeadlineNotified = false;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(DeadlineDate))
                {
                    if (TryParseDate(DeadlineDate, out var dd)) DeadlineDate = dd.ToString("yyyy-MM-dd");
                    else DeadlineDate = null;
                }
                if (!string.IsNullOrWhiteSpace(DeadlineTime))
                {
                    if (TryParseTime(DeadlineTime, out var dt)) DeadlineTime = dt.ToString(@"hh\:mm");
                    else DeadlineTime = null;
                }
                if (DeadlineLeadMinutes < 0) DeadlineLeadMinutes = 0;
                if (DeadlineLeadMinutes > 10080) DeadlineLeadMinutes = 10080;   // 最多 7 天
                // 注意：不要在 Normalize 里重置 DeadlineNotified，
                // 由编辑保存时比较新旧截止时间来决定是否重置。
            }

            UpdatedAt = DateTime.Now;
            return true;
        }

        // ---------- 重复展开 ----------

        public bool OccursOn(DateTime day)
        {
            if (!TryParseDate(Date, out var startDate)) return false;
            day = day.Date;
            if (day < startDate.Date) return false;
            switch (Repeat)
            {
                case RepeatMode.None: return day == startDate.Date;
                case RepeatMode.Daily: return true;
                case RepeatMode.Weekday: return day.DayOfWeek != DayOfWeek.Saturday && day.DayOfWeek != DayOfWeek.Sunday;
                case RepeatMode.Weekly: return day.DayOfWeek == startDate.DayOfWeek;
                case RepeatMode.Monthly: return day.Day == startDate.Day;
                default: return day == startDate.Date;
            }
        }

        /// <summary>
        /// 把重复待办展开为 [from, to] 区间内每一天的实例（不修改原对象）。
        /// </summary>
        public List<TodoItem> Expand(DateTime from, DateTime to)
        {
            var list = new List<TodoItem>();
            if (Repeat == RepeatMode.None)
            {
                if (TryParseDate(Date, out var d) && d.Date >= from.Date && d.Date <= to.Date)
                    list.Add(this);
                return list;
            }
            for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
            {
                if (!OccursOn(day)) continue;
                var inst = Clone();
                inst.Date = day.ToString("yyyy-MM-dd");
                inst.ReminderSent = false;
                inst.StartNotificationSent = false;
                inst.CatchUpPending = false;
                inst.Done = false;
                inst.DoneAt = null;
                list.Add(inst);
            }
            return list;
        }

        public string RepeatText => Repeat switch
        {
            RepeatMode.Daily => "每天",
            RepeatMode.Weekly => "每周" + (TryParseDate(Date, out var wd) ? WeekNames[(int)wd.DayOfWeek] : ""),
            RepeatMode.Monthly => "每月" + (TryParseDate(Date, out var md) ? md.Day : 1) + "日",
            RepeatMode.Weekday => "工作日",
            _ => "",
        };

        private static readonly string[] WeekNames = { "日", "一", "二", "三", "四", "五", "六" };

        public TodoItem CopyForAnotherDate(DateTime day)
        {
            var c = Clone();
            c.Id = Guid.NewGuid().ToString("N");
            c.Date = day.ToString("yyyy-MM-dd");
            c.Done = false;
            c.DoneAt = null;
            c.ReminderSent = false;
            c.StartNotificationSent = false;
            c.CatchUpPending = false;
            c.CreatedAt = DateTime.Now;
            c.UpdatedAt = DateTime.Now;
            c.Repeat = RepeatMode.None;   // 复制到另一天时不继承重复，避免重复生成
            return c;
        }
    }
}
