using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Terrain.Editable;

namespace RingworldTechDemo.Voxel.LOD;

internal readonly record struct RingGrassTileKey(long Around, long Across);

internal sealed class RingGrassTileData
{
    public const int FloatsPerInstance = 20; // transform 12 + COLOR 4 + custom 4
    public RingGrassTileKey Key { get; }
    public RingGrassTileBounds Bounds { get; }
    public double AnchorHeight { get; }
    public float[] Buffer { get; }
    public Aabb LocalAabb { get; }
    public int InstanceCount => Buffer.Length / FloatsPerInstance;
    public ulong PlacementHash { get; }
    /// <summary>Deterministic source subset used to build this tile (1 = near).</summary>
    public float SourceFraction { get; }
    public byte SourceTier { get; }
    public int BladesPerSource { get; }

    public RingGrassTileData(RingGrassTileKey key, RingGrassTileBounds bounds, double anchorHeight,
        float[] buffer, Aabb localAabb, ulong placementHash, float sourceFraction = 1, byte sourceTier = 0, int bladesPerSource = 1)
    {
        Key = key;
        Bounds = bounds;
        AnchorHeight = anchorHeight;
        Buffer = buffer;
        LocalAabb = localAabb;
        PlacementHash = placementHash;
        SourceFraction = sourceFraction;
        SourceTier = sourceTier;
        BladesPerSource = Math.Clamp(bladesPerSource, 1, 4);
    }
}

/// <summary>Worker-only deterministic placement and instance-buffer builder.</summary>
internal static class RingGrassTileBuilder
{
    // x/y are appearance variation. z/w are canonical, periodic wind anchors.
    private readonly record struct Candidate(ulong Priority, Transform3D Transform, float Curve, float Tint, float WindAround, float WindAcross, Vector4 BiomeWeights);

    /// <summary>Keeps worker memory bounded even when a dense tile has more
    /// eligible roots than its upload reservation. The selected set is the
    /// same lowest-hash prefix the old sort-and-trim path produced.</summary>
    private sealed class BoundedPlacements
    {
        private readonly int _limit;
        private readonly PriorityQueue<Candidate, ulong> _worstFirst = new();
        public BoundedPlacements(int limit) => _limit = limit;
        public void Add(in Candidate candidate)
        {
            if (_limit == 0) return;
            ulong inversePriority = ulong.MaxValue - candidate.Priority;
            if (_worstFirst.Count < _limit) { _worstFirst.Enqueue(candidate, inversePriority); return; }
            _worstFirst.TryPeek(out _, out ulong worstInversePriority);
            if (inversePriority <= worstInversePriority) return;
            _worstFirst.Dequeue();
            _worstFirst.Enqueue(candidate, inversePriority);
        }
        public List<Candidate> ToSortedList()
        {
            var result = new List<Candidate>(_worstFirst.UnorderedItems.Count);
            foreach ((Candidate element, _) in _worstFirst.UnorderedItems) result.Add(element);
            result.Sort(static (left, right) => left.Priority.CompareTo(right.Priority));
            return result;
        }
    }

