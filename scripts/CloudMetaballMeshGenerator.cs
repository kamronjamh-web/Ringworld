using System;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace RingworldTechDemo;

public enum CloudFormationProfile
{
    Cumulus = 0,
    FairCumulus = Cumulus,
    Stratocumulus = 1,
    LowCloudBank = Stratocumulus,
    StormTower = 2,
    Cumulonimbus = 3,
    AnvilStorm = Cumulonimbus,
    Cirrus = 4,
    Cirrostratus = 5,
    Cirrocumulus = 6,
    Altostratus = 7,
    Altocumulus = 8,
    Stratus = 9,
    Nimbostratus = 10
}

public static class CloudFormationProfiles
{
    public static readonly CloudFormationProfile[] PhysicalArchetypes =
    {
        CloudFormationProfile.Cirrus,
        CloudFormationProfile.Cirrostratus,
        CloudFormationProfile.Cirrocumulus,
        CloudFormationProfile.Altostratus,
        CloudFormationProfile.Altocumulus,
        CloudFormationProfile.Stratus,
        CloudFormationProfile.Stratocumulus,
        CloudFormationProfile.Nimbostratus,
        CloudFormationProfile.Cumulus,
        CloudFormationProfile.Cumulonimbus
    };

    public static int ToShaderProfile(CloudFormationProfile profile) => profile switch
    {
        CloudFormationProfile.StormTower => 2,
        CloudFormationProfile.Cumulonimbus => 3,
        CloudFormationProfile.Nimbostratus => 2,
        CloudFormationProfile.Stratocumulus
            or CloudFormationProfile.Cirrostratus
            or CloudFormationProfile.Altostratus
            or CloudFormationProfile.Stratus => 1,
        _ => 0
    };

    public static bool IsLayered(CloudFormationProfile profile) => profile is
        CloudFormationProfile.Cirrostratus
        or CloudFormationProfile.Altostratus
        or CloudFormationProfile.Stratus
        or CloudFormationProfile.Stratocumulus
        or CloudFormationProfile.Nimbostratus;

    public static bool IsConvective(CloudFormationProfile profile) => profile is
        CloudFormationProfile.Cumulus
        or CloudFormationProfile.StormTower
        or CloudFormationProfile.Cumulonimbus;
}

/// <summary>
/// Plain-array result from the worker-safe implicit cloud surface extractor.
/// Godot mesh resources are created later on the main thread.
/// </summary>
public readonly record struct CloudMetaballMeshData(
    Vector3[] Vertices,
    Vector3[] Normals,
    int TriangleCount,
    int LobeCount);

internal readonly record struct CloudMetaballLobe(Vector3 Center, Vector3 Radius);

public static class CloudMetaballMeshGenerator
{
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

