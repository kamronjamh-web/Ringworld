using System;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>What an inland-water owner reports at a sample (S7, S9): none, a river channel, or a lake (T2R-8).</summary>
public enum InlandWaterKind : byte { None, River, Lake }

/// <summary>The river operator's answer at one point (S7): the carved height, the owner and its level, and the fields the consumers read.</summary>
public readonly record struct RiverOperatorResult(double Height, InlandWaterKind Kind, double WaterLevel, int Reach, double ChannelOffset, double CorridorWeight, double GroundDelta);

/// <summary>
/// Where a point lies in the channel that owns it (S7, T2R-7 review): the owner piece, its stem and reach, the stem arc parameter of the closest point, the signed
/// lateral coordinate against the meandered centreline (left positive), the unit tangent of that centreline (downstream), the section values used there (widened by the
/// footprint, so the plain values at footprint 0) and the water level at the point. With <see cref="RiverCarveOperator.CoreProfile"/> this gives the bed without a height-model call.
/// </summary>
public readonly record struct RiverLocation(int Piece, int Stem, int Reach, double S, double T, double TangentAround, double TangentAcross, double HalfWidth, double BankWidth, double Depth, double EdgeDepth, double Level);

/// <summary>
/// The river operator of docs/work-orders/terrain-vegetation/T2.md S7 (T2R-7): per curve piece within reach, the section (bed, bank, then the natural ground clamped between a
/// levee bound and a valley bound, faded out by 300 m), combined over overlapping pieces. Where channels of different levels come closer than their cores, the higher one's
/// level is drawn down to the lower one's (a chute into it), so every overlap holds one level and containment is by construction. It is applied to the natural height before
/// the site operators and is never part of <see cref="IDrainageHeightSource"/>. Immutable and thread-safe; <c>Near == 0</c> returns before any piece work.
/// </summary>
public sealed class RiverCarveOperator
{
    /// <summary>Beyond this distance from the centreline the fade has removed every effect (S7).</summary>
    public const double FadeEndMeters = 300d;
    private const double FadeStartCapMeters = 250d;
    private const double LeveeSlope = 0.25;
    /// <summary>A channel caps how fast another reach's levee may rise away from its bank (T2R-7 fix 2), so a tight bend beside a higher reach has no cliff at the core edge. 2 m per m keeps 0.25 m steps under 0.6 m.</summary>
    private const double BankCapSlope = 2.0;
    private const double TerraceStepMeters = 40d, TerraceRiser = 0.14;
    /// <summary>A piece whose control hull is further than this from the point cannot reach |t| < 300 (the meander is at most 100 m).</summary>
    private const double CullMeters = FadeEndMeters + RiverHydraulics.MeanderAmplitudeCapMeters + 10d;
    private const double ValleyOnlyAboveFootprint = 256d, ValleyOnlyNarrowAboveFootprint = 64d;
    private const int MaxHits = 8;

    // Draw-down of a higher channel towards a lower one (T2R-7 review). Inside the lower core and this margin beyond it the higher level equals the lower one; it then
    // returns over a run of 10 m per metre of difference (the chute slope of the level profile), between 10 and 120 m. The margin exceeds the 2 m containment distance.
    // Measured in the review: a linear return over 40 m per metre was worse at three-way junctions (largest violation 0.90 m against 0.31 m).
    private const double DrawMarginMeters = 2.5, DrawRunPerMeter = 10d, DrawRunMinMeters = 10d, DrawRunMaxMeters = 120d;

    // Look of the valley (T2R-7 review). The side leaves the floodplain through a toe of 1.5 widths (slope 0 at the floodplain edge, the full valley slope far out); the
    // floor keeps a share of the natural relief above it, saturating at ReliefCap; the meeting line of the valley bound and the natural ground is rounded.
    private const double ToeWidths = 1.5, ToeMinMeters = 12d, ToeMaxMeters = 90d;
    private const double ReliefShare = 0.2, ReliefCapMeters = 3d, ReliefRunWidths = 0.5, ReliefRunMinMeters = 10d, ReliefRunMaxMeters = 40d;
    private const double RoundPerMeter = 0.1, RoundMaxMeters = 4d;
    private const double FloorRiseRunMeters = 8d;

    private readonly RiverGraph _graph;
    private readonly RiverNetwork _net;
    private readonly RiverSpatialIndex _index;
    private readonly double _halfCircumference;

