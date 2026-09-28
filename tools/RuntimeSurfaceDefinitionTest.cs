using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

public partial class RuntimeSurfaceDefinitionTest : RefCounted
{
    private int _checks;
    public bool Run()
    {
        try
        {
            TerrainGenerationParameters recipe = TerrainGenerationParameters.Default();
            using var settings = Settings(recipe.CanonicalJson);
            using var sampler = Sampler(settings);
            using var model = new TerrainGenerationHeightModel(recipe);
            Check(sampler.TerrainGeneratorVersion == 17 && sampler.UsesRuntimeSurfaceGeneration, "opt-in generator version");
            Check(sampler.SurfaceHeightOffset == 0 && sampler.SeaLevelY == 0, "absolute sea-level contract");
            Check(sampler.BedrockY == -8000 && sampler.MaximumPossibleSurfaceHeight == 8000, "full surface range");
            Check(!sampler.HasExceptionalSummit, "no legacy forced summit");
            for (int i = 0; i < 128; i++)
            {
                long around = i * 357914L - 64;
                int across = i * 7920 % 318000;
                float expected = (float)model.CalculateHeight(around, across);
                Check(sampler.GetPresentationSurfaceHeight(around, across) == expected, "production uses shared authority");
                Check(sampler.GetSmoothSurfaceHeight(around, across) == expected, "smooth height has no offset");
                Check(sampler.GetSurfaceHeight(around, across) == Mathf.FloorToInt(expected), "explicit integer floor compatibility");
                TerrainSample sample = sampler.GetTerrainSample(around, across);
                Check(sample.HasSurfaceWater == (expected < 0), "ocean follows exact height");
                Check(sample.WaterKind is SurfaceWaterKind.Ocean or SurfaceWaterKind.None, "no legacy hydrology carving");
            }
            TestFusedLatticeSampling(sampler, recipe.CanonicalJson);
            ulong frozenHash = sampler.TerrainDefinitionHash;
            float frozenHeight = sampler.GetPresentationSurfaceHeight(44, 158000);
            settings.Seed = 99;
            settings.SeaLevelY = 99;
            settings.SurfaceGenerationRecipeJson = "broken";
            settings.AltitudeTemperatureDropPerKilometre = 99;
            using (var copy = sampler.CreateIndependentCopy())
            {
                Check(copy.TerrainDefinitionHash == frozenHash && copy.Seed == 1337, "copy preserves frozen full definition");
                Check(copy.GetPresentationSurfaceHeight(44, 158000) == frozenHeight, "resource mutation cannot change terrain");
            }
            var workerCopiesMatch = new bool[64];
            Parallel.For(0, workerCopiesMatch.Length, i =>
            {
                using var worker = sampler.CreateIndependentCopy();
                workerCopiesMatch[i] = worker.TerrainDefinitionHash == frozenHash
                    && worker.GetPresentationSurfaceHeight(44, 158000) == frozenHeight;
            });
            Check(Array.TrueForAll(workerCopiesMatch, value => value), "parallel workers share frozen settings with independent noise");
            using (var changed = Settings(recipe.CanonicalJson))
            {
                changed.AltitudeTemperatureDropPerKilometre += .01f;
                using var changedSampler = Sampler(changed);
                Check(changedSampler.TerrainDefinitionHash != frozenHash, "climate change invalidates presentation caches");
            }
            Reject(() => Sampler(Settings(recipe.CanonicalJson), 8000), "old build ceiling rejected for full-range surface");
            using (var bad = Settings(recipe.CanonicalJson))
            {
                bad.EnableUnified3dDensityTerrain = true;
                Reject(() => Sampler(bad), "cave/density contract rejected");
                bad.EnableUnified3dDensityTerrain = false;
                bad.SurfaceGenerationRecipeJson = "";
                Reject(() => Sampler(bad), "missing recipe cannot become legacy terrain");
            }
            using (var legacySettings = new VoxelTerrainSettings())
            using (var legacy = Sampler(legacySettings, 8000))
            {
                Check(legacy.TerrainGeneratorVersion == 16 && !legacy.UsesRuntimeSurfaceGeneration, "legacy version unchanged");
                Check(legacy.SurfaceHeightOffset == .5f, "legacy half-voxel convention preserved");
                Check(legacy.GetPresentationSurfaceHeight(44, 158000) == legacy.GetSmoothSurfaceHeight(44, 158000) + .5f, "legacy presentation preserved");
            }
            Reject(() => TerrainGenerationParameters.FromJson(recipe.CanonicalJson.Replace("\"seed\":1337", "\"seed\":1337,\"seed\":1338")), "duplicate recipe keys rejected");
            Reject(() => TerrainGenerationParameters.FromJson(recipe.CanonicalJson.Replace("\"seed\":1337,", "")), "missing recipe keys rejected");
            TestEditsAndPyramid(sampler);
            TestTriangleQueries(sampler);
            TestNativeCollisionTriangles();
            TestConsumerHeights(sampler);
            TestGridSurfaceMaterials(recipe.CanonicalJson);
            GD.Print($"RUNTIME_SURFACE_DEFINITION=PASS checks={_checks}");
            return true;
        }
        catch (Exception error)
        {
            GD.PushError($"RUNTIME_SURFACE_DEFINITION=FAIL checks={_checks}: {error}");
            return false;
        }
    }