    public static RingGrassTileData Build(RingGrassTileKey key, RingGrassTileBounds bounds, float cellSize,
        int candidates, float bladesPerSquareMeter, int maxInstances, float maxSlope, RingTerrainSampler sampler,
        RingLodSurfaceEditSnapshot edits, RingLodGeometrySnapshot geometry, CancellationToken cancellation,
        SmoothTerrainVolumetricDensitySnapshot? field = null, RingGrassExclusionSnapshot? exclusions = null,
        RingGrassAppearanceSnapshot appearance = default,
        Func<double, double, RingGrassSurfaceHit?>? surfaceOverride = null,
        bool surfaceOverrideOnly = false, RingGrassSurfaceCache? surfaceCache = null,
        float sourceFraction = 1, byte sourceTier = 0, bool useGrassClumps = false)
    {
        double safeCell = Math.Max(1.0, cellSize);
        long circumference = Math.Max(1L, sampler.AroundRingVoxelLength);
        int width = Math.Max(1, sampler.RingAcrossWidthMeters);
        long anchorAround = CanonicalCell((long)Math.Round(bounds.AroundCenter), circumference);
        int anchorAcross = Math.Clamp((int)Math.Round(bounds.AcrossCenter), 0, width - 1);
        var resolver = field == null ? null : new RingGrassSurfaceResolver(field, edits, sampler, cancellation, surfaceCache);
        double anchorHeight = resolver != null && resolver.TryResolve(bounds.AroundCenter, bounds.AcrossCenter, out RingGrassSurfaceHit anchor)
            ? anchor.Height : edits.GetSurfaceHeight(sampler, anchorAround, anchorAcross);
        int cappedCandidates = Math.Clamp(candidates, 1, 16);
        int cappedInstances = Math.Max(0, maxInstances);
        sourceFraction = float.IsFinite(sourceFraction) ? Mathf.Clamp(sourceFraction, 0, 1) : 1;
        var placements = new BoundedPlacements(cappedInstances);

        void TryPlace(double around, double across, long aroundCell, long acrossCell, int salt)
        {
            cancellation.ThrowIfCancellationRequested();
            // The half-open tests make adjacent and partial seam tiles disjoint.
            if (around < bounds.AroundMin || around >= bounds.AroundMax
                || across < bounds.AcrossMin || across >= bounds.AcrossMax) return;
            if (across < 2.0 || across > width - 2.0) return;
            long canonicalAround = CanonicalCell((long)Math.Round(around), circumference);
            int canonicalAcross = Math.Clamp((int)Math.Round(across), 0, width - 1);
            // This rejection is deliberately before sampling terrain.  The same
            // canonical candidate hash is used by every tier, so far is a
            // subset of mid which is a subset of near, including at the seam.
            if (sourceFraction < 1 && Hash01(canonicalAround, canonicalAcross, sampler.Seed, salt + 97) >= sourceFraction) return;
            // A separate stable lattice hash groups four nearby dense sources
            // without perturbing the established near/mid/far hierarchy.
            if (useGrassClumps && bladesPerSquareMeter > 0
                && Hash01(aroundCell, acrossCell, sampler.Seed, salt + 193) >= .25f) return;
            TerrainSample sample = sampler.GetTerrainSample(canonicalAround, canonicalAcross);
            // Water metadata describes its own column: filled terrain above the
            // water plane is dry and may retain grass.
            RingTerrainSampler.BiomeTextureWeights weights = sampler.GetBiomeTextureWeights(sample);
            float grassWeight = Mathf.Max(0.0f, 1.0f - weights.Desert - weights.Forest - weights.Tundra - weights.Alpine);
            float sparseWeight = Mathf.Clamp(weights.Desert + weights.Tundra + weights.Alpine, 0, 1);
            float chance = grassWeight * .82f + weights.Forest * .42f;
            if (appearance.Enabled) chance = Mathf.Clamp(chance * appearance.DensityFor(weights.Forest, sparseWeight)
                + sparseWeight * .08f * appearance.SparseDensityMultiplier, 0, 1);
            if (chance < .02f || Hash01(aroundCell, acrossCell, sampler.Seed, salt + 2) > chance) return;
            float macro = MacroPatch(canonicalAround, canonicalAcross, sampler.Seed, circumference);
            float bladeWidth = Mathf.Lerp(.045f, .082f, Hash01(aroundCell, acrossCell, sampler.Seed, salt + 3));
            float bladeHeight = Mathf.Lerp(.42f, .92f, Hash01(aroundCell, acrossCell, sampler.Seed, salt + 4)) * Mathf.Lerp(.82f, 1.16f, macro)
                * appearance.HeightFor(weights.Forest, sparseWeight);
            double surfaceHeight;
            Vector3? normal = null;
            if (resolver != null)
            {
                // Diagnostic collection and replay use override-only: a null
                // query result rejects this candidate without CPU resolution.
                // Normal generation leaves the optional override unset.
                RingGrassSurfaceHit? overridden = surfaceOverride?.Invoke(around, across);
                if (overridden.HasValue)
                {
                    RingGrassSurfaceHit hit = overridden.Value;
                    if (hit.Material != SmoothTerrainMaterial.Soil
                        || !IsSlopeSuitable(hit.CanonicalNormal, hit.Height, geometry, maxSlope)) return;
                    surfaceHeight = hit.Height;
                    normal = hit.CanonicalNormal;
                }
                else
                {
                    if (surfaceOverrideOnly || !resolver.TryResolve(around, across, out RingGrassSurfaceHit hit)
                        || hit.Material != SmoothTerrainMaterial.Soil
                        || !IsSlopeSuitable(hit.CanonicalNormal, hit.Height, geometry, maxSlope)) return;
                    surfaceHeight = hit.Height;
                    normal = hit.CanonicalNormal;
                }
            }
            else
            {
                if (!SlopeSuitable(edits, sampler, canonicalAround, canonicalAcross, maxSlope)) return;
                surfaceHeight = edits.GetSurfaceHeight(sampler, canonicalAround, canonicalAcross);
            }
            if (IsWaterCovered(sample, surfaceHeight)) return;
            float clumpRadius = useGrassClumps && bladesPerSquareMeter > 0 ? Mathf.Max(.3f, bladeWidth * 3.6f) : 0;
            if (exclusions?.IsExcluded(around, surfaceHeight, across, bladeHeight, clumpRadius) == true) return;
            surfaceHeight -= .035;
            float yaw = Hash01(aroundCell, acrossCell, sampler.Seed, salt + 5) * Mathf.Tau;
            // Curve is normalized for the shader; tint contains a smooth meadow-scale patch.
            float curve = Hash01(aroundCell, acrossCell, sampler.Seed, salt + 6);
            float tint = Mathf.Clamp(Hash01(aroundCell, acrossCell, sampler.Seed, salt + 7) * .55f + macro * .45f, 0, 1);
            const double windPeriod = 48.0;
            double aroundWaves = Math.Max(1.0, Math.Round(circumference / windPeriod));
            float windAround = (float)(Canonicalize(around, circumference) / circumference * aroundWaves % 1.0);
            float windAcross = (float)(across / windPeriod % 1.0);
            Transform3D transform = RingGrassCoordinates.CreateBladeLocalTransform(bounds, anchorHeight, around,
                surfaceHeight, across, bladeWidth, bladeHeight, yaw, geometry, normal);
            placements.Add(new Candidate(Hash64(aroundCell, acrossCell, sampler.Seed, salt + 9), transform, curve, tint, windAround, windAcross,
                new Vector4(weights.Desert, weights.Forest, weights.Tundra, weights.Alpine)));
        }

        bladesPerSquareMeter = float.IsFinite(bladesPerSquareMeter) ? Mathf.Clamp(bladesPerSquareMeter, 0, 96) : 0;
        if (bladesPerSquareMeter > 0.0f)
        {
            // A global lattice, jittered within each cell, makes density stable
            // at tile edges and the wrapped ring seam.
            double spacing = Math.Sqrt(1.0 / Math.Max(.01f, bladesPerSquareMeter));
            long aroundStart = (long)Math.Floor(bounds.AroundMin / spacing);
            long aroundEnd = (long)Math.Ceiling(bounds.AroundMax / spacing);
            long acrossStart = (long)Math.Floor(bounds.AcrossMin / spacing);
            long acrossEnd = (long)Math.Ceiling(bounds.AcrossMax / spacing);
            for (long acrossCell = acrossStart; acrossCell < acrossEnd; acrossCell++)
            for (long aroundCell = aroundStart; aroundCell < aroundEnd; aroundCell++)
            {
                int salt = 1701;
                double around = (aroundCell + .08 + Hash01(aroundCell, acrossCell, sampler.Seed, salt) * .84) * spacing;
                double across = (acrossCell + .08 + Hash01(aroundCell, acrossCell, sampler.Seed, salt + 1) * .84) * spacing;
                TryPlace(around, across, aroundCell, acrossCell, salt);
            }
        }
        else
        {
            long aroundStart = (long)Math.Floor(bounds.AroundMin / safeCell);
            long aroundEnd = (long)Math.Ceiling(bounds.AroundMax / safeCell);
            long acrossStart = (long)Math.Floor(bounds.AcrossMin / safeCell);
            long acrossEnd = (long)Math.Ceiling(bounds.AcrossMax / safeCell);
            for (long acrossCell = acrossStart; acrossCell < acrossEnd; acrossCell++)
            for (long aroundCell = aroundStart; aroundCell < aroundEnd; aroundCell++)
            for (int candidate = 0; candidate < cappedCandidates; candidate++)
            {
                int salt = 301 + candidate * 11;
                double around = (aroundCell + .04 + Hash01(aroundCell, acrossCell, sampler.Seed, salt) * .92) * safeCell;
                double across = (acrossCell + .04 + Hash01(aroundCell, acrossCell, sampler.Seed, salt + 1) * .92) * safeCell;
                TryPlace(around, across, aroundCell, acrossCell, salt);
            }
        }

        List<Candidate> selected = placements.ToSortedList();
        float[] buffer = new float[selected.Count * RingGrassTileData.FloatsPerInstance];
        Aabb localAabb = BuildBounds(selected, useGrassClumps && bladesPerSquareMeter > 0);
        for (int index = 0; index < selected.Count; index++) Pack(buffer, index * RingGrassTileData.FloatsPerInstance, selected[index]);
        return new RingGrassTileData(key, bounds, anchorHeight, buffer, localAabb, HashBuffer(buffer), sourceFraction, sourceTier,
            useGrassClumps && bladesPerSquareMeter > 0 ? 4 : 1);
    }

