using System;
using System.IO;
using System.Text.Json;
using TodoAssistant.Models;

namespace TodoAssistant.Services
{
    /// <summary>设置读写（settings.json）</summary>
    public sealed class SettingsStore
    {
        private readonly string _path;
        private readonly string _dir;

        public AppSettings Current { get; set; } = new AppSettings();

        public SettingsStore(string dataDirectory)
        {
            _dir = dataDirectory;
            _path = Path.Combine(dataDirectory, "settings.json");
        }

        public void Load()
        {
            AppSettings s = null;
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    s = JsonSerializer.Deserialize<AppSettings>(json, TodoStore.JsonOptions);
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取设置失败，使用默认设置", ex);
                try { File.Copy(_path, _path + ".broken." + DateTime.Now.ToString("yyyyMMddHHmmss"), true); }
                catch { }
            }
            Current = s ?? new AppSettings();
            Current.EnsurePhoneSetup();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(Current, TodoStore.JsonOptions));
            }
            catch (Exception ex)
            {
                Log.Error("保存设置失败", ex);
            }
        }
    }
}
