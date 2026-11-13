using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>How a stem ends (S5). StripEnd (a strip's end column) and Dead (reaches no outlet; never expected) exist for tools and tests. Intake and Cut exist only with the site removal options of T2R-23 (Cut: a river that joined a river that was removed at a site).</summary>
public enum RiverTerminal : byte { Sea, Lake, Confluence, Lung, StripEnd, Dead, Intake, Cut }

/// <summary>A maximal chain of channel cells (S5): at each confluence it continues up the incoming branch with the larger area.</summary>
public struct RiverStem
{
    public int NodeStart, NodeCount, KnotStart, KnotCount, ReachStart, ReachCount;
    public RiverTerminal Terminal;
    /// <summary>Lake id for Lake, receiving stem id for Confluence, -1 otherwise.</summary>
    public int TerminalId;
    /// <summary>Arc parameter on the receiving stem (Confluence only).</summary>
    public float JunctionS;
    public int HeadCell, LastCell;
}

/// <summary>A stem interval between sources, confluences, lake contacts and the sea (S5). Width, depth and speed are derived (<see cref="RiverNetwork.Sections"/>).</summary>
public struct RiverReach
{
    public int Stem;
    public float SStart, SEnd, AreaKm2, Q, S, MeanderPhase;
    public int UpstreamLake;
}

/// <summary>Knot of the stored level profile: piecewise linear and non-increasing in S (S7).</summary>
public struct RiverKnot
{
    public float S, W;
}

/// <summary>Channel and valley values of a stem at one arc parameter (S6, S7), as <see cref="RiverNetwork.ChannelAt"/> returns them: blended along the stem, without any footprint.</summary>
public readonly record struct RiverChannel(int Reach, double HalfWidth, double BankWidth, double Depth, double EdgeDepth, double Floodplain, double ValleySlope, double MeanderAmplitude, double MeanderWavelength, int Span = -1);

/// <summary>
/// The T2R-5 river data (docs/work-orders/terrain-vegetation/T2.md, S5 to S7): nodes (decimetres), stems, reaches and level knots, plus what is derived from
/// them at load (stem curves, sections, tail levels). T2R-6 wraps this into the graph file. The centreline of a stem at arc parameter s is the base
/// B-spline plus the analytic meander.
/// </summary>
public sealed class RiverNetwork
{
    public long CircumferenceM { get; }
    public double WidthM { get; }
    public long Seed { get; }
    public double MeanderStrength { get; }
    public double ValleyWidth { get; }
    public double ChannelDepth { get; }
    public double Ferocity { get; }

    public int[] NodeAroundDm { get; }
    public int[] NodeAcrossDm { get; }
    public RiverStem[] Stems { get; }
    public RiverReach[] Reaches { get; }
    public RiverKnotTable Knots { get; internal set; } = RiverKnotTable.Empty;
    /// <summary>The path section of each reach (Q and the reach gradient S): it feeds the wander, the meander amplitude and wavelength and the confluence tail. Channel and valley values come from <see cref="SpanTable"/>.</summary>
    public RiverSection[] Sections { get; }
    /// <summary>T3R-3 (E1): the spans with their calibrated channel and danger values. Null until installed (a network being built reads the path sections, which the level build needs unchanged).</summary>
    public RiverSpanTable? SpanTable { get; private set; }
    public RiverStemCurve[] Curves { get; }
    /// <summary>Level of the receiving water at the end of each stem (tail rule, S7), NaN when the stem has no tail.</summary>
    public double[] ReceivingLevel { get; internal set; }
    /// <summary>T of the tail rule per stem.</summary>
    public double[] TailMeters { get; internal set; }
    /// <summary>Residual drop of the tail rule per stem: the level before the tail at <c>s_end - T</c> minus the receiving level, at least 0 (T2R-5b).</summary>
    public double[] TailDrop { get; internal set; }
    /// <summary>Stem arc length where the metrics polyline ends: the half segment into water is dropped (the D4 baseline's clipping rule).</summary>
    public double[] MetricsEnd { get; }
    /// <summary>Level stations per stem, only when the build asked for them.</summary>
    public RiverStemStations[]? Stations { get; internal set; }
    /// <summary>T2R-8b: arc length up to which a stem that starts inside a lake runs through its water (0 for the others). Set by the bake only (not stored in the graph file); the metrics and the cut statistics leave these parts out.</summary>
    public double[] InLakeEnd { get; internal set; } = Array.Empty<double>();

    public RiverNetwork(long circumference, double widthM, long seed, double meander, double valleyWidth, double channelDepth, double ferocity,
        int[] aroundDm, int[] acrossDm, RiverStem[] stems, RiverReach[] reaches)
    {
        CircumferenceM = circumference; WidthM = widthM; Seed = seed;
        MeanderStrength = meander; ValleyWidth = valleyWidth; ChannelDepth = channelDepth; Ferocity = ferocity;
        NodeAroundDm = aroundDm; NodeAcrossDm = acrossDm; Stems = stems; Reaches = reaches;
        Curves = new RiverStemCurve[stems.Length];
        MetricsEnd = new double[stems.Length];
        _meanderNoise = new double[stems.Length * 4];
        for (int i = 0; i < _meanderNoise.Length; i++) _meanderNoise[i] = 2 * Math.PI * RiverHash.Unit(seed, i / 4, 11 + (i & 3), 4);
        for (int i = 0; i < stems.Length; i++)
        {
            Curves[i] = BuildCurve(this, stems[i]);
            bool water = stems[i].Terminal is RiverTerminal.Sea or RiverTerminal.Lake or RiverTerminal.Lung or RiverTerminal.StripEnd;
            RiverStemCurve c = Curves[i];
            MetricsEnd[i] = water && c.Pieces >= 2 ? c.S0[c.Pieces - 1] : c.Length;
        }
        Sections = new RiverSection[reaches.Length];
        for (int r = 0; r < reaches.Length; r++) Sections[r] = RiverHydraulics.PathSection(reaches[r].Q, reaches[r].S, channelDepth, ferocity);
        BuildChannels();
        ReceivingLevel = new double[stems.Length];
        TailMeters = new double[stems.Length];
        TailDrop = new double[stems.Length];
    }

    /// <summary>Shortest signed difference of two wrapped around positions, in metres.</summary>
    public double WrapDelta(double delta)
    {
        double c = CircumferenceM;
        delta %= c;
        if (delta > c / 2) delta -= c; else if (delta < -c / 2) delta += c;
        return delta;
    }

    private static RiverStemCurve BuildCurve(RiverNetwork net, RiverStem stem)
    {
        int n = stem.NodeCount;
        var x = new double[n];
        var y = new double[n];
        double originAround = net.NodeAroundDm[stem.NodeStart] / 10d, originAcross = net.NodeAcrossDm[stem.NodeStart] / 10d;
        for (int k = 1; k < n; k++)
        {
            double delta = net.WrapDelta((net.NodeAroundDm[stem.NodeStart + k] - net.NodeAroundDm[stem.NodeStart + k - 1]) / 10d);
            x[k] = x[k - 1] + delta;
            y[k] = net.NodeAcrossDm[stem.NodeStart + k] / 10d - originAcross;
        }
        return new RiverStemCurve(originAround, originAcross, x, y);
    }

    public int ReachAt(int stem, double s)
    {
        RiverStem st = Stems[stem];
        int lo = st.ReachStart, hi = st.ReachStart + st.ReachCount - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (Reaches[mid].SStart <= s) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    // Per-reach channel and valley values (S6), derived once from Sections. ChannelAt is the only reader (T2R-7 review, for T3R-3: spans replace reaches behind it).
    private double[] _chA = Array.Empty<double>(), _chB = Array.Empty<double>(), _chDepth = Array.Empty<double>(), _chEdge = Array.Empty<double>(), _chFlood = Array.Empty<double>(),
        _chSlope = Array.Empty<double>(), _chAmp = Array.Empty<double>(), _chLambda = Array.Empty<double>(), _chAmpMax = Array.Empty<double>(), _chStretch = Array.Empty<double>(),
        _chK = Array.Empty<double>();

    /// <summary>The lateral limit <see cref="StemLateralReach"/> is measured for (the operator's fade end); larger limits extend it by the stem's largest stretch.</summary>
    private const double StemReachLimitMeters = 300d;
    private const double StemReachStepMeters = 5d;
    private volatile double[]? _stemReach, _stemStretch;
    private readonly object _stemReachLock = new();

    private void BuildChannels()
    {
        int n = Reaches.Length;
        _chA = new double[n]; _chB = new double[n]; _chDepth = new double[n]; _chEdge = new double[n]; _chFlood = new double[n];
        _chSlope = new double[n]; _chAmp = new double[n]; _chLambda = new double[n]; _chAmpMax = new double[n]; _chStretch = new double[n]; _chK = new double[n];
        for (int r = 0; r < n; r++)
        {
            RiverSection sec = Sections[r];
            _chA[r] = RiverHydraulics.HalfWidth(sec.Width);
            _chB[r] = RiverHydraulics.BankWidth(sec.Width);
            _chDepth[r] = sec.Depth;
            _chEdge[r] = RiverHydraulics.EdgeDepth(sec.Depth);
            _chFlood[r] = RiverHydraulics.FloodplainWidth(sec.Width, sec.Gradient, ValleyWidth);
            _chSlope[r] = RiverHydraulics.ValleySlope(sec.Gradient, ValleyWidth);
            _chAmp[r] = RiverHydraulics.MeanderAmplitude(sec.Width, MeanderStrength, sec.Gradient);
            _chLambda[r] = RiverHydraulics.MeanderWavelength(sec.Width);
            _chK[r] = 2d * Math.PI / _chLambda[r];
        }
        for (int r = 0; r < n; r++)
        {
            // Bounds of the meander within the reach, blend included: |D| <= ampMax, and |D'| <= 11.2 max(A k) / (2 pi) + 0.0075 |A_up - A| (the envelope, the phase and
            // both tapers of Meander, plus the 200 m blend of amplitude and wavenumber; A k is a product of two blends, so its largest value may lie inside the blend).
            bool blended = r > Stems[Reaches[r].Stem].ReachStart;
            double up = blended ? _chAmp[r - 1] : _chAmp[r], kUp = blended ? _chK[r - 1] : _chK[r];
            _chAmpMax[r] = Math.Max(_chAmp[r], up);
            double da = up - _chAmp[r], dk = kUp - _chK[r];
            double ak = Math.Max(_chAmp[r] * _chK[r], up * kUp);
            if (da * dk < 0d)
            {
                double u = -(_chAmp[r] * dk + _chK[r] * da) / (2d * da * dk);
                if (u > 0d && u < 1d) ak = Math.Max(ak, (_chAmp[r] + da * u) * (_chK[r] + dk * u));
            }
            double slope = 11.2 * ak / (2d * Math.PI) + 1.5 / RiverHydraulics.BlendMeters * Math.Abs(da);
            _chStretch[r] = Math.Sqrt(1d + slope * slope);
        }
    }

    /// <summary>Installs the spans (T3R-3). From then on <see cref="ChannelAt"/> reads channel and valley values from them; the meander still comes from the path sections.</summary>
    internal void InstallSpans(RiverSpanTable table) => SpanTable = table;

    /// <summary>
    /// The one accessor for channel and valley values of a stem at s (S6, S7, E1): the span's values blended from the upstream span over the first 200 m (across reach boundaries
    /// too; the first span of a stem does not blend). The meander amplitude and wavelength are the path section's, blended per reach. Callers never read <see cref="Sections"/> for geometry.
    /// Before spans are installed (the level build) it returns the path section's values.
    /// </summary>
    public void ChannelAt(int stem, double s, out RiverChannel channel)
    {
        PathChannelAt(stem, s, out channel);
        RiverSpanTable? table = SpanTable;
        if (table == null) return;
        int i = table.SpanAt(channel.Reach, s);
        double a = table.HalfWidth[i], b = table.BankWidth[i], depth = table.Depth[i], edge = table.EdgeDepth[i], flood = table.Floodplain[i], slope = table.ValleySlope[i];
        if (i > table.ReachFirst[Stems[stem].ReachStart])
        {
            double t = RiverHydraulics.Smoothstep(table.Spans[i].SStart, table.Spans[i].SStart + RiverHydraulics.BlendMeters, s);
            if (t < 1d)
            {
                int up = i - 1;
                a += (table.HalfWidth[up] - a) * (1 - t);
                b += (table.BankWidth[up] - b) * (1 - t);
                depth += (table.Depth[up] - depth) * (1 - t);
                edge += (table.EdgeDepth[up] - edge) * (1 - t);
                flood += (table.Floodplain[up] - flood) * (1 - t);
                slope += (table.ValleySlope[up] - slope) * (1 - t);
            }
        }
        double taper = HeadTaperOverride >= 0d ? HeadTaperOverride : RiverHydraulics.HeadTaperMeters;
        if (taper > 0d && s < taper && IsSource(stem))
        {
            double t = Math.Min(taper, Curves[stem].Length);
            if (s < t)
            {
                // RS4: the span's law at a discharge growing as x^(5/3): depth x^(2/3), width x^(5/9), speed x^(4/9). Bank, edge and floodplain follow through the same statics as RiverSpanTable.Create.
                double x = Math.Max(0d, s) / t;
                depth *= x <= 0d ? 0d : Math.Cbrt(x * x);
                a *= x <= 0d ? 0d : Math.Pow(x, 5d / 9d);
                double w = 2d * a;
                b = RiverHydraulics.BankWidth(w);
                edge = RiverHydraulics.EdgeDepth(depth);
                flood = RiverHydraulics.FloodplainWidth(w, Math.Max(table.Spans[i].Sh, 1e-4f), ValleyWidth);
            }
        }
        channel = new RiverChannel(channel.Reach, a, b, depth, edge, flood, slope, channel.MeanderAmplitude, channel.MeanderWavelength, i);
    }

    /// <summary>T2R-21: test hook for the head taper length (negative: use <see cref="RiverHydraulics.HeadTaperMeters"/>; 0 switches the taper off).</summary>
    internal double HeadTaperOverride = -1d;

    /// <summary>True when the stem starts at a source: its first reach has no upstream lake (lake outlets, in-lake stems and overflow outlets are not sources).</summary>
    public bool IsSource(int stem) => Reaches[Stems[stem].ReachStart].UpstreamLake < 0;

    /// <summary>Half width and bank width of the path section at s (the reach values blended as before spans existed): the level build and the confluence tails use these, so the stored profile and the displayed level do not depend on the spans.</summary>
    public void PathGeometry(int stem, double s, out double a, out double b)
    {
        PathChannelAt(stem, s, out RiverChannel c);
        a = c.HalfWidth; b = c.BankWidth;
    }

    private void PathChannelAt(int stem, double s, out RiverChannel channel)
    {
        int reach = ReachAt(stem, s);
        double a = _chA[reach], b = _chB[reach], depth = _chDepth[reach], edge = _chEdge[reach], flood = _chFlood[reach], slope = _chSlope[reach], amplitude = _chAmp[reach], k = _chK[reach];
        if (reach > Stems[stem].ReachStart)
        {
            double t = RiverHydraulics.Smoothstep(Reaches[reach].SStart, Reaches[reach].SStart + RiverHydraulics.BlendMeters, s);
            int up = reach - 1;
            a += (_chA[up] - a) * (1 - t);
            b += (_chB[up] - b) * (1 - t);
            depth += (_chDepth[up] - depth) * (1 - t);
            edge += (_chEdge[up] - edge) * (1 - t);
            flood += (_chFlood[up] - flood) * (1 - t);
            slope += (_chSlope[up] - slope) * (1 - t);
            amplitude += (_chAmp[up] - amplitude) * (1 - t);
            k += (_chK[up] - k) * (1 - t);
        }
        channel = new RiverChannel(reach, a, b, depth, edge, flood, slope, amplitude, 2d * Math.PI / k);
    }

    /// <summary>
    /// The largest distance from the base curve at which a point can still have a lateral coordinate |t| below <paramref name="lateralLimit"/> anywhere in the reach
    /// (the meander shifts and stretches t). Beyond it the operator skips the piece before any meander work.
    /// </summary>
    public double LateralReach(int reach, double lateralLimit) => _chAmpMax[reach] + lateralLimit * _chStretch[reach] + 2d;

    /// <summary>
    /// The true lateral reach of the stem (T2R-7b): the largest distance from the base curve at which a point can have |t| below <paramref name="lateralLimit"/>, which is
    /// <c>|D| + limit sqrt(1 + D'^2)</c> at its worst along the stem, measured every 5 m through <see cref="ChannelAt"/> and <see cref="MeanderWithSlope"/> (the per-reach
    /// <see cref="LateralReach"/> bound is a loose envelope, up to 940 m). Measured for 300 m and an upper bound above it (the stem's largest stretch for the extra distance).
    /// Needs the meander phases, so it is built on first use, once, and the operator asks for it in its constructor.
    /// </summary>
    public double StemLateralReach(int stem, double lateralLimit)
    {
        if (_stemReach == null) BuildStemReach();
        return _stemReach![stem] + Math.Max(0d, lateralLimit - StemReachLimitMeters) * _stemStretch![stem];
    }

    private void BuildStemReach()
    {
        lock (_stemReachLock)
        {
            if (_stemReach != null) return;
            var reach = new double[Stems.Length];
            var stretch = new double[Stems.Length];
            // Stems are independent and every read is immutable, so the stems are measured in parallel (the result is the same values; this was a 5 s main-thread stall on the first sample).
            Parallel.For(0, Stems.Length, i =>
            {
                double length = Curves[i].Length, best = 0, bestStretch = 1;
                for (double s = 0; ; s += StemReachStepMeters)
                {
                    if (s > length) s = length;
                    ChannelAt(i, s, out RiverChannel c);
                    double d = MeanderWithSlope(i, s, in c, out double slope), st = Math.Sqrt(1d + slope * slope);
                    best = Math.Max(best, Math.Abs(d) + StemReachLimitMeters * st);
                    bestStretch = Math.Max(bestStretch, st);
                    if (s >= length) break;
                }
                reach[i] = best; stretch[i] = bestStretch;
            });
            _stemStretch = stretch;
            _stemReach = reach;
        }
    }

    /// <summary>Half width a, bank width b, meander amplitude and wavelength at s, blended from the upstream reach over the first 200 m of a reach (S6, T2R-7b: the wavelength too).</summary>
    public void Geometry(int stem, double s, out int reach, out double a, out double b, out double amplitude, out double wavelength)
    {
        ChannelAt(stem, s, out RiverChannel c);
        reach = c.Reach; a = c.HalfWidth; b = c.BankWidth; amplitude = c.MeanderAmplitude; wavelength = c.MeanderWavelength;
    }

    /// <summary>
    /// Lateral meander displacement D(s) (S7), left positive. Tuned in T2R-5b and T2R-7b: the chained phase theta is continuous along the stem, its wavenumber
    /// k = 2 pi / lambda blends from the upstream reach over the same 200 m as the amplitude (theta = MeanderPhase_r + k_r (s - SStart_r) + (k_up - k_r) 200 (x - x^3 + x^4 / 2),
    /// x the blend coordinate), and both the wavelength and the amplitude are modulated by slow functions of theta with hashed phases, so the
    /// meander is not a constant sine: <c>D = A_m * E(theta) * sin(theta + 0.6 sin(theta / 2.7 + psi1) + 0.3 sin(theta / 4.6 + psi2)) * tau</c> with the
    /// envelope <c>E = smoothstep(-0.7, 0.7, 0.6 sin(theta / 3.3 + psi3) + 0.4 sin(theta / 7.7 + psi4))</c> in [0, 1]. The local wavelength stays between
    /// about 0.78 and 1.4 lambda (d phase / d theta in [0.71, 1.29]), and |D| &lt;= A_m, so the cap holds.
    /// </summary>
    public double Meander(int stem, double s)
    {
        ChannelAt(stem, s, out RiverChannel c);
        return Meander(stem, s, in c);
    }

    /// <summary>As <see cref="Meander(int, double)"/> with the channel values at s already fetched by <see cref="ChannelAt"/>.</summary>
    public double Meander(int stem, double s, in RiverChannel channel)
    {
        int reach = channel.Reach;
        double amplitude = channel.MeanderAmplitude, wavelength = channel.MeanderWavelength;
        double tau = RiverHydraulics.Smoothstep(0, 2 * wavelength, s);
        if (Stems[stem].Terminal == RiverTerminal.Confluence) tau *= RiverHydraulics.Smoothstep(0, 2 * wavelength, Curves[stem].Length - s);
        if (amplitude == 0 || tau == 0) return 0;
        double theta = Phase(reach, s, Reaches[reach].MeanderPhase, out _, out _);
        int o = stem * 4;
        double phase = theta + 0.6 * Math.Sin(theta / 2.7 + _meanderNoise[o]) + 0.3 * Math.Sin(theta / 4.6 + _meanderNoise[o + 1]);
        double envelope = RiverHydraulics.Smoothstep(-0.7, 0.7, 0.6 * Math.Sin(theta / 3.3 + _meanderNoise[o + 2]) + 0.4 * Math.Sin(theta / 7.7 + _meanderNoise[o + 3]));
        return amplitude * envelope * Math.Sin(phase) * tau;
    }

    /// <summary>
    /// D and dD/ds from one evaluation (T2R-7b): the same value as <see cref="Meander(int, double, in RiverChannel)"/> (to rounding), and the exact derivative of it, including the
    /// blends of amplitude and wavenumber and both tapers (whose wavelength is the blended one). The operator uses it for the stretch and the tangent of the displaced centreline.
    /// </summary>
    public double MeanderWithSlope(int stem, double s, in RiverChannel channel, out double slope)
    {
        slope = 0;
        int reach = channel.Reach;
        double amplitude = channel.MeanderAmplitude, lambda = channel.MeanderWavelength;
        double theta = Phase(reach, s, Reaches[reach].MeanderPhase, out double k, out double dk);
        double dLambda = -lambda * dk / k, twoLambda = 2 * lambda;
        // Tapers: x = a / (2 lambda(s)) with a = s (start) and a = length - s (confluence end).
        double x1 = Math.Clamp(s / twoLambda, 0d, 1d);
        double tau = x1 * x1 * (3d - 2d * x1), dTau = 0d;
        if (x1 > 0d && x1 < 1d) dTau = 6d * x1 * (1d - x1) * (1d - s * dLambda / lambda) / twoLambda;
        if (Stems[stem].Terminal == RiverTerminal.Confluence)
        {
            double a = Curves[stem].Length - s, x2 = Math.Clamp(a / twoLambda, 0d, 1d), f2 = x2 * x2 * (3d - 2d * x2), df2 = 0d;
            if (x2 > 0d && x2 < 1d) df2 = 6d * x2 * (1d - x2) * (-1d - a * dLambda / lambda) / twoLambda;
            dTau = dTau * f2 + tau * df2;
            tau *= f2;
        }
        if (amplitude == 0 || tau == 0) return 0;
        // Amplitude blend derivative.
        double dAmp = 0d;
        if (reach > Stems[stem].ReachStart)
        {
            double x = (s - Reaches[reach].SStart) / RiverHydraulics.BlendMeters;
            if (x > 0d && x < 1d) dAmp = -(_chAmp[reach - 1] - _chAmp[reach]) * 6d * x * (1d - x) / RiverHydraulics.BlendMeters;
        }
        int o = stem * 4;
        (double s1, double c1) = Math.SinCos(theta / 2.7 + _meanderNoise[o]);
        (double s2, double c2) = Math.SinCos(theta / 4.6 + _meanderNoise[o + 1]);
        (double s3, double c3) = Math.SinCos(theta / 3.3 + _meanderNoise[o + 2]);
        (double s4, double c4) = Math.SinCos(theta / 7.7 + _meanderNoise[o + 3]);
        double phase = theta + 0.6 * s1 + 0.3 * s2;
        (double sp, double cp) = Math.SinCos(phase);
        double y = (0.6 * s3 + 0.4 * s4 + 0.7) / 1.4;
        double envelope = RiverHydraulics.Smoothstep(0d, 1d, y), dEnvelope = 0d;
        if (y > 0d && y < 1d) dEnvelope = 6d * y * (1d - y) / 1.4 * (0.6 / 3.3 * c3 + 0.4 / 7.7 * c4);
        double dPhase = 1d + 0.6 / 2.7 * c1 + 0.3 / 4.6 * c2;
        // dtheta/ds = k.
        slope = dAmp * envelope * sp * tau + amplitude * dEnvelope * k * sp * tau + amplitude * envelope * cp * dPhase * k * tau + amplitude * envelope * sp * dTau;
        return amplitude * envelope * sp * tau;
    }

    /// <summary>
    /// The chained meander phase of a reach at s (T2R-7b) and the wavenumber k and its derivative there: k blends from the upstream reach over the first 200 m with the amplitude's
    /// smoothstep, so the phase is <c>base + k_r (s - SStart) + (k_up - k_r) 200 (x - x^3 + x^4 / 2)</c>. The builder chains the next reach's base from the end value.
    /// </summary>
    internal double Phase(int reach, double s, double basePhase, out double k, out double dk)
    {
        double kr = _chK[reach], ds = s - Reaches[reach].SStart;
        k = kr; dk = 0d;
        double theta = basePhase + kr * ds;
        if (reach > Stems[Reaches[reach].Stem].ReachStart)
        {
            double x = Math.Clamp(ds / RiverHydraulics.BlendMeters, 0d, 1d), dKUp = _chK[reach - 1] - kr;
            k = kr + dKUp * (1d - x * x * (3d - 2d * x));
            if (x > 0d && x < 1d) dk = -dKUp * 6d * x * (1d - x) / RiverHydraulics.BlendMeters;
            theta += dKUp * RiverHydraulics.BlendMeters * (x - x * x * x + 0.5 * x * x * x * x);
        }
        return theta;
    }

    private readonly double[] _meanderNoise;

    /// <summary>The displaced centreline at s: ring around position unwrapped from the stem's first node, across, unit tangent of the base curve.</summary>
    public void Centre(int stem, double s, out double around, out double across, out double tx, out double ty)
    {
        RiverStemCurve c = Curves[stem];
        c.Eval(s, out double x, out double y, out tx, out ty);
        double d = Meander(stem, s);
        around = c.OriginAround + x - ty * d;
        across = c.OriginAcross + y + tx * d;
    }

    /// <summary>Position and unit normal (left positive) of the displaced centreline, the normal from a short central difference so it follows the meander.</summary>
    public void CentreFrame(int stem, double s, out double around, out double across, out double nx, out double ny)
    {
        Centre(stem, s, out around, out across, out _, out _);
        double length = Curves[stem].Length;
        double s0 = Math.Max(0, s - 2), s1 = Math.Min(length, s + 2);
        Centre(stem, s0, out double a0, out double c0, out _, out _);
        Centre(stem, s1, out double a1, out double c1, out _, out _);
        double dx = a1 - a0, dy = c1 - c0, norm = Math.Sqrt(dx * dx + dy * dy);
        if (norm < 1e-9) { Curves[stem].Eval(s, out _, out _, out double tx, out double ty); dx = tx; dy = ty; norm = 1; }
        nx = -dy / norm; ny = dx / norm;
    }

    public double WrapAround(double around)
    {
        double c = CircumferenceM;
        around %= c;
        return around < 0 ? around + c : around;
    }
}

public sealed class RiverBuildOptions
{
    /// <summary>Channel threshold in km^2 of unweighted area. Null reads <c>100 / river_density</c>.</summary>
    public double? ThresholdKm2 { get; init; }
    /// <summary>Compute the level profiles (needs <see cref="Source"/>). Off for path-shape-only runs.</summary>
    public bool Levels { get; init; } = true;
    public IDrainageHeightSource? Source { get; init; }
    /// <summary>Multiplies the wander amplitude (the tuning knob of T2R-5; 1 is the spec).</summary>
    public double WanderScale { get; init; } = 1d;
    /// <summary>Multiplies the meander amplitude (tools: 0 scores the wander alone; 1 is the spec).</summary>
    public double MeanderScale { get; init; } = 1d;
    /// <summary>Ease hairpins of the node chain before the wander (T2R-5b; tools switch it off for comparison).</summary>
    public bool EaseHairpins { get; init; } = true;
    /// <summary>T2R-5c: Gaussian smoothing of the node chain on gentle ground (tools switch it off for comparison).</summary>
    public bool Smooth { get; init; } = true;
    /// <summary>Keep every centre curve out of barrier clearance (T2R-6b review; on in production).</summary>
    public bool BarrierGuard { get; init; } = true;
    /// <summary>T2R-5c: snap nodes to the valley floor within their cell's 16 samples (needs <see cref="Source"/>; tools switch it off for comparison).</summary>
    public bool Snap { get; init; } = false; // T2R-5c primary review: off by default. It did not reduce cut depth (17.5 % over 15 m either way) and it raised coastal sharp bends.
    /// <summary>T2R-8b: drowned channels through the lakes (lake channel cells, outlet leads, unjittered lake nodes, stems that break at the lake entry). False is the T2R-8 state, for the negative control of river_carve_invariants_test and drainage_graph_test.</summary>
    public bool LakeChannels { get; init; } = true;
    /// <summary>T2R-8b: the in-lake stem of a chain of marked cells starts at its first deep cell (lowest sample 2 m under the lake level); the shallower cells before it are plain channel cells of the river that enters. False starts it at the entry cell (the work order's literal item 3: 12 % of the ends then stand on ground above the lake level, and inlet counts equal the T2R-8 graph's).</summary>
    public bool DeepEntries { get; init; } = true;
    /// <summary>T2R-23 (RS2): which rivers a site removes. None in production (Part A only measures it; the report tool sets it together with <see cref="DrainageBakeOptions.SiteIntakes"/>).</summary>
    public RiverSiteRemovalRule SiteRemoval { get; init; } = RiverSiteRemovalRule.None;
    public int Workers { get; init; } = 4;
    /// <summary>Keep the level stations on the network (tests and reports).</summary>
    public bool KeepStations { get; init; }
}

/// <summary>What the builder measured about itself (the bake report prints it).</summary>
public sealed record RiverBuildReport
{
    public double ThresholdKm2 { get; init; }
    public int ChannelCells { get; init; }
    public int Stems { get; init; }
    public int Reaches { get; init; }
    public int Nodes { get; init; }
    public int Knots { get; init; }
    public int Stations { get; init; }
    public double LengthKm { get; init; }
    public int TerminalSea { get; init; }
    public int TerminalLake { get; init; }
    public int TerminalConfluence { get; init; }
    public int TerminalLung { get; init; }
    public int TerminalStripEnd { get; init; }
    public int TerminalDead { get; init; }
    /// <summary>T2R-19: stems that end at a site intake cell (always 0 with the option off).</summary>
    public int TerminalIntake { get; init; }
    /// <summary>T2R-23: stems that end where the river they joined was removed (rule Branch), and what the removal took (zero with the option off).</summary>
    public int TerminalCut { get; init; }
    public int RemovedStems { get; init; }
    public int RemovedCells { get; init; }
    public double[] Q { get; init; } = Array.Empty<double>();
    public double[] Gradient { get; init; } = Array.Empty<double>();
    public double[] Depth { get; init; } = Array.Empty<double>();
    public double[] Width { get; init; } = Array.Empty<double>();
    public double[] Speed { get; init; } = Array.Empty<double>();
    public double[] ClassShare { get; init; } = Array.Empty<double>();
    public double CutP50 { get; init; }
    public double CutP90 { get; init; }
    public double CutMax { get; init; }
    public double CutOver40Share { get; init; }
    public double FillShareOfLevel { get; init; }
    public double MaxWanderMeters { get; init; }
    public double MaxMeanderMeters { get; init; }
    /// <summary>Largest distance of a final node from its eased position (smoothing, wander and snap together; at most the 350 m cap).</summary>
    public double MaxNodeShiftMeters { get; init; }
    public double MaxSmoothMeters { get; init; }
    public long SnappedNodes { get; init; }
    /// <summary>Nodes moved by the barrier guard (T2R-6b review) and the largest total move of one node.</summary>
    public long GuardMovedNodes { get; init; }
    public double GuardMaxMeters { get; init; }
    public double CutOver15Share { get; init; }
    /// <summary>T2R-8b: lake channel cells (flow chains and outlet leads), outlet leads, stems that start inside a lake, the length of their in-lake parts and how much of it is cut over 40 m (the cut statistics above leave these parts out).</summary>
    public int LakeChannelCells { get; init; }
    public int LakeLeads { get; init; }
    public int InLakeStems { get; init; }
    public double InLakeKm { get; init; }
    public double InLakeCutOver40Share { get; init; }
    public double InLakeCutMax { get; init; }
    /// <summary>T2R-22 8f: junctions of a stem that is lake water to its end into a receiver (not held by a lake), how many of them made a fall in the receiver, and the numbers of that fall.</summary>
    public int LakeEndJoins { get; init; }
    public int LakeEndLowered { get; init; }
    public string LakeEndText { get; init; } = "";
    /// <summary>T3R-3: spans, landings (spans and km), spans inside an exclusion zone, the zone count and the seconds the span step took (cut, gradients, exclusions, sections, landings).</summary>
    public int Spans { get; init; }
    public int SpanLandings { get; init; }
    public double SpanLandingKm { get; init; }
    public int SpanExcluded { get; init; }
    public int ExclusionZones { get; init; }
    public double SpanSeconds { get; init; }
    public double CentrelineSeconds { get; init; }
    public double LevelSeconds { get; init; }
    public double SampleSeconds { get; init; }

    public string ToText()
    {
        string P(double[] v) => v.Length == 3 ? FormattableString.Invariant($"{v[0]:G4}/{v[1]:G4}/{v[2]:G4}") : "-";
        return string.Join("\n", new[]
        {
            FormattableString.Invariant($"rivers: threshold {ThresholdKm2:F0} km^2, channel cells {ChannelCells}, stems {Stems}, reaches {Reaches}, nodes {Nodes}, knots {Knots}, stations {Stations}, length {LengthKm:F0} km"),
            $"terminals: sea {TerminalSea}, lake {TerminalLake}, confluence {TerminalConfluence}, lung {TerminalLung}, strip end {TerminalStripEnd}, dead {TerminalDead}" + (TerminalIntake > 0 ? $", intake {TerminalIntake}" : "") + (TerminalCut > 0 ? $", cut {TerminalCut}" : "") + (RemovedStems > 0 ? $" (site removal took {RemovedStems} stems, {RemovedCells} cells)" : ""),
            "hydraulics p10/p50/p90 over reach length: Q " + P(Q) + " m3/s, S " + P(Gradient) + ", d " + P(Depth) + " m, w " + P(Width) + " m, U " + P(Speed) + " m/s",
            FormattableString.Invariant($"class share by length: calm {ClassShare.ElementAtOrDefault(0):P1}, swift {ClassShare.ElementAtOrDefault(1):P1}, fierce {ClassShare.ElementAtOrDefault(2):P1}, torrent {ClassShare.ElementAtOrDefault(3):P1}"),
            FormattableString.Invariant($"lake channels: {LakeChannelCells} marked cells ({LakeLeads} outlet leads), {InLakeStems} stems start inside a lake, in-lake length {InLakeKm:F1} km, cut over 40 m on {InLakeCutOver40Share:P2} of it (largest {InLakeCutMax:F1} m)"),
            FormattableString.Invariant($"lake-end joins (T2R-22 8f): {LakeEndJoins} stems that are lake water to their end join a stem; {LakeEndLowered} receivers were lowered to the lake level at the junction; {LakeEndText}"),
            FormattableString.Invariant($"spans {Spans}, landings {SpanLandings} ({SpanLandingKm:F0} km), spans inside an exclusion zone {SpanExcluded} ({ExclusionZones} zones), span step {SpanSeconds:F2} s"),
            FormattableString.Invariant($"cut depth along paths (ground minus bed): p50 {CutP50:F1} m, p90 {CutP90:F1} m, max {CutMax:F1} m, share of length over 40 m {CutOver40Share:P2}, over 15 m {CutOver15Share:P2}; level filled up to a hold or the receiving level on {FillShareOfLevel:P1} of stations"),
            FormattableString.Invariant($"largest wander shift {MaxWanderMeters:F0} m, meander amplitude cap used {MaxMeanderMeters:F0} m, largest node shift from the eased chain {MaxNodeShiftMeters:F0} m (smoothing {MaxSmoothMeters:F0} m), snapped nodes {SnappedNodes}, barrier guard moved {GuardMovedNodes} nodes (largest {GuardMaxMeters:F0} m); seconds: centrelines {CentrelineSeconds:F2}, ground sampling {SampleSeconds:F2}, levels {LevelSeconds:F2}"),
        });
    }
}

/// <summary>
/// T2R-5 (docs/work-orders/terrain-vegetation/T2.md, S5 to S7): channels, stems, reaches, centrelines and level profiles from a <see cref="DrainageField"/>.
/// </summary>
public static class RiverGraphBuilder
{
    private const double JitterMeters = 60d;

    private sealed class StemTmp
    {
        public int[] Cells = Array.Empty<int>();
        public RiverTerminal Terminal;
        public int TerminalId = -1;
        public int ReceiverStem = -1;
        public int ReceiverNodeIndex;
        public int[] Dm = Array.Empty<int>();
        public int[] AcrossDm = Array.Empty<int>();
        public float JunctionS;
        public RiverStemCurve Curve = null!;
        public int[] ReachOfCell = Array.Empty<int>();
        public double[] X = Array.Empty<double>(), Y = Array.Empty<double>(), OX = Array.Empty<double>(), OY = Array.Empty<double>();
        public double OriginAround, OriginAcross;
    }

    /// <summary>The arrays of a <see cref="DrainageField"/> that step 1 and 2 of <see cref="Build"/> read, so that the channel and chain finding runs without a grid (T2R-23).</summary>
    internal sealed class ChainField
    {
        public required DrainageDomain Domain;
        public required int[] Down, Area, LakeId;
        public required DrainageCellKind[] Kind, Terminal;
        public required bool[] Visited;
        /// <summary>Lowest sample per cell and the level of each lake (read only with lake channels).</summary>
        public float[] Lowest = Array.Empty<float>(), LakeLevel = Array.Empty<float>();
        public required double CellArea;
    }

    internal sealed class ChainResult
    {
        public required bool[] Channel, LakeChannel, LakeMarked, DeepCell;
        public required int ChannelCells, LakeChannelCells, LakeLeads;
        public required int[] MainUp;
        public required List<int[]> Chains;
        /// <summary>Per stem: how it ends, the lake or receiving stem id, the receiving stem and its node index; the stems by terminal.</summary>
        public required RiverTerminal[] Terminals;
        public required int[] TerminalIds, ReceiverStems, ReceiverNodeIndices, TerminalCounts;
    }

    /// <summary>
    /// Steps 1 and 2 of <see cref="Build"/>: channel cells, lake channels, the main upstream neighbour of each cell and the stems (chains of cells, sorted by last cell then head cell).
    /// <paramref name="removed"/> (T2R-23): cells that are not channel cells; <paramref name="excludeIntakeTerminal"/>: cells whose water ends at a site are not channel cells. Both are off in production.
    /// </summary>
    internal static ChainResult FindChains(ChainField f, double threshold, bool lakeChannels, bool deepEntries, bool[]? removed, bool excludeIntakeTerminal)
    {
        DrainageDomain d = f.Domain;
        int n = f.Down.Length;
        int[] down = f.Down, area = f.Area, lakeId = f.LakeId;
        DrainageCellKind[] kind = f.Kind, terminalKind = f.Terminal;
        bool[] visited = f.Visited;
        double cellArea = f.CellArea;
        // 1. Channel cells and the main upstream neighbour of each.
        var channel = new bool[n];
        int channelCells = 0;
        for (int c = 0; c < n; c++)
        {
            channel[c] = kind[c] == DrainageCellKind.Land && visited[c] && lakeId[c] < 0 && down[c] >= 0 && area[c] * cellArea >= threshold - 1e-9 && terminalKind[c] != DrainageCellKind.Barrier
                && (removed == null || !removed[c]) && !(excludeIntakeTerminal && terminalKind[c] == DrainageCellKind.Intake); // T2R-23: the removal rules (both off by default)
            if (channel[c]) channelCells++;
        }
        // T2R-8b items 1 and 2: lake channel cells, the flooded cells on the flow path from each inlet to the lake's exit or pit (and an outlet lead where no inlet reaches the exit).
        var lakeChannel = new bool[n]; // the cells of the in-lake stems
        var lakeMarked = new bool[n]; // every marked cell: the in-lake stems and the approach cells before their first deep cell
        var deepCell = new bool[n]; // marked cells whose lowest sample stands 2 m under the lake level
        int lakeChannelCells = 0, lakeLeads = 0;
        if (lakeChannels)
        {
            float[] lowestSample = f.Lowest;
            float[] lakeLevel = f.LakeLevel;
            var provisionalUp = new int[n];
            Array.Fill(provisionalUp, -1);
            for (int c = 0; c < n; c++)
            {
                if (!channel[c]) continue;
                int next = down[c];
                if (!channel[next]) continue;
                int m = provisionalUp[next];
                if (m < 0 || area[c] > area[m] || (area[c] == area[m] && c < m)) provisionalUp[next] = c;
            }
            // A flooded cell is marked while its flow stays in the lake; the exit cell is marked only when the flow goes on into a channel cell (T2R-22 item 7: any channel cell, also one with a river upstream of it: the drowned channel then runs
            // through the exit and joins that river as a confluence), the sea, a Weather Lung or the strip end (a lake that spills into a cell under the channel threshold ends its drowned channel one cell short, like a pit, so the stem stays
            // a Lake stem and is never Dead).
            bool Continues(int cell)
            {
                int next = down[cell];
                return channel[next] || kind[next] is DrainageCellKind.Ocean or DrainageCellKind.Lung or DrainageCellKind.StripOutlet or DrainageCellKind.Intake;
            }
            void MarkFrom(int entry)
            {
                int lake = lakeId[entry], cur = entry;
                while (cur >= 0 && lakeId[cur] == lake && down[cur] >= 0 && !lakeMarked[cur])
                {
                    int next = down[cur];
                    if (lakeId[next] != lake && !Continues(cur)) break;
                    lakeMarked[cur] = true;
                    cur = next;
                }
            }
            for (int c = 0; c < n; c++)
                if (channel[c] && down[c] >= 0 && lakeId[down[c]] >= 0) MarkFrom(down[c]);
            // Leads: a channel cell h with a flooded neighbour flowing into it and no marked one. Walk up the flooded cells (largest area first) to the first whose lowest sample stands 2 m under the level.
            Span<int> nbr = stackalloc int[8];
            var lead = new List<int>();
            for (int h = 0; h < n; h++)
            {
                if (!channel[h]) continue;
                d.GetNeighbors(h, nbr);
                int first = -1;
                bool marked = false;
                for (int dir = 0; dir < 8; dir++)
                {
                    int m = nbr[dir];
                    if (m < 0 || down[m] != h || lakeId[m] < 0) continue;
                    if (lakeMarked[m]) { marked = true; break; }
                    if (first < 0 || area[m] > area[first] || (area[m] == area[first] && m < first)) first = m;
                }
                if (marked || first < 0 || provisionalUp[h] >= 0) continue; // only where the channel cell is a stem head: a lake whose outlet cell has a river of its own upstream keeps it (the inlet and outlet counts stay those of the T2R-8 graph)
                // No lead for a stem that ends Dead: follow h's chain along the main-channel links to its last cell first.
                int tail = h;
                while (true)
                {
                    int next = down[tail];
                    if (next >= 0 && channel[next] && provisionalUp[next] == tail) tail = next; else break;
                }
                int after = down[tail];
                if (!channel[after] && lakeId[after] < 0 && kind[after] is not (DrainageCellKind.Ocean or DrainageCellKind.Lung or DrainageCellKind.StripOutlet or DrainageCellKind.Intake)) continue;
                lead.Clear();
                int cur = first;
                bool found = false;
                while (lead.Count < 64)
                {
                    lead.Add(cur);
                    int lake = lakeId[cur];
                    if (lowestSample[cur] <= Math.Floor(lakeLevel[lake]) - 2d) { found = true; break; }
                    d.GetNeighbors(cur, nbr);
                    int up = -1;
                    for (int dir = 0; dir < 8; dir++)
                    {
                        int m = nbr[dir];
                        if (m < 0 || down[m] != cur || lakeId[m] != lake) continue;
                        if (up < 0 || area[m] > area[up] || (area[m] == area[up] && m < up)) up = m;
                    }
                    if (up < 0) break;
                    cur = up;
                }
                if (!found) continue;
                foreach (int cell in lead) lakeMarked[cell] = true;
                lakeLeads++;
            }
            // A flooded cell is deep when its lowest sample stands 2 m under the lake level (the routing noise floods shallow rim cells whose ground is dry). The in-lake stem starts at the first deep cell of a
            // chain (items 1 and 3), so every inlet ends and every outlet starts in water the terrain shows; the shallow cells before it are approach cells, plain channel cells of the river that enters.
            // A chain with no deep cell at all is taken whole.
            bool Deep(int cell) => lowestSample[cell] <= Math.Floor(lakeLevel[lakeId[cell]]) - 2d;
            for (int c = 0; c < n; c++) deepCell[c] = lakeMarked[c] && Deep(c);
            if (!deepEntries) Array.Copy(lakeMarked, lakeChannel, n);
            for (int c = 0; c < n && deepEntries; c++)
            {
                if (!lakeMarked[c]) continue;
                bool anyDeep = false;
                for (int cur = c; cur >= 0 && lakeMarked[cur] && !anyDeep; cur = down[cur]) anyDeep = Deep(cur);
                if (!anyDeep) lakeChannel[c] = true;
            }
            for (int c = 0; c < n && deepEntries; c++)
            {
                if (!lakeMarked[c] || !Deep(c)) continue;
                for (int cur = c; cur >= 0 && lakeMarked[cur] && !lakeChannel[cur]; cur = down[cur]) lakeChannel[cur] = true;
            }
            // Where two chains meet in the shallow zone (a shared approach cell: two marked cells, or a marked cell and a river, flow into it) the shallow cells stay part of the in-lake stems, so the
            // inlets that meet there stay inlets of the lake (the inlet counts equal the T2R-8 graph's); a run that reaches its deep cell alone is trimmed.
            if (deepEntries)
            {
                Span<int> nbs = stackalloc int[8];
                var stack = new Stack<int>();
                for (int c = 0; c < n; c++)
                {
                    if (!lakeMarked[c] || lakeChannel[c]) continue;
                    d.GetNeighbors(c, nbs);
                    int inflow = 0;
                    for (int dir = 0; dir < 8; dir++) if (nbs[dir] >= 0 && down[nbs[dir]] == c && (lakeMarked[nbs[dir]] || channel[nbs[dir]])) inflow++;
                    if (inflow < 2) continue;
                    for (int cur = c; cur >= 0 && lakeMarked[cur] && !lakeChannel[cur]; cur = down[cur]) lakeChannel[cur] = true;
                    stack.Push(c);
                    while (stack.Count > 0)
                    {
                        int top = stack.Pop();
                        d.GetNeighbors(top, nbs);
                        for (int dir = 0; dir < 8; dir++)
                        {
                            int m = nbs[dir];
                            if (m >= 0 && down[m] == top && lakeMarked[m] && !lakeChannel[m]) { lakeChannel[m] = true; stack.Push(m); }
                        }
                    }
                }
            }
            for (int c = 0; c < n; c++)
                if (lakeMarked[c]) { channel[c] = true; if (lakeChannel[c]) lakeChannelCells++; }
        }
        var mainUp = new int[n];
        Array.Fill(mainUp, -1);
        for (int c = 0; c < n; c++)
        {
            if (!channel[c]) continue;
            int next = down[c];
            if (!channel[next]) continue;
            if (lakeChannel[next] && !lakeChannel[c]) continue; // item 3: a river that enters a lake ends on the in-lake stem
            int m = mainUp[next];
            // T2R-22 item 7: where a lake's exit cell flows into a river that already runs there, the river is the main course and the drowned channel joins it as a confluence, whatever the areas.
            if (m >= 0 && lakeChannel[c] != lakeChannel[m]) { if (lakeChannel[m]) mainUp[next] = c; continue; }
            if (m < 0 || area[c] > area[m] || (area[c] == area[m] && c < m)) mainUp[next] = c;
        }

        // 2. Stems from the heads, then stable ids by (last cell, head cell).
        var chains = new List<int[]>();
        var chain = new List<int>();
        for (int head = 0; head < n; head++)
        {
            if (!channel[head] || mainUp[head] >= 0) continue;
            chain.Clear();
            int cur = head;
            while (true)
            {
                chain.Add(cur);
                int next = down[cur];
                if (next >= 0 && channel[next] && mainUp[next] == cur) cur = next; else break;
            }
            chains.Add(chain.ToArray());
        }
        chains.Sort((a, b) => a[^1] != b[^1] ? a[^1].CompareTo(b[^1]) : a[0].CompareTo(b[0]));
        // How each stem ends (S5), with the receiving stem and node of a confluence.
        int stemCount = chains.Count;
        var terminals = new RiverTerminal[stemCount];
        var terminalIds = new int[stemCount];
        var receiverStems = new int[stemCount];
        var receiverNodes = new int[stemCount];
        Array.Fill(terminalIds, -1);
        Array.Fill(receiverStems, -1);
        var cellStem = new int[n];
        var cellIndex = new int[n];
        Array.Fill(cellStem, -1);
        for (int i = 0; i < stemCount; i++)
            for (int k = 0; k < chains[i].Length; k++) { cellStem[chains[i][k]] = i; cellIndex[chains[i][k]] = k; }
        var counts = new int[8];
        for (int i = 0; i < stemCount; i++)
        {
            int last = chains[i][^1], next = down[last];
            if (channel[next])
            {
                terminals[i] = RiverTerminal.Confluence; receiverStems[i] = cellStem[next]; terminalIds[i] = receiverStems[i]; receiverNodes[i] = cellIndex[next];
                // A river that enters an in-lake stem whose entry cell is shallow (the chain was kept whole where chains meet) joins it at the first deep cell, so its end stands in water the terrain shows.
                if (deepEntries && cellIndex[next] == 0 && lakeChannel[next])
                {
                    int[] rc0 = chains[receiverStems[i]];
                    for (int k = 0; k < rc0.Length; k++) if (deepCell[rc0[k]]) { receiverNodes[i] = k; break; }
                }
            }
            else if (lakeId[next] >= 0) { terminals[i] = RiverTerminal.Lake; terminalIds[i] = lakeId[next]; }
            else if (kind[next] == DrainageCellKind.Ocean) terminals[i] = RiverTerminal.Sea;
            else if (kind[next] == DrainageCellKind.Lung) terminals[i] = RiverTerminal.Lung;
            else if (kind[next] == DrainageCellKind.StripOutlet) terminals[i] = RiverTerminal.StripEnd;
            else if (kind[next] == DrainageCellKind.Intake) terminals[i] = RiverTerminal.Intake;
            else if (removed != null && removed[next]) terminals[i] = RiverTerminal.Cut; // T2R-23, rule Branch: it joined a river that was removed
            else terminals[i] = RiverTerminal.Dead;
            counts[(int)terminals[i]]++;
        }
        return new ChainResult
        {
            Channel = channel, LakeChannel = lakeChannel, LakeMarked = lakeMarked, DeepCell = deepCell, ChannelCells = channelCells, LakeChannelCells = lakeChannelCells, LakeLeads = lakeLeads, MainUp = mainUp, Chains = chains,
            Terminals = terminals, TerminalIds = terminalIds, ReceiverStems = receiverStems, ReceiverNodeIndices = receiverNodes, TerminalCounts = counts,
        };
    }

    /// <summary>T2R-23: the channel finding under the site removal rule. Catchment: one pass without the cells whose water ends at a site. Branch: pass 1 finds the chains, <see cref="RiverSiteRemoval.Mark"/> marks the removed ones, pass 2 runs without their cells.</summary>
    internal static ChainResult FindChannels(ChainField cf, double threshold, RiverBuildOptions options, bool[]? siteDrainedCell, out RiverSiteRemovalStats stats, out bool[]? removedCells)
    {
        stats = default;
        removedCells = null;
        ChainResult result;
        switch (options.SiteRemoval)
        {
            case RiverSiteRemovalRule.Catchment:
                result = FindChains(cf, threshold, options.LakeChannels, options.DeepEntries, null, true);
                break;
            case RiverSiteRemovalRule.Branch:
            {
                ChainResult first = FindChains(cf, threshold, options.LakeChannels, options.DeepEntries, null, false);
                removedCells = RiverSiteRemoval.Mark(first.Chains, cf.Down, cf.Kind, cf.Terminal, siteDrainedCell, out stats);
                result = FindChains(cf, threshold, options.LakeChannels, options.DeepEntries, removedCells, false);
                break;
            }
            default:
                return FindChains(cf, threshold, options.LakeChannels, options.DeepEntries, null, false);
        }
        if (result.TerminalCounts[(int)RiverTerminal.Intake] > 0) throw new InvalidOperationException("site removal left " + result.TerminalCounts[(int)RiverTerminal.Intake] + " stems that end at a site");
        return result;
    }

    public static RiverNetwork Build(DrainageField f, DrainageGrid grid, TerrainGenerationParameters p, RiverBuildOptions options, CancellationToken cancellation,
        out RiverBuildReport report)
    {
        var clock = Stopwatch.StartNew();
        DrainageGridSpec spec = grid.Spec;
        DrainageDomain d = f.Domain;
        int n = f.Count, width = f.Width;
        long seed = p.Seed;
        double density = p.HasRivers ? p.GetExtensionField("river_density") : 1d;
        double meander = p.HasRivers ? p.GetExtensionField("meander_strength") : 1d;
        double valley = p.HasRivers ? p.GetExtensionField("river_valley_width") : 1d;
        double depthScale = p.HasRivers ? p.GetExtensionField("channel_depth") : 1d;
        double ferocity = p.HasRivers ? p.GetExtensionField("river_ferocity") : 1d;
        double threshold = options.ThresholdKm2 ?? 100d / density;
        double cellArea = f.CellAreaKm2;
        double circumference = spec.CircumferenceMeters;
        double widthMeters = spec.Rows * spec.AcrossStepMeters;

        int[] down = f.Down.ToArray(), area = f.AreaCells.ToArray(), lakeId = f.LakeId.ToArray();
        double[] accumulated = f.RunoffAccumulation.ToArray();
        float[] filled = f.UnperturbedFilled.ToArray();
        DrainageCellKind[] kind = f.Kind.ToArray(), terminalKind = f.Terminal.ToArray();
        bool[] visited = f.Visited.ToArray();

        var chainField = new ChainField
        {
            Domain = d, Down = down, Area = area, LakeId = lakeId, Kind = kind, Terminal = terminalKind, Visited = visited, CellArea = cellArea,
            Lowest = options.LakeChannels ? f.Lowest.ToArray() : Array.Empty<float>(),
            LakeLevel = options.LakeChannels ? f.Lakes.ToArray().Select(l => l.Level).ToArray() : Array.Empty<float>(),
        };
        bool[]? siteDrainedCell = options.SiteRemoval == RiverSiteRemovalRule.None ? null : RiverSiteRemoval.SiteDrainedCells(f);
        ChainResult chainResult = FindChannels(chainField, threshold, options, siteDrainedCell, out RiverSiteRemovalStats removal, out bool[]? removedCells);
        bool[] channel = chainResult.Channel, lakeChannel = chainResult.LakeChannel, deepCell = chainResult.DeepCell;
        int channelCells = chainResult.ChannelCells, lakeChannelCells = chainResult.LakeChannelCells, lakeLeads = chainResult.LakeLeads;
        int[] mainUp = chainResult.MainUp;
        List<int[]> chains = chainResult.Chains;
        int stemCount = chains.Count;
        var tmp = new StemTmp[stemCount];
        for (int i = 0; i < stemCount; i++)
            tmp[i] = new StemTmp { Cells = chains[i], Terminal = chainResult.Terminals[i], TerminalId = chainResult.TerminalIds[i], ReceiverStem = chainResult.ReceiverStems[i], ReceiverNodeIndex = chainResult.ReceiverNodeIndices[i] };
        int[] report0 = chainResult.TerminalCounts;

        // 3. Reach splits (a cell that receives a tributary starts a reach) and the hydraulics of each reach from the grid.
        var reachStartCell = new bool[n];
        foreach (StemTmp s in tmp) if (s.Terminal == RiverTerminal.Confluence) reachStartCell[down[s.Cells[^1]]] = true;
        var reachFirst = new List<List<int>>(stemCount);
        var reachList = new List<RiverReach>();
        var reachBase = new int[stemCount];
        for (int i = 0; i < stemCount; i++)
        {
            StemTmp s = tmp[i];
            var starts = new List<int> { 0 };
            // T2R-8b item 3: a reach starts at the first cell outside the lake of a stem that starts inside one, so the in-lake part has its own hydraulics.
            for (int k = 1; k < s.Cells.Length; k++) if (reachStartCell[s.Cells[k]] || (lakeChannel[s.Cells[k - 1]] && !lakeChannel[s.Cells[k]])) starts.Add(k);
            reachFirst.Add(starts);
            reachBase[i] = reachList.Count;
            s.ReachOfCell = new int[s.Cells.Length];
            for (int r = 0; r < starts.Count; r++)
            {
                int k0 = starts[r], k1 = r + 1 < starts.Count ? starts[r + 1] : s.Cells.Length;
                for (int k = k0; k < k1; k++) s.ReachOfCell[k] = r;
                int lastCell = s.Cells[k1 - 1], midCell = s.Cells[(k0 + k1 - 1) / 2];
                int upstreamLake = -1;
                if (r == 0)
                {
                    Span<int> nb = stackalloc int[8];
                    d.GetNeighbors(s.Cells[0], nb);
                    for (int dir = 0; dir < 8; dir++) if (nb[dir] >= 0 && down[nb[dir]] == s.Cells[0] && lakeId[nb[dir]] >= 0) { upstreamLake = lakeId[nb[dir]]; break; }
                    if (lakeChannel[s.Cells[0]]) upstreamLake = lakeId[s.Cells[0]]; // a stem whose first cell is flooded
                }
                reachList.Add(new RiverReach
                {
                    Stem = i, AreaKm2 = (float)(area[lastCell] * cellArea), Q = (float)RiverHydraulics.Discharge(accumulated[lastCell]),
                    S = (float)Gradient(d, down, filled, midCell), UpstreamLake = upstreamLake,
                });
            }
        }
        RiverReach[] reaches = reachList.ToArray();

        // 4. Centrelines, receivers first.
        var order = new List<int>(stemCount);
        var children = new List<int>[stemCount];
        for (int i = 0; i < stemCount; i++)
            if (tmp[i].ReceiverStem < 0) order.Add(i);
            else (children[tmp[i].ReceiverStem] ??= new List<int>()).Add(i);
        for (int h = 0; h < order.Count; h++)
        {
            List<int>? kids = children[order[h]];
            if (kids != null) order.AddRange(kids);
        }
        cancellation.ThrowIfCancellationRequested();
        var wanderAmplitude = new double[reaches.Length];
        for (int r = 0; r < reaches.Length; r++) wanderAmplitude[r] = RiverHydraulics.WanderAmplitude(meander, reaches[r].S, options.WanderScale);
        double[] jx = new double[n], jy = new double[n];
        for (int c = 0; c < n; c++)
            if (channel[c] && lakeId[c] < 0) // item 4: no jitter on a flooded cell's node
            {
                jx[c] = JitterMeters * RiverHash.Signed(seed, c, 0, 1);
                jy[c] = JitterMeters * RiverHash.Signed(seed, c, 0, 2);
            }
        Func<int, (double Around, double Across)> baseNode = cell =>
        {
            int col = cell % width, row = cell / width;
            int idx = grid.LowestIndex[cell], i = idx & 3, j = idx >> 2;
            (long around, int across) = spec.Sample(col, row, i, j);
            return (around, across);
        };

        double maxWander = 0, maxSmooth = 0, maxNodeShift = 0, guardMax = 0;
        long guardMoved = 0;
        long snapped = 0;
        var netShell = new RiverNetwork(spec.CircumferenceMeters, widthMeters, seed, meander, valley, depthScale, ferocity,
            new int[0], new int[0], new RiverStem[0], new RiverReach[0]);
        // Stems are built level by level (receivers first): the stems of one depth are independent, so the valley-floor snap samples them in one batch.
        var depth = new int[stemCount];
        int maxDepth = 0;
        foreach (int si in order)
        {
            depth[si] = tmp[si].ReceiverStem < 0 ? 0 : depth[tmp[si].ReceiverStem] + 1;
            maxDepth = Math.Max(maxDepth, depth[si]);
        }
        var levels = new List<int>[maxDepth + 1];
        for (int l = 0; l <= maxDepth; l++) levels[l] = new List<int>();
        foreach (int si in order) levels[depth[si]].Add(si);
        for (int level = 0; level <= maxDepth; level++)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (int si in levels[level])
            {
                StemTmp s = tmp[si];
                int cellsN = s.Cells.Length;
                bool confluence = s.Terminal == RiverTerminal.Confluence;
                int nodes = cellsN + 1;
                var x = new double[nodes];
                var y = new double[nodes];
                var amp = new double[nodes];
                var grad = new double[nodes];
                int endCell = confluence ? -1 : down[s.Cells[^1]];
                (double a0, double c0) = baseNode(s.Cells[0]);
                double originAround = a0 + jx[s.Cells[0]], originAcross = c0 + jy[s.Cells[0]];
                double previousBase = a0, unwrapped = 0;
                for (int k = 0; k < cellsN; k++)
                {
                    int cell = s.Cells[k];
                    (double ba, double bc) = baseNode(cell);
                    unwrapped += netShell.WrapDelta(ba - previousBase);
                    previousBase = ba;
                    x[k] = unwrapped + jx[cell] - jx[s.Cells[0]];
                    y[k] = bc + jy[cell] - originAcross;
                    amp[k] = wanderAmplitude[reachBase[si] + s.ReachOfCell[k]];
                    grad[k] = reaches[reachBase[si] + s.ReachOfCell[k]].S;
                }
                grad[cellsN] = grad[cellsN - 1];
                if (!confluence)
                {
                    (double ba, double bc) = baseNode(endCell);
                    unwrapped += netShell.WrapDelta(ba - previousBase);
                    bool lakeEnd = options.LakeChannels && lakeId[endCell] >= 0; // item 4: the end node of a stem that ends Terminal Lake is not jittered either
                    double ex = lakeEnd ? 0d : hashJitter(seed, endCell, 1), ey = lakeEnd ? 0d : hashJitter(seed, endCell, 2);
                    x[cellsN] = unwrapped + ex - jx[s.Cells[0]];
                    y[cellsN] = bc + ey - originAcross;
                }
                else
                {
                    StemTmp recv = tmp[s.ReceiverStem];
                    RiverStemCurve rc = recv.Curve;
                    // The junction is where the receiver's curve passes the confluence cell's node (the baseline joins at the confluence cell), not the foot of a perpendicular.
                    double qx = rc.X[s.ReceiverNodeIndex], qy = rc.Y[s.ReceiverNodeIndex];
                    rc.Closest(qx, qy, s.ReceiverNodeIndex - 2, s.ReceiverNodeIndex + 2, out double junctionS, out _);
                    rc.Eval(junctionS, out double rx, out double ry, out _, out _);
                    s.JunctionS = (float)junctionS;
                    x[cellsN] = netShell.WrapDelta(rc.OriginAround + rx - originAround);
                    y[cellsN] = rc.OriginAcross + ry - originAcross;
                }
                if (options.EaseHairpins) RiverWander.EaseHairpins(x, y);
                // T2R-5c: smooth the node chain on gentle ground, then wander; every node then stays within the lateral cap of its eased position.
                var ox = (double[])x.Clone();
                var oy = (double[])y.Clone();
                if (options.Smooth) maxSmooth = Math.Max(maxSmooth, RiverWander.SmoothGentle(x, y, grad));
                maxWander = Math.Max(maxWander, RiverWander.Apply(x, y, amp, seed, si));
                RiverWander.ClampShift(x, y, ox, oy);
                s.X = x; s.Y = y; s.OX = ox; s.OY = oy; s.OriginAround = originAround; s.OriginAcross = originAcross;
            }
            if (options.Snap && options.Source != null) snapped += SnapToValleyFloor(levels[level], tmp, spec, d, netShell, options.Source);
            foreach (int si in levels[level])
            {
                StemTmp s = tmp[si];
                maxNodeShift = Math.Max(maxNodeShift, RiverWander.ClampShift(s.X, s.Y, s.OX, s.OY));
                if (options.BarrierGuard) { (int movedNodes, double movedMax) = GuardBarriers(s, kind, spec, width, f.Height, s.Terminal == RiverTerminal.Cut); guardMoved += movedNodes; guardMax = Math.Max(guardMax, movedMax); }
                int nodes = s.X.Length;
                double originAround = s.OriginAround, originAcross = s.OriginAcross;
                // Quantise to decimetres (the stored form) and clamp across the rim.
                s.Dm = new int[nodes];
                s.AcrossDm = new int[nodes];
                for (int k = 0; k < nodes; k++)
                {
                    double around = originAround + s.X[k], across = Math.Clamp(originAcross + s.Y[k], 5d, widthMeters - 5d);
                    double wrapped = around % circumference;
                    if (wrapped < 0) wrapped += circumference;
                    s.Dm[k] = (int)Math.Round(wrapped * 10d) % (int)(circumference * 10d);
                    s.AcrossDm[k] = (int)Math.Round(across * 10d);
                }
                var qxs = new double[nodes];
                var qys = new double[nodes];
                double oa = s.Dm[0] / 10d, oc = s.AcrossDm[0] / 10d;
                for (int k = 1; k < nodes; k++)
                {
                    qxs[k] = qxs[k - 1] + netShell.WrapDelta((s.Dm[k] - s.Dm[k - 1]) / 10d);
                    qys[k] = s.AcrossDm[k] / 10d - oc;
                }
                s.Curve = new RiverStemCurve(oa, oc, qxs, qys);
                s.X = s.Y = s.OX = s.OY = Array.Empty<double>();
            }
        }
        double centrelineSeconds = clock.Elapsed.TotalSeconds;

        // 5. Flatten into the stored arrays and finish the reach intervals and meander phases.
        int totalNodes = tmp.Sum(t => t.Dm.Length);
        var aroundDm = new int[totalNodes];
        var acrossDm = new int[totalNodes];
        var stems = new RiverStem[stemCount];
        int cursor = 0;
        for (int i = 0; i < stemCount; i++)
        {
            StemTmp s = tmp[i];
            Array.Copy(s.Dm, 0, aroundDm, cursor, s.Dm.Length);
            Array.Copy(s.AcrossDm, 0, acrossDm, cursor, s.Dm.Length);
            stems[i] = new RiverStem
            {
                NodeStart = cursor, NodeCount = s.Dm.Length, ReachStart = reachBase[i], ReachCount = reachFirst[i].Count,
                Terminal = s.Terminal, TerminalId = s.TerminalId, JunctionS = s.JunctionS, HeadCell = s.Cells[0], LastCell = s.Cells[^1],
            };
            cursor += s.Dm.Length;
        }
        var cellJunction = new Dictionary<int, double>();
        for (int i = 0; i < stemCount; i++)
            if (tmp[i].Terminal == RiverTerminal.Confluence)
            {
                int cell = down[tmp[i].Cells[^1]];
                cellJunction[cell] = cellJunction.TryGetValue(cell, out double v) ? Math.Min(v, tmp[i].JunctionS) : tmp[i].JunctionS;
            }
        for (int i = 0; i < stemCount; i++)
        {
            StemTmp s = tmp[i];
            double length = s.Curve.Length;
            List<int> starts = reachFirst[i];
            double previous = 0;
            for (int r = 0; r < starts.Count; r++)
            {
                // A reach that starts where the stem leaves a lake (no tributary there) starts at that cell's node.
                bool lakeExit = r > 0 && lakeChannel[s.Cells[starts[r] - 1]] && !lakeChannel[s.Cells[starts[r]]];
                double start = r == 0 ? 0 : Math.Clamp(cellJunction.TryGetValue(s.Cells[starts[r]], out double j) ? j : lakeExit ? s.Curve.S0[starts[r]] : previous + 1, previous + 1e-3, length);
                reaches[reachBase[i] + r].SStart = (float)start;
                if (r > 0) reaches[reachBase[i] + r - 1].SEnd = (float)start;
                previous = start;
            }
            reaches[reachBase[i] + starts.Count - 1].SEnd = (float)length;
        }
        var net = new RiverNetwork(spec.CircumferenceMeters, widthMeters, seed, meander * options.MeanderScale, valley, depthScale, ferocity, aroundDm, acrossDm, stems, reaches);
        var inLakeEnd = new double[stemCount];
        double inLakeKm = 0;
        int inLakeStems = 0;
        for (int i = 0; i < stemCount; i++)
        {
            StemTmp s = tmp[i];
            if (!lakeChannel[s.Cells[0]]) continue;
            inLakeStems++;
            double end = s.Curve.Length;
            for (int r = 1; r < reachFirst[i].Count; r++)
                if (!lakeChannel[s.Cells[reachFirst[i][r]]]) { end = net.Reaches[reachBase[i] + r].SStart; break; }
            inLakeEnd[i] = end;
            inLakeKm += end / 1000d;
        }
        net.InLakeEnd = inLakeEnd;
        // Phases chain through the reaches of a stem so the meander is continuous: needs wavelengths, which need the sections.
        for (int i = 0; i < stemCount; i++)
        {
            double phase = 2 * Math.PI * RiverHash.Unit(seed, i, 7, 3);
            for (int r = 0; r < stems[i].ReachCount; r++)
            {
                int ri = reachBase[i] + r;
                net.Reaches[ri].MeanderPhase = (float)phase;
                phase = net.Phase(ri, net.Reaches[ri].SEnd, phase, out _, out _);
            }
        }
        double lengthKm = net.Curves.Sum(c => c.Length) / 1000d;
        double maxMeander = 0;
        for (int i = 0; i < net.Reaches.Length; i++) maxMeander = Math.Max(maxMeander, RiverHydraulics.MeanderAmplitude(net.Sections[i].Width, meander, net.Sections[i].Gradient));
        report = new RiverBuildReport
        {
            ThresholdKm2 = threshold, ChannelCells = channelCells, Stems = stemCount, Reaches = reaches.Length, Nodes = totalNodes, LengthKm = lengthKm,
            TerminalSea = report0[(int)RiverTerminal.Sea], TerminalLake = report0[(int)RiverTerminal.Lake], TerminalConfluence = report0[(int)RiverTerminal.Confluence],
            TerminalLung = report0[(int)RiverTerminal.Lung], TerminalStripEnd = report0[(int)RiverTerminal.StripEnd], TerminalDead = report0[(int)RiverTerminal.Dead], TerminalIntake = report0[(int)RiverTerminal.Intake], TerminalCut = report0[(int)RiverTerminal.Cut], RemovedStems = removal.RemovedStems, RemovedCells = removal.RemovedCells,
            LakeChannelCells = lakeChannelCells, LakeLeads = lakeLeads, InLakeStems = inLakeStems, InLakeKm = inLakeKm,
            CentrelineSeconds = centrelineSeconds, MaxWanderMeters = maxWander, MaxMeanderMeters = maxMeander, MaxNodeShiftMeters = maxNodeShift, MaxSmoothMeters = maxSmooth, SnappedNodes = snapped, GuardMovedNodes = guardMoved, GuardMaxMeters = guardMax,
        };
        if (options.Levels && options.Source != null)
        {
            RiverLevel.Build(net, f, grid, options, cancellation, ref report);
        }
        // 6. Spans (T3R-3, E1 to E4): cut after the levels so the gradient reads the stored profile, then the calibrated sections, classes, landings and exclusions.
        var spanClock = Stopwatch.StartNew();
        RiverSpan[] spans = CutSpans(net, tmp, reachBase, reachFirst, accumulated);
        RiverSpanBuilder.FillGradients(net, spans);
        RiverExclusions? exclusions = RiverExclusions.Build(p);
        bool[]? excluded = exclusions?.Mark(net, spans);
        net.InstallSpans(RiverSpanTable.Create(net, spans, excluded, true));
        report = report with { Spans = spans.Length, SpanLandings = net.SpanTable!.LandingCount, SpanLandingKm = net.SpanTable.LandingMeters / 1000d, SpanExcluded = excluded?.Count(x => x) ?? 0, ExclusionZones = exclusions?.ZoneCount ?? 0, SpanSeconds = spanClock.Elapsed.TotalSeconds };
        report = HydraulicsSummary(net, report);
        return net;

        double hashJitter(long sd, int cell, int axis) => JitterMeters * RiverHash.Signed(sd, cell, 0, axis);
    }

    /// <summary>
    /// E1: each reach of k channel cells is cut into <c>ceil(k / 2)</c> spans of equal cell count (the cells are spread evenly); a span boundary is the arc length of the node of its first
    /// cell, the first and last spans keep the reach's own ends so the spans tile it. <c>Q</c> is the discharge at the span's last cell. Gradients are filled in after the levels.
    /// </summary>
    private static RiverSpan[] CutSpans(RiverNetwork net, StemTmp[] tmp, int[] reachBase, List<List<int>> reachFirst, double[] accumulated)
    {
        var spans = new List<RiverSpan>(net.Reaches.Length * 2);
        for (int i = 0; i < tmp.Length; i++)
        {
            StemTmp s = tmp[i];
            List<int> starts = reachFirst[i];
            RiverStemCurve curve = net.Curves[i];
            for (int r = 0; r < starts.Count; r++)
            {
                int reach = reachBase[i] + r, k0 = starts[r], k1 = r + 1 < starts.Count ? starts[r + 1] : s.Cells.Length, cells = k1 - k0;
                float start = net.Reaches[reach].SStart, end = net.Reaches[reach].SEnd;
                int m = Math.Max(1, (cells + 1) / 2);
                if (end - start < 0.02f * m) m = 1;
                var bound = new float[m + 1];
                bound[0] = start; bound[m] = end;
                for (int j = 1; j < m; j++)
                {
                    int cell = k0 + (int)((long)j * cells / m);
                    bound[j] = Math.Max(bound[j - 1] + 0.01f, Math.Min(end - 0.01f * (m - j), curve.S0[cell]));
                }
                for (int j = 0; j < m; j++)
                {
                    int lastCell = j + 1 < m ? k0 + (int)((long)(j + 1) * cells / m) - 1 : k1 - 1;
                    spans.Add(new RiverSpan { Reach = reach, SStart = bound[j], SEnd = bound[j + 1], Q = (float)RiverHydraulics.Discharge(accumulated[s.Cells[lastCell]]), Sh = 1e-4f, Landing = 1f });
                }
            }
        }
        return spans.ToArray();
    }

    private const int SnapChunkPoints = 2_000_000;

    /// <summary>Clearance the centreline keeps from barrier cells: the meander cap (applied at query time) plus a margin.</summary>
    public const double BarrierClearanceMeters = RiverHydraulics.MeanderAmplitudeCapMeters + 5d;

    /// <summary>
    /// Barrier guard (T2R-6b review): after smoothing, wander and the shift clamp, no sample of the node curve may come closer than <see cref="BarrierClearanceMeters"/> to a barrier cell, so the curve
    /// plus the meander (at most 100 m, applied at query time) never enters one. Offending samples push the node under them (and half of each neighbour) away from the nearest barrier cell; the last
    /// node (a junction or the end in water) never moves. Fixed order and fixed passes, so it is deterministic. Returns the number of nodes moved and the largest total move.
    /// </summary>
    private static (int Moved, double MaxMove) GuardBarriers(StemTmp s, DrainageCellKind[] kind, DrainageGridSpec spec, int width, int rows, bool lastMovable = false)
    {
        int n = s.X.Length;
        int movable = lastMovable ? n : n - 1; // T2R-23: the end node of a Cut stem is neither a junction nor in water, so it may move
        if (n < 2) return (0, 0);
        double stepA = spec.AroundStepMeters, stepC = spec.AcrossStepMeters, circumference = spec.CircumferenceMeters, m = BarrierClearanceMeters;
        var startX = (double[])s.X.Clone();
        var startY = (double[])s.Y.Clone();
        var moved = new bool[n];
        bool IsBarrier(long ci, int rj)
        {
            if ((uint)rj >= (uint)rows) return false;
            long ring = ((ci % spec.RingColumns) + spec.RingColumns) % spec.RingColumns - spec.ColumnStart;
            if (ring < 0) ring += spec.RingColumns;
            return ring < width && kind[rj * width + (int)ring] is DrainageCellKind.Barrier or DrainageCellKind.Intake; // Intake cells (T2R-19, RS2) are guarded like barrier cells; none exist with the option off
        }
        // Push vector (away from the nearest barrier cell, length = missing clearance) for a point in absolute around (unwrapped) and across metres.
        bool Push(double a, double c, out double px, out double py)
        {
            px = py = 0;
            double best = 0;
            long col = (long)Math.Floor(a / stepA);
            int row = (int)Math.Floor(c / stepC);
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                long ci = col + di; int rj = row + dj;
                if (!IsBarrier(ci, rj)) continue;
                double x0 = ci * stepA, x1 = x0 + stepA, y0 = rj * stepC, y1 = y0 + stepC;
                double qx = Math.Clamp(a, x0, x1), qy = Math.Clamp(c, y0, y1);
                double dx = a - qx, dy = c - qy, d = Math.Sqrt(dx * dx + dy * dy), deficit, ux, uy;
                if (d > 1e-9) { deficit = m - d; ux = dx / d; uy = dy / d; }
                else
                {
                    double left = a - x0, right = x1 - a, up = c - y0, down = y1 - c, e = Math.Min(Math.Min(left, right), Math.Min(up, down));
                    if (e == left) { ux = -1; uy = 0; } else if (e == right) { ux = 1; uy = 0; } else if (e == up) { ux = 0; uy = -1; } else { ux = 0; uy = 1; }
                    deficit = m + e;
                }
                if (deficit > best) { best = deficit; px = ux * deficit; py = uy * deficit; }
            }
            return best > 0;
        }
        for (int pass = 0; pass < 8; pass++)
        {
            var curve = new RiverStemCurve(0, 0, s.X, s.Y);
            var dxs = new double[n];
            var dys = new double[n];
            bool any = false;
            for (int k = 0; k < n; k++)
                for (int i = 0; i <= 8; i++)
                {
                    curve.EvalPiece(k, i / 8d, out double x, out double y, out _, out _);
                    if (!Push(s.OriginAround + x, s.OriginAcross + y, out double px, out double py)) continue;
                    any = true;
                    foreach ((int node, double w) in new[] { (k, 1d), (k - 1, 0.5), (k + 1, 0.5) })
                    {
                        if (node < 0 || node >= movable) continue;
                        if (Math.Abs(px * w) > Math.Abs(dxs[node])) dxs[node] = px * w;
                        if (Math.Abs(py * w) > Math.Abs(dys[node])) dys[node] = py * w;
                    }
                }
            if (!any) break;
            for (int k = 0; k < movable; k++) { if (dxs[k] != 0 || dys[k] != 0) moved[k] = true; s.X[k] += dxs[k]; s.Y[k] += dys[k]; }
        }
        _ = circumference;
        int count = 0;
        double max = 0;
        for (int k = 0; k < n; k++)
            if (moved[k]) { count++; max = Math.Max(max, Math.Sqrt((s.X[k] - startX[k]) * (s.X[k] - startX[k]) + (s.Y[k] - startY[k]) * (s.Y[k] - startY[k]))); }
        return (count, max);
    }
    private const double SnapToleranceMeters = 2d, SnapTriggerMeters = 8d, SnapMaxShiftMeters = RiverWander.NodeShiftCapMeters;

    /// <summary>
    /// T2R-5c (S7, ladder step 4 of docs/t2-river-reset.md 4.8): moves each interior node of the given stems to the valley floor of the cell it lies in. The 16 samples
    /// of that cell and the node's own position are read through the height source. A node more than <see cref="SnapTriggerMeters"/> above the lowest sample moves (nodes already near the floor keep their smooth position); among the samples within the lateral cap of the node's eased position and no more than
    /// <see cref="SnapToleranceMeters"/> above the lowest of them, the one nearest the node is taken. Barrier, sea and lake cells are left alone. Returns the number moved.
    /// </summary>
    private static long SnapToValleyFloor(List<int> stems, StemTmp[] tmp, DrainageGridSpec spec, DrainageDomain d, RiverNetwork shell, IDrainageHeightSource source)
    {
        var jobs = new List<(int Stem, int Node, int Col, int Row)>();
        foreach (int si in stems)
        {
            StemTmp s = tmp[si];
            for (int k = 1; k < s.X.Length - 1; k++)
            {
                double around = shell.WrapAround(s.OriginAround + s.X[k]), across = s.OriginAcross + s.Y[k];
                int ring = (int)Math.Floor(around / spec.AroundStepMeters), col = ring - spec.ColumnStart;
                if (col < 0) col += spec.RingColumns;
                int row = (int)Math.Floor(across / spec.AcrossStepMeters);
                if (col < 0 || col >= spec.Columns || row < 0 || row >= spec.Rows) continue;
                if (d.Kind[row * spec.Columns + col] != DrainageCellKind.Land) continue;
                jobs.Add((si, k, col, row));
            }
        }
        long moved = 0;
        const int perCell = DrainageGridSpec.SamplesPerCell + 1;
        const int lattice = DrainageGridSpec.SamplesPerCell;
        int first = 0;
        var ax = new double[lattice];
        var ay = new double[lattice];
        var ok = new bool[lattice];
        while (first < jobs.Count)
        {
            int count = Math.Min(jobs.Count - first, SnapChunkPoints / perCell);
            var positions = new DrainagePoint[count * perCell];
            var heights = new float[positions.Length];
            for (int j = 0; j < count; j++)
            {
                (int _, int _, int col, int row) = jobs[first + j];
                for (int q = 0; q < lattice; q++)
                {
                    (long ar, int ac) = spec.Sample(col, row, q & 3, q >> 2);
                    positions[j * perCell + q] = new DrainagePoint(ar, ac);
                }
                StemTmp node = tmp[jobs[first + j].Stem];
                int nk = jobs[first + j].Node;
                long own = (long)Math.Round(shell.WrapAround(node.OriginAround + node.X[nk])) % spec.CircumferenceMeters;
                positions[j * perCell + lattice] = new DrainagePoint(own, Math.Clamp((int)Math.Round(node.OriginAcross + node.Y[nk]), 0, (int)shell.WidthM - 1));
            }
            source.SamplePoints(positions, heights);
            for (int j = 0; j < count; j++)
            {
                (int si, int k, _, _) = jobs[first + j];
                StemTmp s = tmp[si];
                int o = j * perCell;
                double best = double.PositiveInfinity;
                for (int q = 0; q < lattice; q++)
                {
                    ax[q] = shell.WrapDelta(positions[o + q].Around - s.OriginAround);
                    ay[q] = positions[o + q].Across - s.OriginAcross;
                    double dx = ax[q] - s.OX[k], dy = ay[q] - s.OY[k];
                    ok[q] = dx * dx + dy * dy <= SnapMaxShiftMeters * SnapMaxShiftMeters;
                    if (ok[q]) best = Math.Min(best, heights[o + q]);
                }
                if (double.IsPositiveInfinity(best) || heights[o + lattice] <= best + SnapTriggerMeters) continue;
                int pick = -1;
                double pickDistance = double.PositiveInfinity;
                for (int q = 0; q < lattice; q++)
                {
                    if (!ok[q] || heights[o + q] > best + SnapToleranceMeters) continue;
                    double dx = ax[q] - s.X[k], dy = ay[q] - s.Y[k], dist = dx * dx + dy * dy;
                    if (dist < pickDistance) { pickDistance = dist; pick = q; }
                }
                if (pick < 0) continue;
                if (Math.Abs(ax[pick] - s.X[k]) > 0.5 || Math.Abs(ay[pick] - s.Y[k]) > 0.5) moved++;
                s.X[k] = ax[pick]; s.Y[k] = ay[pick];
            }
            first += count;
        }
        return moved;
    }

    /// <summary>S6: gradient on the unperturbed filled surface F over 5 km downstream along the flow path from the reach's middle cell.</summary>
    private static double Gradient(DrainageDomain d, int[] down, float[] filled, int midCell)
    {
        int cur = midCell;
        double distance = 0;
        while (distance < 5000d)
        {
            int next = down[cur];
            if (next < 0) break;
            int dir = d.Direction(cur, next);
            distance += dir >= 0 ? d.StepMeters(dir) : d.AroundStepMeters;
            cur = next;
            if (d.IsOutlet(cur)) break;
        }
        if (distance <= 0) return RiverHydraulics.MinGradient;
        return Math.Max((filled[midCell] - filled[cur]) / distance, RiverHydraulics.MinGradient);
    }

    private static double[] Percentiles(List<(double Value, double Weight)> values)
    {
        if (values.Count == 0) return Array.Empty<double>();
        values.Sort((a, b) => a.Value.CompareTo(b.Value));
        double total = values.Sum(v => v.Weight), run = 0;
        var result = new double[3];
        int idx = 0;
        double[] targets = { 0.1, 0.5, 0.9 };
        foreach ((double v, double w) in values)
        {
            run += w;
            while (idx < 3 && run >= targets[idx] * total) result[idx++] = v;
        }
        while (idx < 3) result[idx++] = values[^1].Value;
        return result;
    }

    private static RiverBuildReport HydraulicsSummary(RiverNetwork net, RiverBuildReport r)
    {
        var q = new List<(double, double)>();
        var s = new List<(double, double)>();
        var dd = new List<(double, double)>();
        var w = new List<(double, double)>();
        var u = new List<(double, double)>();
        var share = new double[4];
        double total = 0;
        RiverSpanTable? table = net.SpanTable;
        if (table != null)
        {
            // T3R-3: the calibrated spans (E2, E3); the class is the danger class.
            for (int i = 0; i < table.Count; i++)
            {
                double len = table.Spans[i].SEnd - table.Spans[i].SStart;
                q.Add((table.Spans[i].Q, len)); s.Add((table.Spans[i].Sh, len)); dd.Add((table.Depth[i], len)); w.Add((2d * table.HalfWidth[i], len)); u.Add((table.Speed[i], len));
                share[(int)table.Class[i]] += len;
                total += len;
            }
            for (int i = 0; i < 4; i++) share[i] = total > 0 ? share[i] / total : 0;
            return r with { Q = Percentiles(q), Gradient = Percentiles(s), Depth = Percentiles(dd), Width = Percentiles(w), Speed = Percentiles(u), ClassShare = share };
        }
        for (int i = 0; i < net.Reaches.Length; i++)
        {
            double len = net.Reaches[i].SEnd - net.Reaches[i].SStart;
            if (len <= 0) continue;
            RiverSection sec = net.Sections[i];
            q.Add((sec.Q, len)); s.Add((sec.Gradient, len)); dd.Add((sec.Depth, len)); w.Add((sec.Width, len)); u.Add((sec.Speed, len));
            share[(int)sec.Class] += len;
            total += len;
        }
        for (int i = 0; i < 4; i++) share[i] = total > 0 ? share[i] / total : 0;
        return r with { Q = Percentiles(q), Gradient = Percentiles(s), Depth = Percentiles(dd), Width = Percentiles(w), Speed = Percentiles(u), ClassShare = share };
    }
}
