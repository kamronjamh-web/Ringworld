using System;
using System.Globalization;
using System.Text;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Presents the code-generated bore liner, rim cap, teal iris rim (the plug is open since M2-3) and its light for the (at most two) sites nearest the
/// explorer and the camera, while the surface apertures are consumed (<see cref="RingTerrain3DGameplay.SurfaceApertureConsumersActive"/>).
/// Two permanent slots; assigning a slot only sets a transform, visibility and collision. It also publishes the aperture
/// shader globals the far terrain and ocean shaders discard against. Always present in the scene; inert in non-site worlds.
/// </summary>
public partial class EmitterBoreLayer : Node3D
{
    public const int SlotCount = 2;
    public const float DefaultVisibleRangeMeters = 16000f, DefaultPlugLightEnergy = 2f;
    private static readonly StringName[] CenterGlobals = { "ring_surface_aperture_0", "ring_surface_aperture_1" };
    private static readonly StringName[] AxisGlobals = { "ring_surface_aperture_axis_0", "ring_surface_aperture_axis_1" };

    private sealed class BoreSlot
    {
        public Node3D Root = null!;
        public MeshInstance3D Mesh = null!;
        public StaticBody3D Body = null!;
        public CollisionShape3D LinerShape = null!;
        public OmniLight3D Light = null!;
        public int Site = -1;
        public SiteKind Kind;
        public bool HasKind;
        public Vector4 PublishedCenter = new(float.NaN, 0, 0, 0), PublishedAxis;
        /// <summary>Phase E4e: factor on the plug light energy while the site's eclipse beam fires (1 = none).</summary>
        public float EventScale = 1f;
    }

    [Export] public bool AutoBind { get; set; } = true;
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GameplayPath { get; set; } = new("../Terrain3DGameplay");

    private readonly BoreSlot[] _slots = new BoreSlot[SlotCount];
    private readonly int[] _selected = new int[EmitterBoreSelector.MaxSelected];
    private VoxelChunkGridManager? _grid;
    private RingVoxelExplorerController? _explorer;
    private RingTerrain3DGameplay? _gameplay;
    private EmitterSitePlacementSource? _placements;
    private object? _lastSamplerSeen;
    private bool _subscribed, _consumersActive;
    private float _rangeMeters = DefaultVisibleRangeMeters, _lightEnergy = DefaultPlugLightEnergy;
    private Transform3D _lastGridGlobal, _lastSelfGlobal;
    private int _selectedCount;

    public bool IsBound => _placements != null && _grid != null;
    public int StepCount { get; private set; }
    public int AnchorRefreshCount { get; private set; }
    public float VisibleRangeMeters => _rangeMeters;
    public float PlugLightEnergy => _lightEnergy;
    public bool ConsumersActive => _consumersActive;
    public int ActiveBoreCount
    {
        get
        {
            int count = 0;
            foreach (BoreSlot slot in _slots) if (slot.Site >= 0) count++;
            return count;
        }
    }

    public override void _Ready()
    {
        ProcessPhysicsPriority = -100;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        for (int i = 0; i < SlotCount; i++) _slots[i] = CreateSlot(i);
        if (!AutoBind || _grid == null) SetPhysicsProcess(false);
    }

    public override void _ExitTree()
    {
        if (_grid != null && _subscribed) { _grid.CanonicalFloatingOriginAnchorChanged -= HandleAnchorChanged; _subscribed = false; }
        for (int i = 0; i < SlotCount; i++) PublishGlobal(i, Vector4.Zero, Vector4.Zero, force: true);
    }