    public static CloudMetaballMeshData BuildArchetype(
        int seed,
        int archetypeIndex,
        CloudFormationProfile profile,
        int cellsX,
        int cellsY,
        int cellsZ,
        CancellationToken cancellation)
    {
        cellsX = Math.Clamp(cellsX, 12, 48);
        cellsY = Math.Clamp(cellsY, 8, 32);
        cellsZ = Math.Clamp(cellsZ, 12, 48);
        List<CloudMetaballLobe> lobes = BuildLobes(seed, archetypeIndex, profile);
        float smoothness = profile switch
        {
            CloudFormationProfile.Stratocumulus
                or CloudFormationProfile.Cirrostratus
                or CloudFormationProfile.Altostratus
                or CloudFormationProfile.Stratus
                or CloudFormationProfile.Nimbostratus =>
                Lerp(0.11f, 0.17f, Hash01(seed, archetypeIndex, 70)),
            CloudFormationProfile.Cumulonimbus =>
                Lerp(0.09f, 0.14f, Hash01(seed, archetypeIndex, 70)),
            _ => Lerp(0.07f, 0.13f, Hash01(seed, archetypeIndex, 70))
        };
        Vector3 minimum = new(-1.55f, -0.72f, -1.30f);
        Vector3 maximum = new(1.55f, 1.02f, 1.30f);
        Vector3 step = new(
            (maximum.X - minimum.X) / cellsX,
            (maximum.Y - minimum.Y) / cellsY,
            (maximum.Z - minimum.Z) / cellsZ);
        int pointX = cellsX + 1;
        int pointY = cellsY + 1;
        int pointZ = cellsZ + 1;
        var samples = new float[pointX * pointY * pointZ];

        for (int y = 0; y < pointY; y++)
        for (int z = 0; z < pointZ; z++)
        for (int x = 0; x < pointX; x++)
        {
            cancellation.ThrowIfCancellationRequested();
            Vector3 position = minimum + new Vector3(x * step.X, y * step.Y, z * step.Z);
            samples[SampleIndex(x, y, z, pointX, pointZ)] = SampleField(
                position,
                lobes,
                smoothness);
        }

        var vertices = new List<Vector3>(cellsX * cellsY * cellsZ);
        var normals = new List<Vector3>(cellsX * cellsY * cellsZ);
        var cornerPositions = new Vector3[8];
        var cornerValues = new float[8];
        for (int y = 0; y < cellsY; y++)
        for (int z = 0; z < cellsZ; z++)
        for (int x = 0; x < cellsX; x++)
        {
            cancellation.ThrowIfCancellationRequested();
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

            for (int tetrahedron = 0; tetrahedron < 6; tetrahedron++)
            {
                PolygoniseTetrahedron(
                    cornerPositions,
                    cornerValues,
                    tetrahedron,
                    lobes,
                    smoothness,
                    step,
                    vertices,
                    normals);
            }
        }

        return new CloudMetaballMeshData(
            vertices.ToArray(),
            normals.ToArray(),
            vertices.Count / 3,
            lobes.Count);
    }

    internal static List<CloudMetaballLobe> BuildLobes(
        int seed,
        int archetypeIndex,
        CloudFormationProfile profile)
    {
        return profile switch
        {
            CloudFormationProfile.Cirrus => BuildCirrusLobes(seed, archetypeIndex),
            CloudFormationProfile.Cirrostratus => BuildLayerLobes(seed, archetypeIndex, 0.08f, 0.04f),
            CloudFormationProfile.Cirrocumulus => BuildRippleLobes(seed, archetypeIndex, 0.13f),
            CloudFormationProfile.Altostratus => BuildLayerLobes(seed, archetypeIndex, 0.16f, 0.08f),
            CloudFormationProfile.Altocumulus => BuildRippleLobes(seed, archetypeIndex, 0.24f),
            CloudFormationProfile.Stratus => BuildLayerLobes(seed, archetypeIndex, 0.11f, 0.03f),
            CloudFormationProfile.Stratocumulus => BuildBankLobes(seed, archetypeIndex),
            CloudFormationProfile.Nimbostratus => BuildLayerLobes(seed, archetypeIndex, 0.31f, 0.12f),
            CloudFormationProfile.StormTower => BuildStormTowerLobes(seed, archetypeIndex),
            CloudFormationProfile.Cumulonimbus => BuildAnvilLobes(seed, archetypeIndex),
            _ => BuildCumulusLobes(seed, archetypeIndex)
        };
    }

    private static List<CloudMetaballLobe> BuildCirrusLobes(int seed, int archetypeIndex)
    {
        float sweep = Lerp(-0.16f, 0.16f, Hash01(seed, archetypeIndex, 201));
        var lobes = new List<CloudMetaballLobe>(10);
        for (int index = 0; index < 5; index++)
        {
            float t = index / 4.0f;
            float x = Lerp(-1.18f, 1.12f, t);
            float y = -0.05f + sweep * x + MathF.Sin(t * MathF.PI) * 0.09f;
            float z = (Hash01(seed, archetypeIndex, 202 + index) - 0.5f) * 0.18f;
            lobes.Add(new CloudMetaballLobe(
                new Vector3(x, y, z),
                new Vector3(0.48f, 0.055f, 0.13f)));
            if (index is > 0 and < 4)
            {
                float featherSide = index % 2 == 0 ? 1.0f : -1.0f;
                lobes.Add(new CloudMetaballLobe(
                    new Vector3(x - 0.10f, y + 0.06f, z + featherSide * 0.16f),
                    new Vector3(0.34f, 0.045f, 0.10f)));
            }
        }
        return lobes;
    }

