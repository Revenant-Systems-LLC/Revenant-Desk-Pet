using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace DesktopPet;

/// <summary>
/// A comic speech bubble at the upper right above the pet's head. The text
/// types out one letter at a time with a click per letter, the bubble grows
/// as it fills, then it stays up long enough to read and goes away. Lines
/// said while one is showing wait their turn.
/// </summary>
sealed class SpeechBubble : IDisposable
{
    const double CharsPerSecond = 16; // Dave: readable, like the old Animal Crossing boxes (30 too fast, 12 too slow)
    const int MaxLineChars = 400;

    readonly BubbleWindow window = new();
    readonly LayeredSurface surface = new();
    readonly Clicker clicker = new();
    readonly Queue<string> queue = new();

    string? text;            // the line being shown
    List<string> lines = new(); // it, already broken into lines for the current size
    int shown;               // letters typed so far
    double nextCharAt, hideAt;
    bool visible;

    // what was last drawn, so an unchanged bubble isn't redrawn every tick
    Sprite? sprite;
    (int Shown, double Scale, int TipX, int Tail) drawnKey;
    double layoutScale;

    /// <summary>Letters click as they appear.</summary>
    public bool Sound { get; set; } = true;

    /// <summary>A line is showing (or waiting).</summary>
    public bool Busy => text is not null || queue.Count > 0;

