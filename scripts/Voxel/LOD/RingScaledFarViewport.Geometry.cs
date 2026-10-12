using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    internal static bool CanPublishGeometry(ulong builtEdit, long builtLayout, ulong currentEdit, long currentLayout)
        => builtEdit == currentEdit && builtLayout == currentLayout;
    internal static float ResolveCoverageSafeGlobalCutout(float requestedCutout, bool regionalEnabled, float localCoverage)
        => regionalEnabled ? Math.Min(requestedCutout, localCoverage) : requestedCutout;
    // Legacy far surfaces historically use integer voxel heights. Honor integer
    // edits in that same space; exact float/runtime worlds retain presentation heights.
    private static float ResolveFarTerrainHeight(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, long around, int across)
        => edits.UsesFloatSurface || sampler.UsesRuntimeSurfaceGeneration
            ? edits.GetPresentationSurfaceHeight(sampler, around, across)
            : edits.GetSurfaceHeight(sampler, around, across);
    private sealed record GlobalSurfaceCandidate(ulong EditVersion, long LayoutVersion, int Around, int Across, IReadOnlyList<FarRegionalBandData> Terrain, IReadOnlyList<FarRegionalBandData> Water);
    private Task<GlobalSurfaceCandidate>? _globalSurfaceBuild;
    private readonly CancellationTokenSource _geometryCancellation = new();
    private GlobalSurfaceCandidate? _globalSurfaceUpload;
    private readonly List<MeshInstance3D> _globalSurfaceActive = new();
    private readonly List<MeshInstance3D> _globalSurfaceStaging = new();
    private int _globalUploadCursor;
    private int _globalDetailAround;
    private int _globalDetailAcross;
    private int _globalPublishedAround;
    private int _globalPublishedAcross;
    private long _globalGeometryVersion;
    private int _configuredGeometryVertexBudget = 750000;
    public int ActiveGeometryAngularSegments => _adaptiveAngularSteps;
    public int ActiveGlobalAroundSegments => _globalPublishedAround;
    public int ActiveGlobalAcrossSegments => _globalPublishedAcross;
    public bool GlobalGeometryReady => _globalSurfaceActive.Count > 0 && _globalPublishedLayoutVersion == _globalGeometryVersion
        && _lastSeededSurfaceEditVersion == _grid?.LodSurfaceEditVersion && _globalSurfaceBuild == null && _globalSurfaceUpload == null;
    private static bool IsGlobalTerrainSector(MeshInstance3D mesh)
        => mesh.GetMeta("far_global_terrain", false).AsBool();
    public int ActiveGlobalTerrainSectorCount => _globalSurfaceActive.Count(IsGlobalTerrainSector);
    public bool GlobalTerrainTilesReady => ActiveGlobalTerrainSectorCount > 0 && _globalSurfaceActive
        .Where(IsGlobalTerrainSector)
        .All(mesh => mesh.MaterialOverride is ShaderMaterial material
            && material.GetShaderParameter("surface_tile_enabled").AsBool()
            && material.GetShaderParameter("surface_tile_blend").AsSingle() >= 1.0f);
    public int ActiveTerrainSectorCount => _globalSurfaceActive.Count + _farRegionalTerrains.Count;
    public int PendingTerrainSectorCount => _globalSurfaceStaging.Count + _farRegionalStagingTerrains.Count;
    private long _geometryFrameUploadStart;
    private int _geometryFrameUploadCount;
    private void BeginGeometryUploadFrame()
    {
        _geometryFrameUploadStart = Stopwatch.GetTimestamp();
        _geometryFrameUploadCount = 0;
    }
    private bool TryReserveGeometryUpload()
    {
        if (_geometryFrameUploadStart == 0) BeginGeometryUploadFrame();
        if (_geometryFrameUploadCount >= Mathf.Clamp(GeometryUploadsPerFrame, 1, 8)) return false;
        if (_geometryFrameUploadCount > 0 && Stopwatch.GetElapsedTime(_geometryFrameUploadStart).TotalMilliseconds >= Math.Max(0.25, GeometryUploadBudgetMilliseconds)) return false;
        _geometryFrameUploadCount++;
        return true;
    }
    private int _globalVertexCount;
    private int _regionalVertexCount;
    public int ActiveGlobalVertexCount => _globalVertexCount;
    public int ActiveRegionalVertexCount => _regionalVertexCount;
    // Visibility here is the submitted set. Godot owns final per-camera frustum
    // decisions and does not expose an exact accepted-sector counter to C#.
    public int VisibleTerrainSectorCount => _globalSurfaceActive.Count(mesh => mesh.Visible) + _farRegionalTerrains.Count(mesh => mesh.Visible);

    // Geometric sagitta projects to at most the requested error for this polar
    // radius/distance ratio. All bands use this shared lattice to prevent cracks.
    internal static int SelectAngularDetail(double pixelsPerRadian, double targetError, int previous, int maximum, double hysteresis)
    {
        maximum = Math.Clamp(maximum, 96, 768);
        int latticeLimit = 96;
        while (latticeLimit * 2 <= maximum) latticeLimit *= 2;
        maximum = latticeLimit;
        double required = Math.PI * Math.Sqrt(Math.Max(0.0, pixelsPerRadian) / (2.0 * Math.Max(0.5, targetError)));
        int desired = 96;
        while (desired < required && desired * 2 <= maximum) desired *= 2;
        previous = Math.Clamp(previous, 96, maximum);
        if (desired < previous && required > previous * 0.5 * (1.0 - Math.Clamp(hysteresis, 0.1, 0.5))) return previous;
        return desired;
    }

    private void UpdateAdaptiveGeometryProjection()
    {
        if (_sourceCamera == null) return;
        Vector2 size = _sourceCamera.GetViewport().GetVisibleRect().Size * Math.Clamp(ResolutionScale, 0.25f, 1.0f);
        double dimension = _sourceCamera.KeepAspect == Camera3D.KeepAspectEnum.Width ? size.X : size.Y;
        _adaptivePixelScale = dimension / (2.0 * Math.Tan(Math.Clamp(_sourceCamera.Fov, 1.0f, 179.0f) * Math.PI / 360.0));
        int bandCount = GetFarRegionalBandSpecs(IntermediateRegionalLodTiersEnabled).Count;
        // Includes sector seam duplication at the worst 256-row radial cap.
        int maximum = Math.Min(GeometryMaxAngularSegments, Math.Max(96, Math.Clamp(GeometryMaxVertices, 100000, 2000000) / (257 * bandCount) - Math.Clamp(GeometrySectorCount, 4, 32)));
        int next = AdaptiveGeometryEnabled ? SelectAngularDetail(_adaptivePixelScale * 4.0, GeometryTargetPixelError, _adaptiveAngularSteps, maximum, GeometryRefinementHysteresis) : 96;
        if (next != _adaptiveAngularSteps)
        {
            _adaptiveAngularSteps = next;
            InvalidateRegionalGeometry();
        }
        SelectGlobalDetail();
    }

    private void SelectGlobalDetail()
    {
        if (_explorer == null) return;
        int maxAround = Mathf.Clamp(AroundSegments, 128, 4096);
        int maxAcross = Mathf.Clamp(AcrossSegments, 4, 128);
        double distance = Math.Max(125000.0, Math.Abs(_explorer.CanonicalPosition.VerticalMeters));
        double errorMeters = Math.Max(0.5, GeometryTargetPixelError) * distance / Math.Max(1.0, _adaptivePixelScale);
        double required = Math.Tau * Math.Sqrt(_radiusMeters / (8.0 * Math.Max(0.01, errorMeters)));
        int around = maxAround;
        if (AdaptiveGeometryEnabled)
            while (around / 2 >= 128 && around / 2 >= required) around /= 2;
        if (_globalDetailAround > around && required > around * (1.0 - Math.Clamp(GeometryRefinementHysteresis, 0.1f, 0.5f))) around = Math.Min(maxAround, _globalDetailAround);
        int across = Math.Max(4, maxAcross * around / maxAround);
        // Width detail responds to projected terrain relief as well as distance.
        // Never let the outer global fallback be coarser across than half its cap.
        across = Math.Max(maxAcross / 2, across);
        int vertexBudget = Math.Clamp(GeometryMaxVertices, 100000, 2000000);
        int sectors = Math.Clamp(GeometrySectorCount, 4, 32);
        while (2L * (around + sectors) * (across + 1) > vertexBudget && (around > 128 || across > 4))
        {
            if (across > 4) across = Math.Max(4, across / 2);
            else around = Math.Max(128, around / 2);
        }
        if (around == _globalDetailAround && across == _globalDetailAcross) return;
        _globalDetailAround = around;
        _globalDetailAcross = across;
        _globalGeometryVersion++;
        RequestGlobalSurfaceBuild();
    }

    public void ConfigureAdaptiveGeometry(bool enabled, float pixelError, int maxAngular, int sectors, float hysteresis)
    {
        pixelError = Mathf.Clamp(pixelError, 0.5f, 16.0f);
        maxAngular = Mathf.Clamp(maxAngular, 96, 768);
        sectors = Mathf.Clamp(sectors, 4, 32);
        hysteresis = Mathf.Clamp(hysteresis, 0.1f, 0.5f);
        if (AdaptiveGeometryEnabled == enabled && GeometryTargetPixelError == pixelError && GeometryMaxAngularSegments == maxAngular
            && GeometrySectorCount == sectors && GeometryRefinementHysteresis == hysteresis && _configuredGeometryVertexBudget == GeometryMaxVertices) return;
        _configuredGeometryVertexBudget = GeometryMaxVertices;
        AdaptiveGeometryEnabled = enabled;
        GeometryTargetPixelError = Mathf.Clamp(pixelError, 0.5f, 16.0f);
        GeometryMaxAngularSegments = Mathf.Clamp(maxAngular, 96, 768);
        GeometrySectorCount = Mathf.Clamp(sectors, 4, 32);
        GeometryRefinementHysteresis = Mathf.Clamp(hysteresis, 0.1f, 0.5f);
        InvalidateRegionalGeometry();
        _globalGeometryVersion++;
        UpdateAdaptiveGeometryProjection();
        RequestGlobalSurfaceBuild();
    }

    private void InvalidateRegionalGeometry()
    {
        _farRegionalLayoutVersion++;
        _farRegionalUpload = null;
        _farRegionalUploadBand = 0;
        DiscardFarRegionalStaging();
        _lastFarRegionalAnchorAround = long.MinValue;
        _lastFarRegionalAnchorAcross = int.MinValue;
        _requestedFarRegionalAnchorAround = long.MinValue;
        _requestedFarRegionalAnchorAcross = int.MinValue;
    }

    private static int RefineTerrainRadialSteps(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, long aroundAnchor, int acrossAnchor, int width, double inner, double outer, int steps, double error)
    {
        // Bounded probes inspect real terrain curvature rather than assuming a
        // smooth analytic cylinder. This estimator is not an error guarantee for
        // unsampled terrain; the sample-spacing target and hard caps still apply.
        double deviation = 0.0;
        for (int ray = 0; ray < 8; ray++)
        {
            double angle = Math.Tau * ray / 8;
            for (int interval = 0; interval < 4; interval++)
            {
                double start = inner + (outer - inner) * interval / 4;
                double end = inner + (outer - inner) * (interval + 1) / 4;
                float Height(double distance)
                {
                    long around = aroundAnchor + (long)Math.Round(Math.Cos(angle) * distance);
                    int across = Math.Clamp(acrossAnchor + (int)Math.Round(Math.Sin(angle) * distance), 0, width);
                    return ResolveFarTerrainHeight(sampler, edits, around, across);
                }
                deviation = Math.Max(deviation, Math.Abs(Height((start + end) * 0.5) - (Height(start) + Height(end)) * 0.5));
            }
        }
        int curvatureSteps = (int)Math.Min(256.0, Math.Ceiling(4.0 * Math.Sqrt(deviation / Math.Max(0.01, error))));
        return Math.Clamp(Math.Max(steps, curvatureSteps), 8, 256);
    }

    private static IReadOnlyList<FarRegionalBandData> SplitFarRegionalBand(FarRegionalBandData source, int sectors)
    {
        var result = new List<FarRegionalBandData>();
        int sourceRow = source.AngularSteps + 1;
        int rows = source.Vertices.Length / sourceRow;
        for (int sector = 0; sector < sectors; sector++)
        {
            int first = sector * source.AngularSteps / sectors;
            int last = (sector + 1) * source.AngularSteps / sectors;
            int columns = last - first + 1;
            var vertices = new Vector3[rows * columns];
            var normals = new Vector3[vertices.Length];
            var colors = new Color[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var around = new double[vertices.Length];
            var across = new double[vertices.Length];
            for (int row = 0; row < rows; row++)
            {
                int src = row * sourceRow + first, dst = row * columns;
                Array.Copy(source.Vertices, src, vertices, dst, columns);
                Array.Copy(source.Normals, src, normals, dst, columns);
                Array.Copy(source.Colors, src, colors, dst, columns);
                Array.Copy(source.MacroShadowUvs, src, uv, dst, columns);
                Array.Copy(source.CanonicalAround, src, around, dst, columns);
                Array.Copy(source.CanonicalAcross, src, across, dst, columns);
            }
            var indices = new List<int>();
            for (int index = 0; index < source.Indices.Length; index += 3)
            {
                int a = source.Indices[index], b = source.Indices[index + 1], c = source.Indices[index + 2];
                int min = Math.Min(a % sourceRow, Math.Min(b % sourceRow, c % sourceRow));
                int max = Math.Max(a % sourceRow, Math.Max(b % sourceRow, c % sourceRow));
                if (min < first || max > last) continue;
                indices.Add(a / sourceRow * columns + a % sourceRow - first);
                indices.Add(b / sourceRow * columns + b % sourceRow - first);
                indices.Add(c / sourceRow * columns + c % sourceRow - first);
            }
            if (indices.Count > 0)
                result.Add(new FarRegionalBandData($"{source.Name}Sector{sector}", vertices, normals, colors, uv, indices.ToArray(), source.InnerRadius, source.OuterRadius, columns - 1, around, across));
        }
        return result;
    }

    private static Vector2[] BuildTileUvs(double[] around, double[] across, out double aroundStart, out double acrossStart, out double aroundExtent, out double acrossExtent)
    {
        aroundStart = around.Min();
        acrossStart = across.Min();
        aroundExtent = Math.Max(1.0, around.Max() - aroundStart);
        acrossExtent = Math.Max(1.0, across.Max() - acrossStart);
        var uv = new Vector2[around.Length];
        for (int index = 0; index < uv.Length; index++)
            uv[index] = new Vector2((float)((around[index] - aroundStart) / aroundExtent), (float)((across[index] - acrossStart) / acrossExtent));
        return uv;
    }

    internal static Aabb TightGeometryBounds(Vector3[] vertices)
    {
        if (vertices.Length == 0) return new Aabb();
        Vector3 min = vertices[0], max = vertices[0];
        foreach (Vector3 vertex in vertices) { min = min.Min(vertex); max = max.Max(vertex); }
        // Includes floating rounding at large global coordinates without creating
        // the previous enormous always-visible whole-ring bounds.
        Vector3 magnitude = min.Abs().Max(max.Abs());
        float pad = Math.Max(0.001f, Math.Max(magnitude.X, Math.Max(magnitude.Y, magnitude.Z)) * 0.000001f);
        return new Aabb(min - Vector3.One * pad, max - min + Vector3.One * (pad * 2));
    }

    private void ReleaseSurfaceMaterialsRecursive(Node node)
    {
        if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material) ReleaseFarSurfaceMaterial(material);
        foreach (Node child in node.GetChildren()) ReleaseSurfaceMaterialsRecursive(child);
    }

    private void RequestGlobalSurfaceBuild()
    {
        if (_farViewport == null || _globalSurfaceBuild != null || _globalSurfaceUpload != null || _grid?.TerrainSampler is not RingTerrainSampler sampler) return;
        if (_geometryCancellation.IsCancellationRequested) return;
        if (_globalSurfaceActive.Count == 0)
        {
            // A bounded first set closes the full ring immediately. Subsequent
            // high-detail work never removes this fallback until atomic publish.
            var coarse = BuildGlobalSurfaceData(sampler, _grid.CaptureLodSurfaceEditSnapshot(), _circumferenceMeters, _radiusMeters, Math.Max(1, RingWidthMeters), CoordinateScale, 128, 4, LowTerrainColor, HighTerrainColor);
            foreach (FarRegionalBandData data in SplitFarRegionalBand(coarse.Terrain, 16)) _globalSurfaceActive.Add(CreateGlobalSector(data, true));
            foreach (FarRegionalBandData data in SplitFarRegionalBand(coarse.Water, 16)) _globalSurfaceActive.Add(CreateGlobalSector(data, false));
            foreach (MeshInstance3D mesh in _globalSurfaceActive) mesh.Visible = true;
            _globalPublishedAround = 128;
            _globalPublishedAcross = 4;
            _globalVertexCount = coarse.Terrain.Vertices.Length + coarse.Water.Vertices.Length;
        }
        int around = _globalDetailAround > 0 ? _globalDetailAround : Mathf.Clamp(AroundSegments, 128, 4096);
        int across = _globalDetailAcross > 0 ? _globalDetailAcross : Mathf.Clamp(AcrossSegments, 4, 128);
        ulong revision = _grid.LodSurfaceEditVersion;
        if (_globalSurfaceActive.Count > 0 && revision == _lastSeededSurfaceEditVersion && around == _globalRequestedAround && across == _globalRequestedAcross && _globalPublishedLayoutVersion == _globalGeometryVersion) return;
        RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
        RingTerrainSampler worker = sampler.CreateIndependentCopy();
        double circumference = _circumferenceMeters, radius = _radiusMeters;
        int width = Math.Max(1, RingWidthMeters), sectors = Mathf.Clamp(GeometrySectorCount, 4, 32);
        int maximumAcross = Mathf.Clamp(AcrossSegments, 4, 128);
        int maximumAround = Mathf.Clamp(AroundSegments, 128, 4096);
        int vertexBudget = Math.Clamp(GeometryMaxVertices, 100000, 2000000);
        float scale = CoordinateScale;
        Color low = LowTerrainColor, high = HighTerrainColor;
        long layoutVersion = _globalGeometryVersion;
        double pixelScale = _adaptivePixelScale;
        double targetError = GeometryTargetPixelError;
        bool adaptive = AdaptiveGeometryEnabled;
        CancellationToken cancellation = _geometryCancellation.Token;
        double cameraDistance = Math.Max(125000.0, Math.Abs(_explorer?.CanonicalPosition.VerticalMeters ?? 0.0));
        _globalSurfaceBuild = Task.Run(() =>
        {
            try
            {
                // Real height variation can demand more detail than the cylinder
                // sagitta estimate. Sample bounded canonical midpoint strips;
                // shared resolutions preserve exact sector edges at every level.
                int finalAcross = across;
                int finalAround = around;
                if (adaptive && across < maximumAcross)
                {
                    double permittedHeightError = Math.Max(0.5, targetError) * cameraDistance / Math.Max(1.0, pixelScale);
                    double relief = 0.0;
                    for (int probe = 0; probe < 8; probe++)
                    {
                        long sampleAround = (long)Math.Round(circumference * probe / 8);
                        for (int strip = 0; strip < 4; strip++)
                        {
                            int z0 = width * strip / 4, z1 = width * (strip + 1) / 4;
                            double middle = ResolveFarTerrainHeight(worker, edits, sampleAround, (z0 + z1) / 2);
                            double ends = (ResolveFarTerrainHeight(worker, edits, sampleAround, z0) + ResolveFarTerrainHeight(worker, edits, sampleAround, z1)) * 0.5;
                            relief = Math.Max(relief, Math.Abs(middle - ends));
                        }
                    }
                    if (relief > permittedHeightError) finalAcross = Math.Min(maximumAcross, across * 2);
                }
                if (adaptive && around < maximumAround)
                {
                    double permitted = Math.Max(0.5, targetError) * cameraDistance / Math.Max(1.0, pixelScale);
                    for (int probe = 0; probe < 8; probe++)
                    {
                        double start = circumference * probe / 8;
                        double end = start + circumference / around;
                        int acrossProbe = width * (probe % 3) / 2;
                        float Height(double value) => ResolveFarTerrainHeight(worker, edits, (long)Math.Round(value), acrossProbe);
                        if (Math.Abs(Height((start + end) * 0.5) - (Height(start) + Height(end)) * 0.5) > permitted)
                        {
                            finalAround = Math.Min(maximumAround, around * 2);
                            break;
                        }
                    }
                }
                while (2L * (finalAround + sectors) * (finalAcross + 1) > vertexBudget && (finalAround > 128 || finalAcross > 4))
                {
                    if (finalAcross > 4) finalAcross = Math.Max(4, finalAcross / 2);
                    else finalAround = Math.Max(128, finalAround / 2);
                }
                cancellation.ThrowIfCancellationRequested();
                var data = BuildGlobalSurfaceData(worker, edits, circumference, radius, width, scale, finalAround, finalAcross, low, high, cancellation);
                return new GlobalSurfaceCandidate(edits.Version, layoutVersion, finalAround, finalAcross,
                    SplitFarRegionalBand(data.Terrain, sectors), SplitFarRegionalBand(data.Water, sectors));
            }
            finally { worker.Dispose(); }
        });
    }

    private long _globalPublishedLayoutVersion = -1;
    private int _globalRequestedAcross;
    private int _globalRequestedAround;

    private static (FarRegionalBandData Terrain, FarRegionalBandData Water) BuildGlobalSurfaceData(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, double circumference, double radius, int width, float scale, int aroundCount, int acrossCount, Color low, Color high, CancellationToken cancellation = default)
    {
        int row = aroundCount + 1, count = row * (acrossCount + 1);
        var terrain = new Vector3[count];
        var water = new Vector3[count];
        var colors = new Color[count];
        var waterColors = new Color[count];
        var normals = new Vector3[count];
        var waterNormals = new Vector3[count];
        var uv = new Vector2[count];
        var aroundValues = new double[count];
        var acrossValues = new double[count];
        var displayedWater = new bool[count];
        bool presentation = edits.UsesFloatSurface || sampler.UsesRuntimeSurfaceGeneration;
        for (int z = 0; z <= acrossCount; z++)
        for (int x = 0; x <= aroundCount; x++)
        {
            if ((x & 63) == 0) cancellation.ThrowIfCancellationRequested();
            double around = circumference * x / aroundCount, across = width * (double)z / acrossCount;
            long sampleAround = x == aroundCount ? 0 : (long)Math.Round(around);
            int sampleAcross = (int)Math.Round(across);
            TerrainSample sample = presentation ? edits.GetPresentationTerrainSample(sampler, sampleAround, sampleAcross) : sampler.GetTerrainSample(sampleAround, sampleAcross);
            float height = ResolveFarTerrainHeight(sampler, edits, sampleAround, sampleAcross);
            float waterHeight = sample.ResolveDisplayedHeight(height);
            int index = z * row + x;
            double angle = x == aroundCount ? 0.0 : Math.Tau * x / aroundCount;
            Vector3 Point(double h) => new((float)(Math.Cos(angle) * (radius - h) * scale), (float)((across - width * 0.5) * scale), (float)(Math.Sin(angle) * (radius - h) * scale));
            terrain[index] = Point(height - 150.0);
            water[index] = Point(waterHeight - 150.0);
            colors[index] = RingTerrainVisuals.ResolveMacroTextureColor(sample, Mathf.RoundToInt(height), sampler, low, high);
            waterColors[index] = RingWaterModel.EncodeWaterKind(RingTerrainVisuals.ResolveMacroTextureColor(sample, Mathf.RoundToInt(waterHeight), sampler, low, high), sample.WaterKind);
            displayedWater[index] = sample.HasSurfaceWater && height < sample.WaterSurfaceHeight;
            uv[index] = new Vector2(x / (float)aroundCount, z / (float)acrossCount);
            aroundValues[index] = around;
            acrossValues[index] = across;
        }
        var indices = new int[aroundCount * acrossCount * 6];
        var waterIndices = new List<int>();
        int cursor = 0;
        for (int z = 0; z < acrossCount; z++)
        for (int x = 0; x < aroundCount; x++)
        {
            int a = z * row + x, b = a + 1, c = a + row, d = c + 1;
            AddRawTriangle(indices, ref cursor, terrain, normals, c, b, a);
            AddRawTriangle(indices, ref cursor, terrain, normals, b, c, d);
            void WaterTriangle(int i0, int i1, int i2)
            {
                if (!IsWaterTriangle(displayedWater, i0, i1, i2)) return;
                waterIndices.Add(i0); waterIndices.Add(i1); waterIndices.Add(i2);
                Vector3 normal = (water[i1] - water[i0]).Cross(water[i2] - water[i0]);
                waterNormals[i0] += normal; waterNormals[i1] += normal; waterNormals[i2] += normal;
            }
            WaterTriangle(c, b, a); WaterTriangle(b, c, d);
        }
        // Average periodic seam normals before sectors copy shared data.
        for (int z = 0; z <= acrossCount; z++)
        {
            int start = z * row, end = start + aroundCount;
            normals[start] = normals[end] = normals[start] + normals[end];
            waterNormals[start] = waterNormals[end] = waterNormals[start] + waterNormals[end];
        }
        for (int index = 0; index < count; index++)
        {
            // SurfaceTool generated inward normals for the former clockwise
            // global surface. Indexed ArrayMesh data must retain that convention.
            normals[index] = normals[index].LengthSquared() > 0.0000001f ? -normals[index].Normalized() : Vector3.Up;
            waterNormals[index] = waterNormals[index].LengthSquared() > 0.0000001f ? -waterNormals[index].Normalized() : normals[index];
        }
        return (new FarRegionalBandData("ScaledSeededRingSurface", terrain, normals, colors, uv, indices, 0, circumference, aroundCount, aroundValues, acrossValues),
            new FarRegionalBandData("ScaledWaterSurface", water, waterNormals, waterColors, uv, waterIndices.ToArray(), 0, circumference, aroundCount, aroundValues, acrossValues));
    }

    private void UpdateGlobalSurfaceBuild()
    {
        if (_grid == null || _farViewport == null) return;
        if (_globalSurfaceBuild?.IsCompleted == true)
        {
            var task = _globalSurfaceBuild;
            _globalSurfaceBuild = null;
            if (task.IsFaulted) GD.PushError($"Far global surface build failed: {task.Exception?.GetBaseException().Message}");
            else if (!task.IsCanceled && CanPublishGeometry(task.Result.EditVersion, task.Result.LayoutVersion, _grid.LodSurfaceEditVersion, _globalGeometryVersion))
            {
                _globalSurfaceUpload = task.Result;
                _globalUploadCursor = 0;
            }
        }
        if (_globalSurfaceUpload != null && !CanPublishGeometry(_globalSurfaceUpload.EditVersion, _globalSurfaceUpload.LayoutVersion, _grid.LodSurfaceEditVersion, _globalGeometryVersion))
        {
            DiscardGlobalStaging();
            _globalSurfaceUpload = null;
        }
        if (_globalSurfaceUpload == null) { RequestGlobalSurfaceBuild(); return; }
        GlobalSurfaceCandidate candidate = _globalSurfaceUpload;
        int count = candidate.Terrain.Count + candidate.Water.Count;
        while (_globalUploadCursor < count)
        {
            if (!TryReserveGeometryUpload()) return;
            bool terrain = _globalUploadCursor < candidate.Terrain.Count;
            FarRegionalBandData data = terrain ? candidate.Terrain[_globalUploadCursor] : candidate.Water[_globalUploadCursor - candidate.Terrain.Count];
            _globalSurfaceStaging.Add(CreateGlobalSector(data, terrain));
            _globalUploadCursor++;
        }
        foreach (MeshInstance3D old in _globalSurfaceActive)
        {
            old.Visible = false;
            if (old.MaterialOverride is ShaderMaterial material) ReleaseFarSurfaceMaterial(material);
            old.QueueFree();
        }
        _globalSurfaceActive.Clear();
        _globalSurfaceActive.AddRange(_globalSurfaceStaging);
        _globalSurfaceStaging.Clear();
        foreach (MeshInstance3D mesh in _globalSurfaceActive) mesh.Visible = true;
        _lastSeededSurfaceEditVersion = candidate.EditVersion;
        _globalPublishedAround = candidate.Around;
        _globalPublishedAcross = candidate.Across;
        _globalVertexCount = candidate.Terrain.Sum(data => data.Vertices.Length) + candidate.Water.Sum(data => data.Vertices.Length);
        _globalPublishedLayoutVersion = candidate.LayoutVersion;
        _globalRequestedAcross = _globalDetailAcross;
        _globalRequestedAround = _globalDetailAround;
        _globalSurfaceUpload = null;
    }

    private MeshInstance3D CreateGlobalSector(FarRegionalBandData data, bool terrain)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = data.Normals;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.TexUV] = data.MacroShadowUvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = BuildTileUvs(data.CanonicalAround, data.CanonicalAcross, out double around, out double across, out double aroundSpan, out double acrossSpan);
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var instance = new MeshInstance3D
        {
            Name = data.Name,
            Mesh = mesh,
            MaterialOverride = terrain ? BindFarSurfaceMaterial(_terrainMaterial!, around, across, aroundSpan, acrossSpan) : _waterMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true,
            CustomAabb = TightGeometryBounds(data.Vertices),
            Visible = false
        };
        // Invisible candidates coexist with the old set, so Godot can rename
        // duplicate child names. Semantic identity must survive that renaming.
        instance.SetMeta("far_global_terrain", terrain);
        _farViewport!.AddChild(instance);
        return instance;
    }

    private void DiscardGlobalStaging()
    {
        foreach (MeshInstance3D mesh in _globalSurfaceStaging)
        {
            if (mesh.MaterialOverride is ShaderMaterial material) ReleaseFarSurfaceMaterial(material);
            mesh.QueueFree();
        }
        _globalSurfaceStaging.Clear();
    }

    private void DisposeGeometryWork()
    {
        _geometryCancellation.Cancel();
        static void Observe(Task? task)
        {
            if (task == null) return;
            _ = task.ContinueWith(faulted => { _ = faulted.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        Observe(_globalSurfaceBuild);
        Observe(_farRegionalBuildTask);
        DiscardGlobalStaging();
        DiscardFarRegionalStaging();
        _globalSurfaceUpload = null;
        _farRegionalUpload = null;
        _geometryCancellation.Dispose();
    }
}
