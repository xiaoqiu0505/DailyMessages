using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TodoAssistant.Models;

namespace TodoAssistant.Services
{
    /// <summary>
    /// 待办数据仓库：内存列表 + 原子写盘（todos.json）。
    /// 所有修改都必须调用 <see cref="Save"/> 并通过 <see cref="Changed"/> 通知界面刷新。
    /// </summary>
    public sealed class TodoStore
    {
        private readonly object _gate = new object();
        private readonly List<TodoItem> _items = new List<TodoItem>();

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        public string DataDirectory { get; }

        /// <summary>数据变化（保存后触发）</summary>
        public event EventHandler Changed;

        /// <summary>在 UI 线程之外保存后，用这个派发到 UI 线程</summary>
        public Action<Action> Dispatch { get; set; } = a => a();

        public TodoStore(string dataDirectory)
        {
            DataDirectory = dataDirectory;
            Directory.CreateDirectory(dataDirectory);
        }

        private string TodosFile => Path.Combine(DataDirectory, "todos.json");
        private string TodosBackup => Path.Combine(DataDirectory, "todos.bak.json");

        // ---------------- 读取 ----------------

        public void Load()
        {
            lock (_gate)
            {
                _items.Clear();
                foreach (string path in new[] { TodosFile, TodosBackup })
                {
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var json = File.ReadAllText(path);
                        var list = JsonSerializer.Deserialize<List<TodoItem>>(json, JsonOptions);
                        if (list == null) continue;
                        foreach (var it in list)
                        {
                            if (it == null) continue;
                            if (!it.Normalize()) continue;   // 丢弃损坏记录
                            _items.Add(it);
                        }
                        break; // 主文件成功就不再读备份
                    }
                    catch (JsonException)
                    {
                        // 主文件损坏 → 尝试备份
                        continue;
                    }
                }
            }
            PumpRollingDueDates(DateTime.Now);
        }

        // ---------------- 查询 ----------------

        /// <summary>返回 [fromDate, toDate] 内（含重复展开）的所有待办，按开始时间排序</summary>
        public List<TodoItem> GetRange(DateTime fromDate, DateTime toDate)
        {
            lock (_gate)
            {
                var list = new List<TodoItem>();
                foreach (var it in _items)
                    list.AddRange(it.Expand(fromDate, toDate));
                return Sort(list);
            }
        }

        private static List<TodoItem> Sort(List<TodoItem> list) => list
            .OrderBy(i => i.StartTime ?? DateTime.MaxValue)
            .ThenBy(i => i.Title, StringComparer.CurrentCulture)
            .ToList();

        /// <summary>直接（非展开）命中某一天的记录，重复待办只出现一次</summary>
        public List<TodoItem> GetDay(DateTime day) => GetRange(day, day);

        public List<TodoItem> GetAllRaw()
        {
            lock (_gate) return _items.Select(i => i).ToList();
        }

        public TodoItem FindRaw(string id)
        {
            lock (_gate) return _items.FirstOrDefault(i => i.Id == id);
        }

        /// <summary>统计某天未完成的条数（含重复展开）</summary>
        public int CountOpen(DateTime day) => GetRange(day, day).Count(i => !i.Done);

        // ---------------- 修改 ----------------

        /// <summary>新增或整体替换（按 Id 匹配）。返回实际存入的对象。</summary>
        public TodoItem Upsert(TodoItem incoming, string targetId = null)
        {
            if (!incoming.Normalize()) throw new ArgumentException("日期或时间格式不正确");
            TodoItem saved;
            lock (_gate)
            {
                var id = targetId ?? incoming.Id;
                var idx = _items.FindIndex(i => i.Id == id);
                if (idx >= 0)
                {
                    var existing = _items[idx];
                    incoming.Id = existing.Id;
                    incoming.CreatedAt = existing.CreatedAt;
                    // 编辑时间/内容后，提醒状态重置，可再次触发
                    bool changed = existing.Date != incoming.Date || existing.Start != incoming.Start
                                   || existing.LeadMinutes != incoming.LeadMinutes;
                    if (changed) incoming.ReminderSent = incoming.StartNotificationSent = incoming.CatchUpPending = false;
                    incoming.UpdatedAt = DateTime.Now;
                    _items[idx] = incoming;
                    saved = incoming;
                }
                else
                {
                    // 按「同一天同标题同开始时间」去重，防止手机端重复提交
                    int dup = _items.FindIndex(i => i.Title == incoming.Title && i.Date == incoming.Date && i.Start == incoming.Start);
                    if (dup >= 0)
                    {
                        incoming.Id = _items[dup].Id;
                        incoming.CreatedAt = _items[dup].CreatedAt;
                        _items[dup] = incoming;
                        saved = incoming;
                    }
                    else
                    {
                        incoming.CreatedAt = DateTime.Now;
                        incoming.UpdatedAt = DateTime.Now;
                        _items.Add(incoming);
                        saved = incoming;
                    }
                }
            }
            Save();
            return saved;
        }

