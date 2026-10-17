using System;
using Godot;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>
/// Tweaks T5 (docs/eclipse-false-sun-spec.md): picks the few segment lights that matter this frame and pushes them to the Sunshine bridge. The layers
/// (<see cref="EclipseHubBeamLayer"/> for beams and orbs, the relay grid layer for pulses) <see cref="Offer"/> candidates; the feeder keeps the
/// <c>limit</c> best (beams and orbs before grid links, then nearest the camera), quantises them and pushes the set only when it differs from the one
/// already pushed. Nothing here allocates: the candidate and pushed sets are fixed arrays. A steady frame (same beams, same camera set) pushes nothing.
/// </summary>
public sealed class EclipseCloudLightFeeder
{
    public const int MaxSegments = RingSunshineCloudBridge.MaximumSegmentLights;
    /// <summary>The layers re-evaluate at most this often: moving lights (orbs, pulses) are pushed at this bounded rate, steady ones only when they change.</summary>
    public const double EvaluateIntervalSeconds = 1d / 30d;
    public const int TierBeam = 0, TierGrid = 1;
    /// <summary>The beam colour: the teal of the halo, so the lit cloud reads as the beam's glow.</summary>
    public static readonly Vector3 BeamColor = new(0.55f, 0.85f, 1f);

    private struct Candidate
    {
        public SunshineSegmentLight Light;
        public int Tier;
        public double Distance;
    }

    private readonly Candidate[] _candidates = new Candidate[MaxSegments];
    private readonly SunshineSegmentLight[] _pending = new SunshineSegmentLight[MaxSegments];
    private readonly SunshineSegmentLight[] _pushed = new SunshineSegmentLight[MaxSegments];
    private int _count, _limit = MaxSegments, _pushedCount;
    private double _clock;

    /// <summary>Pushes made (a changed set each) since construction.</summary>
    public long PushCount { get; private set; }
    /// <summary>Evaluations that ran (<see cref="BeginIfDue"/> returned true).</summary>
    public long EvaluationCount { get; private set; }
    /// <summary>Segments in the set last pushed.</summary>
    public int PushedCount => _pushedCount;
    public ReadOnlySpan<SunshineSegmentLight> Pushed => _pushed.AsSpan(0, _pushedCount);
    /// <summary>Candidates kept by the current evaluation.</summary>
    public int CandidateCount => _count;

    /// <summary>Advances the evaluation clock by <paramref name="delta"/> and starts a candidate pass when one is due. False between evaluations.</summary>
    public bool BeginIfDue(double delta, int limit)
    {
        _clock += Math.Max(delta, 0d);
        if (_clock < EvaluateIntervalSeconds) return false;
        _clock = Math.Min(_clock - EvaluateIntervalSeconds, EvaluateIntervalSeconds);
        Begin(limit);
        return true;
    }

    /// <summary>Starts a candidate pass keeping at most <paramref name="limit"/> segments (1 to 8).</summary>
    public void Begin(int limit)
    {
        _count = 0;
        _limit = Math.Clamp(limit, 1, MaxSegments);
        EvaluationCount++;
    }

    /// <summary>The tier and distance a candidate must beat to be kept now (false while there is still room).</summary>
    public bool IsFull => _count >= _limit;

    /// <summary>True when a candidate of this tier and distance would be kept (a cheap early-out for the layers).</summary>
    public bool Wants(int tier, double distance)
        => _count < _limit || tier < _candidates[_count - 1].Tier || (tier == _candidates[_count - 1].Tier && distance < _candidates[_count - 1].Distance);

    /// <summary>Offers one candidate: lower tier first, then nearer to the camera. Equal keys keep the earlier offer, so the result is deterministic.</summary>
    public void Offer(in SunshineSegmentLight light, int tier, double distance)
    {
        if (!Wants(tier, distance)) return;
        int at = Math.Min(_count, _limit - 1);
        // Insertion sort into the kept prefix; the worst entry falls off the end.
        while (at > 0 && (tier < _candidates[at - 1].Tier || (tier == _candidates[at - 1].Tier && distance < _candidates[at - 1].Distance)))
        {
            _candidates[at] = _candidates[at - 1];
            at--;
        }
        _candidates[at] = new Candidate { Light = light, Tier = tier, Distance = distance };
        if (_count < _limit) _count++;
    }

    /// <summary>
    /// Quantises the kept candidates and pushes them to <paramref name="sink"/> when they differ from the set already pushed. Returns true on a push.
    /// A sink that cannot take lights yet is left alone (nothing is recorded as pushed).
    /// </summary>
    public bool Commit(ISunshineSegmentLightSink sink)
    {
        if (!sink.SegmentLightsAvailable) return false;
        bool same = _count == _pushedCount;
        for (int i = 0; i < _count; i++)
        {
            _pending[i] = Quantize(_candidates[i].Light);
            same = same && _pending[i] == _pushed[i];
        }
        if (same) return false;
        sink.SetSegmentLights(_pending.AsSpan(0, _count));
        for (int i = 0; i < _count; i++) _pushed[i] = _pending[i];
        _pushedCount = _count;
        PushCount++;
        return true;
    }

    /// <summary>Pushes an empty set when a non-empty one was pushed (the event ended, the layer hid, the player went underground). A no-op otherwise.</summary>
    public bool Clear(ISunshineSegmentLightSink? sink)
    {
        _count = 0;
        if (_pushedCount == 0) return false;
        if (sink == null || !sink.SegmentLightsAvailable) return false;
        sink.SetSegmentLights(ReadOnlySpan<SunshineSegmentLight>.Empty);
        _pushedCount = 0;
        PushCount++;
        return true;
    }

    /// <summary>Forgets what was pushed (the sink was replaced): the next commit pushes again whatever it holds.</summary>
    public void ForgetPushed()
    {
        _pushedCount = 0;
        Array.Clear(_pushed);
    }

    /// <summary>Positions to a metre and colour to 1/256: sub-metre camera or rebase jitter does not make a steady beam look changed.</summary>
    public static SunshineSegmentLight Quantize(in SunshineSegmentLight s)
        => new(Round(s.A), Round(s.B), MathF.Round(s.Radius), new Vector3(Q(s.Color.X), Q(s.Color.Y), Q(s.Color.Z)), s.Steady);

    private static Vector3 Round(Vector3 v) => new(MathF.Round(v.X), MathF.Round(v.Y), MathF.Round(v.Z));
    private static float Q(float v) => MathF.Round(Math.Max(v, 0f) * 256f) / 256f;

    /// <summary>Distance from <paramref name="point"/> to the segment a-b.</summary>
    public static double DistanceToSegment(Vector3D point, Vector3D a, Vector3D b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
        double len2 = abx * abx + aby * aby + abz * abz;
        double t = len2 > 1e-9 ? ((point.X - a.X) * abx + (point.Y - a.Y) * aby + (point.Z - a.Z) * abz) / len2 : 0d;
        t = Math.Clamp(t, 0d, 1d);
        double dx = a.X + abx * t - point.X, dy = a.Y + aby * t - point.Y, dz = a.Z + abz * t - point.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
