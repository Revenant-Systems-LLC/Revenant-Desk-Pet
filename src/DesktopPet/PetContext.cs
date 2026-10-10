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
    readonly Settings settings = Settings.Load();
    readonly string title;
    readonly Action<ContextMenuStrip, PetContext>? extendMenu;
    Atlas atlas;
    readonly Pet pet;
    readonly PetWindow window = new();
    readonly LayeredSurface surface = new();
    readonly ContextMenuStrip menu;
    readonly NotifyIcon tray;
    FrameClock? frameClock;      // ticks in step with the display (about 60 a second)
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

    // His other bodies (the Claude Code terminal mod, the PowerShell prompt, a web page later):
    // they announce themselves over the AI link and read state.json.
    readonly StateFile stateFile;
    sealed class Surface
    {
        public double Since, LastAt;
    }
    readonly Dictionary<string, Surface> surfaces = new();
    const double SurfaceQuietSeconds = 1800;   // a body silent this long has gone without saying so
    string? lastReply;                         // his chat's last complete answer, for the other bodies
    int replyCount;                            // bumps on every answer, so a body can tell a new one
    // Following him into another body (opt-in, settings.FollowSurfaces): when a terminal
    // body announces itself from the window in front, he walks over, hops in, and is off
    // the desk until that body goes or its window closes. Then he drops back out of it.
    string? goingTo;                           // the body he is walking toward
    IntPtr goingHwnd;
    double goingX, goInAt = double.PositiveInfinity;
    bool goingWalk;                            // the walk over has started
    int goingTries;                            // walks started so far (a ledge or a fidget can end one early)
    string? awayTo;                            // the body he is inside right now
    IntPtr awayHwnd;
    double nextAwayCheckAt;
    ToolStripMenuItem followItem = null!;

    Dictionary<(string Row, int Index), Sprite> sprites = new();
    double scale = -1;
    double buildingScale = -1;   // a background rebuild for this scale is running
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
    readonly int? friendOf;      // a friend pet: its inviter's process id
    double nextFriendCheckAt;
    double nextTerrainAt;
    ToolStripMenuItem terrainItem = null!;
    ToolStripMenuItem chaseItem = null!;
    ToolStripMenuItem talkSoundItem = null!;
    readonly SpeechBubble bubble = new();
    readonly ThinkingMath math = new();              // math floating round his head while Claude thinks
    readonly Dictionary<string, double> thinkingSince = new(); // sessions thinking over a prompt, since when
    bool chatWaiting;                                // his own Claude hasn't started answering yet
    double lastAskAt = double.NegativeInfinity;
    // Talking to the pet's own Claude (main pet only)
    IChatBrain? chat;                                // who the pet talks with (settings.ChatBrain)
    ClaudeChat? Claude => chat as ClaudeChat;
    SystemScan.Result? scan;                         // what this PC can run, for the Brain menu
    bool scanning;
    ToolStripMenuItem brainMenu = null!;
    ChatBox? chatBox;
    PermissionPrompt? prompt;                          // his Claude wants to change something: Yes / No
    readonly Queue<ClaudeChat.Ask> asks = new();
    double dropSayUntil = double.NegativeInfinity; // his own Claude just finished: its Stop hook's "say" repeats the streamed reply

    // Watching you type: only "a key was pressed recently" (the cursor didn't
    // move, yet there was input) and which program is in front. Never which keys.
    static readonly HashSet<string> TypingApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "pwsh", "powershell", "wezterm-gui", "alacritty", "mintty",
        "claude", "Claude", "Code", "Cursor",
    };
    double nextTypingCheckAt, typingUntil;
    Native.POINT lastTypingCursor;
    uint foregroundPid;
    bool foregroundIsTypingApp, watchingTyping;
    int lastX, lastY;

    /// <param name="atlas">The pet to show; the context owns and disposes it.</param>
    /// <param name="trayIcon">Tray icon; owned and disposed by the context.</param>
    /// <param name="extendMenu">Adds extra items to the pet's menu (optional).</param>
    /// <param name="petId">Which built-in pet <paramref name="atlas"/> is.</param>
    /// <param name="friendOf">For a friend pet: the process id of the pet that invited it (it leaves with it).</param>
    public PetContext(Atlas atlas, Icon trayIcon, bool demoMode, string title = "Desktop Pet",
        Action<ContextMenuStrip, PetContext>? extendMenu = null, string petId = "revbot", int? friendOf = null)
    {
        this.friendOf = friendOf;
        this.atlas = atlas;
        this.petId = petId;
        stateFile = new StateFile(friendOf is null ? "state.json" : $"state.{petId}.json");
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
        terrain.Refresh();
        if (settings.Terrain)
            pet.Terrain = terrain;

        if (demoMode)
            demo = new DemoRunner(pet, atlas);
        else
            pet.Play("wave");

        // Only the main pet listens to Claude Code; a friend is just company.
        if (friendOf is null)
        {
            try
            {
                aiLink = new AiLink(OnAiLine);
            }
            catch (Exception ex)
            {
                Log.Error("ai link start", ex); // the pet works without it
            }
            UseBrain(settings.ChatBrain, announce: false);
            Rescan();
        }

        frameClock = new FrameClock(window, () => OnTick(null, EventArgs.Empty));
    }

    // ------------------------------------------------------------ loop

    void OnTick(object? sender, EventArgs e)
    {
        try
        {
            double t = clock.Elapsed.TotalSeconds;
            long tickStart = Perf.Now, p;
            if (lastT > 0)
                Perf.Add("gap", (t - lastT) * 1000);
            double dt = Math.Clamp(t - lastT, 0, 0.1);
            lastT = t;

            if (awayTo is not null)
            {
                TickAway(t);
                stateFile.Update(Snapshot(), t);
                stateFile.Tick(t);
                return;
            }
            if (t >= nextFullscreenAt)
            {
                nextFullscreenAt = t + 1.0;
                p = Perf.Now;
                CheckFullscreen();
                Perf.Since("fullscreen", p);
            }
            stateFile.Update(Snapshot(), t);
            stateFile.Tick(t);
            if (hidden)
                return;
            TickFollow(t);

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
                p = Perf.Now;
                RefreshMonitor(force: false);
                Perf.Since("monitor", p);
            }

            if (t >= nextTopmostAt && !menu.Visible)
            {
                nextTopmostAt = t + 3.0;
                p = Perf.Now;
                Native.SetWindowPos(window.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                math.KeepOnTop();
                Perf.Since("topmost", p);
            }

            if (terrain is not null && t >= nextTerrainAt)
            {
                nextTerrainAt = t + 0.5; // windows to climb, and other pets
                p = Perf.Now;
                terrain.Refresh();
                pet.Friend = NearestFriend();
                Perf.Since("terrain", p);
            }
            if (friendOf is int parent && t >= nextFriendCheckAt)
            {
                nextFriendCheckAt = t + 2;
                if (!ProcessAlive(parent))
                {
                    Log.Info("the pet that invited me has gone; going home");
                    Quit();
                    return;
                }
            }

            Native.GetCursorPos(out var cursor);
            if (t >= nextTypingCheckAt)
            {
                nextTypingCheckAt = t + 0.25;
                p = Perf.Now;
                CheckTyping(t, cursor);
                Perf.Since("typing", p);
            }
            demo?.Tick(t);
            TickAiLink(t);
            chat?.Tick();
            p = Perf.Now;
            pet.Tick(dt, cursor.X, cursor.Y, Native.IdleMilliseconds());
            Perf.Since("pet", p);
            if (!pet.Work.SameAs(petWorkSeen))
            {
                petWorkSeen = pet.Work;
                RefreshMonitor(force: false); // flew onto another monitor: its DPI now
            }
            p = Perf.Now;
            Render();
            Perf.Since("render", p);
            p = Perf.Now;
            TickBubble(t);
            Perf.Since("bubble", p);
            savedX = (int)Math.Round(pet.X);
            savedY = (int)Math.Round(pet.GroundY);
            Perf.Since("tick", tickStart);
            Perf.Report(t);
        }
        catch (Exception ex)
        {
            Log.Error("tick", ex);
        }
    }

    /// <summary>Open the text box over his head (double-click, or the menu), focused so typing and dictation land in it.</summary>
    void OpenChat()
    {
        if (chat is null || quitting)
            return;
        if (chatBox is { IsDisposed: false })
        {
            chatBox.Activate();
            return;
        }
        var box = new ChatBox(scale, chat.LastReply, chat.Name);
        box.Submitted += text =>
        {
            chatWaiting = true;
            chat.Send(text);
            pet.Play("think");
        };
        box.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(chatBox, box))
                chatBox = null;
            box.Dispose();
        };
        chatBox = box;
        if (lastSprite is Sprite s)
            box.Place(lastX + (int)Math.Round(s.W * 0.5), lastY, pet.Work);
        box.Show();
        chat.Warm(); // start his Claude now so it's ready when you press Enter
    }

    // ------------------------------------------------------------ brains

    /// <summary>Talk with this brain from now on ("claude", "claude:opus", "ollama:llama3.1:8b").</summary>
    void UseBrain(string spec, bool announce = true)
    {
        chatBox?.Close();
        asks.Clear();
        prompt?.Close();
        chat?.Dispose();
        chatWaiting = false;
        IChatBrain next = spec.StartsWith("ollama:", StringComparison.Ordinal)
            ? new OllamaChat(window, spec["ollama:".Length..])
            : new ClaudeChat(window, settings, spec.StartsWith("claude:", StringComparison.Ordinal) ? spec["claude:".Length..] : null);
        next.ReplyGrowing += text =>
        {
            chatWaiting = false;
            bubble.Sound = settings.TalkSounds;
            bubble.Stream(text, done: false);
        };
        next.ReplyDone += (text, _) =>
        {
            chatWaiting = false;
            lastReply = string.IsNullOrWhiteSpace(text) ? null : text;
            replyCount++;
            bubble.Stream(text, done: true);
        };
        if (next is ClaudeChat claude)
        {
            claude.PermissionAsked += ask =>
            {
                asks.Enqueue(ask);
                ShowNextAsk();
            };
            claude.AsksCleared += () =>
            {
                asks.Clear();
                prompt?.Close();
            };
        }
        chat = next;
        if (settings.ChatBrain != spec)
        {
            settings.ChatBrain = spec;
            settings.Save();
        }
        Log.Info($"brain: {next.Name}");
        if (announce)
            Say($"I'm thinking with {next.Name} now.");
    }

    /// <summary>Look at this PC again (graphics card, memory, Ollama's models), off the UI thread.</summary>
    void Rescan()
    {
        if (scanning || quitting)
            return;
        scanning = true;
        Task.Run(SystemScan.Run).ContinueWith(t =>
        {
            try
            {
                window.BeginInvoke(() =>
                {
                    scanning = false;
                    if (t.IsCompletedSuccessfully)
                    {
                        scan = t.Result;
                        Log.Info($"scan: {scan.Summary}; Ollama {(scan.OllamaRunning ? $"running, {scan.Models.Count} models" : "not running")}" +
                                 (scan.Recommended is { } best ? $"; suggests {best.Name}" : ""));
                    }
                    else
                        Log.Error("system scan", t.Exception);
                });
            }
            catch (Exception)
            {
                // closing
            }
        });
    }

    /// <summary>Filled each time it opens, from the latest scan.</summary>
    void FillBrainMenu()
    {
        if (scan is null || (DateTime.Now - scan.When).TotalSeconds > 20)
            Rescan();
        var items = brainMenu.DropDownItems;
        items.Clear();
        string current = settings.ChatBrain;
        void Add(ToolStripItemCollection to, string label, string spec)
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => UseBrain(spec)) { Checked = current == spec };
            to.Add(item);
        }
        Add(items, "Claude (your default)", "claude");
        Add(items, "Claude Sonnet", "claude:sonnet");
        Add(items, "Claude Opus", "claude:opus");
        Add(items, "Claude Haiku", "claude:haiku");
        items.Add(new ToolStripSeparator());
        if (scan is not { } r)
        {
            items.Add(new ToolStripMenuItem("Looking at your PC...") { Enabled = false });
            return;
        }
        items.Add(new ToolStripMenuItem("Your PC: " + r.Summary) { Enabled = false });
        if (!r.OllamaRunning)
        {
            items.Add(OllamaChat.Installed
                ? new ToolStripMenuItem("Start Ollama and list its models", null, async (_, _) =>
                {
                    try
                    {
                        await OllamaChat.EnsureServer();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("start ollama", ex);
                    }
                    Rescan();
                })
                : new ToolStripMenuItem("Local models need Ollama (ollama.com)") { Enabled = false });
            return;
        }
        static string Gb(SystemScan.Model m) => m.Cloud ? "cloud" : $"{m.Bytes / 1e9:0.#} GB";
        if (r.Recommended is { } best)
            Add(items, $"Suggested for your PC: {best.Name} ({Gb(best)})", "ollama:" + best.Name);
        var chat = r.Models.Where(m => !m.Embedding).ToList();
        foreach (var (fit, label) in new[]
        {
            (SystemScan.Fit.Gpu, "Fits your graphics card"),
            (SystemScan.Fit.Cloud, "Runs on Ollama's servers (no graphics card)"),
            (SystemScan.Fit.TooBig, "Too big for your graphics card (slow)"),
        })
        {
            var group = chat.Where(m => r.FitOf(m) == fit).OrderByDescending(m => m.ParamsB).ToList();
            if (group.Count == 0)
                continue;
            var sub = new ToolStripMenuItem($"{label} ({group.Count})");
            foreach (var m in group)
                Add(sub.DropDownItems, $"{m.Name} ({Gb(m)})", "ollama:" + m.Name);
            sub.Checked = group.Any(m => current == "ollama:" + m.Name);
            items.Add(sub);
        }
        items.Add(new ToolStripMenuItem("Scan my PC again", null, (_, _) => Rescan()));
    }

    /// <summary>One permission request at a time, over his head, until it's answered.</summary>
    void ShowNextAsk()
    {
        if (prompt is not null || asks.Count == 0 || chat is null || quitting)
            return;
        var ask = asks.Dequeue();
        var box = new PermissionPrompt(scale, ask.Question, ask.Detail);
        box.Answered += yes =>
        {
            Claude?.Answer(ask.Id, yes);
            Log.Info($"chat: {ask.Tool} {(yes ? "allowed" : "refused")}");
            pet.Play(yes ? "happy" : "shrug");
        };
        box.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(prompt, box))
                prompt = null;
            box.Dispose();
            ShowNextAsk();
        };
        prompt = box;
        if (lastSprite is Sprite s)
            box.Place(lastX + (int)Math.Round(s.W * 0.5), lastY, pet.Work);
        if (!hidden)
            box.Show();
    }

    /// <summary>Say something in a speech bubble above the pet (upper right), typed out a letter at a time.</summary>
    public void Say(string text)
    {
        bubble.Sound = settings.TalkSounds;
        bubble.Say(text);
        Log.Info($"says something ({text.Length} characters)"); // never the words themselves
    }

    /// <summary>Claude Code is waiting on a permission prompt: say so (not more than once every 45 s).</summary>
    void AskForOk()
    {
        double t = clock.Elapsed.TotalSeconds;
        if (t - lastAskAt < 45)
            return;
        lastAskAt = t;
        Say("Hey Dave, I need your OK on something.");
    }

    void SayHello()
    {
        string name = Atlas.BuiltInPets.FirstOrDefault(p => p.Id == petId).Name ?? "RevBot";
        Say($"Hi! I'm {name}. Right-click me to see everything I can do.");
    }

    /// <summary>Keep the bubble typing and pointed at the pet's head (upper right of the current frame).</summary>
    void TickBubble(double t)
    {
        if (lastSprite is not Sprite s)
            return;
        int headX = lastX + (int)Math.Round(s.W * 0.78), headY = lastY + (int)Math.Round(s.H * 0.06);
        bubble.Tick(t, !hidden && chatBox is null && prompt is null, headX, headY, scale, pet.Work);
        foreach (var gone in thinkingSince.Where(kv => t - kv.Value > 90).Select(kv => kv.Key).ToList())
            thinkingSince.Remove(gone); // no tool call for a minute and a half: stop waiting for one
        bool thinking = pet.Frame.Row == "think" || thinkingSince.Count > 0 || chatWaiting;
        math.Tick(t, thinking, !hidden, lastX + s.W / 2, lastY + (int)Math.Round(s.H * 0.28), scale);
    }

    void Render()
    {
        if (!sprites.TryGetValue(pet.Frame, out var s))
            return;
        var (sx, sy, deg) = pet.Pose;
        if (sx != 1 || sy != 1 || deg != 0)
            s = Atlas.Posed(s, sx, sy, deg);
        var (dx, dy) = pet.PoseShift;
        int x = (int)Math.Round(pet.X + dx) + s.X0;
        int y = (int)Math.Round(pet.AnchorY + dy) + s.Y0;
        if (ReferenceEquals(s, lastSprite) && x == lastX && y == lastY)
            return;
        if (ReferenceEquals(s, lastSprite))
            surface.Move(window.Handle, x, y); // same picture, new place: no need to send the pixels again
        else
            surface.Present(window.Handle, s, x, y);
        Perf.Presented();
        lastSprite = s;
        lastX = x;
        lastY = y;
    }

    /// <summary>
    /// Rebuild the frames for a new scale on a worker thread (a few hundred ms for
    /// the whole atlas) and swap them in when done; he keeps moving meanwhile, at
    /// the old size.
    /// </summary>
    void BuildInBackground(double newScale)
    {
        buildingScale = newScale;
        var a = atlas;
        Task.Run(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                Dictionary<(string Row, int Index), Sprite> built;
                lock (a) // one thread at a time on the sheet bitmap
                    built = a.Build(newScale);
                long ms = sw.ElapsedMilliseconds;
                window.BeginInvoke(() =>
                {
                    if (buildingScale == newScale)
                        buildingScale = -1;
                    if (quitting || !ReferenceEquals(a, atlas))
                        return; // switched pets meanwhile
                    sprites = built;
                    scale = newScale;
                    pet.Scale = newScale;
                    ForceRedraw();
                    Log.Info($"scale {newScale:0.###}: {built.Count} frames in {ms} ms (in the background)");
                });
            }
            catch (Exception ex)
            {
                Log.Error("background frame build", ex); // e.g. the pet was switched and its sheet disposed
            }
        });
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
    /// direct commands are "say TEXT", "talk" (open the chat box), "ask TEXT" (to his own Claude), "state NAME", "play NAME", "pet ID", "fling VX VY", "chase [on|off]", "invite ID", "sendhome", "jumpup", "ledges", "clear" and "ping".
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
            case "surface" when arg is not null:
                OnSurface(arg.ToLowerInvariant(), parts.Length > 2 ? parts[2].ToLowerInvariant() : "on", kv);
                break;
            case "follow":
                settings.FollowSurfaces = arg?.ToLowerInvariant() switch { "on" => true, "off" => false, _ => !settings.FollowSurfaces };
                settings.Save();
                Log.Info($"follow into terminals: {(settings.FollowSurfaces ? "on" : "off")}");
                break;
            case "comeback":
                if (awayTo is not null)
                    ComeBack();
                break;
            case "talk":
                OpenChat();
                break;
            case "brain" when arg is not null:
                UseBrain(arg);
                break;
            case "scan":
                Rescan();
                break;
            case "climb":
                if (!pet.ClimbUp())
                    Log.Info("nothing to climb up onto");
                break;
            case "ask" when line.Length > 4 && chat is not null:
                chatWaiting = true;
                chat.Send(line[(line.IndexOf(' ') + 1)..]); // as if typed into the chat box
                pet.Play("think");
                break;
            case "say" when line.Length > 4:
                if (clock.Elapsed.TotalSeconds < dropSayUntil)
                {
                    dropSayUntil = double.NegativeInfinity; // already said, as it streamed
                    break;
                }
                Say(line[(line.IndexOf(' ') + 1)..]);
                break;
            case "chase":
                pet.SetChasing(arg?.ToLowerInvariant() switch { "on" => true, "off" => false, _ => !pet.Chasing });
                break;
            case "invite" when arg is not null && friendOf is null:
                if (Atlas.IsBuiltIn(arg.ToLowerInvariant()) && arg.ToLowerInvariant() != petId)
                    InviteFriend(arg.ToLowerInvariant());
                break;
            case "sendhome":
                SendFriendsHome();
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

    /// <summary>
    /// Another body of his came or went: "surface terminal on session=ID",
    /// "surface powershell on pid=N", "surface terminal off session=ID". A body
    /// repeats "on" now and then as a heartbeat. He greets a new one.
    /// </summary>
    void OnSurface(string name, string onOff, Dictionary<string, string> kv)
    {
        string id = kv.GetValueOrDefault("session") ?? kv.GetValueOrDefault("pid") ?? "default";
        string key = $"{name}:{new string(id.Where(char.IsAsciiLetterOrDigit).ToArray())}";
        double t = clock.Elapsed.TotalSeconds;
        if (onOff == "off")
        {
            if (surfaces.Remove(key))
                Log.Info($"surface gone: {key} ({surfaces.Count} left)");
            if (awayTo == key)
                ComeBack();
            if (goingTo == key)
                StopFollowing();
            return;
        }
        if (surfaces.TryGetValue(key, out var s))
        {
            s.LastAt = t;
            return;
        }
        surfaces[key] = new Surface { Since = t, LastAt = t };
        Log.Info($"surface here: {key} ({surfaces.Count} now)");
        if (hidden || awayTo is not null)
            return;
        if (settings.FollowSurfaces && friendOf is null && name == "terminal" && goingTo is null && StartFollowing(key))
            return;
        pet.Play("wave"); // he sees himself in a new place
    }

    /// <summary>
    /// A terminal body just appeared: if the window in front is a terminal on his
    /// monitor, walk to it. (He goes in once he gets there; see <see cref="TickFollow"/>.)
    /// </summary>
    bool StartFollowing(string key)
    {
        if (pet.Mode is Mode.Held or Mode.Falling)
            return false;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == window.Handle)
            return false;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (!TypingApps.Contains(ProcessName(pid)))
            return false;
        if (Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST) != monitor || !Native.GetWindowRect(fg, out var r))
            return false;
        double m = 90 * scale;
        goingTo = key;
        goingHwnd = fg;
        goingX = Math.Clamp((r.Left + r.Right) / 2.0, pet.Work.Left + m, Math.Max(pet.Work.Left + m, pet.Work.Right - m));
        goInAt = double.PositiveInfinity;
        goingWalk = false;
        goingTries = 0;
        pet.KeepAwake = true;      // a body turning up is worth staying up for
        pet.Terrain = null;        // straight along the ground, no hopping onto windows on the way
        pet.SetState(null);
        if (pet.Mode == Mode.Asleep)
            pet.Poke();            // wakes him; the walk starts once he is up (TickFollow)
        Log.Info($"following into {key}");
        return true;
    }

    void StopFollowing()
    {
        goingTo = null;
        goingWalk = false;
        goInAt = double.PositiveInfinity;
        pet.KeepAwake = false;
        pet.Terrain = settings.Terrain ? terrain : null;
        ApplyAiHold(immediate: true); // whatever Claude is doing shows again
    }

    void TickFollow(double t)
    {
        if (goingTo is null)
            return;
        if (!surfaces.ContainsKey(goingTo) || !Native.IsWindow(goingHwnd))
        {
            StopFollowing();
            return;
        }
        if (goInAt <= t)
        {
            GoIn();
            return;
        }
        if (!double.IsPositiveInfinity(goInAt) || pet.Mode != Mode.Idle)
            return; // waking up, walking, or already jumping in
        if (Math.Abs(pet.X - goingX) < 60 * scale)
        {
            pet.Play("jump");
            goInAt = t + 0.45; // mid-jump: gone
            return;
        }
        if (goingTries < 4)
        {
            // Not there yet (or a walk ended early): walk, or walk again.
            goingTries++;
            goingWalk = true;
            pet.WalkBy((goingX - pet.X) / Math.Max(pet.Scale, 0.01)); // WalkBy takes scale-1 pixels
            return;
        }
        Log.Info("could not get to the terminal; staying out");
        StopFollowing();
        pet.Play("shrug");
    }

    void GoIn()
    {
        awayTo = goingTo;
        awayHwnd = goingHwnd;
        StopFollowing();
        if (dragging)
            FinishDrag();
        pressed = false;
        window.Hide();
        bubble.Hide();
        math.Hide();
        prompt?.Hide();
        if (frameClock is not null)
            frameClock.Slow = true;
        nextAwayCheckAt = clock.Elapsed.TotalSeconds + 1;
        Log.Info($"went into {awayTo}");
    }

    /// <summary>While he is inside another body: the links keep running; he comes back when it goes.</summary>
    void TickAway(double t)
    {
        TickAiLink(t);
        chat?.Tick();
        if (t < nextAwayCheckAt)
            return;
        nextAwayCheckAt = t + 1;
        if (awayTo is not null && (!surfaces.ContainsKey(awayTo) || !Native.IsWindow(awayHwnd)))
            ComeBack();
    }

    /// <summary>Back onto the desk: he drops out of the bottom of the window he was in.</summary>
    void ComeBack()
    {
        string from = awayTo ?? "?";
        IntPtr h = awayHwnd;
        awayTo = null;
        awayHwnd = IntPtr.Zero;
        double x = pet.X;
        double height = 180 * Math.Max(scale, 0.5);
        if (h != IntPtr.Zero && Native.IsWindow(h) && Native.GetWindowRect(h, out var r))
        {
            x = (r.Left + r.Right) / 2.0;
            height = Math.Clamp(pet.GroundY - r.Bottom, 60 * scale, 700 * scale);
        }
        window.Show();
        if (frameClock is not null)
            frameClock.Slow = false;
        ForceRedraw();
        prompt?.Show();
        pet.DropIn(x, pet.Work, height);
        RefreshMonitor(force: false);
        Log.Info($"came back from {from}");
    }

    /// <summary>The moment for state.json: what his other bodies need to show the same pet.</summary>
    StateFile.Snapshot Snapshot() => new(
        Pet: petId,
        Clip: pet.Frame.Row,
        Mode: pet.Mode.ToString(),
        Hold: pet.HeldState,
        Claude: AggregateHold() ?? "idle",
        Sessions: aiSessions.Count,
        Saying: bubble.Saying,
        Reply: lastReply,
        ReplyCount: replyCount,
        Brain: chat?.Name,
        Where: awayTo ?? "desktop",
        Surfaces: string.Join(',', surfaces.Keys.Order()),
        X: (int)Math.Round(pet.X),
        Y: (int)Math.Round(pet.GroundY),
        Hidden: hidden || awayTo is not null);

    void OnHook(string hookEvent, string session, string? tool, string? type)
    {
        bool immediate = true;
        if (hookEvent is not ("UserPromptSubmit" or "Notification" or "PermissionRequest"))
            thinkingSince.Remove(session); // it's doing something now (or done)
        switch (hookEvent)
        {
            case "SessionStart":
                pet.Play("wave");
                return;
            case "UserPromptSubmit":
                SessionHold(session, "review");            // reading what you asked, thinking it over
                thinkingSince[session] = clock.Elapsed.TotalSeconds; // math round his head till the first tool call
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
                if (session != Claude?.SessionId)
                    AskForOk();                            // his own Claude's request is already up on screen
                break;
            case "Notification" when type == "permission_prompt":
                SessionHold(session, "waiting");
                if (session != Claude?.SessionId)
                    AskForOk();
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
                if (Claude?.SessionId == session)
                    dropSayUntil = clock.Elapsed.TotalSeconds + 3;
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
        if (goingTo is not null)
        {
            // On his way into a terminal: no held pose until he is in (or gave up); the state is reapplied then.
            if (aiHolding)
                pet.SetState(null);
            aiHolding = false;
            pendingAiHold = null;
            return;
        }
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
        if (aiHolding && pet.HeldState is null && goingTo is null)
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
        foreach (var key in surfaces.Where(kv => t - kv.Value.LastAt > SurfaceQuietSeconds).Select(kv => kv.Key).ToList())
        {
            surfaces.Remove(key);
            Log.Info($"surface quiet, stood down: {key}");
        }
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

    /// <summary>
    /// Typing into a terminal or Claude right now? Then point the pet at that
    /// window. A keystroke is inferred from "recent input, but the mouse didn't
    /// move"; what was typed is never read.
    /// </summary>
    void CheckTyping(double t, Native.POINT cursor)
    {
        bool mouseStill = cursor.X == lastTypingCursor.X && cursor.Y == lastTypingCursor.Y;
        lastTypingCursor = cursor;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != window.Handle)
        {
            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (pid != foregroundPid)
            {
                foregroundPid = pid;
                foregroundIsTypingApp = TypingApps.Contains(ProcessName(pid));
            }
        }
        else
            foregroundIsTypingApp = false;

        if (foregroundIsTypingApp && mouseStill && Native.IdleMilliseconds() < 400)
            typingUntil = t + 2.5; // keeps watching a little after the last keystroke
        Native.RECT r = default;
        bool watching = foregroundIsTypingApp && t < typingUntil && Native.GetWindowRect(fg, out r);
        pet.Attention = watching ? ((r.Left + r.Right) / 2.0, r.Bottom - 80) : null; // input boxes sit near the bottom
        if (watching != watchingTyping)
        {
            watchingTyping = watching;
            Log.Info(watching ? "watching you type" : "stopped watching");
        }
    }

    /// <summary>The closest other desk pet on this pet's monitor.</summary>
    (double X, double Feet)? NearestFriend()
    {
        if (terrain is null)
            return null;
        (double X, double Feet)? best = null;
        double bestD = double.MaxValue;
        var w = pet.Work;
        foreach (var (_, _, r) in terrain.OtherPets)
        {
            double x = (r.Left + r.Right) / 2.0, feet = r.Bottom;
            if (x < w.Left || x > w.Right || feet < w.Top || feet > w.Bottom + 4)
                continue;
            double d = Math.Abs(x - pet.X);
            if (d < bestD)
                (best, bestD) = ((x, feet), d);
        }
        return best;
    }

    static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Start another built-in pet as company, in its own process, next to this one.</summary>
    void InviteFriend(string id)
    {
        try
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DesktopPet.exe");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (string a in new[] { "--friend", id, "--parent", Environment.ProcessId.ToString(),
                "--near", ((int)Math.Round(pet.X + 220 * pet.Scale)).ToString(), pet.Work.Bottom.ToString() })
                psi.ArgumentList.Add(a);
            using var _ = Process.Start(psi);
            Log.Info($"invited {id}");
        }
        catch (Exception ex)
        {
            Log.Error($"invite {id}", ex);
        }
    }

    /// <summary>Close the friend pets this one invited (only DesktopPet processes: never anything else).</summary>
    void SendFriendsHome()
    {
        terrain?.Refresh();
        string me = Process.GetCurrentProcess().ProcessName;
        foreach (var (hwnd, pid, _) in terrain?.OtherPets ?? [])
        {
            if (ProcessName(pid) == me)
                Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    static string ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
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
        if (force)
        {
            var sw = Stopwatch.StartNew();
            lock (atlas)
                sprites = atlas.Build(newScale);
            scale = newScale;
            pet.Scale = newScale;
            ForceRedraw();
            Log.Info($"scale {newScale:0.###}: {sprites.Count} frames in {sw.ElapsedMilliseconds} ms");
        }
        else if (Math.Abs(newScale - scale) > 1e-6 && Math.Abs(newScale - buildingScale) > 1e-6)
            BuildInBackground(newScale); // onto a monitor with other scaling: don't freeze him while it builds
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
            if (frameClock is not null)
                frameClock.Slow = true;
            bubble.Hide();
            math.Hide();
            prompt?.Hide(); // waits, unanswered, until he's back
            Log.Info("hidden: fullscreen app in front");
        }
        else if (!fullscreen && hidden)
        {
            hidden = false;
            window.Show();
            prompt?.Show();
            ForceRedraw();
            if (frameClock is not null)
                frameClock.Slow = false;
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
        if (e.Clicks >= 2 && chat is not null)
        {
            pressed = dragging = false; // the second click of a double-click: talk, don't poke again
            OpenChat();
            return;
        }
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
            pet.BeginDrag(-grabDx, -grabDy); // he swings from where you grabbed him
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
        followItem = new ToolStripMenuItem("Follow me into the terminal", null, (_, _) =>
        {
            settings.FollowSurfaces = !settings.FollowSurfaces;
            settings.Save();
            if (!settings.FollowSurfaces && awayTo is not null)
                ComeBack();
        });
        talkSoundItem = new ToolStripMenuItem("Talking sounds", null, (_, _) =>
        {
            settings.TalkSounds = !settings.TalkSounds;
            bubble.Sound = settings.TalkSounds;
            settings.Save();
        });

        var friendsMenu = new ToolStripMenuItem("Friends");
        foreach (var (id, name) in Atlas.BuiltInPets)
            friendsMenu.DropDownItems.Add(new ToolStripMenuItem($"Invite {name}", null, (_, _) => InviteFriend(id)) { Tag = id });
        friendsMenu.DropDownItems.Add(new ToolStripSeparator());
        friendsMenu.DropDownItems.Add("Send friends home", null, (_, _) => SendFriendsHome());

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
            friendsMenu,
            chaseItem,
            talkSoundItem,
            stayPutItem,
            terrainItem,
            followItem,
            new ToolStripMenuItem("Come here", null, (_, _) => ComeHere()),
        });
        if (friendOf is null)
        {
            brainMenu = new ToolStripMenuItem("Brain");
            brainMenu.DropDownItems.Add(new ToolStripMenuItem("...") { Enabled = false }); // so it shows as a submenu
            brainMenu.DropDownOpening += (_, _) => FillBrainMenu();
            m.Items.Insert(2, new ToolStripMenuItem("Talk...", null, (_, _) => window.BeginInvoke(OpenChat)));
            m.Items.Insert(3, brainMenu);
        }
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
            followItem.Checked = settings.FollowSurfaces;
            chaseItem.Checked = pet.Chasing;
            talkSoundItem.Checked = settings.TalkSounds;
            foreach (var (item, value) in sizeItems)
                item.Checked = Math.Abs(value - settings.Size) < 1e-6;
            FillActionMenus(actions, states);
            foreach (ToolStripMenuItem item in petItems.DropDownItems)
                item.Checked = (string?)item.Tag == petId;
            // a friend is one fixed pet and invites nobody; the main pet invites anyone but itself
            petMenu.Visible = friendsMenu.Visible = friendOf is null;
            foreach (var item in friendsMenu.DropDownItems.OfType<ToolStripMenuItem>())
                if (item.Tag is string fid)
                    item.Visible = fid != petId;
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
        if (actions.DropDownItems.Count > 0)
            actions.DropDownItems.Add(new ToolStripSeparator());
        actions.DropDownItems.Add("Say hello", null, (_, _) => SayHello());
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
        Dictionary<(string Row, int Index), Sprite> built;
        lock (next)
            built = next.Build(newScale);

        var old = atlas;
        atlas = next;
        pet.ReplaceAtlas(next);
        sprites = built;
        scale = newScale;
        pet.Scale = newScale;
        ForceRedraw();
        lock (old) // a background rebuild may still be reading it
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
        RefreshMonitor(force: false); // rebuilds at the new size in the background
    }

    void ComeHere()
    {
        if (awayTo is not null)
        {
            ComeBack();
            return;
        }
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
        stateFile.Clear();

        frameClock?.Dispose();
        chatBox?.Close();
        prompt?.Close();
        chat?.Dispose();
        OllamaChat.StopOurServer();
        math.Dispose();
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
        bubble.Dispose();
        surface.Dispose();
        atlas.Dispose();
        ExitThread();
    }
}
