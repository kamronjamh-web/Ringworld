using System;
using System.IO;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Surface;

namespace RingworldTechDemo.UI;

/// <summary>Saved emitter far stand-in choices shared with the grave-key drawer.</summary>
public sealed class GameplayEmitterSiteDefaults
{
    public const string UserPath = "user://gameplay_emitter_site_defaults.tres";
    public const float MinimumMaxDistanceKm = 50f, MaximumMaxDistanceKm = 850f;
    public const float MinimumFadeKm = 10f, MaximumFadeKm = 300f;
    public const float MinimumMinPixels = .5f, MaximumMinPixels = 4f;
    public const float MaximumRegularHandoffMeters = 30000f, MaximumMainHandoffMeters = 60000f;
    public const float MinimumRegularLoadMeters = 13500f, MaximumRegularLoadMeters = 18000f;
    public const float MinimumMainLoadMeters = 30500f, MaximumMainLoadMeters = 60000f;
    public const float MinimumUnloadMarginMeters = 500f, MaximumUnloadMarginMeters = 4000f;
    public const float MinimumDwellSeconds = 0f, MaximumDwellSeconds = 5f;
    public const float MinimumCollisionOnMeters = 500f, MaximumCollisionOnMeters = 5000f;
    public const float MinimumCollisionOffMarginMeters = 100f, MaximumCollisionOffMarginMeters = 2000f;
    public const float MinimumBoreRangeMeters = 2000f, MaximumBoreRangeMeters = 20000f;
    public const float MinimumBoreLightEnergy = 0f, MaximumBoreLightEnergy = 8f;
    public const int MinimumTeleportViewpoint = 0, MaximumTeleportViewpoint = 3;
    public const float MinimumFarViewpointKm = 5f, MaximumFarViewpointKm = 60f;
    public const float MinimumHighViewpointKm = 20f, MaximumHighViewpointKm = 400f;
    private readonly string _path;
    public GameplayEmitterSiteSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayEmitterSiteDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

    /// <summary>True once the player has saved a choice; otherwise the layer keeps its own defaults.</summary>
    public bool HasSavedFile => ResourceLoader.Exists(_path);

    public void Load()
    {
        Settings = new();
        LastError = "";
        Dirty = false;
        try
        {
            if (!ResourceLoader.Exists(_path)) return;
            using var loaded = ResourceLoader.Load<GameplayEmitterSiteSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid emitter site settings"; return; }
            using var clean = Sanitize(loaded);
            Copy(clean, Settings);
        }
        catch (Exception error) { LastError = error.Message; }
    }

    /// <summary>Clamps every field; non-finite values fall back to the defaults.</summary>
    public static GameplayEmitterSiteSettings Sanitize(GameplayEmitterSiteSettings input) => new()
    {
        StandInsEnabled = input.StandInsEnabled,
        StandInMaxDistanceKm = Finite(input.StandInMaxDistanceKm, 700f, MinimumMaxDistanceKm, MaximumMaxDistanceKm),
        StandInFadeKm = Finite(input.StandInFadeKm, 100f, MinimumFadeKm, MaximumFadeKm),
        StandInMinPixels = Finite(input.StandInMinPixels, 1.5f, MinimumMinPixels, MaximumMinPixels),
        RegularHandoffMeters = Finite(input.RegularHandoffMeters, 11500f, 0f, MaximumRegularHandoffMeters),
        MainHandoffMeters = Finite(input.MainHandoffMeters, 26000f, 0f, MaximumMainHandoffMeters),
        PoiStreamerEnabled = input.PoiStreamerEnabled,
        RegularLoadRadiusMeters = Finite(input.RegularLoadRadiusMeters, 14000f, MinimumRegularLoadMeters, MaximumRegularLoadMeters),
        MainLoadRadiusMeters = Finite(input.MainLoadRadiusMeters, 32000f, MinimumMainLoadMeters, MaximumMainLoadMeters),
        UnloadMarginMeters = Finite(input.UnloadMarginMeters, 2000f, MinimumUnloadMarginMeters, MaximumUnloadMarginMeters),
        MinDwellSeconds = Finite(input.MinDwellSeconds, 1f, MinimumDwellSeconds, MaximumDwellSeconds),
        CollisionOnMeters = Finite(input.CollisionOnMeters, 1500f, MinimumCollisionOnMeters, MaximumCollisionOnMeters),
        CollisionOffMarginMeters = Finite(input.CollisionOffMarginMeters, 500f, MinimumCollisionOffMarginMeters, MaximumCollisionOffMarginMeters),
        BoreAperturesEnabled = input.BoreAperturesEnabled,
        BoreVisibleRangeMeters = Finite(input.BoreVisibleRangeMeters, 16000f, MinimumBoreRangeMeters, MaximumBoreRangeMeters),
        BorePlugLightEnergy = Finite(input.BorePlugLightEnergy, 2f, MinimumBoreLightEnergy, MaximumBoreLightEnergy),
        TeleportViewpoint = Mathf.Clamp(input.TeleportViewpoint, MinimumTeleportViewpoint, MaximumTeleportViewpoint),
        FarViewpointDistanceKm = Finite(input.FarViewpointDistanceKm, 20f, MinimumFarViewpointKm, MaximumFarViewpointKm),
        HighViewpointAltitudeKm = Finite(input.HighViewpointAltitudeKm, 150f, MinimumHighViewpointKm, MaximumHighViewpointKm)
    };

