using System;
using System.Reflection;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>Read-only binding assertions for the live atmosphere drawer fixture.</summary>
public partial class AtmosphereMenuSmoke : RefCounted
{
    private static readonly FieldInfo? CachedOwner = typeof(RingLiveTuningDrawer)
        .GetField("_atmosphereDefaults", BindingFlags.Instance | BindingFlags.NonPublic);

    public Godot.Collections.Dictionary VerifyBinding(RingLiveTuningDrawer drawer, RingGraphicsQualityController controller)
    {
        AtmosphereVisualDefaults? cached = CachedOwner?.GetValue(drawer) as AtmosphereVisualDefaults;
        AtmosphereVisualDefaults owner = controller.AtmosphereDefaults;
        bool sameOwner = cached != null && ReferenceEquals(cached, owner);
        bool sane = float.IsFinite(owner.Settings.Density)
            && owner.Settings.Density >= 0.0f && owner.Settings.Density <= 2.0f
            && float.IsFinite(owner.Settings.VisibilityFullKm)
            && owner.Settings.VisibilityFullKm >= 10.0f && owner.Settings.VisibilityFullKm <= 400.0f;
        return new Godot.Collections.Dictionary
        {
            ["passed"] = sameOwner && sane,
            ["same_owner"] = sameOwner,
            ["sane"] = sane,
            ["has_cached_owner"] = cached != null,
            ["cached_path"] = cached?.Path ?? string.Empty,
            ["owner_path"] = owner.Path
        };
    }
}