    /// <summary>Test hook for the negative control (S7 "Checked by"): false makes the core an upper bound only, so a dip in the bank zone is not lifted.</summary>
    internal bool CoreLowerBound { get; init; } = true;

    public RiverCarveOperator(RiverGraph graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _net = graph.Network;
        _index = graph.Index;
        _halfCircumference = 0.5 * _net.CircumferenceM;
    }

    public RiverGraph Graph => _graph;

    // ------------------------------------------------------------------ geometry of one piece

    private struct Hit
    {
        public int Piece, Stem, Reach;
        public bool ValleyOnly;
        public double S, T, AbsT, TanX, TanY;
        /// <summary>Section values with the footprint applied; Edge = A + B.</summary>
        public double A, B, Depth, EdgeDepth, Edge;
        /// <summary>Half width plus bank width without the footprint (the draw-down and the valley look must not depend on the footprint), and the width.</summary>
        public double PlainEdge, Width;
        public double Floodplain, ValleySlope;
        /// <summary>The stem's level at S, the level after the draw-down towards lower channels, and the stored profile's height above the pools (0 to 1 m).</summary>
        public double Level, Drawn, FloorRise;
    }

    /// <summary>
    /// Closest point of the point to the base curve piece, the lateral coordinate against the meandered centreline and the channel values there. False when the piece
    /// cannot affect the point: outside the control hull expanded by <see cref="CullMeters"/>, owned by the neighbouring piece, or further than |t| = lateralLimit.
    /// </summary>
    private bool LocatePiece(int piece, double around, double across, double lateralLimit, out Hit hit)
    {
        hit = default;
        int stem = _graph.PieceStem[piece];
        ref readonly RiverStem st = ref _net.Stems[stem];
        int k = piece - st.NodeStart;
        RiverStemCurve curve = _net.Curves[stem];
        curve.Controls(k, out double ax, out double ay, out double px, out double py, out double cx, out double cy);
        double qx = around - (curve.OriginAround + px), qy = across - (curve.OriginAcross + py);
        // Wrap around to the nearest image (the stem is far shorter than half the ring).
        if (qx > _halfCircumference || qx < -_halfCircumference) qx = _net.WrapDelta(qx);
        // Local frame: the piece's own node P at the origin.
        ax -= px; ay -= py; cx -= px; cy -= py;
        if (qx < Math.Min(Math.Min(ax, cx), 0d) - CullMeters || qx > Math.Max(Math.Max(ax, cx), 0d) + CullMeters
            || qy < Math.Min(Math.Min(ay, cy), 0d) - CullMeters || qy > Math.Max(Math.Max(ay, cy), 0d) + CullMeters) return false;
        // B(u) = A + 2u E + u^2 F with E = P - A, F = A - 2P + C; stationary points of |B - q|^2 solve a cubic.
        double ex = -ax, ey = -ay, fx = ax + cx, fy = ay + cy;
        double mx = ax - qx, my = ay - qy;
        double c0 = mx * ex + my * ey, c1 = mx * fx + my * fy + 2 * (ex * ex + ey * ey), c2 = 3 * (ex * fx + ey * fy), c3 = fx * fx + fy * fy;
        double bestU = 0, bestD2 = Distance2(ax, ay, ex, ey, fx, fy, 0, qx, qy);
        double d2End = Distance2(ax, ay, ex, ey, fx, fy, 1, qx, qy);
        if (d2End < bestD2) { bestD2 = d2End; bestU = 1; }
        Span<double> roots = stackalloc double[3];
        int count = SolveCubic(c3, c2, c1, c0, roots);
        for (int i = 0; i < count; i++)
        {
            double u = roots[i];
            if (u <= 0 || u >= 1) continue;
            double d2 = Distance2(ax, ay, ex, ey, fx, fy, u, qx, qy);
            if (d2 < bestD2) { bestD2 = d2; bestU = u; }
        }
        double s = curve.S0[k] + curve.Len[k] * bestU;
        double bx = ax + 2 * bestU * ex + bestU * bestU * fx, by = ay + 2 * bestU * ey + bestU * bestU * fy;
        double tx = ex + bestU * fx, ty = ey + bestU * fy, norm = Math.Sqrt(tx * tx + ty * ty);
        if (norm < 1e-9) { tx = cx - ax; ty = cy - ay; norm = Math.Sqrt(tx * tx + ty * ty); }
        if (norm < 1e-12) { tx = 1; ty = 0; norm = 1; }
        tx /= norm; ty /= norm;
        // A joint between two pieces: the neighbouring piece owns every point whose closest point lies beyond it (the sign of the lateral distance is meaningless there).
        if (((bestU == 0d && k > 0) || (bestU == 1d && k < st.NodeCount - 1)) && Math.Abs((qx - bx) * tx + (qy - by) * ty) > 0.05) return false;
        double dist = Math.Sqrt(bestD2);
        _net.ChannelAt(stem, s, out RiverChannel channel);
        // Too far for any |t| below the limit, whatever the meander does: skip before the meander work.
        if (dist > _net.LateralReach(channel.Reach, lateralLimit)) return false;
        double signed = (qx - bx) * -ty + (qy - by) * tx >= 0 ? dist : -dist;
        // Lateral coordinate against the displaced centreline: (n - D) / sqrt(1 + D'^2), D' by a 1 m one-sided difference.
        double sOther = s + 1d <= curve.Length ? s + 1d : s - 1d;
        double d0 = _net.Meander(stem, s, in channel), slope = (_net.Meander(stem, sOther) - d0) / (sOther - s);
        double stretch = Math.Sqrt(1 + slope * slope);
        hit.Piece = piece; hit.Stem = stem; hit.Reach = channel.Reach; hit.S = s;
        hit.T = (signed - d0) / stretch;
        hit.AbsT = Math.Abs(hit.T);
        // Unit tangent of the displaced centreline: the base tangent turned by the meander's slope.
        hit.TanX = (tx - slope * ty) / stretch; hit.TanY = (ty + slope * tx) / stretch;
        hit.A = channel.HalfWidth; hit.B = channel.BankWidth; hit.Depth = channel.Depth; hit.EdgeDepth = channel.EdgeDepth;
        hit.Floodplain = channel.Floodplain; hit.ValleySlope = channel.ValleySlope;
        hit.PlainEdge = hit.A + hit.B; hit.Width = 2d * hit.A; hit.Edge = hit.PlainEdge;
        return true;
    }

