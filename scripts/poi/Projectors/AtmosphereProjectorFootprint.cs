using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Walk footprint of an atmosphere-shield projector tower: the union of axis-aligned rectangles (asset frame: x = across offset from the wall's centre
/// line, z = around offset from the site) that is solid from the wall top up to <see cref="SlabTopMeters"/>. Generated data
/// (assets/poi/atmosphere_projector/collision/AtmosphereProjector_Footprint.json). Pure apart from loading.
/// </summary>
public sealed class AtmosphereProjectorFootprint
{
    public const string DefaultPath = "res://assets/poi/atmosphere_projector/collision/AtmosphereProjector_Footprint.json";
    private const double MaxSubStepMeters = 0.25;
    private const int MaxPushIterations = 8;

    private static AtmosphereProjectorFootprint? _shared;
    private static bool _sharedLoaded;

    private readonly (double X0, double Z0, double X1, double Z1)[] _rects;

    public IReadOnlyList<(double X0, double Z0, double X1, double Z1)> Rects => _rects;
    public double SlabTopMeters { get; }
    public double MaxAbsX { get; }
    public double MaxAbsZ { get; }

    /// <summary>The footprint loaded once from <see cref="DefaultPath"/>; null (with one warning) when it cannot be read.</summary>
    public static AtmosphereProjectorFootprint? Shared
    {
        get
        {
            if (!_sharedLoaded)
            {
                _sharedLoaded = true;
                _shared = Load(DefaultPath);
            }
            return _shared;
        }
    }

    private AtmosphereProjectorFootprint((double X0, double Z0, double X1, double Z1)[] rects, double slabTopMeters)
    {
        _rects = rects;
        SlabTopMeters = slabTopMeters;
        double maxX = 0d, maxZ = 0d;
        foreach (var r in rects)
        {
            maxX = Math.Max(maxX, Math.Max(Math.Abs(r.X0), Math.Abs(r.X1)));
            maxZ = Math.Max(maxZ, Math.Max(Math.Abs(r.Z0), Math.Abs(r.Z1)));
        }
        MaxAbsX = maxX;
        MaxAbsZ = maxZ;
    }

    public static AtmosphereProjectorFootprint FromRects(IEnumerable<(double X0, double Z0, double X1, double Z1)> rects, double slabTopMeters)
        => new(new List<(double X0, double Z0, double X1, double Z1)>(rects).ToArray(), slabTopMeters);

    private static AtmosphereProjectorFootprint? Load(string path)
    {
        try
        {
            using Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (file == null)
            {
                GD.PushWarning($"AtmosphereProjectorFootprint: cannot open {path}; projector tower collision disabled.");
                return null;
            }
            Variant parsed = Json.ParseString(file.GetAsText());
            if (parsed.VariantType != Variant.Type.Dictionary)
            {
                GD.PushWarning($"AtmosphereProjectorFootprint: {path} is not a JSON object; projector tower collision disabled.");
                return null;
            }
            Godot.Collections.Dictionary root = parsed.AsGodotDictionary();
            double slab = root.ContainsKey("slab_top_m") ? (double)root["slab_top_m"] : 3d;
            Godot.Collections.Array list = (Godot.Collections.Array)root["rects"];
            var rects = new (double X0, double Z0, double X1, double Z1)[list.Count];
            for (int i = 0; i < rects.Length; i++)
            {
                Godot.Collections.Array r = (Godot.Collections.Array)list[i];
                rects[i] = ((double)r[0], (double)r[1], (double)r[2], (double)r[3]);
            }
            return rects.Length == 0 ? null : new AtmosphereProjectorFootprint(rects, slab);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"AtmosphereProjectorFootprint: failed to load {path}: {exception.Message}; projector tower collision disabled.");
            return null;
        }
    }

    /// <summary>True when (x, z) is strictly inside any rectangle grown by <paramref name="inflate"/> (standing on the grown edge is free).</summary>
    public bool Blocks(double x, double z, double inflate)
    {
        if (Math.Abs(x) >= MaxAbsX + inflate || Math.Abs(z) >= MaxAbsZ + inflate) return false;
        foreach (var r in _rects)
            if (x > r.X0 - inflate && x < r.X1 + inflate && z > r.Z0 - inflate && z < r.Z1 + inflate) return true;
        return false;
    }

    /// <summary>
    /// Moves from (x0, z0) to (x1, z1) in sub-steps of at most 0.25 m; each sub-step tries the full step, else the X part, else the Z part, else stays
    /// (axis-separated slide). A start inside the footprint is first pushed out to the nearest free point. Returns the final point.
    /// </summary>
    public (double X, double Z) ResolveMove(double x0, double z0, double x1, double z1, double inflate, out bool blockedX, out bool blockedZ)
    {
        blockedX = false;
        blockedZ = false;
        if (Blocks(x0, z0, inflate)) (x0, z0) = PushOut(x0, z0, inflate);

        double dx = x1 - x0, dz = z1 - z0;
        double length = Math.Sqrt(dx * dx + dz * dz);
        int steps = Math.Max(1, (int)Math.Ceiling(length / MaxSubStepMeters));
        double sx = dx / steps, sz = dz / steps;
        double x = x0, z = z0;
        for (int i = 0; i < steps; i++)
        {
            double nx = x + sx, nz = z + sz;
            if (!Blocks(nx, nz, inflate)) { x = nx; z = nz; continue; }
            bool moved = false;
            if (sx != 0d)
            {
                if (!Blocks(nx, z, inflate)) { x = nx; moved = true; }
                else blockedX = true;
            }
            if (sz != 0d)
            {
                if (!Blocks(x, nz, inflate)) { z = nz; moved = true; }
                else blockedZ = true;
            }
            if (!moved && sx == 0d && sz == 0d) break;
        }
        return (x, z);
    }

    private (double X, double Z) PushOut(double x, double z, double inflate)
    {
        for (int iteration = 0; iteration < MaxPushIterations; iteration++)
        {
            int hit = -1;
            for (int i = 0; i < _rects.Length; i++)
            {
                var r = _rects[i];
                if (x > r.X0 - inflate && x < r.X1 + inflate && z > r.Z0 - inflate && z < r.Z1 + inflate) { hit = i; break; }
            }
            if (hit < 0) return (x, z);
            var rect = _rects[hit];
            var candidates = new (double Distance, double X, double Z)[4];
            candidates[0] = (x - (rect.X0 - inflate), rect.X0 - inflate, z);
            candidates[1] = ((rect.X1 + inflate) - x, rect.X1 + inflate, z);
            candidates[2] = (z - (rect.Z0 - inflate), x, rect.Z0 - inflate);
            candidates[3] = ((rect.Z1 + inflate) - z, x, rect.Z1 + inflate);
            // Nearest edge whose exit point is itself free; else the nearest edge (the next iteration continues from there).
            int best = -1, bestFree = -1;
            for (int c = 0; c < 4; c++)
            {
                if (best < 0 || candidates[c].Distance < candidates[best].Distance) best = c;
                if (!Blocks(candidates[c].X, candidates[c].Z, inflate)
                    && (bestFree < 0 || candidates[c].Distance < candidates[bestFree].Distance)) bestFree = c;
            }
            int pick = bestFree >= 0 ? bestFree : best;
            x = candidates[pick].X;
            z = candidates[pick].Z;
        }
        return (x, z);
    }
}
