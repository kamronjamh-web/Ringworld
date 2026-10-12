using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Test-only probes for the scaled regional LOD layout.</summary>
public partial class RingScaledFarViewport
{
    public void AimGeometryContractCameraAtFarRing() => AimSourceCameraAtOppositeRing();
    private int _asyncGeometryContractPhase;
    private long _asyncGeometryContractVersion;
    private ulong _asyncGeometryContractEdit;
    private int _asyncGeometryContractFrames;
    private int _asyncRegionalContractPhase;
    private int _asyncRegionalContractFrames;

    public string RunAsyncRegionalGeometryLifecycleContract()
    {
        if (_asyncRegionalContractPhase == 3) return "PASS";
        if (!RegionalFarLodsEnabled || _regionalFarViewport == null) return "FAIL: regional fixture needs an initialized regional renderer.";
        if (++_asyncRegionalContractFrames > 6000) return "FAIL: regional fixture exceeded its frame ceiling.";
        if (_asyncRegionalContractPhase == 0)
        {
            if (_farRegionalTerrains.Count == 0) return "PENDING";
            InvalidateRegionalGeometry();
            _asyncRegionalContractPhase = 1;
            return "PENDING";
        }
        if (_farRegionalStagingTerrains.Exists(mesh => mesh.Visible)) return "FAIL: regional staging was visible before publication.";
        if (_farRegionalTerrains.Count == 0 || _farRegionalTerrains.Exists(mesh => !mesh.Visible)) return "FAIL: regional replacement removed the prior published set.";
        if (_asyncRegionalContractPhase == 1 && (_farRegionalBuildTask != null || _farRegionalUpload != null))
        {
            InvalidateRegionalGeometry();
            _asyncRegionalContractPhase = 2;
            return "PENDING";
        }
        if (_asyncRegionalContractPhase == 2 && _farRegionalBuildTask == null && _farRegionalUpload == null
            && _lastFarRegionalAnchorAround != long.MinValue && _lastFarRegionalEditVersion == _grid?.LodSurfaceEditVersion)
        {
            if (_farRegionalStagingTerrains.Count != 0) return "FAIL: regional publication retained staging meshes.";
            _asyncRegionalContractPhase = 3;
            return "PASS";
        }
        return "PENDING";
    }

    /// <summary>Poll once per scene frame while the real renderer is processing.</summary>
    public string RunAsyncGeometryLifecycleContract()
    {
        if (_asyncGeometryContractPhase == 3) return "PASS";
        if (_grid == null || _farViewport == null) return "FAIL: async geometry fixture needs an initialized far renderer.";
        if (++_asyncGeometryContractFrames > 6000) return "FAIL: async geometry publication exceeded the fixture frame limit.";
        if (_asyncGeometryContractPhase == 0)
        {
            if (_globalSurfaceActive.Count == 0) return "FAIL: startup global coverage is absent.";
            _asyncGeometryContractEdit = _grid.LodSurfaceEditVersion;
            _globalGeometryVersion++;
            RequestGlobalSurfaceBuild();
            _asyncGeometryContractPhase = 1;
            return "PENDING";
        }
        if (_globalSurfaceStaging.Exists(mesh => mesh.Visible)) return "FAIL: incomplete global geometry was visible.";
        if (_globalSurfaceActive.Count == 0 || _globalSurfaceActive.Exists(mesh => !mesh.Visible)) return "FAIL: replacement removed prior global coverage.";
        if (_asyncGeometryContractPhase == 1 && (_globalSurfaceBuild != null || _globalSurfaceUpload != null))
        {
            // Invalidate a real pending generation, then prove the staged set is
            // never published and the replacement still eventually appears.
            _globalGeometryVersion++;
            _asyncGeometryContractVersion = _globalGeometryVersion;
            _asyncGeometryContractPhase = 2;
            return "PENDING";
        }
        if (_asyncGeometryContractPhase == 2 && _globalPublishedLayoutVersion == _asyncGeometryContractVersion)
        {
            if (_lastSeededSurfaceEditVersion != _asyncGeometryContractEdit) return "FAIL: current geometry lost the edit snapshot identity.";
            if (_globalSurfaceStaging.Count != 0 || _globalSurfaceUpload != null) return "FAIL: publication retained an incomplete staging set.";
            _asyncGeometryContractPhase = 3;
            return "PASS";
        }
        return _asyncGeometryContractPhase == 3 ? "PASS" : "PENDING";
    }

