namespace DesktopPet;

enum Mode
{
    Idle,     // standing; fidgets, watches the cursor, decides what to do next
    Walk,     // heading for a spot on the floor (a floating pet drifts there)
    Action,   // playing a one-off clip (or a looping clip for a set time)
    Held,     // being dragged by the mouse
    Falling,  // dropped or thrown: coming down under gravity, or a floating pet drifting to a stop
    Asleep,   // user is away, or told to nap
    Hold,     // an outside caller pinned a state (the future AI hook)
}

/// <summary>
/// The pet's behaviour. Pure logic: it owns position and the current clip, and
/// knows nothing about windows. All distances are in physical pixels; tuning
/// constants are for scale 1 and are multiplied by <see cref="Scale"/>.
/// </summary>
sealed class Pet
{
    const double WalkSpeed = 75;        // px/s; roughly matches the stride at 7 fps
    const double EdgeMargin = 90;       // keep walks this far from the monitor edges
    const double WatchRadius = 320;     // cursor closer than this to the head gets watched
    const double HeadHeight = 100;      // head centre above the feet
    const double Gravity = 3000;        // px/s^2
    const double HardFall = 260;        // drops higher than this end in the "failed" clip
    const double SleepAfterSeconds = 300;
    // Floating pets (Damien). All code motion, no drawn frames: easy to tune or drop.
    const double HoverHeight = 36;      // px above the floor
    const double BobAmplitude = 4;      // px up and down
    const double BobPeriod = 2.6;       // s
    const double FloatSpeed = 55;       // px/s drifting
    const double FloatDrag = 2.2;       // 1/s; a throw slows down this fast
    const double FlipSpeed = 350;       // px/s; thrown at least this fast plays "thrown" (flip and squish)
    // Code-made squash and wobble, for walking pets only (Damien's motion is Dave's to draw).
    const double SquashSeconds = 0.45;  // a landing squashes, rebounds and settles in this long
    const double SquashDrop = 500;      // px; a drop this high squashes the most
    const double DizzySeconds = 3.2;    // shaken, then let go: wobbles this long
    const double DizzyTilt = 10;        // degrees either way
    const double NapSeconds = 120;
    // Terrain: window tops are ledges (the desktop as a 2D side-scroller).
    const double LedgeHeadroom = 170;   // px that must be free above a ledge (below the top of the screen)
    const double JumpReach = 280;       // px; the highest ledge he jumps up to
    const double FloatRise = 180;       // px/s; a floating pet glides to a new floor no faster than this
    // Chase mode: he goes after the mouse cursor. Pretend only: the real cursor is never touched.
    const double ChaseRun = 2.4;        // times walking speed (his walk frames play twice as fast)
    const double ChaseHopReach = 260;   // px above his head he jumps for the cursor

    Atlas atlas;
    readonly Random rng = new();

    Mode mode = Mode.Idle;
    ClipDef clip;
    string requested = "idle";
    double clipT, now;
    double nextDecision, nextFidget, actionUntil;
    double walkTarget, walkStart;
    int walkDir;
    double vx, vy, fallFrom, dragAy;
    string? holdClip;
    int? lookIndex, forcedLook;
    bool cursorNear, waking, napping;
    double cursorFarSince, lastGreet = double.NegativeInfinity, napUntil;
    double landAt = double.NegativeInfinity, landImpact, dizzyAt;
    bool dizzyPending;
    Native.RECT work;
    double floorY;              // the floor under the pet: the taskbar (work.Bottom) or a window's top edge
    IntPtr standingOn;          // the window whose top edge that is; zero for the taskbar
    double ownerLeft, ownerTop; // where that window was, to ride along when it moves
    int ownerMovedVersion = -1; // terrain refresh during which that window last moved
    Ledge? jumpFor;             // walking to a take-off spot, then jumping up onto this ledge
    bool selfMove;              // this fall is his own jump or step off: no drag, no fall-apart
    double clipRate = 1;        // animation speed (running in chase mode)
    int cursorX, cursorY;
    double chaseHopAt, chaseCaughtAt = double.NegativeInfinity;

    public Pet(Atlas atlas)
    {
        this.atlas = atlas;
        clip = atlas.Clip("idle");
    }

    bool CanSleep => atlas.Has("sleeping") && atlas.Has("doze_off") && atlas.Has("wake");
    bool Floats => atlas.Floats;
    /// <summary>Where a floating pet rests: a little above the floor, bobbing.</summary>
    double HoverLift => Math.Min(MaxLift, (HoverHeight + BobAmplitude * Math.Sin(now * 2 * Math.PI / BobPeriod)) * Scale);
    /// <summary>Highest a floating pet goes, so it stays on screen (tiny work areas included).</summary>
    double MaxLift => Math.Max(0, floorY - work.Top - 160 * Scale);

    /// <summary>Switch to a different pet: whatever was playing stops, and it stands idle.</summary>
    public void ReplaceAtlas(Atlas next)
    {
        atlas = next;
        holdClip = null;
        forcedLook = lookIndex = null;
        napping = waking = false;
        vx = vy = 0;
        Lift = 0;
        GoIdle(); // a floating pet rises to hover height from here (Tick)
    }

