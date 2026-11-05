using System;
using System.Collections.Generic;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// Sprites and jets (docs/containment-field-design.md 4.2, decision 2: read-only). It derives the discharge of each shared lightning event with
/// <see cref="UpperAtmosphereLightning.TryDerive"/> and the same inputs and chance roll as <c>RingUpperAtmosphereLightningLayer</c> (the global cloud top and the chance
/// override), so a ripple appears exactly when a discharge does, and never touches the sprite. When the column [base, base + Height] contains H(across) it publishes at
/// (around, H, across) with SourceId = EventId ^ <see cref="SourceSalt"/>: jets cross anywhere, red sprites (base above 55 km, above the 40 km apex) never.
/// </summary>
public sealed class UpperAtmosphereDisturbanceAdapter
{
    /// <summary>The salt shared with the megastorm adapter, so a discharge reached through both paths is one ripple (the bus dedupes by SourceId).</summary>
    public const long SourceSalt = 0x5B17E5L;
    public const float DurationSeconds = 4.0f;

    private readonly IContainmentDisturbanceSink _sink;
    private readonly Func<float> _cloudTopMeters;
    private readonly Func<float?> _chanceScale;
    private HashSet<long> _seen = new(), _current = new();

    public ContainmentFieldProfile Profile { get; set; }
    public int Published { get; private set; }
    public int Rejected { get; private set; }

    /// <param name="cloudTopMeters">The global cloud top the layer passes to TryDerive (RingVolumetricCloudLayer.CloudTopMeters).</param>
    /// <param name="chanceScale">The layer's ChanceScaleOverride (null = the user setting).</param>
    public UpperAtmosphereDisturbanceAdapter(IContainmentDisturbanceSink sink, in ContainmentFieldProfile profile, Func<float> cloudTopMeters, Func<float?>? chanceScale = null)
    {
        _sink = sink; Profile = profile; _cloudTopMeters = cloudTopMeters; _chanceScale = chanceScale ?? (() => null);
    }

    public static long SourceIdFor(long eventId) => eventId ^ SourceSalt;

    public int Update(IReadOnlyList<RingLightningEvent> events)
    {
        int accepted = 0;
        _current.Clear();
        float top = _cloudTopMeters();
        float? scale = _chanceScale();
        for (int i = 0; i < events.Count; i++)
        {
            RingLightningEvent e = events[i];
            _current.Add(e.EventId);
            if (!_seen.Add(e.EventId)) continue;
            if (!UpperAtmosphereLightning.TryDerive(e, top, out UpperAtmosphereSprite sprite, scale)) continue;
            if (!TryMake(Profile, sprite, out ContainmentDisturbance d)) continue;
            if (_sink.Publish(d)) { accepted++; Published++; } else Rejected++;
        }
        (_seen, _current) = (_current, _seen);
        return accepted;
    }

    /// <summary>The ripple of a discharge whose column crosses the field, or false (no field at that across, column below or above the field, no energy).</summary>
    public static bool TryMake(in ContainmentFieldProfile profile, in UpperAtmosphereSprite sprite, out ContainmentDisturbance d)
    {
        d = default;
        if (!profile.IsValid || !(sprite.Energy > 0.0f)) return false;
        double h = ContainmentFieldGeometry.HeightAt(profile, sprite.BasePosition.AcrossMeters);
        if (double.IsNaN(h)) return false;
        double bottom = sprite.BasePosition.VerticalMeters, top = bottom + sprite.HeightMeters;
        if (!(h >= bottom && h <= top)) return false;
        UpperAtmosphereSprite s = sprite;
        float energy = Math.Clamp(s.Energy, 0.0f, 1.0f);
        d = new ContainmentDisturbance(SourceIdFor(s.EventId), ContainmentDisturbanceKind.UpperAtmosphere,
            new RingCanonicalPosition(s.BasePosition.AroundMeters, h, s.BasePosition.AcrossMeters), s.StartTimeSeconds, energy, 8000.0f + 12000.0f * energy, DurationSeconds, 1.0f,
            t => UpperAtmosphereLightning.Level(s, t));
        return true;
    }
}
