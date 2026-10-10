using System.Text.Json;

namespace DesktopPet;

/// <summary>
/// What the pet is doing right now, for his other bodies: the Claude Code
/// terminal mod, the PowerShell prompt, later a web page. Written as
/// <c>state.json</c> next to the exe (a friend pet writes <c>state.&lt;pet&gt;.json</c>),
/// atomically, only when something in it changes, and at most ten times a second.
/// Frame numbers are left out on purpose: each body animates on its own from
/// the clip name, so an idle pet costs no disk writes at all.
/// </summary>
sealed class StateFile
{
    /// <summary>One moment of the pet. Value semantics: an unchanged snapshot is not rewritten.</summary>
    public sealed record Snapshot(
        string Pet,          // "revbot", "damien"
        string Clip,         // the clip row playing: "idle", "working", "walk_right", ...
        string Mode,         // Pet.Mode: Idle, Walk, Action, Held, Falling, Asleep, Hold
        string? Hold,        // the held state (from Claude Code or the menu), or null
        string Claude,       // what Claude Code is doing across sessions: "idle", "review", "working", "waiting"
        int Sessions,        // Claude Code sessions he is tracking
        string? Saying,      // the speech bubble's current line, or null
        string? Reply,       // his chat's last complete answer, or null
        int ReplyCount,      // how many answers so far: a body waits for this to change
        string? Brain,       // who he talks with ("Claude", "llama3.1:8b"), or null
        string Where,        // which body shows him: "desktop", "terminal:<session>", ...
        string? Web,         // his page, e.g. "http://localhost:7474/", or null when off
        string Surfaces,     // every body that announced itself, comma separated
        int X,               // feet on screen, physical pixels
        int Y,
        bool Hidden);        // off screen (fullscreen app in front, or gone into another body)

    const double MinWriteGap = 0.1;

    readonly string path;
    Snapshot? written, pending;
    double lastWriteAt = double.NegativeInfinity;
    bool failed;

    public StateFile(string fileName = "state.json") => path = Path.Combine(AppContext.BaseDirectory, fileName);

    public string Path_ => path;

    /// <summary>Hand in the current moment; it is written now, or on the next tick if one just went out.</summary>
    public void Update(Snapshot s, double now)
    {
        if (s == written && pending is null)
            return;
        if (now - lastWriteAt < MinWriteGap)
        {
            pending = s;
            return;
        }
        pending = null;
        Write(s, now);
    }

    /// <summary>Every tick: flush a snapshot that waited for the write gap.</summary>
    public void Tick(double now)
    {
        if (pending is not null && now - lastWriteAt >= MinWriteGap)
        {
            var s = pending;
            pending = null;
            Write(s, now);
        }
    }

    /// <summary>On exit: the pet is gone, so nobody shows a stale state.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(path))
                File.WriteAllText(path, "{\"version\":1,\"running\":false}");
        }
        catch (Exception ex)
        {
            Log.Error("state file clear", ex);
        }
    }

    void Write(Snapshot s, double now)
    {
        lastWriteAt = now;
        written = s;
        try
        {
            var doc = new
            {
                version = 1,
                running = true,
                pet = s.Pet,
                clip = s.Clip,
                mode = s.Mode,
                hold = s.Hold,
                claude = s.Claude,
                sessions = s.Sessions,
                saying = s.Saying,
                reply = s.Reply,
                replyCount = s.ReplyCount,
                brain = s.Brain,
                where = s.Where,
                web = s.Web,
                surfaces = s.Surfaces.Length == 0 ? Array.Empty<string>() : s.Surfaces.Split(','),
                x = s.X,
                y = s.Y,
                hidden = s.Hidden,
                pid = Environment.ProcessId,
                updated = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz"),
            };
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc));
            File.Move(tmp, path, overwrite: true);
            failed = false;
        }
        catch (Exception ex)
        {
            if (!failed)
                Log.Error("state file write", ex); // once, not every tick
            failed = true;
        }
    }
}
