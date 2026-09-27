using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// revbot-hook: tells the running Revenant Desk Pet what Claude Code is doing.
//
//   revbot-hook                 run by a Claude Code hook: the event JSON arrives on stdin
//   revbot-hook --send "LINE"   send a command yourself, e.g. "state working", "play wave", "clear"
//
// It forwards only the event name, the session id, the tool name and the
// notification type, never prompts, file paths, commands or tool output. It prints nothing
// (so it adds nothing to Claude's context) and always exits 0, even when the
// pet is not running, so a hook can never get in the way.

// one pipe per Windows user (must match AiLink.PipeName in the pet)
string PipeName = "RevenantDeskPet-" + new string(Environment.UserName.Where(char.IsAsciiLetterOrDigit).ToArray());
const int MaxStdin = 8 * 1024 * 1024;

try
{
    int send = Array.IndexOf(args, "--send");
    string line = send >= 0 && send + 1 < args.Length ? args[send + 1] : FromHook(ReadStdin());
    line = line.Replace('\r', ' ').Replace('\n', ' ').Trim();
    if (line.Length is > 0 and <= 500)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        pipe.Connect(300);
        pipe.Write(Encoding.UTF8.GetBytes(line + "\n"));
        pipe.Flush();
    }
}
catch
{
    // pet not running, busy, or odd input: nothing to do
}
return 0;

static string ReadStdin()
{
    if (!Console.IsInputRedirected)
        return "";
    using var stdin = Console.OpenStandardInput();
    using var buffer = new MemoryStream();
    var chunk = new byte[64 * 1024];
    int n;
    bool tooBig = false;
    while ((n = stdin.Read(chunk, 0, chunk.Length)) > 0)
    {
        // keep reading to the end even when too big, so the writer never hits a broken pipe
        if (tooBig || buffer.Length + n > MaxStdin)
        {
            tooBig = true;
            continue;
        }
        buffer.Write(chunk, 0, n);
    }
    if (tooBig)
        return ""; // e.g. a huge file write; not worth parsing
    return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('﻿');
}

static string FromHook(string json)
{
    if (string.IsNullOrWhiteSpace(json))
        return "";
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    string? Field(string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Token(v.GetString()) : null;

    string? hookEvent = Field("hook_event_name");
    if (hookEvent is null)
        return "";
    var sb = new StringBuilder("hook ").Append(hookEvent);
    // the session id (a random identifier, no content) lets the pet tell parallel sessions apart
    if (Field("session_id") is string session)
        sb.Append(" session=").Append(session);
    if (Field("tool_name") is string tool)
        sb.Append(" tool=").Append(tool);
    if (Field("notification_type") is string type)
        sb.Append(" type=").Append(type);
    return sb.ToString();
}

// Names only: letters, digits and _ - . : ; anything else is dropped so nothing
// can smuggle extra words or lines into the command.
static string? Token(string? s)
{
    if (string.IsNullOrEmpty(s))
        return null;
    var clean = new string(s.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':').Take(100).ToArray());
    return clean.Length > 0 ? clean : null;
}
