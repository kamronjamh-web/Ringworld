using System;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>Result of scoring one row's centre-line (across = W/2) main-site candidate.</summary>
internal struct MainRowEvaluation
{
    public int Row;
    public long Around;
    public int Across;
    public double Score;
    public double Wet;
    public int Pad;
    public bool Conflict;
    public double Cover;
    public double CutFill;
    public bool Excluded;
    /// <summary>Dry, flat enough, no cover problem and within the recipe's cut/fill budget (dense water check still pending).</summary>
    public bool Acceptable;
}

/// <summary>
/// Worker-confined builder for single registry records (spec section 1.3, without the approach road).
/// Each instance owns one <see cref="INaturalHeightSource"/> copy and must be used by one thread at a time.
/// Stencils, scan radii and cover depth scale with the site kind's shape constants (identical to the original regular-site
/// constants for <see cref="SiteKind.Regular"/>).
/// </summary>
internal sealed class EmitterSiteBuilder : IDisposable
{
    private const int PadStencilCount = 17;
    private const int EnclosureStencilCount = 25;
    private const int ExtraEnclosureCount = 12;
    private const int CandidateCount = 33;
    private const double HysteresisScore = 0.15d;
    private const int MainAlongRingStepsKm = 4;
    private static readonly double[] BearingSampleDistances = { 450d, 700d, 1000d, 1500d, 2000d };
    private static readonly double[] MainBearingSampleDistances = { 1150d, 1800d, 2570d, 3860d, 5140d };
    private static readonly double[] RegularScanRadii = { 410d, 600d, 1000d, 1500d, 2000d, 3000d };
    private static readonly double[] MainScanRadii = { 1150d, 1500d, 2250d, 3000d, 4500d, 6000d };
    private static readonly double[] RegularEstimateRadii = { 700d, 1500d, 2500d };
    private static readonly double[] MainEstimateRadii = { 1400d, 3000d, 5000d };

    private readonly EmitterSiteRecipe _recipe;
    private readonly INaturalHeightSource _source;
    private readonly long _circumference;
    private readonly double _sea, _wetThreshold, _raiseRadius, _mainRaiseRadius;
    private readonly bool _hasExclusion;
    private readonly long _exclusionAround;
    private readonly int _exclusionAcross;
    private readonly double _exclusionRadius;
    private readonly double _minAcross, _maxAcross;
    private readonly double[] _scratch = new double[PadStencilCount];

    private struct Candidate
    {
        public long Around;
        public int Across;
        public double Score;
        public double Wet;
        public int Pad;
        public bool Conflict;
        public double Cover;
    }

    public EmitterSiteBuilder(EmitterSiteRecipe recipe, INaturalHeightSource workerSource, INaturalHeightSource original)
    {
        _recipe = recipe;
        _source = workerSource;
        _circumference = (long)recipe.CircumferenceM;
        _sea = workerSource.SeaLevel;
        _wetThreshold = _sea + recipe.DryMarginM;
        _raiseRadius = ComputeRaiseRadius(recipe, recipe.Regular, workerSource);
        _mainRaiseRadius = ComputeRaiseRadius(recipe, recipe.Main, workerSource);
        double influenceMax = Math.Max(recipe.SkirtMaxRadiusM, _raiseRadius);
        _minAcross = influenceMax + 1000d;
        _maxAcross = recipe.WidthM - influenceMax - 1000d;
        _hasExclusion = original.TryGetExclusionZone(out _exclusionAround, out _exclusionAcross, out _exclusionRadius);
    }

    public void Dispose() => _source.Dispose();

    /// <summary>Conservative raise radius: R_shelf + (Sea - shelf_depth - MinPossible + soft_k) / flank_slope.</summary>
    private static double ComputeRaiseRadius(EmitterSiteRecipe r, EmitterSiteKindShape shape, INaturalHeightSource source)
    {
        double drop = source.SeaLevel - r.ShelfDepthM - source.MinPossibleHeight + r.IslandSoftK;
        return shape.ShelfRadiusM + Math.Max(0d, drop) / r.FlankSlope;
    }

    // ---- regular sites ----------------------------------------------------------------------------------

