using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// revbot-hook: tells the running Revenant Desk Pet what Claude Code is doing.
//
//   revbot-hook                 run by a Claude Code hook: the event JSON arrives on stdin
//   revbot-hook --speak         the same, for the Stop hook, and the pet also says the
//                               opening of Claude's last reply in its speech bubble
//   revbot-hook --send "LINE"   send a command yourself, e.g. "state working", "play wave", "clear"
//
// It forwards only the event name, the session id, the tool name and the
// notification type, never prompts, file paths, commands or tool output. The one
// exception is --speak: the first sentence or two of Claude's own last reply (read
// from the local transcript Claude Code points it to), sent only to the pet over a
// pipe that only this Windows user can open, and never written anywhere. It prints
// nothing (so it adds nothing to Claude's context) and always exits 0, even when the
// pet is not running, so a hook can never get in the way.

// one pipe per Windows user (must match AiLink.PipeName in the pet)
string PipeName = "RevenantDeskPet-" + new string(Environment.UserName.Where(char.IsAsciiLetterOrDigit).ToArray());
const int MaxStdin = 8 * 1024 * 1024;

try
{
    int send = Array.IndexOf(args, "--send");
    if (send >= 0)
        Send(send + 1 < args.Length ? args[send + 1] : "");
    else
    {
        string json = ReadStdin();
        Send(FromHook(json));
        if (args.Contains("--speak") && Snippet(LastReply(json)) is string say)
            Send("say " + say);
    }
}
catch
{
    // pet not running, busy, or odd input: nothing to do
}
return 0;

void Send(string line)
{
    line = line.Replace('\r', ' ').Replace('\n', ' ').Trim();
    if (line.Length is 0 or > 500)
        return;
    try
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        pipe.Connect(300);
        pipe.Write(Encoding.UTF8.GetBytes(line + "\n"));
        pipe.Flush();
    }
    catch
    {
        // pet not running or busy
    }
}

// Claude's last reply: straight from the hook input if Claude Code gives it,
// otherwise the last assistant text in the session transcript (read from the end:
// transcripts get big).
static string? LastReply(string json)
{
    if (string.IsNullOrWhiteSpace(json))
        return null;
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    if (root.TryGetProperty("last_assistant_message", out var m) && m.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(m.GetString()))
        return m.GetString();
    if (!root.TryGetProperty("transcript_path", out var p) || p.ValueKind != JsonValueKind.String)
        return null;
    string path = p.GetString()!;
    if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        return null;

    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    long take = Math.Min(fs.Length, 4L * 1024 * 1024);
    fs.Seek(-take, SeekOrigin.End);
    var buf = new byte[take];
    fs.ReadExactly(buf);
    var lines = Encoding.UTF8.GetString(buf).Split('\n');
    for (int i = lines.Length - 1; i >= 0; i--)
    {
        string l = lines[i].Trim();
        if (!l.StartsWith('{') || !l.Contains("\"assistant\""))
            continue;
        try
        {
            using var entry = JsonDocument.Parse(l);
            var e = entry.RootElement;
            if (!e.TryGetProperty("type", out var t) || t.GetString() != "assistant" ||
                !e.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
                continue;
            string? text = null;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text" &&
                    part.TryGetProperty("text", out var tx) && !string.IsNullOrWhiteSpace(tx.GetString()))
                    text = tx.GetString();
            }
            if (text is not null)
                return text;
        }
        catch (JsonException)
        {
            // a line cut in half by where the read started
        }
    }
    return null;
}

// The opening of a reply, as plain words for the speech bubble: code, links and
// markdown marks removed, then whole sentences up to about 160 characters.
static string? Snippet(string? reply)
{
    if (string.IsNullOrWhiteSpace(reply))
        return null;
    string s = Regex.Replace(reply, "```.*?```", " ", RegexOptions.Singleline);    // code blocks
    var kept = new List<string>();
    foreach (string raw in s.Split('\n'))
    {
        string l = raw.Trim();
        if (l.StartsWith('|') || l.StartsWith("---"))
            continue;                                                            // tables, rules
        l = Regex.Replace(l, @"^(#+|>|[-*+]|\d+[.)])\s+", "");                  // headings, quotes, lists
        kept.Add(l);
    }
    s = string.Join(' ', kept);
    s = Regex.Replace(s, @"!?\[([^\]]*)\]\([^)]*\)", "$1");                     // links: keep the words
    s = Regex.Replace(s, @"(\*\*|__|\*|`)", "");                                 // bold, italics, code marks
    s = Regex.Replace(s, @"\s+", " ").Trim();
    if (s.Length == 0)
        return null;
    const int Max = 160;
    var sentences = Regex.Split(s, @"(?<=[.!?])\s+");
    var sb = new StringBuilder();
    foreach (string sentence in sentences)
    {
        if (sb.Length > 0 && sb.Length + 1 + sentence.Length > Max)
            break;
        if (sb.Length > 0)
            sb.Append(' ');
        sb.Append(sentence);
    }
    string outText = sb.ToString();
    if (outText.Length > Max)
    {
        int cut = outText.LastIndexOf(' ', Max - 3);
        outText = outText[..(cut > 40 ? cut : Max - 3)].TrimEnd(',', ';', ':', ' ') + "...";
    }
    return outText;
}

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
