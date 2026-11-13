using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-12c (docs/work-orders/terrain-vegetation/T2.md, Spec S13.2): a river is drawn at its true width on every proxy level that owns it, and as coverage on the levels that do not.
/// Builds raw sectors of the 4, 8, 16, 32 and 64 m levels over 35 river sections of a 400 km strip graph (five under 8 m wide since T2R-20 lifted the minimum, ten each of w 8 to 16 m, 16 to 64 m and over 64 m), and reads the built arrays.
/// Drawn width: the area of the water triangles inside a window along the reach (and inside the sector) over the window length, against the true wet width of the operator at footprint 0
/// (lines across the reach every 10 m, samples every 0.5 m); print p50 and p90 of drawn over true per level; pass: p90 at most 1.35 and median 0.85 to 1.2 (starting values, not tuned here).
/// A river without an owner at a level has no water triangle there, and the payload of the vertices holds its area (sum of c * spacing^2) within 10 % of the true water area.
/// Slot pack: the attribute block decodes to the same coverage bit for bit and the flag bits are unchanged. A generation-1 sector has zero attr.w on every lattice texel.
/// Prints PROXY_RIVER_TRUE_WIDTH=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests proxy_river_true_width_test.
/// </summary>
public partial class ProxyRiverTrueWidthTest : RefCounted
{
    private const int Seed = 1337;
    private const double StripStart = 9_804_000d;
    private const int StripKm = 400;
    private const int Cells = 64;
    private static readonly int[] Spacings = { 4, 8, 16, 32, 64 };
    private const float RiverLineMorphScale = 0.49f;

    private readonly List<string> _failures = new();
    private void Fail(string message) { if (_failures.Count < 30) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }
    private static void Detail(string text) => Console.WriteLine("PROXY_RIVER_TRUE_WIDTH_DETAIL " + text);

    private sealed record Section(int Stem, double S, double Around, double Across, double Tx, double Ty, double Width);

