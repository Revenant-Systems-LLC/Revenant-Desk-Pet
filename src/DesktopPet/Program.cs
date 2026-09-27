namespace DesktopPet;

static class Program
{
    /// <summary>Every clip name the app ever asks for, used by --selftest.</summary>
    static readonly string[] AllClipNames =
    [
        "idle", "fidget_tilt", "fidget_chest", "fidget_lean", "walk_right", "walk_left", "wave", "think", "shrug",
        "waiting", "jump", "held", "land", "failed", "working", "review", "doze_off", "sleeping", "wake",
        "evolve", "flex", "leap", "happy",
    ];

    [STAThread]
    static int Main(string[] args)
    {
        // --selftest runs before the single-instance check so it works while
        // the pet is already on the desktop: load, build at every scale the
        // app can use, resolve every clip, log, exit.
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest();

        using var mutex = new Mutex(true, @"Local\DesktopPet-6f1c2d9e", out bool first);
        if (!first)
            return 0;

        Log.Init();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("ui thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", e.ExceptionObject as Exception);
        ApplicationConfiguration.Initialize();

        bool demo = args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        Log.Info($"start {typeof(Program).Assembly.GetName().Version} demo={demo}");
        try
        {
            var atlas = Atlas.Load();
            Application.Run(new PetContext(atlas, LoadTrayIcon(), demo));
        }
        catch (Exception ex)
        {
            Log.Error("fatal", ex);
            throw;
        }
        Log.Info("exit");
        return 0;
    }

    static Icon LoadTrayIcon()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("pet.ico")
            ?? throw new InvalidDataException("pet.ico is not embedded");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    static int SelfTest()
    {
        Log.Init();
        try
        {
            using var atlas = Atlas.Load();
            foreach (double scale in new[] { 0.5, 0.625, 0.75, 0.8333, 1.0, 1.25, 1.5, 2.0 })
            {
                var sprites = atlas.Build(scale * atlas.BaseScale);
                if (sprites.Values.Any(s => s.W <= 0 || s.H <= 0 || s.Px.Length != s.W * s.H * 4))
                    throw new InvalidDataException($"bad sprite at scale {scale}");
            }
            var resolved = AllClipNames.Select(n => $"{n}->{atlas.Clip(n).Name}{(atlas.Has(n) ? "" : "*")}");
            Log.Info($"selftest ok: look={atlas.HasLook} base={atlas.BaseScale} clips: {string.Join(' ', resolved)}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("selftest failed", ex);
            return 1;
        }
    }
}
