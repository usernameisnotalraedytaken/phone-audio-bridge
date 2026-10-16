// 统一视觉语言。
//
// 设计参照专业音频插件（FabFilter / Cubase 那一类）的通用手法：
//   - 颜色都有语义，不用来装饰。青=数据、橙=告警、绿=正常
//   - 数值用等宽字体，跳变时不抖动——这是"专业感"的主要来源
//   - 纵深靠明度分层，不靠边框和阴影堆叠
//   - 超椭圆圆角（连续曲率）比标准圆角更耐看
//   - 数据元素带极淡外发光，不是硬边
//
// 所有尺寸围绕 4px 栅格，避免出现 3px、7px 这类"看起来差一点"的间距。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Snad
{
    internal static class Theme
    {
        // ---- 明度分层：背景最暗 -> 面板中间 -> 数据最亮 ----
        public static readonly Color BgBase = Color.FromArgb(11, 13, 17);      // 窗口底
        public static readonly Color BgPanel = Color.FromArgb(18, 21, 27);     // 面板
        public static readonly Color BgRaised = Color.FromArgb(26, 30, 38);    // 控件
        public static readonly Color BgHover = Color.FromArgb(36, 42, 52);
        public static readonly Color BgActive = Color.FromArgb(46, 54, 66);
        public static readonly Color Line = Color.FromArgb(34, 39, 48);       // 分隔线
        public static readonly Color Grid = Color.FromArgb(28, 33, 41);        // 图内栅格
        public static readonly Color GridMajor = Color.FromArgb(40, 47, 58);   // 主刻度

        // ---- 前景 ----
        public static readonly Color TextPrimary = Color.FromArgb(232, 238, 246);
        public static readonly Color TextSecondary = Color.FromArgb(150, 162, 178);
        public static readonly Color TextTertiary = Color.FromArgb(96, 106, 122);

        // ---- 语义色 ----
        public static readonly Color Accent = Color.FromArgb(88, 208, 255);      // 数据/曲线
        public static readonly Color AccentSoft = Color.FromArgb(48, 132, 176);  // 曲线低饱和版
        public static readonly Color Warning = Color.FromArgb(255, 158, 74);     // 告警
        public static readonly Color Success = Color.FromArgb(104, 222, 148);    // 正常
        public static readonly Color Danger = Color.FromArgb(255, 106, 106);     // 错误

        // ---- 频谱填充渐变（低饱和，不抢 EQ 曲线） ----
        public static readonly Color SpectrumLow = Color.FromArgb(150, 60, 170, 210);   // 高频（靠上）
        public static readonly Color SpectrumHigh = Color.FromArgb(200, 40, 120, 160);  // 低频（靠下）
        public static readonly Color SpectrumPeak = Color.FromArgb(220, 200, 226, 240);

        // ---- 栅格 ----
        public const int S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 20, S6 = 24, S8 = 32;

        // ---- 字体 ----
        // 数值用等宽，跳变不抖动；界面文字用 UI 字体。
        private static readonly string MetricFamily = PickFamily(
            new[] { "Bahnschrift", "Cascadia Mono", "Consolas" });
        private static readonly string UiFamily = PickFamily(
            new[] { "Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI" });
        private static readonly string MonoFamily = PickFamily(
            new[] { "Cascadia Mono", "Consolas", "Lucida Console" });

        private static readonly Dictionary<string, Font> Cache = new Dictionary<string, Font>();

        // ⚠ 下方 Font/Metric/Ui/Mono 返回的都是**缓存实例**，调用方绝对不要 Dispose。
        //
        // 这一点踩过一次：调用侧写 using (var f = Theme.Ui(9f)) 会在 using 结束时
        // Dispose 掉缓存里的字体，之后任何使用同一尺寸的绘制都会抛
        // ArgumentException「参数无效」，且报错位置看起来毫无关联。
        // 缓存的生命周期与进程相同，理由：字号取值是有限集合，缓存不会无限增长。

        /// <summary>选中实际可用的字体族，并在都缺失时明确记录，不静默回退。</summary>
        private static string PickFamily(string[] candidates)
        {
            try
            {
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var col = new InstalledFontCollection())
                    foreach (var f in col.Families) installed.Add(f.Name);

                foreach (var c in candidates)
                    if (installed.Contains(c)) return c;

                System.Diagnostics.Debug.WriteLine(
                    "Theme：候选字体都不可用，回退到系统默认。候选=" + string.Join("/", candidates));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Theme：枚举字体失败：" + ex.Message);
            }
            return FontFamily.GenericSansSerif.Name;
        }

        public static string MetricFontName { get { return MetricFamily; } }
        public static string UiFontName { get { return UiFamily; } }
        public static string MonoFontName { get { return MonoFamily; } }

        /// <summary>
        /// 取字体（带缓存，避免绘制时反复构造）。
        /// 返回值是共享实例，调用方**不要 Dispose**。
        /// </summary>
        public static Font Font(string family, float size, FontStyle style = FontStyle.Regular)
        {
            string key = family + "|" + size.ToString("0.##") + "|" + (int)style;
            Font f;
            if (Cache.TryGetValue(key, out f)) return f;
            f = new Font(family, size, style, GraphicsUnit.Point);
            Cache[key] = f;
            return f;
        }

        /// <summary>数值字体：等宽、适合读数。</summary>
        public static Font Metric(float size, FontStyle style = FontStyle.Regular)
        {
            return Font(MetricFamily, size, style);
        }

        /// <summary>界面文字字体。</summary>
        public static Font Ui(float size, FontStyle style = FontStyle.Regular)
        {
            return Font(UiFamily, size, style);
        }

        /// <summary>等宽字体（日志、地址等）。</summary>
        public static Font Mono(float size, FontStyle style = FontStyle.Regular)
        {
            return Font(MonoFamily, size, style);
        }

        // ---------------------------------------------------------------- 绘制助手

        /// <summary>开启高质量绘制。绘制面板应在 OnPaint 开头调用。</summary>
        public static void SetupQuality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }

        /// <summary>
        /// 超椭圆圆角路径。
        ///
        /// 与标准 DrawArc 圆角的区别：用三次贝塞尔，控制点取 r*0.5523
        /// （圆的标准近似系数），转角处曲率连续，视觉上比 DrawArc 的
        /// 硬拼接更"顺"。这是它看起来更"贵"的原因。
        /// </summary>
        public static GraphicsPath SuperEllipse(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            if (radius <= 0.01f)
            {
                p.AddRectangle(r);
                return p;
            }
            float max = Math.Min(r.Width, r.Height) / 2f;
            if (radius > max) radius = max;
            float k = radius * 0.5523f;

            // 左上
            p.AddLine(r.Left + radius, r.Top, r.Right - radius, r.Top);
            p.AddBezier(r.Right - radius, r.Top, r.Right - radius + k, r.Top, r.Right, r.Top + radius - k, r.Right, r.Top + radius);
            // 右上
            p.AddLine(r.Right, r.Top + radius, r.Right, r.Bottom - radius);
            p.AddBezier(r.Right, r.Bottom - radius, r.Right, r.Bottom - radius + k, r.Right - radius + k, r.Bottom, r.Right - radius, r.Bottom);
            // 右下
            p.AddLine(r.Right - radius, r.Bottom, r.Left + radius, r.Bottom);
            p.AddBezier(r.Left + radius, r.Bottom, r.Left + radius - k, r.Bottom, r.Left, r.Bottom - radius + k, r.Left, r.Bottom - radius);
            // 左下
            p.AddLine(r.Left, r.Bottom - radius, r.Left, r.Top + radius);
            p.AddBezier(r.Left, r.Top + radius, r.Left, r.Top + radius - k, r.Left + radius - k, r.Top, r.Left + radius, r.Top);

            p.CloseFigure();
            return p;
        }

        /// <summary>填充超椭圆圆角矩形。</summary>
        public static void FillRound(Graphics g, Brush b, RectangleF r, float radius)
        {
            using (var path = SuperEllipse(r, radius))
                g.FillPath(b, path);
        }

        /// <summary>描边超椭圆圆角矩形。</summary>
        public static void DrawRound(Graphics g, Pen pen, RectangleF r, float radius)
        {
            using (var path = SuperEllipse(r, radius))
                g.DrawPath(pen, path);
        }

        /// <summary>
        /// 微发光：由外向内画若干层递减 alpha 的粗描边。
        /// 层数不宜多（超过 4 层会明显变慢且糊）。
        /// </summary>
        public static void GlowLine(Graphics g, PointF[] pts, Color color, float coreWidth, int layers = 3)
        {
            if (pts == null || pts.Length < 2) return;
            for (int i = layers; i >= 1; i--)
            {
                int a = (int)(26f / i);
                float w = coreWidth + i * 2.2f;
                using (var pen = new Pen(Color.FromArgb(a, color), w))
                {
                    pen.LineJoin = LineJoin.Round;
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, pts);
                }
            }
            using (var pen = new Pen(color, coreWidth))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLines(pen, pts);
            }
        }

        /// <summary>把颜色按比例调亮或调暗（f>1 提亮，f<1 压暗）。</summary>
        public static Color Scale(Color c, float f)
        {
            return Color.FromArgb(c.A,
                Clamp((int)(c.R * f)), Clamp((int)(c.G * f)), Clamp((int)(c.B * f)));
        }

        /// <summary>按 alpha 比例混合两色。</summary>
        public static Color Mix(Color a, Color b, float t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        private static int Clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        /// <summary>启用窗口/控件的系统计时器精度（绘制动画需要）。</summary>
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        public static bool RaiseTimerResolution()
        {
            try { return timeBeginPeriod(1) == 0; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Theme：timeBeginPeriod 失败：" + ex.Message);
                return false;
            }
        }

        public static void RestoreTimerResolution(bool raised)
        {
            if (!raised) return;
            try { timeEndPeriod(1); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Theme：timeEndPeriod 失败：" + ex.Message); }
        }
    }
}