    public double X { get; set; }
    public double Lift { get; private set; }
    public double Scale { get; set; } = 1;
    public bool StayPut { get; set; }
    public bool Demo { get; set; }
    public Mode Mode => mode;
    public Native.RECT Work => work;
    /// <summary>The floor under the pet: the taskbar, or the top edge of the window it stands on.</summary>
    public double GroundY => floorY;
    /// <summary>Window tops to stand on; null keeps it on the taskbar.</summary>
    public ITerrain? Terrain { get; set; }
    /// <summary>Going after the mouse cursor.</summary>
    public bool Chasing { get; private set; }
    public double AnchorY => mode == Mode.Held ? Math.Min(dragAy, GroundY) : GroundY - Lift;
    public string? HeldState => holdClip;
    public (string Row, int Index) Frame { get; private set; } = ("idle", 0);
    /// <summary>Squash/stretch and tilt (degrees) about the anchor, for landings and dizziness; (1, 1, 0) most of the time.</summary>
    public (double Sx, double Sy, double Degrees) Pose { get; private set; } = (1, 1, 0);
    /// <summary>The monitor containing a screen point (its bounds and work area), or null if no monitor is there. Lets a throw carry on to the next screen.</summary>
    public Func<double, double, (Native.RECT Bounds, Native.RECT Work)?>? MonitorAt { get; set; }

    // ------------------------------------------------------------ public API

    /// <summary>
    /// Entry point for anything outside the pet (menu now, an AI later). A
    /// looping clip ("working", "waiting", "review") is held until cleared;
    /// a one-off clip just plays. Null or "idle" clears the held state.
    /// </summary>
    public void SetState(string? name)
    {
        if (string.IsNullOrEmpty(name) || name == "idle")
        {
            if (holdClip is not null)
                Log.Info($"state cleared (was {holdClip})");
            holdClip = null;
            if (mode == Mode.Hold)
                GoIdle();
            return;
        }
        // A pet without this animation plays its closest substitute.
        var c = atlas.Clip(name);
        if (!atlas.Has(name))
            Log.Info($"state '{name}' not drawn for this pet, using '{c.Name}'");
        if (!c.Loop)
        {
            Play(name);
            return;
        }
        if (holdClip == name && mode == Mode.Hold)
            return; // already doing it: don't restart the loop on every repeat
        holdClip = name;
        Log.Info($"state held: {name}");
        // While a one-off clip plays, let it finish; AfterAction enters the hold.
        if (mode is not (Mode.Held or Mode.Falling or Mode.Action))
            EnterHold();
    }

    /// <summary>Play a clip once; looping clips run for a few seconds.</summary>
    public void Play(string name)
    {
        if (mode is Mode.Held or Mode.Falling)
            return;
        napping = waking = false;
        StartAction(name, atlas.Clip(name).Loop ? 4.0 : null);
    }

    /// <summary>A left click on the pet.</summary>
    public void Poke()
    {
        if (Chasing && mode == Mode.Falling && selfMove)
        {
            // swatted in mid-jump: knocked away from the cursor
            vx = (X >= cursorX ? 1 : -1) * 700 * Scale;
            vy = Math.Min(vy, -300 * Scale);
            selfMove = false;
            chaseHopAt = now + 1.5;
            Log.Info("swatted in mid-air");
            return;
        }
        switch (mode)
        {
            case Mode.Asleep:
                napping = false;
                Wake();
                return;
            case Mode.Held or Mode.Falling:
                return;
            case Mode.Action when requested is "evolve" or "flex" or "leap" or "failed" or "dizzy":
                return; // let the big ones finish
        }
        if (Chasing)
        {
            // swatted: knocked away from the cursor
            Throw((X >= cursorX ? 1 : -1) * 700 * Scale, -550 * Scale);
            chaseHopAt = now + 1.5;
            return;
        }
        var reactions = new[] { ("jump", 40.0), ("wave", 30.0), ("happy", 20.0), ("shrug", 12.0), ("flex", 10.0), ("leap", 5.0), ("evolve", 3.0) }
            .Where(r => atlas.Has(r.Item1)).ToArray();
        StartAction(reactions.Length > 0 ? Pick(reactions) : "wave");
    }

    public void Nap()
    {
        if (mode is Mode.Held or Mode.Falling || !CanSleep)
            return;
        StartSleep();
        napping = true;
        napUntil = now + NapSeconds;
    }

    public void WalkBy(double dx)
    {
        if (mode is Mode.Held or Mode.Falling)
            return;
        double m = EdgeMargin * Scale;
        WalkTo(Math.Clamp(X + dx * Scale, work.Left + m, Math.Max(work.Left + m, work.Right - m)));
    }

    /// <summary>Chase mode on or off: he runs after the mouse cursor, jumps for it, and can be swatted away.</summary>
    public void SetChasing(bool on)
    {
        if (Chasing == on)
            return;
        Chasing = on;
        clipRate = 1;
        Log.Info(on ? "chase on" : "chase off");
        if (on && mode == Mode.Asleep)
        {
            napping = false;
            Wake();
        }
        else if (!on && mode == Mode.Walk)
            GoIdle();
    }

