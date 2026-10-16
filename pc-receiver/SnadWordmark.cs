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
        public int MarkWidth { get { return ShowMark ? 20 : 0; } }

        /// <summary>
        /// 逐字绘制并加字距。
        /// Graphics.DrawString 不支持字距，只能自己累加 x。
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

        private float MeasureTracked(Graphics g, string text, Font font, float tracking)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            float w = 0;
            foreach (char c in text) w += g.MeasureString(c.ToString(), font).Width + tracking;
            return w - tracking;
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

            float x = 0;
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
                const float barW = 1.8f, gap = 2.6f;
                float maxH = ClientSize.Height * 0.66f;
                float mx = 1;
                for (int i = 0; i < hs.Length; i++)
                {
                    float bh = maxH * hs[i];
                    float by = midY - bh / 2f;
                    // 明度跟着高度走，高的更亮——比统一 alpha 更有层次
                    int alpha = (int)(110 + 145 * hs[i]);
                    using (var br = new SolidBrush(Color.FromArgb(alpha, Theme.Accent)))
                        g.FillRectangle(br, mx, by, barW, bh);
                    mx += barW + gap;
                }
                x = mx + 6;
            }

            // ---- 主名 ----
            float mainTop = midY - fMain.GetHeight(g) / 2f - 2;
            using (var br = new SolidBrush(Theme.TextPrimary))
                DrawTracked(g, _main, fMain, br, x, mainTop, Tracking);
            float mainW = MeasureTracked(g, _main, fMain, Tracking);

            // 主名下方一道短强调线：是"铭牌"而不是普通标题的关键一笔。
            // 只覆盖主名的前 42%，留白比画满更有设计感。
            // 位置放在字底下方 3px，避免与字母下缘贴在一起显脏。
            using (var br = new SolidBrush(Color.FromArgb(210, Theme.Accent)))
                g.FillRectangle(br, x, mainTop + fMain.GetHeight(g) + 1, mainW * 0.42f, 1.8f);

            // ---- 副题 ----
            float subX = x + mainW + 12;
            float subTop = midY - fSub.GetHeight(g) / 2f + 1;
            using (var br = new SolidBrush(Theme.TextTertiary))
                DrawTracked(g, _sub, fSub, br, subX, subTop, Tracking * 0.7f);
        }

        /// <summary>整块字标绘制出来需要多宽。标题条据此排布右侧内容。</summary>
        public int PreferredPixelWidth
        {
            get
            {
                using (var g = CreateGraphics())
                {
                    var fMain = Theme.MonoEmphasis(11.5f);
                    var fSub = Theme.Mono(7f);
                    float w = MarkWidth;
                    w += MeasureTracked(g, _main, fMain, Tracking);
                    w += 12;
                    w += MeasureTracked(g, _sub, fSub, Tracking * 0.7f);
                    return (int)Math.Ceiling(w) + 2;
                }
            }
        }
    }
}