    private static double Distance2(double ax, double ay, double ex, double ey, double fx, double fy, double u, double qx, double qy)
    {
        double dx = ax + 2 * u * ex + u * u * fx - qx, dy = ay + 2 * u * ey + u * u * fy - qy;
        return dx * dx + dy * dy;
    }

    /// <summary>Real roots of c3 u^3 + c2 u^2 + c1 u + c0 (closed form; degenerate leading terms fall back to the quadratic and the line).</summary>
    private static int SolveCubic(double c3, double c2, double c1, double c0, Span<double> roots)
    {
        if (Math.Abs(c3) < 1e-9)
        {
            if (Math.Abs(c2) < 1e-9)
            {
                if (Math.Abs(c1) < 1e-12) return 0;
                roots[0] = -c0 / c1;
                return 1;
            }
            double disc = c1 * c1 - 4 * c2 * c0;
            if (disc < 0) return 0;
            double sq = Math.Sqrt(disc);
            roots[0] = (-c1 + sq) / (2 * c2); roots[1] = (-c1 - sq) / (2 * c2);
            return 2;
        }
        double a2 = c2 / c3, a1 = c1 / c3, a0 = c0 / c3;
        double q = (a2 * a2 - 3 * a1) / 9d, r = (2 * a2 * a2 * a2 - 9 * a2 * a1 + 27 * a0) / 54d, shift = a2 / 3d;
        double q3 = q * q * q;
        if (r * r < q3)
        {
            double theta = Math.Acos(Math.Clamp(r / Math.Sqrt(q3), -1d, 1d)), m = -2 * Math.Sqrt(q);
            roots[0] = m * Math.Cos(theta / 3) - shift;
            roots[1] = m * Math.Cos((theta + 2 * Math.PI) / 3) - shift;
            roots[2] = m * Math.Cos((theta - 2 * Math.PI) / 3) - shift;
            return 3;
        }
        double big = -Math.Sign(r) * Math.Cbrt(Math.Abs(r) + Math.Sqrt(Math.Max(0, r * r - q3)));
        roots[0] = big + (big == 0 ? 0 : q / big) - shift;
        return 1;
    }

    private static double Terrace(double x) { double q = x / TerraceStepMeters, b = Math.Floor(q); return TerraceStepMeters * (b + RiverHydraulics.Smoothstep(0d, TerraceRiser, q - b)); }