    /// <summary>Jump up onto a nearby window top, if there is one in reach (the AI link's "jumpup").</summary>
    public bool JumpUp()
    {
        if (mode is Mode.Held or Mode.Falling || Floats || Terrain is not { } t || PickJumpTarget(t) is not Ledge g)
            return false;
        napping = waking = false;
        StartJumpTo(g);
        return true;
    }

    /// <summary>Shaken hard while being held: dizzy once let go and at rest.</summary>
    public void Shaken()
    {
        if (mode == Mode.Held)
            dizzyPending = true;
    }

    /// <summary>Force a look direction (0..15, clockwise from up), or null to follow the cursor.</summary>
    public void ForceLook(int? index) => forcedLook = index is int i ? ((i % Atlas.LookDirections) + Atlas.LookDirections) % Atlas.LookDirections : null;

    public void BeginDrag()
    {
        ToTaskbar(); // measured from the taskbar while held, wherever it was standing
        jumpFor = null;
        selfMove = false;
        mode = Mode.Held;
        dizzyPending = false;
        napping = waking = false;
        lookIndex = null;
        PlayClip("held");
        dragAy = GroundY - Lift;
        Log.Info("picked up");
    }

    public void DragTo(double ax, double ay)
    {
        X = ax;
        dragAy = Math.Min(ay, GroundY);
        Lift = Math.Max(0, GroundY - ay);
    }

    public void EndDrag(double throwVx, double throwVy)
    {
        if (mode != Mode.Held)
            return;
        vx = Math.Clamp(throwVx, -1500 * Scale, 1500 * Scale);
        vy = Math.Clamp(throwVy, -1500 * Scale, 1500 * Scale);
        Lift = Math.Max(0, GroundY - dragAy);
        ClampX(); // dragged past the screen edge: bring it back into view
        if (Floats)
        {
            StartFlight();
            return;
        }
        if (Lift < 1)
        {
            Lift = 0;
            Landed(0);
            return;
        }
        fallFrom = Lift;
        mode = Mode.Falling;
        Log.Info($"dropped from {Lift:0}px");
    }

    /// <summary>Throw it from where it is (the AI link's "fling"). From the floor it starts with a small hop, or a sideways throw would just land.</summary>
    public void Throw(double throwVx, double throwVy)
    {
        if (mode is Mode.Held or Mode.Falling)
            return;
        BeginDrag();
        if (!Floats && Lift < 1)
        {
            dragAy = GroundY - 1;
            throwVy = Math.Min(throwVy, -400 * Scale);
        }
        EndDrag(throwVx, throwVy);
    }

    /// <summary>Put the pet somewhere new and let it drop in.</summary>
    public void DropIn(double x, Native.RECT newWork, double height)
    {
        work = newWork;
        standingOn = IntPtr.Zero;
        floorY = work.Bottom;
        jumpFor = null;
        selfMove = false;
        X = x;
        ClampX();
        vx = vy = 0;
        Lift = Math.Max(0, height);
        fallFrom = Lift;
        napping = waking = false;
        if (Floats)
        {
            StartFlight(); // floats down to hover height
            return;
        }
        PlayClip("held");
        mode = Mode.Falling;
    }

    /// <summary>The monitor under the pet, or its work area, changed.</summary>
    public void SetWork(Native.RECT r)
    {
        work = r;
        if (standingOn == IntPtr.Zero)
            floorY = r.Bottom; // on the taskbar: it may have moved
        if (mode == Mode.Held)
        {
            Lift = Math.Max(0, GroundY - dragAy);
            return;
        }
        ClampX();
        if (mode == Mode.Walk && jumpFor is null)
        {
            var (lo, hi) = WalkSpan();
            walkTarget = Math.Clamp(walkTarget, lo, Math.Max(lo, hi));
        }
    }

    // ------------------------------------------------------------ tick

    public void Tick(double dt, int cursorX, int cursorY, uint idleMs)
    {
        now += dt;
        clipT += dt * clipRate;
        (this.cursorX, this.cursorY) = (cursorX, cursorY);
        switch (mode)
        {
            case Mode.Idle: TickIdle(idleMs); break;
            case Mode.Walk: TickWalk(dt); break;
            case Mode.Action:
                if (clip.Loop ? now >= actionUntil : ClipDone)
                    AfterAction();
                break;
            case Mode.Falling: TickFall(dt); break;
            case Mode.Asleep: TickSleep(idleMs); break;
        }
        if (Chasing && mode is Mode.Idle or Mode.Walk)
            SteerChase();
        else
            clipRate = 1;
        if (mode is not (Mode.Held or Mode.Falling))
            KeepFooting();
        if (Floats && mode is not (Mode.Held or Mode.Falling))
        {
            // follow the bob closely, never jump; glide to a new floor
            // chasing, he floats up until his head is at the cursor (a cursor on another screen counts as level with this one)
            double cy = Math.Clamp(cursorY, work.Top, work.Bottom);
            double want = Chasing && mode is Mode.Idle or Mode.Walk
                ? Math.Clamp(GroundY - (cy + 140 * Scale), HoverLift, Math.Max(HoverLift, MaxLift))
                : HoverLift;
            double step = (want - Lift) * (1 - Math.Exp(-8 * dt)), cap = FloatRise * (Chasing ? 2 : 1) * Scale * dt;
            Lift += Math.Clamp(step, -cap, cap);
        }
        UpdateWatch(cursorX, cursorY);

        Pose = CurrentPose();

        int? look = atlas.HasLook ? forcedLook ?? lookIndex : null;
        Frame = mode == Mode.Idle && clip.Name == "idle" && look is int li
            ? ("look", li)
            : (clip.Row, ClipFrame());
    }

