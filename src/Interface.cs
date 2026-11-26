using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace InternetAgain
{
    internal static class Motion
    {
        public static bool EnabledInWindows()
        {
            const uint GetClientAreaAnimation = 0x1042;
            int enabled;
            return SystemParametersInfoW(GetClientAreaAnimation, 0, out enabled, 0) && enabled != 0;
        }

        public static double Ease(double progress)
        {
            return progress * progress * (3 - 2 * progress);
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern bool SystemParametersInfoW(uint action, uint param, out int value, uint flags);
    }

    internal class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    internal sealed class TabRail : BufferedPanel
    {
        private double position, widthFactor = 1;
        private readonly int count;
        private readonly Color rule;

        public TabRail(int count, Color rule)
        {
            this.count = count;
            this.rule = rule;
        }

        public void Position(double tab, double width)
        {
            position = tab;
            widthFactor = width;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
            float tabWidth = (float)ClientSize.Width / count;
            float y = ClientSize.Height * 0.45f;
            using (var pen = new Pen(rule))
            {
                e.Graphics.DrawLine(pen, 0, y, ClientSize.Width, y);
                for (int i = 0; i < count; i++)
                    e.Graphics.DrawLine(pen, i * tabWidth, 0, i * tabWidth, ClientSize.Height - 1);
            }
            float fullWidth = tabWidth * 0.56f;
            float width = fullWidth * (float)widthFactor;
            float x = (float)position * tabWidth + (fullWidth - width) / 2;
            using (var brush = new SolidBrush(ForeColor))
                e.Graphics.FillRectangle(brush, x, y - 1, width, Math.Max(2f, ClientSize.Height * 0.22f));
        }
    }

    internal sealed class RevealRule : BufferedPanel
    {
        private double fraction = 1;

        public void Reveal(double progress)
        {
            fraction = progress;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var ink = new SolidBrush(ForeColor))
                e.Graphics.FillRectangle(ink, 0, 0, (float)(ClientSize.Width * fraction), ClientSize.Height);
        }
    }

    internal sealed class DisplayText : Control
    {
        private readonly float relativeWeight;

        public DisplayText(string text, Font font, float relativeWeight)
        {
            Text = text;
            Font = font;
            this.relativeWeight = relativeWeight;
            AccessibleRole = AccessibleRole.StaticText;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            AccessibleName = Text.Replace("\n", " ");
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (String.IsNullOrEmpty(Text) || ClientSize.Width < 6 || ClientSize.Height < 6) return;
            float em = Font.SizeInPoints * e.Graphics.DpiY / 72f;
            using (var shape = new GraphicsPath())
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            using (var brush = new SolidBrush(ForeColor))
            {
                var lines = Text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    shape.AddString(lines[i], Font.FontFamily, (int)Font.Style, em, new PointF(0, i * em * 0.91f), format);
                var bounds = shape.GetBounds();
                if (bounds.Width <= 0 || bounds.Height <= 0) return;
                float scale = Math.Min(1, Math.Min((ClientSize.Width - 5f) / bounds.Width,
                    (ClientSize.Height - 5f) / bounds.Height));
                using (var matrix = new Matrix(scale, 0, 0, scale, 2 - bounds.Left * scale,
                    (ClientSize.Height - bounds.Height * scale) / 2 - bounds.Top * scale))
                    shape.Transform(matrix);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                if (relativeWeight > 0)
                    using (var outline = new Pen(ForeColor, relativeWeight * e.Graphics.DpiY / 96f) { LineJoin = LineJoin.Round })
                        e.Graphics.DrawPath(outline, shape);
                e.Graphics.FillPath(brush, shape);
            }
        }
    }

    internal sealed class RouteGraphic : BufferedPanel
    {
        private readonly Color ink, rule, faultColor;
        private int failure = -1;
        private bool? online;
        private double pulse;
        private double? scan;

        public bool HasFailure { get { return failure >= 0; } }

        public RouteGraphic(Color ink, Color rule, Color faultColor)
        {
            this.ink = ink;
            this.rule = rule;
            this.faultColor = faultColor;
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "Маршрут проверки подключения";
        }

        public void SetResult(bool? available, RouteStep? failedAt)
        {
            online = available;
            failure = failedAt.HasValue ? (int)failedAt.Value : -1;
            Invalidate();
        }

        public void SetMotion(double pulsePhase, double? scanPosition)
        {
            pulse = pulsePhase;
            scan = scanPosition;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width < 30 || ClientSize.Height < 10) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float unit = ClientSize.Width / 3f;
            float y = ClientSize.Height * 0.56f;
            float dpi = e.Graphics.DpiY / 96f;
            float inset = Math.Min(unit / 2, 35 * dpi);
            float[] x = { inset, unit + inset, 2 * unit + inset };

            using (var guide = new Pen(rule, dpi))
            using (var solid = new Pen(ink, 1.5f * dpi))
            using (var dashed = new Pen(rule, 1.5f * dpi) { DashStyle = DashStyle.Dash })
            using (var fill = new SolidBrush(BackColor))
            using (var fault = new SolidBrush(faultColor))
            {
                for (int i = 0; i < x.Length; i++)
                    e.Graphics.DrawLine(guide, x[i], 0, x[i], ClientSize.Height);
                for (int i = 0; i < x.Length - 1; i++)
                {
                    bool reached = online == true || (failure >= 0 && i < failure);
                    e.Graphics.DrawLine(reached ? solid : dashed, x[i], y, x[i + 1], y);
                }
                if (scan.HasValue)
                {
                    float position = x[0] + (x[2] - x[0]) * (float)scan.Value;
                    using (var dot = new SolidBrush(ink))
                        e.Graphics.FillEllipse(dot, position - 3 * dpi, y - 3 * dpi, 6 * dpi, 6 * dpi);
                }
                for (int i = 0; i < x.Length; i++)
                {
                    float radius = 5.5f * dpi;
                    e.Graphics.FillEllipse(fill, x[i] - radius, y - radius, 2 * radius, 2 * radius);
                    if (i == failure)
                    {
                        float ring = (11 + (float)pulse * 10) * dpi;
                        using (var ripple = new Pen(Color.FromArgb((int)(180 - 165 * pulse), faultColor), dpi))
                            e.Graphics.DrawEllipse(ripple, x[i] - ring, y - ring, 2 * ring, 2 * ring);
                        e.Graphics.FillEllipse(fault, x[i] - radius, y - radius, 2 * radius, 2 * radius);
                    }
                    else
                    {
                        bool reached = online == true || (failure >= 0 && i < failure);
                        e.Graphics.DrawEllipse(reached ? solid : guide, x[i] - radius, y - radius, 2 * radius, 2 * radius);
                    }
                }
            }
        }
    }
}