    private static Aabb BuildBounds(IReadOnlyList<Candidate> placements, bool clumps)
    {
        if (placements.Count == 0) return new Aabb(Vector3.Zero, Vector3.Zero);
        bool initialized = false;
        Vector3 minimum = default, maximum = default;
        float maximumHeight = 0;
        foreach (Candidate placement in placements)
        {
            maximumHeight = Mathf.Max(maximumHeight, placement.Transform.Basis.Y.Length());
        for (int x = -1; x <= 1; x += 2)
        for (int y = 0; y <= 1; y++)
        for (int z = -1; z <= 1; z += 2)
        {
            // Covers the near/intermediate radius 2.5 clumps and the far
            // source's 1.10 normalized half-width (2.5 + 1.10 + lean slack).
            float extent = clumps ? 3.7f : .5f;
            Vector3 point = placement.Transform * new Vector3(x * extent, y, z * extent);
            if (!initialized) { minimum = maximum = point; initialized = true; }
            else { minimum = minimum.Min(point); maximum = maximum.Max(point); }
        }
        }
        // Covers the tallest profile's bounded rest curve, wind, interaction,
        // and edge-on widening in its local orthogonal frame. This deliberately
        // overestimates rather than claiming a camera-dependent tight bound.
        Vector3 margin = Vector3.One * (maximumHeight * 2.0f + .1f);
        return new Aabb(minimum - margin, maximum - minimum + margin * 2.0f);
    }

