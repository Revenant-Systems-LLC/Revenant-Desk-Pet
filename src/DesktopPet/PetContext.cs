using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace DesktopPet;

/// <summary>
/// Wires the pet to the desktop: the layered window, tray icon, menu, mouse,
/// monitors and DPI, and hiding while a fullscreen app is in front.
/// </summary>
sealed class PetContext : ApplicationContext
{
    const int TickMs = 30;
    const int HiddenTickMs = 500;

    readonly Settings settings = Settings.Load();
    readonly string title;
    readonly Action<ContextMenuStrip, PetContext>? extendMenu;
    Atlas atlas;
    readonly Pet pet;
    readonly PetWindow window = new();
    readonly LayeredSurface surface = new();
    readonly ContextMenuStrip menu;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer = new() { Interval = TickMs };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly DemoRunner? demo;
    readonly Queue<(double t, int x, int y)> dragTrail = new();
    readonly List<(ToolStripMenuItem item, double size)> sizeItems = new();
    ToolStripMenuItem stayPutItem = null!;

    Dictionary<(string Row, int Index), Sprite> sprites = new();
    double scale = -1;
    IntPtr monitor;
    double lastT, nextMonitorAt, nextFullscreenAt, nextTopmostAt, nextDragMonitorAt;
    volatile bool displayChanged;
    volatile int savedX, savedY;
    bool hidden, quitting, pressed, dragging;
    Native.POINT pressAt;
    double grabDx, grabDy;
    IntPtr focusBeforeMenu;
    Sprite? lastSprite;
    int lastX, lastY;