    public void Say(string line)
    {
        line = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); // one line, single spaces
        if (line.Length == 0)
            return;
        if (line.Length > MaxLineChars)
        {
            int cut = char.IsHighSurrogate(line[MaxLineChars - 1]) ? MaxLineChars - 1 : MaxLineChars; // never half an emoji
            line = line[..cut].TrimEnd() + "...";
        }
        queue.Enqueue(line);
    }

    /// <summary>
    /// Every tick. (headX, headY) is the upper-right corner of the pet's head
    /// on screen: the bubble's tail points there and the bubble sits above
    /// and to the right of it, moved over only as far as it takes to stay on
    /// the screen.
    /// </summary>
    public void Tick(double now, bool petVisible, int headX, int headY, double scale, Native.RECT work)
    {
        if (text is null)
        {
            if (queue.Count == 0)
                return;
            text = queue.Dequeue();
            shown = 0;
            nextCharAt = now + 0.12;
            hideAt = double.PositiveInfinity;
            layoutScale = -1;
        }

        if (!petVisible)
        {
            Hide();
            return;
        }

        // Type: one letter at a time; a missed stretch (the pet was hidden) doesn't pour out at once.
        nextCharAt = Math.Max(nextCharAt, now - 0.1);
        bool clicked = false;
        while (shown < text.Length && now >= nextCharAt)
        {
            char c = text[shown++];
            if (char.IsHighSurrogate(c) && shown < text.Length && char.IsLowSurrogate(text[shown]))
                shown++; // an emoji is two chars: show both at once
            if (Sound && !clicked && char.IsLetterOrDigit(c))
            {
                clicker.Click(c); // at most one click per tick, so a catch-up never machine-guns
                clicked = true;
            }
            nextCharAt += c switch
            {
                '.' or '!' or '?' => 0.35,
                ',' or ';' or ':' => 0.2,
                _ => 1 / CharsPerSecond,
            };
        }
        if (shown == text.Length && double.IsPositiveInfinity(hideAt))
            hideAt = now + Math.Min(7, 1.8 + 0.045 * text.Length); // time to read it
        if (now >= hideAt)
        {
            text = null;
            Hide();
            return;
        }

        if (Math.Abs(layoutScale - scale) > 1e-6)
        {
            lines = Wrap(text, scale);
            layoutScale = scale;
        }
        Present(headX, headY, scale, work);
    }

    public void Hide()
    {
        if (!visible)
            return;
        visible = false;
        window.Hide();
    }

    // ------------------------------------------------------------ drawing

    static Font MakeFont(double scale) => new("Segoe UI Semibold", (float)(15 * scale), GraphicsUnit.Pixel);

    /// <summary>Break the whole line into rows now, so words never jump to the next row while typing.</summary>
    static List<string> Wrap(string text, double scale)
    {
        using var font = MakeFont(scale);
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        float max = (float)(250 * scale);
        float Width(string s) => g.MeasureString(s, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        // Rows put back together must be exactly the text: the typing counts letters across them.
        var rows = new List<string>();
        string row = "";
        foreach (string word in text.Split(' '))
        {
            string candidate = row.Length == 0 ? word : row + " " + word;
            if (Width(candidate) <= max)
            {
                row = candidate;
                continue;
            }
            if (row.Length > 0)
            {
                rows.Add(row + " ");
                row = "";
            }
            // A word wider than a whole row (a long link) is cut into row-wide pieces, never inside an emoji.
            string rest = word;
            while (Width(rest) > max)
            {
                int take = NextBoundary(rest, 0);
                while (take < rest.Length && Width(rest[..NextBoundary(rest, take)]) <= max)
                    take = NextBoundary(rest, take);
                rows.Add(rest[..take]);
                rest = rest[take..];
            }
            row = rest;
        }
        rows.Add(row);
        return rows;
    }

    /// <summary>Index just past the character that starts at i (both halves of an emoji).</summary>
    static int NextBoundary(string s, int i) =>
        i + 1 < s.Length && char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1]) ? i + 2 : i + 1;

    void Present(int headX, int headY, double scale, Native.RECT work)
    {
        // Where it wants to be: its tail tip on the head, the bubble up and to the right.
        int pad = (int)Math.Round(11 * scale), tail = (int)Math.Round(16 * scale);
        var (body, visibleRows) = Measure(scale, pad);
        int left = headX - (int)Math.Round(8 * scale);
        int top = headY - tail - body.Height;
        if (top < work.Top + 4)
        {
            // No room above (he is up at the top of the screen): sit beside his head,
            // to the right, instead of on top of it; the tail shortens to fit.
            top = work.Top + 4;
            left = headX + (int)Math.Round(10 * scale);
            tail = Math.Clamp(headY - (top + body.Height), 0, tail);
        }
        // Stay on screen: slide left only as far as needed; the tail still points at the head.
        int nudge = Math.Max(0, left + body.Width - (work.Right - 4));
        left = Math.Max(work.Left + 4, left - nudge);

        var key = (shown, scale, headX - left, tail);
        if (sprite is null || key != drawnKey)
        {
            sprite = Draw(body, visibleRows, pad, tail, headX - left, scale);
            drawnKey = key;
        }
        int margin = DrawMargin(scale);
        surface.Present(window.Handle, sprite, left - margin, top - margin);
        if (!visible)
        {
            visible = true;
            window.Show();
        }
    }

    /// <summary>The bubble body's size for the letters typed so far; it grows as they come in.</summary>
    (Size Body, List<string> Rows) Measure(double scale, int pad)
    {
        var rows = new List<string>();
        int left = shown;
        foreach (string row in lines)
        {
            if (left <= 0)
                break;
            rows.Add(row.Length <= left ? row : row[..left]);
            left -= row.Length;
        }
        if (rows.Count == 0)
            rows.Add("");
        using var font = MakeFont(scale);
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        float w = 0;
        foreach (string row in rows)
            w = Math.Max(w, g.MeasureString(row.TrimEnd(), font, PointF.Empty, StringFormat.GenericTypographic).Width);
        int lineH = (int)Math.Ceiling(font.GetHeight(g));
        int minW = (int)Math.Round(48 * scale); // room for the rounded corners and the tail
        return (new Size(Math.Max(minW, (int)Math.Ceiling(w) + 2 * pad), rows.Count * lineH + 2 * pad), rows);
    }

    /// <summary>Bubble plus tail as a sprite; tipX is where the tail tip sits, from the bubble's left edge.</summary>
    static Sprite Draw(Size body, List<string> rows, int pad, int tail, int tipX, double scale)
    {
        float stroke = Stroke(scale);
        int margin = DrawMargin(scale);
        int w = body.Width + 2 * margin, h = body.Height + tail + 2 * margin;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var r = new RectangleF(margin, margin, body.Width, body.Height);
            float radius = (float)Math.Min(14 * scale, Math.Min(body.Height / 2.0, (body.Width - 16 * scale) / 2));
            using var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, 2 * radius, 2 * radius, 180, 90);
            path.AddArc(r.Right - 2 * radius, r.Top, 2 * radius, 2 * radius, 270, 90);
            path.AddArc(r.Right - 2 * radius, r.Bottom - 2 * radius, 2 * radius, 2 * radius, 0, 90);
            // the tail: out of the bottom edge near the left, down to the tip over the pet's head
            float tip = Math.Clamp(margin + tipX, margin + 2, margin + body.Width - 2);
            float lo = r.Left + radius, hi = Math.Max(lo, r.Right - radius - (float)(14 * scale));
            float baseL = Math.Clamp(tip + (float)(6 * scale), lo, hi);
            float baseR = baseL + (float)(14 * scale);
            path.AddLine(baseR, r.Bottom, baseR, r.Bottom);
            path.AddLine(tip, r.Bottom + tail, tip, r.Bottom + tail);
            path.AddLine(baseL, r.Bottom, baseL, r.Bottom);
            path.AddArc(r.Left, r.Bottom - 2 * radius, 2 * radius, 2 * radius, 90, 90);
            path.CloseFigure();

            using (var fill = new SolidBrush(Color.FromArgb(255, 255, 253, 246)))
                g.FillPath(fill, path);
            using (var pen = new Pen(Color.FromArgb(255, 24, 24, 24), stroke) { LineJoin = LineJoin.Round })
                g.DrawPath(pen, path);

            using var font = MakeFont(scale);
            using var ink = new SolidBrush(Color.FromArgb(255, 30, 30, 30));
            float lineH = font.GetHeight(g), y = r.Top + pad;
            foreach (string row in rows)
            {
                g.DrawString(row.TrimEnd(), font, ink, r.Left + pad, y, StringFormat.GenericTypographic);
                y += lineH;
            }
        }

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var px = new byte[w * h * 4];
            for (int row = 0; row < h; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, px, row * w * 4, w * 4);
            return new Sprite { W = w, H = h, X0 = 0, Y0 = 0, Px = px };
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    static float Stroke(double scale) => (float)Math.Max(1.5, 2.5 * scale);

    /// <summary>Transparent border around the bubble in its sprite, so the outline isn't clipped.</summary>
    static int DrawMargin(double scale) => (int)Math.Ceiling(Stroke(scale)) + 1;

    public void Dispose()
    {
        window.Close();
        window.Dispose();
        surface.Dispose();
        clicker.Dispose();
    }

    /// <summary>The bubble's own window: layered, always on top, never takes focus, and clicks go straight through it.</summary>
    sealed class BubbleWindow : Form
    {
        public BubbleWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            Text = "Desktop Pet speech"; // not PetWindow.Title: other pets must not take it for a pet
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
