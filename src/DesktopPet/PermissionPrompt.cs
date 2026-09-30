using System.Drawing.Drawing2D;

namespace DesktopPet;

/// <summary>
/// The pet's own Claude wants to change something: a white rounded bubble over
/// the pet with the question, the exact file or command under it, and Yes / No.
/// It never takes focus (you may be typing somewhere else), stays until it's
/// answered, and only answers when a button is clicked.
/// </summary>
sealed class PermissionPrompt : Form
{
    const int Pad = 14, Corner = 16;

    readonly double scale;
    bool answered;
    TextBox? detailBox;       // re-measured once laid out (see OnLoad)
    Button? yesButton, noButton;

    /// <summary>Yes (true) or No (false), once.</summary>
    public event Action<bool>? Answered;

    public PermissionPrompt(double scale, string question, string? detail)
    {
        this.scale = scale;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        BackColor = Color.White;
        Text = "RevBot asks";

        int pad = S(Pad), w = S(380), inner = w - 2 * pad;
        var font = new Font("Verdana", (float)(16 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        var mono = new Font("Consolas", (float)(14 * scale), GraphicsUnit.Pixel);
        var q = new Label { Text = question, Font = font, ForeColor = Color.FromArgb(30, 30, 34), AutoSize = false };
        q.SetBounds(pad, pad, inner, Measure(question, font, inner));
        Controls.Add(q);
        int y = q.Bottom + S(6);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            // A read-only text box wraps a long path or command anywhere (a label only breaks at spaces).
            var d = new TextBox
            {
                Text = detail,
                Font = mono,
                ForeColor = Color.FromArgb(60, 60, 70),
                BackColor = Color.White,
                BorderStyle = BorderStyle.None,
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                TabStop = false,
            };
            int charW = Math.Max(1, TextRenderer.MeasureText("MMMMMMMMMM", mono, Size.Empty, TextFormatFlags.NoPadding).Width / 10);
            int perLine = Math.Max(1, (inner - S(4)) / charW);
            int lines = detail.Split('\n').Sum(l => Math.Max(1, (l.Length + perLine - 1) / perLine));
            int h = lines * mono.Height + S(4), max = S(160);
            d.ScrollBars = h > max ? ScrollBars.Vertical : ScrollBars.None;
            d.SetBounds(pad, y, inner, Math.Min(h, max));
            Controls.Add(d);
            detailBox = d;
            y = d.Bottom + S(10);
        }
        var yes = MakeButton("Yes", true);
        var no = MakeButton("No", false);
        int bw = S(96), bh = S(34);
        no.SetBounds(w - pad - bw, y, bw, bh);
        yes.SetBounds(no.Left - S(10) - bw, y, bw, bh);
        Controls.Add(yes);
        Controls.Add(no);
        (yesButton, noButton) = (yes, no);
        ClientSize = new Size(w, y + bh + pad);
    }

    /// <summary>The text box wraps at hyphens and slashes as well as anywhere, so ask it how many lines it really took.</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (detailBox is not TextBox d || yesButton is null || noButton is null)
            return;
        int lines = d.GetLineFromCharIndex(Math.Max(0, d.TextLength - 1)) + 1;
        int max = S(160), h = Math.Min(max, lines * d.Font.Height + S(4));
        d.ScrollBars = lines * d.Font.Height + S(4) > max ? ScrollBars.Vertical : ScrollBars.None;
        int shift = h - d.Height;
        if (shift == 0)
            return;
        d.Height = h;
        yesButton.Top += shift;
        noButton.Top += shift;
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + shift);
    }

    Button MakeButton(string text, bool value)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Verdana", (float)(15 * scale), FontStyle.Bold, GraphicsUnit.Pixel),
            BackColor = value ? Color.FromArgb(255, 219, 0) : Color.FromArgb(236, 236, 240), // his hazard yellow for Yes
            ForeColor = Color.Black,
            TabStop = false,
            UseMnemonic = false,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(30, 30, 34);
        b.FlatAppearance.BorderSize = Math.Max(1, S(2));
        b.Click += (_, _) =>
        {
            if (answered)
                return;
            answered = true;
            Answered?.Invoke(value);
            Close();
        };
        return b;
    }

    int S(double v) => (int)Math.Round(v * scale);

    static int Measure(string text, Font font, int width) =>
        TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 2;

    // Never steal focus: clicks work without it, and you may be typing elsewhere.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Over the pet's head (headX, headY: top centre of the pet), kept on screen. Placed once: a button that moves while you aim at it is no good.</summary>
    public void Place(int headX, int headY, Native.RECT work)
    {
        int x = headX - Width / 2, y = headY - Height - S(14);
        x = Math.Clamp(x, work.Left + 4, Math.Max(work.Left + 4, work.Right - Width - 4));
        y = Math.Clamp(y, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - Height - 4));
        if (Left != x || Top != y)
            Location = new Point(x, y);
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
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(30, 30, 34), Math.Max(2f, (float)(2.5 * scale)));
        using var path = Rounded(new Rectangle(1, 1, Width - 3, Height - 3), S(Corner) - 1);
        e.Graphics.DrawPath(pen, path);
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
