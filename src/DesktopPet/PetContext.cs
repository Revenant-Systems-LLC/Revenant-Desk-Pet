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
    ToolStripMenuItem petItems = null!;
    string petId;                // the built-in pet on screen now (settings.Pet is the saved choice)

    // AI link: Claude Code hooks (via revbot-hook.exe) and direct commands
    AiLink? aiLink;
    /// <summary>What one Claude Code session is doing; the pet shows the most important across sessions.</summary>
    sealed class AiSession
    {
        public string? Hold;
        public double LastAt;
        public int Subagents;
    }
    readonly Dictionary<string, AiSession> aiSessions = new();
    bool aiHolding;              // the current held state was set by the AI link
    double aiHoldChangedAt = double.NegativeInfinity;
    string? pendingAiHold;       // waits out the minimum dwell before switching
    const double AiQuietSeconds = 90;          // a session silent this long has gone away
    const double AiWaitingQuietSeconds = 1800; // but a pending permission prompt can wait a long time
    const double AiMinDwellSeconds = 1.2;      // stop rapid tool calls flickering between animations

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
    Native.RECT petWorkSeen;
    DesktopTerrain? terrain;
    double nextTerrainAt;
    ToolStripMenuItem terrainItem = null!;
    ToolStripMenuItem chaseItem = null!;
    int lastX, lastY;

    /// <param name="atlas">The pet to show; the context owns and disposes it.</param>
    /// <param name="trayIcon">Tray icon; owned and disposed by the context.</param>
    /// <param name="extendMenu">Adds extra items to the pet's menu (optional).</param>
    /// <param name="petId">Which built-in pet <paramref name="atlas"/> is.</param>
    public PetContext(Atlas atlas, Icon trayIcon, bool demoMode, string title = "Desktop Pet",
        Action<ContextMenuStrip, PetContext>? extendMenu = null, string petId = "revbot")
    {
        this.atlas = atlas;
        this.petId = petId;
        this.title = title;
        this.extendMenu = extendMenu;
        pet = new Pet(atlas) { StayPut = settings.StayPut, Demo = demoMode, MonitorAt = MonitorAt };
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
        terrain = new DesktopTerrain(window.Handle);
        if (settings.Terrain)
        {
            terrain.Refresh();
            pet.Terrain = terrain;
        }

        if (demoMode)
            demo = new DemoRunner(pet, atlas);
        else
            pet.Play("wave");

        try
        {
            aiLink = new AiLink(OnAiLine);
        }
        catch (Exception ex)
        {
            Log.Error("ai link start", ex); // the pet works without it
        }

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

            if (pet.Terrain is not null && terrain is not null && t >= nextTerrainAt)
            {
                nextTerrainAt = t + 0.5;
                terrain.Refresh();
            }

            Native.GetCursorPos(out var cursor);
            demo?.Tick(t);
            TickAiLink(t);
            pet.Tick(dt, cursor.X, cursor.Y, Native.IdleMilliseconds());
            if (!pet.Work.SameAs(petWorkSeen))
            {
                petWorkSeen = pet.Work;
                RefreshMonitor(force: false); // flew onto another monitor: its DPI now
            }
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
        var (sx, sy, deg) = pet.Pose;
        if (sx != 1 || sy != 1 || deg != 0)
            s = Atlas.Posed(s, sx, sy, deg);
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

    // ------------------------------------------------------------ AI link

    /// <summary>
    /// One line from the AI link, on the UI thread. Hook lines look like
    /// "hook PreToolUse session=ID tool=Edit" or "hook Notification session=ID type=permission_prompt";
    /// direct commands are "state NAME", "play NAME", "pet ID", "fling VX VY", "chase [on|off]", "jumpup", "ledges", "clear" and "ping".
    /// </summary>
    void OnAiLine(string line)
    {
        if (quitting)
            return;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;
        string verb = parts[0].ToLowerInvariant();
        string? arg = parts.Length > 1 ? parts[1] : null;
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts.Skip(1))
        {
            int eq = p.IndexOf('=');
            if (eq > 0)
                kv[p[..eq]] = p[(eq + 1)..]; // last one wins; duplicates must not throw
        }

        switch (verb)
        {
            case "hook" when arg is not null:
                OnHook(arg, kv.GetValueOrDefault("session") ?? "unknown", kv.GetValueOrDefault("tool"), kv.GetValueOrDefault("type"));
                break;
            case "state" when arg is not null:
                SessionHold("direct", arg);
                ApplyAiHold(immediate: true);
                break;
            case "play" when arg is not null:
                pet.Play(arg);
                break;
            case "pet" when arg is not null:
                SwitchPet(arg.ToLowerInvariant());
                break;
            case "fling" when parts.Length >= 3:
                Fling(parts[1], parts[2]);
                break;
            case "clear":
                aiSessions.Clear();
                ApplyAiHold(immediate: true);
                break;
            case "ping":
                Log.Info("ai link ping");
                break;
            case "chase":
                pet.SetChasing(arg?.ToLowerInvariant() switch { "on" => true, "off" => false, _ => !pet.Chasing });
                break;
            case "jumpup":
                if (!pet.JumpUp())
                    Log.Info("nothing to jump up onto");
                break;
            case "ledges": // what the terrain sees: positions only, never window titles
                if (terrain is not null)
                {
                    terrain.Refresh();
                    Log.Info($"ledges ({terrain.Ledges.Count}): " + string.Join(" ", terrain.Ledges.Select(g => $"[{g.Left:0}..{g.Right:0} y{g.Y:0}]")));
                }
                break;
            default:
                Log.Info($"ai link: ignored '{Trim(line)}'");
                break;
        }
    }

    void OnHook(string hookEvent, string session, string? tool, string? type)
    {
        bool immediate = true;
        switch (hookEvent)
        {
            case "SessionStart":
                pet.Play("wave");
                return;
            case "UserPromptSubmit":
                SessionHold(session, "review");            // reading what you asked, thinking it over
                break;
            case "PreToolUse":
                SessionHold(session, StateForTool(tool ?? ""));
                immediate = false;
                break;
            case "PostToolUse":
                // after you approve a permission the tool runs: put the hand down
                if (aiSessions.TryGetValue(session, out var current) && current.Hold == "waiting")
                    SessionHold(session, StateForTool(tool ?? ""));
                else
                    Touch(session);
                immediate = false;
                break;
            case "PostToolUseFailure":
                Touch(session);
                pet.Play("failed");                        // oops; the held state resumes after
                break;
            case "PermissionRequest":
                SessionHold(session, "waiting");           // hand up: needs your OK
                break;
            case "Notification" when type == "permission_prompt":
                SessionHold(session, "waiting");
                break;
            case "Notification" when type == "idle_prompt":
                aiSessions.Remove(session);
                ApplyAiHold(immediate: true);
                pet.Play("wave");                          // your turn
                return;
            case "SubagentStart":
            {
                var st = Session(session);
                st.Subagents++;
                st.Hold ??= "working";
                if (aiSessions.Values.Sum(x => x.Subagents) == 3)
                    pet.Play("flex");                      // a crowd of agents: power up
                immediate = false;
                break;
            }
            case "SubagentStop":
            {
                var st = Session(session);
                st.Subagents = Math.Max(0, st.Subagents - 1);
                break;
            }
            case "Stop":
                aiSessions.Remove(session);
                ApplyAiHold(immediate: true);
                pet.Play("happy");                         // done (falls back to a jump); others' state resumes after
                return;
            case "StopFailure":
                aiSessions.Remove(session);
                ApplyAiHold(immediate: true);
                pet.Play("failed");
                return;
            case "SessionEnd":
                aiSessions.Remove(session);
                break;
            default:
                Log.Info($"ai link: no reaction for {Trim(hookEvent)}");
                return;
        }
        ApplyAiHold(immediate);
    }

    AiSession Session(string id)
    {
        if (!aiSessions.TryGetValue(id, out var s))
            aiSessions[id] = s = new AiSession();
        s.LastAt = clock.Elapsed.TotalSeconds;
        return s;
    }

    void Touch(string id) => Session(id);

    void SessionHold(string id, string state) => Session(id).Hold = state;

    /// <summary>Reading and searching look around; asking waits; everything else is hands-on work.</summary>
    static string StateForTool(string tool)
    {
        if (tool is "AskUserQuestion" or "ExitPlanMode")
            return "waiting";
        string t = tool.ToLowerInvariant();
        string[] looking = ["read", "grep", "glob", "search", "fetch", "find", "list", "get", "query", "view", "screenshot", "snapshot"];
        return looking.Any(t.Contains) ? "review" : "working";
    }

    /// <summary>The state to show across all sessions: needing you beats working beats looking around.</summary>
    string? AggregateHold()
    {
        static int Rank(string? h) => h switch { "waiting" => 3, "working" => 2, null => 0, _ => 1 };
        return aiSessions.Values.Select(s => s.Hold).OrderByDescending(Rank).FirstOrDefault();
    }

    void ApplyAiHold(bool immediate)
    {
        string? state = AggregateHold();
        double now = clock.Elapsed.TotalSeconds;
        if (state is null)
        {
            pendingAiHold = null;
            if (aiHolding)
                pet.SetState(null);
            aiHolding = false;
            return;
        }
        if (aiHolding && pet.HeldState == state)
        {
            pendingAiHold = null;
            return;
        }
        if (!immediate && aiHolding && now - aiHoldChangedAt < AiMinDwellSeconds)
        {
            pendingAiHold = state; // applied by TickAiLink once the dwell is over
            return;
        }
        pendingAiHold = null;
        aiHolding = true;
        aiHoldChangedAt = now;
        pet.SetState(state);
    }

    void TickAiLink(double t)
    {
        // Cleared from the menu (or anything else): respect it, and drop what was queued.
        if (aiHolding && pet.HeldState is null)
        {
            aiHolding = false;
            pendingAiHold = null;
            aiSessions.Clear();
            return;
        }
        // Sessions that went quiet (Claude Code closed or crashed) stand down.
        var gone = aiSessions
            .Where(kv => t - kv.Value.LastAt > (kv.Value.Hold == "waiting" ? AiWaitingQuietSeconds : AiQuietSeconds))
            .Select(kv => kv.Key).ToList();
        if (gone.Count > 0)
        {
            foreach (var id in gone)
                aiSessions.Remove(id);
            Log.Info($"ai link: {gone.Count} quiet session(s) stood down");
            ApplyAiHold(immediate: true);
        }
        if (pendingAiHold is not null && t - aiHoldChangedAt >= AiMinDwellSeconds)
            ApplyAiHold(immediate: true);
    }

    static string Trim(string s) => s.Length > 60 ? s[..60] : s;

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
        // In the air the pet decides which monitor it is over (a throw can carry it
        // across), unless its monitor has gone away (unplugged, rearranged).
        if (force || (!work.SameAs(pet.Work) && (pet.Mode != Mode.Falling || !StillThere(pet.Work))))
            pet.SetWork(work);
    }

    static (Native.RECT Bounds, Native.RECT Work)? MonitorAt(double x, double y)
    {
        IntPtr h = Native.MonitorFromPoint(new Native.POINT((int)Math.Round(x), (int)Math.Round(y)), Native.MONITOR_DEFAULTTONULL);
        return h != IntPtr.Zero && Native.TryMonitor(h, out var bounds, out var work) ? (bounds, work) : null;
    }

    static bool StillThere(Native.RECT work) =>
        MonitorAt((work.Left + work.Right) / 2.0, (work.Top + work.Bottom) / 2.0) is var (_, w) && w.SameAs(work);

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
            shakeTimesX.Clear();
            shakeTimesY.Clear();
            shakeDirX = shakeDirY = 0;
            shakeRunX = shakeRunY = 0;
            shakeLastX = c.X;
            shakeLastY = c.Y;
        }
        TrackShake(clock.Elapsed.TotalSeconds, c.X, c.Y);
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

    // Shaking while held: quick back-and-forth swings along one axis, each at least ~30 px, four within a second.
    readonly Queue<double> shakeTimesX = new(), shakeTimesY = new();
    int shakeDirX, shakeDirY, shakeLastX, shakeLastY;
    double shakeRunX, shakeRunY;

    void TrackShake(double t, int x, int y)
    {
        int dx = x - shakeLastX, dy = y - shakeLastY;
        shakeLastX = x;
        shakeLastY = y;
        CountSwing(shakeTimesX, ref shakeDirX, ref shakeRunX, dx, t);
        CountSwing(shakeTimesY, ref shakeDirY, ref shakeRunY, dy, t);
        if (shakeTimesX.Count >= 4 || shakeTimesY.Count >= 4)
        {
            shakeTimesX.Clear();
            shakeTimesY.Clear();
            pet.Shaken();
            Log.Info("shaken");
        }
    }

    void CountSwing(Queue<double> swings, ref int dir, ref double run, int d, double t)
    {
        while (swings.Count > 0 && t - swings.Peek() > 1.0)
            swings.Dequeue();
        if (d == 0)
            return;
        int sign = Math.Sign(d);
        if (sign == dir)
        {
            run += Math.Abs(d);
            return;
        }
        if (dir != 0 && run >= 30 * scale)
            swings.Enqueue(t); // a full swing one way, now turning back
        dir = sign;
        run = Math.Abs(d);
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

        var petMenu = new ToolStripMenuItem("Pet");
        foreach (var (id, name) in Atlas.BuiltInPets)
        {
            var item = new ToolStripMenuItem(name, null, (_, _) => SwitchPet(id)) { Tag = id };
            petMenu.DropDownItems.Add(item);
        }
        petItems = petMenu;

        terrainItem = new ToolStripMenuItem("Climb on windows", null, (_, _) =>
        {
            settings.Terrain = !settings.Terrain;
            pet.Terrain = settings.Terrain ? terrain : null;
            if (settings.Terrain)
                terrain?.Refresh();
            settings.Save();
        });

        chaseItem = new ToolStripMenuItem("Chase the mouse", null, (_, _) => pet.SetChasing(!pet.Chasing));

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
            petMenu,
            chaseItem,
            stayPutItem,
            terrainItem,
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
            terrainItem.Checked = settings.Terrain;
            chaseItem.Checked = pet.Chasing;
            foreach (var (item, value) in sizeItems)
                item.Checked = Math.Abs(value - settings.Size) < 1e-6;
            FillActionMenus(actions, states);
            foreach (ToolStripMenuItem item in petItems.DropDownItems)
                item.Checked = (string?)item.Tag == petId;
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
    /// Takes ownership of <paramref name="next"/> and of <paramref name="icon"/>,
    /// except when it throws: then nothing has changed and the caller still owns both.
    /// </summary>
    public void ReplaceAtlas(Atlas next, Icon? icon = null)
    {
        // Build the new frames before touching anything, so a pet that can't be
        // built leaves the current one running.
        uint dpi = monitor != IntPtr.Zero ? Native.DpiOf(monitor) : 96;
        double newScale = settings.Size * dpi / 144.0 * next.BaseScale;
        var built = next.Build(newScale);

        var old = atlas;
        atlas = next;
        pet.ReplaceAtlas(next);
        sprites = built;
        scale = newScale;
        pet.Scale = newScale;
        ForceRedraw();
        old.Dispose();
        if (icon is not null)
        {
            var oldIcon = tray.Icon;
            tray.Icon = icon;
            oldIcon?.Dispose();
        }
        Log.Info("pet replaced");
    }

    /// <summary>Throw the pet from where it is, as if picked up and let go at this speed (px/s at normal size).</summary>
    void Fling(string vxText, string vyText)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (dragging || pressed ||
            !double.TryParse(vxText, System.Globalization.NumberStyles.Float, inv, out double vx) ||
            !double.TryParse(vyText, System.Globalization.NumberStyles.Float, inv, out double vy) ||
            !double.IsFinite(vx) || !double.IsFinite(vy))
            return;
        pet.Throw(vx * pet.Scale, vy * pet.Scale); // the speed is clamped there
    }

    /// <summary>Switch to another built-in pet and remember the choice.</summary>
    public void SwitchPet(string id)
    {
        if (!Atlas.IsBuiltIn(id) || id == petId)
            return;
        if (pet.Mode is Mode.Held or Mode.Falling)
        {
            Log.Info($"switch to {id} ignored while held or falling");
            return;
        }
        Atlas? next = null;
        Icon? icon = null;
        try
        {
            next = Atlas.Load(id);
            icon = Atlas.LoadIcon(id, SystemInformation.SmallIconSize);
            ReplaceAtlas(next, icon);
            next = null; // both owned by the context now
            icon = null;
            petId = id;
            settings.Pet = id;
            settings.Save();
            pet.Play("wave");
            Log.Info($"switched to {id}");
        }
        catch (Exception ex)
        {
            next?.Dispose();
            icon?.Dispose();
            Log.Error($"switch to {id}", ex);
        }
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
        aiLink?.Dispose();
        surface.Dispose();
        atlas.Dispose();
        ExitThread();
    }
}
