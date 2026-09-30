using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DesktopPet;

/// <summary>
/// "Thinking hard": math floats around his head, like the Math Lady meme. A handful
/// of symbols, formulas and little diagrams circle his head on a tilted ring; the
/// ones behind it are smaller and fainter. Each fades in, drifts round for a few
/// seconds, fades out and comes back as something else. White with a dark outline
/// in a handwriting font, so they read on any wallpaper. Drawn by the pet (video
/// models turn writing into squiggles), so it loops forever and lasts exactly as
/// long as the thinking does. Its own click-through window, redrawn 30 times a second
/// only while it shows.
/// </summary>
sealed class ThinkingMath : IDisposable
{
    const int Count = 7;
    const double FadeIn = 0.3, FadeOut = 0.5;

    static readonly string[] Texts =
    [
        "π", "√2", "x²", "∑", "∫", "∞", "Δ", "θ", "E=mc²", "a²+b²=c²",
        "f(x)", "dy/dx", "3.14159", "½", "sin θ", "x = ?", "7×8", "log n", "e^iπ", "2+2=5?",
    ];
    const int Shapes = 3; // a right triangle, a parabola on axes, a circle with its radius

    sealed class Glyph
    {
        public int Kind;                        // < Texts.Length: text; otherwise a shape
        public double Angle, Speed, Radius, Lift, Born, Life;
    }

    readonly MathWindow window = new();
    readonly LayeredSurface surface = new();
    readonly Random rng = new();
    readonly Glyph[] glyphs = new Glyph[Count];
    readonly Dictionary<int, Bitmap> art = new(); // each glyph drawn once per scale
    double artScale = -1, level, lastDraw = double.NegativeInfinity, lastT;
    bool visible;
    Bitmap? canvas;
    Sprite? sprite;
    FontFamily? font;

    public void Tick(double t, bool thinking, bool petVisible, int headX, int headY, double scale)
    {
        double dt = Math.Clamp(t - lastT, 0, 0.1);
        lastT = t;
        level = thinking && petVisible
            ? Math.Min(1, level + dt / FadeIn)
            : Math.Max(0, level - dt / FadeOut);
        if (level <= 0)
        {
            Hide();
            return;
        }
        if (t - lastDraw < 1.0 / 30)
            return; // floating math doesn't need 60 frames a second
        lastDraw = t;
        if (Math.Abs(scale - artScale) > 1e-6)
            Prepare(scale);
        for (int i = 0; i < Count; i++)
        {
            if (glyphs[i] is null || t - glyphs[i].Born > glyphs[i].Life)
                glyphs[i] = NewGlyph(t, i);
        }
        Draw(t, headX, headY, scale);
    }

    Glyph NewGlyph(double t, int slot)
    {
        var used = glyphs.Where(g => g is not null).Select(g => g.Kind).ToHashSet();
        int kind;
        do
            kind = rng.Next(Texts.Length + Shapes);
        while (used.Contains(kind));
        return new Glyph
        {
            Kind = kind,
            Angle = 2 * Math.PI * (slot + rng.NextDouble() * 0.6) / Count,
            Speed = 0.45 + rng.NextDouble() * 0.35,
            Radius = 0.85 + rng.NextDouble() * 0.3,
            Lift = rng.NextDouble() * 0.6 - 0.3,
            Born = t - rng.NextDouble() * 0.4, // staggered, so they don't all fade in together
            Life = 2.8 + rng.NextDouble() * 2.2,
        };
    }

    // ------------------------------------------------------------ drawing

    /// <summary>Draw every glyph once at this scale; each frame only places them.</summary>
    void Prepare(double scale)
    {
        foreach (var b in art.Values)
            b.Dispose();
        art.Clear();
        artScale = scale;
        font ??= PickFont();
        for (int k = 0; k < Texts.Length + Shapes; k++)
            art[k] = k < Texts.Length ? DrawText(Texts[k], scale) : DrawShape(k - Texts.Length, scale);
        int w = (int)Math.Ceiling(400 * scale), h = (int)Math.Ceiling(300 * scale);
        canvas?.Dispose();
        canvas = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        sprite = new Sprite { W = w, H = h, Px = new byte[w * h * 4] };
    }

    static FontFamily PickFont()
    {
        foreach (string name in new[] { "Ink Free", "Segoe Print", "Comic Sans MS" })
        {
            try
            {
                return new FontFamily(name);
            }
            catch (ArgumentException)
            {
                // not installed
            }
        }
        return FontFamily.GenericSansSerif;
    }

