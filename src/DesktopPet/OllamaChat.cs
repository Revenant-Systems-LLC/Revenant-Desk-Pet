using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DesktopPet;

/// <summary>
/// Chatting with a local model through Ollama: plain conversation, no tools, the
/// same short-reply persona. Streams from /api/chat so the bubble types as it comes.
/// The model loads onto the graphics card for the chat and Ollama lets it go two
/// quiet minutes later (keep_alive), so the card is free again between chats. If
/// Ollama isn't running, the pet starts it, and stops it again when the pet exits.
/// Nothing said either way is logged, only lengths.
/// </summary>
sealed class OllamaChat : IChatBrain
{
    public const string Url = "http://127.0.0.1:11434";
    const int KeepTurns = 12; // earlier turns fall off, so a long chat stays quick

    readonly Control ui;
    readonly string model;
    readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly List<(string Role, string Content)> history = new();
    CancellationTokenSource? cts;
    Task? starting;
    static Process? ourServer; // Ollama as started by the pet (stopped when the pet exits)
    static readonly HttpClient probe = new() { Timeout = TimeSpan.FromSeconds(1) };

    public event Action<string>? ReplyGrowing;
    public event Action<string, bool>? ReplyDone;

    public string Name => model;
    public bool Busy { get; private set; }
    public string LastReply { get; private set; } = "";

    public OllamaChat(Control ui, string model)
    {
        this.ui = ui;
        this.model = model;
    }

    public void Warm() => starting ??= EnsureServer();

    public void Tick()
    {
    }

    public void Send(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;
        cts?.Cancel();
        cts = new CancellationTokenSource();
        Busy = true;
        history.Add(("user", text));
        Log.Info($"chat ({model}): sent {text.Length} characters");
        _ = Run(cts.Token);
    }

    async Task Run(CancellationToken token)
    {
        var reply = new StringBuilder();
        try
        {
            await (starting ??= EnsureServer());
            starting = null; // check again next time; it may have been closed
            var messages = new List<object> { new { role = "system", content = ChatText.Persona } };
            messages.AddRange(history.TakeLast(KeepTurns * 2).Select(h => (object)new { role = h.Role, content = h.Content }));
            var body = new { model, messages, stream = true, think = false, keep_alive = "2m" };
            using var req = new HttpRequestMessage(HttpMethod.Post, Url + "/api/chat")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(token));
            string? line;
            while ((line = await reader.ReadLineAsync(token)) is not null)
            {
                if (line.Length == 0)
                    continue;
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (e.TryGetProperty("error", out var err))
                    throw new InvalidOperationException(err.GetString());
                if (e.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) && c.GetString() is { Length: > 0 } piece)
                {
                    reply.Append(piece);
                    string soFar = Visible(reply.ToString());
                    if (soFar.Length > 0)
                        ui.BeginInvoke(() => ReplyGrowing?.Invoke(soFar));
                }
                if (e.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                    break;
            }
            string text = Visible(reply.ToString());
            history.Add(("assistant", text));
            Log.Info($"chat ({model}): reply {text.Length} characters");
            ui.BeginInvoke(() => Finish(text, false));
        }
        catch (OperationCanceledException)
        {
            // a newer message, or the pet switched brains
        }
        catch (Exception ex)
        {
            Log.Error($"chat ({model})", ex);
            if (history.Count > 0 && history[^1].Role == "user")
                history.RemoveAt(history.Count - 1);
            ui.BeginInvoke(() => Finish($"I couldn't get an answer from {model}. Is Ollama working?", true));
        }
    }

    /// <summary>What to show: a thinking model that thinks anyway has its &lt;think&gt; part (even one still open) left out.</summary>
    static string Visible(string raw) =>
        ChatText.Tidy(System.Text.RegularExpressions.Regex.Replace(raw, "<think>.*?(</think>|$)", "", System.Text.RegularExpressions.RegexOptions.Singleline).Trim());

    void Finish(string text, bool error)
    {
        Busy = false;
        if (!error)
            LastReply = text;
        ReplyDone?.Invoke(text, error);
    }

    /// <summary>Ollama is answering, or has been started and now is (up to 15 s).</summary>
    public static async Task EnsureServer()
    {
        if (await Alive())
            return;
        string exe = FindOllama() ?? throw new InvalidOperationException("Ollama isn't installed");
        ourServer = Process.Start(new ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true });
        Log.Info("chat: started Ollama");
        for (int i = 0; i < 60 && !await Alive(); i++)
            await Task.Delay(250);
    }

    static async Task<bool> Alive()
    {
        try
        {
            using var r = await probe.GetAsync(Url + "/api/version");
            return r.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool Installed => FindOllama() is not null;

    /// <summary>The pet is exiting: stop Ollama if the pet was the one that started it.</summary>
    public static void StopOurServer()
    {
        if (ourServer is { HasExited: false } s)
        {
            try
            {
                s.Kill(entireProcessTree: true);
                Log.Info("chat: stopped the Ollama the pet started");
            }
            catch (Exception)
            {
            }
        }
        ourServer?.Dispose();
        ourServer = null;
    }

    static string? FindOllama()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
        if (File.Exists(local))
            return local;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string p = Path.Combine(dir.Trim(), "ollama.exe");
                if (File.Exists(p))
                    return p;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    public void Dispose()
    {
        cts?.Cancel();
        http.Dispose();
    }
}
