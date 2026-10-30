using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// S3 WO-D1 static helpers of RingGustFrontDebris: GustLevel identity (strength 0, invalid state), front and outflow terms; WetScales (RecentRain, fat drops and the core suppress dust, the
/// core halves leaves); the biome table (leaves in forest, dust in desert, spray on water, snow share for Tundra and Alpine); the preset budget values.
/// Run: Godot --headless --path . --script res://tools/gust_front_debris_headless_test.gd. Prints GUST_FRONT_DEBRIS=PASS.
/// </summary>
public partial class GustFrontDebrisHeadlessTest : RefCounted
{
    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            StormArrivalState calm = StormArrivalState.Calm;
            StormArrivalState front = calm with { Valid = true, GustFront = 1.0f };
            StormArrivalState outflow = calm with { Valid = true, Outflow = 1.0f };
            StormArrivalState trailing = outflow with { Trailing = 1.0f };
            Check(RingGustFrontDebris.GustLevel(calm, 1.0f) == 0.0f, "invalid state must give 0");
            Check(RingGustFrontDebris.GustLevel(front, 0.0f) == 0.0f, "strength 0 must give 0 (identity)");
            Check(RingGustFrontDebris.GustLevel(front, -1.0f) == 0.0f, "negative strength must give 0");
            Check(Mathf.IsEqualApprox(RingGustFrontDebris.GustLevel(front, 1.0f), 1.0f), "full front at strength 1 must be 1");
            Check(Mathf.IsEqualApprox(RingGustFrontDebris.GustLevel(front, 0.5f), 0.5f), "level must scale with strength");
            Check(RingGustFrontDebris.GustLevel(front, 2.0f) <= 1.0f, "strength 2 must stay clamped at 1");
            Check(Mathf.IsEqualApprox(RingGustFrontDebris.GustLevel(outflow, 1.0f), 0.55f), $"outflow level {RingGustFrontDebris.GustLevel(outflow, 1.0f)} want 0.55");
            Check(RingGustFrontDebris.GustLevel(trailing, 1.0f) < RingGustFrontDebris.GustLevel(outflow, 1.0f), "trailing rain must thin the outflow");

            Vector2 dry = RingGustFrontDebris.WetScales(calm);
            Check(dry == Vector2.One, $"dry ground must keep full leaves and dust, got {dry}");
            Check(RingGustFrontDebris.WetScales(calm with { RecentRainAmount = 0.2f }).Y == 0.0f, "recent rain 0.2 must suppress dust");
            float partial = RingGustFrontDebris.WetScales(calm with { RecentRainAmount = 0.1f }).Y;
            Check(partial > 0.0f && partial < 1.0f, $"recent rain 0.1 must partly suppress dust, got {partial}");
            Check(RingGustFrontDebris.WetScales(calm with { FatDrop = 1.0f }).Y == 0.0f, "fat drops must suppress dust");
            Vector2 core = RingGustFrontDebris.WetScales(calm with { Core = 1.0f });
            Check(core.Y == 0.0f && Mathf.IsEqualApprox(core.X, 0.5f), $"core must remove dust and halve leaves, got {core}");

            Vector3 forest = RingGustFrontDebris.BiomeShares(TerrainBiomeId.Forest, false);
            Check(forest == new Vector3(1, 0, 0), $"forest shares {forest}");
            Vector3 desert = RingGustFrontDebris.BiomeShares(TerrainBiomeId.Desert, false);
            Check(desert == new Vector3(0, 1, 0), $"desert shares {desert}");
            Check(RingGustFrontDebris.BiomeShares(TerrainBiomeId.Ocean, false) == new Vector3(0, 0, 1), "ocean must be spray");
            Check(RingGustFrontDebris.BiomeShares(TerrainBiomeId.Forest, true) == new Vector3(0, 0, 1), "surface water over forest must be spray");
            foreach (TerrainBiomeId biome in Enum.GetValues<TerrainBiomeId>())
            {
                Vector3 s = RingGustFrontDebris.BiomeShares(biome, false);
                Check(s.X >= 0 && s.Y >= 0 && s.Z >= 0 && s.X <= 1 && s.Y <= 1 && s.Z <= 1, $"{biome} shares out of range {s}");
                Check(s.X + s.Y + s.Z > 0.0f, $"{biome} has no debris at all");
            }
            Check(RingGustFrontDebris.SnowShare(TerrainBiomeId.Tundra, false) == 1.0f && RingGustFrontDebris.SnowShare(TerrainBiomeId.Alpine, false) == 1.0f, "tundra and alpine must be snow");
            Check(RingGustFrontDebris.SnowShare(TerrainBiomeId.Forest, false) == 0.0f && RingGustFrontDebris.SnowShare(TerrainBiomeId.Desert, false) == 0.0f, "forest and desert must not be snow");
            Check(RingGustFrontDebris.SnowShare(TerrainBiomeId.Tundra, true) == 0.0f, "water over tundra must not be snow");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("GUST_FRONT_DEBRIS=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("GUST_FRONT_DEBRIS=PASS");
        return 0;
    }
}