    /// <param name="atlas">The pet to show; the context owns and disposes it.</param>
    /// <param name="trayIcon">Tray icon; owned and disposed by the context.</param>
    /// <param name="extendMenu">Adds extra items to the pet's menu (optional).</param>
    public PetContext(Atlas atlas, Icon trayIcon, bool demoMode, string title = "Desktop Pet",
        Action<ContextMenuStrip, PetContext>? extendMenu = null)
    {
        this.atlas = atlas;
        this.title = title;
        this.extendMenu = extendMenu;
        pet = new Pet(atlas) { StayPut = settings.StayPut, Demo = demoMode };
        menu = BuildMenu();

        tray = new NotifyIcon
        {
            Icon = trayIcon,
            Text = title,
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ComeHere();

        window.MouseDown += OnMouseDown;
        window.MouseMove += OnMouseMove;
        window.MouseUp += OnMouseUp;
        window.DpiChangedRaw += () => displayChanged = true;
        window.FormClosed += (_, _) => Quit(windowClosing: true);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        PlaceInitially();
        window.Show();
        RefreshMonitor(force: true);

        if (demoMode)
            demo = new DemoRunner(pet, atlas);
        else
            pet.Play("wave");

        timer.Tick += OnTick;
        timer.Start();
    }

    // ------------------------------------------------------------ loop

    void OnTick(object? sender, EventArgs e)
    {
        try
        {
            double t = clock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(t - lastT, 0, 0.1);
            lastT = t;

            if (t >= nextFullscreenAt)
            {
                nextFullscreenAt = t + 1.0;
                CheckFullscreen();
            }
            if (hidden)
                return;

            if ((pressed || dragging) && (Control.MouseButtons & MouseButtons.Left) == 0)
                LostMouseUp(); // the button-up went somewhere else (lock screen, UAC, ...)

            if (displayChanged)
            {
                displayChanged = false;
                RefreshMonitor(force: false); // rebuilds only if the scale really changed
            }
            else if (t >= nextMonitorAt && !dragging)
            {
                nextMonitorAt = t + 0.5;
                RefreshMonitor(force: false);
            }

            if (t >= nextTopmostAt && !menu.Visible)
            {
                nextTopmostAt = t + 3.0;
                Native.SetWindowPos(window.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            }

            Native.GetCursorPos(out var cursor);
            demo?.Tick(t);
            pet.Tick(dt, cursor.X, cursor.Y, Native.IdleMilliseconds());
            Render();
            savedX = (int)Math.Round(pet.X);
            savedY = (int)Math.Round(pet.GroundY);
        }
        catch (Exception ex)
        {
            Log.Error("tick", ex);
        }
    }

    void Render()
    {
        if (!sprites.TryGetValue(pet.Frame, out var s))
            return;
        int x = (int)Math.Round(pet.X) + s.X0;
        int y = (int)Math.Round(pet.AnchorY) + s.Y0;
        if (ReferenceEquals(s, lastSprite) && x == lastX && y == lastY)
            return;
        surface.Present(window.Handle, s, x, y);
        lastSprite = s;
        lastX = x;
        lastY = y;
    }

    void ForceRedraw()
    {
        lastSprite = null;
        surface.Invalidate();
    }

    // ------------------------------------------------------------ monitors

    void PlaceInitially()
    {
        IntPtr hmon = IntPtr.Zero;
        int x = 0;
        if (settings.X is int sx && settings.Y is int sy)
        {
            hmon = Native.MonitorFromPoint(new Native.POINT(sx, sy - 10), Native.MONITOR_DEFAULTTONULL);
            x = sx;
        }
        if (hmon == IntPtr.Zero || !Native.TryMonitor(hmon, out _, out var work))
        {
            hmon = Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTOPRIMARY);
            Native.TryMonitor(hmon, out _, out work);
            x = (work.Left + work.Right) / 2;
        }
        pet.X = x;
        pet.SetWork(work);
    }

    void RefreshMonitor(bool force)
    {
        var probe = new Native.POINT((int)Math.Round(pet.X), (int)Math.Round(pet.AnchorY) - 10);
        IntPtr hmon = Native.MonitorFromPoint(probe, Native.MONITOR_DEFAULTTONEAREST);
        if (!Native.TryMonitor(hmon, out _, out var work))
            return;
        uint dpi = Native.DpiOf(hmon);
        if (hmon != monitor)
            Log.Info($"monitor work area {work} at {dpi} dpi");
        monitor = hmon;

        // The art is drawn for 150% (144 dpi); keep the same physical-inch size elsewhere.
        double newScale = settings.Size * dpi / 144.0 * atlas.BaseScale;
        if (force || Math.Abs(newScale - scale) > 1e-6)
        {
            var sw = Stopwatch.StartNew();
            sprites = atlas.Build(newScale);
            scale = newScale;
            pet.Scale = newScale;
            ForceRedraw();
            Log.Info($"scale {newScale:0.###}: {sprites.Count} frames in {sw.ElapsedMilliseconds} ms");
        }
        if (force || !work.SameAs(pet.Work))
            pet.SetWork(work);
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e) => displayChanged = true;

    void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        settings.X = savedX;
        settings.Y = savedY;
        settings.Save();
    }

    // ------------------------------------------------------------ fullscreen

    void CheckFullscreen()
    {
        bool fullscreen = false;
        try
        {
            if (Native.SHQueryUserNotificationState(out int q) == 0 &&
                q is Native.QUNS_RUNNING_D3D_FULL_SCREEN or Native.QUNS_PRESENTATION_MODE)
                fullscreen = true;
            fullscreen = fullscreen || ForegroundCoversPetMonitor();
        }
        catch (Exception ex)
        {
            Log.Error("fullscreen check", ex);
        }

        if (fullscreen && !hidden)
        {
            hidden = true;
            if (dragging)
                FinishDrag();
            pressed = false;
            window.Hide();
            timer.Interval = HiddenTickMs;
            Log.Info("hidden: fullscreen app in front");
        }
        else if (!fullscreen && hidden)
        {
            hidden = false;
            window.Show();
            ForceRedraw();
            timer.Interval = TickMs;
            Log.Info("shown");
        }
    }

