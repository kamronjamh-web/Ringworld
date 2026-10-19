using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    [ExportGroup("Adaptive grass output groups")]
    [Export] public bool EnableAdaptiveGrassGrouping { get; set; } = true;
    [Export(PropertyHint.Range, "16,1500,1")] public float GrassGroupingStartMeters { get; set; } = 128;
    [Export(PropertyHint.Range, "32,2000,1")] public float GrassGroupingFarStartMeters { get; set; } = 256;
    [Export(PropertyHint.Range, "1,2,1")] public int GrassGroupingMaximumLevel { get; set; } = 1;
    [Export(PropertyHint.Range, "0,128,1")] public float GrassGroupingHysteresisMeters { get; set; } = 16;
    [Export(PropertyHint.Range, "256,100000,256")] public int MaximumGrassSourcesPerGroup { get; set; } = 16384;
    public int AdaptiveGrassGroupCount { get; private set; }
    public int AdaptiveGrassLevel1GroupCount { get; private set; }
    public int AdaptiveGrassLevel2GroupCount { get; private set; }
    public int AdaptiveGrassGroupedSourceCount { get; private set; }
    public int AdaptiveGrassActivePresentationCount => _activeGrassPresentations.Count;
    public int AdaptiveGrassVisibilityCheckCount { get; private set; }
    public string AdaptiveGrassGroupingStatus { get; private set; } = "Waiting for GPU grass";
    public bool IsAdaptiveGrassGroupingSettled
    {
        get
        {
            if (_adaptiveSourcesDirty || _adaptivePresentationsDirty || _adaptiveCandidateRetryNeeded
                || _adaptiveCandidateCursor < _adaptiveCandidates.Count || _adaptiveOutputRequests.Count > 0) return false;
            foreach (var group in _adaptiveGroups.Values) if (!group.Active || group.Splitting
                || group.Key.Level > Math.Clamp(GrassGroupingMaximumLevel, 1, 2) || group.Resident.Data.InstanceCount > Math.Max(256, MaximumGrassSourcesPerGroup)) return false;
            return true;
        }
    }

    private sealed class AdaptiveGroup
    {
        internal required RingGrassGroupKey Key;
        internal required Resident Resident;
        internal required RingGrassTileData[] Sources;
        internal bool Active;
        internal bool Splitting;
    }
    private readonly Dictionary<RingGrassGroupKey, AdaptiveGroup> _adaptiveGroups = new();
    private readonly Dictionary<RingGrassTileKey, AdaptiveGroup> _adaptiveLeafOwners = new();
    private readonly List<KeyValuePair<RingGrassTileKey, Resident>> _activeGrassPresentations = new();
    private readonly List<RingGrassGroupKey> _adaptiveCandidates = new();
    private readonly List<RingGrassGroupKey> _adaptiveRetire = new();
    private readonly Queue<AdaptiveGroup> _adaptiveOutputRequests = new();
    private readonly HashSet<RingGrassGroupKey> _adaptiveOutputQueued = new();
    private bool _adaptiveSourcesDirty = true, _adaptivePresentationsDirty = true;
    private bool _adaptiveCandidateRetryNeeded = true;
    private int _adaptiveCandidateCursor;
    private Vector3? _adaptiveCandidateCamera;
    private AdaptiveSettings? _adaptiveSettings;
    private AdaptivePlannerState? _adaptivePlannerState;
    private bool _adaptiveHasTransitions;
    private readonly record struct AdaptivePlannerState(AdaptiveSettings Settings, RingLodGeometrySnapshot Geometry,
        Transform3D? Camera, GpuCameraState? CameraView, RingCanonicalPosition Focus);
    private readonly record struct AdaptiveSettings(bool Enabled, int Level, float Start, float Far, float Hysteresis,
        int Roots, bool Gpu, bool DistanceLod, bool Suspended, bool LegacyGust,
        float NearLod, float FarLod, float Transition, float Padding, bool CullTiles, bool FrustumCull);

    // Maintained beside _activeGrassPresentations (rebuilt whenever it is) so per-frame loops allocate no iterator.
    private readonly List<Resident> _activeGrassResidentList = new();
    private List<Resident> ActiveGrassResidents() => _activeGrassResidentList;
    private bool IsAdaptiveGroup(Resident resident) => resident.Group != null;
    // Cards: never group a tile whose tier can still change. The split threshold (start - hysteresis) stays beyond the last
    // ladder boundary + downgrade hysteresis + 4 m camera/focus slack, so a group only ever holds floor-tier tiles.
    internal float EffectiveGrassGroupingStartMeters => _style.IsCards
        ? Math.Max(GrassGroupingStartMeters, _style.LastTierStartMeters + _style.HysteresisMeters + 4f + Math.Max(4f, GrassGroupingHysteresisMeters))
        : GrassGroupingStartMeters;
    internal float EffectiveGrassGroupingFarStartMeters => Math.Max(GrassGroupingFarStartMeters, EffectiveGrassGroupingStartMeters);
    // While a Cards group is active and splitting, its leaves re-warm their near outputs; those stay hidden until the group
    // retires, otherwise density would double for a few frames. Blades always shows.
    private bool AdaptiveNearVisible(Resident resident) => !_style.IsCards || resident.Group != null
        || !_adaptiveLeafOwners.TryGetValue(resident.Data.Key, out var owner) || !owner.Active;
    private bool AdaptiveIntermediateVisible(Resident resident) => resident.Group is { } group
        ? group.Active : !_adaptiveLeafOwners.TryGetValue(resident.Data.Key, out var owner) || !owner.Active;

    /// <summary>Tests only (null in production): group transitions as (key, event, active). Events are "create", "activate",
    /// "split:&lt;cause&gt;" (the first time a group is marked splitting) and "retire:&lt;cause&gt;".</summary>
    internal Action<RingGrassGroupKey, string, bool>? AdaptiveGroupEventForTests { get; set; }
    private void AdaptiveGroupEvent(AdaptiveGroup group, string what) => AdaptiveGroupEventForTests?.Invoke(group.Key, what, group.Active);
    private void MarkAdaptiveSplitting(AdaptiveGroup group, string cause)
    {
        if (!group.Splitting && AdaptiveGroupEventForTests != null) AdaptiveGroupEvent(group, "split:" + cause);
        group.Splitting = true;
    }
    private void AdaptiveLeafChanged(RingGrassTileKey key)
    {
        _adaptiveSourcesDirty = _adaptivePresentationsDirty = true;
        _adaptiveCandidateRetryNeeded = true;
        if (_adaptiveLeafOwners.TryGetValue(key, out var group)) MarkAdaptiveSplitting(group, _tiles.ContainsKey(key) ? "leaf_changed" : "leaf_removed");
    }
    private void RefreshAdaptivePresentations()
    {
        if (!_adaptivePresentationsDirty) return;
        _activeGrassPresentations.Clear(); _activeGrassResidentList.Clear();
        foreach (var pair in _tiles)
        {
            if (_adaptiveLeafOwners.TryGetValue(pair.Key, out var owner) && owner.Active && !owner.Splitting) continue;
            _activeGrassPresentations.Add(pair); _activeGrassResidentList.Add(pair.Value);
        }
        foreach (var group in _adaptiveGroups.Values)
            { _activeGrassPresentations.Add(new(group.Resident.Data.Key, group.Resident)); _activeGrassResidentList.Add(group.Resident); }
        _adaptivePresentationsDirty = false;
        AdaptiveGrassGroupCount = AdaptiveGrassLevel1GroupCount = AdaptiveGrassLevel2GroupCount = AdaptiveGrassGroupedSourceCount = 0;
        foreach (var group in _adaptiveGroups.Values)
        {
            if (!group.Active) continue;
            AdaptiveGrassGroupCount++;
            if (group.Key.Level == 1) AdaptiveGrassLevel1GroupCount++; else AdaptiveGrassLevel2GroupCount++;
            AdaptiveGrassGroupedSourceCount += group.Resident.Data.InstanceCount;
        }
        _adaptiveHasTransitions = false;
        foreach (var group in _adaptiveGroups.Values) if (!group.Active || group.Splitting
            || group.Key.Level > Math.Clamp(GrassGroupingMaximumLevel, 1, 2) || group.Resident.Data.InstanceCount > Math.Max(256, MaximumGrassSourcesPerGroup))
            { _adaptiveHasTransitions = true; break; }
        InvalidateGrassWork();
    }

    private void UpdateAdaptiveGroups(RingLodGeometrySnapshot geometry, RingCanonicalPosition focus, long circumference, Camera3D? camera)
    {
        bool legacyGust = EnableGrassGustTexture && GrassGustTextureReady && !UsesSharedWindField;
        // Cards groups use the intermediate slot with the card mesh/material; there is no distance LOD, so the near band is empty.
        bool cards = _style.IsCards;
        var settings = new AdaptiveSettings(EnableAdaptiveGrassGrouping, Math.Clamp(GrassGroupingMaximumLevel, 1, 2),
            EffectiveGrassGroupingStartMeters, EffectiveGrassGroupingFarStartMeters, GrassGroupingHysteresisMeters,
            Math.Clamp(MaximumGrassSourcesPerGroup, 256, RingGpuVegetationCompute.MaximumRootsPerBatch),
            UseGpuCulling && !_gpuFailureLatched && _gpuCompute is { Ready: true }, cards || EnableDistanceLod, StreamingSuspended, legacyGust,
            cards ? 0f : Mathf.Min(LodNearDistanceMeters, EffectiveGrassDrawDistance() - .01f), EffectiveGrassDrawDistance(),
            cards ? 0f : LodTransitionWidthMeters, GrassFrustumPaddingMeters, CullGrassTiles, GpuFrustumCulling);
        bool enabled = settings.Enabled && settings.Gpu && settings.DistanceLod && !settings.Suspended && camera != null && !legacyGust;
        AdaptiveGrassGroupingStatus = !settings.Enabled ? "Disabled" : !settings.Gpu ? "Fine outputs: GPU unavailable"
            : legacyGust ? "Fine outputs: standalone gust mapping" : !settings.DistanceLod ? "Fine outputs: distance LOD disabled"
            : settings.Suspended ? "Suspended" : "Adaptive distant intermediate outputs";
        // Distances and frustum tests use the latched culling camera, so a small camera move leaves the planner state unchanged.
        Vector3? cameraPosition = camera == null ? null : LatchedGrassCameraPosition(camera);
        var plannerState = new AdaptivePlannerState(settings, geometry, camera == null ? null : _cullState.Transform,
            camera == null ? null : CaptureGrassCameraState(camera), focus);
        if (_adaptivePlannerState == plannerState && !_adaptiveSourcesDirty && !_adaptivePresentationsDirty
            && !_adaptiveHasTransitions && !_adaptiveCandidateRetryNeeded && _adaptiveCandidateCursor >= _adaptiveCandidates.Count) return;
        bool validateSources = _adaptiveSourcesDirty;
        bool validateDistances = _adaptivePlannerState?.Settings != settings || _adaptivePlannerState?.Geometry != geometry
            || _adaptivePlannerState?.Camera?.Origin != cameraPosition || _adaptivePlannerState?.Focus != focus;
        bool cameraViewChanged = _adaptivePlannerState?.CameraView != plannerState.CameraView;
        _adaptivePlannerState = plannerState;
        bool settingsChanged = _adaptiveSettings != settings;
        if (settingsChanged)
        {
            _adaptiveSettings = settings; _adaptiveSourcesDirty = true;
        }
        if (camera != null && (_adaptiveCandidateCamera == null || _adaptiveCandidateCamera.Value.DistanceTo(cameraPosition!.Value) > 4))
        {
            _adaptiveCandidateCamera = cameraPosition;
            if (_adaptiveCandidateCursor >= _adaptiveCandidates.Count) _adaptiveCandidateCursor = 0;
        }
        if (cameraViewChanged && _adaptiveCandidateCursor >= _adaptiveCandidates.Count) _adaptiveCandidateCursor = 0;
        _adaptiveRetire.Clear();
        bool transitionInProgress = false;
        foreach (var group in _adaptiveGroups.Values) if (!group.Active || group.Splitting) { transitionInProgress = true; break; }
        foreach (var pair in _adaptiveGroups)
        {
            AdaptiveGroup group = pair.Value;
            if (validateSources && !GroupSourcesPresent(group))
            {
                // Cards: a leaf of an active group left the wanted set (it lies beyond reach + streaming hysteresis, where every
                // card has faded out). Split instead of retiring, so the group keeps drawing the remaining leaves until their own
                // outputs are ready; retiring at once leaves them undrawn until they re-upload.
                if (_style.IsCards && group.Active) { MarkAdaptiveSplitting(group, "sources"); transitionInProgress = true; }
                else { AdaptiveGroupEvent(group, "retire:sources"); _adaptiveRetire.Add(pair.Key); continue; }
            }
            bool configSplit = group.Key.Level > settings.Level || group.Resident.Data.InstanceCount > settings.Roots;
            if (!validateDistances && !group.Splitting && group.Active && !configSplit) continue;
            float minimum = AdaptiveGroupMinimumDistance(group.Resident, geometry, cameraPosition, focus, circumference);
            if (minimum > settings.FarLod + settings.Transition * .5f)
            {
                AdaptiveGroupEvent(group, "retire:beyond");
                _adaptiveRetire.Add(pair.Key);
                continue;
            }
            float start = group.Key.Level == 2 ? settings.Far : settings.Start;
            float splitThreshold = Math.Max(start - Math.Max(0, settings.Hysteresis),
                settings.NearLod + settings.Transition * .5f + Math.Max(4, settings.Hysteresis));
            if (!enabled || minimum < splitThreshold || (configSplit && (!transitionInProgress || group.Splitting)))
            { MarkAdaptiveSplitting(group, !enabled ? "disabled" : minimum < splitThreshold ? "distance" : "config"); transitionInProgress = true; }
            if (!group.Active && (group.Resident.GpuIntermediateBatch < 0 || !group.Resident.IntermediateWanted || group.Splitting))
            {
                AdaptiveGroupEvent(group, "retire:pending");
                _adaptiveRetire.Add(pair.Key);
            }
        }
        foreach (var key in _adaptiveRetire) RetireAdaptiveGroup(key);
        // Split children join the active traversal while their outputs warm. The group remains the sole intermediate owner.
        foreach (var group in _adaptiveGroups.Values) if (group.Splitting) _adaptivePresentationsDirty = true;
        if (_adaptiveSourcesDirty)
        {
            RingGrassGroupKey? resume = !settingsChanged && _adaptiveCandidateCursor < _adaptiveCandidates.Count
                ? _adaptiveCandidates[_adaptiveCandidateCursor] : null;
            _adaptiveCandidates.Clear();
            var keys = new HashSet<RingGrassGroupKey>();
            if (enabled) foreach (var leaf in _tiles.Keys)
                for (int level = settings.Level; level >= 1; level--) keys.Add(RingGrassGroupKey.FromLeaf(leaf, level));
            _adaptiveCandidates.AddRange(keys);
            _adaptiveCandidates.Sort(static (a, b) => { int level = b.Level.CompareTo(a.Level); return level != 0 ? level : a.Around != b.Around ? a.Around.CompareTo(b.Around) : a.Across.CompareTo(b.Across); });
            _adaptiveCandidateCursor = resume is RingGrassGroupKey next ? Math.Max(0, _adaptiveCandidates.IndexOf(next)) : 0;
            _adaptiveSourcesDirty = false;
        }
        // At most one replacement buffer lives alongside its fine outputs. Candidate checks and group builds are bounded.
        bool pending = false;
        foreach (var group in _adaptiveGroups.Values) if (!group.Active || group.Splitting) { pending = true; break; }
        // Streaming can make an earlier rejected candidate complete after the cursor passes it.
        // Finish one full all-ready pass before claiming convergence, without restarting every live upload.
        if (_adaptiveCandidateRetryNeeded && !pending && _adaptiveCandidateCursor >= _adaptiveCandidates.Count
            && _jobs.Count == 0 && _requests.Count == 0 && _uploads.Count == 0)
        {
            _adaptiveCandidateCursor = 0;
            _adaptiveCandidateRetryNeeded = false;
        }
        if (enabled && !pending && (cards ? _cardMesh != null && _cardMaterial != null : _mesh != null && _intermediateMaterial != null))
        {
            int checks = Math.Min(8, _adaptiveCandidates.Count);
            for (int check = 0; check < checks; check++)
            {
                if (_adaptiveCandidateCursor >= _adaptiveCandidates.Count) break;
                RingGrassGroupKey key = _adaptiveCandidates[_adaptiveCandidateCursor++];
                if (_adaptiveGroups.ContainsKey(key) || !TryAdaptiveSources(key, settings.Roots, out var sources, allowLevel1Owners: key.Level == 2)) continue;
                var bounds = new RingGrassTileBounds(sources[0].Bounds.AroundMin, sources[^1].Bounds.AroundMax,
                    sources[0].Bounds.AcrossMin, sources[^1].Bounds.AcrossMax);
                float start = key.Level == 2 ? settings.Far : settings.Start;
                float threshold = Math.Max(start, settings.NearLod + settings.Transition * .5f + Math.Max(4, settings.Hysteresis)) + Math.Max(4, settings.Hysteresis);
                // Cheap canonical gate avoids rebasing/allocating an ineligible near group.
                if (Math.Sqrt(RingGrassCoordinates.TileDistanceSquared(bounds, focus, circumference)) < threshold) continue;
                RingGrassTileData description = RingGrassGrouping.Describe(sources, geometry);
                float maximumHeight = 0;
                foreach (var source in sources) maximumHeight = Math.Max(maximumHeight, _tiles[source.Key].MaximumSourceHeight);
                var preflight = new Resident { Data = description, MaximumSourceHeight = maximumHeight };
                float expandedMinimum = AdaptiveGroupMinimumDistance(preflight, geometry, cameraPosition!.Value, focus, circumference);
                if (expandedMinimum < threshold || expandedMinimum > EffectiveGrassDrawDistance() + settings.Transition * .5f) continue;
                // Admission uses the same conservative pass distance even when whole-tile culling is disabled.
                // A batch with no intermediate request cannot become ready and must never reserve the pending-group slot.
                float passDistance = ConservativePresentationDistance(description, preflight.Presentation.RawWorldBounds,
                    cameraPosition!.Value, focus, circumference);
                if (passDistance > EffectiveGrassDrawDistance() + settings.Transition * .5f + Math.Max(0, GrassFrustumPaddingMeters)) continue;
                if (CullGrassTiles && GpuFrustumCulling && !AabbIntersectsFrustum(preflight.Presentation.ExpandedWorldBounds, CachedCameraFrustumPlanes(camera!))) continue;
                // Upgrade an eligible complete ancestor through its fine children. The same gate used for creation chooses the bridge.
                bool upgrading = false;
                foreach (var source in sources) if (_adaptiveLeafOwners.TryGetValue(source.Key, out var owner))
                { MarkAdaptiveSplitting(owner, "upgrade"); upgrading = true; }
                if (upgrading) { _adaptivePresentationsDirty = true; _adaptiveCandidateCursor = 0; break; }
                RingGrassTileData data = RingGrassGrouping.Build(sources, geometry, out Vector4[] metadata);
                var resident = new Resident { Data = data, MaximumSourceHeight = maximumHeight, GroupMetadata = metadata };
                var group = new AdaptiveGroup { Key = key, Resident = resident, Sources = sources };
                resident.Group = group;
                _adaptiveGroups.Add(key, group);
                _adaptiveHasTransitions = true;
                foreach (var leaf in sources) _adaptiveLeafOwners.Add(leaf.Key, group);
                InstallAdaptiveGroup(group);
                AdaptiveGroupEvent(group, "create");
                _adaptivePresentationsDirty = true;
                break;
            }
        }
        RefreshAdaptivePresentations();
    }

    private bool TryAdaptiveSources(RingGrassGroupKey key, int roots, out RingGrassTileData[] sources, bool allowLevel1Owners = false)
    {
        sources = new RingGrassTileData[key.Factor * key.Factor];
        int index = 0;
        for (int across = 0; across < key.Factor; across++) for (int around = 0; around < key.Factor; around++)
        {
            RingGrassTileKey leaf = key.Leaf(around, across);
            if (!_wanted.Contains(leaf) || (_adaptiveLeafOwners.TryGetValue(leaf, out var owner)
                    && (!allowLevel1Owners || owner.Key.Level != 1 || !owner.Active || owner.Splitting)) || !_tiles.TryGetValue(leaf, out var resident)
                || _jobs.ContainsKey(leaf) || _requested.Contains(leaf) || _uploadQueued.Contains(leaf)) return false;
            sources[index++] = resident.Data;
        }
        return RingGrassGrouping.Compatible(sources, key.Factor, roots);
    }
    private bool GroupSourcesPresent(AdaptiveGroup group)
    {
        foreach (var leaf in group.Sources)
        {
            if (!_tiles.TryGetValue(leaf.Key, out var resident) || !_wanted.Contains(leaf.Key)) return false;
            if (!ReferenceEquals(leaf, resident.Data)) MarkAdaptiveSplitting(group, "source_replaced");
        }
        return true;
    }
    private float AdaptiveGroupMinimumDistance(Resident resident, RingLodGeometrySnapshot geometry, Vector3? camera,
        RingCanonicalPosition focus, long circumference)
    {
        RefreshCanonicalTilePresentation(resident.Presentation, resident.Data, geometry);
        RefreshPresentationBounds(resident.Presentation, resident.Data, resident.Presentation.World, resident.MaximumSourceHeight);
        if (camera is Vector3 world)
        {
            ConservativeAabbCameraDistances(resident.Presentation.ExpandedWorldBounds, world, out float minimum, out _);
            return minimum;
        }
        return (float)Math.Sqrt(RingGrassCoordinates.TileDistanceSquared(resident.Data.Bounds, focus, circumference));
    }
    private void InstallAdaptiveGroup(AdaptiveGroup group)
    {
        Resident resident = group.Resident;
        bool cards = _style.IsCards;
        MultiMeshInstance3D renderer = CreateRenderer(resident.Data.Key, IntermediateMeshFor(resident.Data), cards ? _cardMaterial! : _intermediateMaterial!, $"Group{group.Key.Level}");
        resident.IntermediateRenderer = renderer;
        // Register directly as GPU-only: concatenated sources must never use INSTANCE_ID-based CPU LOD.
        resident.GpuIntermediateBatch = _gpuCompute!.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(
            $"grass-group-{group.Key.Level}-{group.Key.Around}-{group.Key.Across}", renderer.Multimesh!, IntermediateMeshFor(resident.Data),
            resident.Data.Buffer, resident.GroupMetadata, resident.Data.InstanceCount, LodAabb(resident.Data.LocalAabb), true));
        if (renderer is GrassPresentationRenderer tracked) tracked.RecordInstalledMultiMesh(renderer.Multimesh!, resident.Data.InstanceCount);
        SetGpuCulled(renderer, true, resident.Presentation.Intermediate);
        ApplyGrassHazeSlot(renderer, resident.Presentation.Intermediate, resident.GpuIntermediateBatch);
        if (!cards) ConfigureLodPass(renderer, resident.Data.InstanceCount, true, resident.Data.Key);
        SetRendererVisible(renderer, false);
    }
    private void QueueAdaptiveOutput(AdaptiveGroup group)
    {
        if (_adaptiveOutputQueued.Add(group.Key)) _adaptiveOutputRequests.Enqueue(group);
    }
    private int InstallAdaptiveOutputs(ulong start)
    {
        int installed = 0;
        while (_adaptiveOutputRequests.Count > 0 && installed < Math.Max(1, MaximumTileUploadsPerFrame)
            && (Time.GetTicksUsec() - start) / 1000d < Math.Max(.1, MaximumTileUploadMilliseconds))
        {
            AdaptiveGroup group = _adaptiveOutputRequests.Dequeue();
            _adaptiveOutputQueued.Remove(group.Key);
            if (!_adaptiveGroups.TryGetValue(group.Key, out var current) || !ReferenceEquals(current, group)
                || group.Resident.IntermediateRenderer != null || !group.Resident.IntermediateWanted || _gpuCompute is not { Ready: true }) continue;
            InstallAdaptiveGroup(group);
            InvalidateGrassWork();
            installed++;
        }
        return installed;
    }
    private void CompleteAdaptiveHandoffs()
    {
        _adaptiveRetire.Clear();
        foreach (var group in _adaptiveGroups.Values)
        {
            if (!group.Active && !group.Splitting && group.Resident.IntermediateWanted
                && _gpuReadyBatches.Contains(group.Resident.GpuIntermediateBatch))
            {
                group.Active = true;
                AdaptiveGroupEvent(group, "activate");
                foreach (var leaf in group.Sources) if (_tiles.TryGetValue(leaf.Key, out var resident))
                {
                    RetireGrassOutput(resident, true);
                    // Group admission is entirely beyond the near transition envelope.
                    RetireGrassOutput(resident, false);
                    resident.NearWanted = resident.IntermediateWanted = false;
                }
                _adaptivePresentationsDirty = true;
            }
            if (group.Active && group.Splitting)
            {
                bool ready = true;
                foreach (var source in group.Sources)
                {
                    // Cards splits (rather than retires) when a leaf was evicted; a departed leaf has nothing to warm.
                    if (!_tiles.TryGetValue(source.Key, out var leaf)) { if (_style.IsCards) continue; ready = false; break; }
                    if (leaf.IntermediateWanted && (leaf.IntermediateRenderer == null
                        || (UseGpuCulling && (leaf.GpuIntermediateBatch < 0 || !_gpuReadyBatches.Contains(leaf.GpuIntermediateBatch))))) { ready = false; break; }
                    if (leaf.NearWanted && (leaf.Renderer == null || (UseGpuCulling && (leaf.GpuNearBatch < 0 || !_gpuReadyBatches.Contains(leaf.GpuNearBatch))))) { ready = false; break; }
                }
                if (ready) { AdaptiveGroupEvent(group, "retire:split_done"); _adaptiveRetire.Add(group.Key); }
            }
        }
        foreach (var key in _adaptiveRetire) RetireAdaptiveGroup(key);
        // Called during active enumeration: rebuild on the next visibility pass.
        if (_adaptivePresentationsDirty) InvalidateGrassWork();
    }
    private void RetireAdaptiveGroup(RingGrassGroupKey key)
    {
        if (!_adaptiveGroups.Remove(key, out var group)) return;
        RetireGrassOutput(group.Resident, true);
        _adaptiveOutputQueued.Remove(key);
        foreach (var source in group.Sources)
        {
            _adaptiveLeafOwners.Remove(source.Key);
        }
        _adaptivePresentationsDirty = true;
        InvalidateGrassWork();
    }
    private void ClearAdaptiveGroups()
    {
        foreach (var group in _adaptiveGroups.Values) { AdaptiveGroupEvent(group, "retire:clear"); RetireGrassOutput(group.Resident, true); }
        _adaptiveGroups.Clear(); _adaptiveLeafOwners.Clear(); _adaptiveCandidates.Clear(); _activeGrassPresentations.Clear(); _activeGrassResidentList.Clear();
        _adaptiveOutputRequests.Clear(); _adaptiveOutputQueued.Clear();
        _adaptiveSourcesDirty = _adaptivePresentationsDirty = true;
        _adaptiveCandidateRetryNeeded = true;
        AdaptiveGrassGroupCount = AdaptiveGrassLevel1GroupCount = AdaptiveGrassLevel2GroupCount = AdaptiveGrassGroupedSourceCount = 0;
        InvalidateGrassWork();
    }
    private void SplitAllAdaptiveGroups()
    {
        foreach (var group in _adaptiveGroups.Values) MarkAdaptiveSplitting(group, "split_all");
        _adaptivePresentationsDirty = _adaptiveSourcesDirty = true;
    }

    // GPU failure/explicit CPU mode must restore leaf-local INSTANCE_ID before the grouped allocation is retired.
    private void RestoreAdaptiveFineOutputs()
    {
        if (_style.IsCards)
        {
            // Cards: the leaves keep the near slot. Re-upload them as ordinary CPU outputs; the group retires with the clear below.
            foreach (var group in _adaptiveGroups.Values) foreach (var source in group.Sources)
                if (_tiles.TryGetValue(source.Key, out var leaf))
                {
                    leaf.NearWanted = group.Resident.IntermediateWanted;
                    if (_uploadQueued.Add(source.Key)) _uploads.Enqueue(source.Key);
                }
            ClearAdaptiveGroups();
            RefreshAdaptivePresentations();
            return;
        }
        foreach (var group in _adaptiveGroups.Values) foreach (var source in group.Sources)
        {
            if (!_tiles.TryGetValue(source.Key, out var leaf) || _material == null || _intermediateMaterial == null) continue;
            leaf.IntermediateRenderer ??= CreateRenderer(source.Key, IntermediateMeshFor(leaf.Data), _intermediateMaterial, "Intermediate");
            ReplaceCpuMultiMesh(leaf.IntermediateRenderer, IntermediateMeshFor(leaf.Data), LodAabb(leaf.Data.LocalAabb), leaf.Data.Buffer);
            ConfigureLodPass(leaf.IntermediateRenderer, leaf.Data.InstanceCount, true, source.Key);
            leaf.IntermediateWanted = group.Resident.IntermediateWanted;
            if (group.Resident.Presentation.Bounds != null)
                RefreshTilePresentation(leaf.Presentation, leaf.IntermediateRenderer, leaf.Data, group.Resident.Presentation.Geometry, _presentationInverseLayer, true);
            SetRendererVisible(leaf.IntermediateRenderer, leaf.IntermediateWanted);
        }
        ClearAdaptiveGroups();
        RefreshAdaptivePresentations();
    }

    internal (int ActiveGroups, int OwnedLeaves, int DuplicateOwners, int MissingLeaves, int MismatchedSources, int PendingGroups)
        CaptureAdaptiveGrassOwnershipAuditForTests()
    {
        var leaves = new HashSet<RingGrassTileKey>();
        int active = 0, duplicate = 0, missing = 0, mismatch = 0, pending = 0;
        foreach (var group in _adaptiveGroups.Values)
        {
            if (group.Active) active++; else pending++;
            foreach (var source in group.Sources)
            {
                if (!leaves.Add(source.Key)) duplicate++;
                if (!_tiles.TryGetValue(source.Key, out var leaf)) { if (!(_style.IsCards && group.Splitting)) missing++; }
                else if (!ReferenceEquals(leaf.Data, source) && !group.Splitting) mismatch++;
                if (!_adaptiveLeafOwners.TryGetValue(source.Key, out var owner) || !ReferenceEquals(owner, group)) mismatch++;
            }
        }
        return (active, leaves.Count, duplicate, missing, mismatch, pending);
    }
    public void AssertAdaptiveGrassOwnershipForTests()
    {
        var audit = CaptureAdaptiveGrassOwnershipAuditForTests();
        if (audit.DuplicateOwners != 0 || audit.MissingLeaves != 0 || audit.MismatchedSources != 0 || audit.OwnedLeaves != _adaptiveLeafOwners.Count)
            throw new InvalidOperationException($"Adaptive grass ownership failed: {audit}");
        foreach (var group in _adaptiveGroups.Values)
        {
            if (!group.Active || group.Splitting) continue;
            foreach (var source in group.Sources) if (_tiles.TryGetValue(source.Key, out var leaf)
                && (leaf.Renderer != null || leaf.IntermediateRenderer != null))
                throw new InvalidOperationException("Active grouped leaf retained a fine output allocation");
        }
    }

    /// <summary>Tests: who draws a wanted leaf this frame. LeafDrawn = its own near renderer is visible; GroupDrawn = the
    /// owner group's renderer is visible. *Unready = visible while the GPU batch has not completed a dispatch yet.</summary>
    internal readonly record struct LeafCoverageAudit(RingGrassTileKey Key, int InstanceCount, byte Tier, bool LeafDrawn, bool LeafUnready,
        bool HasOwner, RingGrassGroupKey Owner, bool OwnerActive, bool OwnerSplitting, bool GroupDrawn, bool GroupUnready,
        bool NearWanted, bool OwnerWanted, bool BoundsValid, Aabb RawBounds, bool HasRenderer, int NearBatch, bool NearBatchReady, ulong PlacementHash);
    internal List<LeafCoverageAudit> CaptureLeafCoverageForTests()
    {
        var result = new List<LeafCoverageAudit>(_wanted.Count);
        bool Drawn(MultiMeshInstance3D? renderer) => renderer != null && GodotObject.IsInstanceValid(renderer) && renderer.IsInsideTree() && renderer.Visible;
        bool Unready(MultiMeshInstance3D? renderer, int batch) => Drawn(renderer) && batch >= 0 && !(_gpuCompute?.IsBatchReady(batch) ?? false);
        foreach (var key in _wanted)
        {
            if (!_tiles.TryGetValue(key, out var leaf)) continue;
            _adaptiveLeafOwners.TryGetValue(key, out var owner);
            Resident? group = owner?.Resident;
            result.Add(new LeafCoverageAudit(key, leaf.Data.InstanceCount, leaf.Data.SourceTier,
                Drawn(leaf.Renderer), Unready(leaf.Renderer, leaf.GpuNearBatch),
                owner != null, owner?.Key ?? default, owner?.Active ?? false, owner?.Splitting ?? false,
                Drawn(group?.IntermediateRenderer), group != null && Unready(group.IntermediateRenderer, group.GpuIntermediateBatch),
                leaf.NearWanted, group?.IntermediateWanted ?? false, leaf.Presentation.Bounds != null, leaf.Presentation.RawWorldBounds,
                leaf.Renderer != null, leaf.GpuNearBatch, leaf.GpuNearBatch >= 0 && (_gpuCompute?.IsBatchReady(leaf.GpuNearBatch) ?? false), leaf.Data.PlacementHash));
        }
        return result;
    }

    public static void RunGrassAdaptiveGroupingTests()
    {
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Adaptive grass: " + message); }
        Require(RingGrassGroupKey.FromLeaf(new(-1, -1), 1) == new RingGrassGroupKey(-1, -1, 1), "negative world alignment");
        Require(RingGrassGroupKey.FromLeaf(new(3, 3), 2).Leaf(3, 3) == new RingGrassTileKey(3, 3), "64m alignment");
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero,
            50000, 0, 0, 50000 * Math.PI * 2, 0, 0, new RingCanonicalPosition(32, 0, 32));
        var leaves = new List<RingGrassTileData>();
        for (int across = 0; across < 2; across++) for (int around = 0; around < 2; around++)
        {
            var bounds = new RingGrassTileBounds(around * 16, (around + 1) * 16, across * 16, (across + 1) * 16);
            int count = 2 + around + across;
            float[] rows = new float[count * 20];
            for (int id = 0; id < count; id++)
            {
                Transform3D transform = RingGrassCoordinates.CreateBladeLocalTransform(bounds, 2, bounds.AroundCenter + id,
                    2, bounds.AcrossCenter - id, .08f, .8f + id * .1f, .4f, geometry);
                RingGrassGrouping.WriteTransform(rows, id * 20, transform);
                for (int i = 12; i < 20; i++) rows[id * 20 + i] = (id + i) * .013f;
            }
            leaves.Add(new RingGrassTileData(new(around, across), bounds, 2, rows,
                new Aabb(new(-10, -3, -10), new(20, 12, 20)), 1, .5f, 1, 4));
        }
        Require(RingGrassGrouping.Compatible(leaves, 2, 100), "complete compatible quartet");
        Require(!RingGrassGrouping.Compatible(leaves, 4, 100), "incomplete 64m block rejected");
        Require(!RingGrassGrouping.Compatible(leaves, 2, 2), "source cap rejected");
        RingGrassTileData merged = RingGrassGrouping.Build(leaves, geometry, out Vector4[] metadata);
        Transform3D groupWorld = RingGrassCoordinates.CreateTileWorldTransform(merged.Bounds, merged.AnchorHeight, geometry);
        int destination = 0;
        foreach (var leaf in leaves)
        {
            Transform3D world = RingGrassCoordinates.CreateTileWorldTransform(leaf.Bounds, leaf.AnchorHeight, geometry);
            for (int id = 0; id < leaf.InstanceCount; id++, destination++)
            {
                Transform3D expected = world * RingGrassGrouping.ReadTransform(leaf.Buffer, id * 20);
                Transform3D actual = groupWorld * RingGrassGrouping.ReadTransform(merged.Buffer, destination * 20);
                Require(expected.Origin.DistanceTo(actual.Origin) < .006f && expected.Basis.X.DistanceTo(actual.Basis.X) < .00001f
                    && expected.Basis.Y.DistanceTo(actual.Basis.Y) < .00001f, "full curved transform preserved");
                Require(metadata[destination].X == (id + .5f) / leaf.InstanceCount && metadata[destination].Y == id
                    && metadata[destination].Z == 1, "leaf rank and hash ID preserved");
                for (int i = 12; i < 20; i++) Require(leaf.Buffer[id * 20 + i] == merged.Buffer[destination * 20 + i], "color/custom/wind preserved");
                for (int step = 0; step <= 20; step++)
                {
                    float handoff = step / 20f, density = Mathf.Lerp(.8f, .25f, handoff);
                    float hash = GrassLodHash(leaf.Buffer[id * 20 + 16], leaf.Buffer[id * 20 + 17], id);
                    float mergedHash = GrassLodHash(merged.Buffer[destination * 20 + 16], merged.Buffer[destination * 20 + 17], (int)metadata[destination].Y);
                    Require(((id + .5f) / leaf.InstanceCount <= density && hash < handoff)
                        == (metadata[destination].X <= density && mergedHash < handoff), "density/handoff selectors preserved");
                }
            }
        }
        Require(destination == merged.InstanceCount, "no holes or duplicate roots");
        leaves[0] = new RingGrassTileData(leaves[0].Key, leaves[0].Bounds, 2, leaves[0].Buffer, leaves[0].LocalAabb, 1,
            .5f, 1, 4, new byte[32 * 32 * 4]);
        Require(!RingGrassGrouping.Compatible(leaves, 2, 100), "tile modifier mappings retain fine output");
    }
}
