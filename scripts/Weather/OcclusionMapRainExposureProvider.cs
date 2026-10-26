using System;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Rain exposure from the precipitation occlusion map (docs/storm-revamp-s5-rain-audio-design.md WO8, docs/storm-revamp-s1-design.md section 1).
/// What lands where comes from the map, which sees every rendered occluder (terrain as drawn, POIs, buildings, constructed roofs): the 24 exposure columns and
/// the overhead texel arrive in one asynchronous probe readback (<see cref="RingPrecipitationOcclusion.TryGetColumnSamples"/>), never a synchronous GPU read.
/// Material and the natural surface come from the terrain provider for the same column; a map surface well above the terrain surface is a building or POI
/// (Metal, constructed), anything else is terrain as rendered. Because the map is the rendered ground, a terrain height that sits above the collision ground
/// (up to about 2 m at generated sites) never reads as cover. Walls are not visible from above, so head-height solid tests stay with the terrain provider.
/// <see cref="Available"/> is true only while the probe is fresh; callers fall back to the terrain provider otherwise.
/// </summary>
public sealed class OcclusionMapRainExposureProvider : IRainExposureProvider
{
    /// <summary>A map surface higher than the terrain surface by more than this is an occluder that is not terrain (POI, building, constructed roof).</summary>
    public const float OccluderAboveTerrainMeters = 2.5f;
    /// <summary>Largest distance from a requested column centre to a sampled column for the sample to stand in for it.</summary>
    public const float MatchMeters = 2.0f;

    private readonly TerrainRainExposureProvider _terrain;
    private readonly float[] _heights = new float[RingPrecipitationOcclusion.ProbeColumnSlots];
    private readonly bool[] _valid = new bool[RingPrecipitationOcclusion.ProbeColumnSlots];
    private static readonly float[] OffsetAround = new float[RingPrecipitationOcclusion.ProbeColumnSlots];
    private static readonly float[] OffsetAcross = new float[RingPrecipitationOcclusion.ProbeColumnSlots];
    private RingCanonicalPosition _listener;
    private double _circumference;
    private ulong _snapshotFrame = ulong.MaxValue;
    private bool _snapshotOk;

    static OcclusionMapRainExposureProvider()
    {
        for (int i = 0; i < RainSoundField.ColumnCount; i++) RainSoundField.ColumnGeometry(i, out _, out OffsetAround[i], out OffsetAcross[i]);
    }

    public OcclusionMapRainExposureProvider(TerrainRainExposureProvider terrain) => _terrain = terrain;

    /// <summary>The listener the column samples are relative to (the ambience's camera listener) and the ring circumference for wrap-aware offsets.</summary>
    public void SetListener(in RingCanonicalPosition listener, double circumferenceMeters)
    {
        _listener = listener; _circumference = circumferenceMeters;
    }

    /// <summary>True when the map is published, the probe is fresh and the newest readback carried the column samples with the listener's own texel inside the map.</summary>
    public bool Available
    {
        get { RefreshSnapshot(); return _snapshotOk; }
    }

    private void RefreshSnapshot()
    {
        ulong frame = Engine.GetProcessFrames();
        if (frame == _snapshotFrame) return;
        _snapshotFrame = frame;
        RingPrecipitationOcclusion? map = RingPrecipitationOcclusion.Instance;
        _snapshotOk = map != null && map.Ready && map.TryGetColumnSamples(_heights, _valid) && _valid[RingPrecipitationOcclusion.ProbeColumnSlots - 1];
    }

    public bool TryProbeColumn(long around, int across, out RainColumnProbe probe)
    {
        RefreshSnapshot();
        int index = _snapshotOk ? NearestColumn(around, across) : -1;
        if (!_terrain.TryProbeColumn(around, across, out RainColumnProbe terrain)) { probe = default; return false; }
        if (index < 0 || !_valid[index]) { probe = terrain; return true; }
        float surface = _heights[index];
        if (!float.IsFinite(surface)) { probe = terrain; return true; } // Nothing drawn below the plane: far below the listener, the terrain answer is as good.
        float mapTop = (float)_listener.VerticalMeters + surface;
        if (terrain.Material == RainMaterial.Water && mapTop <= terrain.Top + .5f)
        {
            probe = new RainColumnProbe(true, MathF.Min(terrain.NaturalTop, mapTop), mapTop, RainMaterial.Water, false);
            return true;
        }
        if (mapTop > terrain.Top + OccluderAboveTerrainMeters)
        {
            // A POI, building or roof: rain lands on it, so it rings like sheet metal.
            probe = new RainColumnProbe(true, terrain.NaturalTop, mapTop, RainMaterial.Metal, true);
            return true;
        }
        // Terrain as rendered: the map's own surface is the truth for height, the terrain answer for what it is made of.
        probe = new RainColumnProbe(true, mapTop, mapTop, terrain.Material, false);
        return true;
    }

    public bool IsSolidAt(long around, double vertical, int across) => _terrain.IsSolidAt(around, vertical, across);

    private int NearestColumn(long around, int across)
    {
        double circumference = _circumference;
        double da = around + .5 - _listener.AroundMeters, dc = across + .5 - _listener.AcrossMeters;
        if (circumference > 0) { da %= circumference; if (da > circumference * .5) da -= circumference; else if (da < -circumference * .5) da += circumference; }
        int best = -1;
        double bestDistance = MatchMeters * MatchMeters;
        for (int i = 0; i < OffsetAround.Length; i++)
        {
            double x = da - OffsetAround[i], z = dc - OffsetAcross[i], distance = x * x + z * z;
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }
}
