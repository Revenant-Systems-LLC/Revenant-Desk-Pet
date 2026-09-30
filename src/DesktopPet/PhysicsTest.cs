namespace DesktopPet;

/// <summary>
/// Part of --selftest: runs the pet's own logic (no window, no mouse) through a
/// jump onto a window, a jump across onto one, a chase hop at the cursor and a
/// swing while held, at the real 60 ticks a second, and checks where he ends up.
/// A floating pet must never get code-made squash, stretch or swing.
/// </summary>
static class PhysicsTest
{
    const double Dt = 1.0 / 60;
    static readonly Native.RECT Work = new() { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };

    sealed class FakeTerrain : ITerrain
    {
        public readonly List<Ledge> List = new();
        public IReadOnlyList<Ledge> Ledges => List;
        public int Version => 1;

        public bool TryGetTop(IntPtr owner, out double left, out double right, out double y)
        {
            foreach (var g in List)
            {
                if (g.Owner == owner)
                {
                    (left, right, y) = (g.Left, g.Right, g.Y);
                    return true;
                }
            }
            left = right = y = 0;
            return false;
        }
    }

    /// <summary>Throws with what went wrong; returns a one-line summary.</summary>
    public static string Run(Atlas atlas)
    {
        var notes = new List<string>();
        if (!atlas.Floats)
        {
            notes.Add(JumpOnto(atlas, startX: 900, "straight up"));
            notes.Add(JumpOnto(atlas, startX: 600, "across"));
            notes.Add(ChaseHop(atlas));
            if (atlas.Has("climb"))
            {
                notes.Add(ClimbOnto(atlas, startX: 600, "from the left"));
                notes.Add(ClimbOnto(atlas, startX: 1500, "from the right"));
            }
        }
        notes.Add(Swing(atlas));
        return string.Join("; ", notes);
    }

    static Pet NewPet(Atlas atlas, double x)
    {
        var pet = new Pet(atlas) { StayPut = true, Demo = true };
        pet.SetWork(Work);
        pet.X = x;
        for (int i = 0; i < 30; i++)
            pet.Tick(Dt, -5000, -5000, 0); // settle (a floating pet rises to hover height)
        return pet;
    }

    static void Check(bool ok, string what)
    {
        if (!ok)
            throw new InvalidDataException("physics: " + what);
    }

    static void CheckPose(Pet pet, string what)
    {
        var (sx, sy, deg) = pet.Pose;
        var (dx, dy) = pet.PoseShift;
        Check(double.IsFinite(sx) && double.IsFinite(sy) && double.IsFinite(deg) && double.IsFinite(dx) && double.IsFinite(dy),
            $"{what}: pose is not a number");
        Check(sx is > 0.5 and < 1.5 && sy is > 0.5 and < 1.5, $"{what}: squash out of range ({sx:0.00}, {sy:0.00})");
    }

    static string JumpOnto(Atlas atlas, double startX, string how)
    {
        var terrain = new FakeTerrain();
        var ledge = new Ledge(850, 1250, Work.Bottom - 200, 1);
        terrain.List.Add(ledge);
        var pet = NewPet(atlas, startX);
        pet.Terrain = terrain;
        Check(pet.JumpUp(), $"jump {how}: no jump started");
        double top = 0, t = 0;
        bool left = false;
        for (; t < 6; t += Dt)
        {
            pet.Tick(Dt, -5000, -5000, 0);
            CheckPose(pet, $"jump {how}");
            left |= pet.Mode == Mode.Falling;
            top = Math.Max(top, Work.Bottom - pet.AnchorY);
            if (left && pet.Mode != Mode.Falling)
                break;
        }
        Check(left, $"jump {how}: never left the ground");
        Check(Math.Abs(pet.GroundY - ledge.Y) < 1, $"jump {how}: landed on {pet.GroundY:0}, not the ledge at {ledge.Y:0} (x {pet.X:0}, peak {top:0} px)");
        Check(pet.X >= ledge.Left && pet.X <= ledge.Right, $"jump {how}: landed at x {pet.X:0}, off the ledge");
        return $"jump {how} ok ({t:0.00} s, peak {top:0} px, x {pet.X:0})";
    }

