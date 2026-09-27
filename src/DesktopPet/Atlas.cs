using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DesktopPet;

sealed class ClipDef
{
    public string Name { get; set; } = "";
    public string Row { get; set; } = "";
    public int[] Frames { get; set; } = [];
    public double Fps { get; set; } = 1;
    public bool Loop { get; set; }
    public double Duration => Frames.Length / Fps;
}

sealed class FrameDef
{
    public string Row { get; set; } = "";
    public int Index { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public int Ox { get; set; }
    public int Oy { get; set; }
    /// <summary>Draw this frame flipped left-to-right (e.g. walk_left from walk_right).</summary>
    public bool Mirror { get; set; }
}

sealed class Manifest
{
    public int Version { get; set; }
    public int Pad { get; set; }
    /// <summary>Display scale at 144 dpi and "Normal" size; 1 means one sheet pixel per screen pixel.</summary>
    public double Scale { get; set; } = 1.0;
    public List<FrameDef> Frames { get; set; } = new();
    public Dictionary<string, ClipDef> Clips { get; set; } = new();
}

/// <summary>
/// One frame ready to hand to UpdateLayeredWindow: premultiplied BGRA rows of
/// W*4 bytes. (X0, Y0) is where the top-left pixel sits relative to the
/// anchor, the ground point under the pet.
/// </summary>
sealed class Sprite
{
    public int W, H, X0, Y0;
    public byte[] Px = [];
}

/// <summary>
/// A sprite sheet plus its manifest. Only "idle" is required; every other
/// animation is optional, and <see cref="Clip"/> falls back to something
/// sensible when a pet does not have it.
/// </summary>
sealed class Atlas : IDisposable
{
    public const int LookDirections = 16;

    // Asked-for clip -> what to play instead, in order, before falling back to idle.
    static readonly Dictionary<string, string[]> Fallbacks = new()
    {
        ["wave"] = ["happy", "jump"],
        ["happy"] = ["jump", "wave"],
        ["shrug"] = ["think"],
        ["waiting"] = ["think"],
        ["review"] = ["think"],
        ["working"] = ["think"],
        ["held"] = ["jump"],
        ["failed"] = ["land"],
        ["walk_left"] = ["walk_right"],
    };

    readonly Bitmap sheet;
    readonly Manifest manifest;

    public IReadOnlyDictionary<string, ClipDef> Clips => manifest.Clips;
    public double BaseScale => manifest.Scale > 0 ? manifest.Scale : 1.0;
    public bool HasLook { get; }

    Atlas(Bitmap sheet, Manifest manifest, bool hasLook)
    {
        this.sheet = sheet;
        this.manifest = manifest;
        HasLook = hasLook;
    }

    /// <summary>True if the pet has this animation itself (not via a fallback).</summary>
    public bool Has(string name) => manifest.Clips.ContainsKey(name);

    /// <summary>The clip to play for <paramref name="name"/>; never null.</summary>
    public ClipDef Clip(string name)
    {
        if (manifest.Clips.TryGetValue(name, out var c))
            return c;
        if (Fallbacks.TryGetValue(name, out var alternatives))
        {
            foreach (string alt in alternatives)
            {
                if (manifest.Clips.TryGetValue(alt, out c))
                    return c;
            }
        }
        return manifest.Clips["idle"];
    }

