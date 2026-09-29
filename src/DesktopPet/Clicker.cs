using System.Runtime.InteropServices;

namespace DesktopPet;

/// <summary>
/// The little sound each letter makes as a speech bubble types out (think the
/// old Animal Crossing text boxes): a soft, low wooden "tock", Dave's pick of
/// three. Made in code at start-up in a few pitches, so each letter sounds a
/// little different but the same letter always sounds the same. Played with
/// winmm's PlaySound from memory; nothing is loaded from disk.
/// </summary>
sealed class Clicker : IDisposable
{
    const int SampleRate = 22050;
    const double Seconds = 0.05;    // each tock: 50 ms
    const double Volume = 0.22;

    // Pitches in Hz, low and soft. Vowels get the higher ones, which gives the typing a voice-like lilt.
    static readonly double[] Pitches = [420, 460, 500, 530, 560, 600, 640, 680];

    readonly GCHandle[] clips;

    public Clicker()
    {
        clips = new GCHandle[Pitches.Length];
        for (int i = 0; i < Pitches.Length; i++)
            clips[i] = GCHandle.Alloc(MakeClick(Pitches[i]), GCHandleType.Pinned); // PlaySound reads it while playing
    }

    /// <summary>Click for this letter (nothing for spaces and punctuation).</summary>
    public void Click(char c)
    {
        if (!char.IsLetterOrDigit(c))
            return;
        char lower = char.ToLowerInvariant(c);
        int i = "aeiou".Contains(lower)
            ? Pitches.Length - 1 - "aeiou".IndexOf(lower) % 3
            : lower % (Pitches.Length - 3);
        PlaySound(clips[i].AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
    }

    /// <summary>A 16-bit mono WAV: a soft wooden tock, a pure tone that drops a little in pitch as it dies away.</summary>
    static byte[] MakeClick(double hz)
    {
        int n = (int)(SampleRate * Seconds);
        var pcm = new short[n];
        double phase = 0;
        for (int k = 0; k < n; k++)
        {
            double t = k / (double)SampleRate;
            double f = hz * (1 - 0.15 * Math.Min(1, t / 0.02));          // pitch sinks 15% over 20 ms: the "wood"
            phase += 2 * Math.PI * f / SampleRate;
            double env = Math.Exp(-t / 0.010) * Math.Min(1, t / 0.0015); // 1.5 ms attack, 10 ms decay
            pcm[k] = (short)Math.Clamp(Math.Sin(phase) * env * Volume * short.MaxValue, short.MinValue, short.MaxValue);
        }

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            int data = n * 2;
            w.Write("RIFF"u8);
            w.Write(36 + data);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);                   // fmt chunk size
            w.Write((short)1);             // PCM
            w.Write((short)1);             // mono
            w.Write(SampleRate);
            w.Write(SampleRate * 2);       // bytes per second
            w.Write((short)2);             // block align
            w.Write((short)16);            // bits per sample
            w.Write("data"u8);
            w.Write(data);
            foreach (short s in pcm)
                w.Write(s);
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0); // stop before the memory goes away
        foreach (var h in clips)
        {
            if (h.IsAllocated)
                h.Free();
        }
    }

    const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    [DllImport("winmm.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
