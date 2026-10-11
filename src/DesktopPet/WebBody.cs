using System.Net;
using System.Text;

namespace DesktopPet;

/// <summary>
/// His web body: a small HTTP server on localhost only (never the network)
/// that serves one page showing him from his real sprite sheet, with his
/// state, what he is saying, and a chat box that talks to his brain. Any
/// browser tab on this PC that has the page open counts as a body
/// ("surface web on"), the same as the terminal mod.
///
/// Writes come in as pipe-style lines ("say TEXT", "ask TEXT", "play NAME")
/// and are handed to the UI thread, so the server never touches the pet
/// itself. A POST must carry the X-RevBot header and come from this origin:
/// a web page from elsewhere cannot make him talk or spend his Claude.
/// </summary>
sealed class WebBody : IDisposable
{
    const int MaxBody = 4000;

    readonly HttpListener listener = new();
    readonly SynchronizationContext ui;
    readonly Action<string> onLine;
    readonly Func<string> petId;
    readonly string statePath;
    readonly CancellationTokenSource stop = new();

    public string Url { get; }

    /// <summary>Start on this port, or log why not and return null (the pet works without it).</summary>
    public static WebBody? TryStart(int port, Action<string> onLine, Func<string> petId, string statePath)
    {
        try
        {
            return new WebBody(port, onLine, petId, statePath);
        }
        catch (Exception ex)
        {
            Log.Error($"web body on port {port}", ex);
            return null;
        }
    }

    WebBody(int port, Action<string> onLine, Func<string> petId, string statePath)
    {
        ui = SynchronizationContext.Current ?? throw new InvalidOperationException("WebBody must start on the UI thread");
        this.onLine = onLine;
        this.petId = petId;
        this.statePath = statePath;
        Url = $"http://localhost:{port}/";
        listener.Prefixes.Add(Url);
        listener.Start();
        _ = Task.Run(Loop);
        Log.Info($"web body at {Url}");
    }

    async Task Loop()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("web accept", ex);
                try
                {
                    await Task.Delay(500, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            // http.sys matches the "localhost" prefix on the Host header, not the interface:
            // a machine on the network could send Host: localhost. Loopback only, always.
            if (!IPAddress.IsLoopback(req.RemoteEndPoint.Address))
            {
                Send(res, 403, "text/plain", "this PC only");
                return;
            }
            string path = req.Url?.AbsolutePath ?? "/";
            if (req.HttpMethod == "GET")
            {
                switch (path)
                {
                    case "/":
                        Resource(res, "web.index.html", "text/html; charset=utf-8");
                        return;
                    case "/state":
                        State(req, res);
                        return;
                    case "/atlas.json":
                        Resource(res, Atlas.ResourceName(petId(), "json"), "application/json", etag: petId());
                        return;
                    case "/atlas.png":
                        Resource(res, Atlas.ResourceName(petId(), "png"), "image/png", etag: petId());
                        return;
                    case "/icon":
                        Resource(res, Atlas.ResourceName(petId(), "ico"), "image/x-icon", etag: petId());
                        return;
                }
                Send(res, 404, "text/plain", "not here");
                return;
            }
            if (req.HttpMethod == "POST")
            {
                if (!SameOrigin(req) || req.Headers["X-RevBot"] is null)
                {
                    Send(res, 403, "text/plain", "only his own page may do that");
                    return;
                }
                string body = ReadBody(req);
                string? line = path switch
                {
                    "/say" when body.Length > 0 => "say " + body,
                    "/ask" when body.Length > 0 => "ask " + body,
                    "/play" when body.Length > 0 => "play " + Word(body),
                    "/hold" when body.Length > 0 => "state " + Word(body),
                    "/clear" => "clear",
                    "/quiet" => "quiet " + Word(body),      // "" (an hour), "30", "off"
                    "/chatter" when body.Length > 0 => "chatter " + Word(body),
                    "/hush" => "hush",
                    _ => null,
                };
                if (line is null)
                {
                    Send(res, 400, "text/plain", "nothing to do");
                    return;
                }
                ui.Post(_ => onLine(line), null);
                Send(res, 202, "application/json", "{\"ok\":true}");
                return;
            }
            Send(res, 405, "text/plain", "no");
        }
        catch (Exception ex)
        {
            Log.Error("web request", ex);
            try
            {
                res.StatusCode = 500;
                res.Close();
            }
            catch
            {
                // the client went away
            }
        }
    }

    /// <summary>The state file as it is, and the page's id so he knows this tab is a body of his.</summary>
    void State(HttpListenerRequest req, HttpListenerResponse res)
    {
        string? client = req.QueryString["client"];
        if (!string.IsNullOrEmpty(client))
        {
            string id = new(client.Where(char.IsAsciiLetterOrDigit).Take(32).ToArray());
            if (id.Length > 0)
                ui.Post(_ => onLine($"surface web on session={id}"), null);
        }
        string json;
        try
        {
            json = File.Exists(statePath) ? File.ReadAllText(statePath) : "{\"version\":1,\"running\":false}";
        }
        catch (IOException)
        {
            json = "{\"version\":1,\"running\":true,\"busy\":true}"; // mid-write; the next poll gets it
        }
        res.Headers["Cache-Control"] = "no-store";
        Send(res, 200, "application/json", json);
    }

    bool SameOrigin(HttpListenerRequest req)
    {
        string origin = Url.TrimEnd('/');
        string? host = req.Headers["Host"];
        if (host is null || !($"http://{host}").Equals(origin, StringComparison.OrdinalIgnoreCase))
            return false;
        string? from = req.Headers["Origin"];
        return from is null || from.Equals(origin, StringComparison.OrdinalIgnoreCase);
    }

    static string ReadBody(HttpListenerRequest req)
    {
        if (req.ContentLength64 > MaxBody)
            return "";
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var buf = new char[MaxBody];
        int n = reader.ReadBlock(buf, 0, buf.Length);
        return new string(buf, 0, n).Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    static string Word(string s) => new(s.TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(32).ToArray());

    static void Resource(HttpListenerResponse res, string name, string contentType, string? etag = null)
    {
        using var stream = typeof(WebBody).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            Send(res, 404, "text/plain", "not here");
            return;
        }
        res.StatusCode = 200;
        res.ContentType = contentType;
        res.Headers["Cache-Control"] = "no-cache";
        if (etag is not null)
            res.Headers["ETag"] = $"\"{etag}\"";
        res.ContentLength64 = stream.Length;
        stream.CopyTo(res.OutputStream);
        res.Close();
    }

    static void Send(HttpListenerResponse res, int status, string contentType, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        res.StatusCode = status;
        res.ContentType = contentType.Contains("charset") ? contentType : contentType + "; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
        res.Close();
    }

    public void Dispose()
    {
        stop.Cancel();
        try
        {
            listener.Stop();
            listener.Close();
        }
        catch
        {
            // already down
        }
    }
}