    /// <summary>The valley profile beyond the flat floodplain, terraced into 40 m strata above 40 m of cut (F4).</summary>
    private static double Valley(double v) => v <= 0 ? 0 : v + (Terrace(v) - v) * RiverHydraulics.Smoothstep(40d, 60d, v);

    /// <summary>Polynomial smooth minimum: min(a, b) lowered by at most k / 4 where the two are closer than k. Never above either.</summary>
    private static double SmoothMin(double a, double b, double k)
    {
        double m = a < b ? a : b;
        if (k <= 1e-6) return m;
        double h = k - Math.Abs(a - b);
        return h <= 0d ? m : m - h * h / (4d * k);
    }

    /// <summary>S7 footprint rule: is the piece only a valley at this footprint (no core, no owner).</summary>
    private static bool ValleyOnly(double footprint, double halfWidth) => footprint > ValleyOnlyAboveFootprint || (footprint > ValleyOnlyNarrowAboveFootprint && 2d * halfWidth < footprint / 16d);

    /// <summary>
    /// The core of S7 as a pure function: the bed <c>L - d_e - (d - d_e) (1 - (t / a)^2)</c> inside the half width, then the bank rising to the level plus the freeboard
    /// at <c>a + b</c>. The carve and the flow field (T3) share it, so the flow's bed is the carved bed. Arguments are the values of a <see cref="RiverLocation"/>.
    /// </summary>
    public static double CoreProfile(double absT, double level, double halfWidth, double bankWidth, double depth, double edgeDepth)
        => absT < halfWidth
            ? level - edgeDepth - (depth - edgeDepth) * (1d - absT * absT / (halfWidth * halfWidth))
            : level - edgeDepth + (edgeDepth + RiverHydraulics.Freeboard) * RiverHydraulics.Smoothstep(halfWidth, halfWidth + bankWidth, absT);

    // ------------------------------------------------------------------ the pieces at a point

    /// <summary>
    /// Every piece that acts on the point, with its level and the level after the draw-down, sorted by level. The caller has checked <c>Near</c> for the cell. The
    /// corridor weight comes out as a by-product (it also counts pieces beyond the fade end at coarse footprints).
    /// </summary>
    private int Collect(int col, int row, double around, double across, double fp, Span<Hit> hits, out double weight)
    {
        weight = 0d;
        // Only the corridor weight of a widened channel reaches beyond the fade end.
        double lateralLimit = fp > 0d && fp <= ValleyOnlyAboveFootprint ? Math.Max(FadeEndMeters, 1.25 * fp + 72d) : FadeEndMeters;
        ReadOnlySpan<int> pieces = _index.PiecesNear(col, row);
        int n = 0;
        for (int i = 0; i < pieces.Length; i++)
        {
            if (!LocatePiece(pieces[i], around, across, lateralLimit, out Hit h)) continue;
            h.ValleyOnly = ValleyOnly(fp, h.A);
            if (!h.ValleyOnly && fp > 0d)
            {
                double ap = Math.Max(h.A, 0.75 * fp);
                h.Depth = Math.Max(h.Depth * h.A / ap, 0.05);
                h.A = ap; h.B = Math.Max(h.B, 0.5 * fp);
                h.EdgeDepth = Math.Min(h.EdgeDepth, 0.5 * h.Depth);
                h.Edge = h.A + h.B;
            }
            if (h.AbsT >= FadeEndMeters && h.AbsT >= h.Edge + 60d) continue;
            double w = 1d - RiverHydraulics.Smoothstep(h.Edge + 10d, h.Edge + 60d, h.AbsT);
            if (w > weight) weight = w;
            if (h.AbsT >= FadeEndMeters) continue;
            double level = RiverLevel.AtStem(_net, h.Stem, h.S, out double floorRise);
            if (double.IsNaN(level)) continue;
            h.Level = level; h.Drawn = level; h.FloorRise = floorRise;
            if (n < MaxHits) { hits[n++] = h; continue; }
            // More pieces than slots (never seen): keep the nearest ones.
            int far = 0;
            for (int j = 1; j < n; j++) if (hits[j].AbsT > hits[far].AbsT) far = j;
            if (h.AbsT < hits[far].AbsT) hits[far] = h;
        }
        if (n > 1) DrawDown(hits.Slice(0, n));
        return n;
    }