    private static List<CloudMetaballLobe> BuildLayerLobes(
        int seed,
        int archetypeIndex,
        float verticalRadius,
        float verticalVariation)
    {
        var lobes = new List<CloudMetaballLobe>(15);
        for (int row = -1; row <= 1; row++)
        for (int column = -2; column <= 2; column++)
        {
            float jitter = Hash01(seed, archetypeIndex, 230 + (row + 1) * 5 + column + 2);
            lobes.Add(new CloudMetaballLobe(
                new Vector3(
                    column * 0.52f + (jitter - 0.5f) * 0.12f,
                    (jitter - 0.5f) * verticalVariation,
                    row * 0.48f + (jitter - 0.5f) * 0.10f),
                new Vector3(0.62f, verticalRadius, 0.56f)));
        }
        return lobes;
    }

    private static List<CloudMetaballLobe> BuildRippleLobes(
        int seed,
        int archetypeIndex,
        float verticalRadius)
    {
        var lobes = new List<CloudMetaballLobe>(15);
        for (int row = -1; row <= 1; row++)
        for (int column = -2; column <= 2; column++)
        {
            float phase = (column + row * 0.5f) * 0.72f;
            float jitter = Hash01(seed, archetypeIndex, 270 + (row + 1) * 5 + column + 2);
            lobes.Add(new CloudMetaballLobe(
                new Vector3(
                    column * 0.49f,
                    MathF.Sin(phase) * 0.08f + (jitter - 0.5f) * 0.05f,
                    row * 0.54f),
                new Vector3(0.25f, verticalRadius, 0.23f)));
        }
        return lobes;
    }

    private static List<CloudMetaballLobe> BuildCumulusLobes(int seed, int archetypeIndex)
    {
        var lobes = new List<CloudMetaballLobe>(14)
        {
            // Broad base and central body guarantee a connected formation.
            new(new Vector3(0.0f, -0.13f, 0.0f), new Vector3(0.80f, 0.19f, 0.59f)),
            new(new Vector3(0.0f, 0.13f, 0.0f), new Vector3(0.57f, 0.37f, 0.46f))
        };
        int additional = 7 + (int)(Hash01(seed, archetypeIndex, 0) * 5.0f);
        float windStretch = Lerp(0.78f, 1.34f, Hash01(seed, archetypeIndex, 1));
        float towerBias = Hash01(seed, archetypeIndex, 2);
        for (int index = 0; index < additional; index++)
        {
            float angle = Hash01(seed, archetypeIndex, 10 + index * 8) * MathF.Tau;
            float radial = MathF.Sqrt(Hash01(seed, archetypeIndex, 11 + index * 8));
            float x = MathF.Cos(angle) * radial * 0.88f * windStretch;
            float z = MathF.Sin(angle) * radial * 0.72f / windStretch;
            float centerWeight = 1.0f - Math.Clamp(radial, 0.0f, 1.0f);
            float y = Lerp(
                -0.02f,
                0.47f + towerBias * 0.24f,
                Hash01(seed, archetypeIndex, 12 + index * 8) * 0.62f + centerWeight * 0.38f);
            float radiusX = Lerp(
                0.27f,
                0.47f,
                Hash01(seed, archetypeIndex, 13 + index * 8));
            float radiusY = Lerp(
                0.20f,
                0.40f + towerBias * 0.10f,
                Hash01(seed, archetypeIndex, 14 + index * 8));
            float radiusZ = Lerp(
                0.25f,
                0.45f,
                Hash01(seed, archetypeIndex, 15 + index * 8));
            // Pull outer lobes toward the base so smooth union remains connected.
            y -= radial * Lerp(0.02f, 0.16f, Hash01(seed, archetypeIndex, 16 + index * 8));
            lobes.Add(new CloudMetaballLobe(
                new Vector3(x, y, z),
                new Vector3(radiusX, radiusY, radiusZ)));
        }
        return lobes;
    }