    bool ForegroundCoversPetMonitor()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == window.Handle || fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow())
            return false;
        var cls = new StringBuilder(64);
        Native.GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;
        if (!Native.GetWindowRect(fg, out var r))
            return false;
        IntPtr hm = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
        if (hm != monitor || !Native.TryMonitor(hm, out var b, out _))
            return false;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    // ------------------------------------------------------------ mouse

    void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        Native.GetCursorPos(out var c);
        pressed = true;
        dragging = false;
        pressAt = c;
        grabDx = pet.X - c.X;
        grabDy = pet.AnchorY - c.Y;
        dragTrail.Clear();
    }

    void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!pressed)
            return;
        if ((e.Button & MouseButtons.Left) == 0)
        {
            LostMouseUp();
            return;
        }
        Native.GetCursorPos(out var c);
        if (!dragging)
        {
            int threshold = (int)Math.Ceiling(6 * scale);
            if (Math.Abs(c.X - pressAt.X) < threshold && Math.Abs(c.Y - pressAt.Y) < threshold)
                return;
            dragging = true;
            pet.BeginDrag();
        }
        double t = clock.Elapsed.TotalSeconds;
        dragTrail.Enqueue((t, c.X, c.Y));
        while (dragTrail.Count > 2 && t - dragTrail.Peek().t > 0.12)
            dragTrail.Dequeue();
        pet.DragTo(c.X + grabDx, c.Y + grabDy);
        if (t >= nextDragMonitorAt)
        {
            nextDragMonitorAt = t + 0.1;
            RefreshMonitor(force: false);
        }
        Render();
    }

    void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            ShowMenu();
            return;
        }
        if (e.Button != MouseButtons.Left || !pressed)
            return;
        pressed = false;
        if (dragging)
            FinishDrag();
        else
            pet.Poke();
    }

    void LostMouseUp()
    {
        if (dragging)
            FinishDrag();
        pressed = false;
    }

    void FinishDrag()
    {
        dragging = false;
        pressed = false;
        double vx = 0, vy = 0;
        if (dragTrail.Count >= 2)
        {
            var a = dragTrail.Peek();
            var b = dragTrail.Last();
            double span = b.t - a.t;
            if (span > 0.01 && clock.Elapsed.TotalSeconds - b.t < 0.08) // still moving at release
            {
                vx = (b.x - a.x) / span;
                vy = (b.y - a.y) / span;
            }
        }
        dragTrail.Clear();
        RefreshMonitor(force: false);
        pet.EndDrag(vx, vy);
    }

    // ------------------------------------------------------------ menu

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();

        // Filled each time the menu opens, from whatever the current pet can do.
        var actions = new ToolStripMenuItem("Do something");
        var states = new ToolStripMenuItem("Hold a state");

        var size = new ToolStripMenuItem("Size");
        foreach (var (label, value) in new[] { ("Small", 0.75), ("Normal", 1.0), ("Large", 1.25), ("Huge", 1.5) })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => SetSize(value));
            sizeItems.Add((item, value));
            size.DropDownItems.Add(item);
        }

        stayPutItem = new ToolStripMenuItem("Stay put", null, (_, _) =>
        {
            pet.StayPut = !pet.StayPut;
            settings.StayPut = pet.StayPut;
            settings.Save();
        });

        m.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem(title) { Enabled = false },
            new ToolStripSeparator(),
            actions,
            states,
            size,
            stayPutItem,
            new ToolStripMenuItem("Come here", null, (_, _) => ComeHere()),
        });
        extendMenu?.Invoke(m, this);
        m.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripSeparator(),
            // Deferred so the menu is not disposed inside its own click handler.
            new ToolStripMenuItem("Exit", null, (_, _) => window.BeginInvoke(() => Quit())),
        });
        m.Opening += (_, _) =>
        {
            stayPutItem.Checked = pet.StayPut;
            foreach (var (item, value) in sizeItems)
                item.Checked = Math.Abs(value - settings.Size) < 1e-6;
            FillActionMenus(actions, states);
        };
        m.Closed += (_, _) => ReturnFocus();
        return m;
    }

    void FillActionMenus(ToolStripMenuItem actions, ToolStripMenuItem states)
    {
        actions.DropDownItems.Clear();
        foreach (var (label, clip) in new[]
        {
            ("Wave", "wave"), ("Jump", "jump"), ("Celebrate", "happy"), ("Think", "think"), ("Shrug", "shrug"),
            ("Hammer away", "working"), ("Look around", "review"), ("Oops", "failed"),
            ("Flex", "flex"), ("Mech leap", "leap"), ("Evolve", "evolve"),
        })
        {
            if (atlas.Has(clip))
                actions.DropDownItems.Add(label, null, (_, _) => pet.Play(clip));
        }
        if (atlas.Has("sleeping"))
        {
            if (actions.DropDownItems.Count > 0)
                actions.DropDownItems.Add(new ToolStripSeparator());
            actions.DropDownItems.Add("Take a nap", null, (_, _) => pet.Nap());
        }
        actions.Enabled = actions.DropDownItems.Count > 0;

        states.DropDownItems.Clear();
        foreach (var (label, clip) in new[] { ("Working", "working"), ("Waiting", "waiting"), ("Reviewing", "review") })
        {
            if (atlas.Has(clip))
                states.DropDownItems.Add(label, null, (_, _) => pet.SetState(clip));
        }
        if (states.DropDownItems.Count > 0)
        {
            states.DropDownItems.Add(new ToolStripSeparator());
            states.DropDownItems.Add("Back to normal", null, (_, _) => pet.SetState(null));
        }
        states.Enabled = states.DropDownItems.Count > 0;
    }

    /// <summary>The settings this context saves on exit; change them here so nothing is lost.</summary>
    public Settings Settings => settings;

    /// <summary>A short message from the tray icon.</summary>
    public void Notify(string text) => tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);

    /// <summary>
    /// Swap in a different pet while running (e.g. a freshly imported sheet).
    /// Takes ownership of <paramref name="next"/> and of <paramref name="icon"/>.
    /// </summary>
    public void ReplaceAtlas(Atlas next, Icon? icon = null)
    {
        var old = atlas;
        atlas = next;
        pet.ReplaceAtlas(next);
        RefreshMonitor(force: true);
        old.Dispose();
        if (icon is not null)
        {
            var oldIcon = tray.Icon;
            tray.Icon = icon;
            oldIcon?.Dispose();
        }
        Log.Info("pet replaced");
    }

    void ShowMenu()
    {
        // A menu owned by a window that never activates would not close on an
        // outside click; this click is user input, so taking foreground is allowed.
        // Remember who had focus so it can be handed back when the menu closes.
        IntPtr fg = Native.GetForegroundWindow();
        focusBeforeMenu = fg == window.Handle ? IntPtr.Zero : fg;
        Native.SetForegroundWindow(window.Handle);
        Native.GetCursorPos(out var c);
        menu.Show(new Point(c.X, c.Y));
    }

    void ReturnFocus()
    {
        IntPtr previous = focusBeforeMenu;
        focusBeforeMenu = IntPtr.Zero;
        // Only if the pet still holds focus: if the user clicked another
        // window to dismiss the menu, that window already has it.
        if (previous != IntPtr.Zero && Native.IsWindow(previous) &&
            !window.IsDisposed && Native.GetForegroundWindow() == window.Handle)
            Native.SetForegroundWindow(previous);
    }

    void SetSize(double value)
    {
        settings.Size = value;
        settings.Save();
        RefreshMonitor(force: true);
    }

    void ComeHere()
    {
        IntPtr prim = Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTOPRIMARY);
        if (!Native.TryMonitor(prim, out _, out var work))
            return;
        if (hidden)
            return;
        pet.DropIn((work.Left + work.Right) / 2.0, work, 180 * Math.Max(scale, 0.5));
        RefreshMonitor(force: false);
        Log.Info("come here");
    }

    /// <param name="windowClosing">
    /// True when called from the window's own FormClosed (WM_CLOSE from outside,
    /// e.g. taskkill). WinForms is already closing and will dispose it; closing
    /// it again from inside that handler would re-enter WM_CLOSE.
    /// </param>
    void Quit(bool windowClosing = false)
    {
        if (quitting)
            return;
        quitting = true;
        settings.X = savedX;
        settings.Y = savedY;
        settings.StayPut = pet.StayPut;
        settings.Save();

        timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        tray.Visible = false;
        tray.Icon?.Dispose();
        tray.Dispose();
        menu.Dispose();
        if (!windowClosing && !window.IsDisposed)
        {
            window.Close();
            window.Dispose();
        }
        surface.Dispose();
        atlas.Dispose();
        ExitThread();
    }
}
