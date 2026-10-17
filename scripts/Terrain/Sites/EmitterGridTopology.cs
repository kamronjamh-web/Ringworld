using System;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Terrain.Sites;

public enum EmitterLinkKind : byte { Chain = 0, KnotOut = 1, KnotIn = 2 }

/// <summary>
/// One relay link (docs/emitter-grid-climate-machines-spec.md G1). <see cref="P0"/>/<see cref="P1"/> are the pulse coordinate
/// (metres along the closed loop, measured from the main spire in the +around sense); KnotOut starts at exactly 0 and KnotIn
/// ends at exactly the circumference.
/// </summary>
public readonly record struct EmitterLink(int Index, int FromSite, int ToSite, int Column, EmitterLinkKind Kind,
    double P0, double P1, double FromRelayHeightM, double ToRelayHeightM);

/// <summary>Canonical-space chord geometry of one link (double precision, rebase independent).</summary>
public readonly record struct EmitterLinkGeometry(double ChordLengthM, double SagittaM, double MidSpanAltitudeM, double EndAltitudeMeanM);

/// <summary>
/// The along-ring relay grid: 6 closed chains of 100 links through the main spire (588 chain + 6 knot out + 6 knot in = 600).
/// Pure function of the <see cref="EmitterSiteRegistry"/> and the relay heights; no scene nodes. Link index = column * 100 + k,
/// where k = 0 is the KnotOut link (main -> row MainRow+1) and k = 99 the KnotIn link (row MainRow-1 -> main).
/// </summary>
public sealed class EmitterGridTopology
{
    public const int TopologyVersion = 1;
    public const double DefaultRegularRelayHeightM = 1743d;
    public const double DefaultMainRelayHeightM = EmitterSiteFrameMath.DefaultMainCrownHeightMeters; // tweaks T1: the knot links attach at the main crown focus (3,989 m), the same rule as the regular crowns

    private readonly EmitterLink[] _links;
    private readonly int[] _linkOut; // [site * Columns + column] -> link leaving the site toward +around, or -1
    private readonly int[] _linkIn;  // link arriving at the site from -around, or -1
    private readonly double[] _around, _across, _vertical; // per-site relay node, canonical (vertical includes pad + clearance + relay height)
    private readonly int[] _hubBeamSite;       // spire to hub beam b -> site: the main spire first, then every chain link origin in link order
    private readonly double[] _hubBeamVertical; // crown height of that site: pad + clearance + 1,743 m (3,989 m for the main spire)
    private readonly double[] _hubBeamPad;      // pad height of that site (the base and reactor beam origins are measured from it)
    private readonly SiteKind[] _hubBeamKind;

    public EmitterSiteRegistry Registry { get; }
    public int Columns { get; }
    public int LinksPerChain { get; }
    public double CircumferenceMeters { get; }
    public double RegularRelayHeightM { get; }
    public double MainRelayHeightM { get; }
    public int LinkCount => _links.Length;
    public ulong Identity { get; }
    public ReadOnlySpan<EmitterLink> Links => _links;
    public EmitterLink this[int index] => _links[index];

