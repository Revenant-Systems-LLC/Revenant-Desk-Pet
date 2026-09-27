using System.Runtime.InteropServices;
using System.Text;

namespace DesktopPet;

/// <summary>A visible stretch of a window's top edge the pet can stand on, in physical screen pixels.</summary>
readonly record struct Ledge(double Left, double Right, double Y, IntPtr Owner);

/// <summary>What the pet can stand on besides the taskbar.</summary>
interface ITerrain
{
    /// <summary>The ledges as of the last refresh, front window first.</summary>
    IReadOnlyList<Ledge> Ledges { get; }

    /// <summary>Goes up by one on every refresh.</summary>
    int Version { get; }

    /// <summary>Where a window's top edge is right now (for riding along on it between refreshes); false if it is gone, hidden or minimised.</summary>
    bool TryGetTop(IntPtr owner, out double left, out double right, out double y);
}

/// <summary>
/// The desktop as a 2D side-scroller: the visible top edges of open windows
/// are ledges. Strictly read-only: it only asks Windows where windows are,
/// and never moves, clicks, focuses or sends anything to them.
/// </summary>
sealed class DesktopTerrain : ITerrain
{
    const int MinLedgeWidth = 50;

    readonly IntPtr self;
    readonly uint selfPid = (uint)Environment.ProcessId;
    readonly List<(IntPtr Hwnd, Native.RECT Rect, bool Ledge)> windows = new();
    readonly StringBuilder className = new(64);
    readonly StringBuilder title = new(32);
    readonly List<(IntPtr Hwnd, uint Pid, Native.RECT Rect)> pets = new();
    readonly Native.EnumWindowsProc collect;
    List<Ledge> ledges = new();

    public DesktopTerrain(IntPtr petWindow)
    {
        self = petWindow;
        collect = Collect; // kept in a field so the delegate outlives every EnumWindows call
    }

    public IReadOnlyList<Ledge> Ledges => ledges;

    /// <summary>Other desk pets on screen (other processes' pet windows), as of the last refresh.</summary>
    public IReadOnlyList<(IntPtr Hwnd, uint Pid, Native.RECT Rect)> OtherPets => pets;
    public int Version { get; private set; }

    public void Refresh()
    {
        windows.Clear();
        pets.Clear();
        Native.EnumWindows(collect, IntPtr.Zero); // top of the z-order first

        var next = new List<Ledge>();
        var spans = new List<(double L, double R)>();
        for (int i = 0; i < windows.Count; i++)
        {
            var (hwnd, r, ledge) = windows[i];
            if (!ledge)
                continue;
            // The top edge shows only where no window in front of it covers that row.
            spans.Clear();
            spans.Add((r.Left, r.Right));
            for (int j = 0; j < i && spans.Count > 0; j++)
            {
                var f = windows[j].Rect;
                if (f.Top <= r.Top && r.Top < f.Bottom)
                    Subtract(spans, f.Left, f.Right);
            }
            foreach (var (l, rt) in spans)
            {
                if (rt - l >= MinLedgeWidth)
                    next.Add(new Ledge(l, rt, r.Top, hwnd));
            }
        }
        ledges = next;
        Version++;
    }

    public bool TryGetTop(IntPtr owner, out double left, out double right, out double y)
    {
        left = right = y = 0;
        if (!Native.IsWindow(owner) || !Native.IsWindowVisible(owner) || Native.IsIconic(owner) || Cloaked(owner) ||
            !TryFrame(owner, out var r))
            return false;
        (left, right, y) = (r.Left, r.Right, r.Top);
        return true;
    }

    bool Collect(IntPtr hwnd, IntPtr _)
    {
        if (hwnd == self || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
            return true;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == selfPid)
            return true;
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TRANSPARENT) != 0) // click-through overlays: nothing to see or stand on
            return true;
        if (Cloaked(hwnd) || !TryFrame(hwnd, out var r) || r.Right - r.Left < 1 || r.Bottom - r.Top < 1)
            return true;
        title.Clear();
        Native.GetWindowText(hwnd, title, title.Capacity);
        if ((ex & Native.WS_EX_LAYERED) != 0 && title.ToString() == PetWindow.Title)
        {
            pets.Add((hwnd, pid, r)); // another desk pet: a friend, not terrain
            return true;
        }
        className.Clear();
        Native.GetClassName(hwnd, className, className.Capacity);
        string cls = className.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return true; // the desktop itself, and the taskbars (they are the ground)
        // Tool windows (menus, tooltips, palettes) still hide what is behind them, but are not ledges.
        windows.Add((hwnd, r, (ex & Native.WS_EX_TOOLWINDOW) == 0));
        return true;
    }

    static bool Cloaked(IntPtr hwnd) =>
        Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>The window as drawn (DWM frame bounds, without the invisible resize border).</summary>
    static bool TryFrame(IntPtr hwnd, out Native.RECT r) =>
        Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<Native.RECT>()) == 0 ||
        Native.GetWindowRect(hwnd, out r);

    static void Subtract(List<(double L, double R)> spans, double l, double r)
    {
        for (int k = spans.Count - 1; k >= 0; k--)
        {
            var (a, b) = spans[k];
            if (r <= a || l >= b)
                continue;
            spans.RemoveAt(k);
            if (a < l)
                spans.Add((a, l));
            if (r < b)
                spans.Add((r, b));
        }
    }
}
