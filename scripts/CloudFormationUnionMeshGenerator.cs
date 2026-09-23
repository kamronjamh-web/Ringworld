using System;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace RingworldTechDemo;

[Flags]
public enum CloudUnionRenderBands
{
    None = 0,
    DetailedShadow = 1,
    Detailed = 2,
    Transition = 4
}

public readonly record struct CloudUnionMember(
    int Seed,
    int ArchetypeIndex,
    CloudFormationProfile Profile,
    Transform3D Transform,
    Color CloudData);

public sealed record CloudUnionClusterInput(
    CloudUnionMember[] Members,
    CloudUnionRenderBands RenderBands);

public readonly record struct CloudUnionSurfaceData(
    Vector3[] Vertices,
    Vector3[] Normals,
    Color[] Colors,
    Vector2[] Uv2,
    int TriangleCount)
{
    public static CloudUnionSurfaceData Empty => new(
        Array.Empty<Vector3>(),
        Array.Empty<Vector3>(),
        Array.Empty<Color>(),
        Array.Empty<Vector2>(),
        0);
}

public readonly record struct CloudUnionPatchData(
    CloudUnionSurfaceData DetailedShadow,
    CloudUnionSurfaceData Detailed,
    CloudUnionSurfaceData Transition,
    int ClusterCount,
    int MemberCount,
    double BuildMilliseconds);

/// <summary>
/// Extracts one implicit surface for every touching formation group. Members
/// keep their authored metaball lobes and transforms, but the final geometry
/// contains no intersecting formation surfaces and therefore no join seams.
/// The worker emits plain arrays; Godot resources are installed on the main
/// thread by RingVolumetricCloudLayer.
/// </summary>
public static class CloudFormationUnionMeshGenerator
{
    private readonly record struct OrientedLobe(
        Vector3 Center,
        Basis InverseBasis,
        Vector3 AxisExtent,
        float DistanceScale);

    private readonly record struct FieldSample(
        float Distance,
        Vector3 Gradient,
        Color CloudData,
        float FormationHeight,
        float DetailScaleMeters);

    private sealed record MemberField(
        OrientedLobe[] Lobes,
        float InternalSmoothnessMeters,
        Transform3D InverseFormationTransform,
        Color CloudData,
        float DetailScaleMeters,
        Vector3 BoundsMinimum,
        Vector3 BoundsMaximum);

    private sealed class SurfaceAccumulator
    {
        public readonly List<Vector3> Vertices = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Color> Colors = new();
        public readonly List<Vector2> Uv2 = new();

        public CloudUnionSurfaceData ToData() => new(
            Vertices.ToArray(),
            Normals.ToArray(),
            Colors.ToArray(),
            Uv2.ToArray(),
            Vertices.Count / 3);
    }

    private static readonly int[,] Tetrahedra =
    {
        { 0, 5, 1, 6 },
        { 0, 1, 2, 6 },
        { 0, 2, 3, 6 },
        { 0, 3, 7, 6 },
        { 0, 7, 4, 6 },
        { 0, 4, 5, 6 }
    };

    private static readonly Vector3[] CubeOffsets =
    {
        new(0, 0, 0),
        new(1, 0, 0),
        new(1, 0, 1),
        new(0, 0, 1),
        new(0, 1, 0),
        new(1, 1, 0),
        new(1, 1, 1),
        new(0, 1, 1)
    };

    public static CloudUnionPatchData BuildPatch(
        IReadOnlyList<CloudUnionClusterInput> clusters,
        float detailedCellMeters,
        int detailedMaximumCellsPerAxis,
        float transitionCellMeters,
        int transitionMaximumCellsPerAxis,
        float formationBlendRadiusMeters,
        CancellationToken cancellation)
    {
        ulong started = Time.GetTicksUsec();
        var detailedShadow = new SurfaceAccumulator();
        var detailed = new SurfaceAccumulator();
        var transition = new SurfaceAccumulator();
        int memberCount = 0;

        foreach (CloudUnionClusterInput cluster in clusters)
        {
            cancellation.ThrowIfCancellationRequested();
            if (cluster.Members.Length < 2) continue;
            memberCount += cluster.Members.Length;

            if ((cluster.RenderBands & CloudUnionRenderBands.DetailedShadow) != 0)
            {
                AppendCluster(
                    cluster,
                    detailedShadow,
                    detailedCellMeters,
                    detailedMaximumCellsPerAxis,
                    formationBlendRadiusMeters,
                    cancellation);
            }
            else if ((cluster.RenderBands & CloudUnionRenderBands.Detailed) != 0)
            {
                AppendCluster(
                    cluster,
                    detailed,
                    detailedCellMeters,
                    detailedMaximumCellsPerAxis,
                    formationBlendRadiusMeters,
                    cancellation);
            }

            if ((cluster.RenderBands & CloudUnionRenderBands.Transition) != 0)
            {
                AppendCluster(
                    cluster,
                    transition,
                    transitionCellMeters,
                    transitionMaximumCellsPerAxis,
                    formationBlendRadiusMeters,
                    cancellation);
            }
        }

        return new CloudUnionPatchData(
            detailedShadow.ToData(),
            detailed.ToData(),
            transition.ToData(),
            clusters.Count,
            memberCount,
            (Time.GetTicksUsec() - started) / 1000.0);
    }

