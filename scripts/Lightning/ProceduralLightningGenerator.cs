using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Lightning;

public enum LightningLod
{
    Low,
    Medium,
    High,
    Ultra
}

public sealed class LightningSettings
{
    public int MinSegments { get; set; } = 14;
    public int MaxSegments { get; set; } = 26;
    public float Jitter { get; set; } = 0.30f;
    public float NoiseScale { get; set; } = 2.5f;
    public float EndpointAttraction { get; set; } = 0.72f;
    public float SegmentLengthVariation { get; set; } = 0.30f;

    public int MinBranches { get; set; } = 1;
    public int MaxBranches { get; set; } = 3;
    public float BranchLengthMin { get; set; } = 0.15f;
    public float BranchLengthMax { get; set; } = 0.40f;
    public float BranchAngle { get; set; } = 0.95f;
    public float MinimumBranchSpacing { get; set; } = 0.12f;

    public int MaxSecondaryBranchDepth { get; set; } = 1;
    public float SecondaryBranchChance { get; set; } = 0.20f;
    public float SecondaryBranchLengthMin { get; set; } = 0.10f;
    public float SecondaryBranchLengthMax { get; set; } = 0.25f;

    public float CoreWidth { get; set; } = 0.035f;
    public float GlowWidth { get; set; } = 0.20f;
    public float WidthVariation { get; set; } = 0.25f;
    public float BranchWidthMultiplier { get; set; } = 0.55f;

    public float StrikeWeight1 { get; set; } = 0.70f;
    public float StrikeWeight2 { get; set; } = 0.20f;
    public float StrikeWeight3 { get; set; } = 0.07f;
    public float StrikeWeight4 { get; set; } = 0.03f;
    public float MaximumStrikeDuration { get; set; } = 1.25f;
    public float MinimumStrikeDelay { get; set; } = 0.035f;
    public float MinimumStrikeIntensity { get; set; } = 0.65f;
    public float MaximumStrikeIntensity { get; set; } = 1.0f;
    public float StrikeIntensityVariation { get; set; } = 0.20f;
    public float CoreFadeTime { get; set; } = 0.075f;
    public float GlowFadeTime { get; set; } = 0.25f;
    public LightningLod Lod { get; set; } = LightningLod.High;
}

public sealed class LightningBolt
{
    public int Seed { get; init; }
    public LightningPath MainPath { get; init; } = new();
    public List<LightningBranch> Branches { get; init; } = new();
    public List<LightningStrike> Strikes { get; init; } = new();
    public float Lifetime { get; init; }
    public float CoreWidth { get; init; }
    public float GlowWidth { get; init; }
}

public sealed class LightningPath
{
    public List<LightningPoint> Points { get; } = new();
    public float Length { get; set; }
}

public sealed class LightningPoint
{
    public LightningPoint(Vector2 position, float width, float normalizedPosition)
    {
        Position = position;
        Width = width;
        NormalizedPosition = normalizedPosition;
    }

    public Vector2 Position { get; set; }
    public float Width { get; set; }
    public float NormalizedPosition { get; set; }
}

public sealed class LightningBranch
{
    public LightningPath Path { get; init; } = new();
    public int Depth { get; init; }
    public int ParentPointIndex { get; init; }
    public List<LightningBranch> Children { get; } = new();
}

public sealed class LightningStrike
{
    public float Time { get; init; }
    public float Intensity { get; init; }
    public float CoreFadeTime { get; init; }
    public float GlowFadeTime { get; init; }
}

/// <summary>
/// Deterministic 2D lightning-channel generator. A renderer can project the
/// returned plane into any 3D basis, which keeps generation independent from
/// the ring's curved coordinate frame.
/// </summary>
public static class ProceduralLightningGenerator
{
    public static LightningBolt Generate(
        Vector2 start,
        Vector2 end,
        int seed,
        LightningSettings? settings = null)
    {
        settings ??= new LightningSettings();
        var random = new Random(seed);
        ApplyLod(settings, out int minSegments, out int maxSegments, out int maxBranches, out int maxSecondaryDepth);

        int segmentCount = RandomInt(random, minSegments, maxSegments);
        LightningPath mainPath = GenerateMainPath(start, end, segmentCount, settings, random);
        int branchCount = RandomInt(
            random,
            Math.Max(0, settings.MinBranches),
            Math.Max(0, Math.Min(settings.MaxBranches, maxBranches)));
        List<LightningBranch> branches = GenerateBranches(
            mainPath,
            branchCount,
            settings,
            random,
            maxSecondaryDepth);
        List<LightningStrike> strikes = GenerateStrikeSchedule(settings, random);
        float lifetime = 0.0f;
        foreach (LightningStrike strike in strikes)
            lifetime = Math.Max(lifetime, strike.Time + Math.Max(strike.CoreFadeTime, strike.GlowFadeTime));

        return new LightningBolt
        {
            Seed = seed,
            MainPath = mainPath,
            Branches = branches,
            Strikes = strikes,
            Lifetime = lifetime,
            CoreWidth = settings.CoreWidth,
            GlowWidth = settings.GlowWidth
        };
    }

