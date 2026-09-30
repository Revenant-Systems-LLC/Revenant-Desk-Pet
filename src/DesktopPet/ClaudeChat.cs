using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DesktopPet;

/// <summary>
/// The pet's own Claude Code conversation: one background `claude -p` process
/// that reads messages as stream-json on stdin and streams its replies as
/// stream-json on stdout. It runs on the user's Claude Code login (no API key)
/// and carries on the same conversation across restarts (--resume).
///
/// Version one only reads: its built-in tools are Read, Grep, Glob, WebSearch
/// and WebFetch, no MCP servers, and anything that would ask for permission is
/// refused. Nothing said either way is ever logged, only lengths.
/// Events are raised on the UI thread.
/// </summary>
sealed class ClaudeChat : IDisposable
{
    const string Tools = "Read,Grep,Glob,WebSearch,WebFetch";
    const double IdleSeconds = 600; // no messages this long: let the process go (it resumes next time)

    const string Prompt =
        "You are speaking through RevBot, a small robot desktop pet on the user's screen. " +
        "Your replies appear in a comic speech bubble above his head and type out a letter at a time, " +
        "so keep every reply short: one to three plain sentences. No markdown, no lists, no headings, " +
        "no code blocks, no emojis, no em dashes. If more detail would help, give the gist and offer the rest. " +
        "In this mode you can only read (files, search, the web); you cannot edit, create, move or delete anything " +
        "or run commands. If asked for a change, say briefly what you would do and that editing through the pet isn't switched on yet.";

    readonly Control ui;
    readonly Settings settings;
    Process? proc;
    StreamWriter? stdin;
    bool gotInit, resumed;
    string? pending;          // sent before the process was ready, or to resend after a failed resume
    readonly StringBuilder reply = new();
    double lastUsed;
    readonly Stopwatch clock = Stopwatch.StartNew();

    /// <summary>The reply so far (all of its text), as it arrives.</summary>
    public event Action<string>? ReplyGrowing;
    /// <summary>The reply is complete: its full text, and whether something went wrong.</summary>
    public event Action<string, bool>? ReplyDone;

    public string? SessionId { get; private set; }
    /// <summary>A message is on its way or being answered.</summary>
    public bool Busy { get; private set; }
    /// <summary>The last complete reply, for the chat box.</summary>
    public string LastReply { get; private set; } = "";

    public ClaudeChat(Control ui, Settings settings)
    {
        this.ui = ui;
        this.settings = settings;
        SessionId = settings.ChatSession;
    }

    /// <summary>Start the process now (the chat box opened), so it's ready by the time you press Enter.</summary>
    public void Warm()
    {
        try
        {
            EnsureStarted();
        }
        catch (Exception ex)
        {
            Log.Error("chat start", ex);
        }
    }

    public void Send(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;
        lastUsed = clock.Elapsed.TotalSeconds;
        Busy = true;
        reply.Clear();
        try
        {
            EnsureStarted();
            Write(text);
            Log.Info($"chat: sent {text.Length} characters");
        }
        catch (Exception ex)
        {
            Log.Error("chat send", ex);
            Finish("I couldn't reach Claude just now. Is Claude Code installed and logged in?", error: true);
        }
    }

    /// <summary>Every tick: let an idle process go.</summary>
    public void Tick()
    {
        if (proc is not null && !Busy && clock.Elapsed.TotalSeconds - lastUsed > IdleSeconds)
        {
            Log.Info("chat: idle, closing the process (it resumes next time)");
            Stop();
        }
    }

    void Write(string text)
    {
        if (!gotInit && resumed)
            pending = text; // resend if the resume fails
        var msg = new
        {
            type = "user",
            message = new { role = "user", content = new[] { new { type = "text", text } } },
        };
        stdin!.WriteLine(JsonSerializer.Serialize(msg));
        stdin.Flush();
    }

