using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TodoAssistant.Models;

namespace TodoAssistant.Services
{
    /// <summary>一次到点的提醒事件</summary>
    public sealed class ReminderEvent
    {
        public TodoItem Item { get; set; }
        public ReminderKind Kind { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
        public string Speech { get; set; }
    }

    public enum ReminderKind
    {
        /// <summary>距开始还剩 N 分钟</summary>
        Upcoming,
        /// <summary>到开始时间了</summary>
        Starting,
        /// <summary>启动时补发的提醒</summary>
        CatchUp,
        /// <summary>DDL 每日汇总（早上 / 晚上）</summary>
        DeadlineDigest,
        /// <summary>某条 DDL 的单独提前提醒</summary>
        DeadlineSoon,
    }

    /// <summary>
    /// 提醒调度器：
    /// 1. 每 10 秒扫描今天 + 明天（以及可选的昨天跨零点）的待办；
    /// 2. 距开始时间剩余「提前量」（默认 30 分钟）时触发一次；
    /// 3. 到开始时刻再触发一次（可关）；
    /// 4. 程序没运行错过了提醒点 → 启动时补发一次；
    /// 5. 每天 0:00 清零标记；0:00-8:00 不补发夜间错过的事项（避免开机轰炸）。
    /// </summary>
    public sealed class ReminderScheduler
    {
        private readonly TodoStore _store;
        private readonly SettingsStore _settings;
        private readonly System.Windows.Threading.DispatcherTimer _timer;

        /// <summary>每个「实例键」本进程内已提醒过的时间标记点（防重复）</summary>
        private readonly ConcurrentDictionary<string, DateTime> _fired = new(StringComparer.Ordinal);
        private DateTime? _lastDispatchDay;

        /// <summary>新的提醒（由 App 订阅，负责通知/响铃/推送）</summary>
        public event Action<ReminderEvent> Fire;

        /// <summary>每次扫描时顺带执行的回调（App 用它跑 DDL 汇总检查）</summary>
        public event Action DeadlineTick;

        public ReminderScheduler(TodoStore store, SettingsStore settings)
        {
            _store = store;
            _settings = settings;
            _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(10),
            };
            _timer.Tick += (s, e) => Tick(false);
        }

        public void Start()
        {
            Tick(true);   // 启动时先补发
            _timer.Start();
        }

        public void Stop() => _timer.Stop();

        /// <summary>数据被修改（比如改了提醒时间）后立即重算</summary>
        public void OnDataChanged() => Tick(true);

        private static string Key(TodoItem it, string kind) => it.Id + "|" + it.Date + "|" + kind;

        /// <summary>
        /// 扫描一次。catchUp=true 表示允许补发（启动时 / 数据变更时 / 零点后）。
        /// </summary>
        public void Tick(bool catchUp)
        {
            try
            {
                var now = DateTime.Now;
                var settings = _settings.Current;

                if (_lastDispatchDay != now.Date)
                {
                    _lastDispatchDay = now.Date;
                    // 进程运行跨越 0 点：新的一天允许补发一次（但夜间不打扰）
                    if (now.Hour >= 8) catchUp = true;
                }

                // 1) 已过期的重复待办推进
                _store.PumpRollingDueDates(now);

                // 1.5) DDL 提醒（每日早/晚汇总 + 单条提前提醒）
                DeadlineTick?.Invoke();

                // 2) 收集候选（今天、明天、昨晚跨零点仍在进行中的）
                // 覆盖：昨天（跨零点仍在进行的）+ 今天起 5 天
                var from = now.Date.AddDays(-1);
                var to = now.Date.AddDays(4);
                var candidates = _store.GetRange(from, to);

                var due = new List<ReminderEvent>();

                foreach (var it in candidates)
                {
                    var start = it.StartTime;
                    if (!start.HasValue) continue;
                    if (it.Done) continue;

                    int lead = it.EffectiveLead;

                    // -------- 30 分钟提醒 --------
                    bool leadFired = _fired.ContainsKey(Key(it, "lead"));
                    if (!leadFired && !it.ReminderSent && start.Value > now)
                    {
                        DateTime leadAt = start.Value.AddMinutes(-lead);
                        if (now >= leadAt)
                        {
                            _fired[Key(it, "lead")] = now;
                            MarkSent(it, "lead");
                            int remain = (int)Math.Max(1, Math.Round((start.Value - now).TotalMinutes));
                            due.Add(Build(it, remain, false));
                        }
                        else if (catchUp && it.CatchUpPending && now.Hour >= 8)
                        {
                            // 程序没运行时错过了提醒点：启动后补发一次
                            _fired[Key(it, "lead")] = now;
                            MarkSent(it, "lead");
                            int remain = (int)Math.Max(1, Math.Round((start.Value - now).TotalMinutes));
                            due.Add(Build(it, remain, true));
                        }
                    }

                    // -------- 到点提醒 --------
                    if (settings.NotifyAtStart && !it.StartNotificationSent && !_fired.ContainsKey(Key(it, "start")))
                    {
                        var end = it.EndTime ?? start.Value.AddMinutes(30);
                        if (now >= start.Value && now < start.Value.AddMinutes(3))
                        {
                            _fired[Key(it, "start")] = now;
                            MarkSent(it, "start");
                            due.Add(new ReminderEvent
                            {
                                Item = it,
                                Kind = ReminderKind.Starting,
                                Title = "时间到了 · " + it.Title,
                                Body = $"{it.Date} {it.Start}" + (string.IsNullOrEmpty(it.End) ? "" : "-" + it.End) +
                                       (string.IsNullOrWhiteSpace(it.Notes) ? "" : "　" + it.Notes),
                                Speech = $"现在是{start.Value.Hour}点{start.Value.Minute:00}分，该做「{it.Title}」了。",
                            });
                        }
                    }
                }

                // 3) 清理过期的去重键，避免字典无限增长
                if (_fired.Count > 500)
                {
                    var keepAfter = now.Date.AddDays(-2);
                    foreach (var kv in _fired.ToArray())
                        if (kv.Value < keepAfter) _fired.TryRemove(kv.Key, out _);
                }

                foreach (var ev in due)
                {
                    Log.Write($"提醒触发[{ev.Kind}]: {ev.Title} | {ev.Body}");
                    try { Fire?.Invoke(ev); }
                    catch (Exception ex) { Log.Error("处理提醒事件失败", ex); }
                }
            }
            catch (Exception ex)
            {
                Log.Error("提醒调度异常", ex);
            }
        }