    private static void AppendCluster(
        CloudUnionClusterInput cluster,
        SurfaceAccumulator destination,
        float targetCellMeters,
        int maximumCellsPerAxis,
        float blendRadiusMeters,
        CancellationToken cancellation)
    {
        List<MemberField> fields = BuildMemberFields(cluster.Members);
        if (fields.Count < 2) return;

        Vector3 minimum = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        foreach (MemberField field in fields)
        foreach (OrientedLobe lobe in field.Lobes)
        {
            minimum = minimum.Min(lobe.Center - lobe.AxisExtent);
            maximum = maximum.Max(lobe.Center + lobe.AxisExtent);
        }

        float requestedCell = Math.Clamp(targetCellMeters, 25.0f, 1000.0f);
        int cellCap = Math.Clamp(maximumCellsPerAxis, 12, 72);
        float padding = Math.Max(
            requestedCell * 1.5f,
            Math.Clamp(blendRadiusMeters, 0.0f, 1200.0f) * 0.65f);
        minimum -= Vector3.One * padding;
        maximum += Vector3.One * padding;
        Vector3 size = maximum - minimum;
        int cellsX = Math.Clamp(Mathf.CeilToInt(size.X / requestedCell), 12, cellCap);
        int cellsY = Math.Clamp(Mathf.CeilToInt(size.Y / requestedCell), 8, cellCap);
        int cellsZ = Math.Clamp(Mathf.CeilToInt(size.Z / requestedCell), 12, cellCap);
        Vector3 step = new(
            size.X / cellsX,
            size.Y / cellsY,
            size.Z / cellsZ);
        int pointX = cellsX + 1;
        int pointY = cellsY + 1;
        int pointZ = cellsZ + 1;
        var samples = new float[pointX * pointY * pointZ];
        float blendRadius = Math.Clamp(blendRadiusMeters, 0.0f, 1200.0f);

        for (int y = 0; y < pointY; y++)
        for (int z = 0; z < pointZ; z++)
        for (int x = 0; x < pointX; x++)
        {
            if ((x & 7) == 0) cancellation.ThrowIfCancellationRequested();
            Vector3 position = minimum + new Vector3(x * step.X, y * step.Y, z * step.Z);
            samples[SampleIndex(x, y, z, pointX, pointZ)] = SampleClusterField(
                position,
                fields,
                blendRadius);
        }

        var cornerPositions = new Vector3[8];
        var cornerValues = new float[8];
        for (int y = 0; y < cellsY; y++)
        for (int z = 0; z < cellsZ; z++)
        for (int x = 0; x < cellsX; x++)
        {
            if ((x & 7) == 0) cancellation.ThrowIfCancellationRequested();
            Vector3 cellMinimum = minimum + new Vector3(x * step.X, y * step.Y, z * step.Z);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 offset = CubeOffsets[corner];
                int sampleX = x + (int)offset.X;
                int sampleY = y + (int)offset.Y;
                int sampleZ = z + (int)offset.Z;
                cornerPositions[corner] = cellMinimum + offset * step;
                cornerValues[corner] = samples[
                    SampleIndex(sampleX, sampleY, sampleZ, pointX, pointZ)];
            }

            bool hasInside = false;
            bool hasOutside = false;
            for (int corner = 0; corner < 8; corner++)
            {
                hasInside |= cornerValues[corner] <= 0.0f;
                hasOutside |= cornerValues[corner] > 0.0f;
            }
            if (!hasInside || !hasOutside) continue;

            for (int tetrahedron = 0; tetrahedron < 6; tetrahedron++)
            {
                PolygoniseTetrahedron(
                    cornerPositions,
                    cornerValues,
                    tetrahedron,
                    fields,
                    blendRadius,
                    step,
                    destination);
            }
        }
    }

    private static List<MemberField> BuildMemberFields(
        IReadOnlyList<CloudUnionMember> members)
    {
        var fields = new List<MemberField>(members.Count);
        foreach (CloudUnionMember member in members)
        {
            List<CloudMetaballLobe> sourceLobes =
                CloudMetaballMeshGenerator.BuildLobes(
                    member.Seed,
                    member.ArchetypeIndex,
                    member.Profile);
            var lobes = new OrientedLobe[sourceLobes.Count];
            Vector3 boundsMinimum = new(
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity);
            Vector3 boundsMaximum = new(
                float.NegativeInfinity,
                float.NegativeInfinity,
                float.NegativeInfinity);
            Basis formationBasis = member.Transform.Basis;
            float formationMinimumScale = Math.Max(
                1.0f,
                Math.Min(
                    formationBasis.X.Length(),
                    Math.Min(formationBasis.Y.Length(), formationBasis.Z.Length())));
            for (int index = 0; index < sourceLobes.Count; index++)
            {
                CloudMetaballLobe source = sourceLobes[index];
                Basis lobeBasis = new(
                    formationBasis.X * source.Radius.X,
                    formationBasis.Y * source.Radius.Y,
                    formationBasis.Z * source.Radius.Z);
                float distanceScale = Math.Max(
                    1.0f,
                    Math.Min(
                        lobeBasis.X.Length(),
                        Math.Min(lobeBasis.Y.Length(), lobeBasis.Z.Length())));
                Vector3 axisExtent = new(
                    Math.Abs(lobeBasis.X.X) + Math.Abs(lobeBasis.Y.X) + Math.Abs(lobeBasis.Z.X),
                    Math.Abs(lobeBasis.X.Y) + Math.Abs(lobeBasis.Y.Y) + Math.Abs(lobeBasis.Z.Y),
                    Math.Abs(lobeBasis.X.Z) + Math.Abs(lobeBasis.Y.Z) + Math.Abs(lobeBasis.Z.Z));
                lobes[index] = new OrientedLobe(
                    member.Transform * source.Center,
                    lobeBasis.Inverse(),
                    axisExtent,
                    distanceScale);
                boundsMinimum = boundsMinimum.Min(
                    lobes[index].Center - lobes[index].AxisExtent);
                boundsMaximum = boundsMaximum.Max(
                    lobes[index].Center + lobes[index].AxisExtent);
            }

            float normalizedSmoothness = member.Profile switch
            {
                CloudFormationProfile.LowCloudBank => 0.14f,
                CloudFormationProfile.AnvilStorm => 0.115f,
                _ => 0.10f
            };
            fields.Add(new MemberField(
                lobes,
                normalizedSmoothness * formationMinimumScale,
                member.Transform.AffineInverse(),
                member.CloudData,
                formationMinimumScale,
                boundsMinimum,
                boundsMaximum));
        }
        return fields;
    }

    private static void PolygoniseTetrahedron(
        Vector3[] cubePositions,
        float[] cubeValues,
        int tetrahedron,
        List<MemberField> fields,
        float blendRadius,
        Vector3 gridStep,
        SurfaceAccumulator destination)
    {
        Span<Vector3> positions = stackalloc Vector3[4];
        Span<float> values = stackalloc float[4];
        Span<int> inside = stackalloc int[4];
        Span<int> outside = stackalloc int[4];
        int insideCount = 0;
        int outsideCount = 0;
        for (int vertex = 0; vertex < 4; vertex++)
        {
            int corner = Tetrahedra[tetrahedron, vertex];
            positions[vertex] = cubePositions[corner];
            values[vertex] = cubeValues[corner];
            if (values[vertex] <= 0.0f)
                inside[insideCount++] = vertex;
            else
                outside[outsideCount++] = vertex;
        }

        if (insideCount == 0 || insideCount == 4) return;
        if (insideCount == 1)
        {
            Vector3 a = Interpolate(positions[inside[0]], positions[outside[0]], values[inside[0]], values[outside[0]]);
            Vector3 b = Interpolate(positions[inside[0]], positions[outside[1]], values[inside[0]], values[outside[1]]);
            Vector3 c = Interpolate(positions[inside[0]], positions[outside[2]], values[inside[0]], values[outside[2]]);
            AddTriangle(a, b, c, fields, blendRadius, gridStep, destination);
            return;
        }
        if (insideCount == 3)
        {
            Vector3 a = Interpolate(positions[outside[0]], positions[inside[0]], values[outside[0]], values[inside[0]]);
            Vector3 b = Interpolate(positions[outside[0]], positions[inside[1]], values[outside[0]], values[inside[1]]);
            Vector3 c = Interpolate(positions[outside[0]], positions[inside[2]], values[outside[0]], values[inside[2]]);
            AddTriangle(a, c, b, fields, blendRadius, gridStep, destination);
            return;
        }

        Vector3 p0 = Interpolate(positions[inside[0]], positions[outside[0]], values[inside[0]], values[outside[0]]);
        Vector3 p1 = Interpolate(positions[inside[0]], positions[outside[1]], values[inside[0]], values[outside[1]]);
        Vector3 p2 = Interpolate(positions[inside[1]], positions[outside[0]], values[inside[1]], values[outside[0]]);
        Vector3 p3 = Interpolate(positions[inside[1]], positions[outside[1]], values[inside[1]], values[outside[1]]);
        AddTriangle(p0, p1, p2, fields, blendRadius, gridStep, destination);
        AddTriangle(p1, p3, p2, fields, blendRadius, gridStep, destination);
    }

    private static Vector3 Interpolate(Vector3 a, Vector3 b, float valueA, float valueB)
    {
        float denominator = valueA - valueB;
        float t = Math.Abs(denominator) <= 0.000001f
            ? 0.5f
            : Math.Clamp(valueA / denominator, 0.0f, 1.0f);
        return a + (b - a) * t;
    }

    private static void AddTriangle(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        List<MemberField> fields,
        float blendRadius,
        Vector3 gridStep,
        SurfaceAccumulator destination)
    {
        Vector3 faceNormal = (b - a).Cross(c - a);
        if (faceNormal.LengthSquared() <= 0.00000001f) return;
        FieldSample sampleA = SampleClusterWithGradient(a, fields, blendRadius);
        FieldSample sampleB = SampleClusterWithGradient(b, fields, blendRadius);
        FieldSample sampleC = SampleClusterWithGradient(c, fields, blendRadius);
        Vector3 gradient = sampleA.Gradient + sampleB.Gradient + sampleC.Gradient;
        if (faceNormal.Dot(gradient) < 0.0f)
        {
            (b, c) = (c, b);
            (sampleB, sampleC) = (sampleC, sampleB);
        }

        AppendVertex(a, sampleA, destination);
        AppendVertex(b, sampleB, destination);
        AppendVertex(c, sampleC, destination);
    }

    private static void AppendVertex(
        Vector3 position,
        in FieldSample sample,
        SurfaceAccumulator destination)
    {
        destination.Vertices.Add(position);
        destination.Normals.Add(sample.Gradient.LengthSquared() > 0.00000001f
            ? sample.Gradient.Normalized()
            : Vector3.Up);
        destination.Colors.Add(sample.CloudData);
        destination.Uv2.Add(new Vector2(
            sample.FormationHeight,
            sample.DetailScaleMeters));
    }

    private static FieldSample SampleClusterWithGradient(
        Vector3 position,
        List<MemberField> fields,
        float blendRadius)
    {
        FieldSample sample = SampleMemberWithGradient(position, fields[0]);
        for (int index = 1; index < fields.Count; index++)
        {
            if (DistanceToBounds(
                    position,
                    fields[index].BoundsMinimum,
                    fields[index].BoundsMaximum)
                > sample.Distance + blendRadius)
            {
                continue;
            }
            FieldSample member = SampleMemberWithGradient(position, fields[index]);
            sample = blendRadius > 0.001f
                ? SmoothMinimum(sample, member, blendRadius)
                : member.Distance < sample.Distance ? member : sample;
        }
        return sample;
    }

    private static float SampleClusterField(
        Vector3 position,
        List<MemberField> fields,
        float blendRadius)
    {
        float distance = SampleMemberField(position, fields[0]);
        for (int index = 1; index < fields.Count; index++)
        {
            if (DistanceToBounds(
                    position,
                    fields[index].BoundsMinimum,
                    fields[index].BoundsMaximum)
                > distance + blendRadius)
            {
                continue;
            }
            float memberDistance = SampleMemberField(position, fields[index]);
            distance = blendRadius > 0.001f
                ? SmoothMinimum(distance, memberDistance, blendRadius)
                : Math.Min(distance, memberDistance);
        }
        return distance;
    }

    private static float SampleMemberField(Vector3 position, MemberField field)
    {
        float distance = SampleLobe(position, field.Lobes[0]);
        for (int index = 1; index < field.Lobes.Length; index++)
        {
            OrientedLobe lobe = field.Lobes[index];
            if (DistanceToBounds(
                    position,
                    lobe.Center - lobe.AxisExtent,
                    lobe.Center + lobe.AxisExtent)
                > distance + field.InternalSmoothnessMeters)
            {
                continue;
            }
            distance = SmoothMinimum(
                distance,
                SampleLobe(position, lobe),
                field.InternalSmoothnessMeters);
        }
        return distance;
    }

    private static FieldSample SampleMemberWithGradient(
        Vector3 position,
        MemberField field)
    {
        FieldSample sample = SampleLobeWithGradient(
            position,
            field.Lobes[0],
            field.CloudData);
        for (int index = 1; index < field.Lobes.Length; index++)
        {
            OrientedLobe lobe = field.Lobes[index];
            if (DistanceToBounds(
                    position,
                    lobe.Center - lobe.AxisExtent,
                    lobe.Center + lobe.AxisExtent)
                > sample.Distance + field.InternalSmoothnessMeters)
            {
                continue;
            }
            sample = SmoothMinimum(
                sample,
                SampleLobeWithGradient(
                    position,
                    lobe,
                    field.CloudData),
                field.InternalSmoothnessMeters);
        }
        Vector3 local = field.InverseFormationTransform * position;
        return sample with
        {
            CloudData = field.CloudData,
            FormationHeight = Math.Clamp(
                (local.Y + 0.72f) / 1.74f,
                0.0f,
                1.0f),
            DetailScaleMeters = field.DetailScaleMeters
        };
    }

    private static float SampleLobe(Vector3 position, in OrientedLobe lobe)
    {
        Vector3 unit = lobe.InverseBasis * (position - lobe.Center);
        return (unit.Length() - 1.0f) * lobe.DistanceScale;
    }

    private static FieldSample SampleLobeWithGradient(
        Vector3 position,
        in OrientedLobe lobe,
        Color cloudData)
    {
        Vector3 unit = lobe.InverseBasis * (position - lobe.Center);
        float length = unit.Length();
        Vector3 localGradient = length > 0.000001f
            ? unit / length
            : Vector3.Up;
        Vector3 worldGradient =
            lobe.InverseBasis.Transposed() * localGradient * lobe.DistanceScale;
        return new FieldSample(
            (length - 1.0f) * lobe.DistanceScale,
            worldGradient,
            cloudData,
            0.0f,
            1.0f);
    }

    private static FieldSample SmoothMinimum(
        in FieldSample a,
        in FieldSample b,
        float radius)
    {
        float safeRadius = Math.Max(0.0001f, radius);
        float h = Math.Clamp(
            0.5f + 0.5f * (b.Distance - a.Distance) / safeRadius,
            0.0f,
            1.0f);
        float distance = b.Distance
            + (a.Distance - b.Distance) * h
            - safeRadius * h * (1.0f - h);
        Vector3 gradient = b.Gradient.Lerp(a.Gradient, h);
        return new FieldSample(
            distance,
            gradient,
            b.CloudData.Lerp(a.CloudData, h),
            b.FormationHeight + (a.FormationHeight - b.FormationHeight) * h,
            b.DetailScaleMeters + (a.DetailScaleMeters - b.DetailScaleMeters) * h);
    }

    private static float SmoothMinimum(float a, float b, float radius)
    {
        float safeRadius = Math.Max(0.0001f, radius);
        float h = Math.Clamp(0.5f + 0.5f * (b - a) / safeRadius, 0.0f, 1.0f);
        return b + (a - b) * h - safeRadius * h * (1.0f - h);
    }

    private static int SampleIndex(int x, int y, int z, int pointX, int pointZ) =>
        x + pointX * (z + pointZ * y);

    private static float DistanceToBounds(
        Vector3 position,
        Vector3 minimum,
        Vector3 maximum)
    {
        float dx = position.X < minimum.X
            ? minimum.X - position.X
            : position.X > maximum.X ? position.X - maximum.X : 0.0f;
        float dy = position.Y < minimum.Y
            ? minimum.Y - position.Y
            : position.Y > maximum.Y ? position.Y - maximum.Y : 0.0f;
        float dz = position.Z < minimum.Z
            ? minimum.Z - position.Z
            : position.Z > maximum.Z ? position.Z - maximum.Z : 0.0f;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