    /// <summary>
    /// Builds the regular site at grid cell (<paramref name="row"/>, <paramref name="col"/>). <paramref name="index"/> is the
    /// registry index; the per-cell hash label uses row * columns + col so that a cell's nudge and bearing do not depend on
    /// where the main row sits.
    /// </summary>
    public EmitterSite BuildRegular(int row, int col, int index)
    {
        EmitterSiteKindShape shape = _recipe.Regular;
        int slot = row * _recipe.Columns + col;
        long gridAround = GridAround(row);
        int gridAcross = (int)EmitterSiteRecipe.Snap2(_recipe.ColumnOffsetsM[col]);

        bool ocean = EnclosureWetFraction(shape, gridAround, gridAcross) >= 0.5d;
        Candidate chosen;
        bool promoted = false;
        if (ocean)
        {
            chosen = Evaluate(shape, gridAround, gridAcross, gridAround, gridAcross, true);
        }
        else
        {
            chosen = SearchNudge(shape, slot, gridAround, gridAcross);
            // The 25-point stencil can miss narrow coves inside R_enc; a dense check promotes such sites to islands so
            // the enclosure guarantee (no water within R_enc) holds for every site.
            if (chosen.Wet <= 0d && DenseEnclosureHasWater(shape, chosen.Around, chosen.Across, 30, 48))
            {
                chosen = Evaluate(shape, chosen.Around, chosen.Across, gridAround, gridAcross, true);
                promoted = true;
            }
        }
        return Finish(SiteKind.Regular, shape, index, slot, chosen, gridAround, gridAcross, ocean, promoted, ocean || promoted || chosen.Wet > 0d);
    }

    // ---- main site ---------------------------------------------------------------------------------------

    /// <summary>Grid point of a row (also used for the main site's row).</summary>
    private long GridAround(int row)
        => Wrap((long)EmitterSiteRecipe.Snap2(_recipe.RowPhaseM + row * _recipe.RowPitchM));

    /// <summary>
    /// Scores the centre-line candidate of <paramref name="row"/>: the grid point plus along-ring nudges of up to
    /// +-4 km (across stays exactly W/2 so the beam is vertical to the hub).
    /// </summary>
    public MainRowEvaluation EvaluateMainRow(int row)
    {
        EmitterSiteKindShape shape = _recipe.Main;
        long gridAround = GridAround(row);
        int across = (int)_recipe.MainAcrossM;
        Candidate grid = Evaluate(shape, gridAround, across, gridAround, across, false);
        Candidate best = grid;
        for (int step = 1; step <= MainAlongRingStepsKm; step++)
        {
            for (int sign = 1; sign >= -1; sign -= 2)
            {
                long a = Wrap((long)EmitterSiteRecipe.Snap2(gridAround + sign * step * 1000d));
                if (IsExcluded(shape, a, across)) continue;
                Candidate candidate = Evaluate(shape, a, across, gridAround, across, false, best.Score);
                if (candidate.Score < best.Score) best = candidate;
            }
        }
        Candidate chosen = best.Score < grid.Score - HysteresisScore ? best : grid;
        bool excluded = IsExcluded(shape, chosen.Around, chosen.Across);
        double cutFill = EstimateCutFill(shape, chosen.Around, chosen.Across, chosen.Pad, MainEstimateRadii);
        bool acceptable = !excluded && chosen.Wet <= 0d && !chosen.Conflict && chosen.Cover <= 0d && cutFill <= _recipe.MainMaxCutFillM;
        return new MainRowEvaluation
        {
            Row = row, Around = chosen.Around, Across = chosen.Across, Score = chosen.Score, Wet = chosen.Wet, Pad = chosen.Pad,
            Conflict = chosen.Conflict, Cover = chosen.Cover, CutFill = cutFill, Excluded = excluded, Acceptable = acceptable,
        };
    }

    /// <summary>True when the row's grid point lies inside the gen-16 summit exclusion zone (main fallback must avoid it).</summary>
    public bool GridIsExcluded(int row) => IsExcluded(_recipe.Main, GridAround(row), (int)_recipe.MainAcrossM);

    /// <summary>Dense water check over the whole main enclosure (40 rings x 96 azimuths) for a prospective land site.</summary>
    public bool MainEnclosureIsDry(in MainRowEvaluation e)
        => !DenseEnclosureHasWater(_recipe.Main, e.Around, e.Across, 40, 96);

