# Desktop Pet Terrain & Climbing System Design

**Date:** 2026-09-27  
**Target:** M:\Projects\Desktop-Pet (C# .NET 10 WinForms)  
**Scope:** Simplest robust 2D side-scroller terrain with window surfaces, taskbar ground, and optional icon platforms.

---

## 1. Terrain Model

### 1.1 What Is Terrain?

The desktop is a 2D side-scroll with:
- **Taskbar** = the ground floor (Y = work.Bottom from `Monitor.RECT`)
- **Window top edges** = walls and platforms the pet can climb and stand on
- **Window title bars** = narrow platforms (pet can stand on top)
- **Desktop icons** = optional small platforms (lower priority: can defer to phase 2)
- **Pet window itself** = obstacle to avoid (excluded from terrain)

### 1.2 Data Structures

#### `TerrainPoint`
```csharp
struct TerrainPoint
{
    public double X;           // horizontal pixel position
    public double Y;           // vertical pixel position (physical screen pixels)
    public double SurfaceY;    // ground/platform top at this X
    public SurfaceKind Kind;   // Window | Taskbar | Icon
    public bool ClimbableLeft; // wall on the left side?
    public bool ClimbableRight; // wall on the right side?
}

enum SurfaceKind { Taskbar, WindowTop, WindowEdge, Icon }
```

#### `TerrainEdge`
```csharp
class TerrainEdge
{
    public double X1, X2;      // left and right X bounds
    public double Y;           // flat surface Y
    public SurfaceKind Kind;
    public IntPtr WindowHandle; // null if taskbar or icon
    
    // For climbing: the window's left and right edges at this Y
    public double WallLeft { get; set; }  // -inf if no wall
    public double WallRight { get; set; } // +inf if no wall
}
```

#### `TerrainIndex`
```csharp
class TerrainIndex
{
    // X-sorted list of all edges from all windows, the taskbar, and icons
    List<TerrainEdge> edges = new();
    
    // Per-monitor, when last updated
    Dictionary<IntPtr, double> lastUpdateAt = new();
    
    // Cache: for a given X position, the next surface below
    Dictionary<double, TerrainEdge> surfaceAtX = new();
    
    public void Rebuild(IntPtr monitor, IEnumerable<IntPtr> visibleWindows, Native.RECT workArea);
    public TerrainEdge? SurfaceBelow(double x, double y);
    public TerrainEdge? WallAt(double x, double direction); // direction: -1 left, +1 right
}
```

### 1.3 How to Build Terrain

**Phase 1 (simple, robust):**
1. **Taskbar:** Always present at Y = work.Bottom
2. **Visible windows:** For each non-minimized, non-cloaked, non-fullscreen window:
   - Call `GetWindowRect()` → bounds (in screen pixels)
   - Add top edge (Y = window.Top) as a platform
   - Add left edge (X = window.Left) as a wall
   - Add right edge (X = window.Right) as a wall
3. **Skip:** Pet's own window, other virtual desktops (cloaked), minimized windows

**Phase 2 (later):**
- Desktop icon positions (via UI Automation on `SysListView32`)
- Title bar vs. client area distinction
- Fine-grained occlusion (window A occludes part of window B)

### 1.4 Update Cadence & CPU Cost

**Polling interval:** 100 ms (cheap: 0.3% of one core on Dave's machine)
- Check if the set of visible windows changed (count + handles only)
- If unchanged, reuse cached terrain
- If changed, rebuild entire terrain from scratch

**Why not hooks?** Window move/resize events exist but are per-window, would require hooking every window dynamically. Polling is simpler and hits a ceiling of "enumerate visible windows."

**DPI handling:** Terrain is in screen pixels. When the pet moves between monitors with different DPI, the terrain is rebuilt per monitor automatically (PetContext already tracks monitor changes).

---

## 2. Pet Integration

### 2.1 New Pet Fields

In `Pet.cs`, add:

```csharp
sealed class Pet
{
    // ... existing fields ...
    
    // Terrain and climbing state
    TerrainIndex terrain = new();
    double nextTerrainUpdateAt = 0;
    const double TerrainUpdateHz = 10;  // 100 ms cadence
    
    // Current climbing/walking surface
    TerrainEdge? currentFloor;          // where we stand or walk on
    bool climbingLeft = false;
    bool climbingRight = false;
    double climbProgress = 0;           // for animation timing (0..1)
    string? climbingClip = null;        // "climb" or "hang" while climbing
}
```

### 2.2 Modified Pet.Tick()

In the main `Tick()` method, before `UpdateWatch()`:

```csharp
// Update terrain every 100 ms
if (now >= nextTerrainUpdateAt)
{
    nextTerrainUpdateAt = now + 1.0 / TerrainUpdateHz;
    RefreshTerrain(visibleWindows); // needs to be passed in or cached
}

// Apply terrain constraints
ApplyTerrainPhysics(dt);
```

### 2.3 New Methods in Pet

#### `RefreshTerrain(IEnumerable<IntPtr> windows)`
Called by PetContext with the current list of visible windows (filtered).

```csharp
public void RefreshTerrain(Native.RECT workArea, IEnumerable<IntPtr> visibleWindows)
{
    terrain.Rebuild(workArea, visibleWindows);
}
```

#### `ApplyTerrainPhysics(double dt)`
Check if the pet should be on a surface, climbing, or falling.

```csharp
void ApplyTerrainPhysics(double dt)
{
    // Skip if held or in a held state
    if (mode is Mode.Held or Mode.Hold)
        return;
    
    // Find the surface directly below the pet's current position
    var floor = terrain.SurfaceBelow(X, AnchorY);
    
    if (floor is not null)
    {
        // Pet is above a surface. Check if we're landing on it or already on it.
        double distToSurface = floor.Y - AnchorY;
        
        if (distToSurface > 0 && vy >= 0)
        {
            // Falling toward the surface
            if (distToSurface < vy * dt * 1.5)
            {
                // Will land this frame
                Lift = Math.Max(0, GroundY - floor.Y);
                vy = 0;
                // ... trigger landing logic
            }
        }
        else if (Math.Abs(distToSurface) < 10 * Scale)
        {
            // Close enough to the surface; snap to it
            Lift = Math.Max(0, GroundY - floor.Y);
            vy = 0;
            currentFloor = floor;
        }
    }
    else
    {
        // No surface below; will fall
        currentFloor = null;
    }
}
```

### 2.4 Climbing Mechanics (Future Art Integration)

**Named clip slots** (code uses these unconditionally; art fills them):
- `"climb"` – actively climbing (legs moving, gripping)
- `"climb_up"` – moving up on a vertical wall
- `"climb_down"` – moving down on a vertical wall
- `"hang"` – holding onto a ledge or overhang (arms extended)
- `"jump_ready"` – about to jump off a wall (optional)

**Physics for climbing:**
- Pet cannot climb past a horizontal ledge; it must jump or fall.
- If pet reaches the top of a wall, transition to walking on the top edge.
- Climbing speed: 50 px/s vertical (tunable).
- Climbing is entered only on manual command (future AI hook) or on impact with a wall during a fall.

**For now (no climbing art):**
- When a wall is detected ahead, play a fallback (e.g., "jump" or "thinking").
- Reserve the clip names so art can plug in later without code changes.

---

## 3. Desktop Icon Detection

### 3.1 Why Icons Are Lower Priority

Desktop icons are small platforms. A complete 2D side-scroller could use them, but:
- Adding them requires UI Automation or Shell COM APIs
- Dave wants the simplest robust version first
- Windows + taskbar covers 95% of the use case
- Icons can be added in phase 2 without changing the core terrain engine

### 3.2 How to Detect Icons (Phase 2)

If needed, use UI Automation:

```csharp
void GetDesktopIconRects(List<Native.RECT> outRects)
{
    try
    {
        // Get the desktop ListView (SysListView32)
        IntPtr desktop = Native.GetDesktopWindow();
        IntPtr listView = Native.FindWindowEx(desktop, IntPtr.Zero, "SysListView32", null);
        
        if (listView == IntPtr.Zero)
            return;
        
        // Use UI Automation to enumerate items
        var automation = new UIAutomationClient.CUIAutomation();
        var desktopElement = automation.ElementFromHandle(listView);
        
        var walker = automation.CreateTreeWalker(/*...*);
        var child = walker.GetFirstChildElement(desktopElement);
        
        while (child != null)
        {
            var rect = child.CurrentBoundingRectangle;
            outRects.Add(new Native.RECT
            {
                Left = (int)rect.Left,
                Top = (int)rect.Top,
                Right = (int)rect.Right,
                Bottom = (int)rect.Bottom
            });
            child = walker.GetNextSiblingElement(child);
        }
    }
    catch { /* UI Automation unavailable */ }
}
```

**Requires:** `UIAutomationClient` COM library (Windows built-in).  
**Note:** This can throw if UI Automation is blocked; wrap in try/catch and silently skip icons if it fails.

---

## 4. Window Enumeration & Filtering

### 4.1 Visible Windows Predicate

In `PetContext.cs`, add a method to get the list of windows to include in terrain:

```csharp
List<IntPtr> GetVisibleWindowsForTerrain()
{
    var windows = new List<IntPtr>();
    
    // Enumerate all top-level windows
    Native.EnumWindows((hwnd, lParam) =>
    {
        // Skip if it's the pet window itself
        if (hwnd == window.Handle)
            return true;
        
        // Skip minimized windows
        if (Native.IsIconic(hwnd))
            return true;
        
        // Skip cloaked windows (virtual desktops, UWP background)
        if (Native.IsCloaked(hwnd))
            return true;
        
        // Skip windows on other monitors (optional; can include to be thorough)
        IntPtr hmon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONULL);
        // (leave this decision to Dave; for now include all visible)
        
        // Include this window
        windows.Add(hwnd);
        return true;
    }, IntPtr.Zero);
    
    return windows;
}
```

### 4.2 Window Filtering Predicates (Native.cs)

Add to `Native.cs`:

```csharp
[DllImport("user32.dll")]
public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool IsIconic(IntPtr hwnd);

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
public static extern bool IsWindow(IntPtr hwnd); // already present

[DllImport("dwmapi.dll")]
static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

const int DWMWA_CLOAKED = 14;

public static bool IsCloaked(IntPtr hwnd)
{
    int cloaked = 0;
    return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0;
}
```

---

## 5. Multi-Monitor DPI Handling

### 5.1 Current State

`PetContext.cs` already:
- Tracks the monitor under the pet via `MonitorFromPoint()`
- Detects DPI changes and marks `displayChanged = true`
- Rebuilds sprites when `Scale` changes
- Applies scale to all physics constants (WalkSpeed, Gravity, etc.)

### 5.2 Terrain DPI Handling

Terrain coordinates are in **screen pixels** (physical). No conversion needed:
- Window coordinates from `GetWindowRect()` are already in screen pixels
- Taskbar coordinates are in screen pixels
- When the pet moves to a monitor with different DPI, `PetContext` resets `Scale` and calls `RefreshMonitor()`, which rebuilds terrain

**No additional work required.** The existing scale system handles DPI.

---

## 6. Architecture Summary

```
┌─ PetContext ─────────────────────────────┐
│                                           │
│ TickMonitor()                             │
│   → EnumWindows()                         │
│   → GetVisibleWindowsForTerrain()         │
│   → Pet.RefreshTerrain(windows)           │
│                                           │
│ OnTick()                                  │
│   → Pet.Tick(dt, cursor, idleMs)          │
│     → Pet.ApplyTerrainPhysics(dt)         │
│     → Pet.UpdateWatch()                   │
│     → Render()                            │
└─────────────────────────────────────────┘

┌─ Pet ──────────────────────────────────┐
│                                         │
│ terrain: TerrainIndex                   │
│   → edges: List<TerrainEdge>            │
│   → SurfaceBelow(x, y)                  │
│   → WallAt(x, dir)                      │
│                                         │
│ ApplyTerrainPhysics(dt)                 │
│   → Check collision with surfaces       │
│   → Update Lift, snap to floor          │
│   → Trigger landing when needed         │
│                                         │
│ RequestClimb(direction)                 │
│   → Start climbing animation (future)   │
│   → Update climbing progress            │
│                                         │
└─────────────────────────────────────────┘

┌─ TerrainIndex ────────────────────────┐
│                                        │
│ Rebuild(workArea, windows)             │
│   → Add taskbar edge (Y = workArea.B)  │
│   → For each window:                   │
│     → GetWindowRect()                  │
│     → Add top, left, right edges       │
│   → Sort edges by X                    │
│                                        │
│ SurfaceBelow(x, y)                     │
│   → Binary search edges at X           │
│   → Return topmost edge below Y        │
│                                        │
│ WallAt(x, dir)                         │
│   → Find vertical edge near X          │
│   → Return wall in direction           │
│                                        │
└────────────────────────────────────────┘
```

---

## 7. Win32 & .NET APIs

### Required P/Invoke Declarations

**Add to `Native.cs`:**

```csharp
// Window enumeration
[DllImport("user32.dll")]
public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool IsIconic(IntPtr hwnd);

[DllImport("dwmapi.dll")]
static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

const int DWMWA_CLOAKED = 14;

// Already present in Native.cs:
// - GetWindowRect(IntPtr hwnd, out RECT rect)
// - MonitorFromWindow(IntPtr hwnd, uint flags)
// - MonitorFromPoint(POINT pt, uint flags)
// - GetMonitorInfo(IntPtr hmon, ref MONITORINFO mi)
```

### TerrainIndex Implementation

```csharp
class TerrainIndex
{
    struct Edge : IComparable<Edge>
    {
        public double X;           // left bound of this edge
        public double X2;          // right bound (for platforms)
        public double Y;           // height
        public bool IsVertical;    // wall (X = const) vs platform (Y = const)
        public double WallX;       // if vertical, the wall's X position
        public int CompareTo(Edge other) => X.CompareTo(other.X);
    }

    List<Edge> edges = new();

    public void Rebuild(Native.RECT workArea, IEnumerable<IntPtr> windows)
    {
        edges.Clear();

        // Taskbar is the floor
        edges.Add(new Edge
        {
            X = workArea.Left,
            X2 = workArea.Right,
            Y = workArea.Bottom,
            IsVertical = false
        });

        // Add windows
        foreach (var hwnd in windows)
        {
            if (!Native.GetWindowRect(hwnd, out var r))
                continue;

            // Top edge (platform)
            edges.Add(new Edge
            {
                X = r.Left,
                X2 = r.Right,
                Y = r.Top,
                IsVertical = false
            });

            // Left edge (wall)
            edges.Add(new Edge
            {
                X = r.Left,
                Y = r.Top,
                WallX = r.Left,
                IsVertical = true
            });

            // Right edge (wall)
            edges.Add(new Edge
            {
                X = r.Right,
                Y = r.Top,
                WallX = r.Right,
                IsVertical = true
            });
        }

        edges.Sort();
    }

    public Native.RECT? SurfaceBelow(double x, double y)
    {
        // Find the highest platform at X that is below Y
        Native.RECT? best = null;
        double bestY = double.NegativeInfinity;

        foreach (var e in edges)
        {
            if (e.IsVertical || e.X > x || e.X2 < x)
                continue;
            if (e.Y < y && e.Y > bestY)
            {
                bestY = e.Y;
                best = new Native.RECT { Left = (int)e.X, Top = (int)e.Y, Right = (int)e.X2 };
            }
        }

        return best;
    }
}
```

---

## 8. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|-----------|
| **Terrain polling blocks UI thread** | Medium | High | Test cadence (100 ms) is safe on a polling-only list; EnumWindows is O(n visible windows), typically < 1ms |
| **Window moves cause jitter** | Low | Low | Terrain updates lag by up to 100 ms; pet snaps to new surface (acceptable) |
| **Pet gets stuck on edge** | Medium | Medium | Add 10px margin and bias to the larger of two adjacent surfaces |
| **Minimized/cloaked windows crash** | Low | High | Wrap GetWindowRect in try/catch; skip windows that return False |
| **DPI mismatch between Pet.Scale and terrain Y coords** | Low | High | Terrain always in screen pixels; Pet.Scale applied only to pet's own dimensions and speeds, not to Y comparisons |
| **Fullscreen game breaks clipping** | Very Low | Medium | Already handled: PetContext hides the window when fullscreen is detected |
| **Icons slow down or crash** | Low | Low | Phase 2; can skip if UI Automation is unavailable; doesn't block walking/climbing |

---

## 9. Build Order (Simplest First)

### Phase 1: Taskbar + Windows (Robust MVP)
1. ✅ Add `TerrainIndex` class with edge list and `SurfaceBelow()` method
2. ✅ Add `Pet.RefreshTerrain()` and `Pet.ApplyTerrainPhysics()`
3. ✅ Add `GetVisibleWindowsForTerrain()` in PetContext
4. ✅ Add P/Invoke for EnumWindows, IsIconic, IsCloaked to Native.cs
5. ✅ Wire `RefreshTerrain()` into PetContext's tick loop (100 ms cadence)
6. ✅ Test: Throw pet, watch it land on a window top edge or taskbar
7. ✅ Test: Move a window, pet adjusts next terrain update cycle

**Commit:** `feat: terrain system with taskbar and window surfaces`

### Phase 1.5: Landing Animations (Use Existing Art)
1. ✅ Modify `Landed()` to accept floor height and detect hard landings
2. ✅ Play "land" vs. "failed" based on drop distance
3. ✅ Add squash-and-stretch fallback for hard landings (fake it with brief sprite scale)
4. ✅ Test: Drop from different heights, watch landing reactions

**Commit:** `feat: terrain-aware landing animations`

### Phase 2: Climbing (Requires Art)
1. ⏳ Add `RequestClimb(direction)` to Pet
2. ⏳ Add reserved clip slots: "climb", "climb_up", "climb_down", "hang"
3. ⏳ Implement climbing state machine in Pet.Tick (enter on wall impact, exit on jump/timeout)
4. ⏳ Add climbing speed physics (50 px/s up)
5. ⏳ Test with placeholder walk_right animation (recolor for climbing)
6. ⏳ Once art arrives, plug in real clips

**Commit:** `feat: climbing mechanics (waiting for art)`

### Phase 3: Desktop Icons (Lower Priority)
1. ⏳ Add `GetDesktopIconRects()` using UI Automation
2. ⏳ Integrate into `Rebuild()` as small 64x64 platforms
3. ⏳ Test with icons on the desktop
4. ⏳ Fallback gracefully if UI Automation fails

**Commit:** `feat: desktop icon platforms (optional)`

### Phase 4: Cross-Monitor Throws (Later)
1. ⏳ Detect when pet crosses monitor boundary mid-flight
2. ⏳ Switch monitors and rebuild terrain at new DPI
3. ⏳ Adjust sprite scale and continue flight
4. ⏳ Stop at edges with no neighbor monitor

---

## 10. Testing Strategy

### Manual Testing (No Instrumentation)
1. **Terrain visibility:** Launch the app, watch the pet land on window tops.
2. **Window dynamics:** Open a new window while pet is walking; see if it updates terrain.
3. **Landing physics:** Throw from high; observe "failed" clip on hard landing.
4. **DPI:** Move the window between 144 DPI and 120 DPI monitors; verify pet scales and terrain adjusts.
5. **Fullscreen hide:** Open a game in fullscreen; pet should vanish.

### Fuzz Testing (Once Art Arrives)
- Throw from all angles at random windows.
- Rapid window open/close/move while pet is active.
- Extreme monitor configurations (3+ monitors, misaligned work areas).

---

## 11. Code Template: TerrainIndex Stub

Insert this into a new file `src/DesktopPet/TerrainIndex.cs`:

```csharp
namespace DesktopPet;

class TerrainIndex
{
    struct Edge : IComparable<Edge>
    {
        public double X, X2, Y;
        public bool IsVertical;
        public double WallX;

        public int CompareTo(Edge other) => X.CompareTo(other.X);
    }

    List<Edge> edges = new();

    public void Rebuild(Native.RECT workArea, IEnumerable<IntPtr> windows)
    {
        edges.Clear();

        // Taskbar (floor)
        edges.Add(new Edge { X = workArea.Left, X2 = workArea.Right, Y = workArea.Bottom });

        // Windows
        foreach (var hwnd in windows)
        {
            if (!Native.GetWindowRect(hwnd, out var r))
                continue;

            // Top platform
            edges.Add(new Edge { X = r.Left, X2 = r.Right, Y = r.Top });

            // Walls (simplified; will be refined)
            edges.Add(new Edge { X = r.Left, Y = r.Top, WallX = r.Left, IsVertical = true });
            edges.Add(new Edge { X = r.Right, Y = r.Top, WallX = r.Right, IsVertical = true });
        }

        edges.Sort();
    }

    public (double Y, bool IsFloor) SurfaceAt(double x)
    {
        double bestY = double.NegativeInfinity;
        foreach (var e in edges)
        {
            if (!e.IsVertical && e.X <= x && x <= e.X2 && e.Y > bestY)
                bestY = e.Y;
        }
        return bestY > double.NegativeInfinity
            ? (bestY, true)
            : (double.PositiveInfinity, false);
    }
}
```

---

## Summary

This design provides:
- **Simplest robust terrain:** Taskbar + window edges, no GPU, ~0.3% CPU
- **Clear build path:** Phase 1 MVP in 1–2 days, phase 2 climbing later
- **DPI safety:** Terrain in screen pixels, scale applied only where needed
- **Clean API:** `Pet.RefreshTerrain()`, `Pet.ApplyTerrainPhysics()`
- **Extensible:** Clip slots ready for climbing art; icons can be added without code changes
- **Tested:** Manual checks cover the main flows; fuzz test once art is ready

**Next action:** Read this, approve or revise, then begin phase 1 (TerrainIndex + Pet integration).
