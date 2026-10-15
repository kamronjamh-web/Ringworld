using System.Collections.Generic;
using System.Diagnostics;
using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Immediate, low-cost whole-ring representation. It carries silhouette,
/// width, and structural depth while regional terrain summaries are loading.
/// </summary>
public partial class RingAnalyticGlobalLod : MeshInstance3D
{
    // Sampling and triangle emission yield between bounded groups. The final
    // native mesh commit remains atomic, preserving the existing mesh layout.
    [Export] public bool UseStagedStartup { get; set; }
    private IEnumerator<bool>? _startupBuild;
    private bool _startupComplete;
    public string StartupError { get; private set; } = string.Empty;
    public bool StartupComplete => StartupError.Length == 0 && (!BuildEnabled || (!UseStagedStartup || (_startupComplete && Mesh != null)));
    public Godot.Collections.Dictionary GetLoadingStatus() => new() { ["ready"] = StartupComplete, ["phase"] = "whole ring fallback", ["error"] = StartupError };
    private void ServiceStartupGeometry()
    {
        if (_startupBuild == null) return;
        try
        {
            long start = Stopwatch.GetTimestamp();
            for (int units = 0; units < 4; units++)
            {
                if (!_startupBuild.MoveNext()) { _startupBuild.Dispose(); _startupBuild = null; _startupComplete = Mesh != null; if (!_startupComplete) FailStartup("The global fallback did not produce a mesh."); break; }
                if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 3) break;
            }
        }
        catch (Exception error) { FailStartup(error.Message); }
    }
    private void FailStartup(string error)
    {
        if (!UseStagedStartup || StartupError.Length > 0) return;
        StartupError = "Global fallback startup failed: " + error;
        _startupBuild?.Dispose(); _startupBuild = null;
        SetProcess(false);
    }
    public override void _ExitTree() { _startupBuild?.Dispose(); _startupBuild = null; }
    private IEnumerable<bool> EnumerateStartup()
    {
        foreach (bool unit in EnumerateBuildSurface()) yield return unit;
        foreach (bool unit in EnumerateBuildStructuralShell()) yield return unit;
    }

    [Export] public bool BuildEnabled { get; set; } = true;
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath FocusPath { get; set; } = new("../Explorer");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export(PropertyHint.Range, "128,2048,64")] public int AroundSegments { get; set; } = 512;
    [Export(PropertyHint.Range, "2,16,1")] public int AcrossSegments { get; set; } = 4;
    [Export(PropertyHint.Range, "100,1000000,100")] public int RingWidthMeters { get; set; } = 20000;
    [Export(PropertyHint.Range, "1000,50000,100")] public float StructuralDepthMeters { get; set; } = 22300.0f;
    [Export(PropertyHint.Range, "0,2000,10")] public float SurfaceUnderlayMeters { get; set; } = 150.0f;
    [Export(PropertyHint.Range, "10000,5000000,10000")] public float FadeStartMeters { get; set; } = 500000.0f;
    [Export(PropertyHint.Range, "10000,5000000,10000")] public float FadeEndMeters { get; set; } = 750000.0f;
    [ExportGroup("Far-field depth compression")]
    [Export] public bool EnableFarSpace { get; set; } = true;
    [Export(PropertyHint.Range, "100000,2000000,10000")] public float FarSpaceStartMeters { get; set; } = 850000.0f;
    [Export(PropertyHint.Range, "200000,2000000,10000")] public float FarSpaceRenderEndMeters { get; set; } = 1050000.0f;
    [Export] public Color LowColor { get; set; } = new(0.12f, 0.19f, 0.12f, 1.0f);
    [Export] public Color HighColor { get; set; } = new(0.29f, 0.27f, 0.20f, 1.0f);
    [Export] public Color HullColor { get; set; } = new(0.20f, 0.24f, 0.29f, 1.0f);

    private VoxelChunkGridManager? _grid;
    private Node3D? _focus;
    private DirectionalLight3D? _sunLight;
    private ShaderMaterial? _surfaceMaterial;
    private ShaderMaterial? _hullMaterial;
    private MeshInstance3D? _hull;

    public long EstimatedMeshBytes { get; private set; }

    public override void _Ready()
    {
        if (!UseStagedStartup) { InitializeStartupOwner(); return; }
        try { InitializeStartupOwner(); }
        catch (Exception error) { FailStartup(error.Message); }
    }

    private void InitializeStartupOwner()
    {
        if (!BuildEnabled) return;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _focus = GetNodeOrNull<Node3D>(FocusPath);
        _sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        if (_grid == null || _focus == null)
        {
            GD.PushWarning("RingAnalyticGlobalLod could not resolve the grid or focus.");
            FailStartup("The terrain grid or player focus is unavailable.");
            return;
        }
        CreateMaterials();
        if (UseStagedStartup && (_surfaceMaterial == null || _hullMaterial == null)) { FailStartup("The global terrain or hull material is unavailable."); return; }
        if (UseStagedStartup) _startupBuild = EnumerateStartup().GetEnumerator(); else { BuildSurface(); BuildStructuralShell(); }
        UpdateUniforms();
    }

    public override void _Process(double delta)
    {
        ServiceStartupGeometry();
        _ = delta;
        UpdateUniforms();
    }

    private void CreateMaterials()
    {
        Shader? terrainShader = GD.Load<Shader>("res://materials/ring_terrain_global.gdshader");
        if (terrainShader != null)
        {
            _surfaceMaterial = new ShaderMaterial { Shader = terrainShader };
            _surfaceMaterial.SetShaderParameter("surface_apertures_enabled", true);
            _surfaceMaterial.SetShaderParameter("fade_start_meters", FadeStartMeters);
            _surfaceMaterial.SetShaderParameter("fade_end_meters", Mathf.Max(FadeStartMeters + 1.0f, FadeEndMeters));
            _surfaceMaterial.SetShaderParameter("far_fade_enabled", false);
            ConfigureFarSpace(_surfaceMaterial);
            RingAtmosphereModel.ConfigureMaterial(_surfaceMaterial, _grid!.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
            _surfaceMaterial.SetShaderParameter("ring_width_meters", RingWidthMeters);
            global::RingworldTechDemo.RingTerrainMacroShadowProjector.Register(_surfaceMaterial);
            global::RingworldTechDemo.RingCloudShadowProjector.ConfigureAnalyticField(
                _surfaceMaterial,
                _grid.GetCanonicalRingCircumferenceMeters64(),
                RingWidthMeters,
                _grid.TerrainSampler?.Seed ?? 1337);
            global::RingworldTechDemo.RingCloudShadowProjector.Register(_surfaceMaterial);
            MaterialOverride = _surfaceMaterial;
        }

        Shader? hullShader = GD.Load<Shader>("res://materials/ring_hull_global.gdshader");
        if (hullShader != null)
        {
            _hullMaterial = new ShaderMaterial { Shader = hullShader };
            _hullMaterial.SetShaderParameter("fade_start_meters", FadeStartMeters);
            _hullMaterial.SetShaderParameter("fade_end_meters", Mathf.Max(FadeStartMeters + 1.0f, FadeEndMeters));
            ConfigureFarSpace(_hullMaterial);
            RingAtmosphereModel.ConfigureMaterial(
                _hullMaterial,
                _grid!.GetCanonicalRingCircumferenceMeters64() / Math.Tau,
                opacityMultiplier: 1.35f);
        }
    }

    private void BuildSurface() { foreach (bool _ in EnumerateBuildSurface()) { } }

    private IEnumerable<bool> EnumerateBuildSurface()
    {
        if (_grid == null) yield break;
        if (_grid.TerrainSampler is not RingTerrainSampler sampler)
        {
            GD.PushWarning("RingAnalyticGlobalLod could not resolve the seeded terrain sampler.");
            if (UseStagedStartup) throw new InvalidOperationException("The seeded terrain sampler is unavailable.");
            yield break;
        }
        int aroundCount = Mathf.Max(128, AroundSegments);
        int acrossCount = Mathf.Max(2, AcrossSegments);
        RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
        double aroundStep = _grid.GetCanonicalRingCircumferenceMeters64() / aroundCount;
        double acrossStep = RingWidthMeters / (double)acrossCount;
        int row = aroundCount + 1;
        var vertices = new Vector3[(acrossCount + 1) * row];
        var colors = new Color[vertices.Length];

        for (int z = 0; z <= acrossCount; z++)
        for (int x = 0; x <= aroundCount; x++)
        {
            double around = x * aroundStep;
            double across = z * acrossStep;
            long sampleAround = (long)Math.Round(around);
            int sampleAcross = (int)Math.Round(across);
            bool usesPresentationSurface = edits.UsesFloatSurface || sampler.UsesRuntimeSurfaceGeneration;
            TerrainSample sample = usesPresentationSurface
                ? edits.GetPresentationTerrainSample(sampler, sampleAround, sampleAcross)
                : sampler.GetTerrainSample(sampleAround, sampleAcross);
            int index = z * row + x;
            float terrainHeight = usesPresentationSurface
                ? edits.GetPresentationSurfaceHeight(sampler, sampleAround, sampleAcross)
                : sample.SurfaceHeight;
            float displayedHeight = sample.ResolveDisplayedHeight(terrainHeight);
            vertices[index] = Point(around, displayedHeight - SurfaceUnderlayMeters, across);
            colors[index] = RingTerrainVisuals.ResolveSeededColor(
                sample,
                Mathf.RoundToInt(displayedHeight),
                sampler,
                LowColor,
                HighColor);
            if ((index & 127) == 127) yield return true;
        }

        using var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (int z = 0; z < acrossCount; z++)
        for (int x = 0; x < aroundCount; x++)
        {
            int i00 = z * row + x;
            int i10 = i00 + 1;
            int i01 = i00 + row;
            int i11 = i01 + 1;
            AddTriangle(surface, vertices[i01], vertices[i10], vertices[i00], colors[i01], colors[i10], colors[i00]);
            AddTriangle(surface, vertices[i10], vertices[i01], vertices[i11], colors[i10], colors[i01], colors[i11]);
            if (((z * aroundCount + x) & 127) == 127) yield return true;
        }
        yield return true;
        surface.GenerateNormals();
        surface.Index();
        Mesh = surface.Commit();
        CastShadow = ShadowCastingSetting.Off;
        IgnoreOcclusionCulling = true;
        RenderingServer.InstanceSetIgnoreCulling(GetInstance(), true);
        EstimatedMeshBytes = (long)aroundCount * acrossCount * 6L * 44L;
        GD.Print($"Analytic seeded global ring surface: seed={sampler.Seed}, around={aroundCount}, across={acrossCount}, fade={FadeStartMeters / 1000.0f:0}-{FadeEndMeters / 1000.0f:0}km");
    }

    private void BuildStructuralShell() { foreach (bool _ in EnumerateBuildStructuralShell()) { } }

    private IEnumerable<bool> EnumerateBuildStructuralShell()
    {
        if (_grid == null || _hullMaterial == null) yield break;
        int aroundCount = Mathf.Max(128, AroundSegments / 2);
        double aroundStep = _grid.GetCanonicalRingCircumferenceMeters64() / aroundCount;
        double top = _grid.GetStructuralWallTopGlobalY();
        using var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (int x = 0; x < aroundCount; x++)
        {
            double a0 = x * aroundStep, a1 = (x + 1) * aroundStep;
            double bottom = _grid.GetStructuralBottomGlobalY()
                * (double)VoxelConstants.VoxelSizeMeters;
            AddTriangle(surface, Point(a0, bottom, 0.0), Point(a1, bottom, RingWidthMeters), Point(a1, bottom, 0.0), HullColor);
            AddTriangle(surface, Point(a0, bottom, 0.0), Point(a0, bottom, RingWidthMeters), Point(a1, bottom, RingWidthMeters), HullColor);
            AddWallQuad(surface, a0, a1, 0.0, bottom, top, true);
            AddWallQuad(surface, a0, a1, RingWidthMeters, bottom, top, false);
            if ((x & 31) == 31) yield return true;
        }
        yield return true;
        surface.GenerateNormals();
        surface.Index();
        _hull = new MeshInstance3D
        {
            Name = "AnalyticGlobalStructuralShell",
            Mesh = surface.Commit(),
            MaterialOverride = _hullMaterial,
            CastShadow = ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true
        };
        AddChild(_hull);
        RenderingServer.InstanceSetIgnoreCulling(_hull.GetInstance(), true);
        EstimatedMeshBytes += (long)aroundCount * 18L * 44L;
        GD.Print($"Analytic global structural shell: depth={StructuralDepthMeters / 1000.0f:0.0}km, around={aroundCount}");
    }

    private void AddWallQuad(SurfaceTool surface, double a0, double a1, double across, double bottom, double top, bool reverse)
    {
        Vector3 p0 = Point(a0, bottom, across), p1 = Point(a1, bottom, across);
        Vector3 p2 = Point(a1, top, across), p3 = Point(a0, top, across);
        if (reverse)
        {
            AddTriangle(surface, p0, p1, p3, HullColor);
            AddTriangle(surface, p1, p2, p3, HullColor);
        }
        else
        {
            AddTriangle(surface, p3, p1, p0, HullColor);
            AddTriangle(surface, p1, p3, p2, HullColor);
        }
    }

    private Vector3 Point(double around, double vertical, double across)
    {
        return ToLocal(_grid!.CanonicalRingPositionToWorld(new RingCanonicalPosition(around, vertical, across)));
    }

    private static void AddTriangle(SurfaceTool surface, Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        surface.SetColor(color); surface.AddVertex(a);
        surface.SetColor(color); surface.AddVertex(b);
        surface.SetColor(color); surface.AddVertex(c);
    }

    private static void AddTriangle(
        SurfaceTool surface,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Color colorA,
        Color colorB,
        Color colorC)
    {
        surface.SetColor(colorA); surface.AddVertex(a);
        surface.SetColor(colorB); surface.AddVertex(b);
        surface.SetColor(colorC); surface.AddVertex(c);
    }

    private void UpdateUniforms()
    {
        if (_grid == null || _focus == null) return;
        Vector3 camera = _focus.GlobalPosition;
        Vector3 center = _grid.GetCanonicalRingCenterWorld();
        Vector3 sun = _sunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        Camera3D? activeCamera = GetViewport()?.GetCamera3D();
        RingAtmosphereRayFrame frame = activeCamera == null
            ? RingAtmosphereRayFrame.Invalid
            : RingAtmosphereRayFrame.FromMainCamera(_grid, activeCamera, sun);
        foreach (ShaderMaterial? material in new[] { _surfaceMaterial, _hullMaterial })
        {
            material?.SetShaderParameter("camera_world_position", camera);
            material?.SetShaderParameter("ring_center", center);
            material?.SetShaderParameter("sun_direction", sun);
            frame.Apply(material);
        }
    }

    private void ConfigureFarSpace(ShaderMaterial material)
    {
        if (_grid == null) return;
        double diameter = _grid.GetCanonicalRingCircumferenceMeters64() / Math.PI;
        float realEnd = (float)Math.Max(
            FarSpaceStartMeters + 1.0,
            diameter + RingWidthMeters + StructuralDepthMeters + 10000.0);
        material.SetShaderParameter("far_space_enabled", EnableFarSpace);
        material.SetShaderParameter("far_space_start_meters", FarSpaceStartMeters);
        material.SetShaderParameter("far_space_real_end_meters", realEnd);
        material.SetShaderParameter(
            "far_space_render_end_meters",
            Mathf.Max(FarSpaceStartMeters + 1.0f, FarSpaceRenderEndMeters));
    }
}