    private static LightningPath GenerateMainPath(
        Vector2 start,
        Vector2 end,
        int segments,
        LightningSettings settings,
        Random random)
    {
        var path = new LightningPath();
        Vector2 direction = end - start;
        float totalLength = direction.Length();
        if (totalLength <= 0.00001f)
        {
            path.Points.Add(new LightningPoint(start, 1.0f, 0.0f));
            path.Points.Add(new LightningPoint(end, 0.0f, 1.0f));
            return path;
        }

        segments = Math.Max(2, segments);
        Vector2 forward = direction / totalLength;
        Vector2 perpendicular = new(-forward.Y, forward.X);
        float[] normalizedPositions = BuildSegmentPositions(segments, settings.SegmentLengthVariation, random);
        int noiseSeed = random.Next();
        for (int i = 0; i <= segments; i++)
        {
            float t = normalizedPositions[i];
            Vector2 idealPosition = start.Lerp(end, t);
            Vector2 position = idealPosition;
            if (i != 0 && i != segments)
            {
                float noise = FractalNoise(t * settings.NoiseScale, noiseSeed) * 2.0f - 1.0f;
                float randomJitter = RandomRange(random, -1.0f, 1.0f);
                float displacement = noise * 0.65f + randomJitter * 0.35f;
                displacement *= totalLength
                    * Math.Max(0.0f, settings.Jitter)
                    * 0.15f
                    * MathF.Sin(t * MathF.PI);
                position += perpendicular * displacement;
                position = position.Lerp(idealPosition, Mathf.Clamp(settings.EndpointAttraction, 0.0f, 1.0f));
            }

            path.Points.Add(new LightningPoint(
                position,
                CalculateWidth(t, 1.0f, settings, random),
                t));
        }

        CalculatePathLength(path);
        return path;
    }

    private static float[] BuildSegmentPositions(int segments, float variation, Random random)
    {
        float clampedVariation = Mathf.Clamp(variation, 0.0f, 0.95f);
        var positions = new float[segments + 1];
        float total = 0.0f;
        for (int i = 1; i <= segments; i++)
        {
            total += 1.0f + RandomRange(random, -clampedVariation, clampedVariation);
            positions[i] = total;
        }
        if (total <= 0.00001f) total = 1.0f;
        for (int i = 1; i <= segments; i++) positions[i] /= total;
        positions[segments] = 1.0f;
        return positions;
    }

    private static List<LightningBranch> GenerateBranches(
        LightningPath mainPath,
        int count,
        LightningSettings settings,
        Random random,
        int maxSecondaryDepth)
    {
        var branches = new List<LightningBranch>();
        var usedIndices = new List<int>();
        int attempts = 0;
        while (branches.Count < count && attempts++ < count * 20 && mainPath.Points.Count >= 6)
        {
            int index = RandomInt(random, 2, mainPath.Points.Count - 3);
            if (IsTooCloseToExistingBranch(
                index,
                usedIndices,
                settings.MinimumBranchSpacing,
                mainPath.Points.Count))
                continue;

            LightningBranch? branch = GenerateBranch(mainPath, index, 0, settings, random);
            if (branch == null) continue;
            usedIndices.Add(index);
            branches.Add(branch);
            GenerateSecondaryBranches(branch, settings, random, 1, maxSecondaryDepth);
        }
        return branches;
    }

    private static void GenerateSecondaryBranches(
        LightningBranch parent,
        LightningSettings settings,
        Random random,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth
            || random.NextDouble() > Mathf.Clamp(settings.SecondaryBranchChance, 0.0f, 1.0f)
            || parent.Path.Points.Count < 5)
            return;