        public bool SetDone(string id, bool done, DateTime? at = null)
        {
            bool ok;
            lock (_gate)
            {
                var it = _items.FirstOrDefault(i => i.Id == id);
                if (it == null) return false;
                it.Done = done;
                it.DoneAt = done ? (at ?? DateTime.Now) : (DateTime?)null;
                it.UpdatedAt = DateTime.Now;
                ok = true;
            }
            if (ok) Save();
            return ok;
        }

        public bool SetFlag(string id, Func<TodoItem, bool> mutate)
        {
            bool ok;
            lock (_gate)
            {
                var it = _items.FirstOrDefault(i => i.Id == id);
                if (it == null) return false;
                ok = mutate(it);
            }
            return ok;
        }

        public bool Delete(string id)
        {
            bool ok;
            lock (_gate) ok = _items.RemoveAll(i => i.Id == id) > 0;
            if (ok) Save();
            return ok;
        }

        /// <summary>把待办挪到另一天（同一时间）</summary>
        public bool MoveToDay(string id, DateTime newDay)
        {
            bool ok;
            lock (_gate)
            {
                var it = _items.FirstOrDefault(i => i.Id == id);
                if (it == null) return false;
                it.Date = newDay.ToString("yyyy-MM-dd");
                it.ReminderSent = it.StartNotificationSent = it.CatchUpPending = false;
                it.UpdatedAt = DateTime.Now;
                ok = true;
            }
            if (ok) Save();
            return ok;
        }

        /// <summary>
        /// 重复待办的「勾选」：把重复规则推进到下一次，并把今天这条标记完成。
        /// 做法：把原记录日期改成明天起的那次，同时生成一条今天的已完成记录（用于历史显示）。
        /// </summary>
        public void CompleteRecurringInstance(TodoItem instance, bool done, DateTime today)
        {
            lock (_gate)
            {
                var master = _items.FirstOrDefault(i => i.Id == instance.Id);
                if (master == null) { }
                else
                {
                    var day = DateTime.Parse(instance.Date);
                    // 主记录推进到下一天之后
                    var next = day.AddDays(1);
                    while (!master.OccursOn(next)) next = next.AddDays(1);
                    master.Date = next.ToString("yyyy-MM-dd");
                    master.ReminderSent = master.StartNotificationSent = master.CatchUpPending = false;
                    master.UpdatedAt = DateTime.Now;
                }
                // 今天的这次单独记一条已完成（不带重复），便于查看历史
                var copy = instance.CopyForAnotherDate(DateTime.Parse(instance.Date));
                copy.Id = Guid.NewGuid().ToString("N");
                copy.Done = done;
                copy.DoneAt = done ? DateTime.Now : (DateTime?)null;
                if (!_items.Any(i => i.Id == copy.Id)) _items.Add(copy);
            }
            Save();
        }

        // ---------------- 写入 ----------------

        public void Save()
        {
            List<TodoItem> copy;
            lock (_gate) copy = _items.ToList();
            try
            {
                Directory.CreateDirectory(DataDirectory);
                string json = JsonSerializer.Serialize(copy, JsonOptions);
                atomicWrite(TodosFile, json);
            }
            catch (Exception ex)
            {
                Log.Write("保存待办失败: " + ex.Message);
            }
            try { File.WriteAllText(Path.Combine(DataDirectory, "lastmodified.txt"), DateTime.Now.ToString("o")); }
            catch { /* 忽略 */ }
            var handler = Changed;
            if (handler != null) Dispatch(() => handler(this, EventArgs.Empty));
        }

        private static void atomicWrite(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, path + ".bak");
                    // .bak 保留一份上一个版本
                    var bakNew = path + ".bak";
                    if (File.Exists(bakNew)) File.Copy(bakNew, path.Replace(".json", ".bak.json"), true);
                    return;
                }
                catch (IOException)
                {
                    // 某些文件系统不支持 Replace，退化为覆盖
                }
                catch (PlatformNotSupportedException) { }
            }
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        /// <summary>清理太旧的已完成记录（保留 30 天）</summary>
        public void PurgeOld(DateTime now)
        {
            int removed;
            lock (_gate)
            {
                removed = _items.RemoveAll(i =>
                {
                    if (!i.Done || i.Repeat != RepeatMode.None) return false;
                    if (!TodoItem.TryParseDate(i.Date, out var d)) return true;
                    return d.Date < now.Date.AddDays(-30);
                });
            }
            if (removed > 0) Save();
        }

        /// <summary>已过期的重复待办，把主记录日期推进到今天之后</summary>
        public void PumpRollingDueDates(DateTime now)
        {
            bool changed = false;
            lock (_gate)
            {
                foreach (var it in _items)
                {
                    if (it.Repeat == RepeatMode.None) continue;
                    if (!TodoItem.TryParseDate(it.Date, out var d)) continue;
                    if (d.AddDays(1) >= now.Date) continue;   // 今天及以后不处理
                    var next = now.Date;
                    int guard = 0;
                    while (!it.OccursOn(next) && guard++ < 400) next = next.AddDays(1);
                    it.Date = next.ToString("yyyy-MM-dd");
                    it.Done = false;
                    it.ReminderSent = it.StartNotificationSent = it.CatchUpPending = false;
                    changed = true;
                }
            }
            if (changed) Save();
        }
    }
}
