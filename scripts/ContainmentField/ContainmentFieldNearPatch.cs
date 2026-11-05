using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

// Pure C# placement / LOD / mesh-build logic for the containment field near patch (docs/containment-field-design.md 3.3 and 1.6). No scene tree.

public enum ContainmentFieldViewSide { None = 0, Below = 1, Above = 2 }

/// <summary>Result of one planner update.</summary>
public readonly record struct ContainmentNearPatchView(
    bool Visible, ContainmentFieldViewSide Side, double SignedDistanceMeters, ContainmentFieldPoint Nearest, bool Rebuilt);

/// <summary>Built near-patch grid: (Quads+1)^2 vertices, anchor-relative, world-oriented. Vertex index = j * (Quads + 1) + i (j along arch, i along around).</summary>
public sealed class ContainmentNearPatchMesh
{
    public const int Quads = 48;
    public ContainmentFieldPoint Anchor { get; init; }
    public double RangeMeters { get; init; }
    public double ArchStart { get; init; }
    public double ArchEnd { get; init; }
    public double AroundHalfSpan { get; init; }
    public double NoiseAroundScale { get; init; }
    public Vector2 NoiseOriginMeters { get; init; }
    public Vector3[] Positions { get; init; } = Array.Empty<Vector3>();
    public Vector3[] Normals { get; init; } = Array.Empty<Vector3>();
    public Vector3[] Tangents { get; init; } = Array.Empty<Vector3>();
    public Vector2[] Uv { get; init; } = Array.Empty<Vector2>();
    public Vector2[] Uv2 { get; init; } = Array.Empty<Vector2>();
    public int[] Indices { get; init; } = Array.Empty<int>();
    public Aabb Bounds { get; init; }
    public Basis GridBasis { get; init; }
    internal ContainmentFieldProfile Profile { get; init; }
    internal double Circumference { get; init; }
    internal double RingRadius { get; init; }
}

public static class ContainmentFieldNearPatch
{
    public const double VisibleRangeFactor = 1.1;
    public const double WindowFactor = 1.1;
    public const double RebuildFraction = 0.25;
    public const double NoisePeriodMeters = 46_080.0;   // 256 x 180 m = 1024 x 45 m (the shader's two octaves) = 45 x the lattice spacing
    /// <summary>Hexagonal lattice (user decision 2026-11-05): centre spacing along around (flat to flat, about 1 km); L is a whole number of them.</summary>
    public const double LatticeSpacingMeters = 1_024.0;
    /// <summary>The lattice repeats every two rows along the arch: spacing x sqrt(3).</summary>
    public static readonly double LatticeArchPeriodMeters = LatticeSpacingMeters * Math.Sqrt(3.0);
    public static readonly Vector2 DriftOctave1 = new(0.22f, 0.12f);   // m/s, (around, arch)
    public static readonly Vector2 DriftOctave2 = new(-0.17f, 0.26f);

    public static ContainmentFieldViewSide SideOf(double signedDistanceMeters)
        => signedDistanceMeters > 0.0 ? ContainmentFieldViewSide.Above : ContainmentFieldViewSide.Below;

    /// <summary>Scale on around metres so the circumference is a whole number of noise periods (no seam at around 0).</summary>
    public static double NoiseAroundScale(double circumferenceMeters)
        => Math.Max(1.0, Math.Round(circumferenceMeters / NoisePeriodMeters)) * NoisePeriodMeters / circumferenceMeters;

    private static double PosMod(double v, double m)
    {
        double r = v % m;
        return r < 0.0 ? r + m : r;
    }

    public static Vector2 NoiseOrigin(double aroundMeters, double archMeters, double circumferenceMeters)
        => new((float)PosMod(aroundMeters * NoiseAroundScale(circumferenceMeters), NoisePeriodMeters), (float)PosMod(archMeters, NoisePeriodMeters));