    void EnsureStarted()
    {
        if (proc is { HasExited: false })
            return;
        Stop();
        gotInit = false;
        lastUsed = clock.Elapsed.TotalSeconds;
        string folder = Directory.Exists(settings.ChatFolder) ? settings.ChatFolder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var psi = new ProcessStartInfo("claude")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false), // a BOM would spoil the first JSON line
        };
        foreach (string a in new[]
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--tools", Tools, "--allowedTools", Tools, "--strict-mcp-config", "--permission-prompts", "none",
            "--name", "RevBot", "--append-system-prompt", Prompt,
        })
            psi.ArgumentList.Add(a);
        resumed = SessionId is not null;
        if (resumed)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(SessionId!);
        }
        psi.Environment["REVBOT_PET_CHAT"] = "1";

        var p = Process.Start(psi) ?? throw new InvalidOperationException("claude did not start");
        proc = p;
        stdin = p.StandardInput;
        stdin.AutoFlush = false;
        Log.Info($"chat: started claude (pid {p.Id}){(resumed ? ", resuming its conversation" : ", new conversation")}");
        new Thread(() => ReadOut(p)) { IsBackground = true, Name = "chat stdout" }.Start();
        new Thread(() => DrainErr(p)) { IsBackground = true, Name = "chat stderr" }.Start();
    }

    void ReadOut(Process p)
    {
        try
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                string l = line;
                ui.BeginInvoke(() => OnLine(p, l));
            }
        }
        catch (Exception)
        {
            // the process went away
        }
        try
        {
            ui.BeginInvoke(() => OnExited(p));
        }
        catch (Exception)
        {
            // the pet is closing
        }
    }

    static void DrainErr(Process p)
    {
        try
        {
            int chars = 0;
            string? line;
            while ((line = p.StandardError.ReadLine()) is not null)
                chars += line.Length;
            if (chars > 0)
                Log.Info($"chat: claude wrote {chars} characters of diagnostics"); // not the text: it may quote the conversation
        }
        catch (Exception)
        {
        }
    }

    void OnLine(Process p, string line)
    {
        if (!ReferenceEquals(p, proc) || !line.StartsWith('{'))
            return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            string type = e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            switch (type)
            {
                case "system" when Str(e, "subtype") == "init":
                    gotInit = true;
                    pending = null;
                    if (Str(e, "session_id") is string id && id != SessionId)
                    {
                        SessionId = id;
                        settings.ChatSession = id;
                        settings.Save();
                    }
                    break;
                case "stream_event":
                    if (e.TryGetProperty("event", out var ev) && Str(ev, "type") == "content_block_delta" &&
                        ev.TryGetProperty("delta", out var d) && Str(d, "type") == "text_delta" && Str(d, "text") is string piece)
                    {
                        reply.Append(piece);
                        ReplyGrowing?.Invoke(Tidy(reply.ToString()));
                    }
                    else if (e.TryGetProperty("event", out ev) && Str(ev, "type") == "content_block_start" && reply.Length > 0)
                        reply.Append(' '); // a new block after some text (text, a tool, more text): keep a gap
                    break;
                case "result":
                    bool error = e.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                    string text = reply.ToString().Trim();
                    if (text.Length == 0 && Str(e, "result") is string r)
                        text = r.Trim();
                    Log.Info($"chat: reply {(error ? "failed" : $"{text.Length} characters")} ({Str(e, "subtype") ?? "?"})");
                    Finish(error ? "Something went wrong on Claude's end. Try me again in a bit." : text, error);
                    break;
            }
        }
        catch (JsonException)
        {
            // not a line meant for us
        }
    }

    void OnExited(Process p)
    {
        if (!ReferenceEquals(p, proc))
            return;
        int code = p.HasExited ? p.ExitCode : -1;
        bool failedResume = resumed && !gotInit;
        proc = null;
        stdin = null;
        Log.Info($"chat: claude exited ({code}){(failedResume ? " before starting; its old conversation is gone, starting a new one" : "")}");
        if (failedResume)
        {
            SessionId = null;
            settings.ChatSession = null;
            settings.Save();
            if (pending is string again)
            {
                pending = null;
                try
                {
                    EnsureStarted();
                    Write(again);
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("chat restart", ex);
                }
            }
        }
        if (Busy)
            Finish("I lost my connection to Claude. Try me again.", error: true);
    }

    /// <summary>No em or en dashes on screen, whatever the model does (Dave's rule): a comma reads the same.</summary>
    static string Tidy(string text) =>
        text.Replace(" \u2014 ", ", ").Replace("\u2014", ", ").Replace(" \u2013 ", ", ");

    void Finish(string text, bool error)
    {
        text = Tidy(text);
        Busy = false;
        lastUsed = clock.Elapsed.TotalSeconds;
        if (!error)
            LastReply = text;
        ReplyDone?.Invoke(text, error);
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    void Stop()
    {
        var p = proc;
        proc = null;
        try
        {
            stdin?.Close(); // end of input: claude finishes and exits
        }
        catch (Exception)
        {
        }
        stdin = null;
        if (p is null)
            return;
        Task.Run(() => // off the UI thread: it can take a moment to wind down
        {
            try
            {
                if (!p.WaitForExit(3000))
                    p.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
            p.Dispose();
        });
    }

    public void Dispose() => Stop();
}
