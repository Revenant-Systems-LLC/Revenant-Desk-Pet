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
/// It reads, searches and looks things up freely (Read, Grep, Glob, WebSearch,
/// WebFetch). Anything else that needs permission, like writing or editing a file
/// or running a command, is asked of the host, the pet, which puts up a Yes/No
/// prompt (<see cref="PermissionAsked"/>, answered with <see cref="Answer"/>);
/// the user's own Claude Code permission settings still apply. No MCP servers.
/// Nothing said either way is ever logged, only lengths and tool names.
/// Events are raised on the UI thread.
/// </summary>
sealed class ClaudeChat : IChatBrain
{
    const string ReadOnlyTools = "Read,Grep,Glob,WebSearch,WebFetch"; // allowed without asking
    const string Tools = ReadOnlyTools + ",Edit,Write,NotebookEdit,Bash,PowerShell"; // the rest ask first
    const string Mode = "ask-v2"; // what it may do; a change starts a new conversation (an old one remembers saying it couldn't)
    const double IdleSeconds = 600; // no messages this long: let the process go (it resumes next time)

    const string Prompt = ChatText.Persona + " " +
        "You can read, search and look things up freely. Anything that changes something, like writing or editing a file " +
        "or running a command, asks the user first: a Yes/No prompt pops up on the pet and you wait for the answer. " +
        "Say in a few words what you are about to do before you do it, and do one change at a time.";

    /// <summary>A permission request waiting for Yes or No.</summary>
    public sealed record Ask(string Id, string Tool, string Question, string? Detail);

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
    /// <summary>Claude wants to do something that needs the user's OK: show it, then call <see cref="Answer"/>.</summary>
    public event Action<Ask>? PermissionAsked;
    /// <summary>The process went away: any prompts still up can go.</summary>
    public event Action? AsksCleared;

    readonly Dictionary<string, string> askInputs = new(); // request id -> the tool input (JSON), sent back unchanged on Yes

    public string? SessionId { get; private set; }
    public string Name => model is null ? "Claude" : "Claude " + char.ToUpperInvariant(model[0]) + model[1..];
    /// <summary>A message is on its way or being answered.</summary>
    public bool Busy { get; private set; }
    /// <summary>The last complete reply, for the chat box.</summary>
    public string LastReply { get; private set; } = "";

    readonly string? model; // null: Claude Code's own default

    /// <param name="model">A Claude Code model alias ("sonnet", "opus", "haiku"), or null for its default.</param>
    public ClaudeChat(Control ui, Settings settings, string? model = null)
    {
        this.ui = ui;
        this.settings = settings;
        this.model = model;
        if (settings.ChatMode != Mode)
        {
            settings.ChatSession = null;
            settings.ChatMode = Mode;
            settings.Save();
        }
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
            "--tools", Tools, "--allowedTools", ReadOnlyTools, "--strict-mcp-config",
            "--permission-prompts", "host", "--permission-prompt-tool", "stdio", // permission requests come to us
            "--name", "RevBot", "--append-system-prompt", Prompt,
        })
            psi.ArgumentList.Add(a);
        resumed = SessionId is not null;
        if (resumed)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(SessionId!);
        }
        if (model is not null)
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
        }
        psi.Environment["REVBOT_PET_CHAT"] = "1";

        var p = Process.Start(psi) ?? throw new InvalidOperationException("claude did not start");
        proc = p;
        stdin = p.StandardInput;
        stdin.AutoFlush = false;
        Log.Info($"chat: started claude (pid {p.Id}){(resumed ? ", resuming its conversation" : ", new conversation")}");
        // Say hello as its host first; without this it answers every permission request No by itself.
        stdin.WriteLine("{\"type\":\"control_request\",\"request_id\":\"pet_init\",\"request\":{\"subtype\":\"initialize\"}}");
        stdin.Flush();
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
                        ReplyGrowing?.Invoke(ChatText.Tidy(reply.ToString()));
                    }
                    else if (e.TryGetProperty("event", out ev) && Str(ev, "type") == "content_block_start" && reply.Length > 0)
                        reply.Append(' '); // a new block after some text (text, a tool, more text): keep a gap
                    break;
                case "control_request":
                    OnControlRequest(e);
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

    /// <summary>Yes or No to a permission request (from <see cref="PermissionAsked"/>).</summary>
    public void Answer(string id, bool allow)
    {
        if (!askInputs.Remove(id, out string? input) || stdin is null)
            return;
        string response = allow
            ? $"{{\"behavior\":\"allow\",\"updatedInput\":{input}}}"
            : "{\"behavior\":\"deny\",\"message\":\"The user said no.\"}";
        try
        {
            stdin.WriteLine($"{{\"type\":\"control_response\",\"response\":{{\"subtype\":\"success\",\"request_id\":{JsonSerializer.Serialize(id)},\"response\":{response}}}}}");
            stdin.Flush();
        }
        catch (Exception ex)
        {
            Log.Error("chat answer", ex);
        }
    }

    void OnControlRequest(JsonElement e)
    {
        string? id = Str(e, "request_id");
        if (id is null || !e.TryGetProperty("request", out var req))
            return;
        if (Str(req, "subtype") != "can_use_tool")
        {
            // nothing else is ours to handle: say so, so it doesn't wait on us
            stdin?.WriteLine($"{{\"type\":\"control_response\",\"response\":{{\"subtype\":\"error\",\"request_id\":{JsonSerializer.Serialize(id)},\"error\":\"not supported by the desk pet\"}}}}");
            stdin?.Flush();
            return;
        }
        string tool = Str(req, "tool_name") ?? "a tool";
        var input = req.TryGetProperty("input", out var i) ? i : default;
        askInputs[id] = input.ValueKind == JsonValueKind.Object ? input.GetRawText() : "{}";
        var (question, detail) = Describe(tool, Str(req, "display_name") ?? tool, Str(req, "description"), input);
        Log.Info($"chat: asks permission for {tool}"); // not the path or command
        PermissionAsked?.Invoke(new Ask(id, tool, question, detail));
    }

    /// <summary>The question to put to the user, and the exact thing (a full path or command) under it.</summary>
    static (string Question, string? Detail) Describe(string tool, string display, string? description, JsonElement input)
    {
        string? path = Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path");
        switch (tool)
        {
            case "Write" when path is not null:
                return (File.Exists(path) ? "Can I overwrite this file?" : "Can I create this file?", path);
            case "Edit" or "MultiEdit" or "NotebookEdit" when path is not null:
                return ("Can I edit this file?", path);
            case "Bash" or "PowerShell":
                string cmd = Str(input, "command") ?? "";
                return ("Can I run this command?", cmd.Length > 300 ? cmd[..300] + "..." : cmd);
            case "WebFetch":
                return ("Can I open this web page?", Str(input, "url"));
            default:
                return ($"Can I use {display}?", description ?? path);
        }
    }

    void OnExited(Process p)
    {
        if (!ReferenceEquals(p, proc))
            return;
        if (askInputs.Count > 0)
        {
            askInputs.Clear();
            AsksCleared?.Invoke();
        }
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

    void Finish(string text, bool error)
    {
        text = ChatText.Tidy(text);
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
