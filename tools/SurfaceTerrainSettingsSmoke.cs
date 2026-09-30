using System;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;

/// <summary>Isolated persistence evidence for Certified Terrain3D surface preferences.</summary>
public partial class SurfaceTerrainSettingsSmoke : Node
{
    public string Result { get; private set; } = "RUNNING";
    public string IsolatedPath { get; set; } = "res://.godot/surface_terrain_settings_smoke.tres";
    public override void _Ready()
    {
        try { Run(IsolatedPath); Result = "PASS"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
    }

    private static void Run(string path)
    {
        var first = new GameplaySurfaceTerrainDefaults(path); first.Initialize();
        if (!Mathf.IsEqualApprox(first.Settings.BrushRadiusMeters, 8) || !Mathf.IsEqualApprox(first.Settings.BrushStepMeters, 2) || first.Settings.MeshLods != 4 || first.Settings.MeshSize != 48 || !Mathf.IsEqualApprox(first.Settings.HandoffDistanceMeters, 112) || !Mathf.IsEqualApprox(first.Settings.MoveSpeedMetersPerSecond, 12) || !Mathf.IsEqualApprox(first.Settings.DryRoughness, .92f) || !Mathf.IsEqualApprox(first.Settings.WetRoughness, .35f) || !Mathf.IsEqualApprox(first.Settings.RainWetnessStrength, 1) || !first.Settings.SmoothTerrainShading) throw new InvalidOperationException("missing surface preferences did not use defaults");
        first.SetBrushRadiusMeters(24); first.SetBrushStepMeters(.5f); first.SetMeshLods(5); first.SetMeshSize(24); first.SetHandoffDistanceMeters(128); first.SetMoveSpeedMetersPerSecond(30); first.SetDryRoughness(.8f); first.SetWetRoughness(.28f); first.SetRainWetnessStrength(.6f); first.SetSmoothTerrainShading(false);
        if (!first.Save()) throw new InvalidOperationException("surface preferences did not save");
        var second = new GameplaySurfaceTerrainDefaults(path); second.Initialize();
        if (!Mathf.IsEqualApprox(second.Settings.BrushRadiusMeters, 24) || !Mathf.IsEqualApprox(second.Settings.BrushStepMeters, .5f) || second.Settings.MeshLods != 5 || second.Settings.MeshSize != 24 || !Mathf.IsEqualApprox(second.Settings.HandoffDistanceMeters, 128) || !Mathf.IsEqualApprox(second.Settings.MoveSpeedMetersPerSecond, 30) || !Mathf.IsEqualApprox(second.Settings.DryRoughness, .8f) || !Mathf.IsEqualApprox(second.Settings.WetRoughness, .28f) || !Mathf.IsEqualApprox(second.Settings.RainWetnessStrength, .6f) || second.Settings.SmoothTerrainShading) throw new InvalidOperationException("surface preferences did not round-trip");
        if (!Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampBrushRadiusMeters(-1), 2) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampBrushStepMeters(99), 8) || GameplaySurfaceTerrainDefaults.ClampMeshLods(99) != 6 || GameplaySurfaceTerrainDefaults.ClampMeshSize(21) != 24 || GameplaySurfaceTerrainDefaults.ClampMeshSize(99) != 64 || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampHandoffDistanceMeters(-1), 84) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampHandoffDistanceMeters(999), 128) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampMoveSpeedMetersPerSecond(float.NaN), 12) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampDryRoughness(-1), GameplaySurfaceTerrainDefaults.MinimumDryRoughness) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampWetRoughness(99), GameplaySurfaceTerrainDefaults.MaximumWetRoughness) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampRainWetnessStrength(float.NaN), 1)) throw new InvalidOperationException("surface preference bounds were not enforced");
        second.Reset(); if (!second.Save()) throw new InvalidOperationException("surface preference reset did not save");
        var reset = new GameplaySurfaceTerrainDefaults(path); reset.Initialize();
        if (!Mathf.IsEqualApprox(reset.Settings.BrushRadiusMeters, 8) || !Mathf.IsEqualApprox(reset.Settings.BrushStepMeters, 2) || reset.Settings.MeshLods != 4 || reset.Settings.MeshSize != 48 || !Mathf.IsEqualApprox(reset.Settings.HandoffDistanceMeters, 112) || !Mathf.IsEqualApprox(reset.Settings.MoveSpeedMetersPerSecond, 12) || !Mathf.IsEqualApprox(reset.Settings.DryRoughness, .92f) || !Mathf.IsEqualApprox(reset.Settings.WetRoughness, .35f) || !Mathf.IsEqualApprox(reset.Settings.RainWetnessStrength, 1) || !reset.Settings.SmoothTerrainShading) throw new InvalidOperationException("surface preference reset did not restore defaults");
        string invalidParent = path + ".invalid-parent.tres";
        if (ResourceSaver.Save(new GameplaySurfaceTerrainSettings(), invalidParent) != Error.Ok) throw new InvalidOperationException("invalid-path fixture write failed");
        var invalid = new GameplaySurfaceTerrainDefaults(invalidParent + "/child.tres"); invalid.Initialize(); invalid.SetBrushRadiusMeters(9);
        if (invalid.Save() || invalid.LastError.Length == 0) throw new InvalidOperationException("invalid surface preference path did not fail safely");
        if (!RingLiveTuningDrawer.IsCertifiedSurfacePrototypeWorld(CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters, CertifiedRingScale.AroundChunkCount)) throw new InvalidOperationException("Certified surface prototype launch was not enabled");
        if (RingLiveTuningDrawer.IsCertifiedSurfacePrototypeWorld(80_000.19, 20_000, 15_708)) throw new InvalidOperationException("legacy 80 km integration world incorrectly enabled surface prototype launch");
        if (RingLiveTuningDrawer.IsCertifiedSurfacePrototypeWorld(MegaRingScale.EffectiveRadiusMeters, MegaRingScale.RingWidthMeters, MegaRingScale.AroundChunkCount)) throw new InvalidOperationException("Mega world incorrectly enabled surface prototype launch");
    }
}
