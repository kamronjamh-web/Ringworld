using System;
using System.Diagnostics;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Phase3;
using GodotDictionary = Godot.Collections.Dictionary;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// The terrain generation preview's Drainage view (T2R-13): a CPU strip bake of the preview window and a discharge image of it. The bake is the production
/// one (barriers, conditioning, routing, lake balance) on a strip of whole 1 km columns across the whole ring width, with moisture from the recipe's climate
/// on default terrain settings (the live settings are not read, so the preview is the same wherever it runs). Call <see cref="BakeStrip"/> from a worker thread; it touches no node.
/// </summary>
public partial class DrainagePreviewBridge : RefCounted
{
    public const int MinimumColumns = 8;
    public const int MaximumColumns = 512;
    /// <summary>Contributing area (km^2 of runoff-equivalent) at which a channel starts to show.</summary>
    public const double ChannelThresholdKm2 = 50d;

    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// Bakes the strip that covers <paramref name="aroundMeters"/> to <paramref name="aroundMeters"/> + <paramref name="spanMeters"/> and returns
    /// <c>width</c>, <c>height</c>, <c>origin_around_m</c>, <c>cell_m</c> (around), <c>cell_across_m</c>, <c>rgb</c> (one pixel per cell, around to the right, across down),
    /// <c>river_cells</c>, <c>lake_cells</c>, <c>milliseconds</c> and <c>error</c> (empty on success).
    /// </summary>
    public GodotDictionary BakeStrip(GodotDictionary snapshot, double aroundMeters, double spanMeters, int workers)
    {
        var result = new GodotDictionary { ["error"] = string.Empty };
        try
        {
            var clock = Stopwatch.StartNew();
            TerrainGenerationParameters parameters = TerrainGenerationParameters.FromDictionary(snapshot);
            int columns = Math.Clamp((int)Math.Ceiling(spanMeters / 1000d) + 1, MinimumColumns, MaximumColumns);
            DrainageGridSpec spec = DrainageGridSpec.Strip(parameters, aroundMeters, columns);
            VoxelTerrainSettings effective = new();
            if (parameters.IsWorldGenerationRecipe) RingTerrainSampler.ApplyRecipeClimate(effective, parameters);
            DrainageClimateInputs climate = DrainageClimateInputs.FromSettings(effective, (long)TerrainGenerationParameters.CertifiedCircumferenceMeters, (int)TerrainGenerationParameters.CertifiedWidthMeters);
            var source = new CpuDrainageHeightSource(parameters, climate, Math.Max(1, workers));
            DrainageGrid grid = source.SampleGrid(spec, null, CancellationToken.None);
            DrainageField field = DrainageBake.Bake(grid, parameters, Math.Max(1, workers), null, CancellationToken.None);
            byte[] rgb = Render(field, out int riverCells, out int lakeCells);
            result["width"] = field.Width;
            result["height"] = field.Height;
            result["origin_around_m"] = spec.ColumnStart * spec.AroundStepMeters;
            result["cell_m"] = spec.AroundStepMeters;
            result["cell_across_m"] = spec.AcrossStepMeters;
            result["rgb"] = rgb;
            result["river_cells"] = riverCells;
            result["lake_cells"] = lakeCells;
            result["milliseconds"] = clock.Elapsed.TotalMilliseconds;
            LastError = string.Empty;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            result["error"] = exception.Message;
        }
        return result;
    }

    /// <summary>
    /// Grey hillshade of the valley floor, dark blue sea, light blue lake water, pale tan dry basins, violet barriers; land channels coloured by discharge
    /// (accumulated runoff) on a log ramp from pale green-blue at 50 km^2 to deep blue at 50,000 km^2 and more.
    /// </summary>
    internal static byte[] Render(DrainageField f, out int riverCells, out int lakeCells)
    {
        int w = f.Width, h = f.Height;
        var rgb = new byte[w * h * 3];
        float[] lowest = f.Lowest.ToArray();
        DrainageCellKind[] kind = f.Kind.ToArray();
        double[] discharge = f.RunoffAccumulation.ToArray();
        int[] down = f.Down.ToArray();
        double step = f.AroundStepMeters;
        double lx = -1 / Math.Sqrt(3), ly = 1 / Math.Sqrt(3), lz = -1 / Math.Sqrt(3);
        int rivers = 0, lakes = 0;
        void Put(int cell, double r, double g, double b)
        {
            rgb[cell * 3] = (byte)Math.Clamp((int)(r * 255 + 0.5), 0, 255);
            rgb[cell * 3 + 1] = (byte)Math.Clamp((int)(g * 255 + 0.5), 0, 255);
            rgb[cell * 3 + 2] = (byte)Math.Clamp((int)(b * 255 + 0.5), 0, 255);
        }
        for (int j = 0; j < h; j++)
        for (int i = 0; i < w; i++)
        {
            int cell = j * w + i;
            switch (kind[cell])
            {
                case DrainageCellKind.Ocean:
                    Put(cell, 0.10 * (1 - 0.6 * Math.Clamp(-lowest[cell] / 600d, 0, 1)), 0.20 * (1 - 0.6 * Math.Clamp(-lowest[cell] / 600d, 0, 1)), 0.45 * (1 - 0.6 * Math.Clamp(-lowest[cell] / 600d, 0, 1)));
                    continue;
                case DrainageCellKind.Barrier: Put(cell, 0.80, 0.20, 0.80); continue;
                case DrainageCellKind.Lung: Put(cell, 0.30, 0.25, 0.70); continue;
            }
            if (f.IsLakeWater(cell)) { lakes++; Put(cell, 0.24, 0.64, 0.86); continue; }
            float hl = lowest[j * w + Math.Max(i - 1, 0)], hr = lowest[j * w + Math.Min(i + 1, w - 1)];
            float hu = lowest[Math.Max(j - 1, 0) * w + i], hd = lowest[Math.Min(j + 1, h - 1) * w + i];
            double dx = 6d * (hr - hl) / (2d * step), dz = 6d * (hd - hu) / (2d * step);
            double inv = 1d / Math.Sqrt(dx * dx + 1 + dz * dz);
            double lit = Math.Max(0, -dx * inv * lx + inv * ly - dz * inv * lz);
            if (f.IsDryFloor(cell)) { double k = 0.8 + 0.2 * lit; Put(cell, 0.86 * k, 0.78 * k, 0.60 * k); continue; }
            if (kind[cell] == DrainageCellKind.Land && down[cell] >= 0 && discharge[cell] >= ChannelThresholdKm2)
            {
                // t = 0 at 50 km^2, 1 at 50,000 km^2.
                double t = Math.Clamp(Math.Log10(discharge[cell] / ChannelThresholdKm2) / 3d, 0, 1);
                Put(cell, 0.45 * (1 - t) + 0.03 * t, 0.95 * (1 - t) + 0.25 * t, 0.70 * (1 - t) + 0.75 * t);
                rivers++;
                continue;
            }
            double v = 0.35 + 0.55 * lit;
            Put(cell, v, v, v);
        }
        riverCells = rivers;
        lakeCells = lakes;
        return rgb;
    }
}
