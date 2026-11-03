using System;

namespace TodoAssistant.Models
{
    /// <summary>全局设置（持久化到 settings.json）</summary>
    public sealed class AppSettings
    {
        /// <summary>提前提醒分钟数（所有待办默认值，也是全局提醒时间）</summary>
        public int LeadMinutes { get; set; } = 30;

        /// <summary>到点（开始时刻）也再提醒一次</summary>
        public bool NotifyAtStart { get; set; } = true;

        /// <summary>系统通知（Windows 右下角通知中心）</summary>
        public bool SystemToast { get; set; } = true;

        /// <summary>系统响铃（非静音，会打断当前播放）</summary>
        public bool SystemBeep { get; set; } = true;

        /// <summary>响铃重复次数（每次 3 声）</summary>
        public int BeepRepeat { get; set; } = 2;

        /// <summary>语音朗读待办内容</summary>
        public bool SpeakContent { get; set; } = true;

        /// <summary>开机自启</summary>
        public bool RunAtStartup { get; set; } = true;

        /// <summary>启动后显示主窗口（否则只驻留托盘）</summary>
        public bool ShowOnStartup { get; set; } = false;

        // ---------- 外观 ----------

        /// <summary>主题：0=跟随系统 1=浅色 2=深色</summary>
        public int ThemeMode { get; set; } = 0;

        // ---------- DDL 汇总提醒 ----------

        /// <summary>是否开启 DDL 每日汇总提醒</summary>
        public bool DdlDigestEnabled { get; set; } = true;

        /// <summary>早上汇总时间，HH:mm（默认 09:00）</summary>
        public string DdlDigestMorning { get; set; } = "09:00";

        /// <summary>晚上汇总时间，HH:mm（默认 21:00）</summary>
        public string DdlDigestEvening { get; set; } = "21:00";

        /// <summary>
        /// 汇总时提醒几天内的 DDL（默认 7 天）。
        /// 另外「今天到期」和「已过期」的 DDL 无论多远都会包含。
        /// </summary>
        public int DdlDigestDays { get; set; } = 7;

        /// <summary>最近一次早上汇总触发日期（yyyy-MM-dd），用于防重复</summary>
        public string DdlDigestMorningLast { get; set; } = "";

        /// <summary>最近一次晚上汇总触发日期</summary>
        public string DdlDigestEveningLast { get; set; } = "";

        // ---------- 手机端 ----------

        /// <summary>手机端网页服务器是否启用</summary>
        public bool PhoneServerEnabled { get; set; } = true;

        /// <summary>监听端口</summary>
        public int PhonePort { get; set; } = 8974;

        /// <summary>手机网页登录 PIN（空 = 不验证）</summary>
        public string PhonePin { get; set; } = "";

        /// <summary>通过 ntfy 推送到手机（离线也能响铃）</summary>
        public bool NtfyEnabled { get; set; } = true;

        /// <summary>ntfy 主题（相当于频道名）</summary>
        public string NtfyTopic { get; set; } = "";

        /// <summary>ntfy 服务器地址（中国大陆访问 ntfy.sh 可能较慢，可换自建服务器）</summary>
        public string NtfyServer { get; set; } = "https://ntfy.sh";

        /// <summary>ntfy 推送优先使用最高优先级（叮咚响铃 + 震动）</summary>
        public bool NtfyHighPriority { get; set; } = true;

        /// <summary>最近一次是否自动补齐了手机相关设置</summary>
        public bool IsPhoneConfigured =>
            PhoneServerEnabled || (NtfyEnabled && !string.IsNullOrWhiteSpace(NtfyTopic));

        public AppSettings Clone() => (AppSettings)MemberwiseClone();

        /// <summary>首次运行补齐 PIN 与 ntfy 主题</summary>
        public void EnsurePhoneSetup()
        {
            if (string.IsNullOrWhiteSpace(PhonePin))
                PhonePin = RandomCode(6);
            if (string.IsNullOrWhiteSpace(NtfyTopic))
                NtfyTopic = "tododooo-" + Guid.NewGuid().ToString("N").Substring(0, 10);
            if (PhonePort < 1024 || PhonePort > 65535) PhonePort = 8974;
        }

        public static string RandomCode(int len)
        {
            const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";   // 去掉易混淆字符
            var rng = new Random();
            var chars = new char[len];
            for (int i = 0; i < len; i++) chars[i] = alphabet[rng.Next(alphabet.Length)];
            return new string(chars);
        }
    }

    /// <summary>数据文件根结构</summary>
    public sealed class DataSnapshot
    {
        public int Schema { get; set; } = 1;
        public DateTime LastModified { get; set; } = DateTime.Now;
    }
}
