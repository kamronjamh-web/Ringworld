using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Vegetation.Canopy;

/// <summary>
/// Forest lighting debug switches and the canopy raster publisher (docs/forest-lighting-plan.md sections 5 and 8). Debug switches, not menu settings. Defaults (user, 2026-11-19):
/// leaf translucency and crown occlusion on, canopy shade off (it waits for the grass duplicated-terms gate and closed-stand stills); nothing is built, uploaded or published
/// while canopy shade is off. Node name "ForestLighting", child of the game root; perf_bench node ops reach the three switches by that path.
/// RING_FOREST_LIGHTING=1 at start turns all three on, RING_FOREST_LIGHTING=0 all three off (the pre-forest-lighting picture, for A/B and identity checks).
/// Globals: ringworld_forest_lighting = (translucency, tree sun-shadow distance m, canopy shade, crown occlusion); ringworld_canopy_frame = the 3x4 world -> anchor tangent
/// frame of the published raster (same math as ringworld_height_shadow_frame; recomputed every frame), with the REFERENCE ALTITUDE in element [3][3] (the free w of the
/// translation column; the other w components stay 0): the raster's G and B channels hold b * (altitude - reference), so a receiver adds frame[3][3] back;
/// ringworld_canopy_params = (floor radius m, texel m, half size m, 1 while a raster is published else 0); ringworld_canopy_cover = the RGBAH raster with mips.
/// Texture coordinates: u = (aroundLocal + half) / (2 * half), v = (acrossLocal + half) / (2 * half), with around-local metres = R * atan(f.x, R - f.z), across-local = f.y.
/// </summary>
public partial class RingForestLighting : Node
{
    public const string GlobalCover = "ringworld_canopy_cover", GlobalFrame = "ringworld_canopy_frame", GlobalParams = "ringworld_canopy_params", GlobalSwitches = "ringworld_forest_lighting";
    private const double CopyRadiusMeters = 600.0;
    private const double RebuildDistanceMeters = 64.0;
    private const double MinimumRebuildSeconds = 0.5;

    [Export] public NodePath LayerPath { get; set; } = new("../BiomeDecorations");
    [Export] public bool TranslucencyEnabled { get; set; } = true;
    [Export] public bool CanopyShadeEnabled { get; set; }
    [Export] public bool CrownOcclusionEnabled { get; set; } = true;

    /// <summary>Main-thread milliseconds of the last resident tree copy (including the hull lookup).</summary>
    public double LastCopyMilliseconds { get; private set; }
    /// <summary>Worker milliseconds of the last raster build.</summary>
    public double LastBuildMilliseconds { get; private set; }
    /// <summary>Main-thread milliseconds of the last texture upload.</summary>
    public double LastUploadMilliseconds { get; private set; }
    public int LastTreeCount { get; private set; }
    public int RebuildCount { get; private set; }
    public bool RasterPublished => _published != null;
    public long PublishedAnchorAround => _published?.AnchorAround ?? 0;
    public long PublishedAnchorAcross => _published?.AnchorAcross ?? 0;
    public float PublishedReferenceAltitude => _published?.ReferenceAltitude ?? 0f;

