namespace DesktopPet;

/// <summary>What a line he is about to say is, for the quiet contract.</summary>
enum Speech
{
    /// <summary>You asked for it: a chat reply, a line you sent him, the menu's hello, a brain you just picked.</summary>
    Answer,
    /// <summary>Something needs you: a Claude Code session is waiting on your OK.</summary>
    Alert,
    /// <summary>Unprompted: the opening of Claude's reply when a session finishes.</summary>
    Remark,
}

/// <summary>
/// The quiet contract. He never talks on a timer, only about something that
/// happened, and how much of that he says is yours to set:
///   silent  only answers to you
///   events  also when something needs you (the default)
///   chatty  also a remark on what Claude just did
/// A quiet spell ("quiet for an hour") is silent until it ends, and survives a
/// restart. Hidden with the hotkey, he says nothing at all. The level, the spell
/// and the hiding go into state.json, so his other bodies show and obey them.
/// </summary>
static class Quiet
{
    public const string Silent = "silent", Events = "events", Chatty = "chatty";

    /// <summary>A remark is a glance, not a speech: at most this many characters.</summary>
    public const int RemarkMaxChars = 160;

    /// <summary>How long "quiet" lasts when no length is given.</summary>
    public const int DefaultMinutes = 60;

    public const string DefaultHotkey = "Ctrl+Alt+Shift+R";

    /// <summary>A level from settings or a command; anything unknown is the default.</summary>
    public static string Level(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        Silent => Silent,
        Chatty => Chatty,
        _ => Events,
    };

    /// <summary>May he say a line of this kind now?</summary>
    public static bool Allows(Speech kind, string level, bool quietNow, bool hidden) => !hidden && kind switch
    {
        Speech.Answer => true,
        Speech.Alert => !quietNow && level != Silent,
        Speech.Remark => !quietNow && level == Chatty,
        _ => false,
    };

    /// <summary>
    /// "quiet", "quiet on" or "quiet 30" (minutes, 1 to 1440) start a spell; "quiet off" ends it.
    /// False for anything else, and the spell stays as it was.
    /// </summary>
    public static bool TryUntil(string? arg, DateTimeOffset now, out DateTimeOffset? until)
    {
        until = null;
        string a = arg?.Trim().ToLowerInvariant() ?? "";
        if (a == "off")
            return true;
        if (a is "" or "on")
        {
            until = now.AddMinutes(DefaultMinutes);
            return true;
        }
        if (int.TryParse(a, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int minutes) && minutes is >= 1 and <= 1440)
        {
            until = now.AddMinutes(minutes);
            return true;
        }
        return false;
    }

