using System.Drawing.Drawing2D;

namespace DesktopPet;

/// <summary>
/// The text box for talking to the pet's Claude: a white rounded bubble over
/// his head with the last reply (all of it, scrollable) and a box to type in.
/// It takes keyboard focus when it opens, so typing and dictation (Wispr Flow
/// types into whatever has focus) land in it. Enter sends, Shift+Enter starts
/// a new line, Esc closes. Clicking away closes it too, unless there's
/// something typed in it.
/// </summary>
sealed class ChatBox : Form
{
    const int Pad = 12, Corner = 16;

    readonly TextBox replyBox, input;
    readonly double scale;

    /// <summary>Enter was pressed on this text.</summary>
    public event Action<string>? Submitted;

    public ChatBox(double scale, string lastReply)
    {
        this.scale = scale;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        BackColor = Color.White;
        Text = "Talk to RevBot";
        KeyPreview = true;

        int pad = S(Pad), w = S(380);
        var font = new Font("Verdana", (float)(15 * scale), GraphicsUnit.Pixel);
        replyBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(40, 40, 44),
            Font = font,
            ScrollBars = ScrollBars.Vertical,
            TabStop = false,
            Text = lastReply,
            Visible = lastReply.Length > 0,
        };
        input = new TextBox
        {
            Multiline = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(244, 244, 246),
            ForeColor = Color.Black,
            Font = font,
            PlaceholderText = "Talk to Claude...  (Enter sends, Esc closes)",
            WordWrap = true,
        };
        Controls.Add(replyBox);
        Controls.Add(input);

        int line = font.Height;
        int replyH = lastReply.Length > 0 ? Math.Min(S(220), MeasureHeight(lastReply, font, w - 2 * pad) + 4) : 0;
        int inputH = line * 2 + S(8);
        replyBox.SetBounds(pad, pad, w - 2 * pad, replyH);
        int iy = pad + (replyH > 0 ? replyH + S(8) : 0);
        input.SetBounds(pad, iy, w - 2 * pad, inputH);
        ClientSize = new Size(w, iy + inputH + pad);
        replyBox.ScrollBars = MeasureHeight(lastReply, font, w - 2 * pad) + 4 > replyH ? ScrollBars.Vertical : ScrollBars.None;

        input.KeyDown += OnInputKeyDown;
        input.TextChanged += (_, _) => Grow();
        Deactivate += (_, _) =>
        {
            if (input.TextLength == 0 && Visible)
                Close();
        };
    }

    int S(double v) => (int)Math.Round(v * scale);

    static int MeasureHeight(string text, Font font, int width) =>
        text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;

    /// <summary>Put it over the pet's head (headX, headY: upper right of the head), on screen.</summary>
    public void Place(int headX, int headY, Native.RECT work)
    {
        int x = headX - Width / 2, y = headY - Height - S(14);
        x = Math.Clamp(x, work.Left + 4, Math.Max(work.Left + 4, work.Right - Width - 4));
        y = Math.Clamp(y, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - Height - 4));
        Location = new Point(x, y);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        input.Focus();
        if (replyBox.Visible)
        {
            replyBox.SelectionStart = replyBox.TextLength;
            replyBox.ScrollToCaret();
        }
    }

    void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.SuppressKeyPress = true;
            string text = input.Text.Trim();
            if (text.Length == 0)
                return;
            input.Clear();
            Submitted?.Invoke(text);
            Close();
        }
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    /// <summary>The typing box grows to six lines as you write, upward, keeping its bottom edge put.</summary>
    void Grow()
    {
        int line = input.Font.Height;
        int want = Math.Clamp(MeasureHeight(input.Text + "W", input.Font, input.Width - S(4)) + S(8), line * 2 + S(8), line * 6 + S(8));
        int delta = want - input.Height;
        if (delta == 0)
            return;
        int bottom = Bottom;
        input.Height = want;
        ClientSize = new Size(ClientSize.Width, input.Bottom + S(Pad));
        Top = bottom - Height;
        input.ScrollBars = want >= line * 6 + S(8) ? ScrollBars.Vertical : ScrollBars.None;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = Rounded(new Rectangle(0, 0, Width, Height), S(Corner));
        Region = new Region(path);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(30, 30, 34), Math.Max(2f, (float)(2.5 * scale)));
        using var path = Rounded(new Rectangle(1, 1, Width - 3, Height - 3), S(Corner) - 1);
        g.DrawPath(pen, path);
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