    public string RunShiftedAnchorCoverageContract()
    {
        if (_terrainMaterial == null || _explorer == null || _grid?.TerrainSampler == null)
            return "FAIL: shifted-anchor fixture needs an initialized far renderer.";
        bool regional = RegionalFarLodsEnabled;
        float start = FadeStartMeters, end = FadeEndMeters, minimum = MinimumLocalTerrainCutoutMeters;
        RingCanonicalPosition original = _explorer.CanonicalPosition;
        try
        {
            RegionalFarLodsEnabled = true;
            FadeStartMeters = FadeEndMeters = MinimumLocalTerrainCutoutMeters = 7960000.0f;
            RingCanonicalPosition shifted = original with { AroundMeters = _regionalRenderOriginAround + 250000.0 };
            UpdateTerrainFallbackCutout(shifted);
            if (CurrentTerrainFallbackCutoutMeters > 125000.0f)
                return "FAIL: actual global material cutout hides shifted regional coverage gaps.";
            float shaderCutout = _terrainMaterial.GetShaderParameter("fade_end_meters").AsSingle();
            if (shaderCutout > 125000.0f * CoordinateScale + Math.Max(0.001f, CoordinateScale) * 1.1f)
                return "FAIL: coverage-safe cutout was not applied to the actual shader template.";
            return "PASS";
        }
        finally
        {
            RegionalFarLodsEnabled = regional;
            FadeStartMeters = start;
            FadeEndMeters = end;
            MinimumLocalTerrainCutoutMeters = minimum;
            UpdateTerrainFallbackCutout(original);
        }
    }