    /// <summary>
    /// T2R-7 review: sorts the pieces by level and draws each one down towards every lower one near the point. Inside a lower channel's core (at footprint 0) and
    /// <see cref="DrawMarginMeters"/> beyond it the higher piece takes the lower level, so every point that two cores cover has one level; further out the level returns
    /// over a chute-slope run. It depends on the point and the plain geometry only, never on the footprint, so the level stays the same at every footprint.
    /// </summary>
    private static void DrawDown(Span<Hit> hits)
    {
        int n = hits.Length;
        for (int i = 1; i < n; i++)
        {
            Hit h = hits[i];
            int j = i - 1;
            while (j >= 0 && (hits[j].Level > h.Level || (hits[j].Level == h.Level && hits[j].Piece > h.Piece))) { hits[j + 1] = hits[j]; j--; }
            hits[j + 1] = h;
        }
        for (int i = 1; i < n; i++)
        {
            double own = hits[i].Level, drawn = own;
            for (int j = 0; j < i; j++)
            {
                double low = hits[j].Drawn, drop = own - low;
                if (drop <= 0d) continue;
                double start = hits[j].PlainEdge + DrawMarginMeters, absT = hits[j].AbsT, candidate;
                if (absT <= start) candidate = low;
                else
                {
                    double end = Math.Min(start + Math.Clamp(DrawRunPerMeter * drop, DrawRunMinMeters, DrawRunMaxMeters), FadeEndMeters);
                    if (absT >= end) continue;
                    candidate = own - (1d - RiverHydraulics.Smoothstep(start, end, absT)) * drop;
                }
                if (candidate < drawn) drawn = candidate;
            }
            hits[i].Drawn = drawn;
        }
    }

    /// <summary>The owner (S7): the core piece with the smallest <c>|t| / (a' + b')</c> (tie: lower stem, then lower piece), or -1.</summary>
    private static int Owner(ReadOnlySpan<Hit> hits)
    {
        int owner = -1;
        double best = double.PositiveInfinity;
        for (int i = 0; i < hits.Length; i++)
        {
            ref readonly Hit h = ref hits[i];
            if (h.ValleyOnly || h.AbsT >= h.Edge) continue;
            double ratio = h.AbsT / h.Edge;
            if (ratio < best || (ratio == best && (h.Stem < hits[owner].Stem || (h.Stem == hits[owner].Stem && h.Piece < hits[owner].Piece)))) { best = ratio; owner = i; }
        }
        return owner;
    }

    // ------------------------------------------------------------------ queries

    /// <summary>
    /// The one location method (T2R-7 review): which channel owns the point and where the point lies in it. The same pieces, draw-down and owner rule as
    /// <see cref="Evaluate"/>, without the ground; <see cref="Evaluate"/> reports the owner only where the natural ground is at or above 0. False where no core covers the point.
    /// </summary>
    public bool Locate(double around, double across, double footprintMeters, out RiverLocation location)
    {
        location = default;
        int col = _index.ColumnOf(around), row = _index.RowOf(across);
        if (!_index.IsNear(col, row)) return false;
        Span<Hit> hits = stackalloc Hit[MaxHits];
        int n = Collect(col, row, around, across, footprintMeters, hits, out _);
        int owner = n > 0 ? Owner(hits.Slice(0, n)) : -1;
        if (owner < 0) return false;
        ref readonly Hit h = ref hits[owner];
        location = new RiverLocation(h.Piece, h.Stem, h.Reach, h.S, h.T, h.TanX, h.TanY, h.A, h.B, h.Depth, h.EdgeDepth, h.Drawn);
        return true;
    }

