using System;
using System.Collections.Generic;
using System.Linq;
using TodoAssistant.Models;

namespace TodoAssistant.Services
{
    /// <summary>
    /// DDL 提醒服务，负责两类提醒：
    /// 1) 每日汇总：每天早上 9:00、晚上 21:00（时间可在设置里改）把所有未完成的 DDL 汇总成一条通知；
    /// 2) 单条提前提醒：某条待办设置了自己的提前量时，到点单独提醒一次。
    /// </summary>
    public sealed class DeadlineService
    {
        private readonly TodoStore _store;
        private readonly SettingsStore _settings;

        /// <summary>需要发出的提醒（由 App 负责弹通知/响铃/推送）</summary>
        public event Action<ReminderEvent> Fire;

        public DeadlineService(TodoStore store, SettingsStore settings)
        {
            _store = store;
            _settings = settings;
        }

        /// <summary>取出所有带 DDL 且未完成的待办（去重，按 DDL 时间升序）</summary>
        public List<TodoItem> GetOpenDeadlines()
        {
            var now = DateTime.Now;
            return _store.GetRange(now.Date.AddDays(-60), now.Date.AddDays(365))
                .Where(i => i.HasDeadline && !i.Done && i.DeadlineAt.HasValue)
                .GroupBy(i => i.Id).Select(g => g.First())
                .OrderBy(i => i.DeadlineAt)
                .ToList();
        }

        /// <summary>
        /// 需要出现在汇总里的 DDL：今天到期/已过期的全部算上，其余在 N 天内。
        /// </summary>
        public List<TodoItem> GetDigestItems(int withinDays)
        {
            var now = DateTime.Now;
            var limit = now.Date.AddDays(Math.Max(0, withinDays)).AddDays(1);
            return GetOpenDeadlines()
                .Where(i =>
                {
                    var at = i.DeadlineAt.Value;
                    if (at < now) return true;                 // 已过期，必须提醒
                    if (at.Date == now.Date) return true;      // 今天到期
                    return at <= limit;
                })
                .ToList();
        }

        /// <summary>由调度器每 10 秒调用一次</summary>
        public void Tick()
        {
            try
            {
                TickDigest();
                TickSingle();
            }
            catch (Exception ex)
            {
                Log.Error("DDL 提醒检查失败", ex);
            }
        }

        // ---------------- 每日汇总 ----------------

        private void TickDigest()
        {
            var s = _settings.Current;
            if (!s.DdlDigestEnabled) return;

            var now = DateTime.Now;
            string today = now.Date.ToString("yyyy-MM-dd");
            bool changed = false;

            foreach (var (timeText, isMorning) in new[] { (s.DdlDigestMorning, true), (s.DdlDigestEvening, false) })
            {
                if (!TryParseHm(timeText, out var slot)) continue;
                if (now.Hour != slot.Hours || now.Minute != slot.Minutes) continue;

                string last = isMorning ? s.DdlDigestMorningLast : s.DdlDigestEveningLast;
                string name = isMorning ? "早" : "晚";
                if (last == today) continue;   // 今天这个时段已发过

                if (isMorning) s.DdlDigestMorningLast = today; else s.DdlDigestEveningLast = today;
                changed = true;

                var items = GetDigestItems(s.DdlDigestDays);
                if (items.Count == 0)
                {
                    Log.Write($"DDL {name}间汇总：没有待办 DDL，跳过");
                    continue;
                }

                var now2 = DateTime.Now;
                int overdue = items.Count(i => i.DeadlineAt.Value < now2);
                var first = items[0];
                string title;
                string body;

                if (items.Count == 1)
                {
                    var at = first.DeadlineAt.Value;
                    title = (at < now2 ? "DDL 已过期 · " : "DDL 提醒 · ") + first.Title;
                    body = $"截止 {at:MM月dd日 HH:mm}（{RelativeText(at, now2)}）";
                    if (!string.IsNullOrWhiteSpace(first.Notes)) body += "　" + first.Notes;
                }
                else
                {
                    title = $"DDL {name}间提醒：{items.Count} 个任务有截止时间"
                            + (overdue > 0 ? $"（{overdue} 个已过期）" : "");
                    var lines = items.Take(4).Select(i =>
                        $"· {i.Title} —— {i.DeadlineAt.Value:MM月dd日 HH:mm}（{RelativeText(i.DeadlineAt.Value, now2)}）");
                    body = string.Join("\n", lines);
                    if (items.Count > 4) body += $"\n… 还有 {items.Count - 4} 个，点开查看";
                }

                string speech = items.Count == 1
                    ? $"提醒：{first.Title}，截止时间{RelativeText(first.DeadlineAt.Value, now2)}"
                    : $"提醒：有{items.Count}个任务临近截止时间";

                Fire?.Invoke(new ReminderEvent
                {
                    Item = first,
                    Kind = ReminderKind.DeadlineDigest,
                    Title = title,
                    Body = body,
                    Speech = speech,
                });
            }

            if (changed) _settings.Save();
        }