    private EmitterGridTopology(EmitterSiteRegistry registry, double regularRelayM, double mainRelayM)
    {
        Registry = registry;
        RegularRelayHeightM = regularRelayM;
        MainRelayHeightM = mainRelayM;
        CircumferenceMeters = registry.Recipe.CircumferenceM;
        Columns = registry.Recipe.Columns;
        int rows = registry.Recipe.Rows;
        LinksPerChain = rows;
        int sites = registry.Count;

        _around = new double[sites]; _across = new double[sites]; _vertical = new double[sites];
        for (int i = 0; i < sites; i++)
        {
            EmitterSite s = registry[i];
            _around[i] = s.Around; _across[i] = s.Across;
            _vertical[i] = s.PadHeight + EmitterSiteFrameMath.DefaultPadClearanceMeters + (s.IsMain ? mainRelayM : regularRelayM);
        }

        _hubBeamSite = new int[registry.RegularCount + 1];
        _hubBeamVertical = new double[_hubBeamSite.Length];
        _hubBeamPad = new double[_hubBeamSite.Length];
        _hubBeamKind = new SiteKind[_hubBeamSite.Length];
        _links = new EmitterLink[Columns * rows];
        _linkOut = new int[sites * Columns];
        _linkIn = new int[sites * Columns];
        Array.Fill(_linkOut, -1);
        Array.Fill(_linkIn, -1);

        int main = registry.MainIndex, mainRow = registry.MainRow;
        double c = CircumferenceMeters;
        double mainAround = _around[main];
        for (int col = 0; col < Columns; col++)
        {
            int previous = main;
            for (int k = 0; k < rows; k++)
            {
                int next = k == rows - 1 ? main : registry.RegularIndex((mainRow + k + 1) % rows, col);
                EmitterLinkKind kind = k == 0 ? EmitterLinkKind.KnotOut : k == rows - 1 ? EmitterLinkKind.KnotIn : EmitterLinkKind.Chain;
                // P0 = wrap(A_from - A_main); P1 = P0 + signedWrap(A_to - A_from), which is the next P0 (exactly C for KnotIn).
                double p0 = k == 0 ? 0d : Wrap(_around[previous] - mainAround, c);
                double p1 = k == rows - 1 ? c : Wrap(_around[next] - mainAround, c);
                int index = col * rows + k;
                _links[index] = new EmitterLink(index, previous, next, col, kind, p0, p1,
                    previous == main ? mainRelayM : regularRelayM, next == main ? mainRelayM : regularRelayM);
                _linkOut[previous * Columns + col] = index;
                _linkIn[next * Columns + col] = index;
                previous = next;
            }
        }

        // Hub beams: the main spire is beam 0; the regular spires follow in the order of their outgoing chain links (the order of EclipseSiteTable).
        int beam = 0;
        _hubBeamSite[beam++] = main;
        foreach (EmitterLink l in _links)
            if (l.FromSite != main) _hubBeamSite[beam++] = l.FromSite;
        if (beam != _hubBeamSite.Length) throw new InvalidOperationException($"topology has {beam - 1} regular link origins, expected {registry.RegularCount}");
        for (int b = 0; b < _hubBeamSite.Length; b++)
        {
            EmitterSite s = registry[_hubBeamSite[b]];
            _hubBeamPad[b] = s.PadHeight;
            _hubBeamKind[b] = s.Kind;
            _hubBeamVertical[b] = s.PadHeight + EmitterSiteFrameMath.DefaultPadClearanceMeters
                + (s.IsMain ? EmitterSiteFrameMath.DefaultMainCrownHeightMeters : EmitterSiteFrameMath.DefaultCrownHeightMeters);
        }

        ulong h = EmitterSiteHash.Offset;
        h = EmitterSiteHash.Mix(h, registry.Identity);
        h = EmitterSiteHash.Mix(h, (ulong)TopologyVersion);
        h = EmitterSiteHash.Mix(h, (ulong)BitConverter.DoubleToInt64Bits(regularRelayM));
        h = EmitterSiteHash.Mix(h, (ulong)BitConverter.DoubleToInt64Bits(mainRelayM));
        Identity = h == 0UL ? 1UL : h;
    }

    public static EmitterGridTopology Build(EmitterSiteRegistry registry, double regularRelayM = DefaultRegularRelayHeightM, double mainRelayM = DefaultMainRelayHeightM)
        => new(registry ?? throw new ArgumentNullException(nameof(registry)), regularRelayM, mainRelayM);

    private static double Wrap(double v, double c) { double a = v % c; return a < 0d ? a + c : a; }

    public static double SignedWrap(double v, double c)
    {
        double a = Wrap(v, c);
        return a > c * 0.5 ? a - c : a;
    }

    /// <summary>Link leaving <paramref name="site"/> in the +around sense on <paramref name="column"/>; -1 when absent. The main site has one per column.</summary>
    public int LinkOut(int site, int column) => _linkOut[site * Columns + column];
    /// <summary>Link arriving at <paramref name="site"/> from the -around side on <paramref name="column"/>; -1 when absent.</summary>
    public int LinkIn(int site, int column) => _linkIn[site * Columns + column];

    public int ChainFirstLink(int column) => column * LinksPerChain;
    public int ChainLastLink(int column) => column * LinksPerChain + LinksPerChain - 1;

    /// <summary>Canonical relay node (around, vertical incl. clearance and relay height, across) of a site.</summary>
    public (double Around, double Vertical, double Across) RelayNode(int site) => (_around[site], _vertical[site], _across[site]);