    private static List<CloudMetaballLobe> BuildBankLobes(int seed, int archetypeIndex)
    {
        float stretch = Lerp(0.88f, 1.18f, Hash01(seed, archetypeIndex, 81));
        var lobes = new List<CloudMetaballLobe>(14)
        {
            new(new Vector3(0.0f, -0.28f, 0.0f), new Vector3(1.18f, 0.18f, 0.70f)),
            new(new Vector3(-0.62f, -0.08f, 0.03f), new Vector3(0.72f, 0.22f, 0.55f)),
            new(new Vector3(0.58f, -0.06f, -0.04f), new Vector3(0.78f, 0.24f, 0.56f)),
            new(new Vector3(0.0f, 0.10f, 0.0f), new Vector3(0.84f, 0.24f, 0.62f))
        };
        int additional = 6 + (int)(Hash01(seed, archetypeIndex, 82) * 4.0f);
        for (int index = 0; index < additional; index++)
        {
            float t = additional <= 1 ? 0.5f : index / (float)(additional - 1);
            float x = Lerp(-1.02f, 1.02f, t) * stretch
                + (Hash01(seed, archetypeIndex, 90 + index * 5) - 0.5f) * 0.22f;
            float z = (Hash01(seed, archetypeIndex, 91 + index * 5) - 0.5f) * 0.82f;
            float y = Lerp(
                -0.18f,
                0.20f,
                Hash01(seed, archetypeIndex, 92 + index * 5));
            lobes.Add(new CloudMetaballLobe(
                new Vector3(x, y, z),
                new Vector3(
                    Lerp(0.38f, 0.62f, Hash01(seed, archetypeIndex, 93 + index * 5)),
                    Lerp(0.14f, 0.25f, Hash01(seed, archetypeIndex, 94 + index * 5)),
                    Lerp(0.36f, 0.56f, Hash01(seed, archetypeIndex, 95 + index * 5)))));
        }
        return lobes;
    }

    private static List<CloudMetaballLobe> BuildStormTowerLobes(int seed, int archetypeIndex)
    {
        float lean = (Hash01(seed, archetypeIndex, 121) - 0.5f) * 0.24f;
        var lobes = new List<CloudMetaballLobe>(14)
        {
            new(new Vector3(0.0f, -0.43f, 0.0f), new Vector3(0.82f, 0.16f, 0.68f)),
            new(new Vector3(-lean * 0.2f, -0.25f, 0.0f), new Vector3(0.67f, 0.18f, 0.58f)),
            new(new Vector3(lean * 0.1f, -0.04f, 0.01f), new Vector3(0.58f, 0.18f, 0.53f)),
            new(new Vector3(lean * 0.55f, 0.17f, -0.01f), new Vector3(0.56f, 0.18f, 0.51f)),
            new(new Vector3(lean, 0.38f, 0.0f), new Vector3(0.62f, 0.19f, 0.57f)),
            new(new Vector3(lean * 1.18f, 0.59f, 0.0f), new Vector3(0.72f, 0.18f, 0.64f)),
            new(new Vector3(lean * 1.10f, 0.76f, 0.0f), new Vector3(0.76f, 0.14f, 0.66f))
        };
        int additional = 5 + (int)(Hash01(seed, archetypeIndex, 122) * 4.0f);
        for (int index = 0; index < additional; index++)
        {
            float height = Lerp(
                -0.25f,
                0.65f,
                Hash01(seed, archetypeIndex, 130 + index * 6));
            float widthAtHeight = Lerp(0.54f, 0.28f, SmoothUnit((height + 0.2f) / 0.75f));
            float angle = Hash01(seed, archetypeIndex, 131 + index * 6) * MathF.Tau;
            float radial = Hash01(seed, archetypeIndex, 132 + index * 6) * widthAtHeight;
            lobes.Add(new CloudMetaballLobe(
                new Vector3(
                    lean * (height + 0.35f) + MathF.Cos(angle) * radial,
                    height,
                    MathF.Sin(angle) * radial),
                new Vector3(
                    Lerp(0.34f, 0.56f, Hash01(seed, archetypeIndex, 133 + index * 6)),
                    Lerp(0.13f, 0.19f, Hash01(seed, archetypeIndex, 134 + index * 6)),
                    Lerp(0.33f, 0.53f, Hash01(seed, archetypeIndex, 135 + index * 6)))));
        }
        return lobes;
    }