        // ---------------- 单条提前提醒 ----------------

        private void TickSingle()
        {
            var now = DateTime.Now;
            foreach (var item in GetOpenDeadlines())
            {
                if (item.DeadlineLeadMinutes <= 0) continue;
                if (item.DeadlineNotified) continue;

                var at = item.DeadlineAt.Value;
                var remindAt = at.AddMinutes(-item.DeadlineLeadMinutes);
                if (now < remindAt || now > at.AddMinutes(30)) continue;   // 超出区间则跳过

                string relative = RelativeText(at, now);
                Fire?.Invoke(new ReminderEvent
                {
                    Item = item,
                    Kind = ReminderKind.DeadlineSoon,
                    Title = $"DDL 快到了 · {item.Title}",
                    Body = $"截止 {at:MM月dd日 HH:mm}（{relative}）" +
                           (string.IsNullOrWhiteSpace(item.Notes) ? "" : "　" + item.Notes),
                    Speech = $"{item.Title} 的截止时间{relative}",
                });

                // 重复待办的主记录不落盘，其余标记已提醒
                if (item.Repeat == RepeatMode.None)
                {
                    _store.SetFlag(item.Id, x => { x.DeadlineNotified = true; return true; });
                }
                else
                {
                    item.DeadlineNotified = true;
                }
            }
        }

        // ---------------- 工具 ----------------

        public static bool TryParseHm(string text, out TimeSpan t)
            => TimeSpan.TryParseExact(text ?? "", new[] { @"h\:mm", @"hh\:mm" },
                System.Globalization.CultureInfo.InvariantCulture, out t)
               && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);

        /// <summary>「3 小时后」「已过期 2 小时」这类相对描述</summary>
        public static string RelativeText(DateTime at, DateTime now)
        {
            var span = at - now;
            bool past = span < TimeSpan.Zero;
            span = span.Duration();

            string text;
            if (span.TotalMinutes < 60) text = Math.Max(1, (int)Math.Round(span.TotalMinutes)) + " 分钟";
            else if (span.TotalHours < 24) text = (int)span.TotalHours + " 小时";
            else text = (int)span.TotalDays + " 天";

            return past ? "已过期 " + text : "还剩 " + text;
        }

        /// <summary>给界面/手机端用的 DDL 摘要</summary>
        public object BuildSummary()
        {
            var now = DateTime.Now;
            var all = GetOpenDeadlines();
            return all.Select(i => new
            {
                id = i.Id,
                title = i.Title,
                deadline = i.DeadlineAt?.ToString("o"),
                deadlineText = i.DeadlineText,
                overdue = i.DeadlineAt.HasValue && i.DeadlineAt.Value < now,
                relative = i.DeadlineAt.HasValue ? RelativeText(i.DeadlineAt.Value, now) : "",
            }).ToList();
        }
    }
}
