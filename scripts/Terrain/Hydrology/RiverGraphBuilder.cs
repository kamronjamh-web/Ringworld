using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>How a stem ends (S5). StripEnd (a strip's end column) and Dead (reaches no outlet; never expected) exist for tools and tests.</summary>
public enum RiverTerminal : byte { Sea, Lake, Confluence, Lung, StripEnd, Dead }

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
    public RiverSection[] Sections { get; }
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
        for (int r = 0; r < reaches.Length; r++) Sections[r] = RiverHydraulics.Section(reaches[r].Q, reaches[r].S, channelDepth, ferocity);
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

    /// <summary>Half width a, bank width b, meander amplitude and wavelength at s, blended from the upstream reach over the first 200 m of a reach (S6).</summary>
    public void Geometry(int stem, double s, out int reach, out double a, out double b, out double amplitude, out double wavelength)
    {
        reach = ReachAt(stem, s);
        RiverSection sec = Sections[reach];
        a = RiverHydraulics.HalfWidth(sec.Width);
        b = RiverHydraulics.BankWidth(sec.Width);
        amplitude = RiverHydraulics.MeanderAmplitude(sec.Width, MeanderStrength, sec.Gradient);
        wavelength = RiverHydraulics.MeanderWavelength(sec.Width);
        if (reach > Stems[stem].ReachStart)
        {
            RiverSection up = Sections[reach - 1];
            double t = RiverHydraulics.Smoothstep(Reaches[reach].SStart, Reaches[reach].SStart + RiverHydraulics.BlendMeters, s);
            a += (RiverHydraulics.HalfWidth(up.Width) - a) * (1 - t);
            b += (RiverHydraulics.BankWidth(up.Width) - b) * (1 - t);
            amplitude += (RiverHydraulics.MeanderAmplitude(up.Width, MeanderStrength, up.Gradient) - amplitude) * (1 - t);
        }
    }

    /// <summary>
    /// Lateral meander displacement D(s) (S7), left positive. Tuned in T2R-5b: the chained phase theta = MeanderPhase_r + 2 pi (s - SStart_r) / lambda_r
    /// is continuous along the stem, and both the wavelength and the amplitude are modulated by slow functions of theta with hashed phases, so the
    /// meander is not a constant sine: <c>D = A_m * E(theta) * sin(theta + 0.6 sin(theta / 2.7 + psi1) + 0.3 sin(theta / 4.6 + psi2)) * tau</c> with the
    /// envelope <c>E = smoothstep(-0.7, 0.7, 0.6 sin(theta / 3.3 + psi3) + 0.4 sin(theta / 7.7 + psi4))</c> in [0, 1]. The local wavelength stays between
    /// about 0.78 and 1.4 lambda (d phase / d theta in [0.71, 1.29]), and |D| &lt;= A_m, so the cap holds.
    /// </summary>
    public double Meander(int stem, double s)
    {
        Geometry(stem, s, out int reach, out _, out _, out double amplitude, out double wavelength);
        RiverReach r = Reaches[reach];
        double tau = RiverHydraulics.Smoothstep(0, 2 * wavelength, s);
        if (Stems[stem].Terminal == RiverTerminal.Confluence) tau *= RiverHydraulics.Smoothstep(0, 2 * wavelength, Curves[stem].Length - s);
        if (amplitude == 0 || tau == 0) return 0;
        double theta = r.MeanderPhase + 2 * Math.PI * (s - r.SStart) / wavelength;
        int o = stem * 4;
        double phase = theta + 0.6 * Math.Sin(theta / 2.7 + _meanderNoise[o]) + 0.3 * Math.Sin(theta / 4.6 + _meanderNoise[o + 1]);
        double envelope = RiverHydraulics.Smoothstep(-0.7, 0.7, 0.6 * Math.Sin(theta / 3.3 + _meanderNoise[o + 2]) + 0.4 * Math.Sin(theta / 7.7 + _meanderNoise[o + 3]));
        return amplitude * envelope * Math.Sin(phase) * tau;
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
    public double CentrelineSeconds { get; init; }
    public double LevelSeconds { get; init; }
    public double SampleSeconds { get; init; }

    public string ToText()
    {
        string P(double[] v) => v.Length == 3 ? FormattableString.Invariant($"{v[0]:G4}/{v[1]:G4}/{v[2]:G4}") : "-";
        return string.Join("\n", new[]
        {
            FormattableString.Invariant($"rivers: threshold {ThresholdKm2:F0} km^2, channel cells {ChannelCells}, stems {Stems}, reaches {Reaches}, nodes {Nodes}, knots {Knots}, stations {Stations}, length {LengthKm:F0} km"),
            $"terminals: sea {TerminalSea}, lake {TerminalLake}, confluence {TerminalConfluence}, lung {TerminalLung}, strip end {TerminalStripEnd}, dead {TerminalDead}",
            "hydraulics p10/p50/p90 over reach length: Q " + P(Q) + " m3/s, S " + P(Gradient) + ", d " + P(Depth) + " m, w " + P(Width) + " m, U " + P(Speed) + " m/s",
            FormattableString.Invariant($"class share by length: calm {ClassShare.ElementAtOrDefault(0):P1}, swift {ClassShare.ElementAtOrDefault(1):P1}, fierce {ClassShare.ElementAtOrDefault(2):P1}, torrent {ClassShare.ElementAtOrDefault(3):P1}"),
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

        // 1. Channel cells and the main upstream neighbour of each.
        var channel = new bool[n];
        int channelCells = 0;
        for (int c = 0; c < n; c++)
        {
            channel[c] = kind[c] == DrainageCellKind.Land && visited[c] && lakeId[c] < 0 && down[c] >= 0 && area[c] * cellArea >= threshold - 1e-9 && terminalKind[c] != DrainageCellKind.Barrier;
            if (channel[c]) channelCells++;
        }
        var mainUp = new int[n];
        Array.Fill(mainUp, -1);
        for (int c = 0; c < n; c++)
        {
            if (!channel[c]) continue;
            int next = down[c];
            if (!channel[next]) continue;
            int m = mainUp[next];
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
        int stemCount = chains.Count;
        var tmp = new StemTmp[stemCount];
        var cellStem = new int[n];
        var cellIndex = new int[n];
        Array.Fill(cellStem, -1);
        for (int i = 0; i < stemCount; i++)
        {
            tmp[i] = new StemTmp { Cells = chains[i] };
            for (int k = 0; k < chains[i].Length; k++) { cellStem[chains[i][k]] = i; cellIndex[chains[i][k]] = k; }
        }
        var report0 = new int[6];
        for (int i = 0; i < stemCount; i++)
        {
            StemTmp s = tmp[i];
            int last = s.Cells[^1], next = down[last];
            if (channel[next]) { s.Terminal = RiverTerminal.Confluence; s.ReceiverStem = cellStem[next]; s.TerminalId = s.ReceiverStem; s.ReceiverNodeIndex = cellIndex[next]; }
            else if (lakeId[next] >= 0) { s.Terminal = RiverTerminal.Lake; s.TerminalId = lakeId[next]; }
            else if (kind[next] == DrainageCellKind.Ocean) s.Terminal = RiverTerminal.Sea;
            else if (kind[next] == DrainageCellKind.Lung) s.Terminal = RiverTerminal.Lung;
            else if (kind[next] == DrainageCellKind.StripOutlet) s.Terminal = RiverTerminal.StripEnd;
            else s.Terminal = RiverTerminal.Dead;
            report0[(int)s.Terminal]++;
        }

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
            for (int k = 1; k < s.Cells.Length; k++) if (reachStartCell[s.Cells[k]]) starts.Add(k);
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
            if (channel[c])
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
                    double ex = hashJitter(seed, endCell, 1), ey = hashJitter(seed, endCell, 2);
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
                if (options.BarrierGuard) { (int movedNodes, double movedMax) = GuardBarriers(s, kind, spec, width, f.Height); guardMoved += movedNodes; guardMax = Math.Max(guardMax, movedMax); }
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
                double start = r == 0 ? 0 : Math.Clamp(cellJunction.TryGetValue(s.Cells[starts[r]], out double j) ? j : previous + 1, previous + 1e-3, length);
                reaches[reachBase[i] + r].SStart = (float)start;
                if (r > 0) reaches[reachBase[i] + r - 1].SEnd = (float)start;
                previous = start;
            }
            reaches[reachBase[i] + starts.Count - 1].SEnd = (float)length;
        }
        var net = new RiverNetwork(spec.CircumferenceMeters, widthMeters, seed, meander * options.MeanderScale, valley, depthScale, ferocity, aroundDm, acrossDm, stems, reaches);
        // Phases chain through the reaches of a stem so the meander is continuous: needs wavelengths, which need the sections.
        for (int i = 0; i < stemCount; i++)
        {
            double phase = 2 * Math.PI * RiverHash.Unit(seed, i, 7, 3);
            for (int r = 0; r < stems[i].ReachCount; r++)
            {
                int ri = reachBase[i] + r;
                net.Reaches[ri].MeanderPhase = (float)phase;
                double lambda = RiverHydraulics.MeanderWavelength(net.Sections[ri].Width);
                phase += 2 * Math.PI * (net.Reaches[ri].SEnd - net.Reaches[ri].SStart) / lambda;
            }
        }
        double lengthKm = net.Curves.Sum(c => c.Length) / 1000d;
        double maxMeander = 0;
        for (int i = 0; i < net.Reaches.Length; i++) maxMeander = Math.Max(maxMeander, RiverHydraulics.MeanderAmplitude(net.Sections[i].Width, meander, net.Sections[i].Gradient));
        report = new RiverBuildReport
        {
            ThresholdKm2 = threshold, ChannelCells = channelCells, Stems = stemCount, Reaches = reaches.Length, Nodes = totalNodes, LengthKm = lengthKm,
            TerminalSea = report0[(int)RiverTerminal.Sea], TerminalLake = report0[(int)RiverTerminal.Lake], TerminalConfluence = report0[(int)RiverTerminal.Confluence],
            TerminalLung = report0[(int)RiverTerminal.Lung], TerminalStripEnd = report0[(int)RiverTerminal.StripEnd], TerminalDead = report0[(int)RiverTerminal.Dead],
            CentrelineSeconds = centrelineSeconds, MaxWanderMeters = maxWander, MaxMeanderMeters = maxMeander, MaxNodeShiftMeters = maxNodeShift, MaxSmoothMeters = maxSmooth, SnappedNodes = snapped, GuardMovedNodes = guardMoved, GuardMaxMeters = guardMax,
        };
        if (options.Levels && options.Source != null)
        {
            RiverLevel.Build(net, f, grid, options, cancellation, ref report);
        }
        report = HydraulicsSummary(net, report);
        return net;

        double hashJitter(long sd, int cell, int axis) => JitterMeters * RiverHash.Signed(sd, cell, 0, axis);
    }

    private const int SnapChunkPoints = 2_000_000;

    /// <summary>Clearance the centreline keeps from barrier cells: the meander cap (applied at query time) plus a margin.</summary>
    public const double BarrierClearanceMeters = RiverHydraulics.MeanderAmplitudeCapMeters + 5d;

    /// <summary>
    /// Barrier guard (T2R-6b review): after smoothing, wander and the shift clamp, no sample of the node curve may come closer than <see cref="BarrierClearanceMeters"/> to a barrier cell, so the curve
    /// plus the meander (at most 100 m, applied at query time) never enters one. Offending samples push the node under them (and half of each neighbour) away from the nearest barrier cell; the last
    /// node (a junction or the end in water) never moves. Fixed order and fixed passes, so it is deterministic. Returns the number of nodes moved and the largest total move.
    /// </summary>
    private static (int Moved, double MaxMove) GuardBarriers(StemTmp s, DrainageCellKind[] kind, DrainageGridSpec spec, int width, int rows)
    {
        int n = s.X.Length;
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
            return ring < width && kind[rj * width + (int)ring] == DrainageCellKind.Barrier;
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
                        if (node < 0 || node >= n - 1) continue;
                        if (Math.Abs(px * w) > Math.Abs(dxs[node])) dxs[node] = px * w;
                        if (Math.Abs(py * w) > Math.Abs(dys[node])) dys[node] = py * w;
                    }
                }
            if (!any) break;
            for (int k = 0; k < n - 1; k++) { if (dxs[k] != 0 || dys[k] != 0) moved[k] = true; s.X[k] += dxs[k]; s.Y[k] += dys[k]; }
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
