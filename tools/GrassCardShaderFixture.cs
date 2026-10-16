using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tests;

/// <summary>
/// Synthetic GPU fixture helper for the grass-card / hero-blade shaders (grass-cards plan step S4). GDScript
/// (tools/grass_card_color_match_gpu_test.gd) owns the Terrain3D scene; this class owns the pieces that need C# assets:
/// material construction from the single RingGrassCardMaterialConfig, a deterministic stand-in placer (a jittered lattice cut
/// by the real RingGrassCardCurve, until the S3/S5 tile builder feeds the layer), the Terrain3D ring-frame maths, and CIEDE2000.
/// The ground is an analytic plane h = base + slopeX*x + slopeZ*z in Terrain3D local metres.
/// </summary>
public partial class GrassCardShaderFixture : RefCounted
{
    private const float ChunkMeters = 32f;

    // ---- Terrain3D ring frame (read back from the real terrain material) ----
    private double _radius = 25000, _anchorHeight;
    private Vector3 _frameOrigin;
    private Basis _frameBasis = Basis.Identity;
    private float _anchorAround, _anchorAcross, _acrossDirection = 1f;
    // ---- analytic ground ----
    private float _baseHeight, _slopeX, _slopeZ;
    private Vector4 _biomeA = Vector4.Zero, _biomeB = Vector4.Zero, _biomeC = Vector4.Zero;
    private float _biomeX0 = 1e9f, _biomeX1 = 2e9f, _biomeX2 = 3e9f, _biomeX3 = 4e9f;

    private RingGrassCardAssetSet _assets = null!;
    private RingGrassCardMaterialConfig _config = new();
    public ShaderMaterial CardMaterial { get; private set; } = null!;
    public ShaderMaterial HeroMaterial { get; private set; } = null!;
    public int CardInstances { get; private set; }
    public int LowInstances { get; private set; }
    public int HeroInstances { get; private set; }
    public int ChunkCount { get; private set; }

    public void InitAssets()
    {
        _assets = RingGrassCardAssets.GetBlocking(RingGrassCardAtlasParameters.VariantD);
        _config = new RingGrassCardMaterialConfig();
        CardMaterial = _config.CreateCardMaterial(_assets.AtlasTexture);
        HeroMaterial = _config.CreateHeroMaterial();
    }

    /// <summary>"production" (variant D colour), "neutral" (no AO / tip / jitter / pull / bend, so the card should equal the ground),
    /// "mask" (neutral but strongly recoloured, used only to find the pixels the grass covers).</summary>
    public void SetMode(string mode)
    {
        var c = new RingGrassCardMaterialConfig { Curve = _config.Curve };
        if (mode != "production")
        {
            c.GreenPull = 0f; c.RootAo = 1f; c.TipLight = 0f; c.TipTint = Vector3.One; c.BladeValueJitter = 0f; c.BladeHueJitter = 0f;
            c.DryFraction = -0.2f; c.TuftValueJitter = 0f; c.RootDark = 1f; c.TipGain = 1f; c.TipShift = Vector3.One; c.ColorVariation = 0f;
            c.NormalOut = 0f; c.NormalFace = 0f; c.Backlight = 0f;
            if (mode == "mask") { c.DryFraction = 1f; c.GreenPull = 1f; }
        }
        _config = c;
        _config.ApplyCard(CardMaterial, _assets.AtlasTexture);
        _config.ApplyHero(HeroMaterial);
    }

    public void SetGreenPull(float value)
    {
        _config.GreenPull = value;
        _config.ApplyCard(CardMaterial);
        _config.ApplyHero(HeroMaterial);
    }

    public void SetHeroThinning(bool enabled)
    {
        var p = _config.Curve;
        _config.Curve = p with { HeroThinMinimum = enabled ? .4f : 1f };
        _config.ApplyCard(CardMaterial);
        _config.ApplyHero(HeroMaterial);
    }

    public void SetFrame(double radius, double anchorHeight, Vector3 frameOrigin, Basis frameBasis, float anchorAround, float anchorAcross, float acrossDirection)
    {
        _radius = radius; _anchorHeight = anchorHeight; _frameOrigin = frameOrigin; _frameBasis = frameBasis;
        _anchorAround = anchorAround; _anchorAcross = anchorAcross; _acrossDirection = acrossDirection;
    }