    void TickIdle(uint idleMs)
    {
        if (!Demo && CanSleep && idleMs >= SleepAfterSeconds * 1000)
        {
            StartSleep();
            return;
        }
        if (clip.Name != "idle" && ClipDone)
            PlayClip("idle"); // fidget finished
        if (Demo || Chasing)
            return;
        if (now >= nextDecision)
        {
            Decide();
            return;
        }
        if (clip.Name == "idle" && lookIndex is null && now >= nextFidget)
        {
            var fidgets = new[] { ("fidget_tilt", 1.0), ("fidget_chest", 1.0), ("fidget_lean", 1.0) }
                .Where(f => atlas.Has(f.Item1)).ToArray();
            if (fidgets.Length > 0)
                PlayClip(Pick(fidgets));
            nextFidget = now + Rand(4, 9);
        }
    }

    void Decide()
    {
        var options = new List<(Action act, double weight)> { (() => GoIdle(2, 5), 16) };
        // Only offer what this pet actually has, so a pet with few animations
        // does not repeat its fallback over and over.
        void Offer(string clip, double weight, double? loopSeconds = null)
        {
            if (atlas.Has(clip))
                options.Add((() => StartAction(clip, loopSeconds), weight));
        }
        Offer("think", 8);
        Offer("jump", 6);
        Offer("wave", 4);
        Offer("happy", 3);
        Offer("shrug", 4);
        Offer("review", 5, Rand(3, 6));
        Offer("working", 3, Rand(3, 6));
        Offer("flex", 1.2);
        Offer("leap", 1.0);
        Offer("evolve", 0.8);
        if (!StayPut)
            options.Add((StartWalk, 50));
        if (!StayPut && !Floats && Terrain is { } terrain)
        {
            if (PickJumpTarget(terrain) is Ledge up)
                options.Add((() => StartJumpTo(up), 14));
            if (standingOn != IntPtr.Zero)
                options.Add((StepOffEdge, 5));
        }
        Pick(options.ToArray())();
    }

    void StartWalk()
    {
        var (lo, hi) = WalkSpan();
        if (hi - lo < 50 * Scale)
        {
            GoIdle();
            return;
        }
        double target = X;
        for (int i = 0; i < 6; i++)
        {
            target = lo + rng.NextDouble() * (hi - lo);
            if (Math.Abs(target - X) >= 150 * Scale)
                break;
        }
        WalkTo(target);
    }

    void WalkTo(double target)
    {
        if (mode is Mode.Held or Mode.Falling)
            return;
        jumpFor = null;
        if (Math.Abs(target - X) < 20 * Scale)
        {
            GoIdle();
            return;
        }
        walkTarget = target;
        walkDir = target > X ? 1 : -1;
        walkStart = now;
        mode = Mode.Walk;
        // A floating pet never plays its walk rows (Dave: no squash-and-hop while he drifts).
        PlayClip(Floats ? "float" : walkDir > 0 ? "walk_right" : "walk_left");
        Log.Info($"walk {X:0} -> {target:0}");
    }

    void TickWalk(double dt)
    {
        double speed = Chasing ? WalkSpeed * ChaseRun : WalkSpeed;
        if (Floats)
        {
            // drift: ease in over the first second, ease out over the last 60 px
            double left = Math.Abs(walkTarget - X) / Scale;
            speed = FloatSpeed * (Chasing ? 3 : 1) * Math.Min(1, (now - walkStart) / 1.0) * Math.Clamp(left / 60, 0.2, 1);
        }
        X += walkDir * speed * Scale * dt;
        if ((walkTarget - X) * walkDir <= 0)
        {
            X = walkTarget;
            if (jumpFor is Ledge g)
            {
                jumpFor = null;
                Launch(g);
                return;
            }
            GoIdle(1.5, 5);
        }
    }

    void TickFall(double dt)
    {
        if (Floats)
        {
            TickFlight(dt);
            return;
        }
        double feetBefore = GroundY - Lift;
        vy += Gravity * Scale * dt;
        Lift -= vy * dt;
        X += vx * dt;
        if (!selfMove)
            vx *= Math.Exp(-1.5 * dt);
        SideEdges(0.4);
        fallFrom = Math.Max(fallFrom, Lift); // thrown upward: count from the peak
        if (vy > 0 && Terrain is { } terrain && LedgeCrossed(terrain, feetBefore, GroundY - Lift) is Ledge g)
        {
            double peak = GroundY - fallFrom;
            SetFloor(g.Y, g.Owner);
            Lift = 0;
            Log.Info("landed on a window");
            Landed(g.Y - peak);
            return;
        }
        if (Lift <= 0)
        {
            Lift = 0;
            Landed(fallFrom);
        }
    }