    /// <summary>
    /// Builds the main record at the evaluated land candidate, or, with <paramref name="islandFallback"/>, an enlarged island at
    /// the un-nudged grid point of <paramref name="evaluation"/>'s row.
    /// </summary>
    public EmitterSite BuildMain(in MainRowEvaluation evaluation, bool islandFallback, int index)
    {
        EmitterSiteKindShape shape = _recipe.Main;
        int slot = _recipe.Rows * _recipe.Columns; // hash label slot after the 600 regular grid cells
        long gridAround = GridAround(evaluation.Row);
        int gridAcross = (int)_recipe.MainAcrossM;
        if (islandFallback)
        {
            Candidate forced = Evaluate(shape, gridAround, gridAcross, gridAround, gridAcross, true);
            bool ocean = EnclosureWetFraction(shape, gridAround, gridAcross) >= 0.5d;
            return Finish(SiteKind.Main, shape, index, slot, forced, gridAround, gridAcross, ocean, false, true);
        }
        Candidate chosen = Evaluate(shape, evaluation.Around, evaluation.Across, gridAround, gridAcross, false);
        return Finish(SiteKind.Main, shape, index, slot, chosen, gridAround, gridAcross, false, false, chosen.Wet > 0d);
    }

    // ---- shared record assembly ---------------------------------------------------------------------------

    private EmitterSite Finish(SiteKind kind, EmitterSiteKindShape shape, int index, int slot, Candidate chosen,
        long gridAround, int gridAcross, bool ocean, bool promoted, bool island)
    {
        int pad = chosen.Pad;
        EmitterSiteFlags flags = island ? EmitterSiteFlags.Island : EmitterSiteFlags.Land;
        if (ocean) flags |= EmitterSiteFlags.Ocean;
        if (chosen.Around != gridAround || chosen.Across != gridAcross) flags |= EmitterSiteFlags.Nudged;
        if (chosen.Conflict) flags |= EmitterSiteFlags.CutFillConflict;
        if (!island && pad - _sea >= _recipe.MountainPadHeightM) flags |= EmitterSiteFlags.Mountain;
        if (!island && NeedsEnclosureRaise(shape, chosen.Around, chosen.Across, pad)) flags |= EmitterSiteFlags.EnclosureRaise;

        bool raised = island || (flags & EmitterSiteFlags.EnclosureRaise) != 0;
        double raiseRadius = raised ? (kind == SiteKind.Main ? _mainRaiseRadius : _raiseRadius) : 0d;
        double influence = Math.Max(shape.SkirtMaxRadiusM, raiseRadius);
        double beta = island ? IslandBearing(slot) : LandBearing(shape, kind, chosen.Around, chosen.Across, pad);
        // World yaw in the Terrain3D frame (local Z = -across): rotating the asset's 67.5 degree entry marker by +beta.
        double yaw = 67.5d + beta;
        yaw -= Math.Floor(yaw / 360d) * 360d;
        ScanCut(shape, kind, chosen.Around, chosen.Across, pad, island, out double maxCut, out double maxFill);
        float nudge = (float)Distance(chosen.Around, chosen.Across, gridAround, gridAcross);

        var site = new EmitterSite
        {
            Index = index,
            Kind = kind,
            Around = chosen.Around,
            Across = chosen.Across,
            PadHeight = pad,
            BetaDegrees = (float)beta,
            YawDegrees = (float)yaw,
            Flags = flags,
            InfluenceRadius = (float)influence,
            RaiseRadius = (float)raiseRadius,
            NudgeMeters = nudge,
            MaxCutMeters = (float)maxCut,
            MaxFillMeters = (float)maxFill,
        };
        return WithHash(site);
    }