    public int Run()
    {
        TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers" });
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, recipe);
        try
        {
            int workers = Math.Clamp(System.Environment.ProcessorCount / 2, 2, 6);
            RiverTestGraphs.Install(recipe, settings, StripStart, StripKm, workers);
            using var sampler = new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
            Check(sampler.HasRiverLine, "a rivers sampler reports no river line");
            RiverGraph graph = sampler.RuntimeRiverGraph!;
            List<Section> sections = PickSections(recipe, graph);
            Detail($"sections: {sections.Count} ({sections.Count(s => s.Width < 8)} under 8 m, {sections.Count(s => s.Width >= 8 && s.Width < 16)} 8 to 16 m, {sections.Count(s => s.Width >= 16 && s.Width <= 64)} 16 to 64 m, {sections.Count(s => s.Width > 64)} over 64 m)");
            Check(sections.Count == 35, $"only {sections.Count} usable river sections found");
            var session = RingTerrainProxySector.CreateRiverWidthFixtureSession(EmptyEdits(sampler), sampler, sampler.RingAcrossWidthMeters);
            var op = new RiverCarveOperator(graph);
            foreach (int spacing in Spacings) Level(spacing, sections, sampler, session, graph, op, recipe);
            GenerationOne(settings);
        }
        catch (Exception e) { Console.Error.WriteLine("PROXY_RIVER_TRUE_WIDTH=FAIL exception " + e); return 1; }
        finally { RiverGraphStore.Clear(); }
        if (_failures.Count != 0) { Console.Error.WriteLine("PROXY_RIVER_TRUE_WIDTH=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("PROXY_RIVER_TRUE_WIDTH=PASS");
        return 0;
    }

    private static RingLodSurfaceEditSnapshot EmptyEdits(RingTerrainSampler sampler) => new(0,
        sampler.AroundRingVoxelLength, new Dictionary<RingTerrainColumnCoordinate, int>(),
        new Dictionary<RingTerrainColumnCoordinate, float>(), sampler.UsesRuntimeSurfaceGeneration);

    // ------------------------------------------------------------------ sections

    /// <summary>Five sections under 8 m wide and ten of each other width class, by hash order, on ground at least 2 m, away from lakes, the sea, the strip ends and from other water: the only wet samples within 150 m are the stem's own.</summary>
    private List<Section> PickSections(TerrainGenerationParameters recipe, RiverGraph graph)
    {
        RiverNetwork net = graph.Network;
        var op = new RiverCarveOperator(graph);
        using var natural = new TerrainGenerationHeightModel(recipe, false);
        double aroundMin = StripStart + 20_000d, aroundMax = StripStart + StripKm * 1000d - 20_000d;
        var picked = new List<Section>();
        int[] taken = new int[4];
        var candidates = new List<(int Stem, double S, bool Extra)>();
        var pieceScratch = new List<int>();
        for (int stem = 0; stem < net.Stems.Length; stem++)
            for (double s = 300d; s < net.Curves[stem].Length - 300d; s += 250d) candidates.Add((stem, s, false));
        // Reaches under 8 m wide are rare and short (T2R-20): extra candidates every 40 m, used for that class only.
        for (int stem = 0; stem < net.Stems.Length; stem++)
            for (double s = 40d; s < net.Curves[stem].Length - 40d; s += 40d) candidates.Add((stem, s, true));
        double minSeen = double.MaxValue; int narrowSeen = 0, rejStrip = 0, rejLow = 0, rejOther = 0, rejDirty = 0;
        foreach ((int stem, double s, bool extra) in candidates.OrderBy(c => RiverHash.Mix(Seed, c.Stem, (int)(c.S / (c.Extra ? 40d : 250d)) + (c.Extra ? 7777 : 0))))
        {
            if (taken[0] >= 10 && taken[1] >= 10 && taken[2] >= 10 && taken[3] >= 5) break;
            net.CentreFrame(stem, s, out double around, out double across, out double nx, out double ny);
            net.ChannelAt(stem, s, out RiverChannel channel);
            double width = 2d * channel.HalfWidth;
            int cls = width < 8d ? 3 : width < 16d ? 0 : width <= 64d ? 1 : 2;
            if (width < minSeen) minSeen = width;
            if (width < 8d) narrowSeen++;
            if (extra && cls != 3) continue;
            if (taken[cls] >= (cls == 3 ? 5 : 10) || (cls == 2 && taken[2] >= 5 && width < 128d)) continue;
            if (around < aroundMin || around > aroundMax || across < 5000d || across > net.WidthM - 5000d) { if (width < 8d) rejStrip++; continue; }
            if (natural.CalculateHeight(around, across) < 5d) { if (width < 8d) rejLow++; continue; }
            // No other stem near: the pieces the index lists for a 600 m square (a superset of those within 650 m of it) all belong to this stem.
            pieceScratch.Clear();
            graph.Index.CollectPieces(around - 300d, around + 300d, across - 300d, across + 300d, pieceScratch);
            if (cls != 3 && width < 128d && pieceScratch.Any(p => graph.PieceStem[p] != stem)) { if (width < 8d) rejOther++; continue; }
            // Only this stem's water around: lines across the reach every 20 m to +-260 m along it, samples every 2 m (the widest window is 512 m).
            double tx = ny, ty = -nx;
            bool clean = true;
            for (int k = -13; k <= 13 && clean; k++)
                for (double x = -(width * 0.5 + 150d); x <= width * 0.5 + 150d && clean; x += 2d)
                {
                    double a = around + k * 20d * tx + x * nx, c = across + k * 20d * ty + x * ny, g = natural.CalculateHeight(a, c);
                    RiverOperatorResult r = op.Evaluate(a, c, g, 0d);
                    if (r.Kind == InlandWaterKind.Lake || (g < 1d && r.Kind != InlandWaterKind.River) || natural.CalculateHeight(a, c) < 1d) clean = false;
                    else if (r.Kind == InlandWaterKind.River && op.Locate(a, c, 0d, out RiverLocation loc) && loc.Stem != stem) clean = false;
                }
            if (!clean) { if (width < 8d) rejDirty++; continue; }
            taken[cls]++;
            picked.Add(new Section(stem, s, around, across, tx, ty, width));
        }
        Detail($"candidates: {candidates.Count}, narrowest width seen {minSeen:F2} m, {narrowSeen} candidates under 8 m (before the cleanliness filters); rejected: strip margins {rejStrip}, ground under 5 m {rejLow}, other stem within 300 m {rejOther}, other or lake water within 150 m {rejDirty}");
        return picked;
    }

    // ------------------------------------------------------------------ a level

    private void Level(int spacing, List<Section> sections, RingTerrainSampler sampler, RingTerrainProxySector.RiverWidthFixtureSession session, RiverGraph graph, RiverCarveOperator op, TerrainGenerationParameters recipe)
    {
        int size = Cells * spacing;
        double radius = EmitterSiteTestWorlds.Circumference / Math.Tau;
        double window = Math.Min(Math.Max(200d, 16d * spacing), 0.5 * size);
        var ratios = new List<double>();
        var perSection = new List<string>();
        int ownerSections = 0, plainSections = 0, plainPayloadBad = 0, trianglesInPlain = 0, usedOwner = 0, usedPlain = 0, packBad = 0, packChecked = 0, flagBad = 0;
        double payloadWorst = 0d;
        bool usesLine = RiverFootprint.UsesProxyLine(spacing);
        var rows = new (Section Section, bool Owner, bool Mixed, double True, double Drawn, double Payload, double Channel)[sections.Count];
        Parallel.For(0, sections.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(System.Environment.ProcessorCount / 2, 2, 6) }, i =>
        {
            Section sec = sections[i];
            using var natural = new TerrainGenerationHeightModel(recipe, false);
            double thr = RiverCarveOperator.OwnerWidthPerFootprint * spacing, minW = double.MaxValue, maxW = 0d;
            // The sector that holds the section's centre; the window is the part of the oriented rectangle inside it (the 4 m sector is only 256 m wide).
            var coordinate = new RingTerrainSectorCoordinate((long)Math.Floor(sec.Around / size), (int)Math.Floor(sec.Across / size));
            RingTerrainProxySector.RiverWidthFixtureSector sector = session.Build(coordinate, spacing, Cells, radius);
            double halfLateral = sec.Width * 0.5 + 2d * spacing + 12d;
            double originAround = coordinate.AroundSector * (double)size, originAcross = coordinate.AcrossSector * (double)size;
            bool Inside(double a, double c) => a >= originAround && a <= originAround + size && c >= originAcross && c <= originAcross + size;
            // The edge vertices of a sector also hold the area of the steps just outside it, so the payload is compared on the vertices one in from the edge, which hold the area from half a spacing in.
            bool InsideShrunk(double a, double c) => a >= originAround + 0.5 * spacing && a <= originAround + size - 0.5 * spacing && c >= originAcross + 0.5 * spacing && c <= originAcross + size - 0.5 * spacing;
            // True wet area at footprint 0 inside the window and the sector: lines across the reach every 10 m, samples every 0.5 m.
            double trueArea = 0d;
            for (double u = -0.5 * window + 5d; u < 0.5 * window; u += 10d)
                for (double v = -halfLateral; v <= halfLateral; v += 0.5)
                {
                    double a = sec.Around + u * sec.Tx - v * sec.Ty, c = sec.Across + u * sec.Ty + v * sec.Tx;
                    if (!Inside(a, c)) continue;
                    double g = natural.CalculateHeight(a, c);
                    RiverOperatorResult r = op.Evaluate(a, c, g, 0d);
                    if (r.Kind == InlandWaterKind.River && r.Height < r.WaterLevel) trueArea += 0.5 * 10d;
                }
            // The channel area of the stem inside the same window: full water width (2a) times the arc length of the displaced centreline, in 0.5 m steps (the area S11b deposits).
            double channelArea = 0d, pa0 = double.NaN, pc0 = double.NaN;
            for (double s = Math.Max(0d, sec.S - window - 20d); s <= Math.Min(graph.Network.Curves[sec.Stem].Length, sec.S + window + 20d); s += 0.5)
            {
                graph.Network.Centre(sec.Stem, s, out double ca, out double cc, out _, out _);
                if (!double.IsNaN(pa0))
                {
                    double da = ca - sec.Around, dc = cc - sec.Across, u = da * sec.Tx + dc * sec.Ty, v = -da * sec.Ty + dc * sec.Tx;
                    if (Math.Abs(u) <= 0.5 * window && Math.Abs(v) <= halfLateral && InsideShrunk(ca, cc))
                    {
                        graph.Network.ChannelAt(sec.Stem, s, out RiverChannel ch);
                        minW = Math.Min(minW, 2d * ch.HalfWidth); maxW = Math.Max(maxW, 2d * ch.HalfWidth);
                        channelArea += 2d * ch.HalfWidth * Math.Sqrt((ca - pa0) * (ca - pa0) + (cc - pc0) * (cc - pc0));
                    }
                }
                pa0 = ca; pc0 = cc;
            }
            // A reach whose width crosses the threshold inside the window is mixed (part water geometry, part tint) and is left out of both checks.
            bool owner = minW >= thr, mixed = !owner && maxW >= thr;
            double drawn = 0d;
            for (int t = 0; t + 2 < sector.WaterIndices.Length; t += 3)
            {
                var poly = new List<(double U, double V)>(8);
                for (int k = 0; k < 3; k++)
                {
                    (double a, double c) = sector.ToCanonical(sector.Vertices[sector.WaterIndices[t + k]]);
                    double da = a - sec.Around, dc = c - sec.Across;
                    poly.Add((da * sec.Tx + dc * sec.Ty, -da * sec.Ty + dc * sec.Tx));
                }
                // Clip to the window, the lateral extent and the sector (the sector's edges in (u, v) are four half planes of the oriented frame).
                poly = ClipHalf(poly, 1d, 0d, 0.5 * window); poly = ClipHalf(poly, -1d, 0d, 0.5 * window);
                poly = ClipHalf(poly, 0d, 1d, halfLateral); poly = ClipHalf(poly, 0d, -1d, halfLateral);
                poly = ClipSector(poly, sec, originAround, originAcross, size);
                drawn += Area(poly);
            }
            // Payload: coverage of the terrain lattice vertices inside the window (the bilinear splat conserves area: on average over the lattice phase the vertices inside a cut hold the area up to the cut).
            double payload = 0d;
            for (int z = 1; z < Cells; z++)
                for (int x = 1; x < Cells; x++)
                {
                    double a = originAround + x * spacing, c = originAcross + z * spacing;
                    double da = a - sec.Around, dc = c - sec.Across, u = da * sec.Tx + dc * sec.Ty, v = -da * sec.Ty + dc * sec.Tx;
                    if (Math.Abs(u) > 0.5 * window || Math.Abs(v) > halfLateral) continue;
                    float morph = sector.MorphData[z * (Cells + 1) + x].Y;
                    if (morph > 0f && morph < 0.5f) payload += morph / RiverLineMorphScale * (double)spacing * spacing;
                }
            // Slot pack: attr.w decodes to the payload bit for bit; flag bits unchanged.
            int bad = 0, flags = 0, checkedTexels = 0;
            if (sector.Attr == null) bad = -1;
            else
            {
                ReadOnlySpan<uint> attr = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(sector.Attr);
                for (int v = 0; v < sector.LatticeVertices * 2; v++)
                {
                    float y = sector.MorphData[v].Y;
                    bool terrain = v < sector.LatticeVertices;
                    uint expected = terrain && y > 0f && y < 0.5f ? BitConverter.SingleToUInt32Bits(y / RiverLineMorphScale) : 0u;
                    checkedTexels++;
                    if (attr[v * 4 + 3] != expected) bad++;
                    if (((attr[v * 4 + 2] & 1u) != 0u) != (y >= 0.5f)) flags++;
                }
            }
            lock (rows) rows[i] = (sec, owner, mixed, trueArea / window, drawn / window, payload, channelArea);
            lock (perSection) { packBad += bad < 0 ? 1 : bad; flagBad += flags; packChecked += checkedTexels; if (!owner && !mixed && drawn > 0.01) trianglesInPlain++; }
        });
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            if (r.Owner)
            {
                ownerSections++;
                if (r.True > 0d) { usedOwner++; ratios.Add(r.Drawn / r.True); }
            }
            else if (!r.Mixed)
            {
                plainSections++;
                if (r.True > 0d)
                {
                    usedPlain++;
                    double wetRatio = r.Payload / (r.True * window);
                    double ratio = r.Channel > 0d ? r.Payload / r.Channel : double.NaN;
                    payloadWorst = Math.Max(payloadWorst, Math.Abs(ratio - 1d));
                    if (Math.Abs(ratio - 1d) > 0.10) { plainPayloadBad++; Detail(FormattableString.Invariant($"level {spacing} m: payload outlier stem {r.Section.Stem} s {r.Section.S:F0} w {r.Section.Width:F1}: payload {r.Payload:F0} m2, channel {r.Channel:F0} m2, wet {r.True * window:F0} m2, window {window:F0} m, tangent {r.Section.Tx:F2}/{r.Section.Ty:F2}")); }
                    perSection.Add(FormattableString.Invariant($"w {r.Section.Width:F1}: payload/channel {ratio:F2}, payload/wet {wetRatio:F2}"));
                }
            }
        }
        ratios.Sort();
        double p50 = ratios.Count > 0 ? ratios[ratios.Count / 2] : double.NaN, p90 = ratios.Count > 0 ? ratios[Math.Min(ratios.Count - 1, (int)Math.Ceiling(0.9 * ratios.Count) - 1)] : double.NaN;
        Detail(FormattableString.Invariant($"level {spacing} m: window {window:F0} m, owners {ownerSections} sections ({usedOwner} used): drawn/true p50 {p50:F3}, p90 {p90:F3}, max {(ratios.Count > 0 ? ratios[^1] : double.NaN):F3}; no owner {plainSections} sections ({usedPlain} used): water triangles in {trianglesInPlain}, payload within 10 % of the channel area (2a x length) on {usedPlain - plainPayloadBad}/{usedPlain} (worst {payloadWorst * 100:F1} %); slot attr.w mismatches {packBad}/{packChecked}, flag mismatches {flagBad}"));
        Detail($"level {spacing} m: drawn/true by section: " + string.Join(", ", rows.Where(r => r.Owner && r.True > 0d).OrderBy(r => r.Section.Width).Select(r => FormattableString.Invariant($"{r.Section.Width:F0}m={r.Drawn / r.True:F2}"))));
        if (perSection.Count > 0) Detail($"level {spacing} m: payload " + string.Join(", ", perSection));
        if (ratios.Count > 0)
        {
            Check(p90 <= 1.35, $"level {spacing} m: drawn width over true width p90 {p90:F3} (limit 1.35)");
            Check(p50 >= 0.85 && p50 <= 1.2, $"level {spacing} m: drawn width over true width median {p50:F3} (0.85 to 1.2)");
        }
        else if (spacing <= 16) Check(false, $"level {spacing} m: no river section with an owner");
        Check(trianglesInPlain == 0, $"level {spacing} m: {trianglesInPlain} sections without an owner have water triangles in the window");
        if (usedPlain > 0) Check(plainPayloadBad == 0, $"level {spacing} m: payload differs from the channel area by more than 10 % on {plainPayloadBad}/{usedPlain} sections (worst {payloadWorst * 100:F1} %)");
        Check(packBad == 0 && flagBad == 0 && packChecked > 0, $"level {spacing} m: slot pack mismatches attr.w {packBad}, flags {flagBad} of {packChecked} texels (negative count means the slot could not pack the sector)");
        if (!usesLine) Check(rows.All(r => r.Payload == 0d), $"level {spacing} m carries a payload but UsesProxyLine({spacing}) is false");
    }

    // ------------------------------------------------------------------ generation 1

    private void GenerationOne(VoxelTerrainSettings riverSettings)
    {
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1));
        using var gen1 = new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
        Check(!gen1.HasRiverLine, "a generation-1 sampler reports a river line");
        var session = RingTerrainProxySector.CreateRiverWidthFixtureSession(EmptyEdits(gen1), gen1, gen1.RingAcrossWidthMeters);
        double radius = EmitterSiteTestWorlds.Circumference / Math.Tau;
        int sectors = 0, nonzero = 0;
        foreach (int spacing in Spacings)
            for (int j = 0; j < 3; j++)
            {
                int size = Cells * spacing;
                var coordinate = new RingTerrainSectorCoordinate((long)((StripStart + 40_000d * (j + 1)) / size), 40_000 / size * (j + 2));
                RingTerrainProxySector.RiverWidthFixtureSector sector = session.Build(coordinate, spacing, Cells, radius);
                sectors++;
                if (sector.Attr == null) { nonzero++; continue; }
                ReadOnlySpan<uint> attr = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(sector.Attr);
                for (int v = 0; v < sector.LatticeVertices * 2; v++)
                    if (attr[v * 4 + 3] != 0u || sector.MorphData[v].Y is > 0f and < 0.5f) { nonzero++; break; }
            }
        Detail($"generation 1: {sectors} sectors, {nonzero} with a nonzero attr.w or payload");
        Check(sectors > 0 && nonzero == 0, $"{nonzero} of {sectors} generation-1 sectors have a nonzero lattice attr.w or payload");
    }

    // ------------------------------------------------------------------ polygon clipping

    private static List<(double U, double V)> ClipHalf(List<(double U, double V)> poly, double nu, double nv, double limit)
    {
        // Keep the points with nu * u + nv * v <= limit.
        var result = new List<(double U, double V)>(poly.Count + 2);
        for (int i = 0; i < poly.Count; i++)
        {
            (double U, double V) a = poly[i], b = poly[(i + 1) % poly.Count];
            double da = nu * a.U + nv * a.V - limit, db = nu * b.U + nv * b.V - limit;
            if (da <= 0d) result.Add(a);
            if ((da < 0d && db > 0d) || (da > 0d && db < 0d))
            {
                double t = da / (da - db);
                result.Add((a.U + (b.U - a.U) * t, a.V + (b.V - a.V) * t));
            }
        }
        return result;
    }

    private static List<(double U, double V)> ClipSector(List<(double U, double V)> poly, Section sec, double originAround, double originAcross, int size)
    {
        // Sector edges: around = originAround, originAround + size; across = originAcross, originAcross + size. a = Around + u tx - v ty, c = Across + u ty + v tx.
        if (poly.Count < 3) return poly;
        // around >= originAround: u tx - v ty >= originAround - Around, as -(u tx - v ty) <= Around - originAround.
        poly = ClipHalf(poly, -sec.Tx, sec.Ty, sec.Around - originAround);
        if (poly.Count < 3) return poly;
        poly = ClipHalf(poly, sec.Tx, -sec.Ty, originAround + size - sec.Around);
        if (poly.Count < 3) return poly;
        poly = ClipHalf(poly, -sec.Ty, -sec.Tx, sec.Across - originAcross);
        if (poly.Count < 3) return poly;
        poly = ClipHalf(poly, sec.Ty, sec.Tx, originAcross + size - sec.Across);
        return poly;
    }

    private static double Area(List<(double U, double V)> poly)
    {
        if (poly.Count < 3) return 0d;
        double sum = 0d;
        for (int i = 0; i < poly.Count; i++)
        {
            (double U, double V) a = poly[i], b = poly[(i + 1) % poly.Count];
            sum += a.U * b.V - b.U * a.V;
        }
        return Math.Abs(sum) * 0.5;
    }
}
