using System.Collections.Generic;
using Godot;
using Godot.Collections;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Save;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Loading;

/// <summary>Typed gameplay readiness and input ownership for the persistent loading UI.</summary>
public partial class RingWorldLoadingBridge : Node
{
    private Node? _world;
    private RingVoxelExplorerController? _explorer;
    private RingWorldSaveManager? _saves;
    private RingTerrain3DGameplay? _surface;
    private RingScaledFarViewport? _far;
    private readonly System.Collections.Generic.Dictionary<Node, ProcessModeEnum> _held = new();
    private readonly List<Node> _structureOwners = new();
    private Godot.Collections.Dictionary _spawn = new() { ["ready"] = false };
    private bool _released;
    private readonly List<RingTerrainProxySector> _proxies = new();
    private readonly System.Collections.Generic.Dictionary<SubViewport, SubViewport.UpdateMode> _renderHolds = new();

    public void PrepareWorld(Node world)
    {
        ProcessPriority = 10000;
        _world = world;
        _explorer = world.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        _saves = world.GetNodeOrNull<RingWorldSaveManager>("WorldSaveManager");
        // Direct scene/probe launches historically run without a persistent slot.
        if (string.IsNullOrWhiteSpace(ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString()))
            _saves = null;
        _surface = world.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager")?.UseTerrain3DSurface == true
            ? world.GetNodeOrNull<RingTerrain3DGameplay>("Terrain3DGameplay") : null;
        _far = world.GetNodeOrNull<RingScaledFarViewport>("ScaledFarViewport");
        if (_far?.ProcessMode == ProcessModeEnum.Disabled) _far = null;
        RingLiveTuningDrawer? drawer = world.GetNodeOrNull<RingLiveTuningDrawer>("LiveTuningDrawer");
        if (drawer != null) drawer.DeferDrawerBuildUntilOpened = true;
        if (_saves != null)
        {
            _saves.UseStagedStartup = true;
            _saves.HoldSavesUntilWorldReady = true;
        }
        if (_surface != null)
        {
            _surface.UseStagedStartup = true;
            _surface.WaitForSavedState = _saves != null;
        }
        // These owners still receive _Ready and can be discovered by their peers.
        // Only player interaction is held; terrain/render workers keep processing.
        foreach (string path in new[] { "Explorer", "GridEditController", "LiveTuningDrawer", "LodPauseMenu" })
        {
            Node? owner = world.GetNodeOrNull<Node>(path);
            if (owner == null) continue;
            _held[owner] = owner.ProcessMode;
            owner.ProcessMode = ProcessModeEnum.Disabled;
        }
        foreach (Node child in world.GetChildren())
        {
            if (child is RingTerrainProxySector proxy) _proxies.Add(proxy);
            if (child.ProcessMode == ProcessModeEnum.Disabled) continue;
            // Read-only C# properties are callable, but do not appear in the
            // exported property list. Keep readiness ownership explicit.
            if (child is RingScaledFarViewport or RingTerrainGlobalLod or RingAnalyticGlobalLod or RingCloudLayer or RingBiomeDecorationLayer or EmitterPoiStreamer)
                _structureOwners.Add(child);
            foreach (Godot.Collections.Dictionary property in child.GetPropertyList())
            {
                if (property["name"].AsString() == "UseStagedStartup")
                {
                    child.Set("UseStagedStartup", true);
                }
            }
        }
    }

    public void ConfirmWorldAttached()
    {
        // Some UI owners choose Always in _Ready. Record that intended mode,
        // then hold their input until loading releases ownership.
        foreach (Node owner in new List<Node>(_held.Keys))
        {
            if (owner.ProcessMode != ProcessModeEnum.Disabled) _held[owner] = owner.ProcessMode;
            owner.ProcessMode = ProcessModeEnum.Disabled;
        }
        if (_world != null)
            foreach (Node child in _world.FindChildren("*", "SubViewport", true, false))
                if (child is SubViewport viewport)
                {
                    _renderHolds[viewport] = viewport.RenderTargetUpdateMode;
                    viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                }
    }

    /// <summary>Prepare one rendered world frame while player input remains held.</summary>
    public void PrepareReveal()
    {
        foreach (var pair in _renderHolds)
            if (GodotObject.IsInstanceValid(pair.Key)) pair.Key.RenderTargetUpdateMode = pair.Value;
        _renderHolds.Clear();
    }

