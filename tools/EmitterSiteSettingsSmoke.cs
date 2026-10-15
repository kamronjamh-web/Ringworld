using System;
using Godot;
using RingworldTechDemo.UI;

/// <summary>Isolated persistence evidence for the emitter far stand-in preferences.</summary>
public partial class EmitterSiteSettingsSmoke : Node
{
    public string Result { get; private set; } = "RUNNING";
    public string IsolatedPath { get; set; } = "res://.godot/emitter_site_settings_smoke.tres";
    public override void _Ready()
    {
        try { Run(IsolatedPath); Result = "PASS"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
    }

    private static bool IsDefault(GameplayEmitterSiteSettings s) =>
        s.StandInsEnabled && Mathf.IsEqualApprox(s.StandInMaxDistanceKm, 700) && Mathf.IsEqualApprox(s.StandInFadeKm, 100)
        && Mathf.IsEqualApprox(s.StandInMinPixels, 1.5f) && Mathf.IsEqualApprox(s.RegularHandoffMeters, 11500) && Mathf.IsEqualApprox(s.MainHandoffMeters, 26000)
        && s.PoiStreamerEnabled && Mathf.IsEqualApprox(s.RegularLoadRadiusMeters, 14000) && Mathf.IsEqualApprox(s.MainLoadRadiusMeters, 32000)
        && Mathf.IsEqualApprox(s.UnloadMarginMeters, 2000) && Mathf.IsEqualApprox(s.MinDwellSeconds, 1) && Mathf.IsEqualApprox(s.CollisionOnMeters, 1500)
        && Mathf.IsEqualApprox(s.CollisionOffMarginMeters, 500)
        && s.UndergroundRecoveryEnabled
        && s.BoreAperturesEnabled && Mathf.IsEqualApprox(s.BoreVisibleRangeMeters, 16000) && Mathf.IsEqualApprox(s.BorePlugLightEnergy, 2)
        && s.TeleportViewpoint == 0 && Mathf.IsEqualApprox(s.FarViewpointDistanceKm, 20) && Mathf.IsEqualApprox(s.HighViewpointAltitudeKm, 150);

    private static void Run(string path)
    {
        var first = new GameplayEmitterSiteDefaults(path); first.Load();
        if (!IsDefault(first.Settings) || first.HasSavedFile) throw new InvalidOperationException("missing emitter preferences did not use defaults");
        if (!first.Save()) throw new InvalidOperationException("clean defaults should save as a no-op");
        if (!first.SetStandInsEnabled(false) || first.SetStandInsEnabled(false)) throw new InvalidOperationException("Set did not report the changed flag");
        first.SetStandInMaxDistanceKm(420); first.SetStandInFadeKm(60); first.SetStandInMinPixels(2.5f); first.SetRegularHandoffMeters(9000); first.SetMainHandoffMeters(20000);
        if (!first.SetPoiStreamerEnabled(false) || first.SetPoiStreamerEnabled(false)) throw new InvalidOperationException("POI Set did not report the changed flag");
        first.SetRegularLoadRadiusMeters(15000); first.SetMainLoadRadiusMeters(35000); first.SetUnloadMarginMeters(1500); first.SetMinDwellSeconds(2.5f);
        first.SetCollisionOnMeters(1200); first.SetCollisionOffMarginMeters(300);
        if (!first.SetBoreAperturesEnabled(false) || first.SetBoreAperturesEnabled(false)) throw new InvalidOperationException("bore Set did not report the changed flag");
        first.SetBoreVisibleRangeMeters(9000); first.SetBorePlugLightEnergy(5.5f);
        if (!first.SetUndergroundRecoveryEnabled(false) || first.SetUndergroundRecoveryEnabled(false)) throw new InvalidOperationException("recovery Set did not report the changed flag");
        if (!first.SetTeleportViewpoint(2) || first.SetTeleportViewpoint(2)) throw new InvalidOperationException("viewpoint Set did not report the changed flag");
        first.SetFarViewpointDistanceKm(35); first.SetHighViewpointAltitudeKm(250);
        if (!first.Dirty || !first.Save() || first.Dirty) throw new InvalidOperationException("emitter preferences did not save");
        var second = new GameplayEmitterSiteDefaults(path); second.Load();
        var s = second.Settings;
        if (s.StandInsEnabled || !Mathf.IsEqualApprox(s.StandInMaxDistanceKm, 420) || !Mathf.IsEqualApprox(s.StandInFadeKm, 60) || !Mathf.IsEqualApprox(s.StandInMinPixels, 2.5f)
            || !Mathf.IsEqualApprox(s.RegularHandoffMeters, 9000) || !Mathf.IsEqualApprox(s.MainHandoffMeters, 20000)) throw new InvalidOperationException("emitter preferences did not round-trip");
        if (s.PoiStreamerEnabled || !Mathf.IsEqualApprox(s.RegularLoadRadiusMeters, 15000) || !Mathf.IsEqualApprox(s.MainLoadRadiusMeters, 35000) || !Mathf.IsEqualApprox(s.UnloadMarginMeters, 1500)
            || !Mathf.IsEqualApprox(s.MinDwellSeconds, 2.5f) || !Mathf.IsEqualApprox(s.CollisionOnMeters, 1200) || !Mathf.IsEqualApprox(s.CollisionOffMarginMeters, 300)) throw new InvalidOperationException("POI streamer preferences did not round-trip");

        second.SetStandInMaxDistanceKm(-5); second.SetStandInFadeKm(9999); second.SetStandInMinPixels(float.NaN); second.SetRegularHandoffMeters(1e9f); second.SetMainHandoffMeters(-1);
        second.SetRegularLoadRadiusMeters(1); second.SetMainLoadRadiusMeters(1e9f); second.SetUnloadMarginMeters(float.NaN); second.SetMinDwellSeconds(-2);
        second.SetCollisionOnMeters(1e9f); second.SetCollisionOffMarginMeters(0);
        if (!Mathf.IsEqualApprox(second.Settings.BoreVisibleRangeMeters, 9000) || second.Settings.BoreAperturesEnabled || !Mathf.IsEqualApprox(second.Settings.BorePlugLightEnergy, 5.5f)) throw new InvalidOperationException("bore preferences did not round-trip");
        if (second.Settings.UndergroundRecoveryEnabled) throw new InvalidOperationException("underground recovery switch did not round-trip");
        if (s.TeleportViewpoint != 2 || !Mathf.IsEqualApprox(s.FarViewpointDistanceKm, 35) || !Mathf.IsEqualApprox(s.HighViewpointAltitudeKm, 250)) throw new InvalidOperationException("navigation preferences did not round-trip");
        second.SetTeleportViewpoint(99); second.SetFarViewpointDistanceKm(1); second.SetHighViewpointAltitudeKm(9999);
        if (second.Settings.TeleportViewpoint != GameplayEmitterSiteDefaults.MaximumTeleportViewpoint || !Mathf.IsEqualApprox(second.Settings.FarViewpointDistanceKm, GameplayEmitterSiteDefaults.MinimumFarViewpointKm) || !Mathf.IsEqualApprox(second.Settings.HighViewpointAltitudeKm, GameplayEmitterSiteDefaults.MaximumHighViewpointKm)) throw new InvalidOperationException("navigation setter bounds were not enforced");
        second.SetFarViewpointDistanceKm(float.NaN); second.SetHighViewpointAltitudeKm(float.NaN);
        if (!Mathf.IsEqualApprox(second.Settings.FarViewpointDistanceKm, 20) || !Mathf.IsEqualApprox(second.Settings.HighViewpointAltitudeKm, 150)) throw new InvalidOperationException("navigation NaN did not fall back to defaults");
        second.SetBoreVisibleRangeMeters(1); second.SetBorePlugLightEnergy(99);
        if (!Mathf.IsEqualApprox(second.Settings.BoreVisibleRangeMeters, GameplayEmitterSiteDefaults.MinimumBoreRangeMeters) || !Mathf.IsEqualApprox(second.Settings.BorePlugLightEnergy, GameplayEmitterSiteDefaults.MaximumBoreLightEnergy)) throw new InvalidOperationException("bore setter bounds were not enforced");
        second.SetBoreVisibleRangeMeters(float.NaN); second.SetBorePlugLightEnergy(float.NaN);
        if (!Mathf.IsEqualApprox(second.Settings.BoreVisibleRangeMeters, 16000) || !Mathf.IsEqualApprox(second.Settings.BorePlugLightEnergy, 2)) throw new InvalidOperationException("bore NaN did not fall back to defaults");
        s = second.Settings;
        if (!Mathf.IsEqualApprox(s.StandInMaxDistanceKm, GameplayEmitterSiteDefaults.MinimumMaxDistanceKm) || !Mathf.IsEqualApprox(s.StandInFadeKm, GameplayEmitterSiteDefaults.MaximumFadeKm)
            || !Mathf.IsEqualApprox(s.StandInMinPixels, 1.5f) || !Mathf.IsEqualApprox(s.RegularHandoffMeters, GameplayEmitterSiteDefaults.MaximumRegularHandoffMeters)
            || !Mathf.IsEqualApprox(s.MainHandoffMeters, 0)) throw new InvalidOperationException("setter bounds were not enforced");
        if (!Mathf.IsEqualApprox(s.RegularLoadRadiusMeters, GameplayEmitterSiteDefaults.MinimumRegularLoadMeters) || !Mathf.IsEqualApprox(s.MainLoadRadiusMeters, GameplayEmitterSiteDefaults.MaximumMainLoadMeters)
            || !Mathf.IsEqualApprox(s.UnloadMarginMeters, 2000) || !Mathf.IsEqualApprox(s.MinDwellSeconds, GameplayEmitterSiteDefaults.MinimumDwellSeconds)
            || !Mathf.IsEqualApprox(s.CollisionOnMeters, GameplayEmitterSiteDefaults.MaximumCollisionOnMeters) || !Mathf.IsEqualApprox(s.CollisionOffMarginMeters, GameplayEmitterSiteDefaults.MinimumCollisionOffMarginMeters))
            throw new InvalidOperationException("POI streamer setter bounds were not enforced");
        using var dirty = new GameplayEmitterSiteSettings { StandInMaxDistanceKm = 99999, StandInFadeKm = float.PositiveInfinity, StandInMinPixels = -3, RegularHandoffMeters = float.NaN, MainHandoffMeters = 1e9f,
            RegularLoadRadiusMeters = 100, MainLoadRadiusMeters = float.NaN, UnloadMarginMeters = 1e9f, MinDwellSeconds = float.PositiveInfinity, CollisionOnMeters = -1, CollisionOffMarginMeters = 1e9f,
            BoreVisibleRangeMeters = 1e9f, BorePlugLightEnergy = float.NaN, TeleportViewpoint = -4, FarViewpointDistanceKm = float.NaN, HighViewpointAltitudeKm = 1e9f };
        using var clean = GameplayEmitterSiteDefaults.Sanitize(dirty);
        if (!Mathf.IsEqualApprox(clean.StandInMaxDistanceKm, GameplayEmitterSiteDefaults.MaximumMaxDistanceKm) || !Mathf.IsEqualApprox(clean.StandInFadeKm, 100)
            || !Mathf.IsEqualApprox(clean.StandInMinPixels, GameplayEmitterSiteDefaults.MinimumMinPixels) || !Mathf.IsEqualApprox(clean.RegularHandoffMeters, 11500)
            || !Mathf.IsEqualApprox(clean.MainHandoffMeters, GameplayEmitterSiteDefaults.MaximumMainHandoffMeters)) throw new InvalidOperationException("Sanitize bounds were not enforced");
        if (!Mathf.IsEqualApprox(clean.RegularLoadRadiusMeters, GameplayEmitterSiteDefaults.MinimumRegularLoadMeters) || !Mathf.IsEqualApprox(clean.MainLoadRadiusMeters, 32000)
            || !Mathf.IsEqualApprox(clean.UnloadMarginMeters, GameplayEmitterSiteDefaults.MaximumUnloadMarginMeters) || !Mathf.IsEqualApprox(clean.MinDwellSeconds, 1)
            || !Mathf.IsEqualApprox(clean.CollisionOnMeters, GameplayEmitterSiteDefaults.MinimumCollisionOnMeters) || !Mathf.IsEqualApprox(clean.CollisionOffMarginMeters, GameplayEmitterSiteDefaults.MaximumCollisionOffMarginMeters))
            throw new InvalidOperationException("POI streamer Sanitize bounds were not enforced");

        if (!Mathf.IsEqualApprox(clean.BoreVisibleRangeMeters, GameplayEmitterSiteDefaults.MaximumBoreRangeMeters) || !Mathf.IsEqualApprox(clean.BorePlugLightEnergy, 2)) throw new InvalidOperationException("bore Sanitize bounds were not enforced");
        if (clean.TeleportViewpoint != 0 || !Mathf.IsEqualApprox(clean.FarViewpointDistanceKm, 20) || !Mathf.IsEqualApprox(clean.HighViewpointAltitudeKm, GameplayEmitterSiteDefaults.MaximumHighViewpointKm)) throw new InvalidOperationException("navigation Sanitize bounds were not enforced");

        second.Reset();
        if (!second.Save()) throw new InvalidOperationException("emitter reset did not save");
        var reset = new GameplayEmitterSiteDefaults(path); reset.Load();
        if (!IsDefault(reset.Settings)) throw new InvalidOperationException("emitter reset did not restore defaults");

        string invalidParent = path + ".invalid-parent.tres";
        if (ResourceSaver.Save(new GameplayEmitterSiteSettings(), invalidParent) != Error.Ok) throw new InvalidOperationException("invalid-path fixture write failed");
        var invalid = new GameplayEmitterSiteDefaults(invalidParent + "/child.tres"); invalid.Load(); invalid.SetStandInFadeKm(50);
        if (invalid.Save() || invalid.LastError.Length == 0) throw new InvalidOperationException("invalid emitter preference path did not fail safely");
    }
}
