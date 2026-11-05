using System;
using System.Collections.Generic;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// The megastorm's side of the field (docs/containment-field-design.md 4.2 "Megastorm", docs/megastorm-design.md 6.3). Per frame, on the weather clock:
/// <list type="bullet">
/// <item><see cref="MegastormFieldPublisher"/> for the contact band (kind MegastormContact, only while the storm is active, at most 4/s with the bursts) and the
/// drifting updraft pulses (kind UpdraftBurst; same ids as the megastorm fixture). It needs <see cref="MegastormFieldCoupling.FieldUndersideProvider"/>, which the field
/// installs (<see cref="ContainmentFieldProvider"/>).</item>
/// <item>Far flash-field flashes starting in the window since the last update: a strong one (Energy &gt; 0.8) couples like natural lightning, with the megastorm top and
/// the gap attenuation owned here (kind Lightning, SourceId = flash id, FlashLevel = the flash event's pulse), and a discharge it makes (UpperDischarge, through the
/// same <see cref="UpperAtmosphereLightning.TryDerive"/> roll as the natural sprite layer) publishes as UpperAtmosphere with the shared
/// <see cref="UpperAtmosphereDisturbanceAdapter.SourceIdFor"/>, so a discharge reached through both adapters is one ripple.</item>
/// </list>
/// Field strikes are NOT published here: the megastorm puts them in ActiveLightningEvents with the FieldStrike flag and <see cref="LightningDisturbanceAdapter"/> publishes them.
/// Contact arcs publish nothing. Everything depends only on (model, time), so a paused clock publishes nothing new, a backwards jump re-arms and a jump over 2 s only
/// catches up the last 2 s.
/// </summary>
public sealed class MegastormDisturbanceAdapter
{
    public const double MaxCatchUpSeconds = 2.0;
    public const float StrongFlashEnergy = 0.8f, MinEnergy = 0.1f;

    private readonly Func<MegastormModel?> _model;
    private readonly IContainmentDisturbanceSink _sink;
    private readonly List<MegastormLightning.MegastormFlash> _flashes = new();
    private MegastormModel? _current;
    private MegastormFieldPublisher? _publisher;
    private double _lastTime = double.NaN;

    public ContainmentFieldProfile Profile { get; set; }
    /// <summary>User chance for discharges (null = the user setting, the same as the natural layer).</summary>
    public float? SpriteChanceScale { get; set; }
    public int PublishedLightning { get; private set; }
    public int PublishedUpper { get; private set; }
    public int Rejected { get; private set; }
    public MegastormFieldPublisher? Publisher => _publisher;

    public MegastormDisturbanceAdapter(Func<MegastormModel?> model, IContainmentDisturbanceSink sink, in ContainmentFieldProfile profile)
    {
        _model = model; _sink = sink; Profile = profile;
    }

    /// <summary>Publishes up to weather time <paramref name="t"/>; returns the number accepted by the sink.</summary>
    public int Update(double t, in RingCanonicalPosition camera)
    {
        MegastormModel? model = _model();
        if (model == null || !model.Triggered || !double.IsFinite(t)) { _lastTime = double.NaN; return 0; }
        if (!ReferenceEquals(model, _current)) { _current = model; _publisher = new MegastormFieldPublisher(model, _sink); _lastTime = double.NaN; }
        int accepted = _publisher!.Update(t, camera);
        if (double.IsNaN(_lastTime) || t < _lastTime) { _lastTime = t; return accepted; }
        double from = Math.Max(_lastTime, t - MaxCatchUpSeconds);
        if (t > from && Profile.IsValid && MegastormFieldCoupling.FieldUndersideProvider != null)
        {
            MegastormLightning.FlashesInWindow(model, from, t, _flashes);
            foreach (MegastormLightning.MegastormFlash f in _flashes) accepted += PublishFlash(model, f);
        }
        _lastTime = t;
        return accepted;
    }

    private int PublishFlash(MegastormModel model, in MegastormLightning.MegastormFlash flash)
    {
        if (MegastormFieldCoupling.FlashEvent(model, flash) is not RingLightningEvent e) return 0;
        int accepted = 0;
        if (MegastormFieldCoupling.TryDeriveSprite(model, flash, out UpperAtmosphereSprite sprite, SpriteChanceScale)
            && UpperAtmosphereDisturbanceAdapter.TryMake(Profile, sprite, out ContainmentDisturbance upper))
        {
            if (_sink.Publish(upper)) { accepted++; PublishedUpper++; } else Rejected++;
        }
        if (flash.Energy > StrongFlashEnergy)
        {
            double h = ContainmentFieldGeometry.HeightAt(Profile, flash.AcrossMeters);
            float top = model.Evaluate(flash.StartTimeSeconds).TopMeters;
            if (!double.IsNaN(h))
            {
                float energy = LightningDisturbanceAdapter.CoupledEnergy(flash.Energy, h, top);
                if (energy >= MinEnergy)
                {
                    RingLightningEvent ev = e;
                    var position = new RingCanonicalPosition(e.CloudPosition.AroundMeters, top, e.CloudPosition.AcrossMeters);
                    var d = new ContainmentDisturbance(flash.Id, ContainmentDisturbanceKind.Lightning, position, flash.StartTimeSeconds, energy, 3000.0f + 5000.0f * energy,
                        LightningDisturbanceAdapter.LightningDurationSeconds, 1.0f, t => ev.PulseAt(t), e.CloudPosition, (float)MegastormLightning.FlashRadiusMeters, top);
                    if (_sink.Publish(d)) { accepted++; PublishedLightning++; } else Rejected++;
                }
            }
        }
        return accepted;
    }
}
