using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Readback and analysis of the storm profile map (docs/storm-revamp-s4-sculpt-v2-design.md). Driven by tools/storm_profile_map_gpu_test.gd
/// and tools/storm_anvil_capture.gd. The texture lives on the main RenderingDevice, so the read runs on the render thread; call
/// RequestReadback, await a couple of frames until ReadbackReady, then Analyze / FindHero.
/// Heights texel: (body base, body top, canopy underside, canopy top) in metres; aux: (canopy coverage, mammatus, lifecycle, tower coverage).
/// </summary>
public partial class StormProfileMapProbe : RefCounted
{
    /// <summary>RING_CANOPY_TOP_MAX_METERS in RingStormShapes.glslinc.</summary>
    const float CanopyTopMaxMeters = 16500.0f;

    private const int Capacity = 1024; // RingStormProfileMap.gd CAPACITY
    private byte[]? _heights;
    private byte[]? _aux;
    private Vector2 _origin;
    private float _texel;
    private Vector2I _size;
    private double _circumference;
    private long _builds;

    public string ErrorText { get; private set; } = "";
    public bool ReadbackReady { get; private set; }
    /// <summary>Completed map builds when the last readback was taken.</summary>
    public long ReadbackBuilds => _builds;

    private static GodotObject? Driver(Node world)
    {
        var bridge = world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge");
        return bridge.GetStormProfileMapDriver();
    }

    /// <summary>Front-buffer state: valid, builds completed, origin, texel, used size.</summary>
    public Godot.Collections.Dictionary State(Node world)
    {
        var state = new Godot.Collections.Dictionary { ["valid"] = false, ["builds"] = 0L };
        GodotObject? driver = Driver(world);
        if (driver == null) return state;
        state["valid"] = driver.Get("valid").AsBool();
        state["builds"] = driver.Get("builds_completed").AsInt64();
        state["texel"] = driver.Get("front_texel").AsDouble();
        state["size"] = driver.Get("front_size").AsVector2I();
        state["origin"] = driver.Get("front_origin").AsVector2();
        return state;
    }

