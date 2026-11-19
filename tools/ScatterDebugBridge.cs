using System.Linq;
using Godot;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// TEST ONLY (T5-10): the landscape harness's two scatter switches, both set before the world boots (tools/landscape_baseline_common.gd setup()).
/// `--scatter-a5-density` used to swap in the design-A5 boulder weights (scree 1.0, hollow 0.6, rocky 0.4, lone 0.01); production carries them since 2026-11-19, so it is now a no-op.
/// `EnableTileTiming` makes the scatter streamer record per-tile build times.
/// No menu, setting or normal game start reaches either.
/// </summary>
public partial class ScatterDebugBridge : RefCounted
{
    public void EnableTileTiming(bool on) => RingBiomeDecorationLayer.ScatterTileTiming = on;

    /// <summary>T5-10b: sets the shader's rock_debug_view (see ring_scatter_rock.gdshader) for the rock materials built from now on; 0 clears it.</summary>
    public void SetRockDebugView(int view) => RingScatterRockMaterial.DebugView = view;

    /// <summary>Since 2026-11-19 the production table carries the design-A5 boulder weights (user's choice), so `--scatter-a5-density` is a no-op kept for old command lines.</summary>
    public void UseA5Density(bool on) => RingScatterClassTable.DebugOverride = null;

    /// <summary>The boulder row's term weights of the table now in use, for the log.</summary>
    public string BoulderWeights()
    {
        RingScatterClass row = RingScatterClassTable.Production.Rows.First(r => r.Key == "rock.boulder");
        return string.Join("/", row.Terms.Select(t => t.Weight.ToString("0.###"))) + " base " + row.BaseDensityPerHa;
    }
}
