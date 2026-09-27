using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopPet;

/// <summary>
/// Pushes sprites to a layered window with UpdateLayeredWindow. The window is
/// resized to each sprite, so it never carries a large transparent area, and
/// pixels with zero alpha let clicks fall through to whatever is underneath.
/// </summary>
sealed class LayeredSurface : IDisposable
{
    IntPtr memDc, bitmap, oldBitmap, bits;
    int capW, capH;
    Sprite? current;
    bool failureLogged;

    public void Present(IntPtr hwnd, Sprite s, int x, int y)
    {
        if (!ReferenceEquals(s, current))
        {
            Ensure(s.W, s.H);
            for (int r = 0; r < s.H; r++)
                Marshal.Copy(s.Px, r * s.W * 4, bits + r * capW * 4, s.W * 4);
            current = s;
        }

        var dst = new Native.POINT(x, y);
        var size = new Native.SIZE(s.W, s.H);
        var src = new Native.POINT(0, 0);
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = Native.AC_SRC_ALPHA,
        };
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            if (!failureLogged)
            {
                failureLogged = true;
                Log.Info("GetDC for the screen failed; skipping this frame");
            }
            return;
        }
        try
        {
            if (!Native.UpdateLayeredWindow(hwnd, screen, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA)
                && !failureLogged)
            {
                failureLogged = true;
                Log.Info($"UpdateLayeredWindow failed, win32 error {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Forget what was drawn, so the next Present copies pixels again.</summary>
    public void Invalidate() => current = null;

    void Ensure(int w, int h)
    {
        if (memDc != IntPtr.Zero && w <= capW && h <= capH)
            return;
        int newW = Math.Max(w, capW), newH = Math.Max(h, capH);
        Free();
        capW = newW;
        capH = newH;

        var bmi = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = capW,
            biHeight = -capH, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        try
        {
            memDc = Native.CreateCompatibleDC(screen);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
        bitmap = Native.CreateDIBSection(memDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Free();
            throw new Win32Exception(err, $"CreateDIBSection {capW}x{capH} failed");
        }
        oldBitmap = Native.SelectObject(memDc, bitmap);
    }

    void Free()
    {
        if (memDc != IntPtr.Zero)
        {
            if (oldBitmap != IntPtr.Zero)
                Native.SelectObject(memDc, oldBitmap);
            Native.DeleteDC(memDc);
        }
        if (bitmap != IntPtr.Zero)
            Native.DeleteObject(bitmap);
        memDc = bitmap = oldBitmap = bits = IntPtr.Zero;
        current = null;
    }

    public void Dispose() => Free();
}
