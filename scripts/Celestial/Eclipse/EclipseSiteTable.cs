using System;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>
/// Dead-emitter seam (spec B.5). <see cref="Output"/> is the site's relative output in [0, 1], 1 everywhere by default; 0 means the site
/// never fires or flashes, and its share of the hub brightness is missing (a dimmer sun, not a slower ignition). Bump
/// <see cref="Revision"/> whenever an output changes so the timeline re-reads it. The main spire is not covered.
/// </summary>
public interface IEclipseSiteOutput
{
    float Output(int site);
    ulong Revision { get; }
}

public sealed class FullEclipseSiteOutput : IEclipseSiteOutput
{
    public static readonly FullEclipseSiteOutput Instance = new();
    public float Output(int site) => 1f;
    public ulong Revision => 0UL;
}

/// <summary>
/// Regular sites of the relay grid with the loop coordinate used by the event: <c>P = topology[LinkOut(site, column)].P0</c> (metres from
/// the main spire in the +around sense, spec B.4), plus canonical around/across for distance and bearing. Pure data; build with
/// <see cref="FromTopology"/> or <see cref="Uniform"/> (synthetic evenly spaced chains for tests).
/// </summary>
public sealed class EclipseSiteTable
{
    public int Count { get; }
    public int Columns { get; }
    public double CircumferenceMeters { get; }
    public int MainSiteIndex { get; }
    public double MainAroundMeters { get; }
    public double MainAcrossMeters { get; }
    /// <summary>Registry site index of each entry (the argument of <see cref="IEclipseSiteOutput.Output"/>).</summary>
    public int[] SiteIndex { get; }
    public int[] Column { get; }
    /// <summary>Loop coordinate from the main spire in metres, 0 &lt; P &lt; C.</summary>
    public double[] P { get; }
    public double[] AroundMeters { get; }
    public double[] AcrossMeters { get; }

    private EclipseSiteTable(int count, int columns, double circumference, int mainSite, double mainAround, double mainAcross)
    {
        Count = count; Columns = columns; CircumferenceMeters = circumference; MainSiteIndex = mainSite;
        MainAroundMeters = mainAround; MainAcrossMeters = mainAcross;
        SiteIndex = new int[count]; Column = new int[count]; P = new double[count];
        AroundMeters = new double[count]; AcrossMeters = new double[count];
    }

    public static EclipseSiteTable FromTopology(EmitterGridTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        EmitterSiteRegistry registry = topology.Registry;
        int main = registry.MainIndex;
        var table = new EclipseSiteTable(registry.RegularCount, topology.Columns, topology.CircumferenceMeters, main,
            registry.MainSite.Around, registry.MainSite.Across);
        int n = 0;
        foreach (EmitterLink l in topology.Links)
        {
            if (l.FromSite == main) continue;
            EmitterSite s = registry[l.FromSite];
            table.SiteIndex[n] = l.FromSite; table.Column[n] = l.Column; table.P[n] = l.P0;
            table.AroundMeters[n] = s.Around; table.AcrossMeters[n] = s.Across;
            n++;
        }
        if (n != table.Count) throw new InvalidOperationException($"topology has {n} regular link origins, expected {table.Count}");
        return table;
    }

    /// <summary>Evenly spaced chains: site k (1..rows-1) of every column sits at P = k * C / rows. Main site index = (rows - 1) * columns.</summary>
    public static EclipseSiteTable Uniform(double circumferenceMeters, int columns = 6, int rows = 100)
    {
        var table = new EclipseSiteTable((rows - 1) * columns, columns, circumferenceMeters, (rows - 1) * columns, 0d, 0d);
        int n = 0;
        for (int col = 0; col < columns; col++)
            for (int k = 1; k < rows; k++, n++)
            {
                table.SiteIndex[n] = (k - 1) * columns + col; table.Column[n] = col;
                table.P[n] = k * circumferenceMeters / rows;
                table.AroundMeters[n] = table.P[n]; table.AcrossMeters[n] = (col - (columns - 1) * 0.5d) * 20_000d;
            }
        return table;
    }

    /// <summary>Planar distance (km) and bearing (degrees: 0 = +around, 90 = +across) from an observer to a table entry.</summary>
    public void DistanceBearing(int entry, double observerAround, double observerAcross, out float distanceKm, out float bearingDegrees)
        => Measure(AroundMeters[entry], AcrossMeters[entry], observerAround, observerAcross, CircumferenceMeters, out distanceKm, out bearingDegrees);

    public void MainDistanceBearing(double observerAround, double observerAcross, out float distanceKm, out float bearingDegrees)
        => Measure(MainAroundMeters, MainAcrossMeters, observerAround, observerAcross, CircumferenceMeters, out distanceKm, out bearingDegrees);

    private static void Measure(double around, double across, double oa, double ox, double c, out float km, out float bearing)
    {
        double da = EmitterGridTopology.SignedWrap(around - oa, c), dx = across - ox;
        km = (float)(Math.Sqrt(da * da + dx * dx) / 1000d);
        double b = Math.Atan2(dx, da) * 180d / Math.PI;
        bearing = (float)(b < 0d ? b + 360d : b);
    }
}
