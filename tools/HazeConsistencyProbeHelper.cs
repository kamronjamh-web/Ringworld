using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Test-only bridge for tools/haze_consistency_gpu_test.gd. RingAtmosphereModel is a
/// static C# class, so GDScript cannot read it directly. This exposes the live model
/// values to the harness without touching production code.
/// </summary>
public partial class HazeConsistencyProbeHelper : RefCounted
{
    /// <summary>Same Natural/Cinematic values as AtmosphereVisualDefaults.SetPreset.</summary>
    public void ApplyPreset(int preset, float density)
    {
        if (preset == 0)
            RingAtmosphereModel.ApplyArtDirection(RingAtmosphereModel.ArtDirectionPreset.Natural, 120.0f, density, 0.5f, 0.35f, 0.22f, 0.25f);
        else
            RingAtmosphereModel.ApplyArtDirection(RingAtmosphereModel.ArtDirectionPreset.Cinematic, 60.0f, density * 1.5f, 0.5f, 0.65f, 0.55f, 0.6f);
    }

    /// <summary>0 = Legacy, 1 = Physical (P2a temporary A/B toggle).</summary>
    public void SetHazeModel(int model)
        => RingAtmosphereModel.HazeModel = model == 1 ? RingAtmosphereModel.HazeModelKind.Physical : RingAtmosphereModel.HazeModelKind.Legacy;

    public void ConfigureMaterial(ShaderMaterial material, double ringRadiusMeters)
        => RingAtmosphereModel.ConfigureMaterial(material, ringRadiusMeters);

    private AtmosphereVisualDefaults? _menuDefaults;

    /// <summary>Sets non-default P4 controls through AtmosphereVisualDefaults (the exact path the Atmosphere tab uses; never saved).
    /// Later ApplyPreset/SetHazeModel calls keep the controls (they live in RingAtmosphereModel statics).</summary>
    public void ApplyMenuControls(int preset, int model)
    {
        _menuDefaults = new AtmosphereVisualDefaults("user://haze_consistency_p4_unused.tres");
        _menuDefaults.SetPreset(preset);
        _menuDefaults.SetHazeModel(model);
        _menuDefaults.Set("RayleighStrength", 1.5f);
        _menuDefaults.Set("MistDensity", 2.0f);
        _menuDefaults.Set("MistHeight", 700.0f);
        _menuDefaults.Set("AerosolHeight", 2500.0f);
        _menuDefaults.Set("GroundAlbedo", 0.25f);
        _menuDefaults.Set("MultiScatter", 1.5f);
        _menuDefaults.Set("SkyTintR", 1.1f);
        _menuDefaults.SetHazeQuality(16);
    }

    /// <summary>Restores every P4 control to its default through the same path.</summary>
    public void ResetMenuControls() { _menuDefaults?.Reset(); }

    public Godot.Collections.Dictionary PhysicalControlState() => new()
    {
        ["sigma_r"] = RingAtmosphereModel.HazeSigmaRayleigh, ["sigma_m"] = RingAtmosphereModel.HazeSigmaMist,
        ["h_mist"] = RingAtmosphereModel.HazeMistHeightMeters, ["h_a"] = RingAtmosphereModel.HazeAerosolHeightMeters,
        ["quality"] = RingAtmosphereModel.HazeQuality, ["albedo"] = RingAtmosphereModel.HazeGroundAlbedo,
        ["gain"] = RingAtmosphereModel.HazeMultiscatterGain, ["sky_tint"] = RingAtmosphereModel.HazeSkyTint,
    };

    public float EnergyScale() => RingAtmosphereModel.ScatteringEnergy;

    public void SetEclipseNightScale(float scale) => RingAtmosphereModel.EclipseNightScale = scale;

    /// <summary>Packs the 16-slot Sunshine profile with the production packer (RingSunshineCloudBridge.UpdateAtmosphereProfile uses the same call).</summary>
    public Godot.Collections.Array<Vector4> BuildCloudProfile(
        double preciseRadius, Vector3 outwardWorld, Vector3 axisWorld, float cameraHeightMeters,
        float sidewallNegativeMeters, float sidewallPositiveMeters, Vector3 toSun, float solarVisibility)
    {
        var profile = new Godot.Collections.Array<Vector4>();
        profile.Resize(RingAtmosphereModel.CloudProfileSlots);
        if (!RingAtmosphereModel.Enabled) return profile;
        RingAtmosphereModel.PackCloudProfile(profile, preciseRadius, outwardWorld, axisWorld, cameraHeightMeters,
            sidewallNegativeMeters, sidewallPositiveMeters, toSun, solarVisibility, 0.0f);
        return profile;
    }
}