    /// <summary>A floating pet let go of (or dropped in): it drifts, slows, and settles at hover height.</summary>
    void StartFlight()
    {
        ToTaskbar();
        mode = Mode.Falling;
        napping = waking = false;
        lookIndex = null;
        // Only a drawn, one-off "thrown" clip; a looping stand-in would never finish.
        bool flung = Math.Sqrt(vx * vx + vy * vy) >= FlipSpeed * Scale && atlas.Has("thrown") && !atlas.Clip("thrown").Loop;
        PlayClip(flung ? "thrown" : "idle");
        Log.Info(flung ? "thrown (floating)" : "let go (floating)");
    }

    void TickFlight(double dt)
    {
        double drag = Math.Exp(-FloatDrag * dt);
        vx *= drag;
        vy *= drag;
        X += vx * dt;
        Lift -= vy * dt; // vy > 0 is downward
        SideEdges(0.5);
        double maxLift = MaxLift;
        if (Lift < 0)
        {
            Lift = 0;
            vy = -Math.Abs(vy) * 0.5; // bounce up off the floor
        }
        else if (Lift > maxLift)
        {
            // let go up near the top edge: sink back under it smoothly, no jump
            Lift = maxLift + (Lift - maxLift) * Math.Exp(-6 * dt);
            vy = Math.Abs(vy) * 0.5;
        }

        // As he slows, ease back toward hover height.
        double speed = Math.Sqrt(vx * vx + vy * vy);
        double slow = Math.Clamp(1 - speed / (200 * Scale), 0, 1);
        Lift += (HoverLift - Lift) * (1 - Math.Exp(-3 * slow * dt));

        if (requested == "thrown" && ClipDone)
            PlayClip("idle");
        if (requested != "thrown" && speed < 15 * Scale && Math.Abs(Lift - HoverLift) < 6 * Scale)
        {
            vx = vy = 0;
            Log.Info("settled");
            if (dizzyPending && atlas.Has("dizzy"))
                StartDizzy(); // only if drawn: no code-made wobble for a floating pet
            else
                GoIdle(1.5, 4);
            dizzyPending = false;
        }
    }

    /// <summary>
    /// In the air at a side of the work area: bounce off it, unless another
    /// monitor lies beyond it at this height, in which case fly on over.
    /// </summary>
    void SideEdges(double bounce)
    {
        double m = 40 * Scale;
        if (X < work.Left + m && vx <= 0 && !PassEdge(-1))
        {
            X = work.Left + m;
            vx = Math.Abs(vx) * bounce;
        }
        else if (X > work.Right - m && vx >= 0 && !PassEdge(1))
        {
            X = work.Right - m;
            vx = -Math.Abs(vx) * bounce;
        }
    }

    /// <summary>True if another monitor lies beyond this side at the pet's height; once past the edge, the pet moves onto it (its floor becomes the floor).</summary>
    bool PassEdge(int dir)
    {
        if (MonitorAt is null)
            return false;
        double absY = work.Bottom - Lift;
        // Look just past this monitor's own edge (not the work area's: a taskbar may sit on that side).
        if (MonitorAt((work.Left + work.Right) / 2.0, (work.Top + work.Bottom) / 2.0) is not var (bounds, _))
            return false;
        double probeX = dir < 0 ? bounds.Left - 1 : bounds.Right; // RECT right edges are exclusive
        if (MonitorAt(probeX, absY - 60 * Scale) is not var (_, r) || r.SameAs(work))
            return false;
        double edge = dir < 0 ? work.Left : work.Right;
        if (absY > r.Bottom)
            return false; // below that monitor's floor: its side is a wall
        if (dir < 0 ? X < edge : X >= edge)
        {
            double oldLift = Lift;
            work = r;
            floorY = r.Bottom;
            Lift = r.Bottom - absY; // same height on screen, measured from the new floor
            fallFrom += Lift - oldLift;
            Log.Info($"flew onto the monitor with work area {r}");
        }
        return true;
    }

    void Landed(double height)
    {
        vx = vy = 0;
        Log.Info($"landed after {height:0}px");
        if (height > 20 * Scale)
        {
            landAt = now;
            landImpact = Math.Clamp(height / (SquashDrop * Scale), 0.2, 1);
        }
        if (dizzyPending)
            StartDizzy();
        else
            StartAction(height > HardFall * Scale && !selfMove ? "failed" : "land");
        dizzyPending = false;
        selfMove = false;
    }

    // ------------------------------------------------------------ chase

