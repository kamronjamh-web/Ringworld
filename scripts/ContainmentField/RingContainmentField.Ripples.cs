using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Rendering;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// Ripple patches (docs/containment-field-design.md 3.5, 3.10.2; WO4). Every live ripple of <see cref="RingContainmentDisturbanceHost.SharedBus"/> (16 slots, any kind) is drawn as
/// its own small patch (materials/ring_containment_ripple.gdshader) while the camera is BELOW the field, from any distance up to the camera far plane. Inside the near patch's range
/// the near patch evaluates up to 4 overlapping ripples itself (<c>cf_near_ripples</c>), and the ripple patches skip that part. With the camera ABOVE the field the patches are hidden
/// and <see cref="PublishRipplesAbove"/> draws them after Sunshine (RingContainmentField.RipplesPost.cs); far-reaching ripples and far strikes also feed the far strike layer (RingContainmentField.FarStrikes.cs).
/// </summary>
public partial class RingContainmentField
{
    public const float DefaultRippleStrength = 1.0f;
    public const string RippleShaderPath = "res://materials/ring_containment_ripple.gdshader";
    private const int RipplePoolSize = ContainmentDisturbanceBus.PoolSize;
    private const int MaxFarStrikes = ContainmentDisturbanceBus.FarListSize;
    private const int MaxRebuildsPerFrame = 2;
    /// <summary>Beyond this distance the source storm is handed to Sunshine's own cloud compositing (48 km), so only farther occluders hide ripple parts.</summary>
    private const double OccluderHandoffMeters = 48_000.0;

    /// <summary>Ripple and field strike strength, 0..2 (menu "Ripple strength"): refraction, reveal light, crawl and glow. 0 hides the patches; the bus still runs.</summary>
    public float RippleStrength { get; set; } = DefaultRippleStrength;

    /// <summary>Debug / test seam: when set, replaces the bus's slot list for rendering.</summary>
    public IReadOnlyList<ContainmentRipple?>? SlotsOverride { get; set; }
    /// <summary>Debug / test seam: when set, replaces the bus's far strike list for rendering.</summary>
    public IReadOnlyList<ContainmentFarStrike>? FarOverride { get; set; }

    public int RipplePatchesDrawn { get; private set; }
    public int RippleRebuildCount { get; private set; }
    public int NearRippleCount { get; private set; }
    public double LastRippleMicroseconds { get; private set; }
    public bool RippleSideAbove { get; private set; }
    public ShaderMaterial? RippleMaterial => _rippleMaterial;

    private sealed class RipplePatchSlot
    {
        public MeshInstance3D Node = null!;
        public ArrayMesh Mesh = null!;
        public ContainmentRipple? Ref;
        public float BuiltRadius;
        public ContainmentNearPatchMesh? Patch;
    }

    private static readonly StringName RippleStrengthName = "cf_ripple_strength";
    private static readonly StringName NearActiveName = "cf_near_active";
    private static readonly StringName FarFadeName = "cf_far_fade";
    private static readonly StringName RippleMaxPxName = "cf_ripple_max_px";
    private static readonly StringName NearRippleCountName = "cf_near_ripple_count";
    private static readonly StringName NearRipplesName = "cf_near_ripples";
    private static readonly StringName RippleWaveName = "cf_ripple_wave";
    private static readonly StringName RippleCrawlName = "cf_ripple_crawl";
    private static readonly StringName RippleFrameName = "cf_ripple_frame";
    private static readonly StringName RippleOccluderAName = "cf_ripple_occluder_a";
    private static readonly StringName RippleOccluderBName = "cf_ripple_occluder_b";

