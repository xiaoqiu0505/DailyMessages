using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TodoAssistant.Controls
{
    /// <summary>
    /// 用代码绘制系统托盘 / 窗口图标（不依赖外部图片文件）：
    /// 圆角方块 + 复选框 + 当天日期，托盘图标会随日期变化。
    /// </summary>
    public static class TrayIconFactory
    {
        private static readonly Dictionary<string, System.Drawing.Icon> _cache =
            new Dictionary<string, System.Drawing.Icon>();

        private static readonly Brush NormalBackground = Frozen(0xFF, 0x3B, 0x82, 0xF6);   // 蓝
        private static readonly Brush NormalForeground = Brushes.White;
        private static readonly Brush AlertBackground = Frozen(0xFF, 0xEF, 0x44, 0x44);   // 红（有待办临近）

        /// <summary>生成托盘用 Icon。alert=true 时底色变红。</summary>
        public static System.Drawing.Icon GetTrayIcon(bool alert)
        {
            string key = (alert ? "alert:" : "normal:") + DateTime.Now.ToString("MMdd");
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var icon = Create(16, 32, 48, 256, DateTime.Now.Day.ToString(CultureInfo.InvariantCulture),
                              alert ? AlertBackground : NormalBackground, NormalForeground);
            _cache[key] = icon;
            return icon;
        }

        /// <summary>把多尺寸 Image 拼成 .ico 字节。</summary>
        public static byte[] CreateIcoBytes(int[] sizes, string text, Brush bg, Brush fg)
        {
            var images = new List<(int Size, byte[] Data)>();
            foreach (int s in sizes)
                images.Add((s, EncodePng(Render(s, text, bg, fg))));

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((short)0);          // reserved
            w.Write((short)1);          // type = icon
            w.Write((short)images.Count);
            int offset = 6 + 16 * images.Count;
            foreach (var (size, data) in images)
            {
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)0);       // color count
                w.Write((byte)0);       // reserved
                w.Write((short)1);      // planes
                w.Write((short)32);     // bit count
                w.Write(data.Length);
                w.Write(offset);
                offset += data.Length;
            }
            foreach (var (_, data) in images)
                w.Write(data);
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>生成多尺寸 .ico 的 Icon 对象。注意：调用方不要释放，内部缓存复用。</summary>
        public static System.Drawing.Icon Create(int size1, int size2, int size3, int size4, string text, Brush bg, Brush fg)
        {
            var sizes = new[] { size1, size2, size3, size4 };
            Array.Sort(sizes);
            byte[] bytes = CreateIcoBytes(sizes, text, bg, fg);
            using var ms = new MemoryStream(bytes);
            using var tmp = new System.Drawing.Icon(ms, sizes[sizes.Length - 1], sizes[sizes.Length - 1]);
            // Clone 以脱离 Stream 生命周期（否则 Icon 失效）
            return (System.Drawing.Icon)tmp.Clone();
        }

        /// <summary>生成 PNG 字节（用于网页端 / 通知图标）。</summary>
        public static byte[] CreatePng(int size, string text, Brush bg, Brush fg)
            => EncodePng(Render(size, text, bg, fg));

        private static BitmapSource Render(int size, string text, Brush bg, Brush fg)
        {
            double s = size;
            var dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                // 背景圆角方块（留一点边距，避免 16px 下糊边）
                double pad = Math.Max(0.5, s * 0.02);
                double r = s * 0.24;
                dc.DrawRoundedRectangle(bg, null, new Rect(pad, pad, s - 2 * pad, s - 2 * pad), r, r);

                // 上半部：复选框轮廓 + 对勾
                double cbW = s * 0.44;
                double cbX = s * 0.16;
                double cbY = s * 0.16;
                double cbH = cbW;
                double pen = Math.Max(1.2, s * 0.075);
                var whitePen = new Pen(fg, pen) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                whitePen.Freeze();
                double rr = cbW * 0.28;
                dc.DrawRoundedRectangle(null, whitePen, new Rect(cbX, cbY, cbW, cbH), rr, rr);

                var tick = new StreamGeometry();
                using (var gc = tick.Open())
                {
                    gc.BeginFigure(new Point(cbX + cbW * 0.22, cbY + cbH * 0.52), false, false);
                    gc.LineTo(new Point(cbX + cbW * 0.44, cbY + cbH * 0.74), true, true);
                    gc.LineTo(new Point(cbX + cbW * 0.80, cbY + cbH * 0.28), true, true);
                }
                tick.Freeze();
                dc.DrawGeometry(null, whitePen, tick);

                // 下半部：日期数字
                if (!string.IsNullOrEmpty(text))
                {
                    var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                    var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, s * 0.40, fg, 1.0);
                    // 水平铺满，垂直放在下半区
                    double targetW = s * 0.66;
                    double scale = targetW / Math.Max(1, ft.Width);
                    var dv2 = new DrawingVisual();
                    using (var c2 = dv2.RenderOpen())
                    {
                        c2.PushTransform(new ScaleTransform(scale, scale));
                        c2.DrawText(ft, new Point(0, 0));
                        c2.Pop();
                    }
                    var bmpText = new RenderTargetBitmap((int)Math.Ceiling(ft.Width * scale) + 2, (int)Math.Ceiling(ft.Height * scale) + 2,
                        96, 96, PixelFormats.Pbgra32);
                    bmpText.Render(dv2);
                    double ty = s * 0.50 + Math.Max(0, (s * 0.46 - ft.Height * scale) / 2);
                    double tx = (s - ft.Width * scale) / 2;
                    dc.DrawImage(bmpText, new Rect(tx, ty, ft.Width * scale, ft.Height * scale));
                }
            }

            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private static byte[] EncodePng(BitmapSource src)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static Brush Frozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