    private BoreSlot CreateSlot(int index)
    {
        var slot = new BoreSlot();
        slot.Root = new Node3D { Name = "BoreSlot" + index, Visible = false };
        slot.Mesh = new MeshInstance3D { Name = "Bore", CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        slot.Body = new StaticBody3D { Name = "Collision", ProcessMode = ProcessModeEnum.Disabled };
        slot.LinerShape = new CollisionShape3D { Name = "Liner" };
        slot.Light = new OmniLight3D
        {
            Name = "PlugLight", Position = new Vector3(0f, EmitterBoreGeometry.LightLocalY, 0f), ShadowEnabled = false, OmniAttenuation = 1f,
            OmniRange = EmitterBoreGeometry.RegularLightRange, LightColor = EmitterBoreGeometry.FallbackTeal, LightEnergy = _lightEnergy
        };
        slot.Body.AddChild(slot.LinerShape);
        slot.Root.AddChild(slot.Mesh);
        slot.Root.AddChild(slot.Body);
        slot.Root.AddChild(slot.Light);
        AddChild(slot.Root);
        return slot;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_grid == null) return;
        if (!IsBound)
        {
            object? sampler = _grid.TerrainSampler;
            if (sampler == null || ReferenceEquals(sampler, _lastSamplerSeen)) return;
            _lastSamplerSeen = sampler;
            if (_grid.TerrainSampler?.EmitterSites is { } registry) Bind(_grid, new EmitterSitePlacementSource(registry));
            return;
        }
        // The gameplay node is installed after _Ready, so resolve the collaborators lazily.
        _explorer ??= GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _gameplay ??= GetNodeOrNull<RingTerrain3DGameplay>(GameplayPath);
        if (_explorer == null) return;
        bool consumers = _gameplay?.SurfaceApertureConsumersActive == true;
        RingCanonicalPosition? camera = null;
        if (GetViewport()?.GetCamera3D() is { } view) camera = _grid.WorldToCanonicalRingPosition64(view.GlobalPosition);
        Step(_explorer.CanonicalPosition, camera, consumers);
    }

    public override void _Process(double delta) => PublishGlobals();

    /// <summary>Attaches the placements. Slot assignment is driven by <see cref="Step"/>.</summary>
    public bool Bind(VoxelChunkGridManager grid, EmitterSitePlacementSource placements)
    {
        if (IsBound) return true;
        _grid = grid;
        _placements = placements;
        grid.CanonicalFloatingOriginAnchorChanged += HandleAnchorChanged;
        _subscribed = true;
        return true;
    }

    /// <summary>Applies the tuning values.</summary>
    public void Configure(float visibleRangeMeters, float plugLightEnergy)
    {
        _rangeMeters = float.IsFinite(visibleRangeMeters) ? Mathf.Clamp(visibleRangeMeters, 0f, 100000f) : DefaultVisibleRangeMeters;
        _lightEnergy = float.IsFinite(plugLightEnergy) ? Mathf.Clamp(plugLightEnergy, 0f, 16f) : DefaultPlugLightEnergy;
        foreach (BoreSlot slot in _slots)
        {
            if (slot == null) continue;
            slot.Light.LightEnergy = _lightEnergy * slot.EventScale;
            slot.Light.Visible = slot.Site >= 0 && _lightEnergy > 0f;
        }
    }

    /// <summary>
    /// Phase E4e (spec E.7): scales the plug light of slot <paramref name="index"/> while its spire's eclipse beam fires (<paramref name="scale"/> 1 restores it).
    /// One light per slot; the value is written only when it changes. A slot assignment or release resets it to 1.
    /// </summary>
    public void SetSlotEventScale(int index, float scale)
    {
        if (index < 0 || index >= SlotCount || _slots[index] == null) return;
        scale = float.IsFinite(scale) ? Mathf.Clamp(scale, 1f, 16f) : 1f;
        BoreSlot slot = _slots[index];
        if (slot.EventScale == scale) return;
        slot.EventScale = scale;
        if (slot.Site >= 0) slot.Light.LightEnergy = _lightEnergy * scale;
    }

    public float GetSlotEventScale(int index) => index >= 0 && index < SlotCount && _slots[index] != null ? _slots[index].EventScale : 1f;

    /// <summary>Selects, assigns and releases slots for the current observers. <paramref name="consumersActive"/> false releases everything.</summary>
    public void Step(in RingCanonicalPosition explorer, RingCanonicalPosition? camera, bool consumersActive)
    {
        if (_placements == null || _grid == null) return;
        _consumersActive = consumersActive;
        CheckRootShift();
        _selectedCount = consumersActive ? EmitterBoreSelector.Select(_placements, explorer, camera, _rangeMeters, _selected) : 0;

        foreach (BoreSlot slot in _slots)
            if (slot.Site >= 0 && !IsSelected(slot.Site)) Release(slot);
        RingLodGeometrySnapshot? snapshot = null;
        for (int i = 0; i < _selectedCount; i++)
        {
            int site = _selected[i];
            if (FindSlotForSite(site) >= 0) continue;
            BoreSlot? free = null;
            foreach (BoreSlot slot in _slots) if (slot.Site < 0) { free = slot; break; }
            if (free == null) break;
            snapshot ??= _grid.CaptureLodGeometrySnapshot();
            Assign(free, site, snapshot.Value);
        }
        StepCount++;
    }

