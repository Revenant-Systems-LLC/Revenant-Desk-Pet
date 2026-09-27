using System.Text.Json;

namespace DesktopPet;

/// <summary>User choices, stored as settings.json next to the exe.</summary>
sealed class Settings
{
    public double Size { get; set; } = 1.0;
    public bool StayPut { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }

    static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

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
