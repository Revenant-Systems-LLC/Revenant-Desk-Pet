namespace DesktopPet;

/// <summary>
/// Borderless, per-pixel-alpha, topmost tool window. It never takes focus, has
/// no taskbar button, and does its own DPI handling.
/// </summary>
sealed class PetWindow : Form
{
    /// <summary>Every pet window's title; other pets find each other by it.</summary>
    public const string Title = "Desktop Pet";

    public event Action? DpiChangedRaw;

    /// <summary>A hotkey this window registered was pressed (its id).</summary>
    public event Action<int>? HotKey;

    public PetWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        Text = Title;
        Bounds = new Rectangle(0, 0, 1, 1);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    // UpdateLayeredWindow owns the pixels; GDI painting would do nothing useful.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEACTIVATE:
                m.Result = Native.MA_NOACTIVATE;
                return;
            case Native.WM_DPICHANGED:
                // WinForms would resize the window to its suggested rect; the
                // sprite size is ours to decide, so just report the change.
                m.Result = IntPtr.Zero;
                DpiChangedRaw?.Invoke();
                return;
            case Native.WM_HOTKEY:
                HotKey?.Invoke((int)m.WParam);
                return;
        }
        base.WndProc(ref m);
    }
}
