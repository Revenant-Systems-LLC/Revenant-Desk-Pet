namespace DesktopPet;

/// <summary>Small append-only log next to the exe, capped at two 1 MB files.</summary>
static class Log
{
    const long MaxBytes = 1024 * 1024;
    static readonly object Gate = new();
    static string? path;
    static long written;
    static string? lastError;
    static DateTime lastErrorAt;

    public static void Init()
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "pet.log");
            written = File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            path = null;
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string context, Exception? ex)
    {
        // The same failure repeating every tick would flood the file.
        string key = context + "|" + ex?.GetType().Name + "|" + ex?.Message;
        lock (Gate)
        {
            if (key == lastError && (DateTime.Now - lastErrorAt).TotalSeconds < 60)
                return;
            lastError = key;
            lastErrorAt = DateTime.Now;
        }
        Write("ERROR", $"{context}: {ex}");
    }

    static void Write(string level, string message)
    {
        if (path is null)
            return;
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                if (written > MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                    written = 0;
                }
                File.AppendAllText(path, line);
                written += line.Length;
            }
            catch
            {
                // Logging must never take the pet down.
            }
        }
    }
}
