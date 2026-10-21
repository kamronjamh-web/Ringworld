using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Typed helper for tools/terrain_altitude_lod_flight_test.gd. Casts a grid of camera rays onto the ground
/// plane and checks, against the live proxy chain, that every visible ground point is covered by an installed
/// sector of the tier that should own its slant distance, and that the tier's projected error stays inside
/// the plan budget (2.5x TargetPixelError for proxies, 5x for the global backdrop).
/// Read-only: it never changes the world.
/// </summary>
public partial class TerrainAltitudeLodProbe : RefCounted
{
    private Node? _world;
    private RingTerrainLodCoordinator? _lod;
    private RingTerrainGlobalLod? _global;
    private VoxelChunkGridManager? _grid;

    public bool Bind(Node world)
    {
        _world = world;
        _lod = world.GetNodeOrNull<RingTerrainLodCoordinator>("TerrainLodCoordinator");
        _global = world.GetNodeOrNull<RingTerrainGlobalLod>("GlobalTerrainLod3");
        _grid = world.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
        return _lod != null && _global != null && _grid != null;
    }

    public Godot.Collections.Dictionary GetLodState()
    {
        var result = new Godot.Collections.Dictionary();
        if (_lod == null) return result;
        var levels = new Godot.Collections.Array();
        foreach (RingTerrainProxySector level in _lod.Levels)
        {
            levels.Add(new Godot.Collections.Dictionary
            {
                ["label"] = level.LodLabel,
                ["spacing"] = level.SampleSpacingMeters,
                ["streaming"] = level.StreamingActive,
                ["active_sectors"] = level.ActiveSectorCount,
                ["radius"] = new Godot.Collections.Array { level.SectorRadiusAround, level.SectorRadiusAcross },
                ["fade_in"] = new Godot.Collections.Array { level.FadeStartMeters, level.FadeEndMeters },
                ["fade_out"] = new Godot.Collections.Array { level.FadeOutStartMeters, level.FadeOutEndMeters },
                ["mesh_mb"] = level.EstimatedActiveMeshBytes / 1048576.0,
                ["rebuilt_meshes"] = level.RebuiltSectorMeshCount,
                ["draw_stats"] = level.GetDrawStats(),
            });
        }
        result["levels"] = levels;
        result["preset"] = _lod.CurrentPresetName;
        result["target_pixel_error"] = _lod.CurrentTargetPixelError;
        result["altitude"] = _lod.CurrentViewerAltitudeAboveTerrainMeters;
        result["suppressed_levels"] = _lod.HighAltitudeProxySuspended;
        result["mesh_mb"] = _lod.EstimatedActiveMeshBytes / 1048576.0;
        if (_global != null)
        {
            result["global"] = new Godot.Collections.Dictionary
            {
                ["around"] = _global.AroundSegments,
                ["across"] = _global.AcrossSegments,
                ["fade"] = new Godot.Collections.Array { _global.FadeStartMeters, _global.FadeEndMeters },
            };
        }
        return result;
    }

    /// <summary>Sum of RebuiltSectorMeshCount across levels (monotonic); used to count re-anchor rebuilds.</summary>
    public long GetTotalRebuiltSectorMeshes()
    {
        long total = 0;
        if (_lod == null) return total;
        foreach (RingTerrainProxySector level in _lod.Levels) total += level.RebuiltSectorMeshCount;
        return total;
    }

