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
    const double NapSeconds = 120;

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
    Native.RECT work;

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
    double MaxLift => Math.Max(0, work.Bottom - work.Top - 160 * Scale);

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
    public double GroundY => work.Bottom;
    public double AnchorY => mode == Mode.Held ? Math.Min(dragAy, GroundY) : GroundY - Lift;
    public string? HeldState => holdClip;
    public (string Row, int Index) Frame { get; private set; } = ("idle", 0);

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
        switch (mode)
        {
            case Mode.Asleep:
                napping = false;
                Wake();
                return;
            case Mode.Held or Mode.Falling:
                return;
            case Mode.Action when requested is "evolve" or "flex" or "leap" or "failed":
                return; // let the big ones finish
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

    /// <summary>Force a look direction (0..15, clockwise from up), or null to follow the cursor.</summary>
    public void ForceLook(int? index) => forcedLook = index is int i ? ((i % Atlas.LookDirections) + Atlas.LookDirections) % Atlas.LookDirections : null;

    public void BeginDrag()
    {
        mode = Mode.Held;
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
        if (mode == Mode.Held)
        {
            Lift = Math.Max(0, GroundY - dragAy);
            return;
        }
        ClampX();
        if (mode == Mode.Walk)
        {
            double m = EdgeMargin * Scale;
            walkTarget = Math.Clamp(walkTarget, work.Left + m, Math.Max(work.Left + m, work.Right - m));
        }
    }

    // ------------------------------------------------------------ tick

    public void Tick(double dt, int cursorX, int cursorY, uint idleMs)
    {
        now += dt;
        clipT += dt;
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
        if (Floats && mode is not (Mode.Held or Mode.Falling))
            Lift += (HoverLift - Lift) * (1 - Math.Exp(-8 * dt)); // follow the bob closely, never jump
        UpdateWatch(cursorX, cursorY);

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
        if (Demo)
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
        Pick(options.ToArray())();
    }

    void StartWalk()
    {
        double m = EdgeMargin * Scale;
        double lo = work.Left + m, hi = work.Right - m;
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
        double speed = WalkSpeed;
        if (Floats)
        {
            // drift: ease in over the first second, ease out over the last 60 px
            double left = Math.Abs(walkTarget - X) / Scale;
            speed = FloatSpeed * Math.Min(1, (now - walkStart) / 1.0) * Math.Clamp(left / 60, 0.2, 1);
        }
        X += walkDir * speed * Scale * dt;
        if ((walkTarget - X) * walkDir <= 0)
        {
            X = walkTarget;
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
        vy += Gravity * Scale * dt;
        Lift -= vy * dt;
        X += vx * dt;
        vx *= Math.Exp(-1.5 * dt);
        double m = 40 * Scale;
        if (X < work.Left + m)
        {
            X = work.Left + m;
            vx = Math.Abs(vx) * 0.4;
        }
        else if (X > work.Right - m)
        {
            X = work.Right - m;
            vx = -Math.Abs(vx) * 0.4;
        }
        fallFrom = Math.Max(fallFrom, Lift); // thrown upward: count from the peak
        if (Lift <= 0)
        {
            Lift = 0;
            Landed(fallFrom);
        }
    }

    /// <summary>A floating pet let go of (or dropped in): it drifts, slows, and settles at hover height.</summary>
    void StartFlight()
    {
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
        double m = 40 * Scale;
        if (X < work.Left + m)
        {
            X = work.Left + m;
            vx = Math.Abs(vx) * 0.5;
        }
        else if (X > work.Right - m)
        {
            X = work.Right - m;
            vx = -Math.Abs(vx) * 0.5;
        }
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
            GoIdle(1.5, 4);
        }
    }

    void Landed(double height)
    {
        vx = vy = 0;
        Log.Info($"landed after {height:0}px");
        StartAction(height > HardFall * Scale ? "failed" : "land");
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
        mode = Mode.Hold;
        napping = waking = false;
        lookIndex = null;
        PlayClip(holdClip!);
    }

    void GoIdle(double minDelay = 2, double maxDelay = 6)
    {
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