    private static void Pack(float[] buffer, int offset, in Candidate candidate)
    {
        Basis basis = candidate.Transform.Basis;
        buffer[offset] = basis.X.X; buffer[offset + 1] = basis.Y.X; buffer[offset + 2] = basis.Z.X; buffer[offset + 3] = candidate.Transform.Origin.X;
        buffer[offset + 4] = basis.X.Y; buffer[offset + 5] = basis.Y.Y; buffer[offset + 6] = basis.Z.Y; buffer[offset + 7] = candidate.Transform.Origin.Y;
        buffer[offset + 8] = basis.X.Z; buffer[offset + 9] = basis.Y.Z; buffer[offset + 10] = basis.Z.Z; buffer[offset + 11] = candidate.Transform.Origin.Z;
        // MultiMesh stores COLOR before INSTANCE_CUSTOM: transform 12, color
        // 4, custom 4. Keep the established custom channel contract intact.
        buffer[offset + 12] = candidate.BiomeWeights.X; buffer[offset + 13] = candidate.BiomeWeights.Y;
        buffer[offset + 14] = candidate.BiomeWeights.Z; buffer[offset + 15] = candidate.BiomeWeights.W;
        buffer[offset + 16] = candidate.Curve; buffer[offset + 17] = candidate.Tint;
        buffer[offset + 18] = candidate.WindAround; buffer[offset + 19] = candidate.WindAcross;
    }