    /// <summary>The carved height and the owner at the point; <paramref name="naturalHeight"/> is the natural ground G there and <paramref name="footprintMeters"/> the sample spacing (0 for a point; +infinity for the far views).</summary>
    public RiverOperatorResult Evaluate(double around, double across, double naturalHeight, double footprintMeters)
    {
        double g = naturalHeight;
        int col = _index.ColumnOf(around), row = _index.RowOf(across);
        if (!_index.IsNear(col, row)) return new RiverOperatorResult(g, InlandWaterKind.None, double.NaN, -1, 0d, 0d, 0d);
        Span<Hit> hits = stackalloc Hit[MaxHits];
        int n = Collect(col, row, around, across, footprintMeters, hits, out double weight);
        if (n == 0) return new RiverOperatorResult(g, InlandWaterKind.None, double.NaN, -1, 0d, weight, 0d);
        const double f = RiverHydraulics.Freeboard;
        double hiCore = double.PositiveInfinity, valley = g, lo = double.NegativeInfinity, loCap = double.PositiveInfinity;
        double seaGuard = RiverHydraulics.Smoothstep(-1d, 0d, g);
        for (int i = 0; i < n; i++)
        {
            ref readonly Hit h = ref hits[i];
            double level = h.Drawn, absT = h.AbsT;
            bool core = !h.ValleyOnly && absT < h.Edge;
            double pieceHi, pieceLo, round = 0d;
            if (core)
            {
                pieceHi = CoreProfile(absT, level, h.A, h.B, h.Depth, h.EdgeDepth);
                pieceLo = CoreLowerBound ? pieceHi : double.NegativeInfinity;
            }
            else
            {
                double u = Math.Max(0d, absT - h.Edge);
                pieceLo = h.ValleyOnly ? double.NegativeInfinity : level + f - LeveeSlope * u;
                // Valley bound: the floor rises from the bank top to the smooth profile, then the side leaves it through a toe.
                double x = u - h.Floodplain, side = 0d;
                if (x > 0d) side = h.ValleySlope * x * x / (x + Math.Clamp(ToeWidths * h.Width, ToeMinMeters, ToeMaxMeters));
                pieceHi = level + f + h.FloorRise * RiverHydraulics.Smoothstep(0d, Math.Max(FloorRiseRunMeters, h.B), u) + Valley(side);
                // The ground above the bound keeps a saturating share of its relief, so the floor is not a dead-flat shelf.
                double over = g - pieceHi;
                if (over > 0d)
                {
                    double r = ReliefShare * over;
                    pieceHi += r * ReliefCapMeters / (r + ReliefCapMeters) * RiverHydraulics.Smoothstep(0d, Math.Clamp(ReliefRunWidths * h.Width, ReliefRunMinMeters, ReliefRunMaxMeters), u);
                }
                round = Math.Min(RoundMaxMeters, RoundPerMeter * u);
            }
            double fadeStart = Math.Min(h.Edge + h.Floodplain + 20d, FadeStartCapMeters);
            double fade = 1d - RiverHydraulics.Smoothstep(fadeStart, FadeEndMeters, absT), away = 1d - fade;
            if (away > 0d)
            {
                pieceHi += away * Math.Max(0d, g - pieceHi);
                if (!double.IsNegativeInfinity(pieceLo)) pieceLo -= away * Math.Max(0d, pieceLo - g);
                round *= fade;
            }
            if (!double.IsNegativeInfinity(pieceLo)) pieceLo = g + seaGuard * (pieceLo - g);
            if (!h.ValleyOnly)
            {
                double cap = level + f + BankCapSlope * Math.Max(0d, absT - h.Edge);
                if (cap < loCap) loCap = cap;
            }
            if (core) { if (pieceHi < hiCore) hiCore = pieceHi; }
            else
            {
                double clamped = SmoothMin(g, pieceHi, round);
                if (clamped < valley) valley = clamped;
            }
            if (pieceLo > lo) lo = pieceLo;
        }
        // Combination (S7, T2R-7): valley bounds clamp the ground from above, every lower bound (bank, levee) then lifts it, and a channel core caps the result, so a channel
        // always beats another reach's levee or valley and the highest lower bound beats a lower reach's valley (a bend beside a lower reach must not cut this reach's bank).
        double height = valley;
        if (lo > loCap) lo = loCap;
        if (lo > height) height = lo;
        if (hiCore < height) height = hiCore;
        int owner = g >= 0d ? Owner(hits.Slice(0, n)) : -1;
        if (owner < 0) return new RiverOperatorResult(height, InlandWaterKind.None, double.NaN, -1, 0d, weight, height - g);
        return new RiverOperatorResult(height, InlandWaterKind.River, hits[owner].Drawn, hits[owner].Reach, hits[owner].T, weight, height - g);
    }

    /// <summary>
    /// 0 outside every river's corridor, 1 on the bed and bank, continuous in between: <c>max_i (1 - smoothstep(a' + b' + 10, a' + b' + 60, |t_i|))</c> (S7). Consumers
    /// (vegetation, far water) read it without the ground.
    /// </summary>
    public double CorridorWeight(double around, double across, double footprintMeters)
    {
        int col = _index.ColumnOf(around), row = _index.RowOf(across);
        if (!_index.IsNear(col, row)) return 0d;
        Span<Hit> hits = stackalloc Hit[MaxHits];
        Collect(col, row, around, across, footprintMeters, hits, out double weight);
        return weight;
    }
}