    /// <summary>
    /// Chord geometry in canonical double precision. The ring is modelled as a circle of radius C / 2 pi with altitude measured
    /// inward from the sea-level datum (the vertical base offset cancels). Sagitta = mid-span altitude minus the mean end altitude.
    /// </summary>
    public EmitterLinkGeometry GetGeometry(int linkIndex)
    {
        EmitterLink l = _links[linkIndex];
        double c = CircumferenceMeters, radius = c / (2d * Math.PI);
        double a0 = Wrap(_around[l.FromSite], c) / radius, a1 = a0 + SignedWrap(_around[l.ToSite] - _around[l.FromSite], c) / radius;
        double r0 = radius - _vertical[l.FromSite], r1 = radius - _vertical[l.ToSite];
        double x0 = r0 * Math.Cos(a0), z0 = r0 * Math.Sin(a0), x1 = r1 * Math.Cos(a1), z1 = r1 * Math.Sin(a1);
        double dy = _across[l.ToSite] - _across[l.FromSite];
        double length = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0) + dy * dy);
        double mid = Math.Sqrt((x0 + x1) * (x0 + x1) + (z0 + z1) * (z0 + z1)) * 0.5;
        double endMean = (_vertical[l.FromSite] + _vertical[l.ToSite]) * 0.5;
        double midAltitude = radius - mid;
        return new EmitterLinkGeometry(length, midAltitude - endMean, midAltitude, endMean);
    }

    /// <summary>
    /// Writes the world-space endpoints of every link into <paramref name="a"/> and <paramref name="b"/> (length >= LinkCount).
    /// Each endpoint is an absolute evaluation of <see cref="EmitterSiteFrameContext.WorldPosition"/> against the current
    /// snapshot, never a delta from a previous rebase. No allocation.
    /// </summary>
    public void FillWorldEndpoints(in RingLodGeometrySnapshot g, Span<Vector3D> a, Span<Vector3D> b)
    {
        if (a.Length < _links.Length || b.Length < _links.Length) throw new ArgumentException("endpoint spans too small");
        var context = new EmitterSiteFrameContext(g);
        for (int i = 0; i < _links.Length; i++)
        {
            ref readonly EmitterLink l = ref _links[i];
            a[i] = context.WorldPosition(_around[l.FromSite], _vertical[l.FromSite], _across[l.FromSite]);
            b[i] = context.WorldPosition(_around[l.ToSite], _vertical[l.ToSite], _across[l.ToSite]);
        }
    }

    // ---------------------------------------------------------------- eclipse spire to hub beams (docs/eclipse-false-sun-spec.md E.2)

    /// <summary>Spire to hub beams: the main spire plus every regular spire (595 on the default world).</summary>
    public int HubBeamCount => _hubBeamSite.Length;
    /// <summary>Registry site of hub beam <paramref name="beam"/> (0 is the main spire, then the order of the regular chain link origins).</summary>
    public int HubBeamSite(int beam) => _hubBeamSite[beam];
    /// <summary>Canonical crown (around, vertical, across) of hub beam <paramref name="beam"/>: the vertical includes the pad, the clearance and the crown height (1,743 m, or 3,989 m for the main spire).</summary>
    public (double Around, double Vertical, double Across) HubBeamCrown(int beam) => (_around[_hubBeamSite[beam]], _hubBeamVertical[beam], _across[_hubBeamSite[beam]]);
    /// <summary>
    /// Canonical (around, vertical, across) of the point hub beam <paramref name="beam"/> rises from for <paramref name="origin"/>
    /// (<see cref="EmitterSiteFrameMath.BeamOriginHeightMeters"/>). <see cref="EmitterBeamOrigin.Crown"/> is exactly <see cref="HubBeamCrown"/>.
    /// </summary>
    public (double Around, double Vertical, double Across) HubBeamOrigin(int beam, EmitterBeamOrigin origin)
        => (_around[_hubBeamSite[beam]], HubBeamOriginVertical(beam, origin), _across[_hubBeamSite[beam]]);

    private double HubBeamOriginVertical(int beam, EmitterBeamOrigin origin)
        => origin == EmitterBeamOrigin.Crown ? _hubBeamVertical[beam] : _hubBeamPad[beam] + EmitterSiteFrameMath.BeamOriginHeightMeters(_hubBeamKind[beam], origin);

    /// <summary>Across coordinate of the ring hub: half the ring width.</summary>
    public double HubAcrossMeters => Registry.Recipe.WidthM * 0.5d;

    /// <summary>
    /// Writes the world-space crown of every hub beam into <paramref name="crown"/> and the hub point into <paramref name="hub"/>
    /// (crown length >= <see cref="HubBeamCount"/>). Each endpoint is an absolute evaluation against the current snapshot, never a delta from
    /// a previous rebase. No allocation.
    /// </summary>
    public void FillHubBeamEndpoints(in RingLodGeometrySnapshot g, Span<Vector3D> crown, out Vector3D hub)
        => FillHubBeamEndpoints(g, EmitterBeamOrigin.Crown, crown, out hub);

    /// <summary>Like the crown overload, but writes the world-space origin of every hub beam for <paramref name="origin"/> (the point the beam rises from).</summary>
    public void FillHubBeamEndpoints(in RingLodGeometrySnapshot g, EmitterBeamOrigin origin, Span<Vector3D> points, out Vector3D hub)
    {
        if (points.Length < _hubBeamSite.Length) throw new ArgumentException("endpoint span too small");
        var context = new EmitterSiteFrameContext(g);
        for (int b = 0; b < _hubBeamSite.Length; b++)
        {
            int site = _hubBeamSite[b];
            points[b] = context.WorldPosition(_around[site], HubBeamOriginVertical(b, origin), _across[site]);
        }
        hub = context.HubWorldPosition(HubAcrossMeters);
    }
}
