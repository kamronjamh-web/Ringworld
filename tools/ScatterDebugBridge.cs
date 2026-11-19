using System.Linq;
using Godot;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// TEST ONLY (T5-10): the landscape harness's two scatter switches, both set before the world boots (tools/landscape_baseline_common.gd setup()).
/// `--scatter-a5-density` swaps the production class table for one whose boulder habitat-term weights are the design-A5 values (the calibrated weights scaled back by 2.5: scree 1.0, hollow 0.6,
/// rocky 0.4, lone 0.01; base density, angle of repose and slope multipliers unchanged). `EnableTileTiming` makes the scatter streamer record per-tile build times.
/// No menu, setting or normal game start reaches either.
/// </summary>
public partial class ScatterDebugBridge : RefCounted
{
    public void EnableTileTiming(bool on) => RingBiomeDecorationLayer.ScatterTileTiming = on;

    public void UseA5Density(bool on)
    {
        RingScatterClassTable.DebugOverride = null;
        if (!on) return;
        var rows = RingScatterClassTable.Production.Rows.Select(row =>
        {
            if (row.Key != "rock.boulder") return row;
            var terms = row.Terms.ToArray();
            for (int i = 0; i < terms.Length - 1; i++) terms[i] = terms[i] with { Weight = terms[i].Weight * 2.5f }; // the last term is the lone-boulder constant (0.01), unchanged
            return row with { Terms = terms };
        }).ToArray();
        RingScatterClassTable.DebugOverride = RingScatterClassTable.CreateForTests(rows);
    }

    /// <summary>The boulder row's term weights of the table now in use, for the log.</summary>
    public string BoulderWeights()
    {
        RingScatterClass row = RingScatterClassTable.Production.Rows.First(r => r.Key == "rock.boulder");
        return string.Join("/", row.Terms.Select(t => t.Weight.ToString("0.###"))) + " base " + row.BaseDensityPerHa;
    }
}
