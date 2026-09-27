namespace DesktopPet;

static class Program
{
    /// <summary>Every clip name the app ever asks for, used by --selftest.</summary>
    static readonly string[] AllClipNames =
    [
        "idle", "fidget_tilt", "fidget_chest", "fidget_lean", "walk_right", "walk_left", "wave", "think", "shrug",
        "waiting", "jump", "held", "land", "failed", "working", "review", "doze_off", "sleeping", "wake",
        "evolve", "flex", "leap", "happy", "thrown", "float",
    ];

    [STAThread]
    static int Main(string[] args)
    {
        // --selftest runs before the single-instance check so it works while
        // the pet is already on the desktop: load, build at every scale the
        // app can use, resolve every clip, log, exit.
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest();

        // --friend ID: company for the main pet, in its own process (own settings,
        // log and single-instance lock; leaves when --parent PID exits).
        string? friend = ArgValue(args, "--friend") is string f && Atlas.IsBuiltIn(f) ? f : null;
        int? parent = int.TryParse(ArgValue(args, "--parent"), out int pp) ? pp : null;
        if (friend is not null && parent is null)
            friend = null;

        using var mutex = new Mutex(true, friend is null ? @"Local\DesktopPet-6f1c2d9e" : $@"Local\DesktopPet-6f1c2d9e-friend-{friend}", out bool first);
        if (!first)
            return 0;

        if (friend is not null)
            Settings.FileName = $"settings.{friend}.json";
        Log.Init(friend is null ? "pet" : $"pet.{friend}");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("ui thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", e.ExceptionObject as Exception);
        ApplicationConfiguration.Initialize();

        bool demo = args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        Log.Info($"start {typeof(Program).Assembly.GetName().Version} demo={demo}");
        try
        {
            var saved = Settings.Load();
            if (friend is not null)
            {
                // a friend is always that pet, and turns up next to whoever invited it
                saved.Pet = friend;
                int ai = Array.FindIndex(args, a => a.Equals("--near", StringComparison.OrdinalIgnoreCase));
                if (ai >= 0 && ai + 2 < args.Length && int.TryParse(args[ai + 1], out int nx) && int.TryParse(args[ai + 2], out int ny))
                    (saved.X, saved.Y) = (nx, ny);
                saved.Save();
            }
            string pet = saved.Pet;
            Atlas atlas;
            Icon icon;
            try
            {
                atlas = Atlas.Load(pet);
                try
                {
                    icon = Atlas.LoadIcon(pet, SystemInformation.SmallIconSize);
                }
                catch
                {
                    atlas.Dispose();
                    throw;
                }
            }
            catch (Exception ex) when (pet != "revbot")
            {
                // The saved choice stays as it is; RevBot stands in for this run.
                Log.Error($"pet '{pet}' failed to load, using RevBot", ex);
                pet = "revbot";
                atlas = Atlas.Load(pet);
                icon = Atlas.LoadIcon(pet, SystemInformation.SmallIconSize);
            }
            Application.Run(new PetContext(atlas, icon, demo, title: friend is null ? "Desktop Pet" : $"Desktop Pet: {Atlas.BuiltInPets.First(p => p.Id == friend).Name}",
                petId: pet, friendOf: friend is null ? null : parent));
        }
        catch (Exception ex)
        {
            Log.Error("fatal", ex);
            throw;
        }
        Log.Info("exit");
        return 0;
    }

    static string? ArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static int SelfTest()
    {
        Log.Init();
        try
        {
            foreach (var (id, name) in Atlas.BuiltInPets)
            {
                using var atlas = Atlas.Load(id);
                Atlas.LoadIcon(id, new Size(16, 16)).Dispose();
                foreach (double scale in new[] { 0.5, 0.625, 0.75, 0.8333, 1.0, 1.25, 1.5, 2.0 })
                {
                    var sprites = atlas.Build(scale * atlas.BaseScale);
                    if (sprites.Values.Any(s => s.W <= 0 || s.H <= 0 || s.Px.Length != s.W * s.H * 4))
                        throw new InvalidDataException($"{name}: bad sprite at scale {scale}");
                }
                var resolved = AllClipNames.Select(n => $"{n}->{atlas.Clip(n).Name}{(atlas.Has(n) ? "" : "*")}");
                Log.Info($"selftest ok: {name} look={atlas.HasLook} base={atlas.BaseScale} clips: {string.Join(' ', resolved)}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("selftest failed", ex);
            return 1;
        }
    }
}
