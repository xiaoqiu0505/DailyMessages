using System;
using System.IO;
using TodoAssistant.Controls;

namespace TodoAssistant.Services
{
    /// <summary>极简文件日志（data/app.log），便于排查启动/托盘/网络问题。</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _file;

        public static void Init(string dataDirectory)
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                _file = Path.Combine(dataDirectory, "app.log");
                try { File.AppendAllText(_file, ""); } catch { }
                // 日志超过 2MB 就滚动一次
                if (File.Exists(_file) && new FileInfo(_file).Length > 2 * 1024 * 1024)
                {
                    try { File.Move(_file, _file + ".old", true); } catch { }
                }
            }
            catch { _file = null; }
        }

        private static System.IO.StreamWriter _writer;

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message;
            lock (Gate)
            {
                try
                {
                    if (_file == null) { Console.Error.WriteLine(line); return; }
                    if (_writer == null)
                        _writer = new System.IO.StreamWriter(new System.IO.FileStream(
                            _file, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite),
                            new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                    _writer.WriteLine(line);
                }
                catch
                {
                    try { _writer?.Dispose(); } catch { }
                    _writer = null;
                }
            }
        }

        public static void Error(string message, Exception ex)
        {
            Write("[ERROR] " + message + (ex == null ? "" : "  " + ex.GetType().Name + ": " + ex.Message +
                                                               Environment.NewLine + ex.StackTrace));
        }

        public static string ReadTail(int lines = 200)
        {
            try
            {
                if (_file == null || !File.Exists(_file)) return "(无日志)";
                var all = File.ReadAllLines(_file);
                int start = Math.Max(0, all.Length - lines);
                return string.Join(Environment.NewLine, all, start, all.Length - start);
            }
            catch (Exception ex) { return "(读取日志失败: " + ex.Message + ")"; }
        }

        public static string FilePath => _file;
    }
}