    private void TestNativeCollisionTriangles()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        float Height(long around, int across) => (around, across) switch
        {
            (64, 158000) => -2, (66, 158000) => 4,
            (64, 158002) => 8, (66, 158002) => 20, _ => 0
        };
        var field = new RingSurfaceHeightField(123, CertifiedRingScale.AroundVoxelLength,
            318000, -8000, 8000, Height, useTriangleInterpolation: true);
        var frame = new RingSurfaceFrame(64, 158000, 0, CertifiedRingScale.AroundVoxelLength,
            CertifiedRingScale.EffectiveRadiusMeters, 2, -1);
        var surface = new RingTerrain3DSurface();
        try
        {
            tree.Root.AddChild(surface);
            surface.Initialize(field, frame);
            Check(surface.IsReady, "real curved field collider initialized");
            var body = surface.GetNode<StaticBody3D>("RingTerrainSurfaceCollision");
            var shape = (ConcavePolygonShape3D)body.GetChild<CollisionShape3D>(0).Shape;
            Vector3[] faces = shape.Data;
            int cell = (95 * 192 + 96) * 6;
            foreach (var query in new[] { new Vector2(.25f, .75f), new Vector2(.75f, .25f), new Vector2(.5f, .5f) })
            {
                double around = 64 + query.X * 2, across = 158000 + query.Y * 2;
                Vector3 point = frame.MapPosition(around, across, field.SampleHeight(around, across));
                int start = cell + (query.X >= query.Y ? 3 : 0);
                Vector3 normal = (faces[start + 1] - faces[start]).Cross(faces[start + 2] - faces[start]).Normalized();
                Check(Math.Abs(normal.Dot(point - faces[start])) < .001,
                    "fractional query lies on actual curved native collider triangle");
            }
        }
        finally { surface.Free(); }
    }

    private void TestFusedLatticeSampling(RingTerrainSampler sampler, string recipeJson)
    {
        VerifyFusedParity(sampler, "default runtime recipe");
        TerrainGenerationParameters runtimeV1Recipe = TerrainGenerationParameters.LegacyDefault();
        using var runtimeV1Settings = Settings(runtimeV1Recipe.CanonicalJson);
        using var runtimeV1 = Sampler(runtimeV1Settings);
        Check(runtimeV1.UsesRuntimeSurfaceGeneration && runtimeV1.TerrainGeneratorVersion == 17,
            "reference-v1 recipe remains runtime generation");
        VerifyFusedParity(runtimeV1, "runtime reference-v1 recipe", requireKnownDryland: true);
        using var seededSettings = Settings(recipeJson.Replace("\"seed\":1337", "\"seed\":7331"));
        seededSettings.Seed = 7331;
        using var seeded = Sampler(seededSettings);
        VerifyFusedParity(seeded, "alternate runtime seed");
        using var legacySettings = new VoxelTerrainSettings();
        using var legacy = Sampler(legacySettings, 8000);
        VerifyFusedParity(legacy, "legacy fallback");

        const int count = 4096;
        for (int i = 0; i < 256; i++)
        {
            _ = sampler.GetPresentationSurfaceHeight(i * 2L, 158000);
            _ = sampler.GetBiomeTextureWeights(i * 2L, 158000);
            sampler.GetPresentationSurfaceHeightAndBiomeWeights(i * 2L, 158000, out _, out _);
        }
        var separateRounds = new List<double>();
        var fusedRounds = new List<double>();
        for (int round = 0; round < 4; round++)
        {
            if ((round & 1) == 0)
            {
                separateRounds.Add(TimeSeparateCalls(sampler, count));
                fusedRounds.Add(TimeFusedCalls(sampler, count));
            }
            else
            {
                fusedRounds.Add(TimeFusedCalls(sampler, count));
                separateRounds.Add(TimeSeparateCalls(sampler, count));
            }
        }
        GD.Print($"RUNTIME_FUSED_LATTICE_TIMING separate_median_ms={Median(separateRounds):0.###} fused_median_ms={Median(fusedRounds):0.###} samples={count} rounds=4");
    }

    private static double TimeSeparateCalls(RingTerrainSampler sampler, int count)
    {
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            long around = (i * 2L) % sampler.AroundRingVoxelLength;
            _ = sampler.GetPresentationSurfaceHeight(around, 158000);
            _ = sampler.GetBiomeTextureWeights(around, 158000);
        }
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double TimeFusedCalls(RingTerrainSampler sampler, int count)
    {
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
            sampler.GetPresentationSurfaceHeightAndBiomeWeights((i * 2L) % sampler.AroundRingVoxelLength, 158000, out _, out _);
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) * .5 : values[middle];
    }

    private void VerifyFusedParity(RingTerrainSampler sampler, string label, bool requireKnownDryland = false)
    {
        long circumference = sampler.AroundRingVoxelLength;
        foreach ((long around, int across) in new[]
        {
            (-2L, 0), (0L, 0), (2L, 2), (64L, 158000),
            (circumference - 2, 317998), (circumference, 0),
            (1L, 1), (63L, 158001), (circumference - 1, 317999), (0L, 318000)
        })
        {
            VerifyFusedPoint(sampler, label, around, across);
        }
        bool foundLand = false, foundOcean = false, foundBiomeWeight = false;
        const int aroundSamples = 16, acrossSamples = 9;
        for (int aroundIndex = 0; aroundIndex < aroundSamples; aroundIndex++)
        for (int acrossIndex = 0; acrossIndex < acrossSamples; acrossIndex++)
        {
            long around = (aroundIndex * circumference / (aroundSamples - 1)) & ~1L;
            int across = (acrossIndex * (sampler.RingAcrossWidthMeters - 2) / (acrossSamples - 1)) & ~1;
            TerrainSample sample = sampler.GetTerrainSample(around, across);
            foundLand |= !sample.HasSurfaceWater;
            foundOcean |= sample.HasSurfaceWater;
            RingTerrainSampler.BiomeTextureWeights weights = VerifyFusedPoint(sampler, label + " distributed grid", around, across);
            foundBiomeWeight |= weights.Desert != 0 || weights.Forest != 0 || weights.Tundra != 0 || weights.Alpine != 0;
        }
        Check(foundLand && foundOcean, $"fused parity climate sweep includes land and ocean ({label})");
        Check(foundBiomeWeight, $"fused parity climate sweep includes biome weights ({label})");
        if (requireKnownDryland)
        {
            const long drylandAround = 16_443_476;
            const int drylandAcross = 38_692;
            TerrainSample dryland = sampler.GetTerrainSample(drylandAround, drylandAcross);
            Check(!dryland.HasSurfaceWater, $"known runtime reference-v1 dryland remains dry ({label})");
            RingTerrainSampler.BiomeTextureWeights weights = VerifyFusedPoint(sampler, label + " known dryland", drylandAround, drylandAcross);
            Check(weights.Desert != 0 || weights.Forest != 0 || weights.Tundra != 0 || weights.Alpine != 0,
                $"known runtime reference-v1 dryland has biome weights ({label})");
        }
        // The fused API is intentionally lattice-only. These fractional
        // presentation queries remain on the existing triangle-lattice path.
        Check(float.IsFinite(sampler.GetPresentationSurfaceHeight(62.25, 158000.75)),
            $"fractional presentation fallback remains finite ({label})");
    }

    private RingTerrainSampler.BiomeTextureWeights VerifyFusedPoint(RingTerrainSampler sampler, string label, long around, int across)
    {
        float expectedHeight = sampler.GetPresentationSurfaceHeight(around, across);
        RingTerrainSampler.BiomeTextureWeights expectedWeights = sampler.GetBiomeTextureWeights(around, across);
        sampler.GetPresentationSurfaceHeightAndBiomeWeights(around, across, out float actualHeight, out RingTerrainSampler.BiomeTextureWeights actualWeights);
        Check(BitConverter.SingleToInt32Bits(actualHeight) == BitConverter.SingleToInt32Bits(expectedHeight),
            $"fused height parity {label} ({around},{across})");
        Check(BitConverter.SingleToInt32Bits(actualWeights.Desert) == BitConverter.SingleToInt32Bits(expectedWeights.Desert)
            && BitConverter.SingleToInt32Bits(actualWeights.Forest) == BitConverter.SingleToInt32Bits(expectedWeights.Forest)
            && BitConverter.SingleToInt32Bits(actualWeights.Tundra) == BitConverter.SingleToInt32Bits(expectedWeights.Tundra)
            && BitConverter.SingleToInt32Bits(actualWeights.Alpine) == BitConverter.SingleToInt32Bits(expectedWeights.Alpine),
            $"fused biome parity {label} ({around},{across})");
        using var expectedImage = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        using var actualImage = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        expectedImage.SetPixel(0, 0, expectedWeights.ToVertexColor());
        actualImage.SetPixel(0, 0, actualWeights.ToVertexColor());
        Check(expectedImage.GetData().AsSpan().SequenceEqual(actualImage.GetData().AsSpan()),
            $"fused packed RGBA8 parity {label} ({around},{across})");
        return actualWeights;
    }

    private void TestTriangleQueries(RingTerrainSampler sampler)
    {
        var field = new RingSurfaceHeightField(sampler.TerrainDefinitionHash, sampler.AroundRingVoxelLength,
            318000, -8000, 8000, sampler.GetPresentationSurfaceHeight, useTriangleInterpolation: true);
        var empty = new RingLodSurfaceEditSnapshot(0, sampler.AroundRingVoxelLength,
            new Dictionary<RingTerrainColumnCoordinate, int>(), new Dictionary<RingTerrainColumnCoordinate, float>(), true);
        foreach (double around in new[] { -1.75, -1.0, -.25, 0, .25, 1, 1.75, 62.25, sampler.AroundRingVoxelLength + .75 })
        foreach (double across in new[] { 0, .25, 1.0, 158000.25, 158001.75, 317998.0 })
        {
            float expected = field.SampleHeight(around, across);
            Check(sampler.GetPresentationSurfaceHeight(around, across) == expected, "sampler and collision lattice agree at fractions/seam");
            Check(empty.GetPresentationSurfaceHeight(sampler, around, across) == expected, "unedited snapshot shares collision lattice");
        }
        Check(sampler.GetPresentationSurfaceHeight(62.25, 318000.0) == field.SampleHeight(62.25, 317998),
            "public border clamps to final collision row");
        var vertices = new Dictionary<RingTerrainColumnCoordinate, float>
        {
            [new(64, 158000)] = -2, [new(66, 158000)] = 4,
            [new(64, 158002)] = 8, [new(66, 158002)] = 20
        };
        foreach (var pair in vertices)
            field.ApplyBrush(pair.Key.AroundVoxel, pair.Key.AcrossVoxel, 1,
                pair.Value - field.GetSample(pair.Key.AroundVoxel, pair.Key.AcrossVoxel));
        // Account for floating-point addition when writing a large procedural baseline.
        foreach (var key in new List<RingTerrainColumnCoordinate>(vertices.Keys))
            vertices[key] = field.GetSample(key.AroundVoxel, key.AcrossVoxel);
        var edited = new RingLodSurfaceEditSnapshot(1, sampler.AroundRingVoxelLength,
            new Dictionary<RingTerrainColumnCoordinate, int>(), vertices, true);
        foreach (var query in new[] { new Vector2(.25f, .75f), new Vector2(.75f, .25f), new Vector2(.5f, .5f) })
        {
            // Terrain3D's local negative diagonal becomes canonical positive
            // diagonal because the gameplay frame has AcrossDirection=-1.
            float expected = query.X >= query.Y
                ? vertices[new(64, 158000)] * (1 - query.X) + vertices[new(66, 158000)] * (query.X - query.Y) + vertices[new(66, 158002)] * query.Y
                : vertices[new(64, 158000)] * (1 - query.Y) + vertices[new(64, 158002)] * (query.Y - query.X) + vertices[new(66, 158002)] * query.X;
            double around = 64 + 2 * query.X, across = 158000 + 2 * query.Y;
            Check(Math.Abs(field.SampleHeight(around, across) - expected) < .00001f, "field uses triangle weights, not bilinear saddle");
            Check(edited.GetPresentationSurfaceHeight(sampler, around, across) == field.SampleHeight(around, across),
                "immutable edited snapshot agrees at fractional collider positions");
        }
    }

    private void TestConsumerHeights(RingTerrainSampler sampler)
    {
        // Invoke the real worker sample cache and geometry helpers. A rounded
        // compatibility overlay intentionally disagrees at this shallow shore.
        Type sector = typeof(RingTerrainProxySector);
        Type cacheType = sector.GetNestedType("TerrainBuildSampleCache", BindingFlags.NonPublic)!;
        foreach (float editedHeight in new[] { -.25f, .25f, 4321.125f })
        {
            var edits = new RingLodSurfaceEditSnapshot(1, sampler.AroundRingVoxelLength,
                new Dictionary<RingTerrainColumnCoordinate, int> { [new(64, 158000)] = Mathf.RoundToInt(editedHeight) },
                new Dictionary<RingTerrainColumnCoordinate, float> { [new(64, 158000)] = editedHeight }, true);
            object cache = Activator.CreateInstance(cacheType, edits, sampler, 318000)!;
            object point = cacheType.GetMethod("Get")!.Invoke(cache, new object[] { 64L, 158000 })!;
            float rendered = (float)sector.GetMethod("GetTerrainRenderHeight", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new[] { point, (object)true })!;
            float water = (float)sector.GetMethod("GetWaterRenderHeight", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new[] { point })!;
            bool wet = (bool)point.GetType().GetProperty("IsDisplayedWater")!.GetValue(point)!;
            Check(rendered == editedHeight, "proxy geometry preserves fractional override without offset");
            Check(wet == (editedHeight < 0) && water == Math.Max(0, editedHeight), "proxy shore and water use exact zero plane");
            Check(RingBiomeDecorationLayer.PresentationSurfaceHeight(edits, sampler, 64, 158000) == editedHeight,
                "tree root placement agrees with exact surface");
        }
    }

    private void TestGridSurfaceMaterials(string json)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var grid = new VoxelChunkGridManager
        {
            BuildOnReady = false, DormantEditableBackend = true, EnableChunkStreaming = false,
            UseTerrain3DSurface = true, UseDeterministicTerrainGeneration = true,
            UseRingCurvedPlacement = true, AroundRingChunkLength = CertifiedRingScale.AroundChunkCount,
            RingAcrossWidthMeters = 318000, MaxBuildGlobalY = 8400,
            MinExcavationGlobalY = -8192, TerrainBottomGlobalY = -8192,
            TerrainSettings = Settings(json)
        };
        try
        {
            tree.Root.AddChild(grid);
            using var sampler = grid.CreateIndependentTerrainSampler()!;
            var field = new RingSurfaceHeightField(sampler.TerrainDefinitionHash, sampler.AroundRingVoxelLength,
                318000, -8000, 8000, sampler.GetPresentationSurfaceHeight, useTriangleInterpolation: true);
            grid.AttachSurfaceHeightField(field);
            field.ApplyBrush(64, 158000, 1, -20000);
            Check(grid.GetCanonicalBlockType(64, -7999, 158000) == BlockType.Air,
                "deep surface has no phantom protected roof");
            Check(!grid.IsSolidCanonicalVoxel(64, -7999, 158000), "solid query agrees above deep surface");
            Check(grid.GetCanonicalBlockType(64, -8000, 158000) == BlockType.Bedrock, "surface bottom bedrock policy");
            field.ApplyBrush(64, 158000, 1, 8000.25f);
            Check(grid.GetCanonicalBlockType(64, 0, 158000) == BlockType.Soil, "fractional surface soil material");
            Check(grid.GetCanonicalBlockType(64, 1, 158000) == BlockType.Air, "fractional material boundary is not rounded up");
            grid.DetachSurfaceHeightField(field);
        }
        finally { grid.Free(); }
    }

    private void TestEditsAndPyramid(RingTerrainSampler sampler)
    {
        var field = new RingSurfaceHeightField(sampler.TerrainDefinitionHash, CertifiedRingScale.AroundVoxelLength,
            CertifiedRingScale.RingWidthMeters, -8000, 8000, sampler.GetPresentationSurfaceHeight, useTriangleInterpolation: true);
        float before = field.GetSample(0, 158000);
        Check(field.ApplyBrush(0, 158000, 1, .25f), "fractional brush");
        float edited = field.GetSample(0, 158000);
        Check(edited == before + .25f, "fractional edit retained");
        var copy = new RingSurfaceHeightField(sampler.TerrainDefinitionHash, CertifiedRingScale.AroundVoxelLength,
            CertifiedRingScale.RingWidthMeters, -8000, 8000, sampler.GetPresentationSurfaceHeight, useTriangleInterpolation: true);
        string directory = ProjectSettings.GlobalizePath("res://.godot/terrain-generation-tests/m5-contract");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "surface_runtime_edits_v1.json");
        Check(field.Save(path).Success && copy.Load(path).Success, "sparse absolute edit round trip");
        Check(copy.GetSample(0, 158000) == edited, "reload preserves fractional value");
        var other = new RingSurfaceHeightField(sampler.TerrainDefinitionHash ^ 1UL, CertifiedRingScale.AroundVoxelLength,
            CertifiedRingScale.RingWidthMeters, -8000, 8000, sampler.GetPresentationSurfaceHeight);
        Check(!other.Load(path).Success && other.EditCount == 0, "mismatched edit identity rejected before restore");
        field.ApplyBrush(0, 158000, 1, -20000);
        field.ApplyBrush(2, 158000, 1, 20000);
        Check(field.GetSample(0, 158000) == -8000 && field.GetSample(2, 158000) == 8000, "full range survives editing without protected roof clamp");

        var floats = new Dictionary<RingTerrainColumnCoordinate, float> { [new(0, 158000)] = -.25f };
        var edits = new RingLodSurfaceEditSnapshot(1, sampler.AroundRingVoxelLength,
            new Dictionary<RingTerrainColumnCoordinate, int>(), floats, true);
        floats[new(0, 158000)] = 123f;
        Check(edits.GetPresentationSurfaceHeight(sampler, 0, 158000) == -.25f, "snapshot owns immutable float inputs");
        Check(edits.GetPresentationTerrainSample(sampler, 0, 158000).WaterKind == SurfaceWaterKind.Ocean, "fractional below-sea edit produces ocean");
        var dryEdits = new RingLodSurfaceEditSnapshot(2, sampler.AroundRingVoxelLength,
            new Dictionary<RingTerrainColumnCoordinate, int>(), new Dictionary<RingTerrainColumnCoordinate, float> { [new(0, 158000)] = .25f }, true);
        Check(!dryEdits.GetPresentationTerrainSample(sampler, 0, 158000).HasSurfaceWater, "fractional above-sea edit removes ocean");

        var pyramid = new RingTerrainMinMaxPyramid(sampler, 318000, 1000000);
        var baseline = pyramid.Query(0, 158000, 1000000);
        var peak = new RingLodSurfaceEditSnapshot(3, sampler.AroundRingVoxelLength,
            new Dictionary<RingTerrainColumnCoordinate, int>(), new Dictionary<RingTerrainColumnCoordinate, float> { [new(0, 158000)] = 7999.75f }, true);
        pyramid.ApplyEdits(peak);
        Check(pyramid.Query(0, 158000, 1000000).MaxHeight >= 7999.75f, "shadow hierarchy bounds fractional peak conservatively");
        pyramid.ApplyEdits(new RingLodSurfaceEditSnapshot(4, sampler.AroundRingVoxelLength,
            new Dictionary<RingTerrainColumnCoordinate, int>(), new Dictionary<RingTerrainColumnCoordinate, float>(), true));
        Check(pyramid.Query(0, 158000, 1000000) == baseline, "clearing float edit restores shadow baseline");
        Check(pyramid.ValidateHierarchy(out _), "conservative shadow hierarchy");
        string cachePath = Path.Combine(directory, "pyramid.bin");
        pyramid.SaveCache(cachePath);
        Check(RingTerrainMinMaxPyramid.TryLoadCache(cachePath, sampler, 318000, 1000000, default, out var restored)
            && restored!.Query(0, 158000, 1000000) == baseline, "versioned float pyramid round trip");
    }

    private static VoxelTerrainSettings Settings(string json) => new()
    {
        SurfaceGenerationRecipeJson = json,
        SurfaceGenerationContractId = RingTerrainSampler.RuntimeSurfaceGenerationContractId,
        BedrockY = -8000, SeaLevelY = 0, EnableUnified3dDensityTerrain = false,
        EnableProceduralCaves = false, EnableNaturalOverhangs = false, EnableDesignedCaveDensityComposition = false
    };
    private static RingTerrainSampler Sampler(VoxelTerrainSettings settings, int ceiling = 8400) => new(settings, true,
        CertifiedRingScale.AroundVoxelLength, CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters, ceiling);
    private void Check(bool valid, string label) { _checks++; if (!valid) throw new InvalidOperationException(label); }
    private void Reject(Func<object> operation, string label)
    {
        bool rejected = false;
        try { if (operation() is IDisposable disposable) disposable.Dispose(); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, label);
    }
}
