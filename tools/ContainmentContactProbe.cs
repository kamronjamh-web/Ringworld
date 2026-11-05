using Godot;
using RingworldTechDemo.ContainmentField;

/// <summary>
/// Typed access for tools/rim_wall_check.gd --contact (containment field contact ripples and WO10 audio, debug only): the body-contact adapter's counters, the
/// live BodyContact ripples on the bus and the field audio's vacuum values and whump state. Reads the real game's host; nothing is installed.
/// </summary>
public partial class ContainmentContactProbe : RefCounted
{
    /// <summary>Installs a short 220 Hz test tone loop as the field hum (runtime only; nothing is shipped). Returns false when the host is not ready.</summary>
    public bool InjectTone()
    {
        if (RingContainmentDisturbanceHost.Instance?.FieldAudio is not { } audio) return false;
        const int rate = 22050, n = rate;
        var data = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            short v = (short)(Mathf.Sin(Mathf.Tau * 220.0f * i / rate) * 12000.0f);
            data[i * 2] = (byte)(v & 0xFF); data[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        audio.SetHumStream(new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Data = data });
        return true;
    }

    public Godot.Collections.Dictionary State()
    {
        var d = new Godot.Collections.Dictionary();
        RingContainmentDisturbanceHost? host = RingContainmentDisturbanceHost.Instance;
        if (host?.BodyContactAdapter is not { } a || host.FieldAudio is not { } audio || host.Bus is not { } bus) return d;
        d["crossings"] = a.Crossings; d["published"] = a.Published; d["suppressed"] = a.Suppressed; d["rejected"] = a.Rejected;
        d["live_body"] = bus.LiveOf(ContainmentDisturbanceKind.BodyContact);
        d["weight"] = audio.Weight; d["cutoff_hz"] = audio.CutoffHz; d["gain_db"] = audio.GainDb;
        d["whumps"] = audio.WhumpCount; d["whump_playing"] = audio.WhumpPlaying; d["whump_db"] = audio.LastWhumpDb; d["whump_pitch"] = audio.LastWhumpPitch;
        d["hum_distance"] = audio.HumDistanceMeters; d["hum_pos_gain"] = audio.HumPositionalGain; d["hum_st_gain"] = audio.HumStereoGain;
        d["hum_pos_db"] = audio.HumPositionalDb; d["hum_st_db"] = audio.HumStereoDb; d["hum_playing"] = audio.HumPlaying; d["hum_pos"] = audio.HumPosition;
        d["engaged_hum_bus"] = audio.IsEngagedOn(RingContainmentFieldAudio.HumBus);
        d["engaged_thunder"] = audio.IsEngagedOn("Thunder"); d["engaged_wind"] = audio.IsEngagedOn("Wind"); d["engaged_rainbed"] = audio.IsEngagedOn("RainBed");
        if (a.LastCrossing is { } c)
        {
            d["last_speed"] = c.SpeedMetersPerSecond; d["last_radius"] = c.RadiusMeters; d["last_energy"] = c.Energy; d["last_to_vacuum"] = c.ToVacuumSide;
            d["last_across"] = c.Point.AcrossMeters; d["last_vertical"] = c.Point.VerticalMeters;
        }
        return d;
    }
}