    private static bool SlopeSuitable(RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler, long around, int across, float maximumSlope)
    {
        const int offset = 1;
        float dx = edits.GetSurfaceHeight(sampler, around + offset, across) - edits.GetSurfaceHeight(sampler, around - offset, across);
        float dz = edits.GetSurfaceHeight(sampler, around, across + offset) - edits.GetSurfaceHeight(sampler, around, across - offset);
        return Mathf.RadToDeg(Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz) / (2 * offset))) <= maximumSlope;
    }

    internal static bool IsWaterCovered(in TerrainSample sample, double surfaceHeight)
        => sample.HasSurfaceWater && surfaceHeight <= sample.WaterSurfaceHeight;

    internal static bool IsSlopeSuitable(Vector3 canonicalNormal, double height, RingLodGeometrySnapshot geometry, float maximumSlope)
    {
        if (canonicalNormal.LengthSquared() < .0001f) return false;
        double arcScale = (geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters - height) / geometry.RingRadiusMeters;
        Vector3 physicalNormal = new(canonicalNormal.X / (float)Math.Max(.0001, arcScale), canonicalNormal.Y, canonicalNormal.Z);
        float up = Mathf.Clamp(physicalNormal.Normalized().Y, -1.0f, 1.0f);
        return Mathf.RadToDeg(Mathf.Acos(up)) <= maximumSlope;
    }

    private static long CanonicalCell(long value, long circumference)
    {
        long canonical = value % circumference;
        return canonical < 0 ? canonical + circumference : canonical;
    }

    private static double Canonicalize(double value, long circumference)
    {
        double canonical = value % circumference;
        return canonical < 0 ? canonical + circumference : canonical;
    }

    // Low-frequency periodic variation keeps meadow height and colour changes
    // broad rather than giving every blade independent visual noise.
    private static float MacroPatch(long around, int across, int seed, long circumference)
    {
        // Use an integral number of waves around the ring, so the patch field
        // reaches the identical value and derivative on either side of its seam.
        double aroundWaves = Math.Max(1.0, Math.Round(circumference / 72.0));
        double aroundPhase = CanonicalCell(around, circumference) / (double)circumference * aroundWaves * Math.Tau;
        double acrossPhase = across * (.055 + Hash01(0, 0, seed, 913) * .025);
        double value = Math.Sin(aroundPhase + Hash01(1, 0, seed, 914) * Math.Tau)
            + .55 * Math.Sin(acrossPhase + Hash01(2, 0, seed, 915) * Math.Tau)
            + .25 * Math.Sin(aroundPhase * 2.0 - acrossPhase * .65);
        return Mathf.Clamp((float)(value / 3.6 + .5), 0, 1);
    }

    private static float Hash01(long x, long z, int seed, int salt) =>
        (float)(Hash64(x, z, seed, salt) >> 40) / 16_777_215f;

    private static ulong Hash64(long x, long z, int seed, int salt)
    {
        ulong value = unchecked((ulong)x) * 0x9E3779B185EBCA87UL
            ^ unchecked((ulong)z) * 0xC2B2AE3D27D4EB4FUL
            ^ unchecked((uint)(seed + salt * 7919));
        value ^= value >> 30; value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27; value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static ulong HashBuffer(float[] buffer)
    {
        ulong hash = 14695981039346656037UL;
        foreach (float value in buffer)
        {
            uint bits = BitConverter.SingleToUInt32Bits(value);
            for (int byteIndex = 0; byteIndex < 4; byteIndex++)
            {
                hash ^= (byte)(bits >> (byteIndex * 8));
                hash *= 1099511628211UL;
            }
        }
        return hash;
    }
}
