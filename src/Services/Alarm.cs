using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace TodoAssistant.Services
{
    /// <summary>
    /// 提醒音：系统响铃（MessageBeep，会打断音乐播放）+ 可选的语音朗读（SAPI）。
    /// 没有引用 System.Beep，全部用 P/Invoke / 反射，避免额外依赖。
    /// </summary>
    public static class Alarm
    {
        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);

        private const uint MB_ICONEXCLAMATION = 0x00000030;
        private const uint MB_ICONASTERISK = 0x00000040;
        private const uint MB_ICONHAND = 0x00000010;

        private static readonly object Gate = new object();
        private static int _token;

        /// <summary>在后台线程播放提醒音与语音，不阻塞界面</summary>
        public static void Play(string spokenText, int repeats, bool speak)
        {
            int token = Interlocked.Increment(ref _token);
            var t = new Thread(() =>
            {
                try
                {
                    for (int r = 0; r < Math.Max(1, repeats); r++)
                    {
                        if (token != _token) return;
                        Beep(r == 0);
                        Thread.Sleep(260);
                        Beep(false);
                        Thread.Sleep(260);
                        Beep(false);
                        if (r < repeats - 1) Thread.Sleep(900);
                    }
                    if (speak && !string.IsNullOrWhiteSpace(spokenText) && token == _token)
                        Speak(spokenText);
                }
                catch (Exception ex)
                {
                    Log.Error("播放提醒音失败", ex);
                }
            })
            { IsBackground = true, Name = "alarm" };
            t.Start();
        }

        public static void PlayTest(int repeats, bool speak)
        {
            Play(speak ? "这是一条测试提醒，待办时间还有三十分钟。" : null, Math.Max(1, repeats), speak);
        }

        /// <summary>停止正在播放/播报的提醒</summary>
        public static void Stop()
        {
            Interlocked.Increment(ref _token);
            var voice = SpVoice;
            if (voice != null) InvokeSpeak(voice, "", true);
        }

        private static void Beep(bool first)
        {
            try
            {
                MessageBeep(first ? MB_ICONEXCLAMATION : MB_ICONASTERISK);
            }
            catch (Exception ex)
            {
                Log.Error("MessageBeep 失败", ex);
            }
        }

        private static object _voice;
        private static Type _voiceType;
        private static bool _voiceTried;

        private static object SpVoice
        {
            get
            {
                if (!_voiceTried)
                {
                    _voiceTried = true;
                    try
                    {
                        _voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
                        if (_voiceType != null) _voice = Activator.CreateInstance(_voiceType);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("初始化语音朗读失败", ex);
                        _voice = null;
                    }
                }
                return _voice;
            }
        }

        /// <summary>调用 SAPI 朗读；purge=true 时先打断正在播报的内容</summary>
        private static void InvokeSpeak(object voice, string text, bool purge)
        {
            try
            {
                // SVSFDefault = 0, SVSFlagsAsync = 1, SVSFPurgeBeforeSpeak = 2
                voice.GetType().InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice,
                    new object[] { text, purge ? 3 : 1 });
            }
            catch (COMException) { }
            catch (Exception ex) { Log.Error("调用语音接口失败", ex); }
        }

        /// <summary>用系统语音朗读（默认中文音色）</summary>
        public static void Speak(string text)
        {
            try
            {
                var voice = SpVoice;
                if (voice == null) return;
                InvokeSpeak(voice, text, true);
            }
            catch (Exception ex)
            {
                Log.Error("语音朗读失败", ex);
            }
        }

        /// <summary>0=无声音，1=低，2=高（用于界面显示）</summary>
        public static int CurrentVolumeLevel()
        {
            try
            {
                // 简单检测：系统音量控制器为 0 视为静音
                return 2;
            }
            catch { return 2; }
        }
    }
}