    private static float Finite(float value, float fallback, float min, float max) =>
        float.IsFinite(value) ? Mathf.Clamp(value, min, max) : fallback;

    private static void Copy(GameplayEmitterSiteSettings from, GameplayEmitterSiteSettings to)
    {
        to.StandInsEnabled = from.StandInsEnabled;
        to.StandInMaxDistanceKm = from.StandInMaxDistanceKm;
        to.StandInFadeKm = from.StandInFadeKm;
        to.StandInMinPixels = from.StandInMinPixels;
        to.RegularHandoffMeters = from.RegularHandoffMeters;
        to.MainHandoffMeters = from.MainHandoffMeters;
        to.PoiStreamerEnabled = from.PoiStreamerEnabled;
        to.RegularLoadRadiusMeters = from.RegularLoadRadiusMeters;
        to.MainLoadRadiusMeters = from.MainLoadRadiusMeters;
        to.UnloadMarginMeters = from.UnloadMarginMeters;
        to.MinDwellSeconds = from.MinDwellSeconds;
        to.CollisionOnMeters = from.CollisionOnMeters;
        to.CollisionOffMarginMeters = from.CollisionOffMarginMeters;
        to.BoreAperturesEnabled = from.BoreAperturesEnabled;
        to.BoreVisibleRangeMeters = from.BoreVisibleRangeMeters;
        to.BorePlugLightEnergy = from.BorePlugLightEnergy;
        to.TeleportViewpoint = from.TeleportViewpoint;
        to.FarViewpointDistanceKm = from.FarViewpointDistanceKm;
        to.HighViewpointAltitudeKm = from.HighViewpointAltitudeKm;
    }

    public bool SetStandInsEnabled(bool value) => Change(Settings.StandInsEnabled != value, () => Settings.StandInsEnabled = value);
    public bool SetStandInMaxDistanceKm(float value) { float v = Finite(value, 700f, MinimumMaxDistanceKm, MaximumMaxDistanceKm); return Change(Settings.StandInMaxDistanceKm != v, () => Settings.StandInMaxDistanceKm = v); }
    public bool SetStandInFadeKm(float value) { float v = Finite(value, 100f, MinimumFadeKm, MaximumFadeKm); return Change(Settings.StandInFadeKm != v, () => Settings.StandInFadeKm = v); }
    public bool SetStandInMinPixels(float value) { float v = Finite(value, 1.5f, MinimumMinPixels, MaximumMinPixels); return Change(Settings.StandInMinPixels != v, () => Settings.StandInMinPixels = v); }
    public bool SetRegularHandoffMeters(float value) { float v = Finite(value, 11500f, 0f, MaximumRegularHandoffMeters); return Change(Settings.RegularHandoffMeters != v, () => Settings.RegularHandoffMeters = v); }
    public bool SetMainHandoffMeters(float value) { float v = Finite(value, 26000f, 0f, MaximumMainHandoffMeters); return Change(Settings.MainHandoffMeters != v, () => Settings.MainHandoffMeters = v); }
    public bool SetPoiStreamerEnabled(bool value) => Change(Settings.PoiStreamerEnabled != value, () => Settings.PoiStreamerEnabled = value);
    public bool SetRegularLoadRadiusMeters(float value) { float v = Finite(value, 14000f, MinimumRegularLoadMeters, MaximumRegularLoadMeters); return Change(Settings.RegularLoadRadiusMeters != v, () => Settings.RegularLoadRadiusMeters = v); }
    public bool SetMainLoadRadiusMeters(float value) { float v = Finite(value, 32000f, MinimumMainLoadMeters, MaximumMainLoadMeters); return Change(Settings.MainLoadRadiusMeters != v, () => Settings.MainLoadRadiusMeters = v); }
    public bool SetUnloadMarginMeters(float value) { float v = Finite(value, 2000f, MinimumUnloadMarginMeters, MaximumUnloadMarginMeters); return Change(Settings.UnloadMarginMeters != v, () => Settings.UnloadMarginMeters = v); }
    public bool SetMinDwellSeconds(float value) { float v = Finite(value, 1f, MinimumDwellSeconds, MaximumDwellSeconds); return Change(Settings.MinDwellSeconds != v, () => Settings.MinDwellSeconds = v); }
    public bool SetCollisionOnMeters(float value) { float v = Finite(value, 1500f, MinimumCollisionOnMeters, MaximumCollisionOnMeters); return Change(Settings.CollisionOnMeters != v, () => Settings.CollisionOnMeters = v); }
    public bool SetCollisionOffMarginMeters(float value) { float v = Finite(value, 500f, MinimumCollisionOffMarginMeters, MaximumCollisionOffMarginMeters); return Change(Settings.CollisionOffMarginMeters != v, () => Settings.CollisionOffMarginMeters = v); }
    public bool SetBoreAperturesEnabled(bool value) => Change(Settings.BoreAperturesEnabled != value, () => Settings.BoreAperturesEnabled = value);
    public bool SetBoreVisibleRangeMeters(float value) { float v = Finite(value, 16000f, MinimumBoreRangeMeters, MaximumBoreRangeMeters); return Change(Settings.BoreVisibleRangeMeters != v, () => Settings.BoreVisibleRangeMeters = v); }
    public bool SetBorePlugLightEnergy(float value) { float v = Finite(value, 2f, MinimumBoreLightEnergy, MaximumBoreLightEnergy); return Change(Settings.BorePlugLightEnergy != v, () => Settings.BorePlugLightEnergy = v); }
    public bool SetTeleportViewpoint(int value) { int v = Mathf.Clamp(value, MinimumTeleportViewpoint, MaximumTeleportViewpoint); return Change(Settings.TeleportViewpoint != v, () => Settings.TeleportViewpoint = v); }
    public bool SetFarViewpointDistanceKm(float value) { float v = Finite(value, 20f, MinimumFarViewpointKm, MaximumFarViewpointKm); return Change(Settings.FarViewpointDistanceKm != v, () => Settings.FarViewpointDistanceKm = v); }
    public bool SetHighViewpointAltitudeKm(float value) { float v = Finite(value, 150f, MinimumHighViewpointKm, MaximumHighViewpointKm); return Change(Settings.HighViewpointAltitudeKm != v, () => Settings.HighViewpointAltitudeKm = v); }

