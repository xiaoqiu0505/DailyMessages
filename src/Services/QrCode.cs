using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TodoAssistant.Services
{
    /// <summary>
    /// 二维码生成器（QR Code Model 2，Byte 模式，版本 1-10，纠错等级 M）。
    /// 只用于「扫码打开手机版网页」，因此独立实现，不依赖第三方包。
    /// </summary>
    public static class QrCode
    {
        private const int EccLevel = 0;   // 0 = M

        // 版本 1-10、纠错等级 M 的参数表（索引 0 = 版本 1）
        private static readonly int[] VersionTotalCodewords = { 26, 44, 70, 100, 134, 172, 196, 242, 292, 346 };
        private static readonly int[] VersionEccPerBlock = { 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        private static readonly int[] VersionNumBlocks = { 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };

        // 功能模块布局用图（不依赖版本）:
        // -1 = 不在掩码区，0 = 功能图形，1 = 格式信息，2 = 版本信息
        private static readonly int[][] FunctionMaskTables = new[]
        {
            FunctionPatternTable(17, 0), FunctionPatternTable(21, 0), FunctionPatternTable(25, 0),
            FunctionPatternTable(29, 0), FunctionPatternTable(33, 0), FunctionPatternTable(37, 0),
            FunctionPatternTable(41, 1), FunctionPatternTable(45, 1), FunctionPatternTable(49, 1),
            FunctionPatternTable(53, 1),
        };

        /// <summary>生成指定像素尺寸的白底二维码位图</summary>
        public static BitmapSource Render(string text, int pixelSize)
        {
            bool[,] matrix = Encode(text ?? "");
            int size = matrix.GetLength(0);
            const int quiet = 4;
            int total = size + quiet * 2;
            int modulePixels = Math.Max(2, pixelSize / total);
            int dim = total * modulePixels;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, dim, dim));
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        if (matrix[y, x])
                            dc.DrawRectangle(Brushes.Black, null, new System.Windows.Rect(
                                (x + quiet) * modulePixels, (y + quiet) * modulePixels, modulePixels, modulePixels));
            }
            var rtb = new RenderTargetBitmap(dim, dim, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>生成模块矩阵，true = 黑</summary>
        public static bool[,] Encode(string text)
        {
            InitGf();
            byte[] data = Encoding.UTF8.GetBytes(text);

            int version = 1;
            for (; version <= 10; version++)
            {
                int capacity = DataCodewords(version) * 8;
                if (capacity >= 4 + CharCountBits(version) + data.Length * 8) break;
            }
            if (version > 10) throw new ArgumentException("内容过长，无法生成二维码");

            // ---- 数据位流 ----
            var bits = new List<bool>();
            AppendBits(bits, 0x4, 4);                               // Byte 模式
            AppendBits(bits, data.Length, CharCountBits(version));  // 字符数
            foreach (byte b in data) AppendBits(bits, b, 8);

            int capacityBits = DataCodewords(version) * 8;
            int terminator = Math.Min(4, capacityBits - bits.Count);
            AppendBits(bits, 0, terminator);
            while (bits.Count % 8 != 0) bits.Add(false);

            var dataBytes = new List<byte>();
            for (int i = 0; i < bits.Count; i += 8)
            {
                int v = 0;
                for (int j = 0; j < 8; j++) if (bits[i + j]) v |= 1 << (7 - j);
                dataBytes.Add((byte)v);
            }
            for (int pad = 0; dataBytes.Count < DataCodewords(version); pad++)
                dataBytes.Add((byte)(pad % 2 == 0 ? 0xEC : 0x11));

            // ---- 分块 + 纠错 + 交织 ----
            List<byte> allCodewords = AddEccAndInterleave(dataBytes.ToArray(), version);

            // ---- 铺满矩阵 ----
            var modules = new bool[17 + version * 4, 17 + version * 4];
            var isFunction = new bool[modules.GetLength(0), modules.GetLength(0)];
            DrawFunctionPatterns(modules, isFunction, version);
            DrawCodewords(allCodewords, modules, isFunction);
            ChooseMaskAndDraw(modules, isFunction, version);
            return modules;
        }

        // ---------------- 版本参数 ----------------

        private static int DataCodewords(int version)
            => VersionTotalCodewords[version - 1] - VersionEccPerBlock[version - 1] * VersionNumBlocks[version - 1];

        private static int CharCountBits(int version) => version <= 9 ? 8 : 16;

        private static void AppendBits(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        // ---------------- 伽罗华域 GF(256) ----------------

        private static readonly byte[] GfExp = new byte[512];
        private static readonly byte[] GfLog = new byte[256];
        private static bool _gfReady;

        private static void InitGf()
        {
            if (_gfReady) return;
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                GfExp[i] = (byte)x;
                GfLog[x] = (byte)i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) GfExp[i] = GfExp[i - 255];
            _gfReady = true;
        }

        private static byte GfMul(byte a, byte b)
            => (a == 0 || b == 0) ? (byte)0 : GfExp[GfLog[a] + GfLog[b]];

        /// <summary>计算纠错码字（余数方式，生成多项式系数低位在前）</summary>
        private static byte[] ReedSolomonRemainder(byte[] block, int eccLen)
        {
            var divisor = new byte[eccLen];
            Array.Fill(divisor, (byte)0);
            divisor[eccLen - 1] = 1;
            byte root = 1;
            for (int i = 0; i < eccLen; i++)
            {
                for (int j = 0; j < eccLen; j++)
                {
                    divisor[j] = GfMul(divisor[j], root);
                    if (j + 1 < eccLen) divisor[j] ^= divisor[j + 1];
                }
                root = GfMul(root, 2);
            }
            var rem = new byte[eccLen];
            foreach (byte b in block)
            {
                byte factor = (byte)(b ^ rem[0]);
                for (int j = 0; j < eccLen - 1; j++) rem[j] = rem[j + 1];
                rem[eccLen - 1] = 0;
                for (int j = 0; j < eccLen; j++) rem[j] ^= GfMul(divisor[j], factor);
            }
            return rem;
        }

        private static List<byte> AddEccAndInterleave(byte[] data, int version)
        {
            int numBlocks = VersionNumBlocks[version - 1];
            int eccLen = VersionEccPerBlock[version - 1];
            int rawCodewords = VersionTotalCodewords[version - 1];
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortDataLen = rawCodewords / numBlocks - eccLen;

            var dataBlocks = new List<byte[]>();
            var eccBlocks = new List<byte[]>();
            int offset = 0;
            for (int i = 0; i < numBlocks; i++)
            {
                int len = shortDataLen + (i < numShortBlocks ? 0 : 1);
                var block = new byte[len];
                Array.Copy(data, offset, block, 0, len);
                offset += len;
                dataBlocks.Add(block);
                eccBlocks.Add(ReedSolomonRemainder(block, eccLen));
            }

            var result = new List<byte>(rawCodewords);
            for (int i = 0; i <= shortDataLen; i++)
                for (int b = 0; b < numBlocks; b++)
                    if (i < dataBlocks[b].Length) result.Add(dataBlocks[b][i]);
            for (int i = 0; i < eccLen; i++)
                for (int b = 0; b < numBlocks; b++)
                    result.Add(eccBlocks[b][i]);
            return result;
        }

        // ---------------- 功能图形 ----------------

        private static int[] FunctionPatternTable(int size, int version)
        {
            var table = new int[size * size];
            Array.Fill(table, -1);

            for (int i = 0; i < size; i++)
            {
                table[6 * size + i] = 0;   // 水平时序线
                table[i * size + 6] = 0;   // 垂直时序线
            }
            for (int i = 0; i < 9; i++)
            {
                table[8 * size + i] = 0;   // 格式信息（左上水平）
                table[i * size + 8] = 0;   // 格式信息（左上垂直）
                table[8 * size + size - 1 - i] = 0;
                table[(size - 1 - i) * size + 8] = 0;
            }
            // 左上、右上、左下定位图形 + 分隔符
            for (int y = 0; y < 9; y++)
                for (int x = 0; x < 9; x++)
                {
                    table[y * size + x] = 0;
                    table[y * size + size - 1 - x] = 0;
                    table[(size - 1 - y) * size + x] = 0;
                }
            if (version >= 7)
            {
                for (int y = 0; y < 6; y++)
                    for (int x = size - 11; x < size - 8; x++)
                    {
                        table[y * size + x] = 2;
                        table[x * size + y] = 2;
                    }
            }
            return table;
        }

        private static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction, int version)
        {
            int size = modules.GetLength(0);

            // 时序线
            for (int i = 0; i < size; i++)
            {
                SetFunc(modules, isFunction, 6, i, i % 2 == 0);
                SetFunc(modules, isFunction, i, 6, i % 2 == 0);
            }

            // 定位图形
            DrawFinder(modules, isFunction, 3, 3);
            DrawFinder(modules, isFunction, size - 4, 3);
            DrawFinder(modules, isFunction, 3, size - 4);

            // 对齐图形
            int numAlign = version / 7 + 2;
            int step = (version == 1) ? 0 : 26 / (numAlign) / 2 * 2 + 2;
            if (version >= 7) step = 26 / (numAlign) / 2 * 2 + 4;
            var positions = new List<int>();
            if (version > 1)
            {
                int pos = size - 7;
                for (int i = 0; i < numAlign - 1; i++, pos -= step)
                {
                    positions.Insert(0, pos);
                }
                positions.Insert(0, 6);
            }
            foreach (int cy in positions)
                foreach (int cx in positions)
                {
                    if ((cx == 6 && cy == 6) || (cx == 6 && cy == size - 7) || (cx == size - 7 && cy == 6)) continue;
                    DrawAlignment(modules, isFunction, cx, cy);
                }

            // 预留格式信息 + 暗模块
            for (int i = 0; i < 9; i++)
            {
                SetFunc(modules, isFunction, 8, i, false);
                SetFunc(modules, isFunction, i, 8, false);
            }
            for (int i = 0; i < 8; i++)
            {
                SetFunc(modules, isFunction, size - 1 - i, 8, false);
                SetFunc(modules, isFunction, 8, size - 1 - i, false);
            }
            SetFunc(modules, isFunction, 8, size - 8, true);

            // 版本信息
            if (version >= 7)
            {
                int rem = version;
                for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                int bits = version << 12 | (rem & 0xFFF);
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((bits >> i) & 1) != 0;
                    int a = size - 11 + i % 3;
                    int b = i / 3;
                    SetFunc(modules, isFunction, a, b, bit);
                    SetFunc(modules, isFunction, b, a, bit);
                }
            }
        }

        private static void DrawFinder(bool[,] modules, bool[,] isFunction, int cx, int cy)
        {
            int size = modules.GetLength(0);
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunc(modules, isFunction, x, y, dist != 2 && dist != 4);
                }
        }

        private static void DrawAlignment(bool[,] modules, bool[,] isFunction, int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    SetFunc(modules, isFunction, cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        private static void SetFunc(bool[,] modules, bool[,] isFunction, int x, int y, bool dark)
        {
            modules[y, x] = dark;
            isFunction[y, x] = true;
        }

        private static void DrawCodewords(List<byte> data, bool[,] modules, bool[,] isFunction)
        {
            int size = modules.GetLength(0);
            int i = 0;
            int totalBits = data.Count * 8;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                bool upward = ((right + 1) & 2) == 0;
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        int y = upward ? size - 1 - vert : vert;
                        if (isFunction[y, x]) continue;
                        bool bit = i < totalBits && ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        modules[y, x] = bit;
                        i++;
                    }
                }
            }
        }

        // ---------------- 掩码 ----------------

        private static void ChooseMaskAndDraw(bool[,] modules, bool[,] isFunction, int version)
        {
            int size = modules.GetLength(0);
            int[][] maskTables = new int[8][];
            for (int i = 0; i < 8; i++) maskTables[i] = MaskTable(i, size);

            int bestMask = 0;
            int minPenalty = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(modules, isFunction, maskTables[mask]);
                DrawFormatBits(modules, mask);
                int penalty = PenaltyScore(modules);
                if (penalty < minPenalty)
                {
                    minPenalty = penalty;
                    bestMask = mask;
                }
            }
            ApplyMask(modules, isFunction, maskTables[bestMask]);
            DrawFormatBits(modules, bestMask);
        }

        private static int[] MaskTable(int mask, int size)
        {
            var table = new int[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    table[y * size + x] = invert ? 1 : 0;
                }
            return table;
        }

        private static void ApplyMask(bool[,] modules, bool[,] isFunction, int[] mask)
        {
            int size = modules.GetLength(0);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (!isFunction[y, x] && mask[y * size + x] == 1)
                        modules[y, x] = !modules[y, x];
        }

        private static void DrawFormatBits(bool[,] modules, int mask)
        {
            int size = modules.GetLength(0);
            int data = EccLevel << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++) modules[i, 8] = GetBit(bits, i);
            modules[7, 8] = GetBit(bits, 6);
            modules[8, 8] = GetBit(bits, 7);
            modules[8, 7] = GetBit(bits, 8);
            for (int i = 9; i < 15; i++) modules[8, 14 - i] = GetBit(bits, i);
            for (int i = 0; i < 8; i++) modules[8, size - 1 - i] = GetBit(bits, i);
            for (int i = 8; i < 15; i++) modules[size - 15 + i, 8] = GetBit(bits, i);
            modules[size - 8, 8] = true;
        }

        private static bool GetBit(int v, int i) => ((v >> i) & 1) != 0;

        // ---------------- 罚分 ----------------

        private static int PenaltyScore(bool[,] modules)
        {
            int size = modules.GetLength(0);
            const int N1 = 3, N2 = 3, N3 = 40, N4 = 10;
            int penalty = 0;

            // 规则 1
            for (int y = 0; y < size; y++)
            {
                int run = 1;
                for (int x = 1; x < size; x++)
                {
                    if (modules[y, x] == modules[y, x - 1])
                    {
                        run++;
                        if (run == 5) penalty += N1;
                        else if (run > 5) penalty++;
                    }
                    else run = 1;
                }
            }
            for (int x = 0; x < size; x++)
            {
                int run = 1;
                for (int y = 1; y < size; y++)
                {
                    if (modules[y, x] == modules[y - 1, x])
                    {
                        run++;
                        if (run == 5) penalty += N1;
                        else if (run > 5) penalty++;
                    }
                    else run = 1;
                }
            }

            // 规则 2
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                {
                    bool c = modules[y, x];
                    if (c == modules[y, x + 1] && c == modules[y + 1, x] && c == modules[y + 1, x + 1])
                        penalty += N2;
                }

            // 规则 3：1:1:3:1:1 模式（含两侧 4 个空）
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size - 10; x++)
                {
                    if (FinderLike(modules, x, y, 1, 0)) penalty += N3;
                    if (FinderLike(modules, x, y, 0, 1)) penalty += N3;
                }

            // 规则 4
            int dark = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (modules[y, x]) dark++;
            int total = size * size;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            if (k > 0) penalty += k * N4;
            return penalty;
        }

        private static bool FinderLike(bool[,] modules, int x, int y, int dx, int dy)
        {
            int[] pattern = { 1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0 };
            int size = modules.GetLength(0);
            int cx = x, cy = y;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (cx >= size || cy >= size || cx < 0 || cy < 0) return false;
                bool expected = pattern[i] == 1;
                if (modules[cy, cx] != expected) return false;
                cx += dx;
                cy += dy;
            }
            return true;
        }
    }
}