    /// <summary>Queues the readback of both layers of the front buffer on the render thread (a frame or two later ReadbackReady is true).</summary>
    public void RequestReadback(Node world)
    {
        ReadbackReady = false;
        GodotObject? driver = Driver(world);
        if (driver == null || !driver.Get("valid").AsBool()) { ErrorText = "storm profile map is not valid"; ReadbackReady = true; _heights = null; return; }
        Rid texture = driver.Get("texture").AsRid();
        int buffer = (int)driver.Get("front_buffer").AsInt64();
        _origin = driver.Get("front_origin").AsVector2();
        _texel = (float)driver.Get("front_texel").AsDouble();
        _size = driver.Get("front_size").AsVector2I();
        _circumference = driver.Get("front_circumference").AsDouble();
        _builds = driver.Get("builds_completed").AsInt64();
        byte[]? heights = null, aux = null;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            RenderingDevice rd = RenderingServer.GetRenderingDevice();
            heights = rd.TextureGetData(texture, (uint)(buffer * 2));
            aux = rd.TextureGetData(texture, (uint)(buffer * 2 + 1));
            _heights = heights; _aux = aux;
            ReadbackReady = true;
        }));
    }

    /// <summary>Order-independent hash of the used heights rectangle (determinism check between two bakes of one snapshot).</summary>
    public string HeightsHash()
    {
        if (_heights == null) return "none";
        ulong hash = 1469598103934665603UL;
        for (int y = 0; y < _size.Y; y++)
            for (int i = y * Capacity * 8; i < y * Capacity * 8 + _size.X * 8; i++)
                hash = (hash ^ _heights[i]) * 1099511628211UL;
        return hash.ToString("x16");
    }

    private float H(byte[] data, int x, int y, int channel)
        => (float)BitConverter.ToHalf(data, ((y * Capacity) + x) * 8 + channel * 2);

    /// <summary>
    /// Contract checks on the last readback: finite, nonempty intervals inside [floor - 1, ceiling + 1], and neighbouring texels with
    /// coverage above 0.05 differ by at most 1.5 km in base and top (continuity). S6: every body top is at most the 18 km cloud ceiling and every
    /// canopy top at most 16.5 km. Returns "PASS ..." or "FAIL ...".
    /// </summary>
    public string Analyze(double bandFloor, double bandCeiling)
    {
        if (_heights == null || _aux == null) return "FAIL no readback: " + ErrorText;
        int nonempty = 0, towerTexels = 0, canopyTexels = 0;
        float maxStepBody = 0.0f, maxStepCanopy = 0.0f, maxTop = 0.0f, maxCanopyTop = 0.0f;
        for (int y = 0; y < _size.Y; y++)
        {
            for (int x = 0; x < _size.X; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float v = H(_heights, x, y, c);
                    if (!float.IsFinite(v)) return $"FAIL non-finite height at ({x},{y}) channel {c}";
                }
                float tower = H(_aux, x, y, 3), canopy = H(_aux, x, y, 0);
                if (tower > 0.05f)
                {
                    towerTexels++;
                    float b = H(_heights, x, y, 0), t = H(_heights, x, y, 1);
                    if (t - b > 100.0f) { nonempty++; if (b < bandFloor - 1.0 || t > bandCeiling + 1.0) return $"FAIL body outside the band at ({x},{y}): {b}..{t}"; }
                    maxTop = Math.Max(maxTop, t);
                    if (t > RingCloudLayers.CeilingMeters + 1.0f) return $"FAIL body top above the 18 km ceiling at ({x},{y}): {t}";
                }
                if (canopy > 0.05f)
                {
                    canopyTexels++;
                    float b = H(_heights, x, y, 2), t = H(_heights, x, y, 3);
                    maxCanopyTop = Math.Max(maxCanopyTop, t);
                    if (t > CanopyTopMaxMeters + 1.0f) return $"FAIL canopy top above {CanopyTopMaxMeters:F0} m at ({x},{y}): {t}";
                    if (t - b > 100.0f) { nonempty++; if (b < bandFloor - 1.0 || t > bandCeiling + 1.0) return $"FAIL canopy outside the band at ({x},{y}): {b}..{t}"; }
                }
                foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= _size.X || ny >= _size.Y) continue;
                    if (tower > 0.05f && H(_aux, nx, ny, 3) > 0.05f)
                        maxStepBody = Math.Max(maxStepBody, Math.Abs(H(_heights, x, y, 0) - H(_heights, nx, ny, 0)));
                    if (canopy > 0.05f && H(_aux, nx, ny, 0) > 0.05f)
                        maxStepCanopy = Math.Max(maxStepCanopy, Math.Abs(H(_heights, x, y, 2) - H(_heights, nx, ny, 2)));
                }
            }
        }
        if (maxStepBody > 1500.0f) return $"FAIL body base discontinuity {maxStepBody:F0} m between neighbouring texels";
        if (maxStepCanopy > 3000.0f) return $"FAIL canopy underside discontinuity {maxStepCanopy:F0} m between neighbouring texels";
        if (towerTexels == 0) return "FAIL no tower texels (no storm in the map)";
        return $"PASS texel={_texel:F0} size={_size.X}x{_size.Y} tower_texels={towerTexels} canopy_texels={canopyTexels} intervals={nonempty} max_base_step={maxStepBody:F0} max_underside_step={maxStepCanopy:F0} max_top={maxTop:F0} max_canopy_top={maxCanopyTop:F0}";
    }

    /// <summary>Writes grayscale previews of the last readback: body / canopy thickness, body top, canopy underside, aux coverage (debug).</summary>
    public bool SavePreviews(string directory)
    {
        if (_heights == null || _aux == null) return false;
        System.IO.Directory.CreateDirectory(directory);
        string[] names = { "body_thickness", "canopy_thickness", "body_top", "canopy_underside", "canopy_cover", "tower_cover" };
        foreach (string name in names)
        {
            var image = Image.CreateEmpty(_size.X, _size.Y, false, Image.Format.Rgb8);
            for (int y = 0; y < _size.Y; y++)
                for (int x = 0; x < _size.X; x++)
                {
                    float v = name switch
                    {
                        "body_thickness" => (H(_heights, x, y, 1) - H(_heights, x, y, 0)) / 16000.0f,
                        "canopy_thickness" => (H(_heights, x, y, 3) - H(_heights, x, y, 2)) / 6000.0f,
                        "body_top" => H(_heights, x, y, 1) / RingCloudLayers.CeilingMeters,
                        "canopy_underside" => H(_heights, x, y, 2) / RingCloudLayers.CeilingMeters,
                        "canopy_cover" => H(_aux, x, y, 0),
                        _ => H(_aux, x, y, 3),
                    };
                    v = Mathf.Clamp(v, 0.0f, 1.0f);
                    image.SetPixel(x, y, new Color(v, v, v));
                }
            image.SavePng(System.IO.Path.Combine(directory, name + ".png"));
        }
        return true;
    }

    private Vector2 TexelWorld(int x, int y) => new(_origin.X + (x + 0.5f) * _texel, _origin.Y + (y + 0.5f) * _texel);

    private float Wrap(float delta)
    {
        if (_circumference <= 1.0) return delta;
        return (float)(delta - _circumference * Math.Floor(delta / _circumference + 0.5));
    }

    /// <summary>
    /// Hero cell: among tower columns within <paramref name="searchRadius"/> metres of (centreAround, centreAcross) (canonical metres
    /// relative to the camera) pick the one with the greatest downwind canopy reach. Returns found, around/across offsets relative to
    /// the camera, core radius, downwind reach, upwind lip and canopy width (all metres).
    /// </summary>
    public Godot.Collections.Dictionary FindHero(Node world, Vector2 wind, double centreAround, double centreAcross, double searchRadius)
    {
        var result = new Godot.Collections.Dictionary { ["found"] = false };
        if (_heights == null || _aux == null) return result;
        var grid = world.GetNode<VoxelChunkGridManager>("ChunkGridManager");
        _circumference = grid.GetCanonicalRingCircumferenceMeters64();
        Vector2 cross = new(-wind.Y, wind.X);
        const double baseAround = 8150667.255809009, baseAcross = 119474.33742621914;
        double best = -1.0;
        for (int y = 1; y < _size.Y - 1; y++)
        {
            for (int x = 1; x < _size.X - 1; x++)
            {
                if (H(_aux, x, y, 3) < 0.95f || H(_heights, x, y, 1) - H(_heights, x, y, 0) < 9000.0f) continue; // a tower, not the deck
                float top = H(_heights, x, y, 1);
                bool localMax = true;
                for (int j = -1; j <= 1 && localMax; j++)
                    for (int i = -1; i <= 1; i++)
                        if ((i != 0 || j != 0) && H(_heights, x + i, y + j, 1) > top) { localMax = false; break; }
                if (!localMax) continue;
                Vector2 world2 = TexelWorld(x, y);
                double offA = Wrap((float)(world2.X - baseAround));
                double offC = world2.Y - baseAcross;
                if (new Vector2((float)(offA - centreAround), (float)(offC - centreAcross)).Length() > searchRadius) continue;
                // Rays along the downwind / upwind / across axes through the core: last texel with a canopy at least 600 m thick.
                float reach = Ray(x, y, wind), lip = Ray(x, y, -wind), widthLeft = Ray(x, y, cross), widthRight = Ray(x, y, -cross);
                // Core radius: where the body thickness falls to half its core value, along the across axis.
                float coreThickness = H(_heights, x, y, 1) - H(_heights, x, y, 0);
                float radius = 0.0f;
                for (float r = _texel; r < 20000.0f; r += _texel)
                {
                    Vector2 p = new Vector2(x, y) + cross * (r / _texel);
                    int px = (int)MathF.Round(p.X), py = (int)MathF.Round(p.Y);
                    if (px < 0 || py < 0 || px >= _size.X || py >= _size.Y) break;
                    radius = r;
                    if (H(_heights, px, py, 1) - H(_heights, px, py, 0) < 0.5f * coreThickness) break;
                }
                if (reach > best)
                {
                    best = reach;
                    result["found"] = true;
                    result["around"] = offA; result["across"] = offC;
                    result["core_radius"] = (double)radius; result["reach"] = (double)reach; result["lip"] = (double)lip;
                    result["width"] = (double)(widthLeft + widthRight);
                    result["lifecycle"] = (double)H(_aux, x, y, 2);
                }
            }
        }
        return result;
    }

    private float Ray(int x, int y, Vector2 direction)
    {
        float last = 0.0f;
        for (float r = 0.0f; r < 60000.0f; r += _texel * 0.5f)
        {
            Vector2 p = new Vector2(x, y) + direction * (r / _texel);
            int px = (int)MathF.Round(p.X), py = (int)MathF.Round(p.Y);
            if (px < 0 || py < 0 || px >= _size.X || py >= _size.Y) break;
            if (H(_heights!, px, py, 3) - H(_heights!, px, py, 2) >= 600.0f && H(_aux!, px, py, 0) > 0.05f) last = r;
        }
        return last;
    }
}