    private static List<CloudMetaballLobe> BuildAnvilLobes(int seed, int archetypeIndex)
    {
        List<CloudMetaballLobe> lobes = BuildStormTowerLobes(seed, archetypeIndex);
        float direction = Hash01(seed, archetypeIndex, 170) < 0.5f ? -1.0f : 1.0f;
        float skew = (Hash01(seed, archetypeIndex, 171) - 0.5f) * 0.25f;
        lobes.Add(new CloudMetaballLobe(
            new Vector3(direction * 0.16f, 0.73f, skew),
            new Vector3(1.22f, 0.10f, 0.79f)));
        lobes.Add(new CloudMetaballLobe(
            new Vector3(direction * 0.69f, 0.75f, skew * 1.4f),
            new Vector3(0.91f, 0.08f, 0.68f)));
        lobes.Add(new CloudMetaballLobe(
            new Vector3(-direction * 0.42f, 0.70f, -skew),
            new Vector3(0.78f, 0.09f, 0.65f)));
        lobes.Add(new CloudMetaballLobe(
            new Vector3(direction * 0.22f, 0.86f, skew),
            new Vector3(0.74f, 0.07f, 0.52f)));
        return lobes;
    }

    private static void PolygoniseTetrahedron(
        Vector3[] cubePositions,
        float[] cubeValues,
        int tetrahedron,
        List<CloudMetaballLobe> lobes,
        float smoothness,
        Vector3 gridStep,
        List<Vector3> vertices,
        List<Vector3> normals)
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
            AddTriangle(a, b, c, lobes, smoothness, gridStep, vertices, normals);
            return;
        }
        if (insideCount == 3)
        {
            Vector3 a = Interpolate(positions[outside[0]], positions[inside[0]], values[outside[0]], values[inside[0]]);
            Vector3 b = Interpolate(positions[outside[0]], positions[inside[1]], values[outside[0]], values[inside[1]]);
            Vector3 c = Interpolate(positions[outside[0]], positions[inside[2]], values[outside[0]], values[inside[2]]);
            AddTriangle(a, c, b, lobes, smoothness, gridStep, vertices, normals);
            return;
        }

        Vector3 p0 = Interpolate(positions[inside[0]], positions[outside[0]], values[inside[0]], values[outside[0]]);
        Vector3 p1 = Interpolate(positions[inside[0]], positions[outside[1]], values[inside[0]], values[outside[1]]);
        Vector3 p2 = Interpolate(positions[inside[1]], positions[outside[0]], values[inside[1]], values[outside[0]]);
        Vector3 p3 = Interpolate(positions[inside[1]], positions[outside[1]], values[inside[1]], values[outside[1]]);
        AddTriangle(p0, p1, p2, lobes, smoothness, gridStep, vertices, normals);
        AddTriangle(p1, p3, p2, lobes, smoothness, gridStep, vertices, normals);
    }

    private static Vector3 Interpolate(Vector3 a, Vector3 b, float valueA, float valueB)
    {
        float denominator = valueA - valueB;
        float t = MathF.Abs(denominator) <= 0.000001f
            ? 0.5f
            : Math.Clamp(valueA / denominator, 0.0f, 1.0f);
        return a + (b - a) * t;
    }

    private static void AddTriangle(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        List<CloudMetaballLobe> lobes,
        float smoothness,
        Vector3 gridStep,
        List<Vector3> vertices,
        List<Vector3> normals)
    {
        Vector3 faceNormal = (b - a).Cross(c - a);
        if (faceNormal.LengthSquared() <= 0.00000001f) return;
        Vector3 centroid = (a + b + c) / 3.0f;
        Vector3 gradient = SampleGradient(centroid, lobes, smoothness, gridStep);
        if (faceNormal.Dot(gradient) < 0.0f)
            (b, c) = (c, b);

        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
        normals.Add(SampleGradient(a, lobes, smoothness, gridStep));
        normals.Add(SampleGradient(b, lobes, smoothness, gridStep));
        normals.Add(SampleGradient(c, lobes, smoothness, gridStep));
    }

    private static Vector3 SampleGradient(
        Vector3 position,
        List<CloudMetaballLobe> lobes,
        float smoothness,
        Vector3 gridStep)
    {
        float epsilon = MathF.Max(0.0025f, MathF.Min(gridStep.X, MathF.Min(gridStep.Y, gridStep.Z)) * 0.32f);
        float dx = SampleField(position + Vector3.Right * epsilon, lobes, smoothness)
            - SampleField(position - Vector3.Right * epsilon, lobes, smoothness);
        float dy = SampleField(position + Vector3.Up * epsilon, lobes, smoothness)
            - SampleField(position - Vector3.Up * epsilon, lobes, smoothness);
        float dz = SampleField(position + Vector3.Back * epsilon, lobes, smoothness)
            - SampleField(position - Vector3.Back * epsilon, lobes, smoothness);
        Vector3 gradient = new(dx, dy, dz);
        return gradient.LengthSquared() > 0.00000001f
            ? gradient.Normalized()
            : Vector3.Up;
    }

    private static float SampleField(
        Vector3 position,
        List<CloudMetaballLobe> lobes,
        float smoothness)
    {
        float distance = EllipsoidDistance(position, lobes[0]);
        for (int index = 1; index < lobes.Count; index++)
        {
            distance = SmoothMinimum(
                distance,
                EllipsoidDistance(position, lobes[index]),
                smoothness);
        }
        return distance;
    }

    private static float EllipsoidDistance(
        Vector3 position,
        in CloudMetaballLobe ellipsoid)
    {
        Vector3 q = new(
            (position.X - ellipsoid.Center.X) / MathF.Max(0.001f, ellipsoid.Radius.X),
            (position.Y - ellipsoid.Center.Y) / MathF.Max(0.001f, ellipsoid.Radius.Y),
            (position.Z - ellipsoid.Center.Z) / MathF.Max(0.001f, ellipsoid.Radius.Z));
        float minimumRadius = MathF.Min(
            ellipsoid.Radius.X,
            MathF.Min(ellipsoid.Radius.Y, ellipsoid.Radius.Z));
        return (q.Length() - 1.0f) * minimumRadius;
    }

    private static float SmoothMinimum(float a, float b, float radius)
    {
        float h = Math.Clamp(0.5f + 0.5f * (b - a) / MathF.Max(0.0001f, radius), 0.0f, 1.0f);
        return Lerp(b, a, h) - radius * h * (1.0f - h);
    }

    private static int SampleIndex(int x, int y, int z, int pointX, int pointZ) =>
        x + pointX * (z + pointZ * y);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothUnit(float value)
    {
        float t = Math.Clamp(value, 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private static float Hash01(int seed, int archetype, int salt)
    {
        unchecked
        {
            uint hash = (uint)seed ^ 0x9E3779B9u;
            hash ^= (uint)archetype * 0x85EBCA6Bu;
            hash ^= (uint)salt * 0xC2B2AE35u;
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            hash *= 0x846CA68Bu;
            hash ^= hash >> 16;
            return (hash & 0x00FFFFFFu) / 16777215.0f;
        }
    }
}
