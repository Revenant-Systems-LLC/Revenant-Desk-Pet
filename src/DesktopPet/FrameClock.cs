using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopPet;

/// <summary>
/// Drives the pet's ticks in step with the display. A background thread sleeps
/// on a high-resolution waitable timer until just before the next display
/// refresh (DWM says when that is), then posts one tick to the UI thread. About
/// 60 ticks a second: every refresh at 60 Hz, every 2nd at 120 or 144 Hz, every
/// 3rd at 165 or 180 Hz, so each position stays up for the same number of
/// refreshes. The UI thread never blocks waiting for it, so the mouse stays
/// immediate, and a tick still waiting to run is never posted twice.
/// </summary>
sealed class FrameClock : IDisposable
{
    const double TargetHz = 60;
    const double LeadMs = 3;        // post this long before the refresh, so the frame makes it
    const double SlowSeconds = 0.5; // hidden behind a fullscreen app

    readonly Control owner;
    readonly Action tick;
    readonly Thread thread;
    volatile bool stop, slow;
    int pending;

    public FrameClock(Control owner, Action tick)
    {
        this.owner = owner;
        this.tick = tick;
        thread = new Thread(Run) { IsBackground = true, Name = "frame clock", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>Tick twice a second instead (nothing is on screen).</summary>
    public bool Slow { set => slow = value; }

    void Run()
    {
        IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        bool precise = timer != IntPtr.Zero;
        if (!precise)
            timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS); // older Windows: coarser, still works
        if (timer == IntPtr.Zero)
        {
            Log.Info("frame clock: no waitable timer; ticking with Thread.Sleep");
        }
        long freq = Stopwatch.Frequency;
        long lead = (long)(LeadMs / 1000 * freq);
        long nextTiming = 0, period = freq / 60, vblank = Stopwatch.GetTimestamp();
        int every = 1;
        int toldEvery = 0;
        try
        {
            while (!stop)
            {
                long now = Stopwatch.GetTimestamp();
                if (slow)
                {
                    WaitUntil(timer, now + (long)(SlowSeconds * freq));
                    Post();
                    continue;
                }
                if (now >= nextTiming)
                {
                    nextTiming = now + freq; // the refresh rate can change (another monitor, a setting)
                    if (TryTiming(out long p, out long v))
                        (period, vblank) = (p, v);
                    else
                        (period, vblank) = (freq / 60, now);
                    double hz = (double)freq / period;
                    every = Math.Max(1, (int)Math.Round(hz / TargetHz));
                    if (every != toldEvery) // DWM's reported rate wobbles (59 / 59.9 Hz with mixed monitors): say it when the pacing changes
                    {
                        toldEvery = every;
                        Log.Info($"frame clock: display {hz:0.#} Hz, a tick every {every} refresh{(every > 1 ? "es" : "")} ({hz / every:0.#}/s){(precise ? "" : ", coarse timer")}");
                    }
                }
                long step = period * every;
                long k = (now + lead - vblank) / step + 1;
                WaitUntil(timer, vblank + k * step - lead);
                Post();
            }
        }
        finally
        {
            if (timer != IntPtr.Zero)
                CloseHandle(timer);
        }
    }

    void Post()
    {
        if (stop || Interlocked.Exchange(ref pending, 1) == 1)
            return; // the last one hasn't run yet
        try
        {
            owner.BeginInvoke(RunTick);
        }
        catch (Exception) when (stop || owner.IsDisposed || !owner.IsHandleCreated)
        {
            stop = true; // shutting down
        }
    }

    void RunTick()
    {
        Volatile.Write(ref pending, 0);
        if (!stop)
            tick();
    }

    static void WaitUntil(IntPtr timer, long qpc)
    {
        long ticks100ns = (qpc - Stopwatch.GetTimestamp()) * 10_000_000 / Stopwatch.Frequency;
        if (ticks100ns <= 0)
            return;
        if (timer == IntPtr.Zero)
        {
            Thread.Sleep(Math.Max(1, (int)(ticks100ns / 10_000)));
            return;
        }
        long due = -ticks100ns; // negative: relative
        if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            WaitForSingleObject(timer, 1000);
        else
            Thread.Sleep(1);
    }

    /// <summary>The display's refresh period and the time of its last refresh, in Stopwatch (QPC) ticks.</summary>
    static bool TryTiming(out long period, out long vblank)
    {
        // DWM_TIMING_INFO is packed (pshpack1): 292 bytes. rateRefresh sits at 4,
        // qpcRefreshPeriod at 12, rateCompose at 20, qpcVBlank at 28.
        const int Size = 292;
        period = vblank = 0;
        IntPtr buf = Marshal.AllocHGlobal(Size);
        try
        {
            for (int i = 0; i < Size; i += 4)
                Marshal.WriteInt32(buf, i, 0);
            Marshal.WriteInt32(buf, 0, Size);
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, buf) != 0)
                return false;
            period = Marshal.ReadInt64(buf, 12);
            vblank = Marshal.ReadInt64(buf, 28);
            long freq = Stopwatch.Frequency;
            return period > freq / 500 && period < freq / 20 && vblank > 0; // 20..500 Hz, or it's nonsense
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public void Dispose()
    {
        stop = true;
        thread.Join(500);
    }

    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    const uint TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, IntPtr timingInfo);
}
