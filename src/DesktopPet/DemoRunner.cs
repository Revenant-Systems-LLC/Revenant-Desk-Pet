namespace DesktopPet;

/// <summary>
/// "--demo": steps through every clip, held state and look direction on a
/// loop, logging each step. For checking the art and behaviour by eye.
/// </summary>
sealed class DemoRunner
{
    readonly List<(string Label, Func<double> Run)> steps = new();
    int index = -1;
    double nextAt;

    public DemoRunner(Pet pet, Atlas atlas)
    {
        steps.Add(("walk right", () => { pet.WalkBy(320); return 4.8; }));
        steps.Add(("walk left", () => { pet.WalkBy(-320); return 4.8; }));
        foreach (string c in new[] { "wave", "think", "shrug", "jump", "failed", "flex", "leap", "evolve" })
            steps.Add(($"clip {c}", () => { pet.Play(c); return atlas.Clip(c).Duration + 0.8; }));
        foreach (string s in new[] { "working", "waiting", "review" })
            steps.Add(($"state {s}", () => { pet.SetState(s); return 4.0; }));
        steps.Add(("state cleared", () => { pet.SetState(null); return 1.0; }));
        for (int i = 0; i < Atlas.LookDirections; i++)
        {
            int k = i;
            steps.Add(($"look {k * 22.5:0.#} deg", () => { pet.ForceLook(k); return 0.4; }));
        }
        steps.Add(("look follows cursor", () => { pet.ForceLook(null); return 0.5; }));
        steps.Add(("nap", () => { pet.Nap(); return 4.0; }));
        steps.Add(("poke awake", () => { pet.Poke(); return 2.5; }));
    }

    public void Tick(double t)
    {
        if (t < nextAt)
            return;
        index = (index + 1) % steps.Count;
        var (label, run) = steps[index];
        Log.Info($"demo: {label}");
        nextAt = t + run();
    }
}
