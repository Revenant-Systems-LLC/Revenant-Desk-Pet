using System.IO.Pipes;
using System.Text;

namespace DesktopPet;

/// <summary>
/// A local command line into the running pet: a named pipe only the current
/// Windows user can connect to. Each connection sends one short text line,
/// e.g. "hook PreToolUse tool=Edit" (from revbot-hook.exe, run by Claude Code
/// hooks) or "state working" / "play wave" / "clear". Lines are handed to the
/// UI thread; nothing is ever written back.
/// </summary>
sealed class AiLink : IDisposable
{
    /// <summary>One pipe per Windows user, so another account can't squat on the name.</summary>
    public static readonly string PipeName =
        "RevenantDeskPet-" + new string(Environment.UserName.Where(char.IsAsciiLetterOrDigit).ToArray());
    const int Listeners = 4;          // bursts of hook events do not have to queue
    const int MaxLine = 512;

    readonly SynchronizationContext ui;
    readonly Action<string> onLine;
    readonly CancellationTokenSource stop = new();

    public AiLink(Action<string> onLine)
    {
        ui = SynchronizationContext.Current ?? throw new InvalidOperationException("AiLink must start on the UI thread");
        this.onLine = onLine;
        for (int i = 0; i < Listeners; i++)
            _ = Task.Run(ListenLoop);
        Log.Info($"ai link listening on \\\\.\\pipe\\{PipeName}");
    }

    async Task ListenLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                string? line = await ReadLine(pipe).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                    ui.Post(_ => Dispatch(line.Trim()), null);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("ai link", ex);
                try
                {
                    await Task.Delay(1000, stop.Token).ConfigureAwait(false); // do not spin on a persistent error
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    async Task<string?> ReadLine(NamedPipeServerStream pipe)
    {
        // A sender has two seconds to say its line; a stuck client must not hold a listener.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var buffer = new byte[MaxLine];
        int total = 0;
        try
        {
            while (total < MaxLine)
            {
                int n = await pipe.ReadAsync(buffer.AsMemory(total, MaxLine - total), timeout.Token).ConfigureAwait(false);
                if (n == 0)
                    break;
                total += n;
                if (Array.IndexOf(buffer, (byte)'\n', 0, total) >= 0)
                    break;
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // timed out: use whatever arrived
        }
        string text = Encoding.UTF8.GetString(buffer, 0, total);
        int nl = text.IndexOf('\n');
        return nl >= 0 ? text[..nl] : text;
    }

    void Dispatch(string line)
    {
        try
        {
            onLine(line);
        }
        catch (Exception ex)
        {
            Log.Error("ai link dispatch", ex);
        }
    }

    // Not disposing the token source: listener loops may still be reading its token as they exit.
    public void Dispose() => stop.Cancel();
}