    /// <summary>Debug probe for stills and pose searches (not used by the game): the most blocked texel of mip <paramref name="level"/> within <paramref name="radiusMeters"/> of the
    /// published anchor, as (around offset m, across offset m, blocked fraction b) relative to PublishedAnchorAround / PublishedAnchorAcross; z = -1 while nothing is published.</summary>
    public Vector3 DensestCanopyOffset(int level, float radiusMeters)
    {
        CanopyRasterResult? raster = _published;
        if (raster == null) return new Vector3(0, 0, -1);
        level = Math.Clamp(level, 0, ForestCanopyRaster.Levels - 1);
        int size = ForestCanopyRaster.Size >> level;
        float texel = ForestCanopyRaster.TexelMeters * (1 << level);
        var best = new Vector3(0, 0, -1);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float around = (x + 0.5f) * texel - ForestCanopyRaster.HalfSizeMeters, across = (y + 0.5f) * texel - ForestCanopyRaster.HalfSizeMeters;
                if (around * around + across * across > radiusMeters * radiusMeters) continue;
                float blocked = raster.Texel(level, x, y, 0);
                if (blocked > best.Z) best = new Vector3(around, across, blocked);
            }
        return best;
    }

    /// <summary>Debug probe: the blocked fraction b of the mip <paramref name="level"/> texel at an anchor-relative offset (metres); -1 while nothing is published or outside the raster.</summary>
    public float CanopyBlockedAt(int level, float aroundOffset, float acrossOffset)
    {
        CanopyRasterResult? raster = _published;
        if (raster == null) return -1f;
        level = Math.Clamp(level, 0, ForestCanopyRaster.Levels - 1);
        int size = ForestCanopyRaster.Size >> level;
        float texel = ForestCanopyRaster.TexelMeters * (1 << level);
        int x = (int)MathF.Floor((aroundOffset + ForestCanopyRaster.HalfSizeMeters) / texel), y = (int)MathF.Floor((acrossOffset + ForestCanopyRaster.HalfSizeMeters) / texel);
        return x < 0 || y < 0 || x >= size || y >= size ? -1f : raster.Texel(level, x, y, 0);
    }

    private RingBiomeDecorationLayer? _layer;
    private readonly ForestCanopyRaster _raster = new();
    private readonly List<CanopyTreeRecord> _records = new();
    private CanopyTree[] _trees = Array.Empty<CanopyTree>();
    private Task<CanopyRasterResult>? _job;
    private CanopyRasterResult? _published;
    private ImageTexture? _texture;
    private bool _textureGlobalSet;
    private long _lastSignature;
    private bool _haveSignature;
    private double _lastStartSeconds = double.NegativeInfinity;
    private Vector4 _switchesPublished = new(float.NaN, 0, 0, 0), _paramsPublished = new(float.NaN, 0, 0, 0);

    public override void _Ready()
    {
        if (OS.GetEnvironment("RING_FOREST_LIGHTING") == "1") TranslucencyEnabled = CanopyShadeEnabled = CrownOcclusionEnabled = true;
        else if (OS.GetEnvironment("RING_FOREST_LIGHTING") == "0") TranslucencyEnabled = CanopyShadeEnabled = CrownOcclusionEnabled = false;
    }

    public override void _ExitTree()
    {
        _published = null;
        PublishSwitches(true);
        PublishParams(new Vector4(0, 0, 0, 0));
    }

    public override void _Process(double delta)
    {
        bool any = TranslucencyEnabled || CanopyShadeEnabled || CrownOcclusionEnabled;
        if (!any && _published == null && _job == null && _switchesPublished.X == 0f && _switchesPublished.Y == 0f && _switchesPublished.Z == 0f && _switchesPublished.W == 0f) return;
        if (_layer == null || !IsInstanceValid(_layer)) _layer = GetNodeOrNull<RingBiomeDecorationLayer>(LayerPath);
        PublishSwitches(!any);
        if (!CanopyShadeEnabled)
        {
            // Off at run time: drop the raster (a build still in flight is discarded when it lands) and zero the params.
            if (_published != null) { _published = null; _haveSignature = false; }
            if (_job != null && _job.IsCompleted) _job = null;
            if (_paramsPublished.W != 0f) PublishParams(new Vector4(0, 0, 0, 0));
            return;
        }
        if (_layer == null || !_layer.TryGetCanopyContext(out RingLodGeometrySnapshot geometry, out RingCanonicalPosition camera)) { PublishParams(new Vector4(0, 0, 0, 0)); return; }
        double now = Time.GetTicksMsec() / 1000.0;
        long circumference = (long)Math.Round(geometry.CircumferenceMeters);

        if (_job != null && _job.IsCompleted)
        {
            Task<CanopyRasterResult> job = _job; _job = null;
            if (job.IsCompletedSuccessfully) Upload(job.Result);
            else GD.PushWarning("ForestLighting: canopy build failed: " + job.Exception?.GetBaseException().Message);
        }

        if (_job == null && now - _lastStartSeconds >= MinimumRebuildSeconds)
        {
            long signature = _layer.CanopyTileSignature();
            bool changed = !_haveSignature || signature != _lastSignature;
            bool moved = _published == null;
            if (_published != null)
            {
                double da = camera.AroundMeters - _published.AnchorAround;
                double half = circumference * 0.5;
                if (da > half) da -= circumference; else if (da < -half) da += circumference;
                double dz = camera.AcrossMeters - _published.AnchorAcross;
                moved = da * da + dz * dz >= RebuildDistanceMeters * RebuildDistanceMeters;
            }
            if (changed || moved) StartBuild(geometry, camera, circumference, signature, now);
        }

        if (_published != null)
        {
            double[] m = RingTerrainHeightShadow.BuildFrameFor(geometry, _published.AnchorAround, _published.AnchorAcross, circumference);
            var frame = new Projection(new Vector4((float)m[0], (float)m[4], (float)m[8], 0), new Vector4((float)m[1], (float)m[5], (float)m[9], 0),
                new Vector4((float)m[2], (float)m[6], (float)m[10], 0), new Vector4((float)m[3], (float)m[7], (float)m[11], _published.ReferenceAltitude));
            RenderingServer.GlobalShaderParameterSet(GlobalFrame, Variant.From(frame));
            PublishParams(new Vector4((float)(geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters), ForestCanopyRaster.TexelMeters, ForestCanopyRaster.HalfSizeMeters, 1f));
        }
        else PublishParams(new Vector4(0, 0, 0, 0));
    }

    private void StartBuild(in RingLodGeometrySnapshot geometry, RingCanonicalPosition camera, long circumference, long signature, double now)
    {
        long start = Stopwatch.GetTimestamp();
        TreeCrownHull[] hulls = TreeCrownHullTable.Get();
        _records.Clear();
        _layer!.CopyCanopyTrees(camera, CopyRadiusMeters, _records);
        int count = _records.Count;
        if (_trees.Length < count) _trees = new CanopyTree[Math.Max(count, _trees.Length * 2)];
        double altitudeSum = 0;
        for (int i = 0; i < count; i++)
        {
            CanopyTreeRecord t = _records[i];
            TreeCrownHull hull = hulls[t.Variant];
            // The raster uses the hull's own base and top (normalized * instance height), the same ellipsoid FL-2 feeds the tree shaders.
            _trees[i] = new CanopyTree(t.Id, t.Around, t.Across, MathF.Max(0.05f, t.Width * 0.5f), t.RootAltitude + hull.Base * t.Height, t.RootAltitude + hull.Top * t.Height, hull.Porosity, hull.SpeciesFactor);
            altitudeSum += t.RootAltitude;
        }
        float reference = count > 0 ? MathF.Round((float)(altitudeSum / count)) : 0f;
        long anchorAround = ForestCanopyRaster.SnapAnchorAround(camera.AroundMeters, circumference), anchorAcross = ForestCanopyRaster.SnapAnchor(camera.AcrossMeters);
        CanopyTree[] trees = _trees;
        double ringCircumference = geometry.CircumferenceMeters;
        LastCopyMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        LastTreeCount = count;
        _lastSignature = signature; _haveSignature = true; _lastStartSeconds = now;
        _job = Task.Run(() => _raster.Build(new ReadOnlySpan<CanopyTree>(trees, 0, count), anchorAround, anchorAcross, ringCircumference, reference));
    }

    private void Upload(CanopyRasterResult result)
    {
        long start = Stopwatch.GetTimestamp();
        var image = Image.CreateFromData(ForestCanopyRaster.Size, ForestCanopyRaster.Size, true, Image.Format.Rgbah, result.Data);
        if (_texture == null) _texture = ImageTexture.CreateFromImage(image);
        else _texture.Update(image);
        if (!_textureGlobalSet) { RenderingServer.GlobalShaderParameterSet(GlobalCover, _texture); _textureGlobalSet = true; }
        _published = result;
        LastBuildMilliseconds = result.BuildMilliseconds;
        LastUploadMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        RebuildCount++;
    }

    private void PublishSwitches(bool forceZero)
    {
        float distance = forceZero || _layer == null || !IsInstanceValid(_layer) ? 0f : _layer.CanopyTreeSunShadowDistanceMeters;
        var value = forceZero ? new Vector4(0, 0, 0, 0)
            : new Vector4(TranslucencyEnabled ? 1f : 0f, TranslucencyEnabled || CrownOcclusionEnabled ? distance : 0f, CanopyShadeEnabled ? 1f : 0f, CrownOcclusionEnabled ? 1f : 0f);
        if (value == _switchesPublished) return;
        _switchesPublished = value;
        RenderingServer.GlobalShaderParameterSet(GlobalSwitches, value);
    }

    private void PublishParams(Vector4 value)
    {
        if (value == _paramsPublished) return;
        _paramsPublished = value;
        RenderingServer.GlobalShaderParameterSet(GlobalParams, value);
    }
}