    private readonly RipplePatchSlot?[] _ripplePool = new RipplePatchSlot?[RipplePoolSize];
    private readonly ContainmentRippleRenderState[] _rippleStates = new ContainmentRippleRenderState[RipplePoolSize];
    private readonly bool[] _rippleVisible = new bool[RipplePoolSize], _rippleReady = new bool[RipplePoolSize], _rippleNear = new bool[RipplePoolSize];
    private readonly float[] _flashScratch = new float[RipplePoolSize + MaxFarStrikes];
    private readonly int[] _rippleFlashIndex = new int[RipplePoolSize], _nearSelection = new int[ContainmentRipplePatches.MaxNearRipples];
    private readonly int[] _rebuildQueue = new int[RipplePoolSize];
    private readonly Vector4[] _nearRippleUniform = new Vector4[ContainmentRipplePatches.MaxNearRipples * 3];
    private ShaderMaterial? _rippleMaterial;
    private bool _rippleHazeRegistered;
    private int _nearCountApplied;
    private float _nearActiveApplied = -1.0f;
    private static long _debugRippleCounter;

    private static double UsSince(long start) => System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000.0;

    partial void UpdateRipplesCore()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        RunRipples();
        LastRippleMicroseconds = UsSince(start);
    }

    /// <summary>
    /// The camera is ABOVE the field this frame (the ripple meshes are hidden). <paramref name="states"/> is indexed by slot (<c>default</c> = not visible), valid for
    /// every slot of <paramref name="slots"/>; <paramref name="lighting"/> is the scene lighting at the first visible ripple (or first far strike).
    /// </summary>
    partial void PublishRipplesAbove(Camera3D camera, in RingLodGeometrySnapshot snapshot, in ContainmentFieldProfile profile, IReadOnlyList<ContainmentRipple?> slots,
        ContainmentRippleRenderState[] states, IReadOnlyList<ContainmentFarStrike>? farStrikes, double now, in FieldLighting lighting, float seamFlowPhase, float seamEnergyScale);

    private void RunRipples()
    {
        ContainmentDisturbanceBus? bus = RingContainmentDisturbanceHost.SharedBus;
        IReadOnlyList<ContainmentRipple?>? slots = SlotsOverride ?? bus?.Slots;
        IReadOnlyList<ContainmentFarStrike>? far = FarOverride ?? bus?.FarStrikes;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        ContainmentFieldProfile? profileOrNull = CurrentProfile();
        int slotCount = slots == null ? 0 : Math.Min(slots.Count, RipplePoolSize), farCount = far == null ? 0 : Math.Min(far.Count, MaxFarStrikes);
        int live = 0;
        for (int i = 0; i < slotCount; i++) if (slots![i] != null) live++;
        if (slots == null || !Enabled || !(RippleStrength > 0.0f) || camera == null || _grid == null || profileOrNull is not { IsValid: true } || (live == 0 && farCount == 0)
            || !(_weather != null))
        {
            IdleRipples();
            return;
        }
        ContainmentFieldProfile profile = profileOrNull.Value;
        double now = _weather.CloudSimulationTimeSeconds;

        // Flash levels: every live ripple plus the far strikes, summed-capped together (reduced flashes).
        int flashes = 0;
        for (int i = 0; i < slotCount; i++)
        {
            ContainmentRipple? r = slots[i];
            _rippleFlashIndex[i] = -1;
            if (r == null) continue;
            _rippleFlashIndex[i] = flashes;
            _flashScratch[flashes++] = r.FlashAt(now);
        }
        int farFlashBase = flashes;
        for (int k = 0; k < farCount; k++) _flashScratch[flashes++] = far![k].FlashAt(now);
        ContainmentRipplePatches.CapFlashes(new Span<float>(_flashScratch, 0, flashes));
        bool anyVisible = false;
        int firstVisible = -1;
        for (int i = 0; i < RipplePoolSize; i++)
        {
            ContainmentRipple? r = i < slotCount ? slots[i] : null;
            ContainmentRippleRenderState state = r == null ? default : ContainmentRipplePatches.Evaluate(r, now, _flashScratch[_rippleFlashIndex[i]], RippleStrength, LightningVisualPolicy.ReducedFlashes);
            _rippleStates[i] = state;
            _rippleVisible[i] = state.Visible;
            if (state.Visible) { anyVisible = true; if (firstVisible < 0) firstVisible = i; }
        }
        if (!anyVisible && farCount == 0) { IdleRipples(); return; }

        // Side: the near planner's when it ran this frame, else the exact signed distance.
        RingCanonicalPosition cameraCanonical = _frameCameraValid ? _frameCameraCanonical : _grid.WorldToCanonicalRingPosition64(camera.GlobalPosition);
        bool above = ForcePostPass || (_frameNearSideKnown ? _frameView.Side == ContainmentFieldViewSide.Above : ContainmentFieldGeometry.SignedDistance(profile, cameraCanonical) > 0.0);
        RippleSideAbove = above;

        if (!_frameSnapshotValid) { _frameSnapshot = _grid.CaptureLodGeometrySnapshot(); _frameSnapshotValid = true; }
        RingLodGeometrySnapshot snapshot = _frameSnapshot;

        // Lighting at the first visible ripple (or the first far strike), once per frame; the near patch's when it computed it.
        FieldLighting light = default;
        if (anyVisible || farCount > 0)
        {
            if (_frameLightingFresh) light = LastLighting;
            else
            {
                ContainmentFieldPoint at = firstVisible >= 0 ? slots[firstVisible]!.Surface : far![0].Surface;
                light = UpdateLighting(snapshot, at);
                LastLighting = light;
            }
        }
        double flowCycles = now / SeamFlowPeriodSeconds;
        float flowPhase = (float)(flowCycles - Math.Floor(flowCycles));
        float energyScale = Mathf.Lerp(0.35f, 1.0f, light.Day);

        // Far strike layer: the light of ripples reaching past the far fade band and of far-list strikes, on either side of the field.
        UpdateFarStrikes(camera, snapshot, cameraCanonical, slots, slotCount, far, farCount, farFlashBase, now);

        // Rebuilds: a slot's patch is rebuilt when its ripple changed, its render radius grew by more than 0.1 %, or the profile / circumference / grid basis changed; at most 2 a frame, oldest start first.
        int queued = 0;
        for (int i = 0; i < RipplePoolSize; i++)
        {
            _rippleReady[i] = false;
            RipplePatchSlot? ps = _ripplePool[i];
            ContainmentRipple? r = i < slotCount ? slots[i] : null;
            if (r == null) { if (ps != null) ps.Ref = null; continue; }
            if (!_rippleVisible[i]) continue;
            if (ps == null || NeedsRippleRebuild(ps, r, profile, snapshot)) _rebuildQueue[queued++] = i;
            else _rippleReady[i] = true;
        }
        for (int built = 0; built < MaxRebuildsPerFrame && queued > 0; built++)
        {
            int best = 0;
            for (int q = 1; q < queued; q++) if (slots[_rebuildQueue[q]]!.StartTimeSeconds < slots[_rebuildQueue[best]]!.StartTimeSeconds) best = q;
            int index = _rebuildQueue[best];
            _rebuildQueue[best] = _rebuildQueue[--queued];
            RebuildRipplePatch(index, slots[index]!, profile, snapshot);
            _rippleReady[index] = true;
        }

        // Near patch integration: its (up to 4) nearest overlapping ripples are drawn by the near patch inside its range, in whichever pass draws it (spatial below, post above).
        int nearCount = 0;
        Array.Clear(_rippleNear);
        if (_frameNearDrawn && _frameNearMesh != null)
        {
            nearCount = ContainmentRipplePatches.SelectNear(_frameNearMesh, snapshot, slots, _rippleReady, _nearSelection);
            double circumference = snapshot.CircumferenceMeters;
            for (int k = 0; k < nearCount; k++)
            {
                int i = _nearSelection[k];
                ContainmentRippleRenderState state = _rippleStates[i];
                Vector4 centre = ContainmentRipplePatches.CentreIn(_frameNearMesh, slots[i]!, snapshot, circumference);
                _nearRippleUniform[3 * k] = state.Wave;
                _nearRippleUniform[3 * k + 1] = state.Crawl;
                _nearRippleUniform[3 * k + 2] = new Vector4(centre.X, centre.Y, centre.Z, state.StrikeLevel);
                _rippleNear[i] = true;
            }
        }
        if (above)
        {
            HideRipplePatches();
            SetNearRipples(0);
            NearRippleCount = nearCount;
            PublishNearRipplesPost(nearCount);
            RipplePatchesDrawn = 0;
            PublishRipplesAbove(camera, snapshot, profile, slots, _rippleStates, far, now, light, flowPhase, energyScale);
            return;
        }
        PublishRipplesInactive();
        RipplePostDrawn = 0;
        SetNearRipples(nearCount);

        // Shared material, once a frame.
        ShaderMaterial material = EnsureRippleMaterial();
        (double fadeStart, double fadeEnd) = ContainmentRipplePatches.FarFade(camera.Far);
        material.SetShaderParameter(RippleStrengthName, RippleStrength);
        material.SetShaderParameter(SheenName, SheenStrength);
        material.SetShaderParameter(LatticeName, LatticeStrength);
        material.SetShaderParameter(RangeName, (float)(ShimmerRangeKm * 1000.0));
        material.SetShaderParameter(FarFadeName, new Vector2((float)fadeStart, (float)fadeEnd));
        material.SetShaderParameter(CenterName, _grid.GetCanonicalRingCenterWorld());
        material.SetShaderParameter(DebugName, DebugMode);
        material.SetShaderParameter(ToSunName, light.ToSun);
        material.SetShaderParameter(UpName, light.Up);
        material.SetShaderParameter(SunIrradianceName, light.SunIrradiance);
        material.SetShaderParameter(SkyRadianceName, light.SkyRadiance);
        material.SetShaderParameter(GroundRadianceName, light.GroundRadiance);
        material.SetShaderParameter(SeamFlowPhaseName, flowPhase);
        material.SetShaderParameter(SeamEnergyScaleName, energyScale);
        float nearActive = nearCount > 0 ? 1.0f : 0.0f;
        if (nearActive != _nearActiveApplied) { material.SetShaderParameter(NearActiveName, nearActive); _nearActiveApplied = nearActive; }

        Vector3 cameraWorld = camera.GlobalPosition;
        int drawn = 0;
        for (int i = 0; i < RipplePoolSize; i++)
        {
            RipplePatchSlot? ps = _ripplePool[i];
            if (!_rippleReady[i] || ps == null) { if (ps != null && ps.Node.Visible) ps.Node.Visible = false; continue; }
            ContainmentRipple r = slots[i]!;
            ContainmentRippleRenderState state = _rippleStates[i];
            ContainmentNearPatchMesh patch = ps.Patch!;
            ContainmentFieldPoint anchor = patch.Anchor;
            ps.Node.GlobalTransform = new Transform3D(Basis.Identity, snapshot.CanonicalToWorld(anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters));
            Vector2 lattice = ContainmentFieldNearPatch.LatticeOrigin(patch);
            Vector4 occluderA = Vector4.Zero, occluderB = new(0f, 0f, 0f, _rippleNear[i] ? 1.0f : 0.0f);
            if (r.OccluderCenter is { } centre && r.OccluderRadiusMeters > 0.0f)
            {
                Vector3 centreAtCamera = snapshot.CanonicalToWorld(centre.AroundMeters, centre.VerticalMeters, centre.AcrossMeters);
                if (cameraWorld.DistanceTo(centreAtCamera) > OccluderHandoffMeters)
                {
                    Vector3 top = snapshot.CanonicalToWorld(centre.AroundMeters, r.OccluderTopMeters, centre.AcrossMeters);
                    ContainmentFieldPlacement.AxesAt(snapshot, centre.AroundMeters, out _, out Vector3 up, out _);
                    up = up.Normalized();
                    occluderA = new Vector4(top.X, top.Y, top.Z, r.OccluderRadiusMeters);
                    occluderB = new Vector4(up.X, up.Y, up.Z, occluderB.W);
                }
            }
            ps.Node.SetInstanceShaderParameter(RippleWaveName, state.Wave);
            ps.Node.SetInstanceShaderParameter(RippleCrawlName, state.Crawl);
            ps.Node.SetInstanceShaderParameter(RippleFrameName, new Vector4(lattice.X, lattice.Y, (float)patch.SurfaceMetresPerUvX, state.StrikeLevel));
            ps.Node.SetInstanceShaderParameter(RippleOccluderAName, occluderA);
            ps.Node.SetInstanceShaderParameter(RippleOccluderBName, occluderB);
            if (!ps.Node.Visible) ps.Node.Visible = true;
            drawn++;
        }
        RipplePatchesDrawn = drawn;
        if (drawn > 0)
        {
            if (!_rippleHazeRegistered)
            {
                RingPoiHaze.RegisterMaterial(material, snapshot.RingRadiusMeters);
                _rippleHazeRegistered = true;
            }
            RingPoiHaze.Tick(this, _grid, _sun);
        }
    }

    private void IdleRipples()
    {
        HideRipplePatches();
        SetNearRipples(0);
        RipplePatchesDrawn = 0;
        RipplePostDrawn = 0;
        RippleSideAbove = false;
        PublishRipplesInactive();
        ClearFarStrikes();
    }

    private void HideRipplePatches()
    {
        for (int i = 0; i < RipplePoolSize; i++)
        {
            RipplePatchSlot? ps = _ripplePool[i];
            if (ps != null && ps.Node.Visible) ps.Node.Visible = false;
        }
    }

    /// <summary>Writes the near patch's ripple uniforms; with count 0 only the count is (re)written, once.</summary>
    private void SetNearRipples(int count)
    {
        NearRippleCount = count;
        if (_material == null) return;
        if (count > 0)
        {
            _material.SetShaderParameter(NearRipplesName, _nearRippleUniform);
            _material.SetShaderParameter(NearRippleCountName, count);
            _nearCountApplied = count;
        }
        else if (_nearCountApplied != 0)
        {
            _material.SetShaderParameter(NearRippleCountName, 0);
            _nearCountApplied = 0;
        }
        if (count == 0 && _rippleMaterial != null && _nearActiveApplied != 0.0f) { _rippleMaterial.SetShaderParameter(NearActiveName, 0.0f); _nearActiveApplied = 0.0f; }
    }

    private ShaderMaterial EnsureRippleMaterial()
    {
        if (_rippleMaterial != null) return _rippleMaterial;
        _rippleMaterial = new ShaderMaterial { Shader = GD.Load<Shader>(RippleShaderPath) };
        _rippleMaterial.SetShaderParameter(RippleMaxPxName, ContainmentRippleShaderMaxPx);
        return _rippleMaterial;
    }

    /// <summary>CF_RIPPLE_MAX_PX of ring_containment_field_core.gdshaderinc.</summary>
    private const float ContainmentRippleShaderMaxPx = 6.0f;

    private static bool NeedsRippleRebuild(RipplePatchSlot ps, ContainmentRipple r, in ContainmentFieldProfile profile, in RingLodGeometrySnapshot snapshot)
    {
        ContainmentNearPatchMesh? built = ps.Patch;
        return built == null || ps.Ref != r || ContainmentRipplePatches.RenderPatchRadius(r) > ps.BuiltRadius * 1.001f
            || built.Profile != profile || built.Circumference != snapshot.CircumferenceMeters || built.RingRadius != snapshot.RingRadiusMeters
            || built.GridBasis != snapshot.GridGlobalTransform.Basis;
    }

    private void RebuildRipplePatch(int index, ContainmentRipple r, in ContainmentFieldProfile profile, in RingLodGeometrySnapshot snapshot)
    {
        RipplePatchSlot ps = _ripplePool[index] ??= CreateRipplePatchSlot(index);
        ContainmentNearPatchMesh patch = ContainmentRipplePatches.BuildPatch(profile, snapshot, r);
        UploadPatch(ps.Mesh, patch);
        ps.Node.CustomAabb = patch.Bounds.Grow(1.0f);
        ps.Patch = patch;
        ps.Ref = r;
        ps.BuiltRadius = ContainmentRipplePatches.RenderPatchRadius(r);
        PublishRippleRecords(index, patch);
        RippleRebuildCount++;
    }

    private RipplePatchSlot CreateRipplePatchSlot(int index)
    {
        ShaderMaterial material = EnsureRippleMaterial();
        var mesh = new ArrayMesh();
        var node = new MeshInstance3D
        {
            Name = "RipplePatch" + index,
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            Visible = false,
            Layers = RingRenderLayers.NoPrecipitationOcclusion,
            TopLevel = true,
        };
        node.AddToGroup(RingRenderLayers.NoPrecipitationOcclusionGroup);
        AddChild(node);
        return new RipplePatchSlot { Node = node, Mesh = mesh };
    }

    private static void UploadPatch(ArrayMesh mesh, ContainmentNearPatchMesh patch)
    {
        int count = patch.Positions.Length;
        var tangents = new float[count * 4];
        for (int k = 0; k < count; k++)
        {
            Vector3 t = patch.Tangents[k];
            tangents[k * 4] = t.X; tangents[k * 4 + 1] = t.Y; tangents[k * 4 + 2] = t.Z; tangents[k * 4 + 3] = 1.0f;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = patch.Positions;
        arrays[(int)Mesh.ArrayType.Normal] = patch.Normals;
        arrays[(int)Mesh.ArrayType.Tangent] = tangents;
        arrays[(int)Mesh.ArrayType.TexUV] = patch.Uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = patch.Uv2;
        arrays[(int)Mesh.ArrayType.Index] = patch.Indices;
        mesh.ClearSurfaces();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    // ---- Debug / test API (capture tools and the Diagnostics buttons) ----

    private double DebugNow() => _weather?.CloudSimulationTimeSeconds ?? RingContainmentDisturbanceHost.SharedBus?.Now ?? 0.0;

    /// <summary>Publishes a lit debug ripple to the shared bus at the weather clock now (flash = a decaying pulse over <paramref name="flashSeconds"/>; none when that is 0 or less).</summary>
    public ContainmentPublishResult SpawnDebugRipple(RingCanonicalPosition source, float energy = 1.0f, float radiusMeters = 8000.0f, float durationSeconds = 4.0f,
        float flashSeconds = 0.3f, ContainmentDisturbanceKind kind = ContainmentDisturbanceKind.Scripted)
    {
        ContainmentDisturbanceBus? bus = RingContainmentDisturbanceHost.SharedBus;
        if (bus == null) return ContainmentPublishResult.Invalid;
        double start = DebugNow();
        Func<double, float>? flash = null;
        if (flashSeconds > 0.0f)
        {
            double decay = flashSeconds / 3.0, end = start + flashSeconds;
            flash = t => t >= start && t <= end ? (float)(LightningVisualPolicy.PeakScale * Math.Exp(-(t - start) / decay)) : 0.0f;
        }
        return bus.TryPublish(new ContainmentDisturbance(0x7E57_0000_0000L + _debugRippleCounter++, kind, source, start, energy, radiusMeters, durationSeconds, 1.0f, flash));
    }

    /// <summary>Publishes a debug field strike (Rmax 6 km + 9 km E, T 3 s, a 0.8 s lightning-like pulse with one re-pulse) at <paramref name="contact"/>.</summary>
    public ContainmentPublishResult SpawnDebugFieldStrike(RingCanonicalPosition contact, float energy = 1.0f)
    {
        ContainmentDisturbanceBus? bus = RingContainmentDisturbanceHost.SharedBus;
        if (bus == null) return ContainmentPublishResult.Invalid;
        double start = DebugNow();
        Func<double, float> flash = t =>
        {
            double u = t - start;
            if (u < 0.0 || u > 0.8) return 0.0f;
            double level = Math.Exp(-u / 0.12) + (u >= 0.35 ? 0.6 * Math.Exp(-(u - 0.35) / 0.12) : 0.0);
            return (float)(Math.Min(level, 1.0) * LightningVisualPolicy.PeakScale);
        };
        return bus.TryPublish(new ContainmentDisturbance(0x7E57_0000_0000L + _debugRippleCounter++, ContainmentDisturbanceKind.FieldStrike, contact, start, energy,
            6000.0f + 9000.0f * energy, 3.0f, 1.0f, flash));
    }

    private RingCanonicalPosition? AboveCameraSource()
    {
        Camera3D? camera = GetViewport()?.GetCamera3D();
        ContainmentFieldProfile? profile = CurrentProfile();
        if (camera == null || _grid == null || profile is not { IsValid: true } p) return null;
        RingCanonicalPosition c = _grid.WorldToCanonicalRingPosition64(camera.GlobalPosition);
        return new RingCanonicalPosition(c.AroundMeters, ContainmentFieldGeometry.HeightAt(p, c.AcrossMeters), c.AcrossMeters);
    }

    /// <summary>A debug ripple on the field straight above the camera; null without a camera, grid or profile.</summary>
    public ContainmentPublishResult? SpawnDebugRippleAboveCamera() => AboveCameraSource() is { } source ? SpawnDebugRipple(source) : null;

    /// <summary>A debug field strike on the field straight above the camera; null without a camera, grid or profile.</summary>
    public ContainmentPublishResult? SpawnDebugFieldStrikeAboveCamera() => AboveCameraSource() is { } source ? SpawnDebugFieldStrike(source) : null;

    /// <summary>
    /// Fills <see cref="SlotsOverride"/> with <paramref name="count"/> (at most 16) ripples built directly on a 4 x 4 grid around the field point nearest <paramref name="centre"/>, spaced
    /// <paramref name="spacingMeters"/> apart, alternating field strikes (Rmax 15 km, crawl 6 km) and lightning (Rmax 8 km), mid-life at the weather clock (duration 600 s, started 2 s ago).
    /// </summary>
    public void ForceDebugRipples(int count, RingCanonicalPosition centre, double spacingMeters, bool lit)
    {
        ContainmentFieldProfile? profileOrNull = CurrentProfile();
        if (profileOrNull is not { IsValid: true } profile) return;
        count = Math.Clamp(count, 0, RipplePoolSize);
        ContainmentFieldPoint middle = ContainmentFieldGeometry.Nearest(profile, centre);
        double archLength = ContainmentFieldGeometry.ArchLength(profile), now = DebugNow();
        var list = new ContainmentRipple?[count];
        for (int index = 0; index < count; index++)
        {
            int column = index % 4, row = index / 4;
            double s = Math.Clamp(middle.ArchLengthMeters + (row - 1.5) * spacingMeters, 0.0, archLength);
            ContainmentFieldPoint surface = ContainmentFieldGeometry.SurfaceAtArchLength(profile, middle.AroundMeters + (column - 1.5) * spacingMeters, s);
            bool strike = (index & 1) == 0;
            list[index] = new ContainmentRipple
            {
                Slot = index, SourceId = 0x7E57_0000_1000L + index, Kind = strike ? ContainmentDisturbanceKind.FieldStrike : ContainmentDisturbanceKind.Lightning,
                Surface = surface, Source = new RingCanonicalPosition(surface.AroundMeters, surface.VerticalMeters, surface.AcrossMeters),
                StartTimeSeconds = now - 2.0, Energy = 1.0f, RadiusMeters = strike ? 15_000.0f : 8_000.0f, DurationSeconds = 600.0f, FlashCoupling = 1.0f,
                FlashLevel = lit ? (t => 1.0f) : null,
                CrawlRadiusMeters = strike ? 6_000.0f : 0.0f, CrawlSeed = 1000 + index * 7919, NearerRoot = surface.AcrossMeters < profile.Centre ? 0 : 1,
            };
        }
        SlotsOverride = list;
        FarOverride = Array.Empty<ContainmentFarStrike>();
    }

    /// <summary>Removes <see cref="SlotsOverride"/> and <see cref="FarOverride"/>: the bus drives the patches again.</summary>
    public void ClearDebugRipples()
    {
        SlotsOverride = null;
        FarOverride = null;
    }
}
