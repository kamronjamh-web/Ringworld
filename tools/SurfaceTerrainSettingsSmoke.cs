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
        if (!Mathf.IsEqualApprox(first.Settings.BrushRadiusMeters, 8) || !Mathf.IsEqualApprox(first.Settings.BrushStepMeters, 2) || first.Settings.MeshSize != 48 || !Mathf.IsEqualApprox(first.Settings.HandoffDistanceMeters, 112) || !Mathf.IsEqualApprox(first.Settings.MoveSpeedMetersPerSecond, 2) || !Mathf.IsEqualApprox(first.Settings.SprintSpeedMetersPerSecond, 7) || !Mathf.IsEqualApprox(first.Settings.DryRoughness, .92f) || !Mathf.IsEqualApprox(first.Settings.WetRoughness, .35f) || !Mathf.IsEqualApprox(first.Settings.RainWetnessStrength, 1) || !first.Settings.SmoothTerrainShading) throw new InvalidOperationException("missing surface preferences did not use defaults");
        first.SetBrushRadiusMeters(24); first.SetBrushStepMeters(.5f); first.SetMeshSize(24); first.SetHandoffDistanceMeters(128); first.SetMoveSpeedMetersPerSecond(30); first.SetDryRoughness(.8f); first.SetWetRoughness(.28f); first.SetRainWetnessStrength(.6f); first.SetSmoothTerrainShading(false);
        if (!first.Save()) throw new InvalidOperationException("surface preferences did not save");
        var second = new GameplaySurfaceTerrainDefaults(path); second.Initialize();
        if (!Mathf.IsEqualApprox(second.Settings.BrushRadiusMeters, 24) || !Mathf.IsEqualApprox(second.Settings.BrushStepMeters, .5f) || second.Settings.MeshSize != 24 || !Mathf.IsEqualApprox(second.Settings.HandoffDistanceMeters, 128) || !Mathf.IsEqualApprox(second.Settings.MoveSpeedMetersPerSecond, 30) || !Mathf.IsEqualApprox(second.Settings.DryRoughness, .8f) || !Mathf.IsEqualApprox(second.Settings.WetRoughness, .28f) || !Mathf.IsEqualApprox(second.Settings.RainWetnessStrength, .6f) || second.Settings.SmoothTerrainShading) throw new InvalidOperationException("surface preferences did not round-trip");
        if (!Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampBrushRadiusMeters(-1), 2) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampBrushStepMeters(99), 8) || GameplaySurfaceTerrainDefaults.ClampMeshSize(21) != 24 || GameplaySurfaceTerrainDefaults.ClampMeshSize(99) != 64 || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampHandoffDistanceMeters(-1), 84) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampHandoffDistanceMeters(999), 128) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampMoveSpeedMetersPerSecond(float.NaN), 2) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampMoveSpeedMetersPerSecond(0), .5f) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampMoveSpeedMetersPerSecond(999), 60) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampSprintSpeedMetersPerSecond(float.NaN), 7) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampSprintSpeedMetersPerSecond(0), 1) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampSprintSpeedMetersPerSecond(999), 80) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampDryRoughness(-1), GameplaySurfaceTerrainDefaults.MinimumDryRoughness) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampWetRoughness(99), GameplaySurfaceTerrainDefaults.MaximumWetRoughness) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampRainWetnessStrength(float.NaN), 1)) throw new InvalidOperationException("surface preference bounds were not enforced");
        second.Reset(); if (!second.Save()) throw new InvalidOperationException("surface preference reset did not save");
        var reset = new GameplaySurfaceTerrainDefaults(path); reset.Initialize();
        if (!Mathf.IsEqualApprox(reset.Settings.BrushRadiusMeters, 8) || !Mathf.IsEqualApprox(reset.Settings.BrushStepMeters, 2) || reset.Settings.MeshSize != 48 || !Mathf.IsEqualApprox(reset.Settings.HandoffDistanceMeters, 112) || !Mathf.IsEqualApprox(reset.Settings.MoveSpeedMetersPerSecond, 2) || !Mathf.IsEqualApprox(reset.Settings.SprintSpeedMetersPerSecond, 7) || !Mathf.IsEqualApprox(reset.Settings.DryRoughness, .92f) || !Mathf.IsEqualApprox(reset.Settings.WetRoughness, .35f) || !Mathf.IsEqualApprox(reset.Settings.RainWetnessStrength, 1) || !reset.Settings.SmoothTerrainShading) throw new InvalidOperationException("surface preference reset did not restore defaults");
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
        // MeshLods was removed 2026-11-17 (Terrain3D builds only the clipmap levels past the handoff): an old file that still has it must load quietly.
        System.IO.File.WriteAllText(albedoFile, urgentStripped.TrimEnd() + "\nMeshLods = 5\n");
        var meshLodsOld = new GameplaySurfaceTerrainDefaults(path); meshLodsOld.Initialize();
        if (meshLodsOld.LastError.Length != 0 || !Mathf.IsEqualApprox(meshLodsOld.Settings.BrushRadiusMeters, urgentOld.Settings.BrushRadiusMeters)) throw new InvalidOperationException("old surface file with MeshLods did not load quietly");
        // T4 ground surface rows: defaults equal the designed look (and the ringworld_ground_look project default), round-trip, bounds, NaN fallback, and an older file loads the defaults.
        var ground = new GameplaySurfaceTerrainDefaults(path); ground.Initialize();
        Vector4 projectLook = ProjectSettings.GetSetting("shader_globals/ringworld_ground_look").AsGodotDictionary()["value"].AsVector4();
        if (!Mathf.IsEqualApprox(ground.Settings.GroundRockSlopeDegrees, 38) || !Mathf.IsEqualApprox(ground.Settings.GroundStrataContrast, .12f) || !Mathf.IsEqualApprox(ground.Settings.GroundColorVariation, 1) || !Mathf.IsEqualApprox(projectLook.X, ground.Settings.GroundRockSlopeDegrees) || !Mathf.IsEqualApprox(projectLook.Y, ground.Settings.GroundStrataContrast) || !Mathf.IsEqualApprox(projectLook.Z, ground.Settings.GroundColorVariation)) throw new InvalidOperationException("ground surface settings did not default to the designed look (project default " + projectLook + ")");
        if (ground.SetGroundRockSlopeDegrees(38) || ground.SetGroundStrataContrast(.12f) || ground.SetGroundColorVariation(1)) throw new InvalidOperationException("ground surface setters reported a change for the default value");
        if (!ground.SetGroundRockSlopeDegrees(44.5f) || !ground.SetGroundStrataContrast(.2f) || !ground.SetGroundColorVariation(1.5f)) throw new InvalidOperationException("ground surface setters did not report a change");
        if (!ground.Save()) throw new InvalidOperationException("ground surface settings did not save");
        var groundLoaded = new GameplaySurfaceTerrainDefaults(path); groundLoaded.Initialize();
        if (!Mathf.IsEqualApprox(groundLoaded.Settings.GroundRockSlopeDegrees, 44.5f) || !Mathf.IsEqualApprox(groundLoaded.Settings.GroundStrataContrast, .2f) || !Mathf.IsEqualApprox(groundLoaded.Settings.GroundColorVariation, 1.5f)) throw new InvalidOperationException("ground surface settings did not round-trip");
        Vector4 published = RingLiveTuningDrawer.GroundLookGlobal(groundLoaded.Settings);
        if (!Mathf.IsEqualApprox(published.X, 44.5f) || !Mathf.IsEqualApprox(published.Y, .2f) || !Mathf.IsEqualApprox(published.Z, 1.5f) || published.W != 0f) throw new InvalidOperationException("the published ringworld_ground_look did not carry the three ground surface settings (" + published + ")");
        if (!Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGroundRockSlopeDegrees(10), 30) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGroundRockSlopeDegrees(99), 50) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGroundRockSlopeDegrees(float.NaN), 38)
            || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGroundStrataContrast(-1), 0) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGroundStrataContrast(5), .3f) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGroundStrataContrast(float.PositiveInfinity), .12f)
            || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGroundColorVariation(-1), 0) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGroundColorVariation(9), 2) || !Mathf.IsEqualApprox(GameplaySurfaceTerrainDefaults.ClampGroundColorVariation(float.NaN), 1)) throw new InvalidOperationException("ground surface setting bounds were not enforced");
        string groundFile = ProjectSettings.GlobalizePath(path);
        string groundText = System.IO.File.ReadAllText(groundFile);
        string groundStripped = string.Join('\n', Array.FindAll(groundText.Split('\n'), line => !line.StartsWith("GroundRockSlopeDegrees") && !line.StartsWith("GroundStrataContrast") && !line.StartsWith("GroundColorVariation")));
        if (groundStripped.Length >= groundText.Length) throw new InvalidOperationException("ground surface settings were not written");
        System.IO.File.WriteAllText(groundFile, groundStripped);
        var groundOld = new GameplaySurfaceTerrainDefaults(path); groundOld.Initialize();
        if (!Mathf.IsEqualApprox(groundOld.Settings.GroundRockSlopeDegrees, 38) || !Mathf.IsEqualApprox(groundOld.Settings.GroundStrataContrast, .12f) || !Mathf.IsEqualApprox(groundOld.Settings.GroundColorVariation, 1)) throw new InvalidOperationException("old surface file did not load the ground surface defaults");
        groundOld.SetGroundRockSlopeDegrees(41); groundOld.Reset();
        if (!Mathf.IsEqualApprox(groundOld.Settings.GroundRockSlopeDegrees, 38) || !Mathf.IsEqualApprox(groundOld.Settings.GroundColorVariation, 1)) throw new InvalidOperationException("surface reset did not restore the ground surface defaults");
        // Realistic movement (user 2026-11-20): sprint default, setter semantics, round-trip, and the one-time walk-speed migration.
        var sprint = new GameplaySurfaceTerrainDefaults(path); sprint.Initialize();
        if (sprint.SetSprintSpeedMetersPerSecond(7) || !sprint.SetSprintSpeedMetersPerSecond(9.5f) || !sprint.SetMoveSpeedMetersPerSecond(3.5f)) throw new InvalidOperationException("sprint setter change semantics were wrong");
        if (!sprint.Save()) throw new InvalidOperationException("sprint speed did not save");
        var sprintLoaded = new GameplaySurfaceTerrainDefaults(path); sprintLoaded.Initialize();
        if (!Mathf.IsEqualApprox(sprintLoaded.Settings.SprintSpeedMetersPerSecond, 9.5f) || !Mathf.IsEqualApprox(sprintLoaded.Settings.MoveSpeedMetersPerSecond, 3.5f) || sprintLoaded.Dirty || sprintLoaded.Settings.MoveSpeedMigration != GameplaySurfaceTerrainSettings.CurrentMoveSpeedMigration) throw new InvalidOperationException("sprint speed did not round-trip, or a migrated file was reset again");
        sprintLoaded.SetSprintSpeedMetersPerSecond(float.NaN);
        if (!Mathf.IsEqualApprox(sprintLoaded.Settings.SprintSpeedMetersPerSecond, 7)) throw new InvalidOperationException("sprint NaN did not fall back to the default");
        // A file saved before the marker existed: walk 12 and sprint 20 on disk -> walk reset to 2 once, sprint kept, file loads dirty; after saving, a later load leaves a re-chosen walk alone.
        string moveFile = ProjectSettings.GlobalizePath(path);
        string moveText = System.IO.File.ReadAllText(moveFile);
        string[] kept = Array.FindAll(moveText.Split('\n'), line => !line.StartsWith("MoveSpeedMigration") && !line.StartsWith("MoveSpeedMetersPerSecond") && !line.StartsWith("SprintSpeedMetersPerSecond"));
        string legacy = string.Join('\n', kept).TrimEnd() + "\nMoveSpeedMetersPerSecond = 12.0\nSprintSpeedMetersPerSecond = 20.0\n";
        System.IO.File.WriteAllText(moveFile, legacy);
        var migrating = new GameplaySurfaceTerrainDefaults(path); migrating.Initialize();
        if (!Mathf.IsEqualApprox(migrating.Settings.MoveSpeedMetersPerSecond, 2) || !Mathf.IsEqualApprox(migrating.Settings.SprintSpeedMetersPerSecond, 20) || !migrating.Dirty || migrating.Settings.MoveSpeedMigration != GameplaySurfaceTerrainSettings.CurrentMoveSpeedMigration) throw new InvalidOperationException("legacy walk speed 12 was not migrated to 2 (walk " + migrating.Settings.MoveSpeedMetersPerSecond + ", dirty " + migrating.Dirty + ")");
        if (!migrating.Save()) throw new InvalidOperationException("migrated surface settings did not save");
        var afterMigration = new GameplaySurfaceTerrainDefaults(path); afterMigration.Initialize();
        if (!Mathf.IsEqualApprox(afterMigration.Settings.MoveSpeedMetersPerSecond, 2) || afterMigration.Dirty) throw new InvalidOperationException("migrated file did not load clean");
        afterMigration.SetMoveSpeedMetersPerSecond(12); afterMigration.Save();
        var keptChoice = new GameplaySurfaceTerrainDefaults(path); keptChoice.Initialize();
        if (!Mathf.IsEqualApprox(keptChoice.Settings.MoveSpeedMetersPerSecond, 12) || keptChoice.Dirty) throw new InvalidOperationException("the walk speed migration ran twice");
        string invalidParent = path + ".invalid-parent.tres";
        if (ResourceSaver.Save(new GameplaySurfaceTerrainSettings(), invalidParent) != Error.Ok) throw new InvalidOperationException("invalid-path fixture write failed");
        var invalid = new GameplaySurfaceTerrainDefaults(invalidParent + "/child.tres"); invalid.Initialize(); invalid.SetBrushRadiusMeters(9);
        if (invalid.Save() || invalid.LastError.Length == 0) throw new InvalidOperationException("invalid surface preference path did not fail safely");
        if (!RingLiveTuningDrawer.IsCertifiedSurfacePrototypeWorld(CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters, CertifiedRingScale.AroundChunkCount)) throw new InvalidOperationException("Certified surface prototype launch was not enabled");
        if (RingLiveTuningDrawer.IsCertifiedSurfacePrototypeWorld(80_000.19, 20_000, 15_708)) throw new InvalidOperationException("legacy 80 km integration world incorrectly enabled surface prototype launch");
        if (RingLiveTuningDrawer.IsCertifiedSurfacePrototypeWorld(MegaRingScale.EffectiveRadiusMeters, MegaRingScale.RingWidthMeters, MegaRingScale.AroundChunkCount)) throw new InvalidOperationException("Mega world incorrectly enabled surface prototype launch");
    }
}
