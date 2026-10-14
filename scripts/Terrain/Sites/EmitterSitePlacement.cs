using System;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>
/// Canonical placement of one emitter POI: around A, across X, pad height P (presentation metres) and the world yaw
/// of the POI root about local up, theta = 67.5 deg + beta (docs/emitter-poi-streaming-spec.md section A.2, E3).
/// </summary>
public readonly record struct EmitterSitePlacement(
    int Index,
    double AroundMeters,
    double AcrossMeters,
    double PadHeightMeters,
    double BearingDegrees,
    double YawRadians,
    EmitterSiteFlags Flags,
    SiteKind Kind = SiteKind.Regular)
{
    public double YawDegrees => YawRadians * (180d / Math.PI);
}

/// <summary>
/// Adapts <see cref="EmitterSiteRegistry"/> to placements and fills world transforms for all sites without
/// allocating. Per-site constants (wrapped around, yaw sin/cos) are cached once at construction.
/// </summary>
public sealed class EmitterSitePlacementSource
{
    /// <summary>Asset-space bearing of the EntryApproach marker (BeamEmitterPOI.tscn), degrees.</summary>
    public const double AssetEntryBearingDegrees = 67.5;

    private readonly double[] _around;
    private readonly double[] _across;
    private readonly double[] _pad;
    private readonly double[] _bearingDegrees;
    private readonly double[] _yaw;
    private readonly double[] _cosYaw;
    private readonly double[] _sinYaw;
    private readonly EmitterSiteFlags[] _flags;
    private readonly SiteKind[] _kind;
    private readonly double[] _crown;

    public EmitterSiteRegistry Registry { get; }
    public int Count => _around.Length;
    public ulong Identity => Registry.Identity;
    public double CircumferenceMeters { get; }
    public double WidthMeters { get; }

    public EmitterSitePlacementSource(EmitterSiteRegistry registry)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        CircumferenceMeters = registry.Recipe.CircumferenceM;
        WidthMeters = registry.Recipe.WidthM;
        int n = registry.Count;
        _around = new double[n]; _across = new double[n]; _pad = new double[n]; _bearingDegrees = new double[n];
        _yaw = new double[n]; _cosYaw = new double[n]; _sinYaw = new double[n]; _flags = new EmitterSiteFlags[n];
        _kind = new SiteKind[n]; _crown = new double[n];
        for (int i = 0; i < n; i++)
        {
            EmitterSite s = registry[i];
            _around[i] = s.Around; _across[i] = s.Across; _pad[i] = s.PadHeight;
            _bearingDegrees[i] = s.BetaDegrees; _flags[i] = s.Flags; _kind[i] = s.Kind;
            _crown[i] = s.Kind == SiteKind.Main ? registry.Recipe.MainCrownHeightM : EmitterSiteFrameMath.DefaultCrownHeightMeters;
            // theta = 67.5 + beta in the AcrossDirection = -1 world frame (local Z = -across).
            double yaw = (AssetEntryBearingDegrees + (double)s.BetaDegrees) * (Math.PI / 180d);
            _yaw[i] = yaw; _cosYaw[i] = Math.Cos(yaw); _sinYaw[i] = Math.Sin(yaw);
        }
    }

    public EmitterSitePlacement GetPlacement(int siteIndex)
        => new(siteIndex, _around[siteIndex], _across[siteIndex], _pad[siteIndex], _bearingDegrees[siteIndex], _yaw[siteIndex], _flags[siteIndex], _kind[siteIndex]);

    /// <summary>Beam crown focus height above the pad: 1,743 m regular spire, 3,989 m main spire (BeamCrownFocus of MainSpirePOI.tscn).</summary>
    public double GetCrownHeightMeters(int siteIndex) => _crown[siteIndex];

    /// <summary>Beam geometry for one site using its kind's crown height; the hub is the ring centre line.</summary>
    public EmitterBeamGeometry GetBeamGeometry(in RingLodGeometrySnapshot snapshot, int siteIndex, double padClearanceMeters = EmitterSiteFrameMath.DefaultPadClearanceMeters)
        => EmitterSiteFrameMath.GetBeamGeometry(snapshot, GetPlacement(siteIndex), WidthMeters * 0.5d, _crown[siteIndex], padClearanceMeters);

    /// <summary>Site transform in the current world frame (see <see cref="EmitterSiteFrameMath.ComputeSiteTransform"/>).</summary>
    public Transform3D GetWorldTransform(in RingLodGeometrySnapshot snapshot, int siteIndex, double padClearanceMeters = EmitterSiteFrameMath.DefaultPadClearanceMeters)
        => EmitterSiteFrameMath.ComputeSiteTransform(snapshot, GetPlacement(siteIndex), padClearanceMeters);

    /// <summary>
    /// Fills <paramref name="destination"/>[0..Count) with every site's world transform for the far stand-in MultiMesh.
    /// No allocation; the snapshot-dependent constants are hoisted, so each site costs one sin/cos for the ring angle.
    /// Results are identical to <see cref="GetWorldTransform"/>. Returns the number written.
    /// </summary>
    public int FillWorldTransforms(in RingLodGeometrySnapshot snapshot, Span<Transform3D> destination, double padClearanceMeters = EmitterSiteFrameMath.DefaultPadClearanceMeters)
    {
        int n = Count;
        if (destination.Length < n) throw new ArgumentException("destination too small", nameof(destination));
        var context = new EmitterSiteFrameContext(snapshot);
        for (int i = 0; i < n; i++)
            destination[i] = context.SiteTransform(_around[i], _across[i], _pad[i] + padClearanceMeters, _cosYaw[i], _sinYaw[i]);
        return n;
    }
}
