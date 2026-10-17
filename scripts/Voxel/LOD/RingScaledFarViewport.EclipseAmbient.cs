using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    /// <summary>Main WorldEnvironment meta published by day_side_atmosphere.gd: the smoothed eclipse darkness weight (0 outside an eclipse).</summary>
    public const string EclipseDarknessMetaName = "ringworld_eclipse_darkness";

    /// <summary>
    /// The far and regional environments copy the main ambient once at creation and were never updated (docs/eclipse-false-sun-spec.md F12),
    /// so the opposite arch kept the ambient of load time even at totality. While an eclipse darkens the main ambient, blend each snapshot toward
    /// the live (darkened) main value by the darkness weight; at weight 0 the creation snapshot is restored exactly, so non-eclipse frames
    /// are bit-identical to before.
    /// </summary>
    private void UpdateFarEclipseAmbient()
    {
        float weight = 0.0f;
        Godot.Environment? live = null;
        if (_mainWorldEnvironment != null && GodotObject.IsInstanceValid(_mainWorldEnvironment) && _mainWorldEnvironment.HasMeta(EclipseDarknessMetaName))
        {
            weight = Mathf.Clamp(_mainWorldEnvironment.GetMeta(EclipseDarknessMetaName).AsSingle(), 0.0f, 1.0f);
            live = _mainWorldEnvironment.Environment;
        }
        if (!float.IsFinite(weight) || weight <= 0.0f || live == null)
        {
            if (!_farEclipseAmbientApplied) return;
            _farEclipseAmbientApplied = false;
            if (_farEnvironment != null) ApplyAmbient(_farEnvironment, _farAmbientSnapshotColor, _farAmbientSnapshotEnergy);
            if (_regionalFarEnvironment != null) ApplyAmbient(_regionalFarEnvironment, _regionalAmbientSnapshotColor, _regionalAmbientSnapshotEnergy);
            return;
        }
        _farEclipseAmbientApplied = true;
        if (_farEnvironment != null)
            ApplyAmbient(_farEnvironment, _farAmbientSnapshotColor.Lerp(live.AmbientLightColor, weight), Mathf.Lerp(_farAmbientSnapshotEnergy, live.AmbientLightEnergy, weight));
        if (_regionalFarEnvironment != null)
            ApplyAmbient(_regionalFarEnvironment, _regionalAmbientSnapshotColor.Lerp(live.AmbientLightColor, weight), Mathf.Lerp(_regionalAmbientSnapshotEnergy, live.AmbientLightEnergy, weight));
    }

    private static void ApplyAmbient(Godot.Environment environment, Color color, float energy)
    {
        if (environment.AmbientLightColor != color) environment.AmbientLightColor = color;
        if (environment.AmbientLightEnergy != energy) environment.AmbientLightEnergy = energy;
    }
}
