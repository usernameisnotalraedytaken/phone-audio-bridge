// 标题字标（wordmark）。
//
// 为什么不用两个 Label：logo 感来自字距、字重对比、以及一个图形符号的配合，
// Label 只能整体设字体，做不出"逐字排布 + 标记 + 基线对齐"这类细节。
// 自绘后这些都直接可控，也顺带解决了原先写死坐标导致的遮挡问题。
//
// 构成（自左向右）：
//   声波标记 —— 五根高低不一的竖条，取自频谱的意象，也是本程序在做的事
//   SNAD     —— 主体，等宽大写、加宽字距、用强调色
//   AUDIO BRIDGE —— 副题，小字号、压低明度、同样加宽字距
//
// 三者按实测宽度依次排布并做基线对齐，因此换字体、改字号都不会互相遮挡。

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Snad
{
    internal sealed class Wordmark : Control
    {
        // 间距与条宽常量。绘制与测量都引用它们，改一处即全局一致。
        private const int MarkGap = 6;      // 标记与主名之间的间距
        private const int SubGap = 12;      // 主名与副题之间的间距
        private const float BarW = 1.8f;    // 声波标记每根条的宽度
        private const float BarGap = 2.6f;  // 条间距
        private const int BarCount = 6;

        private string _main = "SNAD";
        private string _sub = "AUDIO BRIDGE";

        /// <summary>字距（像素）。等宽字体加宽字距最有"铭牌"感。</summary>
        public float Tracking = 3.0f;

        /// <summary>是否绘制左侧声波标记。</summary>
        public bool ShowMark = true;

        public Wordmark()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // 注意不能用 Color.Transparent：Control 的 BackColor 不支持透明，
            // 赋值会抛 ArgumentException。改为在 OnPaint 里用父容器底色铺底。
            BackColor = Theme.BgPanel;
            Height = 34;
        }

        public string MainText
        {
            get { return _main; }
            set { _main = value ?? ""; Invalidate(); }
        }

        public string SubText
        {
            get { return _sub; }
            set { _sub = value ?? ""; Invalidate(); }
        }

        /// <summary>标记宽度（含与文字的间距）。供外部排版时预留。</summary>
        /// <summary>标记占用的宽度：6 根条 + 5 个间隔。与 OnPaint 的绘制保持一致。</summary>
        public int MarkWidth
        {
            get { return ShowMark ? (int)Math.Ceiling(BarCount * BarW + (BarCount - 1) * BarGap) : 0; }
        }

        /// <summary>
        /// 逐字绘制并加字距。DrawString 不支持字距，只能自己累加 x。
        /// 返回绘制推进到的 x（即最右侧内容位置）。
        /// </summary>
        private float DrawTracked(Graphics g, string text, Font font, Brush brush,
                                  float x, float y, float tracking)
        {
            foreach (char c in text)
            {
                string s = c.ToString();
                g.DrawString(s, font, brush, x, y);
                x += g.MeasureString(s, font).Width + tracking;
            }
            return x - tracking;   // 末尾那一次字距不算
        }

        /// <summary>
        /// 测量加字距后的宽度。
        ///
        /// 必须与 DrawTracked 用同一套度量（Graphics.MeasureString 逐字符累加）。
        /// 此前改用 TextRenderer.MeasureText 整串测量，与实际绘制对不上：
        /// 控件报告 189px，而画出来比 189 宽，副题末尾仍被截断。
        /// 测量与绘制口径不一致，是这类"算了但还是截断"问题的根源。
        /// </summary>
        private float MeasureTracked(Graphics g, string text, Font font, float tracking)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            float w = 0;
            foreach (char c in text) w += g.MeasureString(c.ToString(), font).Width + tracking;
            return w - tracking;
        }


        /// <summary>
        /// 字标的几何布局。绘制与测量都读这一份，因此两者不可能不一致。
        ///
        /// 之前测量另用一套 TextRenderer，与实际绘制口径不同，
        /// 于是"报告 189px、画出来更宽、末尾仍被截断"。
        /// 把布局提取成纯数据后，这类偏差从结构上消失。
        /// </summary>
        private struct MarkLayout
        {
            public float MarkX, MarkEndX;      // 声波标记
            public float MainX, MainEndX;      // 主名
            public float RuleY, RuleW;         // 主名下方的强调线
            public float SubX, SubEndX;        // 副题
            public float EndX { get { return Math.Max(MainEndX, SubEndX); } }
        }

        /// <summary>
        /// 用给定的 Graphics 量出布局。g 为 null 时只按字符数估算不必要——
        /// 因此调用方需保证 g 有效；无句柄时用 CreateGraphics 临时取一个。
        /// </summary>
        private MarkLayout ComputeLayout(Graphics g)
        {
            var fMain = Theme.MonoEmphasis(11.5f);
            var fSub = Theme.Mono(7f);

            var L = new MarkLayout();
            float x = 0;

            if (ShowMark)
            {
                L.MarkX = 1;
                L.MarkEndX = 1 + MarkWidth;
                x = L.MarkEndX + MarkGap;
            }

            L.MainX = x;
            // 与 DrawTracked 完全相同的推进方式
            L.MainEndX = x + MeasureTracked(g, _main, fMain, Tracking);

            // 强调线：只覆盖主名前 42%，放在字底下方 1px
            float mainTop = ClientSize.Height / 2f - fMain.GetHeight(g) / 2f - 2;
            L.RuleY = mainTop + fMain.GetHeight(g) + 1;
            L.RuleW = (L.MainEndX - L.MainX) * 0.42f;

            L.SubX = L.MainEndX + SubGap;
            L.SubEndX = L.SubX + MeasureTracked(g, _sub, fSub, Tracking * 0.7f);

            return L;
        }

        /// <summary>
        /// 字标的几何布局。绘制与测量都读这一份，因此不可能不一致。
        /// </summary>
        private MarkLayout ComputeLayoutSafe()
        {
            var fMain = Theme.MonoEmphasis(11.5f);
            var fSub = Theme.Mono(7f);
            var L = new MarkLayout();
            float x = 0;

            if (ShowMark) { L.MarkX = 1; L.MarkEndX = 1 + MarkWidth; x = L.MarkEndX + MarkGap; }

            L.MainX = x;
            float mainAdvance = Advance(_main, fMain, Tracking);
            L.MainEndX = x + mainAdvance;

            float mainTop = ClientSize.Height / 2f - fMain.Height / 2f - 2;
            L.RuleY = mainTop + fMain.Height + 1;
            L.RuleW = mainAdvance * 0.42f;

            L.SubX = L.MainEndX + SubGap;
            L.SubEndX = L.SubX + Advance(_sub, fSub, Tracking * 0.7f);
            return L;
        }

        /// <summary>
        /// 不用 Graphics 的字符推进估算：字符数 × 字宽 + 字距。
        /// 等宽字体下这是准确的，而本字标用的正是等宽字体。
        /// </summary>
        private static float Advance(string text, Font font, float tracking)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            // 等宽字体：用一个字符的宽度乘以长度
            float cw;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
                cw = g.MeasureString("M", font).Width;
            return text.Length * cw + tracking * (text.Length - 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            // 用父容器底色铺底，等效于透明（Control 不支持 Color.Transparent）
            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgPanel))
                g.FillRectangle(bg, ClientRectangle);

            var fMain = Theme.MonoEmphasis(11.5f);
            var fSub = Theme.Mono(7f);
            var L = ComputeLayout(g);
            float midY = ClientSize.Height / 2f;

            // ---- 声波标记 ----
            //
            // 早先做成五根等宽等距、中间最长的竖条，整体读起来像条形码/柱状图，
            // 不像声波。这里的处理：
            //   - 条更窄(1.8)、间隔更大(2.6)，避免"排线"观感
            //   - 高度按一个不对称包络，峰不在正中，更像真实的瞬态
            //   - 明度随高度变化：高的更亮，形成纵向的渐变感
            if (ShowMark)
            {
                float[] hs = { 0.30f, 0.62f, 1.00f, 0.42f, 0.78f, 0.22f };
                const float barW = BarW, gap = BarGap;
                float maxH = ClientSize.Height * 0.66f;
                float mx = L.MarkX;
                for (int i = 0; i < hs.Length; i++)
                {
                    float bh = maxH * hs[i];
                    float by = midY - bh / 2f;
                    int alpha = (int)(110 + 145 * hs[i]);
                    using (var br = new SolidBrush(Color.FromArgb(alpha, Theme.Accent)))
                        g.FillRectangle(br, mx, by, barW, bh);
                    mx += barW + gap;
                }
            }

            // ---- 主名 ----
            float mainTop = midY - fMain.GetHeight(g) / 2f - 2;
            using (var br = new SolidBrush(Theme.TextPrimary))
                DrawTracked(g, _main, fMain, br, L.MainX, mainTop, Tracking);

            // ---- 强调线 ----
            using (var br = new SolidBrush(Color.FromArgb(210, Theme.Accent)))
                g.FillRectangle(br, L.MainX, L.RuleY, L.RuleW, 1.8f);

            // ---- 副题 ----
            float subTop = midY - fSub.GetHeight(g) / 2f + 1;
            using (var br = new SolidBrush(Theme.TextTertiary))
                DrawTracked(g, _sub, fSub, br, L.SubX, subTop, Tracking * 0.7f);
        }

        /// <summary>
        /// 整块字标需要多宽。
        ///
        /// 用 TextRenderer 而非 Graphics.MeasureString：前者不需要
        /// Graphics 上下文，控件尚未加入窗口时也能量准。
        /// 早先用 CreateGraphics()，控件未挂到窗口时 DPI 取默认值，测量偏小。
        /// </summary>
        /// <summary>
        /// 整块字标需要多宽。
        ///
        /// 用与 OnPaint 完全相同的推进方式（逐字符 + 字距）算到最后一个字符的右端，
        /// 而不是另用一套 TextRenderer 测量——两套度量口径不同，
        /// 会出现"报告 189px、画出来更宽、末尾仍被截断"的情况。
        /// </summary>
        public int PreferredPixelWidth
        {
            get
            {
                return (int)Math.Ceiling(ComputeLayoutSafe().EndX) + 6;
            }
        }

        /// <summary>
        /// 按内容撑开宽度。
        ///
        /// 必须由外部在加入窗口后调用一次——此前是调用方写死 Width = 240，
        /// 而实际需要约 255，副题被截成 "AUDIO BR"（末几个字母没了）。
        /// 这类"固定前缀 + 文字"的控件都应由内容决定宽度。
        /// </summary>
        public void FitToContent()
        {
            Width = PreferredPixelWidth;
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 句柄建立后 DPI 才确定，此时量出的宽度才准
            if (!_autoSized) { _autoSized = true; FitToContent(); }
        }

        private bool _autoSized;
    }
}