    private bool IsSelected(int site)
    {
        for (int i = 0; i < _selectedCount; i++) if (_selected[i] == site) return true;
        return false;
    }

    private int FindSlotForSite(int site)
    {
        for (int i = 0; i < SlotCount; i++) if (_slots[i].Site == site) return i;
        return -1;
    }

    private void Assign(BoreSlot slot, int site, in RingLodGeometrySnapshot snapshot)
    {
        EmitterSitePlacement placement = _placements!.GetPlacement(site);
        if (!slot.HasKind || slot.Kind != placement.Kind)
        {
            EmitterBoreKindResources resources = EmitterBoreGeometry.Get(placement.Kind);
            slot.Mesh.Mesh = resources.Mesh;
            if (_grid != null) RingPoiHaze.RegisterTree(slot.Mesh, _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
            // The liner hangs below the pad datum (the mesh origin), so it sits wholly inside the occlusion zone.
            PoiBaseAmbientOcclusion.Apply(slot.Mesh, slot.Mesh, slot.Mesh.GetAabb().Size.Y);
            slot.LinerShape.Shape = resources.LinerShape;
            slot.Light.OmniRange = resources.LightRange;
            slot.Light.LightColor = resources.InlayEmission;
            slot.Kind = placement.Kind;
            slot.HasKind = true;
        }
        slot.Site = site;
        slot.Root.Transform = SlotTransform(snapshot, site);
        slot.EventScale = 1f;
        slot.Light.LightEnergy = _lightEnergy;
        slot.Light.Visible = _lightEnergy > 0f;
        slot.Root.Visible = true;
        slot.Body.ProcessMode = ProcessModeEnum.Inherit;
    }

    private void Release(BoreSlot slot)
    {
        slot.Site = -1;
        slot.EventScale = 1f;
        slot.Root.Visible = false;
        slot.Light.Visible = false;
        slot.Body.ProcessMode = ProcessModeEnum.Disabled;
    }

    /// <summary>The plug top must land exactly at P - 200, so the pad clearance is dropped (clearance 0).</summary>
    private Transform3D SlotTransform(in RingLodGeometrySnapshot snapshot, int site)
    {
        Transform3D worldToLayer = IsInsideTree() ? GlobalTransform.AffineInverse() : Transform3D.Identity;
        return worldToLayer * _placements!.GetWorldTransform(snapshot, site, 0d);
    }

    // ---- Shader globals ----------------------------------------------------------------------------------------------------

    private void PublishGlobals()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            BoreSlot slot = _slots[i];
            if (slot == null || slot.Site < 0 || !slot.Root.IsInsideTree()) { PublishGlobal(i, Vector4.Zero, Vector4.Zero, force: false); continue; }
            Transform3D global = slot.Root.GlobalTransform;
            Vector3 c = global.Origin, axis = global.Basis.Y.Normalized();
            PublishGlobal(i, new Vector4(c.X, c.Y, c.Z, EmitterSiteApertures.DiscardRadius(slot.Kind)),
                new Vector4(axis.X, axis.Y, axis.Z, EmitterSiteApertures.PlugDepthM), force: false);
        }
    }

    private void PublishGlobal(int index, Vector4 center, Vector4 axis, bool force)
    {
        BoreSlot? slot = _slots[index];
        if (!force && slot != null && slot.PublishedCenter == center && slot.PublishedAxis == axis) return;
        RenderingServer.GlobalShaderParameterSet(CenterGlobals[index], center);
        RenderingServer.GlobalShaderParameterSet(AxisGlobals[index], axis);
        if (slot == null) return;
        slot.PublishedCenter = center;
        slot.PublishedAxis = axis;
    }

    /// <summary>Currently published aperture globals for slot <paramref name="index"/> (center xyz+discard radius, axis xyz+depth).</summary>
    public Vector4 GetPublishedCenter(int index) => index >= 0 && index < SlotCount ? _slots[index].PublishedCenter : Vector4.Zero;

    // ---- Floating origin ---------------------------------------------------------------------------------------------------

    private void HandleAnchorChanged(RingLodGeometrySnapshot previous, RingLodGeometrySnapshot current, RingCanonicalPosition anchor)
    {
        _ = previous; _ = anchor;
        RefreshActiveTransforms(current);
    }

    /// <summary>Recomputes every active slot transform absolutely from <paramref name="snapshot"/>; never applies a delta.</summary>
    private void RefreshActiveTransforms(in RingLodGeometrySnapshot snapshot)
    {
        if (_placements == null) return;
        foreach (BoreSlot slot in _slots)
            if (slot.Site >= 0) slot.Root.Transform = SlotTransform(snapshot, slot.Site);
        if (_grid != null && _grid.IsInsideTree()) _lastGridGlobal = _grid.GlobalTransform;
        if (IsInsideTree()) _lastSelfGlobal = GlobalTransform;
        AnchorRefreshCount++;
    }

    private void CheckRootShift()
    {
        if (_grid == null || !_grid.IsInsideTree() || !IsInsideTree()) return;
        if (_lastGridGlobal == default && _lastSelfGlobal == default)
        {
            _lastGridGlobal = _grid.GlobalTransform;
            _lastSelfGlobal = GlobalTransform;
            return;
        }
        if (!_grid.GlobalTransform.IsEqualApprox(_lastGridGlobal) || !GlobalTransform.IsEqualApprox(_lastSelfGlobal))
            RefreshActiveTransforms(_grid.CaptureLodGeometrySnapshot());
    }

    // ---- Diagnostics -------------------------------------------------------------------------------------------------------

    /// <summary>Site index presented in slot <paramref name="index"/>, or -1.</summary>
    public int GetActiveSite(int index) => index >= 0 && index < SlotCount ? _slots[index].Site : -1;

    public bool IsSlotCollisionEnabled(int index)
        => index >= 0 && index < SlotCount && _slots[index].Body.ProcessMode != ProcessModeEnum.Disabled;

    public Node3D? GetSlotNode(int index) => index >= 0 && index < SlotCount ? _slots[index].Root : null;
    public OmniLight3D? GetSlotLight(int index) => index >= 0 && index < SlotCount ? _slots[index].Light : null;
    public MeshInstance3D? GetSlotMesh(int index) => index >= 0 && index < SlotCount ? _slots[index].Mesh : null;
    public StaticBody3D? GetSlotBody(int index) => index >= 0 && index < SlotCount ? _slots[index].Body : null;

    public string DescribeStatus()
    {
        if (_grid == null) return "Emitter bores unavailable (no chunk grid).";
        if (!IsBound) return "Emitter bores inactive (needs the v5 sites world).";
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Bores {ActiveBoreCount}/{SlotCount} active, apertures {(_consumersActive ? "on" : "off")}");
        for (int i = 0; i < SlotCount; i++)
            if (_slots[i].Site >= 0)
                text.Append(CultureInfo.InvariantCulture, $"; #{_slots[i].Site} {(_slots[i].Kind == SiteKind.Main ? "main" : "regular")} coll {(IsSlotCollisionEnabled(i) ? "on" : "off")}");
        return text.ToString();
    }

    /// <summary>The nearest site (no range limit) with its bore numbers, for tests and tools. Empty when unbound.</summary>
    public Godot.Collections.Dictionary FindNearestBoreSite(double around, double across)
    {
        var result = new Godot.Collections.Dictionary();
        if (_placements == null) return result;
        EmitterSiteRegistry registry = _placements.Registry;
        double circumference = _placements.CircumferenceMeters;
        double wrapped = around % circumference;
        if (wrapped < 0d) wrapped += circumference;
        if (!registry.TryGetNearestSite((long)Math.Floor(wrapped), across, out int index, out _, out _)) return result;
        EmitterSite site = registry[index];
        result["site"] = index;
        result["kind"] = (int)site.Kind;
        result["is_main"] = site.Kind == SiteKind.Main;
        result["around"] = (double)site.Around;
        result["across"] = (double)site.Across;
        result["pad"] = (double)site.PadHeight;
        result["liner_radius"] = (double)EmitterSiteApertures.LinerRadius(site.Kind);
        result["hole_radius"] = (double)EmitterSiteApertures.HoleRadius(site.Kind);
        result["discard_radius"] = (double)EmitterSiteApertures.DiscardRadius(site.Kind);
        result["floor"] = (double)(site.PadHeight - EmitterSiteApertures.PlugDepthM); // iris rim height; not walkable since M2-3
        EmitterSitePlacement placement = _placements.GetPlacement(index);
        result["datum"] = placement.PadHeightMeters + EmitterSiteFrameMath.DefaultPadClearanceMeters;
        result["origin_around"] = placement.AroundMeters;
        result["origin_across"] = placement.AcrossMeters;
        result["yaw_rad"] = placement.YawRadians;
        return result;
    }
}
