using System.Collections.Generic;
using Godot;
using Godot.Collections;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
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
    // Background (warm world) mode: the world loads behind the start menu and is never released to the player.
    private bool _background;
    private bool _placed;
    private const double AltitudeRefreshIntervalSeconds = 0.5;
    private double _altitudeRefreshSeconds;
    // Staged activation: world children are disabled at attach and enabled one by one on a per-frame budget.
    private const double ActivationBudgetMilliseconds = 8.0;
    private readonly List<Node> _activationList = new();
    private readonly System.Collections.Generic.Dictionary<Node, ProcessModeEnum> _activationHolds = new();
    private readonly List<(string Name, double Ms)> _activationTimings = new();
    private int _activationIndex;
    private ulong _activationAttachFrame;
    private bool _activationStarted;
    private bool _activationCancelled;
    private double _activationCurrentMs;
    private double _activationMaxStepMs;
    private double _activationMaxFrameMs;
    private const double ActivationCheapFrameMilliseconds = 2.0;
    private const int ActivationCheapFrameChildren = 4;
    private ulong _activationFrames;
    private float _cruiseAltitudeMeters = 16000f;
    private float _cruisePitchDegrees = -25f;
    private bool _clocksPaused;
    private Godot.Collections.Dictionary? _placementPose;
    // A menu snapshot pose is only used when it is within the loiter radius of the save and in a sane altitude band.
    private const double SnapshotMaxHorizontalMeters = 30000.0;
    private const double SnapshotMinAltitudeMeters = 2000.0;
    private const double SnapshotMaxAltitudeMeters = 18000.0;

    /// <summary>True once every attached world child has been re-enabled and finished its deferred ready steps.</summary>
    public bool ActivationComplete { get; private set; }

    /// <summary>True when the last placement used the snapshot pose passed to <see cref="SetPlacementPose"/> instead of the default cruise.</summary>
    public bool PlacementUsedSnapshot { get; private set; }

    /// <summary>True once the explorer was moved to the cruise viewpoint above the saved position (background mode).</summary>
    public bool CruisePlaced => _placed;

    /// <summary>Marks this bridge as the holder of a warm world. Call before <see cref="PrepareWorld"/>.</summary>
    public void EnableBackgroundMode(float cruiseAltitudeMeters, float cruisePitchDegrees)
    {
        _background = true;
        // The menu camera never rises above the snapshot band ceiling, whatever the tuning says.
        _cruiseAltitudeMeters = Mathf.Clamp(cruiseAltitudeMeters, 100f, (float)SnapshotMaxAltitudeMeters);
        _cruisePitchDegrees = cruisePitchDegrees;
    }

    /// <summary>
    /// Pose of a valid, non-stale menu snapshot: {around_m, vertical_m, across_m, yaw_deg, pitch_deg}. Used by the cruise placement when it
    /// is plausible; otherwise the default cruise above the save spot applies. Call before <see cref="PrepareWorld"/>.
    /// </summary>
    public void SetPlacementPose(Godot.Collections.Dictionary pose)
        => _placementPose = pose != null && pose.Count > 0 ? pose.Duplicate() : null;

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
        // A warm world sits behind the start menu: its HUD layers must never draw over the menu, not even during the load.
        if (_background) HideWorldHud();
        BeginActivation();
    }

    private void BeginActivation()
    {
        _activationAttachFrame = Engine.GetProcessFrames();
        _activationStarted = true;
        _activationList.Clear();
        _activationHolds.Clear();
        if (_world == null) { ActivationComplete = true; return; }
        foreach (Node child in _world.GetChildren())
        {
            if (_held.ContainsKey(child) || child.ProcessMode == ProcessModeEnum.Disabled) continue;
            // Audio is out of scope: disabling a node pauses its streams.
            if (child is AudioStreamPlayer or AudioStreamPlayer2D or AudioStreamPlayer3D || HasAudioPlayerChild(child)) continue;
            _activationList.Add(child);
            _activationHolds[child] = child.ProcessMode;
            child.ProcessMode = ProcessModeEnum.Disabled;
        }
        ActivationComplete = _activationList.Count == 0;
    }

    private static bool HasAudioPlayerChild(Node node)
    {
        foreach (Node child in node.GetChildren())
            if (child is AudioStreamPlayer or AudioStreamPlayer2D or AudioStreamPlayer3D) return true;
        return false;
    }

    private void AdvanceActivation()
    {
        if (!_activationStarted || ActivationComplete || _activationCancelled) return;
        ulong frame = Engine.GetProcessFrames();
        if (frame <= _activationAttachFrame + 1) return;
        _activationFrames++;
        // A re-enabled child pays its first _Process in the next frame, which this node cannot time directly. Use the
        // previous frame's measured process time: cheap frames let a few more children in, expensive ones admit one.
        double previousProcessMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
        _activationMaxFrameMs = System.Math.Max(_activationMaxFrameMs, previousProcessMs);
        int allowed = previousProcessMs < ActivationCheapFrameMilliseconds ? ActivationCheapFrameChildren : 1;
        int enabled = 0;
        var budget = System.Diagnostics.Stopwatch.StartNew();
        while (_activationIndex < _activationList.Count)
        {
            Node child = _activationList[_activationIndex];
            if (!GodotObject.IsInstanceValid(child))
            {
                _activationIndex++;
                continue;
            }
            if (child is IRingDeferredReady deferred && deferred.DeferredReadyPending)
            {
                long stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                deferred.AdvanceDeferredReady();
                double stepMs = System.Diagnostics.Stopwatch.GetElapsedTime(stepStart).TotalMilliseconds;
                _activationCurrentMs += stepMs;
                _activationMaxStepMs = System.Math.Max(_activationMaxStepMs, stepMs);
                if (deferred.DeferredReadyPending)
                {
                    if (budget.Elapsed.TotalMilliseconds >= ActivationBudgetMilliseconds) return;
                    continue;
                }
            }
            long enableStart = System.Diagnostics.Stopwatch.GetTimestamp();
            child.ProcessMode = _activationHolds[child];
            _activationCurrentMs += System.Diagnostics.Stopwatch.GetElapsedTime(enableStart).TotalMilliseconds;
            _activationTimings.Add((child.Name.ToString(), _activationCurrentMs));
            _activationCurrentMs = 0.0;
            _activationIndex++;
            enabled++;
            if ((enabled >= allowed || budget.Elapsed.TotalMilliseconds >= ActivationBudgetMilliseconds) && _activationIndex < _activationList.Count) return;
        }
        ActivationComplete = true;
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
        AdvanceActivation();
        // Minimap and cloud-mask producers may request an update later in a
        // frame. Keep their viewports idle behind the opaque loading screen.
        foreach (SubViewport viewport in _renderHolds.Keys)
            if (GodotObject.IsInstanceValid(viewport)) viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        // HUD owners may show their layer again from their own process; the menu owns the screen until the world is released.
        if (_background && !_released && _world != null && CountVisibleHudLayers() > 0) ForceHudHidden();
        // The held explorer does not tick its own altitude; keep it fresh so altitude-gated streaming (trees, grass) sees the cruise height.
        if (_background && _placed && !_released && _explorer != null && GodotObject.IsInstanceValid(_explorer))
        {
            _altitudeRefreshSeconds += delta;
            if (_altitudeRefreshSeconds >= AltitudeRefreshIntervalSeconds)
            {
                _altitudeRefreshSeconds = 0.0;
                _explorer.RefreshAltitudeAboveTerrain();
            }
        }
    }

    private void ForceHudHidden()
    {
        if (_world == null) return;
        foreach (string path in new[] { "PresentationHud", "TerrainMinimap", "LodDebugHud", "CelestialDebugHud", "LiveTuningDrawer" })
            if (_world.GetNodeOrNull<CanvasLayer>(path) is { Visible: true } layer) layer.Visible = false;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_released || _explorer == null || !GodotObject.IsInstanceValid(_explorer) || !_explorer.IsInsideTree()) return;
        if (_saves != null && !_saves.IsWorldLoadComplete) return;
        if (_surface != null && !_surface.IsReady) return;
        // The terrain stage is complete here. Move to the cruise viewpoint once, before the surroundings stage finishes, so the far and
        // proxy owners complete for the viewpoint the player sees instead of building the ground view first.
        if (_background && !_placed) PlaceAtCruise();
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

        if (!ActivationComplete)
            return new() { ["ready"] = false, ["stage"] = "terrain", ["detail"] = "Assembling world systems.", ["progress"] = .33f };

        bool structureReady = true;
        int structuresReady = 0;
        int structuresCounted = 0;
        string surroundingsDetail = "Bringing the distant ring into view.";
        foreach (Node child in _structureOwners)
        {
            if (!GodotObject.IsInstanceValid(child)) continue;
            // Trees are hidden above 12 km, so a warm world does not wait for them. Their startup still runs.
            if (_background && child is RingBiomeDecorationLayer) continue;
            structuresCounted++;
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
                ["progress"] = .72f + .16f * structuresReady / System.Math.Max(1, structuresCounted), ["spawn"] = _spawn };
        if (_background)
            return new() { ["ready"] = _placed, ["stage"] = _placed ? "ready" : "position", ["detail"] = "Your world is ready.",
                ["progress"] = _placed ? 1f : .9f, ["save_ready"] = saveReady, ["surface_ready"] = surfaceReady, ["far_ready"] = farReady };
        bool spawnReady = _spawn.TryGetValue("ready", out Variant spawn) && spawn.AsBool();
        return new()
        {
            ["ready"] = spawnReady, ["stage"] = spawnReady ? "ready" : "position",
            ["detail"] = spawnReady ? "Welcome back to the ring." : "Preparing solid ground around your location.",
            ["progress"] = spawnReady ? 1f : .9f, ["spawn"] = _spawn,
            ["save_ready"] = saveReady, ["surface_ready"] = surfaceReady, ["far_ready"] = farReady
        };
    }

    private void PlaceAtCruise()
    {
        if (_explorer == null || _surface == null) return;
        RingCanonicalPosition saved = _explorer.CanonicalPosition;
        string feedback;
        if (TryGetSnapshotPlacement(saved, out RingCanonicalPosition snapshotTarget, out float snapshotYaw, out float snapshotPitch)
            && _explorer.TryTeleportToCanonical(snapshotTarget, snapshotYaw, snapshotPitch, true, out feedback))
        {
            PlacementUsedSnapshot = true;
        }
        else
        {
            float ground = _surface.SampleHeight(saved.AroundMeters, saved.AcrossMeters);
            double groundVertical = float.IsFinite(ground) ? ground : saved.VerticalMeters;
            var target = new RingCanonicalPosition(saved.AroundMeters, groundVertical + _cruiseAltitudeMeters, saved.AcrossMeters);
            float yawDeg = Mathf.RadToDeg(_explorer.SavedYaw);
            if (!_explorer.TryTeleportToCanonical(target, yawDeg, _cruisePitchDegrees, true, out feedback))
            {
                GD.PushWarning("Warm world cruise placement failed: " + feedback);
                return;
            }
        }
        // The teleport pins the streaming focus on the destination; the normal chase takes over again from here.
        _world?.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager")?.ClearStreamingCanonicalFocusOverride();
        PauseClocks(true);
        _placed = true;
        _explorer.RefreshAltitudeAboveTerrain();
        _altitudeRefreshSeconds = 0.0;
    }

    private bool TryGetSnapshotPlacement(in RingCanonicalPosition saved, out RingCanonicalPosition target, out float yawDeg, out float pitchDeg)
    {
        target = saved;
        yawDeg = 0f;
        pitchDeg = 0f;
        if (_placementPose == null || _surface == null) return false;
        if (!TryReadPoseNumber("around_m", out double around) || !TryReadPoseNumber("vertical_m", out double vertical)
            || !TryReadPoseNumber("across_m", out double across) || !TryReadPoseNumber("yaw_deg", out double yaw)
            || !TryReadPoseNumber("pitch_deg", out double pitch))
            return false;
        double circumference = _world?.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager")?.GetCanonicalRingCircumferenceMeters64() ?? 0.0;
        double deltaAround = around - saved.AroundMeters;
        if (circumference > 0.0) deltaAround -= System.Math.Round(deltaAround / circumference) * circumference;
        double horizontal = System.Math.Sqrt(deltaAround * deltaAround + (across - saved.AcrossMeters) * (across - saved.AcrossMeters));
        if (horizontal > SnapshotMaxHorizontalMeters) return false;
        float ground = _surface.SampleHeight(around, across);
        if (!float.IsFinite(ground)) return false;
        double altitude = vertical - ground;
        if (altitude < SnapshotMinAltitudeMeters || altitude > SnapshotMaxAltitudeMeters) return false;
        target = new RingCanonicalPosition(around, vertical, across);
        yawDeg = (float)yaw;
        pitchDeg = (float)pitch;
        return true;
    }

    private bool TryReadPoseNumber(string key, out double value)
    {
        value = 0.0;
        if (_placementPose == null || !_placementPose.TryGetValue(key, out Variant raw)) return false;
        if (raw.VariantType != Variant.Type.Float && raw.VariantType != Variant.Type.Int) return false;
        value = raw.AsDouble();
        return double.IsFinite(value);
    }

    /// <summary>Explorer pose as the menu camera sees it: canonical position, yaw and pitch in degrees, and the live camera field of view.</summary>
    public Godot.Collections.Dictionary GetCameraPose()
    {
        var result = new Godot.Collections.Dictionary { ["valid"] = false };
        if (_explorer == null || !GodotObject.IsInstanceValid(_explorer)) return result;
        RingCanonicalPosition position = _explorer.CanonicalPosition;
        Camera3D? camera = _explorer.GetNodeOrNull<Camera3D>(_explorer.CameraPath);
        result["valid"] = true;
        result["around_m"] = position.AroundMeters;
        result["vertical_m"] = position.VerticalMeters;
        result["across_m"] = position.AcrossMeters;
        result["yaw_deg"] = Mathf.RadToDeg(_explorer.SavedYaw);
        result["pitch_deg"] = _explorer.SavedPitch;
        result["fov_deg"] = camera != null ? camera.Fov : 75f;
        return result;
    }

    /// <summary>
    /// Saved player pose of the held world ({valid, around_m, vertical_m, across_m, yaw_deg, pitch_deg}): the staged save document when there
    /// is one, otherwise the explorer's current pose. The menu loiters around it.
    /// </summary>
    public Godot.Collections.Dictionary GetHomePose()
    {
        var result = new Godot.Collections.Dictionary { ["valid"] = false };
        if (_saves != null && GodotObject.IsInstanceValid(_saves)
            && _saves.TryGetStagedPlayerPose(out RingCanonicalPosition staged, out float yaw, out float pitch))
        {
            result["valid"] = true;
            result["around_m"] = staged.AroundMeters;
            result["vertical_m"] = staged.VerticalMeters;
            result["across_m"] = staged.AcrossMeters;
            result["yaw_deg"] = Mathf.RadToDeg(yaw);
            result["pitch_deg"] = pitch;
            return result;
        }
        return GetCameraPose();
    }

    /// <summary>Holds or resumes every save of the held world (a menu flight must never write).</summary>
    public void HoldSaves(bool hold)
    {
        if (_saves != null && GodotObject.IsInstanceValid(_saves)) _saves.HoldSaves(hold);
    }

    /// <summary>
    /// Lets the held explorer run its physics step under a flight director: the node is un-held but the body ignores all input. Returns it
    /// (null when unavailable). Saves stay held.
    /// </summary>
    public RingVoxelExplorerController? BeginScriptedFlight()
    {
        if (_explorer == null || !GodotObject.IsInstanceValid(_explorer)) return null;
        HoldSaves(true);
        if (_held.Remove(_explorer, out ProcessModeEnum mode)) _explorer.ProcessMode = mode;
        return _explorer.BeginScriptedFlight() ? _explorer : null;
    }

    /// <summary>Returns the explorer to the held state used while a world is parked behind the menu.</summary>
    public void EndScriptedFlight()
    {
        if (_explorer == null || !GodotObject.IsInstanceValid(_explorer)) return;
        _explorer.EndScriptedFlight();
        if (!_held.ContainsKey(_explorer))
        {
            _held[_explorer] = _explorer.ProcessMode;
            _explorer.ProcessMode = ProcessModeEnum.Disabled;
        }
    }

    /// <summary>Freezes the celestial and cloud clocks (the menu shows the saved time of day). Nothing is saved while the world is held.</summary>
    public void PauseClocks(bool paused)
    {
        if (_world == null || !GodotObject.IsInstanceValid(_world)) return;
        _clocksPaused = paused;
        if (_world.GetNodeOrNull<RingDayNightCycle>("RingDayNightCycle") is { } cycle) cycle.Paused = paused;
        if (_world.GetNodeOrNull<RingVolumetricCloudLayer>("RingVolumetricClouds") is { } clouds) clouds.CloudSimulationPaused = paused;
    }

    /// <summary>True while <see cref="ApplyMenuSuspension"/> is in effect.</summary>
    public bool MenuSuspensionActive { get; private set; }

    /// <summary>
    /// What the held world stops doing while the camera loiters kilometres up behind the menu: the water simulation (presentation suspend group),
    /// and both terrain streamers stop chasing the camera and stream the saved spot instead (so Continue lands on resident ground). Shadows, grass
    /// and trees follow their own altitude rules; audio is deliberately left alone. Undone by <see cref="ReleaseMenuSuspension"/>.
    /// </summary>
    public void ApplyMenuSuspension()
    {
        if (_world == null || !GodotObject.IsInstanceValid(_world) || _explorer == null || !GodotObject.IsInstanceValid(_explorer)) return;
        Godot.Collections.Dictionary home = GetHomePose();
        bool haveHome = home.Count > 0 && home.TryGetValue("valid", out Variant valid) && valid.AsBool();
        if (haveHome)
        {
            var spot = new RingCanonicalPosition(home["around_m"].AsDouble(), home["vertical_m"].AsDouble(), home["across_m"].AsDouble());
            _explorer.SetScriptedStreamingHold(spot);
            _surface?.SetCoverageFocusOverride(spot);
        }
        if (_world.GetNodeOrNull<RingworldTechDemo.Underground.RingUndergroundModeController>("UndergroundMode") is { } underground)
            underground.PresentationSuspendGroups |= RingworldTechDemo.Underground.UndergroundGroups.Water;
        MenuSuspensionActive = true;
    }

    /// <summary>Clears everything <see cref="ApplyMenuSuspension"/> set; safe to call when nothing was applied or the world is already gone.</summary>
    public void ReleaseMenuSuspension()
    {
        if (!MenuSuspensionActive) return;
        MenuSuspensionActive = false;
        if (_world == null || !GodotObject.IsInstanceValid(_world)) return;
        if (_explorer != null && GodotObject.IsInstanceValid(_explorer)) _explorer.SetScriptedStreamingHold(null);
        if (_surface != null && GodotObject.IsInstanceValid(_surface)) _surface.ClearCoverageFocusOverride();
        if (_world.GetNodeOrNull<RingworldTechDemo.Underground.RingUndergroundModeController>("UndergroundMode") is { } underground)
            underground.PresentationSuspendGroups &= ~RingworldTechDemo.Underground.UndergroundGroups.Water;
    }

    /// <summary>Hides the world HUD layers so they do not draw over the start menu.</summary>
    public void HideWorldHud() => _world?.GetNodeOrNull<RingLodPauseMenu>("LodPauseMenu")?.SuppressGameplayHud();

    /// <summary>
    /// Starts rendering the world behind an opaque cover: releases the viewport holds (the minimap stays off, its HUD is hidden) and
    /// re-enables 3D on the root viewport. Player input stays held.
    /// </summary>
    public void RevealBehindMenu()
    {
        PrepareReveal();
        if (_world != null)
            foreach (Node viewport in _world.FindChildren("*", "SubViewport", true, false))
                if (viewport is SubViewport sub && sub.GetParent() is RingTerrainMinimap)
                    sub.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        GetTree().Root.Disable3D = false;
    }

    /// <summary>Altitude, speed and sun elevation for the menu's survey readout.</summary>
    public Godot.Collections.Dictionary GetExplorerTelemetry()
    {
        var result = new Godot.Collections.Dictionary { ["valid"] = false };
        if (_explorer == null || !GodotObject.IsInstanceValid(_explorer)) return result;
        result["valid"] = true;
        // The cached altitude is only refreshed by the (held) explorer physics step, so compute it.
        result["altitude_m"] = _explorer.GetCanonicalAtmosphereHeightMeters();
        result["speed_mps"] = _explorer.ActualTravelSpeedMetersPerSecond;
        result["sun_elevation_degrees"] = _world?.GetNodeOrNull<RingDayNightCycle>("RingDayNightCycle")?.SunElevationDegrees ?? 0f;
        result["clocks_paused"] = _clocksPaused;
        return result;
    }

    /// <summary>Counts world HUD layers that are still visible (a warm world must show none).</summary>
    public int CountVisibleHudLayers()
    {
        int visible = 0;
        if (_world == null) return 0;
        foreach (string path in new[] { "PresentationHud", "TerrainMinimap", "LodDebugHud", "CelestialDebugHud", "LiveTuningDrawer" })
            if (_world.GetNodeOrNull<CanvasLayer>(path) is { Visible: true }) visible++;
        return visible;
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
        var perChild = new Godot.Collections.Dictionary();
        foreach ((string name, double ms) in _activationTimings) perChild[name] = ms;
        diagnostics["activation"] = new Godot.Collections.Dictionary
        {
            ["per_child_ms"] = perChild, ["max_step_ms"] = _activationMaxStepMs, ["max_frame_process_ms"] = _activationMaxFrameMs,
            ["frames"] = (long)_activationFrames, ["complete"] = ActivationComplete
        };
        return diagnostics;
    }

    public void CancelWorld()
    {
        _released = true;
        // Pending activation steps never run and held children stay disabled; the world is freed by the caller.
        _activationCancelled = true;
        _activationList.Clear();
        _activationHolds.Clear();
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
