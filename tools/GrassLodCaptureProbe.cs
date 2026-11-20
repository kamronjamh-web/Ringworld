using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Helper for `landscape_lod_switch_capture --grass-lod` (tools/landscape_baseline_common.gd run_grass_lod): finds a ground-cover flower card near a pose in the selected review world by
/// building the 32 m card tiles around it through RingGrassTileBuilder.Build with the production arguments (as WorldGenerationFingerprint step 4). TEST ONLY, not a test.
/// </summary>
public partial class GrassLodCaptureProbe : RefCounted
{
    const float Tile = 32f;
    const int Stride = RingGrassTileData.FloatsPerInstance;
    const long Circ = LandscapeWorld.Circumference;
    const int Width = LandscapeWorld.Width;

    /// <summary>
    /// Returns {found, around, across, variant, cluster, flowers, violet_pink, tiles, error}. Violet or pink (atlas variant 2 or 3) flowers are preferred, the pick being the one with the most
    /// violet or pink flowers within 3 m (itself included); without any, the flower nearest the pose.
    /// </summary>
    public Godot.Collections.Dictionary FindFlower(LandscapeWorldBridge world, double around, double across, double radiusM)
    {
        var result = new Godot.Collections.Dictionary { ["found"] = false };
        try
        {
            var tuning = RingGrassCardTuning.Default;
            var style = RingGrassStyleProfile.Build(new RingGrassStyleProfile.Inputs(RingStandingGrassLayer.GrassRenderStyleKind.Cards, 0, Tile, 400f, 0, 256, 0, tuning));
            RingGrassCardPlacement placement = style.PlacementForTier(0);
            int mainCap = style.MainCapPerTile;
            var clean = new RingLodSurfaceEditSnapshot(1, Circ, new Dictionary<RingTerrainColumnCoordinate, int>());
            using var sampler = world.SelectedSpec.CreateSampler();
            var flowers = new List<(double Around, double Across, int Variant)>();
            var probes = new List<RingGrassTileBuilder.GroundCoverProbe>();
            int span = (int)Math.Ceiling(radiusM / Tile) + 1, tiles = 0;
            long ca = (long)Math.Floor(around / Tile), cz = (long)Math.Floor(across / Tile);
            try
            {
                for (long ta = ca - span; ta <= ca + span; ta++)
                    for (long tz = cz - span; tz <= cz + span; tz++)
                    {
                        RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(ta, tz, Tile, Circ, Width);
                        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, Circ / Math.Tau, 0, 0, Circ, -1000, 1000,
                            new RingCanonicalPosition(bounds.AroundCenter, 0, bounds.AcrossCenter));
                        probes.Clear();
                        RingGrassTileBuilder.GroundCoverProbeForTests = probes.Add;
                        RingGrassTileData data = RingGrassTileBuilder.Build(new RingGrassTileKey(ta, tz), bounds, 8f, 9, 0f, mainCap, 25f, sampler, clean, geometry, CancellationToken.None,
                            exclusions: null, appearance: RingGrassAppearanceSnapshot.Default, sourceFraction: 1f, sourceTier: 0, useGrassClumps: false, modifiers: null,
                            modifierDensityStrength: 1f, meadowTufts: false, meadowSpread: 1f, minimumHeight: .10f, maximumHeight: .20f, useConfiguredHeightRange: true, cards: placement, groundCoverShare: 1f);
                        RingGrassTileBuilder.GroundCoverProbeForTests = null;
                        tiles++;
                        foreach (var p in probes)
                        {
                            if (!p.Placed || p.Kind != RingGrassCardKind.Flower) continue;
                            double da = p.Around - around, dz = p.Across - across;
                            if (da * da + dz * dz > radiusM * radiusM) continue;
                            // the variant is only in the buffer (CUSTOM.x = kind_slot, kind = slot / 4, variant = slot % 4): match the instance by its tile-local position (x = across, z = around offsets)
                            double lx = p.Across - bounds.AcrossCenter, lz = p.Around - bounds.AroundCenter, bestD = 1.0;
                            int variant = -1;
                            for (int i = 0; i < data.InstanceCount; i++)
                            {
                                int slot = (int)Math.Floor(data.Buffer[i * Stride + 16]);
                                if (slot / 4 != (int)RingGrassCardKind.Flower) continue;
                                double dx = data.Buffer[i * Stride + 3] - lx, dzz = data.Buffer[i * Stride + 11] - lz, d = dx * dx + dzz * dzz;
                                if (d < bestD) { bestD = d; variant = slot % 4; }
                            }
                            flowers.Add((p.Around, p.Across, variant));
                        }
                    }
            }
            finally { RingGrassTileBuilder.GroundCoverProbeForTests = null; }
            result["tiles"] = tiles; result["flowers"] = flowers.Count;
            int violetPink = 0;
            foreach (var f in flowers) if (f.Variant == 2 || f.Variant == 3) violetPink++;
            result["violet_pink"] = violetPink;
            if (flowers.Count == 0) { result["error"] = "no placed flower card within the radius"; return result; }
            int best = -1, bestCluster = -1; double bestDistance = double.MaxValue;
            for (int i = 0; i < flowers.Count; i++)
            {
                bool preferred = flowers[i].Variant == 2 || flowers[i].Variant == 3;
                if (violetPink > 0 && !preferred) continue;
                int cluster = 0;
                if (violetPink > 0)
                    for (int j = 0; j < flowers.Count; j++)
                    {
                        if (flowers[j].Variant != 2 && flowers[j].Variant != 3) continue;
                        double da = flowers[j].Around - flowers[i].Around, dz = flowers[j].Across - flowers[i].Across;
                        if (da * da + dz * dz <= 9d) cluster++;
                    }
                double pa = flowers[i].Around - around, pz = flowers[i].Across - across, distance = pa * pa + pz * pz;
                if (cluster > bestCluster || (cluster == bestCluster && distance < bestDistance)) { best = i; bestCluster = cluster; bestDistance = distance; }
            }
            result["found"] = true; result["around"] = flowers[best].Around; result["across"] = flowers[best].Across; result["variant"] = flowers[best].Variant;
            result["cluster"] = bestCluster; result["distance_to_pose"] = Math.Sqrt(bestDistance);
            return result;
        }
        catch (Exception exception) { result["error"] = exception.Message; return result; }
    }
}