        int index = RandomInt(random, 1, parent.Path.Points.Count - 3);
        LightningBranch? child = GenerateBranch(parent.Path, index, depth, settings, random);
        if (child == null) return;
        parent.Children.Add(child);
        GenerateSecondaryBranches(child, settings, random, depth + 1, maxDepth);
    }

    private static LightningBranch? GenerateBranch(
        LightningPath parentPath,
        int parentIndex,
        int depth,
        LightningSettings settings,
        Random random)
    {
        if (parentIndex <= 0 || parentIndex >= parentPath.Points.Count - 1) return null;
        Vector2 source = parentPath.Points[parentIndex].Position;
        Vector2 parentDirection = parentPath.Points[parentIndex + 1].Position
            - parentPath.Points[parentIndex - 1].Position;
        if (parentDirection.LengthSquared() < 0.00001f) return null;
        parentDirection = parentDirection.Normalized();

        float side = random.NextDouble() < 0.5 ? -1.0f : 1.0f;
        Vector2 perpendicular = new(-parentDirection.Y, parentDirection.X);
        Vector2 branchDirection = (parentDirection * 0.25f + perpendicular * side).Normalized();
        branchDirection = branchDirection.Rotated(RandomRange(
            random,
            -Math.Max(0.0f, settings.BranchAngle),
            Math.Max(0.0f, settings.BranchAngle)));

        float minimumLength = depth > 0
            ? settings.SecondaryBranchLengthMin
            : settings.BranchLengthMin;
        float maximumLength = depth > 0
            ? settings.SecondaryBranchLengthMax
            : settings.BranchLengthMax;
        float length = parentPath.Length
            * RandomRange(random, Math.Max(0.01f, minimumLength), Math.Max(minimumLength, maximumLength))
            / (1.0f + depth * 0.75f);
        Vector2 end = source + branchDirection * length;
        int segments = Math.Max(3, (int)(8.0f / (depth + 1)));
        LightningPath path = GenerateMainPath(source, end, segments, settings, random);
        float widthMultiplier = MathF.Pow(Mathf.Clamp(settings.BranchWidthMultiplier, 0.05f, 1.0f), depth + 1);
        foreach (LightningPoint point in path.Points) point.Width *= widthMultiplier;
        return new LightningBranch { Path = path, Depth = depth, ParentPointIndex = parentIndex };
    }

    private static bool IsTooCloseToExistingBranch(
        int index,
        List<int> existing,
        float normalizedSpacing,
        int pointCount)
    {
        float normalizedIndex = (float)index / Math.Max(1, pointCount - 1);
        foreach (int existingIndex in existing)
        {
            float existingNormalized = (float)existingIndex / Math.Max(1, pointCount - 1);
            if (Math.Abs(normalizedIndex - existingNormalized) < Math.Max(0.0f, normalizedSpacing)) return true;
        }
        return false;
    }

    private static float CalculateWidth(
        float t,
        float multiplier,
        LightningSettings settings,
        Random random)
    {
        float taper = 0.75f + 0.25f * MathF.Sin(t * MathF.PI);
        float randomVariation = 1.0f + RandomRange(
            random,
            -Mathf.Clamp(settings.WidthVariation, 0.0f, 0.95f),
            Mathf.Clamp(settings.WidthVariation, 0.0f, 0.95f));
        return taper * randomVariation * multiplier;
    }

    private static List<LightningStrike> GenerateStrikeSchedule(
        LightningSettings settings,
        Random random)
    {
        var strikes = new List<LightningStrike>();
        int count = GenerateStrikeCount(settings, random);
        strikes.Add(new LightningStrike
        {
            Time = 0.0f,
            Intensity = RandomRange(random, settings.MinimumStrikeIntensity, settings.MaximumStrikeIntensity),
            CoreFadeTime = Math.Max(0.01f, settings.CoreFadeTime),
            GlowFadeTime = Math.Max(0.01f, settings.GlowFadeTime)
        });

        float currentTime = 0.0f;
        for (int i = 1; i < count; i++)
        {
            float normalized = MathF.Pow((float)random.NextDouble(), 2.5f);
            float remaining = Math.Max(0.0f, settings.MaximumStrikeDuration) - currentTime;
            if (remaining <= settings.MinimumStrikeDelay) break;
            currentTime += Math.Max(0.001f, settings.MinimumStrikeDelay) + normalized * remaining * 0.65f;
            float falloff = 1.0f - (float)i / Math.Max(1, count);
            float intensity = settings.MinimumStrikeIntensity
                + (settings.MaximumStrikeIntensity - settings.MinimumStrikeIntensity) * falloff
                + RandomRange(
                    random,
                    -Math.Max(0.0f, settings.StrikeIntensityVariation),
                    Math.Max(0.0f, settings.StrikeIntensityVariation));
            strikes.Add(new LightningStrike
            {
                Time = currentTime,
                Intensity = Mathf.Clamp(intensity, 0.0f, 1.5f),
                CoreFadeTime = Math.Max(0.01f, settings.CoreFadeTime),
                GlowFadeTime = Math.Max(0.01f, settings.GlowFadeTime)
            });
        }
        return strikes;
    }

    private static int GenerateStrikeCount(LightningSettings settings, Random random)
    {
        float weight1 = Math.Max(0.0f, settings.StrikeWeight1);
        float weight2 = Math.Max(0.0f, settings.StrikeWeight2);
        float weight3 = Math.Max(0.0f, settings.StrikeWeight3);
        float weight4 = Math.Max(0.0f, settings.StrikeWeight4);
        float total = weight1 + weight2 + weight3 + weight4;
        if (total <= 0.00001f) return 1;
        float roll = (float)random.NextDouble() * total;
        if (roll < weight1) return 1;
        roll -= weight1;
        if (roll < weight2) return 2;
        roll -= weight2;
        return roll < weight3 ? 3 : 4;
    }

    private static void CalculatePathLength(LightningPath path)
    {
        float length = 0.0f;
        for (int i = 1; i < path.Points.Count; i++)
            length += path.Points[i - 1].Position.DistanceTo(path.Points[i].Position);
        path.Length = length;
    }

    private static float FractalNoise(float x, int seed)
    {
        float value = 0.0f;
        float amplitude = 1.0f;
        float frequency = 1.0f;
        float totalAmplitude = 0.0f;
        for (int octave = 0; octave < 4; octave++)
        {
            value += ValueNoise(x * frequency, seed + octave * 1000) * amplitude;
            totalAmplitude += amplitude;
            amplitude *= 0.5f;
            frequency *= 2.0f;
        }
        return totalAmplitude > 0.0f ? value / totalAmplitude : 0.0f;
    }

    private static float ValueNoise(float x, int seed)
    {
        int x0 = (int)Math.Floor(x);
        int x1 = x0 + 1;
        float t = x - x0;
        t = t * t * (3.0f - 2.0f * t);
        return Mathf.Lerp(HashToFloat(x0, seed), HashToFloat(x1, seed), t);
    }

    private static float HashToFloat(int x, int seed)
    {
        unchecked
        {
            int hash = x * 374761393 + seed * 668265263;
            hash = (hash ^ (hash >> 13)) * 1274126177;
            hash ^= hash >> 16;
            return ((uint)hash & 0xFFFFFF) / 16777215.0f;
        }
    }

    private static void ApplyLod(
        LightningSettings settings,
        out int minSegments,
        out int maxSegments,
        out int maxBranches,
        out int maxSecondaryDepth)
    {
        switch (settings.Lod)
        {
            case LightningLod.Low:
                minSegments = 7;
                maxSegments = 12;
                maxBranches = 1;
                maxSecondaryDepth = 0;
                break;
            case LightningLod.Medium:
                minSegments = 10;
                maxSegments = 17;
                maxBranches = 2;
                maxSecondaryDepth = 1;
                break;
            case LightningLod.Ultra:
                minSegments = settings.MinSegments + 5;
                maxSegments = settings.MaxSegments + 12;
                maxBranches = settings.MaxBranches;
                maxSecondaryDepth = settings.MaxSecondaryBranchDepth + 1;
                break;
            default:
                minSegments = settings.MinSegments;
                maxSegments = settings.MaxSegments;
                maxBranches = settings.MaxBranches;
                maxSecondaryDepth = settings.MaxSecondaryBranchDepth;
                break;
        }
        minSegments = Math.Max(3, minSegments);
        maxSegments = Math.Max(minSegments, maxSegments);
        maxBranches = Math.Max(0, maxBranches);
        maxSecondaryDepth = Math.Max(0, maxSecondaryDepth);
    }

    private static float RandomRange(Random random, float minimum, float maximum)
    {
        if (maximum < minimum) (minimum, maximum) = (maximum, minimum);
        return minimum + (float)random.NextDouble() * (maximum - minimum);
    }

    private static int RandomInt(Random random, int minimum, int maximum)
    {
        if (maximum < minimum) (minimum, maximum) = (maximum, minimum);
        return random.Next(minimum, maximum + 1);
    }
}
