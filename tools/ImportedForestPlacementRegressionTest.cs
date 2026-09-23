using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

public partial class ImportedForestPlacementRegressionTest : RefCounted
{
    public bool Run()
    {
        try
        {
            const long circumference = 981_748L * 32L; const int width = 318_000; const double cell = 30;
            var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true)
                ?? throw new InvalidOperationException("terrain settings missing"));
            using var sampler = new RingTerrainSampler(settings, true, circumference, 5_000_000.0, width, 8000);
            long cells = (long)Math.Ceiling(circumference / cell);
            // Canonical cells around the verified dense endpoint, not camera state.
            long tx = (long)Math.Floor(186000 / cell) / 8; long tz = (long)Math.Floor(141000 / cell) / 8;
            List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> first = RingBiomeDecorationLayer.BuildImportedForestReviewTile(sampler, tx, tz, cells, cell, settings.Seed, circumference, width);
            List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> again = RingBiomeDecorationLayer.BuildImportedForestReviewTile(sampler, tx, tz, cells, cell, settings.Seed, circumference, width);
            Require(first.Count > 0, "verified dense tile generated no records"); Require(Equal(first, again), "same tile generation changed records");
            List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> left = RingBiomeDecorationLayer.BuildImportedForestReviewTile(sampler, tx - 1, tz, cells, cell, settings.Seed, circumference, width);
            List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> right = RingBiomeDecorationLayer.BuildImportedForestReviewTile(sampler, tx + 1, tz, cells, cell, settings.Seed, circumference, width);
            // Repeat the neighboring requests in the opposite order: tile order
            // must not influence the canonical halo/tie-break outcome.
            List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> rightFirst = RingBiomeDecorationLayer.BuildImportedForestReviewTile(sampler, tx + 1, tz, cells, cell, settings.Seed, circumference, width);
            List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> leftSecond = RingBiomeDecorationLayer.BuildImportedForestReviewTile(sampler, tx - 1, tz, cells, cell, settings.Seed, circumference, width);
            Require(NoDuplicate(first, left, right), "adjacent tile order/ownership duplicated a canonical record");
            Require(Equal(left, leftSecond) && Equal(right, rightFirst), "adjacent generation order changed records");
            var combined = new List<RingBiomeDecorationLayer.ImportedForestPlacementRecord>(left.Count + first.Count + right.Count); combined.AddRange(left); combined.AddRange(first); combined.AddRange(right);
            Require(Valid(combined, sampler, circumference), "placement record violates slope spacing or catalog identity");
            string cachePath = ProjectSettings.GlobalizePath("res://.godot/forest_review/imported_cache_roundtrip.bin");
            Require(RingBiomeDecorationLayer.VerifyImportedForestCacheReview(sampler, tx, tz, cells, cell, settings.Seed, circumference, width, cachePath), "cache roundtrip/version/species rejection regression");
            GD.Print($"IMPORTED_FOREST_PLACEMENT_REGRESSION records={first.Count} left={left.Count} right={right.Count}"); return true;
        }
        catch (Exception e) { GD.PushError("IMPORTED_FOREST_PLACEMENT_REGRESSION=FAIL " + e.Message); return false; }
    }
    static bool Equal(IReadOnlyList<RingBiomeDecorationLayer.ImportedForestPlacementRecord> a, IReadOnlyList<RingBiomeDecorationLayer.ImportedForestPlacementRecord> b)
    { if(a.Count!=b.Count)return false; for(int i=0;i<a.Count;i++)if(!a[i].Equals(b[i]))return false; return true; }
    static bool NoDuplicate(params IReadOnlyList<RingBiomeDecorationLayer.ImportedForestPlacementRecord>[] sets)
    { var ids=new HashSet<ulong>(); foreach(var s in sets) foreach(var r in s) if(!ids.Add(r.Id)) return false; return true; }
    static bool Valid(IReadOnlyList<RingBiomeDecorationLayer.ImportedForestPlacementRecord> records, RingTerrainSampler sampler, long circumference)
    {
        var catalog = ImportedTreeCatalog.Load();
        var forest = new ForestRegionSampler(sampler.Seed, circumference, 318000);
        var radii = new float[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Variant < 0 || record.Variant >= catalog.Count || catalog[record.Variant].Species != record.Species || record.Width <= 0 || record.Height <= 0) return false;
            TerrainSample terrain = sampler.GetTerrainSample(record.Around, record.Across);
            float slope = Slope(sampler, record.Around, record.Across);
            radii[i] = RingBiomeDecorationLayer.ForestLocalSpacingMeters(terrain.Moisture, forest.Sample(record.Around, record.Across, terrain, slope).Density);
        }
        for (int i = 0; i < records.Count; i++) for (int j = i + 1; j < records.Count; j++)
        {
            double around = records[i].Around - records[j].Around;
            if (around > circumference * .5) around -= circumference;
            if (around < -circumference * .5) around += circumference;
            double across = records[i].Across - records[j].Across;
            float required = Math.Max(radii[i], radii[j]);
            if (around * around + across * across < required * required) return false;
        }
        return true;
    }
    static float Slope(RingTerrainSampler s,long a,int z){const int d=2;float x=s.GetTerrainSample(a+d,z).SurfaceHeight-s.GetTerrainSample(a-d,z).SurfaceHeight;float q=s.GetTerrainSample(a,z+d).SurfaceHeight-s.GetTerrainSample(a,z-d).SurfaceHeight;return Mathf.RadToDeg(Mathf.Atan(Mathf.Sqrt(x*x+q*q)/(2*d)));}
    static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
}