    /// <summary>
    /// Lattice origin for the shader (cf_lattice_origin): x = the noise around origin ((around x scale) mod L, L a whole number of lattice spacings), y = anchor arch
    /// length mod the lattice's arch period, both in double. The lattice is fixed to the field (no drift) and continuous across rebuilds and around 0.
    /// </summary>
    public static Vector2 LatticeOrigin(ContainmentNearPatchMesh mesh)
    {
        double y = mesh.Anchor.ArchLengthMeters % LatticeArchPeriodMeters;
        if (y < 0.0) y += LatticeArchPeriodMeters;
        return new Vector2(mesh.NoiseOriginMeters.X, (float)y);
    }

    /// <summary>Per-octave shader origins at weather time t: (o1.x, o1.y, o2.x, o2.y).</summary>
    public static Vector4 NoiseOrigins(ContainmentNearPatchMesh mesh, double timeSeconds)
    {
        double ox = mesh.NoiseOriginMeters.X, oy = mesh.NoiseOriginMeters.Y;
        return new Vector4(
            (float)PosMod(ox + DriftOctave1.X * timeSeconds, NoisePeriodMeters),
            (float)PosMod(oy + DriftOctave1.Y * timeSeconds, NoisePeriodMeters),
            (float)PosMod(ox + DriftOctave2.X * timeSeconds, NoisePeriodMeters),
            (float)PosMod(oy + DriftOctave2.Y * timeSeconds, NoisePeriodMeters));
    }