    Bitmap DrawText(string text, double scale)
    {
        float size = (float)(34 * scale);
        using var path = new GraphicsPath();
        path.AddString(text, font!, (int)FontStyle.Bold, size, PointF.Empty, StringFormat.GenericTypographic);
        var bounds = path.GetBounds();
        float outline = (float)Math.Max(2, 3.5 * scale);
        int pad = (int)Math.Ceiling(outline) + 2;
        var bmp = new Bitmap((int)Math.Ceiling(bounds.Width) + 2 * pad, (int)Math.Ceiling(bounds.Height) + 2 * pad, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(pad - bounds.X, pad - bounds.Y);
        using var pen = new Pen(Color.FromArgb(235, 30, 30, 38), outline) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
        g.FillPath(Brushes.White, path);
        return bmp;
    }

    static Bitmap DrawShape(int shape, double scale)
    {
        int s = (int)Math.Ceiling(58 * scale), pad = (int)Math.Ceiling(5 * scale);
        var bmp = new Bitmap(s + 2 * pad, s + 2 * pad, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(pad, pad);
        float thick = (float)Math.Max(3, 5 * scale), thin = (float)Math.Max(1.5, 2.2 * scale);
        void Stroke(Action<Pen> draw)
        {
            using var under = new Pen(Color.FromArgb(235, 30, 30, 38), thick) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var over = new Pen(Color.White, thin) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            draw(under);
            draw(over);
        }
        switch (shape)
        {
            case 0: // right triangle with its square corner
                Stroke(p => g.DrawPolygon(p, new PointF[] { new(3, s - 3), new(s - 3, s - 3), new(3, 5) }));
                Stroke(p => g.DrawLines(p, new PointF[] { new(3, s - 12), new(12, s - 12), new(12, s - 3) }));
                break;
            case 1: // a parabola on axes
                Stroke(p => g.DrawLine(p, 3, s / 2f, s - 3, s / 2f));
                Stroke(p => g.DrawLine(p, s / 2f, 3, s / 2f, s - 3));
                var pts = Enumerable.Range(0, 13).Select(i =>
                {
                    float x = (i - 6) / 6f;
                    return new PointF(s / 2f + x * (s / 2f - 6), s / 2f + 16 * (float)scale - x * x * (s / 2f - 4));
                }).ToArray();
                Stroke(p => g.DrawCurve(p, pts));
                break;
            default: // a circle and its radius
                Stroke(p => g.DrawEllipse(p, 4, 4, s - 8, s - 8));
                Stroke(p => g.DrawLine(p, s / 2f, s / 2f, s - 5, s / 2f));
                break;
        }
        return bmp;
    }

    void Draw(double t, int headX, int headY, double scale)
    {
        if (canvas is null || sprite is null)
            return;
        int w = canvas.Width, h = canvas.Height;
        double cx = w / 2.0, cy = h / 2.0, rx = 128 * scale, ry = 50 * scale;
        using (var g = Graphics.FromImage(canvas))
        using (var attrs = new ImageAttributes())
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            // back to front, so nearer glyphs cover farther ones
            foreach (var gl in glyphs.OrderBy(gl => Math.Sin(gl.Angle + gl.Speed * (t - gl.Born))))
            {
                double a = gl.Angle + gl.Speed * (t - gl.Born);
                double depth = Math.Sin(a);                          // -1 behind his head .. 1 in front
                double age = t - gl.Born;
                double life = Math.Min(1, Math.Min(age / 0.5, (gl.Life - age) / 0.6));
                if (life <= 0)
                    continue;
                double alpha = level * life * (0.6 + 0.4 * (depth + 1) / 2);
                double size = 0.75 + 0.3 * (depth + 1) / 2;
                var bmp = art[gl.Kind];
                float bw = (float)(bmp.Width * size), bh = (float)(bmp.Height * size);
                float x = (float)(cx + rx * gl.Radius * Math.Cos(a) - bw / 2);
                float y = (float)(cy + ry * gl.Radius * depth + gl.Lift * 60 * scale - bh / 2 + Math.Sin(t * 1.7 + gl.Kind) * 3 * scale);
                var cm = new ColorMatrix { Matrix33 = (float)alpha };
                attrs.SetColorMatrix(cm);
                g.DrawImage(bmp, new Rectangle((int)x, (int)y, (int)bw, (int)bh), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
            }
        }
        var data = canvas.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int row = 0; row < h; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, sprite.Px, row * w * 4, w * 4);
        }
        finally
        {
            canvas.UnlockBits(data);
        }
        surface.Invalidate(); // same Sprite object, new pixels
        surface.Present(window.Handle, sprite, headX - w / 2, headY - h / 2);
        if (!visible)
        {
            visible = true;
            window.Show();
        }
    }

    /// <summary>Back on top of the pet (which re-asserts its own topmost now and then).</summary>
    public void KeepOnTop()
    {
        if (visible)
            Native.SetWindowPos(window.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    public void Hide()
    {
        if (!visible)
            return;
        visible = false;
        window.Hide();
    }

    public void Dispose()
    {
        foreach (var b in art.Values)
            b.Dispose();
        canvas?.Dispose();
        surface.Dispose();
        window.Dispose();
    }

    sealed class MathWindow : Form
    {
        public MathWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            Text = "Desktop Pet thinking"; // not PetWindow.Title: other pets must not take it for a pet
            Bounds = new Rectangle(0, 0, 1, 1);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
    }
}