    static string ClimbOnto(Atlas atlas, double startX, string how)
    {
        var terrain = new FakeTerrain();
        var ledge = new Ledge(850, 1250, Work.Bottom - 520, 2); // too tall to jump onto
        terrain.List.Add(ledge);
        var pet = NewPet(atlas, startX);
        pet.Terrain = terrain;
        Check(pet.ClimbUp(), $"climb {how}: no climb started");
        bool climbed = false;
        double t = 0, maxStep = 0;
        double? prevTop = null;
        string? prevRow = null;
        for (; t < 30; t += Dt)
        {
            pet.Tick(Dt, -5000, -5000, 0);
            CheckPose(pet, $"climb {how}");
            climbed |= pet.Mode == Mode.Climb;
            // where the top of the drawn frame is on screen: from the climb loop into the
            // pull-over it must carry on, not jump (the anchor itself does jump: the loop
            // is pinned by his head, the pull-over by the window's edge)
            double top = pet.AnchorY + (atlas.TopOf(pet.Frame) ?? 0) * pet.Scale;
            if (prevTop is double pt && prevRow == "climb" && pet.Frame.Row == "climb_top")
                maxStep = Math.Max(maxStep, Math.Abs(top - pt));
            prevTop = top;
            prevRow = pet.Frame.Row;
            if (climbed && pet.Mode != Mode.Climb)
                break;
        }
        Check(climbed, $"climb {how}: never started climbing");
        Check(pet.Mode != Mode.Falling, $"climb {how}: fell off");
        Check(Math.Abs(pet.GroundY - ledge.Y) < 1, $"climb {how}: ended on {pet.GroundY:0}, not the ledge at {ledge.Y:0}");
        Check(pet.X >= ledge.Left && pet.X <= ledge.Right, $"climb {how}: ended at x {pet.X:0}, off the ledge");
        Check(maxStep < 40, $"climb {how}: jumped {maxStep:0} px going over the top");
        return $"climb {how} ok ({t:0.0} s, x {pet.X:0}, {maxStep:0} px shift going over the top)";
    }

    static string ChaseHop(Atlas atlas)
    {
        var pet = NewPet(atlas, 900);
        pet.SetChasing(true);
        double headY = Work.Bottom - 100; // HeadHeight at scale 1
        int cy = (int)(headY - 150);
        bool left = false;
        double peak = 0, t = 0;
        for (; t < 4; t += Dt)
        {
            pet.Tick(Dt, 900, cy, 0);
            CheckPose(pet, "chase hop");
            left |= pet.Mode == Mode.Falling;
            peak = Math.Max(peak, pet.Lift);
            if (left && pet.Mode != Mode.Falling)
                break;
        }
        pet.SetChasing(false);
        Check(left, "chase hop: never jumped for the cursor");
        Check(peak > 100, $"chase hop: only rose {peak:0} px");
        Check(pet.Lift == 0, "chase hop: didn't come back down");
        return $"chase hop ok (peak {peak:0} px)";
    }

    static string Swing(Atlas atlas)
    {
        var pet = NewPet(atlas, 900);
        double ay = pet.AnchorY - 60;
        pet.BeginDrag(0, -120); // held 120 px above his feet
        double maxDeg = 0;
        double x = 900, v = 0;
        for (int i = 0; i < 18; i++) // the cursor speeds up to the right for 0.3 s
        {
            v += 3000 * Dt;
            x += v * Dt;
            pet.DragTo(x, ay);
            pet.Tick(Dt, (int)x, (int)ay, 0);
            CheckPose(pet, "swing");
            maxDeg = Math.Max(maxDeg, pet.Pose.Degrees);
        }
        for (int i = 0; i < 180; i++) // then holds still for 3 s
        {
            pet.DragTo(x, ay);
            pet.Tick(Dt, (int)x, (int)ay, 0);
            CheckPose(pet, "swing");
        }
        double rest = pet.Pose.Degrees;
        pet.EndDrag(0, 0);
        if (atlas.Floats)
        {
            Check(maxDeg == 0 && pet.PoseShift == default, "swing: a floating pet got code-made swing");
            return "no swing for a floating pet ok";
        }
        Check(maxDeg > 2, $"swing: pulled right, he only tilted {maxDeg:0.0} degrees");
        Check(maxDeg <= 35.01, $"swing: tilted {maxDeg:0.0} degrees, over the limit");
        Check(Math.Abs(rest) < 5, $"swing: still at {rest:0.0} degrees after 3 s");
        return $"swing ok (max {maxDeg:0.0} deg, {rest:0.0} after 3 s)";
    }
}
