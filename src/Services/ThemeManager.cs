using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace TodoAssistant.Services
{
    /// <summary>主题：跟随系统 / 浅色 / 深色</summary>
    public enum AppTheme
    {
        Auto = 0,
        Light = 1,
        Dark = 2,
    }

    /// <summary>
    /// 主题管理。
    /// 实现方式：不替换资源字典（那样已编译进 BAML 的引用不会更新），
    /// 而是直接往 Application.Resources 里写入每种颜色键的新画刷值。
    /// 因为界面全部用 DynamicResource 引用这些键，写入后立即全局生效。
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>浅色配色</summary>
        private static readonly Dictionary<string, string> Light = new()
        {
            ["AccentBrush"] = "#3B82F6",
            ["AccentHoverBrush"] = "#2F6FE0",
            ["AccentPressedBrush"] = "#2560C8",
            ["AccentSoftBrush"] = "#EAF1FF",
            ["AccentTextBrush"] = "#3B82F6",
            ["WinBackgroundBrush"] = "#F6F8FC",
            ["CardBrush"] = "#FFFFFF",
            ["CardHoverBrush"] = "#F8FAFE",
            ["ElevatedBrush"] = "#FFFFFF",
            ["TextBrush"] = "#111827",
            ["MutedBrush"] = "#6B7280",
            ["FaintBrush"] = "#9CA3AF",
            ["LineBrush"] = "#E6EBF3",
            ["GreenBrush"] = "#16A34A",
            ["RedBrush"] = "#EF4444",
            ["AmberBrush"] = "#F59E0B",
            ["SoonBgBrush"] = "#FFFCF3",
            ["PastBgBrush"] = "#FFF8F8",
            ["DoneBgBrush"] = "#FAFBFD",
            ["ChipBgBrush"] = "#F1F5FB",
            ["ChipWarnBgBrush"] = "#FFF7E6",
            ["ChipWarnTextBrush"] = "#B45309",
            ["HintBgBrush"] = "#F2F7FF",
            ["HintTextBrush"] = "#3B6BB8",
            ["InputBgBrush"] = "#F8FAFE",
            ["SoftButtonBrush"] = "#EEF2F8",
            ["SoftButtonHoverBrush"] = "#E1E8F4",
            ["DangerSoftBrush"] = "#FEECEC",
            ["ScrollThumbBrush"] = "#CBD5E1",
            ["ScrollThumbHoverBrush"] = "#94A3B8",
            ["DdlBgBrush"] = "#FDF2F8",
            ["DdlBorderBrush"] = "#F9A8D4",
            ["DdlTextBrush"] = "#BE185D",
            ["OnAccentBrush"] = "#FFFFFF",
        };

        /// <summary>深色配色（键与浅色完全一致）</summary>
        private static readonly Dictionary<string, string> Dark = new()
        {
            ["AccentBrush"] = "#4C8DF6",
            ["AccentHoverBrush"] = "#5F9BFF",
            ["AccentPressedBrush"] = "#3B7AE0",
            ["AccentSoftBrush"] = "#1C2740",
            ["AccentTextBrush"] = "#7CAEF8",
            ["WinBackgroundBrush"] = "#0F1117",
            ["CardBrush"] = "#191C24",
            ["CardHoverBrush"] = "#20242E",
            ["ElevatedBrush"] = "#1E222B",
            ["TextBrush"] = "#E8EBF0",
            ["MutedBrush"] = "#9AA3B2",
            ["FaintBrush"] = "#6B7280",
            ["LineBrush"] = "#2A2F3A",
            ["GreenBrush"] = "#34D399",
            ["RedBrush"] = "#F87171",
            ["AmberBrush"] = "#FBBF24",
            ["SoonBgBrush"] = "#2A2418",
            ["PastBgBrush"] = "#2A1B1B",
            ["DoneBgBrush"] = "#15171D",
            ["ChipBgBrush"] = "#232833",
            ["ChipWarnBgBrush"] = "#2E2617",
            ["ChipWarnTextBrush"] = "#FBBF24",
            ["HintBgBrush"] = "#18202E",
            ["HintTextBrush"] = "#8FB6F0",
            ["InputBgBrush"] = "#14171E",
            ["SoftButtonBrush"] = "#232833",
            ["SoftButtonHoverBrush"] = "#2C323F",
            ["DangerSoftBrush"] = "#2E1A1A",
            ["ScrollThumbBrush"] = "#3A4150",
            ["ScrollThumbHoverBrush"] = "#4C5568",
            ["DdlBgBrush"] = "#2B1A25",
            ["DdlBorderBrush"] = "#7A2C50",
            ["DdlTextBrush"] = "#F9A8D4",
            ["OnAccentBrush"] = "#FFFFFF",
        };

        public static AppTheme Current { get; private set; } = AppTheme.Auto;
        public static bool IsDark { get; private set; }

        public static event EventHandler Changed;

        public static bool SystemPrefersDark()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int i) return i == 0;
            }
            catch { }
            return false;
        }

        public static bool Resolve(AppTheme theme) => theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemPrefersDark(),
        };

        /// <summary>解析 "#RRGGBB" 为画刷</summary>
        public static SolidColorBrush BrushOf(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>应用主题：直接改写 Application.Resources 里的颜色画刷</summary>
        public static void Apply(AppTheme theme)
        {
            Current = theme;
            bool dark = Resolve(theme);
            IsDark = dark;

            var palette = dark ? Dark : Light;
            var app = Application.Current;
            if (app != null)
            {
                foreach (var kv in palette)
                    app.Resources[kv.Key] = BrushOf(kv.Value);
            }

            Changed?.Invoke(null, EventArgs.Empty);
            Log.Write($"主题已切换: {DisplayName(theme)}（实际深色={dark}）");
        }

        public static string DisplayName(AppTheme theme) => theme switch
        {
            AppTheme.Light => "浅色",
            AppTheme.Dark => "深色",
            _ => "跟随系统",
        };
    }
}