        private ReminderEvent Build(TodoItem it, int remain, bool isCatchUp)
        {
            var start = it.StartTime.Value;
            string body = $"{start:MM月dd日 HH:mm}-{(string.IsNullOrEmpty(it.End) ? "?" : it.End)}";
            if (!string.IsNullOrWhiteSpace(it.Notes)) body += "　" + it.Notes;

            string title = isCatchUp
                ? (remain > 1 ? $"还有约 {remain} 分钟 · {it.Title}" : $"即将开始 · {it.Title}")
                : $"还有 {remain} 分钟 · {it.Title}";

            string speech = $"{start.Hour}点{start.Minute:00}分有「{it.Title}」" + (remain > 1 ? $"，还有{remain}分钟。" : "，马上开始。");

            return new ReminderEvent
            {
                Item = it,
                Kind = isCatchUp ? ReminderKind.CatchUp : ReminderKind.Upcoming,
                Title = title,
                Body = body,
                Speech = speech,
            };
        }

        /// <summary>把标记落盘（重复待办的实例只记在内存，主记录不落盘）</summary>
        private void MarkSent(TodoItem instance, string kind)
        {
            if (instance.Repeat != RepeatMode.None) return;   // 重复实例不写回主记录
            _store.SetFlag(instance.Id, it =>
            {
                if (kind == "lead") { it.ReminderSent = true; it.CatchUpPending = false; }
                else it.StartNotificationSent = true;
                return true;
            });
            _store.Save();
        }

        /// <summary>
        /// 应用启动时：找出「提醒点已过但还没到开始时间」的今日事项，打上补发标记，
        /// 供随后的 <see cref="Tick"/> 补发一次（夜间 0-8 点不补发，避免开机轰炸）。
        /// </summary>
        public void PrepareCatchUp(DateTime now)
        {
            try
            {
                foreach (var it in _store.GetRange(now.Date, now.Date))
                {
                    if (it.Done) continue;
                    var start = it.StartTime;
                    if (!start.HasValue) continue;
                    var leadAt = start.Value.AddMinutes(-it.EffectiveLead);
                    if (now >= leadAt && now < start.Value && !it.ReminderSent)
                    {
                        if (it.Repeat != RepeatMode.None) { it.CatchUpPending = true; continue; }
                        _store.SetFlag(it.Id, x => { x.CatchUpPending = true; return true; });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("准备补发提醒失败", ex);
            }
        }

        /// <summary>统计今天还剩多少待办</summary>
        public (int open, int total) TodaySummary()
        {
            var today = _store.GetRange(DateTime.Today, DateTime.Today);
            return (today.Count(i => !i.Done), today.Count);
        }
    }
}
