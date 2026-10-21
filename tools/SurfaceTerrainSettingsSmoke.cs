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
        // Albedo scales: default, round-trip, bounds, and a file written before they existed loads the defaults.
        if (!Mathf.IsEqualApprox(reset.Settings.TerrainAlbedoScale, GameplaySurfaceTerrainSettings.DefaultTerrainAlbedoScale) || !Mathf.IsEqualApprox(reset.Settings.GrassAlbedoScale, GameplaySurfaceTerrainSettings.DefaultGrassAlbedoScale)) throw new InvalidOperationException("albedo scales did not default");
        reset.SetTerrainAlbedoScale(.9f); reset.SetGrassAlbedoScale(1.4f);
        if (!reset.Save()) throw new InvalidOperationException("albedo scales did not save");
        var albedo = new GameplaySurfaceTerrainDefaults(path); albedo.Initialize();
        if (!Mathf.IsEqualApprox(albedo.Settings.TerrainAlbedoScale, .9f) || !Mathf.IsEqualApprox(albedo.Settings.GrassAlbedoScale, 1.4f)) throw new InvalidOperationException("albedo scales did not round-trip");
        if (!Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampTerrainAlbedoScale(99), 2) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGrassAlbedoScale(0), .05f) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampTerrainAlbedoScale(float.NaN), GameplaySurfaceTerrainSettings.DefaultTerrainAlbedoScale)) throw new InvalidOperationException("albedo scale bounds were not enforced");
        string albedoFile = ProjectSettings.GlobalizePath(path);
        string albedoText = System.IO.File.ReadAllText(albedoFile);
        string albedoStripped = string.Join('\n', Array.FindAll(albedoText.Split('\n'), line => !line.StartsWith("TerrainAlbedoScale") && !line.StartsWith("GrassAlbedoScale")));
        if (albedoStripped.Length >= albedoText.Length) throw new InvalidOperationException("albedo scales were not written");
        System.IO.File.WriteAllText(albedoFile, albedoStripped);
        var albedoOld = new GameplaySurfaceTerrainDefaults(path); albedoOld.Initialize();
        if (!Mathf.IsEqualApprox(albedoOld.Settings.TerrainAlbedoScale, GameplaySurfaceTerrainSettings.DefaultTerrainAlbedoScale) || !Mathf.IsEqualApprox(albedoOld.Settings.GrassAlbedoScale, GameplaySurfaceTerrainSettings.DefaultGrassAlbedoScale)) throw new InvalidOperationException("old surface file did not load albedo defaults");
        if (!Mathf.IsEqualApprox(albedoOld.Settings.GenerationUrgentBudgetMs, 6) || !Mathf.IsEqualApprox(new GameplaySurfaceTerrainSettings().GenerationUrgentBudgetMs, 6)) throw new InvalidOperationException("urgent publication budget did not default to 6");
        albedoOld.SetGenerationUrgentBudgetMs(10);
        if (!albedoOld.Save()) throw new InvalidOperationException("urgent publication budget did not save");
        var urgent = new GameplaySurfaceTerrainDefaults(path); urgent.Initialize();
        if (!Mathf.IsEqualApprox(urgent.Settings.GenerationUrgentBudgetMs, 10)) throw new InvalidOperationException("urgent publication budget did not round-trip");
        if (!Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGenerationUrgentBudgetMs(float.NaN), 6) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGenerationUrgentBudgetMs(99), 16) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGenerationUrgentBudgetMs(-1), .1f)) throw new InvalidOperationException("urgent publication budget bounds were not enforced");
        string urgentText = System.IO.File.ReadAllText(albedoFile);
        string urgentStripped = string.Join('\n', Array.FindAll(urgentText.Split('\n'), line => !line.StartsWith("GenerationUrgentBudgetMs")));
        if (urgentStripped.Length >= urgentText.Length) throw new InvalidOperationException("urgent publication budget was not written");
        System.IO.File.WriteAllText(albedoFile, urgentStripped);
        var urgentOld = new GameplaySurfaceTerrainDefaults(path); urgentOld.Initialize();
        if (!Mathf.IsEqualApprox(urgentOld.Settings.GenerationUrgentBudgetMs, 6)) throw new InvalidOperationException("old surface file did not load the urgent budget default");
        string invalidParent = path + ".invalid-parent.tres";
        if (ResourceSaver.Save(new GameplaySurfaceTerrainSettings(), invalidParent) != Error.Ok) throw new InvalidOperationException("invalid-path fixture write failed");
        var invalid = new GameplaySurfaceTerrainDefaults(invalidParent + "/child.tres"); invalid.Initialize(); invalid.SetBrushRadiusMeters(9);
        if (invalid.Save() || invalid.LastError.Length == 0) throw new InvalidOperationException("invalid surface preference path did not fail safely");
        if (!RingLiveTuningDrawer.IsCertifiedSurfacePrototypeWorld(CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters, CertifiedRingScale.AroundChunkCount)) throw new InvalidOperationException("Certified surface prototype launch was not enabled");
        if (RingLiveTuningDrawer.IsCertifiedSurfacePrototypeWorld(80_000.19, 20_000, 15_708)) throw new InvalidOperationException("legacy 80 km integration world incorrectly enabled surface prototype launch");
        if (RingLiveTuningDrawer.IsCertifiedSurfacePrototypeWorld(MegaRingScale.EffectiveRadiusMeters, MegaRingScale.RingWidthMeters, MegaRingScale.AroundChunkCount)) throw new InvalidOperationException("Mega world incorrectly enabled surface prototype launch");
    }
}