    /// <summary>The RevBot sheet built by tools/build_atlas.py and embedded in the exe.</summary>
    public static Atlas Load()
    {
        var asm = typeof(Atlas).Assembly;
        Manifest m;
        using (var js = asm.GetManifestResourceStream("atlas.json") ?? throw new InvalidDataException("atlas.json is not embedded"))
            m = JsonSerializer.Deserialize<Manifest>(js, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("atlas.json is empty");

        Bitmap sheet;
        using (var ps = asm.GetManifestResourceStream("atlas.png") ?? throw new InvalidDataException("atlas.png is not embedded"))
        using (var decoded = new Bitmap(ps))
            sheet = new Bitmap(decoded); // detach from the stream; GDI+ keeps a stream-backed bitmap lazy
        return FromParts(sheet, m);
    }

    /// <summary>Validate a sheet + manifest and take ownership of the bitmap.</summary>
    public static Atlas FromParts(Bitmap sheet, Manifest m)
    {
        try
        {
            var have = m.Frames.Select(f => (f.Row, f.Index)).ToHashSet();
            foreach (var f in m.Frames)
            {
                if (f.W <= 0 || f.H <= 0 || f.X - m.Pad < 0 || f.Y - m.Pad < 0 ||
                    f.X + f.W + m.Pad > sheet.Width || f.Y + f.H + m.Pad > sheet.Height)
                    throw new InvalidDataException($"frame {f.Row}:{f.Index} lies outside the sheet");
            }
            foreach (var (name, clip) in m.Clips)
            {
                clip.Name = name;
                if (clip.Frames.Length == 0 || !(clip.Fps > 0) || clip.Frames.Any(i => !have.Contains((clip.Row, i))))
                    throw new InvalidDataException($"clip {name} references missing frames");
            }
            if (!m.Clips.ContainsKey("idle"))
                throw new InvalidDataException("the pet has no idle animation");

            // Walking left is walking right, flipped, unless the pet has its own.
            if (!m.Clips.ContainsKey("walk_left") && m.Clips.TryGetValue("walk_right", out var right))
            {
                const string row = "walk_left_mirrored";
                foreach (var f in m.Frames.Where(f => f.Row == right.Row && right.Frames.Contains(f.Index)).ToList())
                    m.Frames.Add(new FrameDef { Row = row, Index = f.Index, X = f.X, Y = f.Y, W = f.W, H = f.H, Ox = f.Ox, Oy = f.Oy, Mirror = !f.Mirror });
                m.Clips["walk_left"] = new ClipDef { Name = "walk_left", Row = row, Frames = right.Frames, Fps = right.Fps, Loop = right.Loop };
            }

            bool hasLook = Enumerable.Range(0, LookDirections).All(i => have.Contains(("look", i)));
            return new Atlas(sheet, m, hasLook);
        }
        catch
        {
            sheet.Dispose();
            throw;
        }
    }

    /// <summary>Every frame scaled by <paramref name="scale"/>, keyed by (row, index).</summary>
    public Dictionary<(string Row, int Index), Sprite> Build(double scale)
    {
        var result = new Dictionary<(string, int), Sprite>(manifest.Frames.Count);
        bool exact = Math.Abs(scale - 1.0) < 1e-9;
        foreach (var f in manifest.Frames)
        {
            var s = exact ? CopyExact(f) : Resample(f, scale);
            result[(f.Row, f.Index)] = f.Mirror ? Mirrored(s) : s;
        }
        return result;
    }

    Sprite CopyExact(FrameDef f)
    {
        var data = sheet.LockBits(new Rectangle(f.X, f.Y, f.W, f.H), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            return new Sprite { W = f.W, H = f.H, X0 = f.Ox, Y0 = f.Oy, Px = ReadPixels(data, f.W, f.H) };
        }
        finally
        {
            sheet.UnlockBits(data);
        }
    }

    Sprite Resample(FrameDef f, double scale)
    {
        // Include the transparent padding so the filter sees clean edges, and
        // put the output on a pixel grid aligned to the anchor.
        int pad = manifest.Pad;
        double left = (f.Ox - pad) * scale, top = (f.Oy - pad) * scale;
        double right = (f.Ox + f.W + pad) * scale, bottom = (f.Oy + f.H + pad) * scale;
        int x0 = (int)Math.Floor(left), y0 = (int)Math.Floor(top);
        int w = Math.Max(1, (int)Math.Ceiling(right) - x0);
        int h = Math.Max(1, (int)Math.Ceiling(bottom) - y0);

        // Resample from a standalone copy of the padded frame. Drawing a
        // sub-rectangle of the sheet lets the filter reach into neighbouring
        // frames when shrinking; a separate bitmap has no neighbours, and
        // TileFlipXY mirrors its transparent border at the edges.
        var srcRect = new Rectangle(f.X - pad, f.Y - pad, f.W + 2 * pad, f.H + 2 * pad);
        using var src = sheet.Clone(srcRect, PixelFormat.Format32bppArgb);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var attrs = new ImageAttributes())
        {
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float dx = (float)(left - x0), dy = (float)(top - y0);
            float dw = (float)(srcRect.Width * scale), dh = (float)(srcRect.Height * scale);
            var dest = new[] { new PointF(dx, dy), new PointF(dx + dw, dy), new PointF(dx, dy + dh) };
            g.DrawImage(src, dest, new RectangleF(0, 0, srcRect.Width, srcRect.Height), GraphicsUnit.Pixel, attrs);
        }
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            return new Sprite { W = w, H = h, X0 = x0, Y0 = y0, Px = ReadPixels(data, w, h) };
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>Flip left-to-right about the anchor: [X0, X0+W) becomes [-(X0+W), -X0).</summary>
    static Sprite Mirrored(Sprite s)
    {
        var px = new byte[s.Px.Length];
        int stride = s.W * 4;
        for (int y = 0; y < s.H; y++)
        {
            int row = y * stride;
            for (int x = 0; x < s.W; x++)
                Buffer.BlockCopy(s.Px, row + (s.W - 1 - x) * 4, px, row + x * 4, 4);
        }
        return new Sprite { W = s.W, H = s.H, X0 = -(s.X0 + s.W), Y0 = s.Y0, Px = px };
    }

    static byte[] ReadPixels(BitmapData data, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int r = 0; r < h; r++)
            Marshal.Copy(data.Scan0 + r * data.Stride, px, r * w * 4, w * 4);
        return px;
    }

    public void Dispose() => sheet.Dispose();
}