    public string RunAdaptiveGeometryContract()
    {
        const float nearReach = 125000.0f, regionalOuter = 7960000.0f, cameraOffset = 250000.0f;
        float safeCutout = ResolveCoverageSafeGlobalCutout(regionalOuter, true, nearReach);
        bool Covered(float point, float cutout) => Math.Abs(point - cameraOffset) <= nearReach
            || (Math.Abs(point) >= nearReach && Math.Abs(point) <= regionalOuter)
            || Math.Abs(point - cameraOffset) >= cutout;
        if (Covered(0, regionalOuter) || Covered(8000000, regionalOuter))
            return "FAIL: shifted-anchor fixture no longer reproduces the legacy coverage gaps.";
        if (!Covered(0, safeCutout) || !Covered(8000000, safeCutout)
            || ResolveCoverageSafeGlobalCutout(regionalOuter, false, nearReach) != regionalOuter)
            return "FAIL: shifted regional anchor lost inner or outer global fallback coverage.";
        int low = SelectAngularDetail(200, 2, 96, 768, 0.25);
        int high = SelectAngularDetail(100000, 2, low, 768, 0.25);
        if (high <= low || high > 768 || high % 96 != 0)
            return "FAIL: screen-space angular refinement is not bounded and responsive.";
        int held = SelectAngularDetail(3280, 2, 192, 384, 0.25);
        if (held != 192) return "FAIL: angular refinement hysteresis dropped a near-threshold lattice.";
        const int columns = 13, rows = 3;
        var vertices = new Vector3[columns * rows];
        var normals = new Vector3[vertices.Length];
        var colors = new Color[vertices.Length];
        var uv = new Vector2[vertices.Length];
        var around = new double[vertices.Length];
        var across = new double[vertices.Length];
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            int index = row * columns + column;
            vertices[index] = new Vector3(column, row, (column % 3) * 0.25f);
            normals[index] = Vector3.Up;
            colors[index] = Colors.White;
            around[index] = 75_000_000_000.0 + column * 64.0;
            across[index] = row * 32.0;
        }
        var indices = new int[(rows - 1) * (columns - 1) * 6];
        int cursor = 0;
        for (int row = 0; row < rows - 1; row++)
        for (int column = 0; column < columns - 1; column++)
        {
            int a = row * columns + column;
            indices[cursor++] = a; indices[cursor++] = a + 1; indices[cursor++] = a + columns;
            indices[cursor++] = a + 1; indices[cursor++] = a + columns + 1; indices[cursor++] = a + columns;
        }
        var source = new FarRegionalBandData("Fixture", vertices, normals, colors, uv, indices, 0, 12, 12, around, across);
        IReadOnlyList<FarRegionalBandData> sectors = SplitFarRegionalBand(source, 5);
        int triangleIndices = 0;
        for (int sectorIndex = 0; sectorIndex < sectors.Count; sectorIndex++)
        {
            FarRegionalBandData sector = sectors[sectorIndex];
            triangleIndices += sector.Indices.Length;
            Aabb bounds = TightGeometryBounds(sector.Vertices);
            foreach (Vector3 vertex in sector.Vertices)
                if (!bounds.HasPoint(vertex)) return "FAIL: sector bounds reject an installed vertex.";
            foreach (int index in sector.Indices)
                if (index < 0 || index >= sector.Vertices.Length) return "FAIL: sector remap produced invalid topology.";
            Vector2[] localUv = BuildTileUvs(sector.CanonicalAround, sector.CanonicalAcross, out _, out _, out _, out _);
            foreach (Vector2 value in localUv)
                if (value.X < 0 || value.X > 1 || value.Y < 0 || value.Y > 1) return "FAIL: canonical tile UV exceeded sector bounds.";
            if (sectorIndex == 0) continue;
            FarRegionalBandData previous = sectors[sectorIndex - 1];
            for (int row = 0; row < rows; row++)
            {
                int previousIndex = row * (previous.AngularSteps + 1) + previous.AngularSteps;
                int nextIndex = row * (sector.AngularSteps + 1);
                if (previous.Vertices[previousIndex] != sector.Vertices[nextIndex] || previous.Normals[previousIndex] != sector.Normals[nextIndex]
                    || previous.CanonicalAround[previousIndex] != sector.CanonicalAround[nextIndex])
                    return "FAIL: independently culled sectors lost a shared seam.";
            }
        }
        if (triangleIndices != indices.Length) return "FAIL: sector subdivision lost or duplicated triangles.";
        if (!CanPublishGeometry(2, 3, 2, 3) || CanPublishGeometry(1, 3, 2, 3) || CanPublishGeometry(2, 1, 2, 3))
            return "FAIL: stale geometry publication accepted an edit or layout mismatch.";
        return "PASS";
    }
    internal static IReadOnlyList<FarRegionalBandSpec> GetFarRegionalBandSpecsFixture(bool intermediateTiers)
        => GetFarRegionalBandSpecs(intermediateTiers);

    internal static bool AreFarRegionalBandSpecsContiguousFixture(bool intermediateTiers)
    {
        IReadOnlyList<FarRegionalBandSpec> bands = GetFarRegionalBandSpecs(intermediateTiers);
        if (bands.Count == 0) return false;
        for (int index = 0; index < bands.Count; index++)
        {
            FarRegionalBandSpec band = bands[index];
            if (band.InnerRadius < 0.0 || band.OuterRadius <= band.InnerRadius || band.SampleSpacing <= 0.0)
                return false;
            if (index > 0)
            {
                FarRegionalBandSpec previous = bands[index - 1];
                if (previous.OuterRadius != band.InnerRadius || previous.SampleSpacing >= band.SampleSpacing)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Builds actual polar-band data at the circumference seam and a clamped
    /// across edge. Adjacent bands must share their radial boundary positions;
    /// they intentionally use the same 96 angular samples.
    /// </summary>
    public static bool RunFarRegionalTransitionContract(
        RingTerrainSampler sampler,
        RingLodSurfaceEditSnapshot edits,
        out string failure)
    {
        if (sampler == null) throw new ArgumentNullException(nameof(sampler));
        IReadOnlyList<FarRegionalBandSpec> bands = GetFarRegionalBandSpecs(true);
        if (!AreFarRegionalBandSpecsContiguousFixture(true)
            || bands.Count != 7
            || bands[0].InnerRadius != 125000.0
            || bands[^1].OuterRadius != 7960000.0)
        {
            failure = "Intermediate scaled-far band layout is not the expected contiguous 512m through 32768m chain.";
            return false;
        }

        const double circumference = 8_000_000.0;
        const int width = 256;
        const float scale = 0.001f;
        long anchorAround = (long)circumference - 64L;
        int anchorAcross = 0; // Forces the negative half of every polar ring to clamp.
        double radius = circumference / Math.Tau;
        Color low = new(0.12f, 0.19f, 0.12f);
        Color high = new(0.29f, 0.27f, 0.20f);
        var built = new List<FarRegionalBandData>(bands.Count);
        foreach (FarRegionalBandSpec band in bands)
        {
            FarRegionalBandData data = BuildFarRegionalBandData(
                band.Name, sampler, anchorAround, anchorAcross,
                band.InnerRadius, band.OuterRadius, band.SampleSpacing,
                circumference, radius, width, scale, low, high, edits);
            if (data.Vertices.Length > 257 * 97)
            {
                failure = $"{band.Name} exceeded the 256 radial-row cap.";
                return false;
            }
            built.Add(data);
        }
        WeldFarRegionalBandNormals(built);

        const int row = 97;
        for (int index = 1; index < built.Count; index++)
        {
            FarRegionalBandData previous = built[index - 1];
            FarRegionalBandData next = built[index];
            int previousLastRow = previous.Vertices.Length - row;
            for (int angular = 0; angular < row; angular++)
            {
                if (!previous.Vertices[previousLastRow + angular].IsEqualApprox(next.Vertices[angular]))
                {
                    failure = $"Shared boundary mismatch between {previous.Name} and {next.Name} at angular column {angular}.";
                    return false;
                }
                if (!previous.Normals[previousLastRow + angular].IsEqualApprox(next.Normals[angular]))
                {
                    failure = $"Shared normal mismatch between {previous.Name} and {next.Name} at angular column {angular}.";
                    return false;
                }
            }
        }
        failure = string.Empty;
        return true;
    }

    /// <summary>Scene-facing contract entry point for the headless GDScript launcher.</summary>
    public string RunFarRegionalTransitionContract()
    {
        VoxelChunkGridManager? grid = _grid ?? GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        if (grid?.TerrainSampler is not RingTerrainSampler sampler)
            return "FAIL: Scaled far viewport has no terrain sampler.";
        RingLodSurfaceEditSnapshot edits = grid.CaptureLodSurfaceEditSnapshot();
        using RingTerrainSampler workerSampler = sampler.CreateIndependentCopy();
        return RunFarRegionalTransitionContract(workerSampler, edits, out string failure)
            ? "PASS"
            : $"FAIL: {failure}";
    }

    /// <summary>Standalone variant used by the headless contract launcher.</summary>
    public string RunStandaloneFarRegionalTransitionContract()
    {
        const long circumference = 8_000_000L;
        const int width = 256;
        var settings = new VoxelTerrainSettings();
        using var sampler = new RingTerrainSampler(
            settings, periodicAroundRing: true, circumference,
            circumference / Math.Tau, width, maxBuildGlobalY: 2048);
        var edits = new RingLodSurfaceEditSnapshot(
            version: 0UL, circumference,
            new Dictionary<RingTerrainColumnCoordinate, int>());
        return RunFarRegionalTransitionContract(sampler, edits, out string failure)
            ? "PASS"
            : $"FAIL: {failure}";
    }

    public string RunAdaptiveTerrainTransitionContract()
    {
        const long circumference = 8_000_000L;
        const int width = 256;
        var settings = new VoxelTerrainSettings();
        using var sampler = new RingTerrainSampler(settings, periodicAroundRing: true, circumference, circumference / Math.Tau, width, maxBuildGlobalY: 2048);
        var edits = new RingLodSurfaceEditSnapshot(0UL, circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        var built = new List<FarRegionalBandData>();
        foreach (FarRegionalBandSpec band in GetFarRegionalBandSpecs(true))
        {
            FarRegionalBandData data = BuildFarRegionalBandData(band.Name, sampler, circumference - 64, 0,
                band.InnerRadius, band.OuterRadius, band.SampleSpacing, circumference, circumference / Math.Tau,
                width, 0.001f, Colors.DarkGreen, Colors.Gray, edits, 192, 32.0, default, 64);
            if (data.Vertices.Length > 65 * 193) return "FAIL: adaptive radial cap exceeded.";
            built.Add(data);
        }
        WeldFarRegionalBandNormals(built);
        const int row = 193;
        for (int band = 1; band < built.Count; band++)
        for (int column = 0; column < row; column++)
        {
            int previous = built[band - 1].Vertices.Length - row + column;
            if (built[band - 1].Vertices[previous] != built[band].Vertices[column]
                || built[band - 1].Normals[previous] != built[band].Normals[column])
                return "FAIL: adaptive tier lattice boundary is not deterministic.";
        }
        var global = BuildGlobalSurfaceData(sampler, edits, circumference, circumference / Math.Tau, width, 0.001f, 128, 8, Colors.DarkGreen, Colors.Gray);
        int legacyBaseline = sampler.GetTerrainSample(0, 0).SurfaceHeight;
        if (ResolveFarTerrainHeight(sampler, edits, 0, 0) != legacyBaseline)
            return "FAIL: unedited legacy far surface changed its integer baseline.";
        var integerEdits = new RingLodSurfaceEditSnapshot(1UL, circumference,
            new Dictionary<RingTerrainColumnCoordinate, int> { [new(0, 0)] = legacyBaseline + 64 });
        var floatEdits = new RingLodSurfaceEditSnapshot(2UL, circumference,
            new Dictionary<RingTerrainColumnCoordinate, int>(),
            new Dictionary<RingTerrainColumnCoordinate, float> { [new(0, 0)] = legacyBaseline + 64.25f }, true);
        if (ResolveFarTerrainHeight(sampler, integerEdits, 0, 0) != legacyBaseline + 64
            || ResolveFarTerrainHeight(sampler, floatEdits, 0, 0) != legacyBaseline + 64.25f)
            return "FAIL: far terrain height lost integer or float edit semantics.";
        var editedGlobal = BuildGlobalSurfaceData(sampler, integerEdits, circumference, circumference / Math.Tau, width, 0.001f, 128, 8, Colors.DarkGreen, Colors.Gray);
        float expectedRadius = (float)((circumference / Math.Tau - (legacyBaseline + 64 - 150.0)) * 0.001);
        if (!Mathf.IsEqualApprox(editedGlobal.Terrain.Vertices[0].X, expectedRadius))
            return "FAIL: global mesh geometry ignored the legacy integer edit.";
        for (int z = 0; z <= 8; z++)
        {
            int first = z * 129, last = first + 128;
            if (global.Terrain.Vertices[first] != global.Terrain.Vertices[last]
                || global.Terrain.Normals[first] != global.Terrain.Normals[last])
                return "FAIL: global periodic seam is not closed.";
        }
        if (global.Terrain.Normals[0].Dot(-global.Terrain.Vertices[0].Normalized()) <= 0)
            return "FAIL: global indexed terrain normals do not face the inhabited interior.";
        return "PASS";
    }
}
