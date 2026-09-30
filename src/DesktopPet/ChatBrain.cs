namespace DesktopPet;

/// <summary>
/// Whatever the pet talks with: its own Claude Code conversation (<see cref="ClaudeChat"/>)
/// or a local model through Ollama (<see cref="OllamaChat"/>). Events come on the UI thread.
/// </summary>
interface IChatBrain : IDisposable
{
    /// <summary>For the user: "Claude", "Claude Opus", "llama3.1:8b".</summary>
    string Name { get; }
    /// <summary>The reply so far (all of its text), as it arrives.</summary>
    event Action<string>? ReplyGrowing;
    /// <summary>The reply is complete: its full text, and whether something went wrong.</summary>
    event Action<string, bool>? ReplyDone;
    /// <summary>A message is on its way or being answered.</summary>
    bool Busy { get; }
    /// <summary>The last complete reply, for the chat box.</summary>
    string LastReply { get; }
    /// <summary>Get ready now (the chat box opened), so the reply comes sooner.</summary>
    void Warm();
    void Send(string text);
    /// <summary>Every tick.</summary>
    void Tick();
}

static class ChatText
{
    /// <summary>The persona every brain gets: short, plain replies for a speech bubble.</summary>
    public const string Persona =
        "You are speaking through RevBot, a small robot desktop pet on the user's screen. " +
        "Your replies appear in a comic speech bubble above his head and type out a letter at a time, " +
        "so keep every reply short: one to three plain sentences. No markdown, no lists, no headings, " +
        "no code blocks, no emojis, no em dashes. If more detail would help, give the gist and offer the rest.";

    /// <summary>No em or en dashes on screen, whatever the model does (Dave's rule): a comma reads the same.</summary>
    public static string Tidy(string text) =>
        text.Replace(" \u2014 ", ", ").Replace("\u2014", ", ").Replace(" \u2013 ", ", ");
}