    private bool Change(bool differs, Action apply)
    {
        if (!differs) return false;
        apply();
        Dirty = true;
        return true;
    }

    /// <summary>Copies the saved choices onto the stand-in layer.</summary>
    public void Apply(EmitterStandInLayer? layer)
    {
        if (layer == null || !GodotObject.IsInstanceValid(layer)) return;
        layer.Configure(Settings.StandInsEnabled, Settings.StandInMaxDistanceKm * 1000f, Settings.StandInFadeKm * 1000f,
            Settings.StandInMinPixels, Settings.RegularHandoffMeters, Settings.MainHandoffMeters);
    }

    /// <summary>Copies the saved choices onto the POI streamer (its radii are raised to the effective minimum internally).</summary>
    public void Apply(EmitterPoiStreamer? streamer)
    {
        if (streamer == null || !GodotObject.IsInstanceValid(streamer)) return;
        streamer.Configure(Settings.PoiStreamerEnabled, Settings.RegularLoadRadiusMeters, Settings.MainLoadRadiusMeters, Settings.UnloadMarginMeters,
            Settings.MinDwellSeconds, Settings.CollisionOnMeters, Settings.CollisionOffMarginMeters);
    }

    /// <summary>Applies the surface hole switch to the Terrain3D gameplay surface (the new window swaps in asynchronously).</summary>
    public void Apply(RingTerrain3DGameplay? gameplay)
    {
        if (gameplay == null || !GodotObject.IsInstanceValid(gameplay)) return;
        gameplay.SetSurfaceAperturesEnabled(Settings.BoreAperturesEnabled);
    }

    /// <summary>Copies the bore visible range and plug light energy onto the bore layer.</summary>
    public void Apply(EmitterBoreLayer? layer)
    {
        if (layer == null || !GodotObject.IsInstanceValid(layer)) return;
        layer.Configure(Settings.BoreVisibleRangeMeters, Settings.BorePlugLightEnergy);
    }

    public void Reset() { Settings = new(); Dirty = true; LastError = ""; }

    public bool Save()
    {
        if (!Dirty) return true;
        string temporary = "";
        try
        {
            string full = ProjectSettings.GlobalizePath(_path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save emitter site settings");
            File.Move(ProjectSettings.GlobalizePath(temporary), full, true);
            temporary = "";
            Dirty = false;
            LastError = "";
            return true;
        }
        catch (Exception error) { LastError = error.Message; return false; }
        finally { if (temporary.Length > 0) try { File.Delete(ProjectSettings.GlobalizePath(temporary)); } catch { } }
    }
}
