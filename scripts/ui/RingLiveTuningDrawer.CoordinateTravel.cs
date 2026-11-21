using System;
using System.Globalization;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private const double CoordinateTravelAcrossMarginKm = 0.08;
    private SpinBox? _coordArc, _coordAcross, _coordHeight;
    private Label? _coordResult;
    private RingVoxelExplorerController? _coordExplorer;
    private EmitterTeleportPhase _coordPhase = EmitterTeleportPhase.Idle;
    private string _coordMessage = "";
    private double _coordElapsed;

    /// <summary>State of the last coordinate teleport: Idle, Holding, Arrived, TimedOut or Failed.</summary>
    public string CoordinateTeleportState => _coordPhase.ToString();
    public string CoordinateTeleportMessage => _coordMessage;

    private double CoordinateTravelCircumferenceKm() => (_grid?.GetCanonicalRingCircumferenceMeters64() ?? 31_415_936.0) / 1000d;
    private double CoordinateTravelWidthKm() => (_grid != null ? _grid.RingAcrossWidthMeters : 318_000d) / 1000d;

    private void BuildCoordinateTravelSection(VBoxContainer tabRoot)
    {
        var section = TuningMenuUi.CreateSection(tabRoot, "Travel to coordinates",
            "Teleport to the ARC and ACROSS shown on the HUD (km). You arrive in flight mode above the ground; switch flight off to land.");
        _coordArc = AddCoordinateSpin(section, "CoordTravelArc", "ARC (km)", 0d, CoordinateTravelCircumferenceKm(), 0d);
        _coordAcross = AddCoordinateSpin(section, "CoordTravelAcross", "ACROSS (km)", CoordinateTravelAcrossMarginKm, CoordinateTravelWidthKm() - CoordinateTravelAcrossMarginKm, CoordinateTravelWidthKm() * .5);
        _coordHeight = AddCoordinateSpin(section, "CoordTravelHeight", "Height above ground (m)", 0d, 5000d, 2d, 1d);
        var row = new HBoxContainer();
        var go = new Button { Name = "CoordTravelGo", Text = "Go", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        go.Pressed += () => RequestCoordinateTeleport(_coordArc!.Value, _coordAcross!.Value, _coordHeight!.Value);
        var here = new Button { Name = "CoordTravelHere", Text = "Here", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Fill ARC and ACROSS from the current position." };
        here.Pressed += FillCoordinateTravelFromPlayer;
        row.AddChild(go); row.AddChild(here);
        section.AddChild(row);
        _coordResult = new Label { Name = "CoordTravelResult", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_coordResult);
    }

    private static SpinBox AddCoordinateSpin(VBoxContainer section, string name, string label, double min, double max, double value, double step = .001)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(160, 0) });
        var spin = new SpinBox { Name = name, MinValue = min, MaxValue = max, Step = step, AllowGreater = false, AllowLesser = false, Value = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddChild(spin);
        section.AddChild(row);
        return spin;
    }

    private void FillCoordinateTravelFromPlayer()
    {
        RingVoxelExplorerController? explorer = EmitterNavExplorer();
        if (explorer == null || _coordArc == null || _coordAcross == null) { SetCoordinateResult("No player."); return; }
        RingCanonicalPosition position = explorer.CanonicalPosition;
        _coordArc.Value = Math.Round(position.AroundMeters / 1000d, 3);
        _coordAcross.Value = Math.Round(position.AcrossMeters / 1000d, 3);
        SetCoordinateResult("");
    }

    private void SetCoordinateResult(string text)
    {
        _coordMessage = text;
        if (_coordResult != null) _coordResult.Text = text;
    }

    /// <summary>Ground (or, over water, the water surface) height at a column: the committed collision height when it is finite, else the CPU sampler's float surface.</summary>
    private float CoordinateTravelGroundHeight(RingTerrain3DGameplay gameplay, double around, double across)
    {
        float height = gameplay.SampleCollisionHeight(around, across);
        var sampler = _grid?.TerrainSampler;
        if (sampler == null) return height;
        long a = (long)Math.Floor(around);
        int z = (int)Math.Floor(across);
        if (!float.IsFinite(height)) height = sampler.GetPresentationSurfaceHeight(around, across);
        TerrainSample sample = sampler.GetTerrainSample(a, z);
        if (sample.HasSurfaceWater && sample.WaterLevel > height) height = sample.WaterLevel;
        return height;
    }

    /// <summary>Test hook (works while the drawer is unbuilt): teleport in flight mode to ARC and ACROSS (km) at <paramref name="heightAboveGroundM"/> above the ground or water surface.</summary>
    public bool RequestCoordinateTeleport(double aroundKm, double acrossKm, double heightAboveGroundM)
    {
        _coordPhase = EmitterTeleportPhase.Failed;
        RingVoxelExplorerController? explorer = EmitterNavExplorer();
        _emitterGameplay ??= GetNodeOrNull<RingTerrain3DGameplay>("../Terrain3DGameplay");
        RingTerrain3DGameplay? gameplay = _emitterGameplay;
        if (explorer == null || !GodotObject.IsInstanceValid(explorer) || _grid == null) { SetCoordinateResult("Teleport unavailable: no player."); return false; }
        if (gameplay == null || !GodotObject.IsInstanceValid(gameplay) || !gameplay.IsReady) { SetCoordinateResult("Teleport unavailable: Terrain3D gameplay is not ready."); return false; }
        if (!double.IsFinite(aroundKm) || !double.IsFinite(acrossKm) || !double.IsFinite(heightAboveGroundM)) { SetCoordinateResult("Teleport target is not finite."); return false; }
        double circumferenceKm = CoordinateTravelCircumferenceKm(), widthKm = CoordinateTravelWidthKm();
        double arc = ((aroundKm % circumferenceKm) + circumferenceKm) % circumferenceKm;
        double across = Math.Clamp(acrossKm, CoordinateTravelAcrossMarginKm, widthKm - CoordinateTravelAcrossMarginKm);
        double aroundM = arc * 1000d, acrossM = across * 1000d;
        float ground = CoordinateTravelGroundHeight(gameplay, aroundM, acrossM);
        if (!float.IsFinite(ground)) ground = 0f;
        double vertical = ground + Math.Clamp(heightAboveGroundM, 0d, 5000d);
        if (!explorer.TryTeleportToCanonical(new RingCanonicalPosition(aroundM, vertical, acrossM), Mathf.RadToDeg(explorer.SavedYaw), explorer.SavedPitch, true, out string feedback))
        {
            SetCoordinateResult(feedback);
            return false;
        }
        _coordExplorer = explorer;
        _coordElapsed = 0d;
        _coordPhase = EmitterTeleportPhase.Holding;
        SetCoordinateResult(string.Create(CultureInfo.InvariantCulture, $"ARC {arc:0.000} km, ACROSS {across:0.000} km: moving, waiting for terrain."));
        return true;
    }

    private void ProcessCoordinateTravel(double delta)
    {
        if (_coordPhase != EmitterTeleportPhase.Holding) return;
        if (_coordExplorer == null || !GodotObject.IsInstanceValid(_coordExplorer))
        {
            _coordPhase = EmitterTeleportPhase.Failed;
            SetCoordinateResult("Teleport interrupted: player was removed.");
            ReleaseCoordinateFocus();
            return;
        }
        _coordElapsed += Math.Max(0d, delta);
        if (!_coordExplorer.IsSpawnTerrainSettling)
        {
            _coordPhase = EmitterTeleportPhase.Arrived;
            SetCoordinateResult(string.Create(CultureInfo.InvariantCulture, $"Arrived after {_coordElapsed:0.0} s."));
            ReleaseCoordinateFocus();
        }
        else if (_coordElapsed >= EmitterSiteTeleport.TimeoutSeconds)
        {
            _coordPhase = EmitterTeleportPhase.TimedOut;
            SetCoordinateResult(string.Create(CultureInfo.InvariantCulture, $"Timed out after {EmitterSiteTeleport.TimeoutSeconds:0} s (terrain still settling)."));
            ReleaseCoordinateFocus();
        }
    }

    private void ReleaseCoordinateFocus()
    {
        if (_grid != null && GodotObject.IsInstanceValid(_grid)) _grid.ClearStreamingCanonicalFocusOverride();
    }
}
