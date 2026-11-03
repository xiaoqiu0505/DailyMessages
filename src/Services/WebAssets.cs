using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TodoAssistant.Controls;

namespace TodoAssistant.Services
{
    /// <summary>为手机端生成图标（PNG，白色勾 + 日期 1 的样式，不带当天日期以便缓存）</summary>
    public static class WebAssets
    {
        public static void EnsureIcons(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var bg = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
                bg.Freeze();
                Write(Path.Combine(dir, "icon-192.png"), TrayIconFactory.CreatePng(192, "", bg, Brushes.White));
                Write(Path.Combine(dir, "icon-512.png"), TrayIconFactory.CreatePng(512, "", bg, Brushes.White));
                Write(Path.Combine(dir, "apple-touch-icon.png"), TrayIconFactory.CreatePng(180, "", bg, Brushes.White));
                Log.Write("图标已生成: " + dir);
            }
            catch (Exception ex)
            {
                Log.Error("生成网页图标失败", ex);
            }
        }

        private static void Write(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);
    }
}