    public void SetGround(float baseHeight, float slopeX, float slopeZ) { _baseHeight = baseHeight; _slopeX = slopeX; _slopeZ = slopeZ; }

    /// <summary>Biome weights (desert, forest, tundra, alpine) blend from A to B with smoothstep(x0, x1, x) in Terrain3D local x.</summary>
    public void SetBiome(Color a, Color b, float x0, float x1)
    {
        _biomeA = new Vector4(a.R, a.G, a.B, a.A); _biomeB = new Vector4(b.R, b.G, b.B, b.A); _biomeX0 = x0; _biomeX1 = x1;
    }

    /// <summary>Second blend, from B to C over smoothstep(x2, x3, x).</summary>
    public void SetBiomeSecond(Color c, float x2, float x3)
    {
        _biomeC = new Vector4(c.R, c.G, c.B, c.A); _biomeX2 = x2; _biomeX3 = x3;
    }

    public float Height(float x, float z) => _baseHeight + _slopeX * x + _slopeZ * z;

    /// <summary>The biome weights the test writes into the Terrain3D colour map at a grid node (8-bit quantised, as stored).</summary>
    public Color BiomeColor(float x)
    {
        float t = Smooth(_biomeX0, _biomeX1, x);
        Vector4 v = _biomeA.Lerp(_biomeB, t).Lerp(_biomeC, Smooth(_biomeX2, _biomeX3, x));
        return new Color(Q(v.X), Q(v.Y), Q(v.Z), Q(v.W));
    }