    public static ContainmentNearPatchMesh Build(in ContainmentFieldProfile profile, in RingLodGeometrySnapshot snapshot, in ContainmentFieldPoint anchor, double rangeMeters)
    {
        const int Q = ContainmentNearPatchMesh.Quads, V = Q + 1;
        double archLength = ContainmentFieldGeometry.ArchLength(profile);
        double c = snapshot.CircumferenceMeters, r = snapshot.RingRadiusMeters;
        double sA = anchor.ArchLengthMeters, aA = anchor.AroundMeters;
        double archStart = Math.Max(0.0, sA - WindowFactor * rangeMeters), archEnd = Math.Min(archLength, sA + WindowFactor * rangeMeters);
        double radialAnchor = r - (snapshot.RingVerticalBaseOffsetMeters + anchor.VerticalMeters);
        double half = WindowFactor * rangeMeters * r / radialAnchor;
        double scale = NoiseAroundScale(c);
        // Same maths as AnchoredAt(snapshot, anchor).CanonicalToWorld, with the per-row radii and the anchor's local position hoisted out of the vertex loop.
        (double anchorX, double anchorY, double anchorZ) = snapshot.CanonicalToAbsoluteLocal(aA, anchor.VerticalMeters, anchor.AcrossMeters);
        Basis basis = snapshot.GridGlobalTransform.Basis;
        Vector3 center = snapshot.RingCenterLocalOffset;

        var rows = new ContainmentFieldPoint[V];
        var radial = new double[V];
        ContainmentFieldGeometry.SurfaceRows(profile, aA, archStart, archEnd, rows);
        for (int j = 0; j < V; j++)
            radial[j] = r - (snapshot.RingVerticalBaseOffsetMeters + rows[j].VerticalMeters);

        var pos = new Vector3[V * V];
        var nrm = new Vector3[V * V];
        var tan = new Vector3[V * V];
        var uv = new Vector2[V * V];
        var uv2 = new Vector2[V * V];
        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < V; i++)
        {
            double around = aA - half + 2.0 * half * i / Q;
            ContainmentFieldPlacement.AxesAt(snapshot, around, out Vector3 aroundDir, out Vector3 upDir, out Vector3 acrossDir);
            double wrapped = around % c;
            if (wrapped < 0.0) wrapped += c;
            (double sin, double cos) = Math.SinCos(wrapped / r);
            for (int j = 0; j < V; j++)
            {
                ContainmentFieldPoint pt = rows[j];
                int k = j * V + i;
                Vector3 p = basis * new Vector3(
                    (float)(center.X + cos * radial[j] - anchorX),
                    (float)(center.Y + snapshot.RingAcrossBaseOffsetMeters + pt.AcrossMeters - anchorY),
                    (float)(center.Z + sin * radial[j] - anchorZ));
                pos[k] = p;
                nrm[k] = upDir * (float)pt.NormalVertical + acrossDir * (float)pt.NormalAcross;
                tan[k] = aroundDir;
                uv[k] = new Vector2((float)((around - aA) * scale), (float)(pt.ArchLengthMeters - sA));
                uv2[k] = new Vector2((float)Math.Min(pt.ArchLengthMeters, archLength - pt.ArchLengthMeters), 0f);
                min = new Vector3(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = new Vector3(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
        }

        var idx = new int[Q * Q * 6];
        int n = 0;
        for (int j = 0; j < Q; j++)
        {
            for (int i = 0; i < Q; i++)
            {
                int a = j * V + i, b = a + 1, d = a + V, e = d + 1;
                idx[n++] = a; idx[n++] = d; idx[n++] = b;
                idx[n++] = b; idx[n++] = d; idx[n++] = e;
            }
        }

        return new ContainmentNearPatchMesh
        {
            Anchor = anchor, RangeMeters = rangeMeters, ArchStart = archStart, ArchEnd = archEnd, AroundHalfSpan = half,
            NoiseAroundScale = scale, NoiseOriginMeters = NoiseOrigin(aA, sA, c),
            Positions = pos, Normals = nrm, Tangents = tan, Uv = uv, Uv2 = uv2, Indices = idx,
            Bounds = new Aabb(min, max - min), GridBasis = snapshot.GridGlobalTransform.Basis,
            Profile = profile, Circumference = c, RingRadius = r,
        };
    }
}

/// <summary>Decides visibility and when the near patch mesh must be rebuilt.</summary>
public sealed class ContainmentNearPatchPlanner
{
    public ContainmentNearPatchMesh? Mesh { get; private set; }
    public int BuildCount { get; private set; }

    public void Invalidate() => Mesh = null;

    public ContainmentNearPatchView Update(in ContainmentFieldProfile profile, in RingLodGeometrySnapshot snapshot, in RingCanonicalPosition camera, double rangeMeters)
    {
        if (!profile.IsValid || !(rangeMeters > 0.0))
            return new ContainmentNearPatchView(false, ContainmentFieldViewSide.None, double.NaN, default, false);
        ContainmentFieldPoint nearest = ContainmentFieldGeometry.NearestAndSigned(profile, camera, out double sd);
        if (!(Math.Abs(sd) < rangeMeters * ContainmentFieldNearPatch.VisibleRangeFactor))
            return new ContainmentNearPatchView(false, ContainmentFieldViewSide.None, sd, default, false);

        bool rebuild = NeedsRebuild(profile, snapshot, nearest, rangeMeters);
        if (rebuild)
        {
            Mesh = ContainmentFieldNearPatch.Build(profile, snapshot, nearest, rangeMeters);
            BuildCount++;
        }
        return new ContainmentNearPatchView(true, ContainmentFieldNearPatch.SideOf(sd), sd, nearest, rebuild);
    }

    private bool NeedsRebuild(in ContainmentFieldProfile profile, in RingLodGeometrySnapshot snapshot, in ContainmentFieldPoint nearest, double rangeMeters)
    {
        ContainmentNearPatchMesh? m = Mesh;
        if (m == null || m.Profile != profile || m.RangeMeters != rangeMeters
            || m.Circumference != snapshot.CircumferenceMeters || m.RingRadius != snapshot.RingRadiusMeters
            || m.GridBasis != snapshot.GridGlobalTransform.Basis) return true;
        double c = snapshot.CircumferenceMeters, limit = ContainmentFieldNearPatch.RebuildFraction * rangeMeters;
        double da = (nearest.AroundMeters - m.Anchor.AroundMeters) % c;
        if (da > c * 0.5) da -= c; else if (da < -c * 0.5) da += c;
        return Math.Abs(da) > limit || Math.Abs(nearest.ArchLengthMeters - m.Anchor.ArchLengthMeters) > limit;
    }
}