    private static EmitterSite WithHash(EmitterSite s)
    {
        ulong h = EmitterSiteHash.Offset;
        h = EmitterSiteHash.Mix(h, (ulong)s.Index);
        h = EmitterSiteHash.Mix(h, (ulong)s.Kind);
        h = EmitterSiteHash.Mix(h, (ulong)s.Around);
        h = EmitterSiteHash.Mix(h, (ulong)s.Across);
        h = EmitterSiteHash.Mix(h, (ulong)(long)s.PadHeight);
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.BetaDegrees));
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.YawDegrees));
        h = EmitterSiteHash.Mix(h, (ulong)s.Flags);
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.InfluenceRadius));
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.RaiseRadius));
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.MaxCutMeters));
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.MaxFillMeters));
        return new EmitterSite
        {
            Index = s.Index, Kind = s.Kind, Around = s.Around, Across = s.Across, PadHeight = s.PadHeight,
            BetaDegrees = s.BetaDegrees, YawDegrees = s.YawDegrees, Flags = s.Flags,
            InfluenceRadius = s.InfluenceRadius, RaiseRadius = s.RaiseRadius, NudgeMeters = s.NudgeMeters,
            MaxCutMeters = s.MaxCutMeters, MaxFillMeters = s.MaxFillMeters,
            SiteHash = h,
        };
    }

    // ---- nudge search (section 1.3 step 3) -------------------------------------------------------------

    private bool IsExcluded(EmitterSiteKindShape shape, long around, int across)
        => _hasExclusion && Distance(around, across, _exclusionAround, _exclusionAcross) < _exclusionRadius + shape.SkirtMaxRadiusM;

    private Candidate SearchNudge(EmitterSiteKindShape shape, int slot, long gridAround, int gridAcross)
    {
        double rotation = EmitterSiteHash.Unit(EmitterSiteHash.Derive(_recipe.Seed, "emitter-nudge", slot)) * 45d;
        Candidate grid = Evaluate(shape, gridAround, gridAcross, gridAround, gridAcross, false);
        Candidate best = grid;
        for (int c = 1; c < CandidateCount; c++)
        {
            int ring = (c - 1) / 8 + 1, dir = (c - 1) % 8;
            double angle = (rotation + dir * 45d) * Math.PI / 180d;
            double radius = ring * 1000d;
            long a = Wrap((long)EmitterSiteRecipe.Snap2(gridAround + radius * Math.Cos(angle)));
            int x = (int)EmitterSiteRecipe.Snap2(gridAcross + radius * Math.Sin(angle));
            if (x < _minAcross || x > _maxAcross) continue;
            if (_hasExclusion && Distance(a, x, _exclusionAround, _exclusionAcross)
                < _exclusionRadius + _recipe.SkirtMaxRadiusM) continue;
            Candidate candidate = Evaluate(shape, a, x, gridAround, gridAcross, false, best.Score);
            if (candidate.Score < best.Score) best = candidate;
        }
        // Hysteresis: keep the grid point unless a candidate is clearly better.
        return best.Score < grid.Score - HysteresisScore ? best : grid;
    }

    private Candidate Evaluate(EmitterSiteKindShape shape, long around, int across, long gridAround, int gridAcross, bool forceIsland,
        double ceiling = double.PositiveInfinity)
    {
        // Pad stencil: centre, 8 at 4/7 of the pad radius (R200 regular), 8 at 8/7 (R400 regular, rotated 22.5 degrees).
        double r1 = shape.PadRadiusM * 4d / 7d, r2 = shape.PadRadiusM * 8d / 7d;
        double[] pad = _scratch;
        pad[0] = H(around, across, 0, 0);
        for (int k = 0; k < 8; k++)
        {
            double angle = k * Math.PI / 4d;
            pad[1 + k] = H(around, across, r1 * Math.Cos(angle), r1 * Math.Sin(angle));
            double angle2 = angle + Math.PI / 8d;
            pad[9 + k] = H(around, across, r2 * Math.Cos(angle2), r2 * Math.Sin(angle2));
        }
        double mean = 0d, min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < PadStencilCount; i++)
        {
            mean += pad[i];
            if (pad[i] < min) min = pad[i];
            if (pad[i] > max) max = pad[i];
        }
        mean /= PadStencilCount;
        double variance = 0d;
        for (int i = 0; i < PadStencilCount; i++) variance += (pad[i] - mean) * (pad[i] - mean);
        double sigma = Math.Sqrt(variance / PadStencilCount);
        double span = max - min;

        int wetCount = 0;
        double enclosureMin = double.MaxValue;
        for (int i = 0; i < EnclosureStencilCount; i++)
        {
            double h = EnclosureSample(shape, around, across, i);
            if (h < _wetThreshold) wetCount++;
            if (h < enclosureMin) enclosureMin = h;
        }
        double wet = wetCount / (double)EnclosureStencilCount;

        bool island = forceIsland || wet > 0d;
        int padHeight = ComputePadHeight(pad, island, out bool conflict);
        double cover = Math.Max(0d, (padHeight - shape.CoverDepthM) - enclosureMin);
        double dist = Distance(around, across, gridAround, gridAcross) / _recipe.NudgeRadiusM;
        double score = sigma / 20d + span / 100d + 2d * wet + cover / 200d + 0.35d * dist;
        // D4: penalise terraced-cone cut and embankment fill so candidates needing huge carves lose to flatter ones.
        // The penalty is non-negative, so a candidate already at or above the incumbent score cannot win: skip the samples.
        if (!island && score < ceiling)
            score += EstimateCutFill(shape, around, across, padHeight, ReferenceEstimateRadii(shape)) / shape.CutFillPenaltyScaleM;
        return new Candidate { Around = around, Across = across, Score = score, Wet = wet, Pad = padHeight, Conflict = conflict, Cover = cover };
    }

    private double[] ReferenceEstimateRadii(EmitterSiteKindShape shape) => ReferenceEquals(shape, _recipe.Main) ? MainEstimateRadii : RegularEstimateRadii;

    /// <summary>Section 1.3 step 4 (D1): median of the footprint, cut and fill capped, land/island minimum.</summary>
    private int ComputePadHeight(double[] pad, bool island, out bool conflict)
    {
        Span<double> sorted = stackalloc double[PadStencilCount];
        for (int i = 0; i < PadStencilCount; i++) sorted[i] = pad[i];
        sorted.Sort();
        double median = sorted[PadStencilCount / 2];
        double min = sorted[0], max = sorted[PadStencilCount - 1];
        double p0 = Math.Round(median, MidpointRounding.AwayFromZero);
        double p1 = Math.Max(p0, Math.Ceiling(max) - _recipe.MaxPadCutM);
        double fillBound = Math.Floor(min) + _recipe.MaxPadFillM;
        double p2 = p1;
        bool clash = false;
        if (fillBound < p1) clash = true;      // cut wins on conflict
        else p2 = Math.Min(p1, fillBound);
        double minimum = _sea + (island ? _recipe.IslandPadMinM : _recipe.LandPadMinM);
        double p = Math.Max(p2, minimum);
        conflict = clash && p == p2;
        return (int)p;
    }

    /// <summary>Section 1.3 step 5: a nearby valley floor would leave the cavern roof too thin.</summary>
    private bool NeedsEnclosureRaise(EmitterSiteKindShape shape, long around, int across, int pad)
    {
        double min = double.MaxValue;
        for (int i = 0; i < EnclosureStencilCount + ExtraEnclosureCount; i++)
            min = Math.Min(min, EnclosureSample(shape, around, across, i));
        return min < pad - shape.CoverDepthM + 8d;
    }

    /// <summary>0: centre, 1..12: R/2, 13..24: R (offset 15 degrees), 25..36: 3R/4 (R = enclosure radius; 1000/2000/1500 regular).</summary>
    private double EnclosureSample(EmitterSiteKindShape shape, long around, int across, int i)
    {
        if (i == 0) return H(around, across, 0, 0);
        int ring = i < 13 ? 0 : i < 25 ? 1 : 2;
        int k = ring == 0 ? i - 1 : ring == 1 ? i - 13 : i - 25;
        double radius = ring == 0 ? shape.EnclosureRadiusM * 0.5d : ring == 1 ? shape.EnclosureRadiusM : shape.EnclosureRadiusM * 0.75d;
        double angle = (k * 30d + (ring == 1 ? 15d : 0d)) * Math.PI / 180d;
        return H(around, across, radius * Math.Cos(angle), radius * Math.Sin(angle));
    }

    /// <summary><paramref name="rings"/> radii (R/rings..R) x <paramref name="azimuths"/> azimuths (offset half a step on odd rings) inside the enclosure radius.</summary>
    private bool DenseEnclosureHasWater(EmitterSiteKindShape shape, long around, int across, int rings, int azimuths)
    {
        double step = 360d / azimuths;
        for (int ring = 1; ring <= rings; ring++)
        {
            double radius = ring * (shape.EnclosureRadiusM / rings);
            for (int k = 0; k < azimuths; k++)
            {
                double angle = (k * step + (ring & 1) * step * 0.5d) * Math.PI / 180d;
                if (H(around, across, radius * Math.Cos(angle), radius * Math.Sin(angle)) < _wetThreshold) return true;
            }
        }
        return false;
    }

    private double EnclosureWetFraction(EmitterSiteKindShape shape, long around, int across)
    {
        int wet = 0;
        for (int i = 0; i < EnclosureStencilCount; i++)
            if (EnclosureSample(shape, around, across, i) < _wetThreshold) wet++;
        return wet / (double)EnclosureStencilCount;
    }

    // ---- approach bearing (section 1.3 step 6, grade cone only; no road) -------------------------------

    private double IslandBearing(int slot)
    {
        double u = EmitterSiteHash.Unit(EmitterSiteHash.Derive(_recipe.Seed, "emitter-bearing", slot));
        return Math.Floor(u * 16d) * 22.5d;
    }

    private double LandBearing(EmitterSiteKindShape shape, SiteKind kind, long around, int across, int pad)
    {
        double[] distances = kind == SiteKind.Main ? MainBearingSampleDistances : BearingSampleDistances;
        double bestCost = double.MaxValue;
        int bestIndex = 0;
        for (int b = 0; b < 16; b++)
        {
            double angle = b * 22.5d * Math.PI / 180d;
            double ex = Math.Cos(angle), ey = Math.Sin(angle);
            double cost = 0d;
            foreach (double s in distances)
            {
                double h = H(around, across, s * ex, s * ey);
                double reach = _recipe.ApproachGrade * (s - shape.PadRadiusM);
                double clamped = Math.Clamp(h, pad - reach, pad + reach);
                cost += Math.Abs(h - clamped);
            }
            if (cost < bestCost) { bestCost = cost; bestIndex = b; }
        }
        return bestIndex * 22.5d;
    }

    /// <summary>
    /// Spec 2.3 diagnostics: largest cut below the terraced cone bound U and fill above the embankment bound L
    /// over a 64-azimuth ring scan (apron edge to skirt). Island sites are raised, never cut, so they report 0.
    /// The soft clamp is ignored (it only reduces cut/fill), so this is a slight over-estimate.
    /// </summary>
    private void ScanCut(EmitterSiteKindShape shape, SiteKind kind, long around, int across, int pad, bool island, out double maxCut, out double maxFill)
    {
        maxCut = 0d; maxFill = 0d;
        if (island) return;
        foreach (double radius in kind == SiteKind.Main ? MainScanRadii : RegularScanRadii)
        {
            ConeBounds(shape, radius, pad, out double upper, out double lower);
            for (int k = 0; k < 64; k++)
            {
                double angle = k * Math.PI / 32d;
                double h = H(around, across, radius * Math.Cos(angle), radius * Math.Sin(angle));
                maxCut = Math.Max(maxCut, h - upper);
                maxFill = Math.Max(maxFill, lower - h);
            }
        }
    }

    /// <summary>
    /// Coarse cut + fill estimate used only for candidate scoring: 3 rings x 16 azimuths (ring offset 7.5 degrees each),
    /// the same cone bounds as <see cref="ScanCut"/>. Deterministic; returns max cut + max fill in metres.
    /// </summary>
    private double EstimateCutFill(EmitterSiteKindShape shape, long around, int across, int pad, double[] radii)
    {
        double maxCut = 0d, maxFill = 0d;
        for (int ring = 0; ring < radii.Length; ring++)
        {
            double radius = radii[ring];
            ConeBounds(shape, radius, pad, out double upper, out double lower);
            for (int k = 0; k < 16; k++)
            {
                double angle = (k * 22.5d + ring * 7.5d) * Math.PI / 180d;
                double h = H(around, across, radius * Math.Cos(angle), radius * Math.Sin(angle));
                maxCut = Math.Max(maxCut, h - upper);
                maxFill = Math.Max(maxFill, lower - h);
            }
        }
        return maxCut + maxFill;
    }

    private void ConeBounds(EmitterSiteKindShape shape, double radius, int pad, out double upper, out double lower)
    {
        double period = _recipe.CutTerraceBenchM + _recipe.CutTerraceRiserM / _recipe.CutRiserSlope;
        double benchFraction = _recipe.CutTerraceBenchM / period;
        double rho = Math.Max(0d, radius - shape.RhoStartM);
        double u = rho / period, n = Math.Floor(u), f = u - n;
        double t = Math.Clamp((f - benchFraction) / (1d - benchFraction), 0d, 1d);
        upper = pad + _recipe.CutTerraceRiserM * (n + t * t * (3d - 2d * t));
        lower = pad - _recipe.FillSlope * rho;
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private double H(long around, int across, double dAround, double dAcross)
    {
        long a = Wrap(around + (long)Math.Round(dAround));
        int x = across + (int)Math.Round(dAcross);
        if (x < 0) x = 0;
        else if (x > (int)_recipe.WidthM) x = (int)_recipe.WidthM;
        return _source.NaturalHeight(a, x);
    }

    private long Wrap(long around)
    {
        long a = around % _circumference;
        return a < 0 ? a + _circumference : a;
    }

    private double Distance(long a1, int x1, long a2, int x2)
    {
        long d = Math.Abs(a1 - a2);
        d = Math.Min(d, _circumference - d);
        double dx = x1 - x2;
        return Math.Sqrt((double)d * d + dx * dx);
    }
}