    private static float Q(float v) => MathF.Round(Math.Clamp(v, 0f, 1f) * 255f) / 255f;
    private static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3f - 2f * t); }

    /// <summary>Curved ring-frame world position of local (x, z) on the analytic ground (identical to the terrain vertex stage).</summary>
    public Vector3 GroundWorld(float x, float z) => World(x, Height(x, z), z);

    private Vector3 World(double x, double h, double z)
    {
        double theta = x / _radius;
        double rah = _radius - (h + _anchorHeight);
        double s = Math.Sin(theta), half = Math.Sin(theta * .5);
        var local = new Vector3((float)(rah * s), (float)(h + 2.0 * rah * half * half), (float)z);
        return _frameOrigin + _frameBasis * local;
    }

    public Vector3 GroundNormal(float x, float z)
    {
        double theta = x / _radius;
        var tangent = _frameBasis * new Vector3((float)Math.Cos(theta), (float)Math.Sin(theta), 0f);
        var up = _frameBasis * new Vector3((float)-Math.Sin(theta), (float)Math.Cos(theta), 0f);
        var across = _frameBasis * new Vector3(0f, 0f, 1f);
        var np = new Vector3(-_slopeX, 1f, -_slopeZ).Normalized();
        return (tangent * np.X + up * np.Y + across * np.Z).Normalized();
    }

    private Vector3 GroundTangent(float x)
    {
        double theta = x / _radius;
        return (_frameBasis * new Vector3((float)Math.Cos(theta), (float)Math.Sin(theta), 0f)).Normalized();
    }

    /// <summary>Weights at a local point, interpolated bilinearly between the 2 m terrain grid nodes (as the terrain shader does).</summary>
    private Vector4 BiomeWeights(float x)
    {
        float gx = MathF.Floor(x / 2f) * 2f;
        float f = (x - gx) / 2f;
        Color a = BiomeColor(gx), b = BiomeColor(gx + 2f);
        return new Vector4(Mathf.Lerp(a.R, b.R, f), Mathf.Lerp(a.G, b.G, f), Mathf.Lerp(a.B, b.B, f), Mathf.Lerp(a.A, b.A, f));
    }

    // ---- stand-in placer ----
    private static uint Hash(uint a, uint b, uint c)
    {
        unchecked
        {
            uint h = a * 0x9E3779B1u ^ b * 0x85EBCA77u ^ c * 0xC2B2AE3Du;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return h;
        }
    }
    private static float Unit(uint h) => (h & 0xFFFFFFu) / 16777216f;
    private static float ValueNoise(float x, float z, uint seed)
    {
        int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
        float fx = x - ix, fz = z - iz; fx = fx * fx * (3f - 2f * fx); fz = fz * fz * (3f - 2f * fz);
        float a = Unit(Hash((uint)ix, (uint)iz, seed)), b = Unit(Hash((uint)(ix + 1), (uint)iz, seed));
        float c = Unit(Hash((uint)ix, (uint)(iz + 1), seed)), d = Unit(Hash((uint)(ix + 1), (uint)(iz + 1), seed));
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
    }

    private sealed class Chunk
    {
        public readonly List<float> Buffer = new(8192);
        public Vector3 Min = new(float.MaxValue, float.MaxValue, float.MaxValue), Max = new(float.MinValue, float.MinValue, float.MinValue);
        public int Count;
    }

    private void Emit(Dictionary<(int, int), Chunk> chunks, float x, float z, float yaw, float width, float height, float lean, uint hx, int kind, float rank, bool hero)
    {
        Vector3 pos = GroundWorld(x, z);
        Vector3 up = GroundNormal(x, z);
        Vector3 tx = GroundTangent(x);
        tx = (tx - up * tx.Dot(up)).Normalized();
        Vector3 tz = tx.Cross(up);   // so that cross(Z, X) = up
        float cs = MathF.Cos(yaw), sn = MathF.Sin(yaw);
        Vector3 bx = (tx * cs - tz * sn) * width;
        Vector3 bz = (tx * sn + tz * cs) * width;
        Vector3 by = up * height;
        if (!hero)
        {
            float lx = (Unit(Hash(hx, 17u, 18u)) - .5f) * 2f * lean * height, lz = (Unit(Hash(hx, 19u, 20u)) - .5f) * 2f * lean * height;
            by += bx.Normalized() * lx + bz.Normalized() * lz;
        }
        Vector4 w = BiomeWeights(x);
        float variant = (int)(Unit(Hash(hx, 11u, 12u)) * 4f) % 4;
        float custom = hero ? 0f : kind * 4f + variant + rank * .999f;
        var key = ((int)MathF.Floor(x / ChunkMeters), (int)MathF.Floor(z / ChunkMeters));
        if (!chunks.TryGetValue(key, out Chunk? chunk)) chunks[key] = chunk = new Chunk();
        List<float> b = chunk.Buffer;
        b.Add(bx.X); b.Add(by.X); b.Add(bz.X); b.Add(pos.X);
        b.Add(bx.Y); b.Add(by.Y); b.Add(bz.Y); b.Add(pos.Y);
        b.Add(bx.Z); b.Add(by.Z); b.Add(bz.Z); b.Add(pos.Z);
        b.Add(w.X); b.Add(w.Y); b.Add(w.Z); b.Add(w.W);
        b.Add(custom); b.Add(Unit(Hash(hx, 15u, 16u))); b.Add(Unit(Hash(hx, 23u, 24u))); b.Add(Unit(Hash(hx, 25u, 26u)));
        chunk.Min = chunk.Min.Min(pos); chunk.Max = chunk.Max.Max(pos);
        chunk.Count++;
    }

    /// <summary>Builds card (+ low layer) and hero renderers around a camera at Terrain3D local (camX, camZ), eye height above ground.
    /// Instances are kept exactly as the real curve would accept them at that camera distance, so the field is static for the pose.</summary>
    public Node3D BuildField(float camX, float camZ, float eyeHeight, float reach, bool cards, bool hero, int seed)
    {
        var root = new Node3D { Name = "GrassField" };
        RingGrassCardCurveParameters p = _config.Curve with { ReachMeters = reach };
        CardInstances = LowInstances = HeroInstances = 0;
        var chunks = new Dictionary<(int, int), Chunk>();
        uint s0 = (uint)seed;
        if (cards)
        {
            float spacing = 1f / MathF.Sqrt(p.NearDensity);
            int cells = (int)MathF.Ceiling(reach / spacing);
            int i0 = (int)MathF.Floor(camX / spacing) - cells, i1 = i0 + 2 * cells, j0 = (int)MathF.Floor(camZ / spacing) - cells, j1 = j0 + 2 * cells;
            float reach2 = reach * reach;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    uint hx = Hash((uint)i, (uint)j, s0);
                    float x = (i + Unit(hx)) * spacing, z = (j + Unit(Hash(hx, 1u, 2u))) * spacing;
                    float dx = x - camX, dz = z - camZ;
                    float d2 = dx * dx + dz * dz;
                    if (d2 >= reach2) continue;
                    float d = MathF.Sqrt(d2 + eyeHeight * eyeHeight);
                    float rank = Unit(Hash(hx, 3u, 4u));
                    float keep = RingGrassCardCurve.Keep(p, d);
                    if (rank >= keep || RingGrassCardCurve.GrowIn(keep, rank) < RingGrassCardCurve.MinimumGrowIn) continue;
                    float n = ValueNoise(x / 70f, z / 70f, 991u) * .65f + ValueNoise(x / 23f, z / 23f, 313u) * .35f;
                    bool tall = Unit(Hash(hx, 5u, 6u)) < Smooth(.42f, .58f, n);
                    float h = (tall ? .6f : .3f) * (.85f + .3f * Unit(Hash(hx, 7u, 8u)));
                    float w = .45f + (tall ? .6f : .3f) * 1.1f;
                    Emit(chunks, x, z, Unit(Hash(hx, 9u, 10u)) * Mathf.Tau, w, h, .12f, hx, 0, rank, false);
                    CardInstances++;
                }
            // Low upright layer: own lattice, kind 1, fully kept to 8 m, gone by 22 m.
            float lowSpacing = 1f / MathF.Sqrt(18f);
            int lc = (int)MathF.Ceiling(p.LowLayerEndMeters / lowSpacing);
            int li0 = (int)MathF.Floor(camX / lowSpacing) - lc, lj0 = (int)MathF.Floor(camZ / lowSpacing) - lc;
            for (int j = lj0; j <= lj0 + 2 * lc; j++)
                for (int i = li0; i <= li0 + 2 * lc; i++)
                {
                    uint hx = Hash((uint)i, (uint)j, s0 + 4242u);
                    float x = (i + Unit(hx)) * lowSpacing, z = (j + Unit(Hash(hx, 1u, 2u))) * lowSpacing;
                    float dx = x - camX, dz = z - camZ;
                    float d = MathF.Sqrt(dx * dx + dz * dz + eyeHeight * eyeHeight);
                    if (d >= p.LowLayerEndMeters) continue;
                    float rank = Unit(Hash(hx, 3u, 4u));
                    if (RingGrassCardCurve.LowScale(p, rank, d) <= 0f) continue;
                    float h = .11f + .09f * Unit(Hash(hx, 7u, 8u)), w = .30f + .18f * Unit(Hash(hx, 21u, 22u));
                    Emit(chunks, x, z, Unit(Hash(hx, 9u, 10u)) * Mathf.Tau, w, h, .12f, hx, 1, rank, false);
                    LowInstances++;
                }
        }
        ChunkCount = chunks.Count;
        foreach (var kv in chunks) root.AddChild(MakeRenderer(kv.Key, kv.Value, _assets.CardArrayMesh, CardMaterial, 3.5f));

        if (hero)
        {
            var heroChunks = new Dictionary<(int, int), Chunk>();
            // 50 tufts / m2 on a jittered lattice inside the hero ring (6 m fade + 1.4 m dither + margin).
            float cell = 4f, density = 50f, heroReach = _config.HeroEndMeters + _config.HeroDitherMeters + 1f;
            int n = (int)MathF.Ceiling(MathF.Sqrt(density * cell * cell));
            float step = cell / n;
            int cx0 = (int)MathF.Floor((camX - heroReach) / cell), cx1 = (int)MathF.Floor((camX + heroReach) / cell);
            int cz0 = (int)MathF.Floor((camZ - heroReach) / cell), cz1 = (int)MathF.Floor((camZ + heroReach) / cell);
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                    for (int j = 0; j < n; j++)
                        for (int i = 0; i < n; i++)
                        {
                            uint hx = Hash((uint)(cx * n + i), (uint)(cz * n + j), s0 + 77u);
                            float x = cx * cell + (i + Unit(hx)) * step, z = cz * cell + (j + Unit(Hash(hx, 1u, 2u))) * step;
                            float dx = x - camX, dz = z - camZ;
                            if (dx * dx + dz * dz > heroReach * heroReach) continue;
                            float nz = ValueNoise(x / 70f, z / 70f, 991u) * .65f + ValueNoise(x / 23f, z / 23f, 313u) * .35f;
                            bool tall = Unit(Hash(hx, 5u, 6u)) < Smooth(.42f, .58f, nz);
                            float height = (tall ? .6f : .3f) * (.85f + .3f * Unit(Hash(hx, 7u, 8u))) * .9f;
                            float sx = height / RingGrassHeroTuftMeshFactory.UnitHeight * (.9f + .2f * Unit(Hash(hx, 21u, 22u)));
                            Emit(heroChunks, x, z, Unit(Hash(hx, 9u, 10u)) * Mathf.Tau, sx, height, 0f, hx, 2, 0f, true);
                            HeroInstances++;
                        }
            foreach (var kv in heroChunks) root.AddChild(MakeRenderer(kv.Key, kv.Value, _assets.HeroArrayMesh, HeroMaterial, 1.5f));
        }
        return root;
    }

    private MultiMeshInstance3D MakeRenderer((int, int) key, Chunk chunk, Mesh mesh, ShaderMaterial material, float pad)
    {
        var multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = mesh, InstanceCount = chunk.Count };
        multimesh.Buffer = chunk.Buffer.ToArray();
        var instance = new MultiMeshInstance3D { Multimesh = multimesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        instance.CustomAabb = new Aabb(chunk.Min - new Vector3(pad, pad, pad), chunk.Max - chunk.Min + new Vector3(pad, pad, pad) * 2f + new Vector3(0, 1f, 0));
        // Tile origin: the chunk centre, in canonical (around, across) metres like the layer's gust mapping.
        float cx = (key.Item1 + .5f) * ChunkMeters, cz = (key.Item2 + .5f) * ChunkMeters;
        Vector3 origin = GroundWorld(cx, cz);
        double h = Height(cx, cz);
        double th = cx / _radius, rah = _radius - (h + _anchorHeight);
        // Jacobian columns d(world)/d(x, h, z); its inverse rows are the dual covectors (metres of canonical around/across per world metre).
        var ex = _frameBasis * new Vector3((float)(rah * Math.Cos(th) / _radius + 0.0), (float)(rah * Math.Sin(th) / _radius), 0f);
        var eh = _frameBasis * new Vector3((float)-Math.Sin(th), (float)Math.Cos(th), 0f);
        var ez = _frameBasis * new Vector3(0f, 0f, 1f);
        Basis inverse = new Basis(ex, eh, ez).Inverse();
        Vector3 around = inverse.Row0, across = inverse.Row2 * _acrossDirection;
        float canonicalAround = _anchorAround + cx, canonicalAcross = _anchorAcross + cz * _acrossDirection;
        float u = (float)(Mod(canonicalAround, 5.0) * .2), v = (float)(Mod(canonicalAcross, 5.0) * .2);
        instance.SetInstanceShaderParameter("grass_gust_origin_world", origin);
        instance.SetInstanceShaderParameter("grass_gust_around_world", new Vector4(around.X, around.Y, around.Z, 1f / 48f));
        instance.SetInstanceShaderParameter("grass_gust_across_world", new Vector4(across.X, across.Y, across.Z, 1f / 48f));
        instance.SetInstanceShaderParameter("grass_gust_origin_uv", Vector2.Zero);
        instance.SetInstanceShaderParameter("grass_terrain_origin_uv", new Vector4(u, v, (float)(canonicalAround / (2.0 * Math.PI * _radius)), (float)(canonicalAcross / 20000.0)));
        instance.SetInstanceShaderParameter("grass_modifier_layer", -1);
        return instance;
    }

    private static double Mod(double v, double m) { double r = v % m; return r < 0 ? r + m : r; }

    /// <summary>Registers the card and hero materials as followers of a RingTerrain3DSurface (lighting, atmosphere, biome samplers).</summary>
    public void RegisterFollowers(Node surface)
    {
        var terrain = (RingworldTechDemo.Terrain.Surface.RingTerrain3DSurface)surface;
        terrain.RegisterSurfaceFollower(CardMaterial, RingGrassCardMaterialConfig.SurfaceFollowerNames);
        terrain.RegisterSurfaceFollower(HeroMaterial, RingGrassCardMaterialConfig.SurfaceFollowerNames);
    }

    /// <summary>Shader uniform names present in both the Terrain3D surface shader and a grass shader (used to audit SurfaceFollowerNames).</summary>
    public string[] SharedUniformNames(Shader terrain, Shader grass)
    {
        var names = new HashSet<string>();
        foreach (var entry in terrain.GetShaderUniformList(false)) names.Add(entry.AsGodotDictionary()["name"].AsString());
        var shared = new List<string>();
        foreach (var entry in grass.GetShaderUniformList(false))
        {
            string n = entry.AsGodotDictionary()["name"].AsString();
            if (names.Contains(n)) shared.Add(n);
        }
        shared.Sort(StringComparer.Ordinal);
        return shared.ToArray();
    }

    /// <summary>Names in the shared set that SurfaceFollowerNames does not forward.</summary>
    public string[] UnforwardedShared(string[] shared)
    {
        var missing = new List<string>();
        foreach (string n in shared) if (!RingGrassCardMaterialConfig.SurfaceFollowerNames.Contains(n)) missing.Add(n);
        return missing.ToArray();
    }

    // ---- colour comparison ----
    private static float Lin(byte c) { float v = c / 255f; return v <= .04045f ? v / 12.92f : MathF.Pow((v + .055f) / 1.055f, 2.4f); }

    public static Vector3 ToLab(Color srgb) => LinearToLab(Lin((byte)Math.Round(srgb.R * 255f)), Lin((byte)Math.Round(srgb.G * 255f)), Lin((byte)Math.Round(srgb.B * 255f)));

    private static Vector3 LinearToLab(float r, float g, float b)
    {
        float x = (.4124564f * r + .3575761f * g + .1804375f * b) / .95047f;
        float y = .2126729f * r + .7151522f * g + .0721750f * b;
        float z = (.0193339f * r + .1191920f * g + .9503041f * b) / 1.08883f;
        static float F(float t) => t > .008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
        float fx = F(x), fy = F(y), fz = F(z);
        return new Vector3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
    }

    public static float DeltaE2000(Vector3 a, Vector3 b)
    {
        double L1 = a.X, a1 = a.Y, b1 = a.Z, L2 = b.X, a2 = b.Y, b2 = b.Z;
        double C1 = Math.Sqrt(a1 * a1 + b1 * b1), C2 = Math.Sqrt(a2 * a2 + b2 * b2), Cm = (C1 + C2) / 2;
        double G = .5 * (1 - Math.Sqrt(Math.Pow(Cm, 7) / (Math.Pow(Cm, 7) + Math.Pow(25, 7))));
        double ap1 = (1 + G) * a1, ap2 = (1 + G) * a2;
        double Cp1 = Math.Sqrt(ap1 * ap1 + b1 * b1), Cp2 = Math.Sqrt(ap2 * ap2 + b2 * b2);
        double hp1 = Math.Atan2(b1, ap1) * 180 / Math.PI; if (hp1 < 0) hp1 += 360;
        double hp2 = Math.Atan2(b2, ap2) * 180 / Math.PI; if (hp2 < 0) hp2 += 360;
        double dL = L2 - L1, dC = Cp2 - Cp1, dh;
        if (Cp1 * Cp2 == 0) dh = 0; else { dh = hp2 - hp1; if (dh > 180) dh -= 360; else if (dh < -180) dh += 360; }
        double dH = 2 * Math.Sqrt(Cp1 * Cp2) * Math.Sin(dh * Math.PI / 360);
        double Lpm = (L1 + L2) / 2, Cpm = (Cp1 + Cp2) / 2, hpm;
        if (Cp1 * Cp2 == 0) hpm = hp1 + hp2; else { hpm = Math.Abs(hp1 - hp2) <= 180 ? (hp1 + hp2) / 2 : (hp1 + hp2 < 360 ? (hp1 + hp2 + 360) / 2 : (hp1 + hp2 - 360) / 2); }
        double T = 1 - .17 * Math.Cos((hpm - 30) * Math.PI / 180) + .24 * Math.Cos(2 * hpm * Math.PI / 180) + .32 * Math.Cos((3 * hpm + 6) * Math.PI / 180) - .20 * Math.Cos((4 * hpm - 63) * Math.PI / 180);
        double dTheta = 30 * Math.Exp(-Math.Pow((hpm - 275) / 25, 2));
        double Rc = 2 * Math.Sqrt(Math.Pow(Cpm, 7) / (Math.Pow(Cpm, 7) + Math.Pow(25, 7)));
        double Sl = 1 + .015 * Math.Pow(Lpm - 50, 2) / Math.Sqrt(20 + Math.Pow(Lpm - 50, 2));
        double Sc = 1 + .045 * Cpm, Sh = 1 + .015 * Cpm * T;
        double Rt = -Math.Sin(2 * dTheta * Math.PI / 180) * Rc;
        return (float)Math.Sqrt(Math.Pow(dL / Sl, 2) + Math.Pow(dC / Sc, 2) + Math.Pow(dH / Sh, 2) + Rt * (dC / Sc) * (dH / Sh));
    }

    private static Vector3 LabAt(byte[] data, int offset) => LinearToLab(Lin(data[offset]), Lin(data[offset + 1]), Lin(data[offset + 2]));

    /// <summary>Per-pixel CIEDE2000 between <paramref name="shot"/> (grass, neutral colour) and <paramref name="ground"/> (grass hidden) over the
    /// pixels <paramref name="mask"/> (grass, strongly recoloured) differs from the ground by more than <paramref name="threshold"/> (0..255).</summary>
    public Godot.Collections.Dictionary CompareMasked(Image shot, Image ground, Image mask, Rect2I region, int threshold)
    {
        shot.Convert(Image.Format.Rgba8); ground.Convert(Image.Format.Rgba8); mask.Convert(Image.Format.Rgba8);
        byte[] s = shot.GetData(), g = ground.GetData(), m = mask.GetData();
        int w = shot.GetWidth();
        var deltas = new List<float>();
        double lumShot = 0, lumGround = 0;
        for (int y = region.Position.Y; y < region.End.Y; y++)
            for (int x = region.Position.X; x < region.End.X; x++)
            {
                int o = (y * w + x) * 4;
                int md = Math.Max(Math.Abs(m[o] - g[o]), Math.Max(Math.Abs(m[o + 1] - g[o + 1]), Math.Abs(m[o + 2] - g[o + 2])));
                if (md <= threshold) continue;
                Vector3 ls = LabAt(s, o), lg = LabAt(g, o);
                deltas.Add(DeltaE2000(ls, lg));
                lumShot += ls.X; lumGround += lg.X;
            }
        var result = new Godot.Collections.Dictionary { ["count"] = deltas.Count };
        if (deltas.Count == 0) { result["mean"] = -1.0; result["p95"] = -1.0; result["max"] = -1.0; result["mean_dl"] = 0.0; return result; }
        deltas.Sort();
        double sum = 0; foreach (float d in deltas) sum += d;
        result["mean"] = sum / deltas.Count;
        result["median"] = deltas[deltas.Count / 2];
        result["p95"] = deltas[(int)(deltas.Count * .95)];
        result["max"] = deltas[^1];
        result["mean_dl"] = (lumShot - lumGround) / deltas.Count;   // mean lightness difference (L*), positive = grass brighter
        return result;
    }

    /// <summary>Block-mean CIEDE2000: the image is cut into <paramref name="block"/> px squares; in each block with at least
    /// <paramref name="minCover"/> covered pixels the mean Lab of the covered pixels of <paramref name="shot"/> is compared with the mean Lab of
    /// the same pixels of <paramref name="ground"/>. This removes the unavoidable per-pixel texture phase between a card in front of the
    /// ground and the ground seen behind it, leaving the colour (patch / light / haze) agreement the plan targets.</summary>
    public Godot.Collections.Dictionary CompareBlocks(Image shot, Image ground, Image mask, Rect2I region, int threshold, int block, float minCover)
    {
        shot.Convert(Image.Format.Rgba8); ground.Convert(Image.Format.Rgba8); mask.Convert(Image.Format.Rgba8);
        byte[] s = shot.GetData(), g = ground.GetData(), m = mask.GetData();
        int w = shot.GetWidth();
        var deltas = new List<float>();
        double lumDiff = 0;
        for (int by = region.Position.Y; by + block <= region.End.Y; by += block)
            for (int bx = region.Position.X; bx + block <= region.End.X; bx += block)
            {
                Vector3 ss = Vector3.Zero, gg = Vector3.Zero; int n = 0;
                for (int y = by; y < by + block; y++)
                    for (int x = bx; x < bx + block; x++)
                    {
                        int o = (y * w + x) * 4;
                        int md = Math.Max(Math.Abs(m[o] - g[o]), Math.Max(Math.Abs(m[o + 1] - g[o + 1]), Math.Abs(m[o + 2] - g[o + 2])));
                        if (md <= threshold) continue;
                        ss += LabAt(s, o); gg += LabAt(g, o); n++;
                    }
                if (n < block * block * minCover) continue;
                ss /= n; gg /= n;
                deltas.Add(DeltaE2000(ss, gg));
                lumDiff += ss.X - gg.X;
            }
        var result = new Godot.Collections.Dictionary { ["blocks"] = deltas.Count };
        if (deltas.Count == 0) { result["mean"] = -1.0; result["p95"] = -1.0; result["max"] = -1.0; result["mean_dl"] = 0.0; return result; }
        deltas.Sort();
        double sum = 0; foreach (float d in deltas) sum += d;
        result["mean"] = sum / deltas.Count;
        result["median"] = deltas[deltas.Count / 2];
        result["p95"] = deltas[(int)(deltas.Count * .95)];
        result["max"] = deltas[^1];
        result["mean_dl"] = lumDiff / deltas.Count;
        return result;
    }

    /// <summary>Mean Lab colour of the covered pixels (differing from <paramref name="ground"/> by > threshold) of two renders in a region,
    /// and the CIEDE2000 between the two means. Used for hero-vs-card agreement.</summary>
    public Godot.Collections.Dictionary CompareCoveredMeans(Image a, Image b, Image ground, Rect2I region, int threshold)
    {
        a.Convert(Image.Format.Rgba8); b.Convert(Image.Format.Rgba8); ground.Convert(Image.Format.Rgba8);
        byte[] da = a.GetData(), db = b.GetData(), g = ground.GetData();
        int w = a.GetWidth();
        Vector3 sa = Vector3.Zero, sb = Vector3.Zero; int na = 0, nb = 0;
        for (int y = region.Position.Y; y < region.End.Y; y++)
            for (int x = region.Position.X; x < region.End.X; x++)
            {
                int o = (y * w + x) * 4;
                if (Math.Max(Math.Abs(da[o] - g[o]), Math.Max(Math.Abs(da[o + 1] - g[o + 1]), Math.Abs(da[o + 2] - g[o + 2]))) > threshold) { sa += LabAt(da, o); na++; }
                if (Math.Max(Math.Abs(db[o] - g[o]), Math.Max(Math.Abs(db[o + 1] - g[o + 1]), Math.Abs(db[o + 2] - g[o + 2]))) > threshold) { sb += LabAt(db, o); nb++; }
            }
        var result = new Godot.Collections.Dictionary { ["count_a"] = na, ["count_b"] = nb };
        if (na == 0 || nb == 0) { result["delta_e"] = -1.0; return result; }
        Vector3 ma = sa / na, mb = sb / nb;
        result["delta_e"] = DeltaE2000(ma, mb);
        result["lab_a"] = ma; result["lab_b"] = mb;
        return result;
    }

    // ---- curve parity ----
    /// <summary>C# reference values for the GLSL parity test: for each (rank, distance) the Scale product and its factors.</summary>
    public Godot.Collections.Array CurveReference(float[] ranks, float[] distances, float reach)
    {
        RingGrassCardCurveParameters p = RingGrassCardCurveParameters.Default with { ReachMeters = reach };
        var rows = new Godot.Collections.Array();
        for (int i = 0; i < ranks.Length; i++)
        {
            float r = ranks[i], d = distances[i];
            rows.Add(new Godot.Collections.Array
            {
                RingGrassCardCurve.Scale(p, r, d), RingGrassCardCurve.Keep(p, d), RingGrassCardCurve.Density(p, d), RingGrassCardCurve.LowScale(p, r, d),
            });
        }
        return rows;
    }
}
