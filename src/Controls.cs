// Theme, DPI scaling and the custom-drawn controls used by every window.
// Everything is sized in "logical" pixels (as at 100% scaling) and multiplied by the window's real DPI,
// so text and shapes are sharp at any Windows scaling and on any screen (laptop, monitor, TV).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TVMode
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(22, 24, 29);
        public static readonly Color Card = Color.FromArgb(32, 35, 42);
        public static readonly Color CardBorder = Color.FromArgb(46, 50, 59);
        public static readonly Color Inset = Color.FromArgb(25, 27, 33);
        public static readonly Color Hover = Color.FromArgb(44, 48, 58);
        public static readonly Color Pressed = Color.FromArgb(52, 57, 68);
        public static readonly Color Fg = Color.FromArgb(234, 236, 240);
        public static readonly Color Muted = Color.FromArgb(146, 152, 165);
        public static readonly Color Faint = Color.FromArgb(98, 104, 117);
        public static readonly Color Accent = Color.FromArgb(76, 141, 255);
        public static readonly Color AccentHover = Color.FromArgb(101, 158, 255);
        public static readonly Color AccentSoft = Color.FromArgb(33, 52, 88);
        public static readonly Color Success = Color.FromArgb(86, 204, 140);
        public static readonly Color Danger = Color.FromArgb(255, 117, 107);
        public static readonly Color Warning = Color.FromArgb(255, 196, 92);

        static string iconFont;

        /// <summary>Windows 11's icon font, or Windows 10's.</summary>
        public static string IconFont
        {
            get
            {
                if (iconFont == null)
                {
                    using (var fonts = new System.Drawing.Text.InstalledFontCollection())
                        iconFont = fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
                }
                return iconFont;
            }
        }

        // Icon glyphs (same code points in both icon fonts)
        public const string GlyphPc = "";        // laptop
        public const string GlyphTv = "";        // TV
        public const string GlyphSettings = "";
        public const string GlyphRefresh = "";
        public const string GlyphSave = "";
        public const string GlyphWarning = "";

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    /// <summary>Converts logical (100%) sizes to real pixels for one DPI, and hands out fonts sized for it.</summary>
    class Scaler
    {
        public readonly int Dpi;
        public readonly float F;
        readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        public Scaler(int dpi)
        {
            Dpi = dpi <= 0 ? 96 : dpi;
            F = Dpi / 96f;
        }

        public int S(float logical) { return (int)Math.Round(logical * F); }

        /// <summary>Font sized in logical pixels (13 = normal body text).</summary>
        public Font Font(float px, bool semibold = false)
        {
            return Get(semibold ? "Segoe UI Semibold" : "Segoe UI", px);
        }

        public Font Icons(float px) { return Get(Theme.IconFont, px); }

        Font Get(string family, float px)
        {
            string key = family + "|" + px;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font(family, px * F, FontStyle.Regular, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }
    }

    static class UiNative
    {
        [DllImport("user32.dll")]
        public static extern int GetDpiForWindow(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static int DpiOf(IntPtr hwnd)
        {
            try { int d = GetDpiForWindow(hwnd); return d > 0 ? d : 96; }
            catch { return 96; } // Windows before 10 1607
        }

        /// <summary>Windows 11 smooth rounded corners for a borderless window (ignored elsewhere).</summary>
        public static void RoundCorners(IntPtr hwnd)
        {
            try { int round = 2; DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4); }
            catch { }
        }
    }

    /// <summary>
    /// Base window: dark title bar, and rebuilds its controls at the right size when it is created on - or moved
    /// to - a screen with different scaling (WM_DPICHANGED), so nothing is ever stretched and blurry.
    /// </summary>
    abstract class ThemedForm : Form
    {
        protected Scaler Sc;

        protected ThemedForm(int dpi)
        {
            Sc = new Scaler(dpi);
            AutoScaleMode = AutoScaleMode.None; // we scale ourselves, per screen
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = Sc.Font(13);
            DoubleBuffered = true;
        }

        /// <summary>Clears and recreates all controls for the current <see cref="Sc"/>.</summary>
        protected abstract void BuildUi();

        protected void ClearUi()
        {
            var old = Controls.Cast<Control>().ToList();
            Controls.Clear();
            foreach (var c in old) c.Dispose();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.UseDarkTitleBar(this);
            int dpi = UiNative.DpiOf(Handle);
            if (dpi != Sc.Dpi) Rescale(dpi, null);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DPICHANGED = 0x02E0;
            if (m.Msg == WM_DPICHANGED)
            {
                int dpi = m.WParam.ToInt32() & 0xFFFF;
                var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                Rescale(dpi, new Point(r.Left, r.Top));
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        void Rescale(int dpi, Point? location)
        {
            Sc = new Scaler(dpi);
            SuspendLayout();
            Font = Sc.Font(13);
            BuildUi();
            ResumeLayout(true);
            if (location.HasValue) Location = location.Value;
            Invalidate(true);
        }

        // ---- small factory helpers shared by all windows

        protected Label AddLabel(Control parent, string text, float x, float y, float w, float h, Font font, Color color,
            ContentAlignment align = ContentAlignment.TopLeft)
        {
            var l = new Label
            {
                Text = text, Left = Sc.S(x), Top = Sc.S(y), Width = Sc.S(w), Height = Sc.S(h), Font = font, ForeColor = color,
                BackColor = parent is Card ? ((Card)parent).Fill : Theme.Bg, AutoEllipsis = true, UseMnemonic = false, TextAlign = align
            };
            parent.Controls.Add(l);
            return l;
        }

        protected Label Caption(Control parent, string text, float x, float y, float w)
        {
            return AddLabel(parent, text.ToUpperInvariant(), x, y, w, 16, Sc.Font(11, true), Theme.Muted);
        }

        protected T Place<T>(Control parent, T c, float x, float y, float w, float h) where T : Control
        {
            c.SetBounds(Sc.S(x), Sc.S(y), Sc.S(w), Sc.S(h));
            parent.Controls.Add(c);
            return c;
        }
    }

    /// <summary>Rounded panel. Children should use <see cref="Fill"/> as their BackColor.</summary>
    class Card : Panel
    {
        public Color Fill = Theme.Card;
        public Color Border = Theme.CardBorder;
        public float Radius = 10;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius))
            using (var b = new SolidBrush(Fill))
            using (var p = new Pen(Border))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
        }
    }

    /// <summary>Flat, rounded, hover-aware button. Styles: primary, secondary, ghost (text only), icon, and the big mode cards.</summary>
    class FlatButton : Control
    {
        public enum Kind { Primary, Secondary, Ghost, Icon, Mode }

        public Kind Style = Kind.Secondary;
        public string Glyph;          // icon-font glyph (optional)
        public Font GlyphFont;
        public string Subtitle;       // mode cards
        public Font SubtitleFont;
        public bool Active;           // mode cards: this mode is on
        public float Radius = 8;
        public Color? TextColor;
        bool hover, down;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color parentBg = Parent is Card ? ((Card)Parent).Fill : (Parent != null ? Parent.BackColor : Theme.Bg);
            g.Clear(parentBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float f = DeviceDpiScale();

            Color fill, border, text = TextColor ?? Theme.Fg;
            switch (Style)
            {
                case Kind.Primary:
                    fill = down ? Theme.Accent : hover ? Theme.AccentHover : Theme.Accent; border = fill; text = Color.White; break;
                case Kind.Ghost:
                    fill = down ? Theme.Pressed : hover ? Theme.Hover : parentBg; border = fill; text = TextColor ?? Theme.Accent; break;
                case Kind.Icon:
                    fill = down ? Theme.Pressed : hover ? Theme.Hover : parentBg; border = fill; text = TextColor ?? Theme.Muted;
                    if (hover) text = Theme.Fg; break;
                case Kind.Mode:
                    fill = Active ? Theme.AccentSoft : down ? Theme.Pressed : hover ? Theme.Hover : Theme.Card;
                    border = Active ? Theme.Accent : hover ? Theme.Faint : Theme.CardBorder; break;
                default:
                    fill = down ? Theme.Pressed : hover ? Theme.Hover : Theme.Card; border = Theme.CardBorder; break;
            }
            if (!Enabled) { text = Theme.Faint; }

            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.RoundRect(rect, Radius * f))
            using (var b = new SolidBrush(fill))
            using (var p = new Pen(border, Style == Kind.Mode && Active ? Math.Max(1.5f, 2 * f) : 1))
            {
                g.FillPath(b, path);
                if (border != fill) g.DrawPath(p, path);
                if (Focused && ShowFocusCues)
                {
                    using (var fp = new Pen(Theme.AccentHover, Math.Max(1, f)) { DashStyle = DashStyle.Dot })
                    using (var inner = Theme.RoundRect(RectangleF.Inflate(rect, -2 * f, -2 * f), Math.Max(1, (Radius - 2) * f)))
                        g.DrawPath(fp, inner);
                }
            }

            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            if (Style == Kind.Mode)
            {
                int pad = (int)(20 * f);
                int glyphW = 0;
                if (Glyph != null && GlyphFont != null)
                {
                    glyphW = (int)(36 * f);
                    var gr = new Rectangle(pad, 0, glyphW, Height);
                    TextRenderer.DrawText(g, Glyph, GlyphFont, gr, Active ? Theme.Accent : Theme.Muted, fill,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                int tx = pad + glyphW + (int)(14 * f);
                int titleH = (int)(Font.GetHeight() + 2);
                int subH = SubtitleFont != null ? (int)(SubtitleFont.GetHeight() + 2) : 0;
                int top = (Height - titleH - subH) / 2;
                TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, top, Width - tx - pad, titleH), text, fill, flags | TextFormatFlags.Left);
                if (subH > 0 && !string.IsNullOrEmpty(Subtitle))
                    TextRenderer.DrawText(g, Subtitle, SubtitleFont, new Rectangle(tx, top + titleH, Width - tx - pad, subH),
                        Active ? Theme.AccentHover : Theme.Muted, fill, flags | TextFormatFlags.Left);
                return;
            }

            if (Style == Kind.Icon && Glyph != null && GlyphFont != null)
            {
                TextRenderer.DrawText(g, Glyph, GlyphFont, ClientRectangle, text, fill,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }

            // text, optionally with a leading glyph, centered
            Size ts = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            int gw = 0, gap = 0;
            if (Glyph != null && GlyphFont != null)
            {
                gw = TextRenderer.MeasureText(g, Glyph, GlyphFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                gap = (int)(8 * f);
            }
            int total = gw + gap + ts.Width;
            int x = Style == Kind.Ghost && TextAlignLeft ? (int)(6 * f) : (Width - total) / 2;
            if (gw > 0)
                TextRenderer.DrawText(g, Glyph, GlyphFont, new Rectangle(x, 0, gw, Height), text, fill,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + gw + gap, 0, Width - (x + gw + gap), Height), text, fill,
                flags | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }

        public bool TextAlignLeft;

        float DeviceDpiScale() { return IsHandleCreated ? UiNative.DpiOf(Handle) / 96f : 1f; }
    }

    /// <summary>Small rounded label ("PC mode", "Custom setup"...).</summary>
    class Pill : Control
    {
        public Color Fill = Theme.AccentSoft;
        public Color TextFg = Theme.AccentHover;

        public Pill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>Sizes the pill to its text.</summary>
        public void Fit(Scaler sc)
        {
            var ts = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            Width = ts.Width + sc.S(22);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f))
            using (var b = new SolidBrush(Fill))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, TextFg, Fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>On/off switch with a label, like Windows 11 settings.</summary>
    class Toggle : Control
    {
        bool isOn, hover;
        public event EventHandler CheckedChanged;
        public Color Surface = Theme.Bg;

        public Toggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        public bool Checked
        {
            get { return isOn; }
            set
            {
                if (isOn == value) return;
                isOn = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>Sets the state without raising <see cref="CheckedChanged"/>.</summary>
        public void SetQuietly(bool value) { isOn = value; Invalidate(); }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }
        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty); base.OnKeyUp(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float f = IsHandleCreated ? UiNative.DpiOf(Handle) / 96f : 1f;
            float tw = 38 * f, th = 20 * f, ty = (Height - th) / 2f;
            var track = new RectangleF(1, ty, tw, th);

            if (!Enabled)
            {
                // "Not available" must not look like "off": dashed outline, no knob, a dash in the middle.
                using (var path = Theme.RoundRect(track, th / 2))
                using (var p = new Pen(Theme.Faint, Math.Max(1, f)) { DashStyle = DashStyle.Dash })
                    g.DrawPath(p, path);
                using (var p = new Pen(Theme.Faint, Math.Max(1.5f, 2 * f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(p, track.X + tw * 0.36f, ty + th / 2, track.Right - tw * 0.36f, ty + th / 2);
            }
            else
            {
                Color trackFill = isOn ? (hover ? Theme.AccentHover : Theme.Accent) : Surface;
                Color trackBorder = isOn ? trackFill : (hover ? Theme.Fg : Theme.Muted);
                using (var path = Theme.RoundRect(track, th / 2))
                using (var b = new SolidBrush(trackFill))
                using (var p = new Pen(trackBorder, Math.Max(1, f)))
                {
                    g.FillPath(b, path);
                    g.DrawPath(p, path);
                }
                float knob = isOn ? 12 * f : 10 * f;
                float kx = isOn ? track.Right - 4 * f - knob : track.X + 5 * f;
                using (var b = new SolidBrush(isOn ? Color.White : (hover ? Theme.Fg : Theme.Muted)))
                    g.FillEllipse(b, kx, ty + (th - knob) / 2f, knob, knob);
            }

            if (Focused && ShowFocusCues)
                using (var fp = new Pen(Theme.AccentHover, Math.Max(1, f)) { DashStyle = DashStyle.Dot })
                using (var fpath = Theme.RoundRect(RectangleF.Inflate(track, 2.5f * f, 2.5f * f), th / 2 + 2.5f * f))
                    g.DrawPath(fp, fpath);

            int tx = (int)(tw + 12 * f);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Width - tx, Height), Enabled ? ForeColor : Theme.Faint, Surface,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>List of screens with custom checkboxes and a status on the right ("On", "Not connected").</summary>
    class CheckList : Control
    {
        public class Row
        {
            public string Key, Text, Detail, SaveName;
            public Color DetailColor = Theme.Muted;
            public bool Checked, Dim;
        }

        public readonly List<Row> Rows = new List<Row>();
        public int RowHeight = 32;
        public Font DetailFont;
        public string EmptyText = "No screens found";
        public event Action<Row> RowToggled;
        int hoverIndex = -1, scroll;

        public CheckList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
        }

        public void SetRows(IEnumerable<Row> rows)
        {
            Rows.Clear();
            Rows.AddRange(rows);
            scroll = Math.Max(0, Math.Min(scroll, MaxScroll()));
            Invalidate();
        }

        int MaxScroll() { return Math.Max(0, Rows.Count * RowHeight - Height); }

        int IndexAt(Point p)
        {
            int i = (p.Y + scroll) / RowHeight;
            return i >= 0 && i < Rows.Count ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != hoverIndex) { hoverIndex = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            Focus();
            int i = IndexAt(e.Location);
            if (i >= 0 && e.Button == MouseButtons.Left) Toggle(i);
            base.OnMouseClick(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(MaxScroll(), scroll - Math.Sign(e.Delta) * RowHeight));
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Rows.Count > 0)
            {
                if (e.KeyCode == Keys.Down) { hoverIndex = Math.Min(Rows.Count - 1, hoverIndex + 1); Invalidate(); }
                if (e.KeyCode == Keys.Up) { hoverIndex = Math.Max(0, hoverIndex - 1); Invalidate(); }
                if (e.KeyCode == Keys.Space && hoverIndex >= 0) Toggle(hoverIndex);
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { if (hoverIndex < 0 && Rows.Count > 0) hoverIndex = 0; Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        void Toggle(int i)
        {
            Rows[i].Checked = !Rows[i].Checked;
            Invalidate();
            if (RowToggled != null) RowToggled(Rows[i]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color surface = Parent is Card ? ((Card)Parent).Fill : Theme.Bg;
            g.Clear(surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float f = IsHandleCreated ? UiNative.DpiOf(Handle) / 96f : 1f;

            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8 * f))
            using (var b = new SolidBrush(Theme.Inset))
            using (var p = new Pen(Theme.CardBorder))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
                g.SetClip(path);
            }

            if (Rows.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, Font, ClientRectangle, Theme.Faint, Theme.Inset,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int pad = (int)(10 * f), box = (int)(16 * f);
            for (int i = 0; i < Rows.Count; i++)
            {
                var r = Rows[i];
                int y = i * RowHeight - scroll;
                if (y + RowHeight < 0 || y > Height) continue;
                var rowRect = new Rectangle(0, y, Width, RowHeight);
                Color rowBg = Theme.Inset;
                if (i == hoverIndex)
                {
                    rowBg = Theme.Hover;
                    using (var hb = new SolidBrush(rowBg)) g.FillRectangle(hb, rowRect);
                }

                // checkbox
                var br = new RectangleF(pad, y + (RowHeight - box) / 2f, box, box);
                using (var bp = Theme.RoundRect(br, 4 * f))
                {
                    if (r.Checked)
                    {
                        using (var fill = new SolidBrush(Theme.Accent)) g.FillPath(fill, bp);
                        using (var tick = new Pen(Color.White, Math.Max(1.5f, 2 * f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                            g.DrawLines(tick, new[]
                            {
                                new PointF(br.X + box * 0.24f, br.Y + box * 0.52f),
                                new PointF(br.X + box * 0.43f, br.Y + box * 0.70f),
                                new PointF(br.X + box * 0.77f, br.Y + box * 0.32f)
                            });
                    }
                    else
                        using (var bpen = new Pen(i == hoverIndex ? Theme.Fg : Theme.Muted, Math.Max(1, f))) g.DrawPath(bpen, bp);
                }

                int tx = pad + box + (int)(10 * f);
                int detailW = 0;
                if (!string.IsNullOrEmpty(r.Detail) && DetailFont != null)
                {
                    detailW = TextRenderer.MeasureText(g, r.Detail, DetailFont).Width;
                    TextRenderer.DrawText(g, r.Detail, DetailFont, new Rectangle(Width - pad - detailW, y, detailW, RowHeight), r.DetailColor, rowBg,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                TextRenderer.DrawText(g, r.Text, Font, new Rectangle(tx, y, Width - tx - pad - detailW - (int)(6 * f), RowHeight),
                    r.Dim ? Theme.Muted : Theme.Fg, rowBg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>Dark dropdown (used for the sound outputs).</summary>
    class DarkCombo : ComboBox
    {
        public class Item
        {
            public string Text;
            public bool Muted;
            public object Tag;
            public override string ToString() { return Text; }
        }

        public DarkCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Standard;
            BackColor = Theme.Inset;
            ForeColor = Theme.Fg;
            MaxDropDownItems = 12;
            Native.UseDarkControlTheme(this, true);
        }

        public Item SelectedEntry { get { return SelectedItem as Item; } }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var it = Items[e.Index] as Item;
            string text = it != null ? it.Text : Items[e.Index].ToString();
            bool inEdit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool selected = (e.State & DrawItemState.Selected) != 0 && !inEdit;
            Color bg = selected ? Theme.AccentSoft : Theme.Inset;
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);

            float f = ItemHeight / 26f;
            int x = e.Bounds.Left + (int)(8 * f);
            Color fg = it != null && it.Muted ? Theme.Muted : Theme.Fg;
            TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(x, e.Bounds.Top, e.Bounds.Right - x - 4, e.Bounds.Height), fg, bg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Shows a key combination as key caps; click it and press keys to record a new one.</summary>
    class HotkeyBox : Control
    {
        public bool Recording { get; private set; }
        public event Action<uint, Keys> Recorded;   // modifiers (MOD_*), key
        public event EventHandler RecordingChanged;
        public string Combo = "";
        bool hover;

        public HotkeyBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnClick(EventArgs e) { Focus(); SetRecording(true); base.OnClick(e); }
        protected override void OnLostFocus(EventArgs e) { SetRecording(false); base.OnLostFocus(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override bool IsInputKey(Keys k) { return true; }

        public void SetRecording(bool on)
        {
            if (Recording == on) return;
            Recording = on;
            Invalidate();
            if (RecordingChanged != null) RecordingChanged(this, EventArgs.Empty);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Alt combinations arrive as system keys; route them to OnKeyDown while recording.
            if (Recording && (msg.Msg == 0x104 /* WM_SYSKEYDOWN */ || msg.Msg == 0x100))
            {
                OnKeyDown(new KeyEventArgs(keyData));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (!Recording)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) SetRecording(true);
                return;
            }
            Keys k = e.KeyCode;
            if (k == Keys.ControlKey || k == Keys.Menu || k == Keys.ShiftKey || k == Keys.LWin || k == Keys.RWin) return;
            if (k == Keys.Escape) { SetRecording(false); return; }
            uint mods = 0;
            if (e.Control) mods |= Native.MOD_CONTROL;
            if (e.Alt) mods |= Native.MOD_ALT;
            if (e.Shift) mods |= Native.MOD_SHIFT;
            if (Recorded != null) Recorded(mods, k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color surface = Parent is Card ? ((Card)Parent).Fill : Theme.Bg;
            g.Clear(surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float f = IsHandleCreated ? UiNative.DpiOf(Handle) / 96f : 1f;
            Color border = Recording ? Theme.Accent : hover ? Theme.Faint : Theme.CardBorder;
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8 * f))
            using (var b = new SolidBrush(Recording ? Theme.AccentSoft : Theme.Inset))
            using (var p = new Pen(border, Recording ? Math.Max(1.5f, 2 * f) : 1))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }

            if (Recording)
            {
                TextRenderer.DrawText(g, "Press the keys you want...", Font, ClientRectangle, Theme.AccentHover, Theme.AccentSoft,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            // key caps
            var keys = Combo.Split(new[] { " + " }, StringSplitOptions.RemoveEmptyEntries);
            int capH = (int)(Height - 14 * f), gap = (int)(8 * f), padX = (int)(10 * f);
            var widths = keys.Select(k => TextRenderer.MeasureText(g, k, Font).Width + padX).ToList();
            int plusW = TextRenderer.MeasureText(g, "+", Font).Width;
            int total = widths.Sum() + (keys.Length - 1) * (plusW + gap * 2);
            int x = (Width - total) / 2, y = (Height - capH) / 2;
            for (int i = 0; i < keys.Length; i++)
            {
                var cap = new RectangleF(x, y, widths[i], capH);
                using (var cp = Theme.RoundRect(cap, 5 * f))
                using (var cb = new SolidBrush(Theme.Hover))
                using (var cpen = new Pen(Theme.Faint))
                {
                    g.FillPath(cb, cp);
                    g.DrawPath(cpen, cp);
                }
                TextRenderer.DrawText(g, keys[i], Font, Rectangle.Round(cap), Theme.Fg, Theme.Hover,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                x += widths[i];
                if (i < keys.Length - 1)
                {
                    TextRenderer.DrawText(g, "+", Font, new Rectangle(x + gap, 0, plusW, Height), Theme.Muted, Theme.Inset,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    x += plusW + gap * 2;
                }
            }
        }
    }

    /// <summary>Dark style for the tray menu.</summary>
    class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
            public override Color MenuBorder { get { return Theme.CardBorder; } }
            public override Color MenuItemBorder { get { return Theme.Hover; } }
            public override Color MenuItemSelected { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
            public override Color SeparatorDark { get { return Theme.CardBorder; } }
            public override Color SeparatorLight { get { return Theme.Card; } }
            public override Color CheckBackground { get { return Theme.AccentSoft; } }
            public override Color CheckSelectedBackground { get { return Theme.AccentSoft; } }
            public override Color CheckPressedBackground { get { return Theme.AccentSoft; } }
        }

        public DarkMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Fg : Theme.Faint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float d = Math.Min(r.Width, r.Height) * 0.42f;
            using (var b = new SolidBrush(Theme.Accent))
                g.FillEllipse(b, r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
        }
    }

    // ------------------------------------------------------------------ Icons for apps
    /// <summary>Real app icons at any pixel size (including Store apps), via the Windows shell.</summary>
    static class ShellIcons
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public ushort bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [In] ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

        /// <summary>Icon for a file, shortcut or "shell:AppsFolder\..." Store app; null if none.</summary>
        public static Bitmap Get(string parsingName, int size)
        {
            if (string.IsNullOrEmpty(parsingName)) return null;
            IShellItemImageFactory factory = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factory);
                if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x4 /* SIIGBF_ICONONLY */, out hbm) != 0 || hbm == IntPtr.Zero) return null;
                return ToBitmap(hbm);
            }
            catch { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        static Bitmap ToBitmap(IntPtr hbm)
        {
            BITMAP info;
            if (GetObject(hbm, Marshal.SizeOf(typeof(BITMAP)), out info) == 0) return null;
            int w = info.bmWidth, h = info.bmHeight;
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h /* top-down */, biPlanes = 1, biBitCount = 32 };
            var buf = new byte[w * h * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, hbm, 0, (uint)h, buf, ref bi, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, dc); }

            bool hasAlpha = false;
            for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha) for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(buf, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
            bmp.UnlockBits(data);
            return bmp;
        }
    }

    // ------------------------------------------------------------------ App catalog (for the picker)
    class AppChoice
    {
        public string Name = "";
        public string Path = "";   // "" = open nothing
        public string Args = "";
        public Image Image;
        public override string ToString() { return Name; }
    }

    static class AppCatalog
    {
        /// <summary>Icon for a TV-app path (Steam links use Steam's own icon).</summary>
        public static Image IconFor(string path, int size)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (AppLauncher.IsSteam(path)) path = AppLauncher.SteamExe();
            if (path == null || path.Contains("://")) return null;
            return ShellIcons.Get(path, size);
        }

        static string Env(string var, string rest)
        {
            string root = Environment.GetEnvironmentVariable(var);
            return string.IsNullOrEmpty(root) ? null : System.IO.Path.Combine(root, rest);
        }

        /// <summary>Well-known couch apps that are installed on this PC, ready to pick with one click.</summary>
        public static List<AppChoice> Presets(int iconSize)
        {
            var list = new List<AppChoice>();
            string steam = AppLauncher.SteamExe();
            if (steam != null && System.IO.File.Exists(steam))
                list.Add(new AppChoice { Name = "Steam Big Picture", Path = AppLauncher.SteamBigPicture });

            var known = new[]
            {
                new[] { "Kodi", Env("ProgramFiles", @"Kodi\kodi.exe"), "" },
                new[] { "VLC (full screen)", Env("ProgramFiles", @"VideoLAN\VLC\vlc.exe"), "--fullscreen" },
                new[] { "VLC (full screen)", Env("ProgramFiles(x86)", @"VideoLAN\VLC\vlc.exe"), "--fullscreen" },
                new[] { "Plex HTPC", Env("ProgramFiles", @"Plex\Plex HTPC\Plex HTPC.exe"), "" },
                new[] { "Jellyfin Media Player", Env("ProgramFiles", @"Jellyfin\Jellyfin Media Player\JellyfinMediaPlayer.exe"), "" },
                new[] { "Playnite (full screen)", Env("LOCALAPPDATA", @"Playnite\Playnite.FullscreenApp.exe"), "" },
                new[] { "Moonlight", Env("ProgramFiles", @"Moonlight Game Streaming\Moonlight.exe"), "" },
            };
            foreach (var k in known)
            {
                if (k[1] == null || !System.IO.File.Exists(k[1]) || list.Any(c => c.Name == k[0])) continue;
                list.Add(new AppChoice { Name = k[0], Path = k[1], Args = k[2] });
            }
            foreach (var c in list) c.Image = IconFor(c.Path, iconSize);
            return list;
        }

        static readonly string[] Junk = { "uninstall", "readme", "read me", "help", "website", "documentation", "release notes", "license", "manual", "support" };

        /// <summary>Everything in the Start menu (shortcuts) plus Store apps (Netflix, Disney+, ...), sorted by name.</summary>
        public static List<AppChoice> AllApps(int iconSize)
        {
            var byName = new Dictionary<string, AppChoice>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu })
            {
                string root = System.IO.Path.Combine(Environment.GetFolderPath(folder), "Programs");
                foreach (var file in SafeFiles(root))
                {
                    string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".lnk" && ext != ".url") continue;
                    string name = System.IO.Path.GetFileNameWithoutExtension(file);
                    string lower = name.ToLowerInvariant();
                    if (Junk.Any(j => lower.Contains(j)) || byName.ContainsKey(name)) continue;
                    byName[name] = new AppChoice { Name = name, Path = file };
                }
            }

            // Store apps live only in the virtual "Apps" folder; their Path there is an app ID like "Pkg_xyz!App".
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                object shell = Activator.CreateInstance(shellType);
                object apps = Invoke(shell, "NameSpace", "shell:AppsFolder");
                object items = Invoke(apps, "Items");
                int count = (int)Get(items, "Count");
                for (int i = 0; i < count; i++)
                {
                    object item = Invoke(items, "Item", i);
                    string name = Get(item, "Name") as string, id = Get(item, "Path") as string;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id) || !id.Contains("!") || byName.ContainsKey(name)) continue;
                    string lower = name.ToLowerInvariant();
                    if (Junk.Any(j => lower.Contains(j))) continue;
                    byName[name] = new AppChoice { Name = name, Path = @"shell:AppsFolder\" + id };
                }
            }
            catch { }

            var list = byName.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var c in list) c.Image = IconFor(c.Path, iconSize);
            return list;
        }

        static object Invoke(object target, string method, params object[] args)
        {
            return target.GetType().InvokeMember(method, System.Reflection.BindingFlags.InvokeMethod, null, target, args);
        }

        static object Get(object target, string prop)
        {
            return target.GetType().InvokeMember(prop, System.Reflection.BindingFlags.GetProperty, null, target, null);
        }

        static IEnumerable<string> SafeFiles(string dir)
        {
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                try
                {
                    result.AddRange(System.IO.Directory.GetFiles(d));
                    foreach (var sub in System.IO.Directory.GetDirectories(d)) stack.Push(sub);
                }
                catch { } // folders we can't read
            }
            return result;
        }
    }
}