    public override void _Process(double delta)
    {
        // Minimap and cloud-mask producers may request an update later in a
        // frame. Keep their viewports idle behind the opaque loading screen.
        foreach (SubViewport viewport in _renderHolds.Keys)
            if (GodotObject.IsInstanceValid(viewport)) viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_released || _explorer == null || !GodotObject.IsInstanceValid(_explorer) || !_explorer.IsInsideTree()) return;
        if (_saves != null && !_saves.IsWorldLoadComplete) return;
        if (_surface != null && !_surface.IsReady) return;
        if (_spawn.TryGetValue("ready", out Variant ready) && ready.AsBool()) return;
        _spawn = _explorer.AdvanceWorldLoadingSpawn(delta);
    }

    public Godot.Collections.Dictionary CaptureReadiness()
    {
        if (_world == null || !GodotObject.IsInstanceValid(_world) || !_world.IsInsideTree())
            return new() { ["ready"] = false, ["stage"] = "preparing", ["detail"] = "Preparing your world.", ["progress"] = .32f };
        if (_saves?.IsWorldLoadFailed == true)
            return new() { ["ready"] = false, ["error"] = _saves.WorldLoadError };
        if (_surface?.InitializationFailed == true)
            return new() { ["ready"] = false, ["error"] = _surface.LastError };

        bool structureReady = true;
        int structuresReady = 0;
        string surroundingsDetail = "Bringing the distant ring into view.";
        foreach (Node child in _structureOwners)
        {
            if (!GodotObject.IsInstanceValid(child)) continue;
            bool ready = child switch
            {
                RingScaledFarViewport far => far.StartupComplete,
                RingTerrainGlobalLod terrain => terrain.StartupComplete,
                RingAnalyticGlobalLod analytic => analytic.StartupComplete,
                RingCloudLayer clouds => clouds.StartupComplete,
                RingBiomeDecorationLayer vegetation => vegetation.StartupComplete,
                EmitterPoiStreamer emitters => emitters.StartupComplete,
                _ => true
            };
            structureReady &= ready;
            if (ready) structuresReady++;
            if (child.HasMethod("GetLoadingStatus"))
            {
                Godot.Collections.Dictionary status = child.Call("GetLoadingStatus").AsGodotDictionary();
                if (status.TryGetValue("error", out Variant error) && !string.IsNullOrEmpty(error.AsString()))
                    return new() { ["ready"] = false, ["error"] = error.AsString() };
            }
            if (!ready && child is RingBiomeDecorationLayer)
                surroundingsDetail = "Preparing trees around your arrival point.";
            else if (!ready && child is EmitterPoiStreamer)
                surroundingsDetail = "Preparing the emitter spires.";
        }

        bool saveReady = _saves == null || _saves.IsWorldLoadComplete;
        bool surfaceReady = _surface == null || _surface.IsReady;
        if (!saveReady || !surfaceReady)
        {
            Godot.Collections.Dictionary status = _surface?.InitializationPending == true
                ? _surface.GetLoadingStatus() : _saves?.GetLoadingStatus() ?? new();
            float fraction = status.TryGetValue("progress", out Variant progress) ? Mathf.Clamp(progress.AsSingle(), 0, 1) : 0;
            return new() { ["ready"] = false, ["stage"] = "terrain", ["detail"] = status.TryGetValue("detail", out Variant detail) ? detail : "Restoring terrain.", ["progress"] = .35f + fraction * .35f };
        }

        // A complete coarse far surface is enough to enter. Texture residency
        // and later adaptive refinements continue towards the user's settings.
        bool farReady = _far == null || !_far.Enabled || _far.ActiveGlobalTerrainSectorCount > 0;
        if (!farReady || !structureReady)
            return new() { ["ready"] = false, ["stage"] = "surroundings", ["detail"] = surroundingsDetail,
                ["progress"] = .72f + .16f * structuresReady / System.Math.Max(1, _structureOwners.Count), ["spawn"] = _spawn };
        bool spawnReady = _spawn.TryGetValue("ready", out Variant spawn) && spawn.AsBool();
        return new()
        {
            ["ready"] = spawnReady, ["stage"] = spawnReady ? "ready" : "position",
            ["detail"] = spawnReady ? "Welcome back to the ring." : "Preparing solid ground around your location.",
            ["progress"] = spawnReady ? 1f : .9f, ["spawn"] = _spawn,
            ["save_ready"] = saveReady, ["surface_ready"] = surfaceReady, ["far_ready"] = farReady
        };
    }

    public void ReleaseWorld()
    {
        if (_released) return;
        _released = true;
        if (_saves != null) _saves.HoldSavesUntilWorldReady = false;
        foreach (var pair in _held)
            if (GodotObject.IsInstanceValid(pair.Key)) pair.Key.ProcessMode = pair.Value;
        _held.Clear();
    }

    public Godot.Collections.Dictionary GetStartupDiagnostics()
    {
        var diagnostics = new Godot.Collections.Dictionary();
        foreach (Node child in _structureOwners)
            if (GodotObject.IsInstanceValid(child) && child.HasMethod("GetLoadingStatus"))
                diagnostics[child.Name.ToString()] = child.Call("GetLoadingStatus");
        return diagnostics;
    }

    public void CancelWorld()
    {
        _released = true;
        if (_saves != null && GodotObject.IsInstanceValid(_saves)) _saves.CancelWorldLoad();
        if (_surface != null && GodotObject.IsInstanceValid(_surface)) _surface.CancelStartup("World loading cancelled.");
        foreach (RingTerrainProxySector proxy in _proxies)
            if (GodotObject.IsInstanceValid(proxy)) proxy.BeginLoadingShutdown();
        _world?.GetNodeOrNull<Node>("TerrainLodCoordinator")?.SetProcess(false);
    }

    public bool IsShutdownReady()
    {
        bool ready = _surface == null || !GodotObject.IsInstanceValid(_surface) || _surface.StartupCancellationReady;
        foreach (RingTerrainProxySector proxy in _proxies)
            if (GodotObject.IsInstanceValid(proxy)) ready &= proxy.LoadingShutdownReady;
        return ready;
    }
}
