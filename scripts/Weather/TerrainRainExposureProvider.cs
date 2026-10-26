using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Rain exposure from the terrain itself: generated or edited surface, constructed voxels and water, resolved per integer ring column.
/// Extracted unchanged from the ambience node; one canonical query feeds shelter and sector placement, so an edited excavation never falls back to legacy air.
/// </summary>
public sealed class TerrainRainExposureProvider : IRainExposureProvider
{
    private readonly VoxelChunkGridManager _grid;
    private readonly SmoothTerrainGrid? _smoothTerrain;

    public TerrainRainExposureProvider(VoxelChunkGridManager grid, SmoothTerrainGrid? smoothTerrain)
    {
        _grid = grid;
        _smoothTerrain = smoothTerrain;
    }

    public int SurfaceQueriesFrame { get; private set; }
    public int SurfaceQueriesTotal { get; private set; }
    public int DensityQueriesFrame { get; private set; }
    public int DensityQueriesTotal { get; private set; }

    public void BeginFrame() { SurfaceQueriesFrame = 0; DensityQueriesFrame = 0; }

    /// <summary>Snapshot/restore for diagnostics that must not disturb the budgets.</summary>
    public (int Surface, int SurfaceTotal, int Density, int DensityTotal) SaveCounters() => (SurfaceQueriesFrame, SurfaceQueriesTotal, DensityQueriesFrame, DensityQueriesTotal);
    public void RestoreCounters((int Surface, int SurfaceTotal, int Density, int DensityTotal) saved)
    {
        SurfaceQueriesFrame = saved.Surface; SurfaceQueriesTotal = saved.SurfaceTotal;
        DensityQueriesFrame = saved.Density; DensityQueriesTotal = saved.DensityTotal;
    }

    public bool TryProbeColumn(long around, int across, out RainColumnProbe probe)
    {
        probe = default;
        SurfaceQueriesFrame++; SurfaceQueriesTotal++;
        if (_grid.TerrainSampler == null || across < 0 || across >= _grid.RingAcrossWidthMeters) return false;
        float natural;
        if (_smoothTerrain != null)
        {
            natural = Mathf.FloorToInt(_grid.TerrainSampler.GetSmoothSurfaceHeight(around, across) + .5f);
            RingLodSurfaceEditSnapshot edits = _smoothTerrain.CaptureLodSurfaceEditSnapshot();
            if (edits.TryGetEditedSurfaceHeight(around, across, out int editedNatural)) natural = editedNatural;
            // Validate the highest nearby crossing, then refine to 1/32 metre.
            int bracket = Mathf.FloorToInt(natural);
            bool found = false;
            for (int y = bracket + 2; y >= bracket - 2; y--)
            {
                DensityQueriesFrame += 2; DensityQueriesTotal += 2;
                if (!_smoothTerrain.TryQueryEditableDensityAtCanonicalPosition(around, y, across, out float inside)
                    || !_smoothTerrain.TryQueryEditableDensityAtCanonicalPosition(around, y + 1, across, out float above)) continue;
                if (inside <= 0 && above > 0)
                {
                    float lo = y, hi = y + 1;
                    for (int step = 0; step < 5; step++)
                    {
                        float mid = (lo + hi) * .5f; DensityQueriesFrame++; DensityQueriesTotal++;
                        if (!_smoothTerrain.TryQueryEditableDensityAtCanonicalPosition(around, mid, across, out float density)) break;
                        if (density <= 0) lo = mid; else hi = mid;
                    }
                    natural = (lo + hi) * .5f; found = true; break;
                }
            }
            if (!found) return false;
        }
        else
        {
            RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
            bool floatSurface = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface;
            natural = floatSurface
                ? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, around, across)
                : edits.GetSurfaceHeight(_grid.TerrainSampler, around, across) + 1.0f;
        }
        float top = natural;
        BlockType material = _grid.GetCanonicalBlockType(around, Mathf.FloorToInt(natural - .01f), across);
        if (_smoothTerrain?.CaptureVolumetricDensitySnapshot() is SmoothTerrainVolumetricDensitySnapshot field)
        {
            DensityQueriesFrame++; DensityQueriesTotal++;
            SmoothTerrainSample naturalSample = field.Sample(around, natural - .5f, across);
            material = naturalSample.Material == (int)SmoothTerrainMaterial.Soil ? BlockType.Soil : BlockType.Rock;
        }
        bool constructed = false;
        if (_grid.TryGetHighestRainSurface(around, across, int.MinValue, out int constructedTop, out BlockType constructedMaterial) && constructedTop + 1.0f > top)
        { top = constructedTop + 1.0f; material = constructedMaterial; constructed = true; }
        TerrainSample sample = _grid.CaptureLodSurfaceEditSnapshot().GetPresentationTerrainSample(_grid.TerrainSampler, around, across);
        RainMaterial kind = material == BlockType.Soil
            ? (sample.Biome is TerrainBiomeId.Forest or TerrainBiomeId.Grassland ? RainMaterial.Foliage : RainMaterial.Soil)
            : RainMaterial.Hard;
        float waterTop = sample.WaterSurfaceHeight + (_grid.TerrainSampler.UsesRuntimeSurfaceGeneration ? 0.0f : 1.0f);
        if (sample.HasSurfaceWater && waterTop > top) { top = waterTop; kind = RainMaterial.Water; constructed = false; }
        else if (_grid.TryGetDynamicWaterSurfaceAtCanonical(new RingCanonicalPosition(around, Mathf.CeilToInt(top), across), out float dynamicTop) && dynamicTop > top) { top = dynamicTop; kind = RainMaterial.Water; constructed = false; }
        probe = new RainColumnProbe(true, natural, top, kind, constructed);
        return true;
    }

    /// <summary>Head-height wall test: constructed blocks first (one lookup), then smooth-terrain density where smooth terrain exists, else the canonical block.</summary>
    public bool IsSolidAt(long around, double vertical, int across)
    {
        if (across < 0 || across >= _grid.RingAcrossWidthMeters) return true;
        int y = Mathf.FloorToInt((float)vertical);
        if (_grid.HasConstructedBlockAt(around, y, across)) return true;
        if (_smoothTerrain != null)
        {
            DensityQueriesFrame++; DensityQueriesTotal++;
            return _smoothTerrain.TryQueryEditableDensityAtCanonicalPosition(around, y + .5f, across, out float density) && density <= 0;
        }
        return _grid.GetCanonicalBlockType(around, y, across) != BlockType.Air;
    }
}