    /// <summary>Head for the cursor along what he's standing on; under it, jump for it; at it, celebrate.</summary>
    void SteerChase()
    {
        double lo, hi;
        if (Floats)
            (lo, hi) = (work.Left + 40 * Scale, work.Right - 40 * Scale);
        else
            (lo, hi) = WalkSpan();
        double target = Math.Clamp(cursorX, Math.Min(lo, hi), Math.Max(lo, hi));
        double dx = target - X;
        if (Math.Abs(dx) > 24 * Scale)
        {
            if (mode != Mode.Walk || Math.Sign(dx) != walkDir)
                WalkTo(target);
            walkTarget = target; // keep following the cursor
            clipRate = Floats ? 1 : ChaseRun; // walk frames keep pace with the run
            return;
        }
        clipRate = 1;
        if (mode == Mode.Walk)
            GoIdle();

        double headY = AnchorY - HeadHeight * Scale;
        double above = headY - cursorY;
        bool reachable = Math.Abs(cursorX - X) < 60 * Scale;
        if (!Floats && reachable && above > 30 * Scale && above < ChaseHopReach * Scale && now >= chaseHopAt)
        {
            ChaseHop(above);
            return;
        }
        if (reachable && Math.Abs(cursorY - headY) < 50 * Scale && now - chaseCaughtAt > 3)
        {
            chaseCaughtAt = now;
            string? cheer = atlas.Has("happy") ? "happy" : atlas.Has("jump") ? "jump" : null;
            if (cheer is not null)
                StartAction(cheer); // got it!
        }
    }

    void ChaseHop(double above)
    {
        double gAcc = Gravity * Scale;
        double up = Math.Sqrt(2 * gAcc * (above + 20 * Scale));
        ToTaskbar();
        vy = -up;
        vx = Math.Clamp((cursorX - X) * 2, -300 * Scale, 300 * Scale);
        selfMove = true;
        fallFrom = Lift;
        napping = waking = false;
        mode = Mode.Falling;
        PlayClip("held");
        chaseHopAt = now + 1.3;
        Log.Info("jumps for the cursor");
    }

    // ------------------------------------------------------------ terrain

    /// <summary>Stand on a different floor without moving on screen (Lift is measured from the floor).</summary>
    void SetFloor(double y, IntPtr owner)
    {
        Lift += y - floorY;
        floorY = y;
        standingOn = owner;
        if (owner != IntPtr.Zero && Terrain is { } t && t.TryGetTop(owner, out double l, out _, out double top))
            (ownerLeft, ownerTop) = (l, top);
    }

    void ToTaskbar() => SetFloor(work.Bottom, IntPtr.Zero);

    /// <summary>Room for the pet above a ledge at this height, and above the taskbar.</summary>
    bool LedgeFits(double y) => y - work.Top >= LedgeHeadroom * Scale && y < work.Bottom - 8 * Scale;

    /// <summary>The ledge of the window it stands on, as of the last refresh.</summary>
    Ledge? CurrentLedge(ITerrain t)
    {
        foreach (var g in t.Ledges)
        {
            if (g.Owner == standingOn && Math.Abs(g.Y - floorY) < 3 && X >= g.Left - 6 && X <= g.Right + 6)
                return g;
        }
        return null;
    }

    /// <summary>Where it can walk: along the ledge it stands on, or the taskbar.</summary>
    (double Lo, double Hi) WalkSpan()
    {
        if (!Floats && standingOn != IntPtr.Zero)
        {
            if (Terrain is not { } t || CurrentLedge(t) is not Ledge g)
                return (X, X);
            double inset = 24 * Scale;
            return (Math.Max(g.Left, work.Left) + inset, Math.Min(g.Right, work.Right) - inset);
        }
        double m = EdgeMargin * Scale;
        return (work.Left + m, work.Right - m);
    }

    /// <summary>
    /// Every tick while not in the air. Standing on a window: ride along when
    /// it moves; step off when it closes, gets covered, or he walks off the
    /// end. A floating pet hovers over whatever is under it.
    /// </summary>
    void KeepFooting()
    {
        if (Floats)
        {
            FloatOverTerrain();
            return;
        }
        if (standingOn == IntPtr.Zero)
            return;
        if (Terrain is not { } t || !t.TryGetTop(standingOn, out double l, out double r, out double top))
        {
            StepOff("the window went away");
            return;
        }
        if (l != ownerLeft || top != ownerTop)
        {
            double dx = l - ownerLeft;
            X += dx;
            walkTarget += dx;
            floorY += top - ownerTop;
            (ownerLeft, ownerTop) = (l, top);
            ownerMovedVersion = t.Version; // what covers it is known again after the next refresh
        }
        bool fresh = ownerMovedVersion != t.Version;
        if (X < l || X > r)
            StepOff("walked off the edge");
        else if (!LedgeFits(floorY) || X < work.Left || X > work.Right)
            StepOff("no room up there");
        else if (fresh && CurrentLedge(t) is null)
            StepOff("the window got covered");
    }

    void StepOff(string why)
    {
        Log.Info($"off the window ({why})");
        ToTaskbar();
        vx = mode == Mode.Walk ? walkDir * WalkSpeed * Scale : 0;
        vy = 0;
        jumpFor = null;
        selfMove = true;
        fallFrom = Lift;
        napping = waking = false;
        mode = Mode.Falling;
        PlayClip("held");
    }