    public Godot.Collections.Dictionary Evaluate(double groundVertical, int columns, int rows)
    {
        var result = new Godot.Collections.Dictionary();
        Camera3D? camera = _world?.GetViewport().GetCamera3D();
        if (_lod == null || _grid == null || _global == null || camera == null) return result;

        Vector2 size = _world!.GetViewport().GetVisibleRect().Size;
        float projectionScale = size.Y / (2.0f * Mathf.Tan(Mathf.DegToRad(camera.Fov) * 0.5f));
        float e = Mathf.Max(0.5f, _lod.CurrentTargetPixelError);
        float proxyLimit = 2.5f * e * 1.01f;
        float backdropLimit = 5.0f * e * 1.01f;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double width = _global.RingWidthMeters;
        float globalEdge = (float)Math.Max(
            circumference / Math.Max(1, _global.AroundSegments), width / Math.Max(1, _global.AcrossSegments));
        float maxRange = Mathf.Min(camera.Far, 1500000.0f);

        IReadOnlyList<RingTerrainProxySector> levels = _lod.Levels;
        int firstStreaming = -1;
        for (int i = 0; i < levels.Count; i++)
        {
            if (levels[i].StreamingActive) { firstStreaming = i; break; }
        }

        int rays = 0, hits = 0, holes = 0, epsViolations = 0, backdropHits = 0, backdropViolations = 0;
        float worstEps = 0.0f, worstBackdrop = 0.0f;
        string worstEpsTier = "-";
        var tierHits = new Godot.Collections.Dictionary();
        var samples = new Godot.Collections.Array<string>();
        Vector3 origin = camera.GlobalPosition;
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                rays++;
                var screen = new Vector2(
                    size.X * (column + 0.5f) / columns,
                    size.Y * (row + 0.5f) / rows);
                Vector3 direction = camera.ProjectRayNormal(screen);
                if (!TryHitGround(origin, direction, groundVertical, maxRange,
                        out double around, out double across, out float distance))
                    continue;
                if (across < 0.0 || across > width) continue;
                hits++;

                RingTerrainProxySector? owner = null;
                for (int i = 0; i < levels.Count; i++)
                {
                    if (!levels[i].StreamingActive) continue;
                    if (levels[i].FadeOutEndMeters >= distance) { owner = levels[i]; break; }
                }

                if (owner == null)
                {
                    backdropHits++;
                    float backdropError = globalEdge * projectionScale / distance;
                    if (backdropError > worstBackdrop) worstBackdrop = backdropError;
                    if (backdropError > backdropLimit)
                    {
                        backdropViolations++;
                        if (samples.Count < 6)
                            samples.Add($"backdrop eps={backdropError:0.0}px d={distance:0} edge={globalEdge:0}");
                    }
                    continue;
                }

                string key = owner.LodLabel;
                tierHits[key] = (int)tierHits.GetValueOrDefault(key, 0) + 1;
                float error = owner.SampleSpacingMeters * projectionScale / distance;
                if (error > worstEps) { worstEps = error; worstEpsTier = key; }
                if (error > proxyLimit)
                {
                    epsViolations++;
                    if (samples.Count < 6)
                        samples.Add($"proxy {key} eps={error:0.0}px d={distance:0} limit={proxyLimit:0.0}");
                }

                bool covered = false;
                for (int i = 0; i < levels.Count && !covered; i++)
                {
                    RingTerrainProxySector level = levels[i];
                    if (!level.StreamingActive) continue;
                    if (level.FadeOutEndMeters < distance * 0.995f) continue;
                    if (i != firstStreaming && distance < level.FadeEndMeters * 0.98f) continue;
                    covered = level.HasInstalledSectorAt(around, across);
                }
                if (!covered)
                {
                    holes++;
                    if (samples.Count < 6)
                        samples.Add($"hole owner={key} d={distance:0} around={around:0} across={across:0}");
                }
            }
        }

        result["rays"] = rays;
        result["hits"] = hits;
        result["holes"] = holes;
        result["proxy_eps_violations"] = epsViolations;
        result["backdrop_hits"] = backdropHits;
        result["backdrop_violations"] = backdropViolations;
        result["worst_proxy_eps"] = worstEps;
        result["worst_proxy_tier"] = worstEpsTier;
        result["worst_backdrop_eps"] = worstBackdrop;
        result["proxy_limit"] = proxyLimit;
        result["backdrop_limit"] = backdropLimit;
        result["global_edge"] = globalEdge;
        result["tier_hits"] = tierHits;
        result["samples"] = samples;
        return result;
    }

    private bool TryHitGround(
        Vector3 origin, Vector3 direction, double groundVertical, float maxRange,
        out double around, out double across, out float distance)
    {
        around = across = 0.0;
        distance = 0.0f;
        double previousT = 0.0;
        RingCanonicalPosition start = _grid!.WorldToCanonicalRingPosition64(origin);
        double previousF = start.VerticalMeters - groundVertical;
        if (previousF <= 0.0) return false;
        double t = 25.0;
        while (t <= maxRange)
        {
            RingCanonicalPosition p = _grid.WorldToCanonicalRingPosition64(origin + direction * (float)t);
            double f = p.VerticalMeters - groundVertical;
            if (f <= 0.0)
            {
                double lo = previousT, hi = t;
                for (int i = 0; i < 22; i++)
                {
                    double mid = (lo + hi) * 0.5;
                    RingCanonicalPosition m = _grid.WorldToCanonicalRingPosition64(origin + direction * (float)mid);
                    if (m.VerticalMeters - groundVertical > 0.0) lo = mid; else hi = mid;
                }
                RingCanonicalPosition hit = _grid.WorldToCanonicalRingPosition64(origin + direction * (float)hi);
                around = hit.AroundMeters;
                across = hit.AcrossMeters;
                distance = (float)hi;
                return true;
            }
            previousT = t;
            t *= 1.1;
        }
        return false;
    }
}