    /// <summary>"Ctrl+Alt+Shift+R", "Win+F9", "Ctrl+Alt+5": modifier flags and a virtual key. False for "", "off" or nonsense.</summary>
    public static bool TryParseHotkey(string? text, out uint modifiers, out uint key)
    {
        modifiers = key = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        uint? found = null;
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= Native.MOD_CONTROL;
                    continue;
                case "alt":
                    modifiers |= Native.MOD_ALT;
                    continue;
                case "shift":
                    modifiers |= Native.MOD_SHIFT;
                    continue;
                case "win" or "windows":
                    modifiers |= Native.MOD_WIN;
                    continue;
            }
            if (found is not null)
                return false; // two keys
            if (part.Length == 1 && char.IsAsciiDigit(part[0]))
                found = (uint)Keys.D0 + (uint)(part[0] - '0'); // "5" would otherwise parse as the number 5
            else if (part.Length > 0 && !char.IsAsciiDigit(part[0]) && Enum.TryParse(part, ignoreCase: true, out Keys k) &&
                     (k & Keys.Modifiers) == 0 && k is not (Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin))
                found = (uint)k;
            else
                return false;
        }
        if (found is null)
            return false;
        key = found.Value;
        return true;
    }

    /// <summary>Checks for --selftest; throws on the first rule that does not hold.</summary>
    public static string SelfTest()
    {
        void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("quiet contract: " + what);
        }
        Check(Allows(Speech.Answer, Silent, quietNow: true, hidden: false), "an answer always gets through");
        Check(!Allows(Speech.Answer, Chatty, quietNow: false, hidden: true), "hidden means nothing at all");
        Check(!Allows(Speech.Alert, Silent, false, false), "silent: no alerts");
        Check(Allows(Speech.Alert, Events, false, false), "events: alerts");
        Check(!Allows(Speech.Alert, Events, true, false), "a quiet spell holds alerts back");
        Check(!Allows(Speech.Remark, Events, false, false), "events: no remarks");
        Check(Allows(Speech.Remark, Chatty, false, false), "chatty: remarks");
        Check(!Allows(Speech.Remark, Chatty, true, false), "a quiet spell holds remarks back");
        Check(Level(null) == Events && Level(" CHATTY ") == Chatty && Level("loud") == Events, "levels");

        var now = new DateTimeOffset(2026, 10, 10, 4, 0, 0, TimeSpan.Zero);
        Check(TryUntil(null, now, out var u) && u == now.AddHours(1), "quiet alone is an hour");
        Check(TryUntil("30", now, out u) && u == now.AddMinutes(30), "quiet 30");
        Check(TryUntil("off", now, out u) && u is null, "quiet off");
        Check(!TryUntil("0", now, out _) && !TryUntil("-5", now, out _) && !TryUntil("2000", now, out _) && !TryUntil("soon", now, out _), "quiet nonsense");

        Check(TryParseHotkey(DefaultHotkey, out uint m, out uint k) && m == (Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT) && k == (uint)Keys.R, "default hotkey");
        Check(TryParseHotkey("win + f9", out m, out k) && m == Native.MOD_WIN && k == (uint)Keys.F9, "win+f9");
        Check(TryParseHotkey("Ctrl+Alt+5", out _, out k) && k == (uint)Keys.D5, "a digit key");
        Check(!TryParseHotkey("Ctrl+Alt", out _, out _) && !TryParseHotkey("off", out _, out _) && !TryParseHotkey("", out _, out _) &&
              !TryParseHotkey("Ctrl+R+T", out _, out _) && !TryParseHotkey("Ctrl+Nope", out _, out _) && !TryParseHotkey("Ctrl+16", out _, out _), "hotkey nonsense");

        var alerts = new WaitAlerts();
        Check(alerts.ShouldAlert("a", 0), "first wait is said");
        Check(!alerts.ShouldAlert("a", 100), "the same wait is never said twice");
        Check(!alerts.ShouldAlert("b", 10), "a second session within 45 s is folded into the first");
        alerts.KeepOnly(id => id == "b"); // a stopped waiting (you answered); b still waits
        Check(!alerts.ShouldAlert("b", 200), "b's wait was already counted");
        Check(alerts.ShouldAlert("a", 200), "a new wait for a is said");
        return "ok";
    }
}

/// <summary>
/// Which waiting Claude Code sessions he has already told you about, so a wait
/// is mentioned once, never again however long it lasts, and a hush or a quiet
/// spell during it is not undone when the spell ends. Several sessions starting
/// to wait at once get one line between them.
/// </summary>
sealed class WaitAlerts
{
    const double MinGapSeconds = 45;

    readonly HashSet<string> told = new();
    double lastAt = double.NegativeInfinity;

    /// <summary>This session is now waiting on your OK: true when he should say so.</summary>
    public bool ShouldAlert(string session, double now)
    {
        if (!told.Add(session))
            return false;
        if (now - lastAt < MinGapSeconds)
            return false;
        lastAt = now;
        return true;
    }

    /// <summary>Forget sessions that stopped waiting, so their next wait is news again.</summary>
    public void KeepOnly(Func<string, bool> stillWaiting) => told.RemoveWhere(id => !stillWaiting(id));
}