    /// <summary>
    /// The first ledge the feet passed through on the way down this tick, where
    /// its window is now (the snapshot can be half a second old and the window
    /// may be moving). A ledge level with the feet at the start doesn't count:
    /// that's the one it just stepped or was thrown off.
    /// </summary>
    Ledge? LedgeCrossed(ITerrain t, double feetBefore, double feetAfter)
    {
        if (X < work.Left || X > work.Right)
            return null;
        double slack = 40 * Scale;
        Ledge? best = null;
        foreach (var g in t.Ledges)
        {
            if (g.Y < feetBefore - slack || g.Y > feetAfter + slack || X < g.Left - slack || X > g.Right + slack)
                continue;
            if (!t.TryGetTop(g.Owner, out double l, out double r, out double top))
                continue;
            if (top <= feetBefore || top > feetAfter || !LedgeFits(top) ||
                X < Math.Max(l, g.Left - slack) + 4 * Scale || X > Math.Min(r, g.Right + slack) - 4 * Scale)
                continue;
            if (best is not Ledge b || top < b.Y)
                best = g with { Y = top };
        }
        return best;
    }

    /// <summary>A ledge a little way up, near enough to jump onto.</summary>
    Ledge? PickJumpTarget(ITerrain t)
    {
        var (lo, hi) = WalkSpan();
        var candidates = new List<Ledge>();
        foreach (var g in t.Ledges)
        {
            double rise = floorY - g.Y;
            double l = Math.Max(g.Left, work.Left), r = Math.Min(g.Right, work.Right);
            double gap = X < l ? l - X : X > r ? X - r : 0;
            if (rise >= 50 * Scale && rise <= JumpReach * Scale && r - l >= 90 * Scale && LedgeFits(g.Y) &&
                gap <= 350 * Scale && hi > lo)
                candidates.Add(g);
        }
        return candidates.Count > 0 ? candidates[rng.Next(candidates.Count)] : null;
    }

    void StartJumpTo(Ledge g)
    {
        var (lo, hi) = WalkSpan();
        double l = Math.Max(g.Left, work.Left), r = Math.Min(g.Right, work.Right);
        // Take off from beside the ledge (or right under it) and arc up onto its near end.
        double takeoff = X < l ? l - 70 * Scale : X > r ? r + 70 * Scale : X;
        takeoff = Math.Clamp(takeoff, lo, Math.Max(lo, hi));
        if (Math.Abs(takeoff - X) < 20 * Scale)
        {
            Launch(g);
            return;
        }
        WalkTo(takeoff);
        if (mode == Mode.Walk)
            jumpFor = g; // after WalkTo, which clears it
    }

    void Launch(Ledge g)
    {
        // The window may have moved or closed on the way to the take-off spot.
        if (Terrain is not { } t || !t.TryGetTop(g.Owner, out double wl, out double wr, out double top) || Math.Abs(top - g.Y) > 3)
        {
            GoIdle();
            return;
        }
        double l = Math.Max(g.Left, work.Left), r = Math.Min(g.Right, work.Right);
        double landX = Math.Clamp(X, l + 45 * Scale, Math.Max(l + 45 * Scale, r - 45 * Scale));
        double h = floorY - top;
        if (h < 20 * Scale)
        {
            GoIdle();
            return;
        }
        double gAcc = Gravity * Scale, over = 45 * Scale; // peak a little above the ledge
        double up = Math.Sqrt(2 * gAcc * (h + over));
        double flight = up / gAcc + Math.Sqrt(2 * over / gAcc);
        ToTaskbar();
        vy = -up;
        vx = (landX - X) / flight;
        selfMove = true;
        fallFrom = Lift;
        napping = waking = false;
        mode = Mode.Falling;
        PlayClip("held");
        Log.Info($"jump up {h:0}px onto a window");
    }

    /// <summary>Walk to just past the nearer end of this ledge, and drop off.</summary>
    void StepOffEdge()
    {
        if (Terrain is not { } t || CurrentLedge(t) is not Ledge g)
        {
            GoIdle();
            return;
        }
        double past = 30 * Scale, room = 60 * Scale;
        var ends = new List<double>();
        if (g.Left - past > work.Left + room)
            ends.Add(g.Left - past);
        if (g.Right + past < work.Right - room)
            ends.Add(g.Right + past);
        if (ends.Count == 0)
        {
            GoIdle();
            return;
        }
        WalkTo(ends.OrderBy(e => Math.Abs(e - X)).First());
    }

    /// <summary>A floating pet hovers over the highest window top under it, or the taskbar.</summary>
    void FloatOverTerrain()
    {
        double target = work.Bottom;
        IntPtr owner = IntPtr.Zero;
        if (Terrain is { } t)
        {
            foreach (var g in t.Ledges)
            {
                if (X >= g.Left && X <= g.Right && g.Y < target && LedgeFits(g.Y))
                {
                    target = g.Y;
                    owner = g.Owner;
                }
            }
            // where that window is now: it may have moved since the last refresh
            if (owner != IntPtr.Zero)
            {
                if (t.TryGetTop(owner, out _, out _, out double top) && LedgeFits(top))
                    target = top;
                else
                    (target, owner) = (work.Bottom, IntPtr.Zero);
            }
        }
        if (target != floorY || owner != standingOn)
            SetFloor(target, owner);
    }

