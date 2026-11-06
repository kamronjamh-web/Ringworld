using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>Capture helper for tools/natural_field_strike_capture.gd (containment field WO14): the next natural field strike of a parked storm, and the weather clock.</summary>
public partial class NaturalFieldStrikeCaptureProbe : RefCounted
{
    static RingVolumetricCloudLayer? Layer(Node world) => world.GetNodeOrNull<RingVolumetricCloudLayer>("RingVolumetricClouds");

    public double Clock(Node world) => Layer(world)?.CloudSimulationTimeSeconds ?? -1.0;
    public void SetClock(Node world, double t) { if (Layer(world) is { } l) l.CloudSimulationTimeSeconds = t; }

    /// <summary>The first strike of the registered storm <paramref name="systemId"/> starting in [from, from + window), straight from the pure query (the live stream makes exactly these).</summary>
    public Godot.Collections.Dictionary NextStrike(Node world, double from, double window, int systemId, bool anySystem, double minGap = 0.0)
    {
        var result = new Godot.Collections.Dictionary { ["found"] = false };
        if (Layer(world) is not { } layer) return result;
        foreach (NaturalFieldStrike s in layer.QueryNaturalFieldStrikesInWindow(from, from + window, 1.0f))
        {
            if ((!anySystem && s.SystemId != systemId) || s.GapMeters < minGap) continue;
            result["found"] = true; result["id"] = s.Id; result["system"] = s.SystemId; result["start"] = s.StartTimeSeconds; result["duration"] = s.DurationSeconds;
            result["around"] = s.AroundMeters; result["across"] = s.AcrossMeters; result["contact"] = s.ContactAltitudeMeters; result["base"] = s.BaseAltitudeMeters;
            result["gap"] = s.GapMeters; result["wall"] = s.WallDistanceMeters; result["energy"] = s.Energy;
            return result;
        }
        return result;
    }

    /// <summary>The main channel overlay's published state: selected slots (id, field-strike flag, length), pulses and tips, segment count.</summary>
    public string ChannelInfo(Node world)
    {
        if (world.GetNodeOrNull<RingLocalWeatherEffects>("LocalWeatherEffects") is not { } fx) return "no LocalWeatherEffects";
        string d = fx.LightningChannelDebug();
        int cut = d.IndexOf(" bboxLo=", System.StringComparison.Ordinal);
        return $"bolts={fx.LightningChannelBoltCount} segs={fx.LightningChannelSegmentCount} " + (cut > 0 ? d.Substring(0, cut) : d);
    }

    /// <summary>Flagged natural events in the live list, the layer's own schedule counter, and the live strike ids.</summary>
    public string StreamInfo(Node world)
    {
        if (Layer(world) is not { } l) return "no layer";
        int flagged = 0;
        string ids = "";
        foreach (var e in l.ActiveLightningEvents) if (e.FieldStrike) { flagged++; ids += $" {e.EventId}(k{e.SystemKind})"; }
        return $"t={l.CloudSimulationTimeSeconds:0.00} flagged={flagged}{ids} scheduled_total={l.NaturalFieldStrikesScheduled}";
    }
}
