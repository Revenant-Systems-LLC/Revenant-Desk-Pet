using System.Text.Json;

namespace DesktopPet;

/// <summary>User choices, stored as settings.json next to the exe.</summary>
sealed class Settings
{
    public double Size { get; set; } = 1.0;
    public bool StayPut { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    /// <summary>Which built-in pet to show ("revbot", "damien").</summary>
    public string Pet { get; set; } = "revbot";
    /// <summary>Climb onto and walk along the tops of open windows.</summary>
    public bool Terrain { get; set; } = true;
    /// <summary>Letters click as a speech bubble types out.</summary>
    public bool TalkSounds { get; set; } = true;
    /// <summary>When a terminal body appears, walk to its window and go in (off the desk until it closes).</summary>
    public bool FollowSurfaces { get; set; }
    /// <summary>Who the pet talks with: "claude" (Claude Code's default model), "claude:opus" etc., or "ollama:MODEL".</summary>
    public string ChatBrain { get; set; } = "claude";
    /// <summary>Where the pet's own Claude Code conversation runs (its working folder).</summary>
    public string ChatFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    /// <summary>That conversation's session id, so it carries on after a restart.</summary>
    public string? ChatSession { get; set; }
    /// <summary>What that conversation was allowed to do; when the pet's chat mode changes, a new conversation starts.</summary>
    public string? ChatMode { get; set; }

    /// <summary>"settings.json", or "settings.&lt;pet&gt;.json" for a friend pet.</summary>
    public static string FileName { get; set; } = "settings.json";

    static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                if (!double.IsFinite(s.Size))
                    s.Size = 1.0;
                s.Size = Math.Clamp(s.Size, 0.5, 2.0);
                if (!Atlas.IsBuiltIn(s.Pet))
                    s.Pet = "revbot";
                return s;
            }
        }
        catch (Exception ex)
        {
            Log.Error("settings load", ex);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("settings save", ex);
        }
    }
}