    void StartDizzy() => StartAction("dizzy", DizzySeconds); // plays idle until a "dizzy" clip is drawn

    (double, double, double) CurrentPose()
    {
        if (Floats)
            return (1, 1, 0);
        double sx = 1, sy = 1, deg = 0;
        double tl = now - landAt;
        if (tl < SquashSeconds)
        {
            // squash down, spring back a little past normal, settle
            double k = landImpact * 0.3 * Math.Exp(-tl / 0.12) * Math.Cos(2 * Math.PI * tl / 0.3);
            sy = 1 - k;
            sx = 1 + 0.6 * k;
        }
        if (mode == Mode.Action && requested == "dizzy")
        {
            double td = now - dizzyAt;
            deg = DizzyTilt * Math.Sin(2 * Math.PI * 1.4 * td) * Math.Max(0, 1 - td / DizzySeconds);
        }
        return (sx, sy, deg);
    }

    void TickSleep(uint idleMs)
    {
        if (waking)
        {
            if (ClipDone)
                GoIdle();
            return;
        }
        if (clip.Name == "doze_off" && ClipDone)
            PlayClip("sleeping");
        bool wake = napping ? now >= napUntil : idleMs < 1500;
        if (wake)
            Wake();
    }

    void StartSleep()
    {
        jumpFor = null;
        mode = Mode.Asleep;
        waking = napping = false;
        lookIndex = null;
        PlayClip("doze_off");
        Log.Info("asleep");
    }

    void Wake()
    {
        if (mode != Mode.Asleep || waking)
            return;
        waking = true;
        napping = false;
        PlayClip("wake");
        Log.Info("waking");
    }

    void UpdateWatch(int cx, int cy)
    {
        double dx = cx - X, dy = cy - (AnchorY - HeadHeight * Scale);
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d >= WatchRadius * Scale)
        {
            if (cursorNear)
                cursorFarSince = now;
            cursorNear = false;
            lookIndex = null;
            return;
        }
        bool entered = !cursorNear;
        cursorNear = true;
        if (mode != Mode.Idle)
        {
            lookIndex = null;
            return;
        }
        // Greet the cursor when it comes back after a while away.
        if (entered && !Demo && now - cursorFarSince > 20 && now - lastGreet > 90)
        {
            lastGreet = now;
            lookIndex = null;
            StartAction("wave");
            return;
        }
        if (d < 30 * Scale || !atlas.HasLook)
        {
            lookIndex = null; // right on top of it (or no look frames): face forward
            return;
        }
        double angle = Math.Atan2(dx, -dy) * 180 / Math.PI; // clockwise from up
        if (angle < 0)
            angle += 360;
        lookIndex = (int)Math.Round(angle / (360.0 / Atlas.LookDirections)) % Atlas.LookDirections;
    }

    // ------------------------------------------------------------ helpers

    void StartAction(string name, double? loopSeconds = null)
    {
        jumpFor = null;
        if (name == "dizzy")
            dizzyAt = now;
        mode = Mode.Action;
        lookIndex = null;
        PlayClip(name);
        actionUntil = now + (clip.Loop ? loopSeconds ?? 4.0 : clip.Duration);
        Log.Info($"action {name}");
    }

    void AfterAction()
    {
        if (holdClip is not null)
            EnterHold();
        else
            GoIdle();
    }

    void EnterHold()
    {
        jumpFor = null;
        mode = Mode.Hold;
        napping = waking = false;
        lookIndex = null;
        PlayClip(holdClip!);
    }

    void GoIdle(double minDelay = 2, double maxDelay = 6)
    {
        jumpFor = null;
        if (holdClip is not null)
        {
            EnterHold();
            return;
        }
        mode = Mode.Idle;
        PlayClip("idle");
        nextDecision = now + Rand(minDelay, maxDelay);
        nextFidget = now + Rand(2, 5);
    }

    void PlayClip(string name)
    {
        requested = name; // may differ from clip.Name when a stand-in plays
        clip = atlas.Clip(name);
        clipT = 0;
    }

    bool ClipDone => !clip.Loop && clipT >= clip.Duration;

    int ClipFrame()
    {
        int n = clip.Frames.Length;
        int i = (int)(clipT * clip.Fps);
        i = clip.Loop ? i % n : Math.Min(i, n - 1);
        return clip.Frames[i];
    }

    void ClampX()
    {
        double m = 40 * Scale;
        X = Math.Clamp(X, work.Left + m, Math.Max(work.Left + m, work.Right - m));
    }

    double Rand(double a, double b) => a + rng.NextDouble() * (b - a);

    T Pick<T>(params (T item, double weight)[] options)
    {
        double r = rng.NextDouble() * options.Sum(o => o.weight);
        foreach (var (item, weight) in options)
        {
            r -= weight;
            if (r < 0)
                return item;
        }
        return options[^1].item;
    }
}
