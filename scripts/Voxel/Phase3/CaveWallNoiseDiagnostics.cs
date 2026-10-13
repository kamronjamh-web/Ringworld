using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

public static class CaveWallNoiseDiagnostics
{
    public static bool RunSelfTest(out string summary)
    {
        try
        {
            const long aroundLength = 4097;
            CaveGenerationContract contract = CaveGenerationContract.Create(
                7331);
            var applicator = new CaveWallNoiseApplicator(contract, aroundLength);
            var repeated = new CaveWallNoiseApplicator(contract, aroundLength);
            var changedSeed = new CaveWallNoiseApplicator(
                CaveGenerationContract.Create(7332),
                aroundLength);
            var changedRing = new CaveWallNoiseApplicator(contract, aroundLength + 1);
            if (applicator.Identity != repeated.Identity
                || applicator.Identity == changedSeed.Identity
                || applicator.Identity == changedRing.Identity)
            {
                throw new InvalidOperationException("wall-noise identity isolation failed");
            }
            Throws<ArgumentException>(() => new CaveWallNoiseApplicator(default, aroundLength));
            Throws<ArgumentOutOfRangeException>(() => new CaveWallNoiseApplicator(contract, 0));

            var profile = new CaveWallNoiseProfile(
                MaximumDisplacementMeters: 8.0,
                ProtectedOpenCoreMeters: 4.0,
                WallInfluenceMeters: 16.0);
            ValidateInvalidInputs(applicator, profile);
            (double minimum, double maximum, double maximumSeamStep) = ValidateNoiseField(
                applicator,
                changedSeed,
                aroundLength);
            ValidateMaskAndTopologyProtection(applicator, profile);
            ValidateNoRemoteOpenings(applicator, profile);
            ValidateRequestOrderIndependence(applicator, profile);
            int optimizedComparisons = ValidateOptimizedMatchesReference(applicator, changedSeed, profile);

            summary = $"identity={applicator.Identity:X16} range={minimum:F3}..{maximum:F3} seamStep={maximumSeamStep:F6} optimizedVsReference={optimizedComparisons}";
            return true;
        }
        catch (Exception exception)
        {
            summary = exception.Message;
            return false;
        }
    }

    private static void ValidateInvalidInputs(
        CaveWallNoiseApplicator applicator,
        CaveWallNoiseProfile valid)
    {
        Throws<ArgumentOutOfRangeException>(() => applicator.Apply(0, 0, 0, double.NaN, valid));
        Throws<ArgumentOutOfRangeException>(() => applicator.Apply(0, 0, 0, double.PositiveInfinity, valid));
        Throws<ArgumentOutOfRangeException>(() => applicator.Apply(
            0, 0, 0, 0, valid with { MaximumDisplacementMeters = -1 }));
        Throws<ArgumentOutOfRangeException>(() => applicator.Apply(
            0, 0, 0, 0, valid with { MaximumDisplacementMeters = double.NaN }));
        Throws<ArgumentOutOfRangeException>(() => applicator.Apply(
            0, 0, 0, 0, valid with
            {
                MaximumDisplacementMeters = CaveWallNoiseApplicator.MaximumAllowedDisplacementMeters + 1
            }));
        Throws<ArgumentOutOfRangeException>(() => applicator.Apply(
            0, 0, 0, 0, valid with { ProtectedOpenCoreMeters = 0 }));
        Throws<ArgumentOutOfRangeException>(() => applicator.Apply(
            0, 0, 0, 0, valid with { ProtectedOpenCoreMeters = double.PositiveInfinity }));
        Throws<ArgumentOutOfRangeException>(() => applicator.Apply(
            0, 0, 0, 0, valid with
            {
                ProtectedOpenCoreMeters = CaveWallNoiseApplicator.MaximumAllowedProtectedOpenCoreMeters + 1
            }));
        Throws<ArgumentOutOfRangeException>(() => applicator.Apply(
            0, 0, 0, 0, valid with { WallInfluenceMeters = 0 }));
        Throws<ArgumentOutOfRangeException>(() => applicator.Apply(
            0, 0, 0, 0, valid with { WallInfluenceMeters = double.NaN }));
        Throws<ArgumentOutOfRangeException>(() => applicator.Apply(
            0, 0, 0, 0, valid with { WallInfluenceMeters = 7 }));
        Throws<ArgumentOutOfRangeException>(() => applicator.Apply(
            0, 0, 0, 0, valid with
            {
                WallInfluenceMeters = CaveWallNoiseApplicator.MaximumAllowedWallInfluenceMeters + 1
            }));
    }

    private static (double Minimum, double Maximum, double MaximumSeamStep) ValidateNoiseField(
        CaveWallNoiseApplicator applicator,
        CaveWallNoiseApplicator changedSeed,
        long aroundLength)
    {
        var points = new[]
        {
            (Around: -8195L, Vertical: -16385L, Across: -4097L),
            (Around: -1L, Vertical: -1L, Across: -1L),
            (Around: 0L, Vertical: 0L, Across: 0L),
            (Around: 1L, Vertical: -257L, Across: 513L),
            (Around: 4096L, Vertical: -8192L, Across: 16384L),
            (Around: 12345L, Vertical: 987654321L, Across: -123456789L),
            (Around: long.MinValue, Vertical: long.MinValue, Across: long.MaxValue),
            (Around: long.MaxValue, Vertical: long.MaxValue, Across: long.MinValue)
        };
        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        bool seedChangedAValue = false;
        foreach (var point in points)
        {
            double first = applicator.SampleNormalized(point.Around, point.Vertical, point.Across);
            double second = applicator.SampleNormalized(point.Around, point.Vertical, point.Across);
            if (!double.IsFinite(first) || first < -1.0 || first > 1.0 || first != second)
                throw new InvalidOperationException("wall-noise sample was unbounded or nondeterministic");
            minimum = Math.Min(minimum, first);
            maximum = Math.Max(maximum, first);
            if (first != changedSeed.SampleNormalized(point.Around, point.Vertical, point.Across))
                seedChangedAValue = true;
        }
        if (!seedChangedAValue)
            throw new InvalidOperationException("shared terrain seed did not change any sampled wall noise");

        double maximumSeamStep = 0.0;
        for (long vertical = -513; vertical <= 513; vertical += 171)
        for (long across = -1025; across <= 1025; across += 257)
        {
            double zero = applicator.SampleNormalized(0, vertical, across);
            double oneRing = applicator.SampleNormalized(aroundLength, vertical, across);
            double negativeRing = applicator.SampleNormalized(-aroundLength, vertical, across);
            double beforeSeam = applicator.SampleNormalized(aroundLength - 1, vertical, across);
            double normalizedBeforeSeam = applicator.SampleNormalized(-1, vertical, across);
            if (zero != oneRing || zero != negativeRing || beforeSeam != normalizedBeforeSeam)
                throw new InvalidOperationException("wall-noise ring periodicity failed");
            double seamStep = Math.Abs(zero - beforeSeam);
            maximumSeamStep = Math.Max(maximumSeamStep, seamStep);
            if (seamStep > 0.02)
                throw new InvalidOperationException("partial final wall-noise lattice introduced a ring seam");
        }
        return (minimum, maximum, maximumSeamStep);
    }

    private static void ValidateMaskAndTopologyProtection(
        CaveWallNoiseApplicator applicator,
        CaveWallNoiseProfile profile)
    {
        CaveWallNoiseResult wall = applicator.Apply(37, -259, 521, 0.0, profile);
        if (wall.TopologyDensityMeters != 0.0
            || wall.InfluenceMask != 1.0
            || wall.AppliedDisplacementMeters != wall.NormalizedNoise * profile.MaximumDisplacementMeters
            || wall.FinalDensityMeters != wall.TopologyDensityMeters + wall.AppliedDisplacementMeters)
        {
            throw new InvalidOperationException("topology wall did not receive the full bounded noise mask");
        }

        CaveWallNoiseResult solidFade = applicator.Apply(37, -259, 521, -8.0, profile);
        CaveWallNoiseResult openFade = applicator.Apply(37, -259, 521, 2.0, profile);
        if (solidFade.InfluenceMask != 0.5 || openFade.InfluenceMask != 0.5)
            throw new InvalidOperationException("wall mask did not fade independently toward solid and open guards");

        foreach (double density in new[] { 4.0, 5.0, 64.0, double.MaxValue })
        {
            CaveWallNoiseResult result = applicator.Apply(91, -777, -333, density, profile);
            AssertUnchanged(result, "protected open core");
        }
        foreach (double density in new[] { -16.0, -17.0, -64.0, -double.MaxValue })
        {
            CaveWallNoiseResult result = applicator.Apply(91, -777, -333, density, profile);
            AssertUnchanged(result, "far solid band");
        }

        var disabled = profile with { MaximumDisplacementMeters = 0.0 };
        for (double density = -20.0; density <= 8.0; density += 0.5)
            AssertUnchanged(applicator.Apply(-91, -257, 333, density, disabled), "zero amplitude");

        for (long coordinate = -4096; coordinate <= 4096; coordinate += 73)
        for (double density = -15.5; density < 4.0; density += 0.5)
        {
            CaveWallNoiseResult result = applicator.Apply(
                coordinate,
                -coordinate - 1024,
                coordinate * 3,
                density,
                profile);
            if (!double.IsFinite(result.NormalizedNoise)
                || !double.IsFinite(result.InfluenceMask)
                || !double.IsFinite(result.AppliedDisplacementMeters)
                || !double.IsFinite(result.FinalDensityMeters)
                || result.NormalizedNoise < -1.0
                || result.NormalizedNoise > 1.0
                || result.InfluenceMask < 0.0
                || result.InfluenceMask > 1.0
                || Math.Abs(result.AppliedDisplacementMeters) > profile.MaximumDisplacementMeters + 1e-12
                || Math.Abs(result.AppliedDisplacementMeters)
                    > profile.MaximumDisplacementMeters * result.InfluenceMask + 1e-12)
            {
                throw new InvalidOperationException("wall-noise application exceeded its declared bounds");
            }
        }
    }

    private static void ValidateNoRemoteOpenings(
        CaveWallNoiseApplicator applicator,
        CaveWallNoiseProfile profile)
    {
        for (long around = -2048; around <= 2048; around += 61)
        for (double topologyDensity = -32.0; topologyDensity <= 0.0; topologyDensity += 0.25)
        {
            CaveWallNoiseResult result = applicator.Apply(
                around,
                around * 7 - 8192,
                -around * 3,
                topologyDensity,
                profile);
            if (topologyDensity <= 0.0
                && result.FinalDensityMeters > 0.0
                && -topologyDensity > profile.MaximumDisplacementMeters + 1e-12)
            {
                throw new InvalidOperationException("wall noise opened solid beyond its displacement cap");
            }
            if (topologyDensity <= -profile.WallInfluenceMeters
                && result.FinalDensityMeters != topologyDensity)
            {
                throw new InvalidOperationException("wall noise seeded a remote solid-space channel");
            }
        }
    }

    private static void ValidateRequestOrderIndependence(
        CaveWallNoiseApplicator applicator,
        CaveWallNoiseProfile profile)
    {
        var requests = new[]
        {
            (Around: -4097L, Vertical: -8193L, Across: -17L, Density: -3.0),
            (Around: 0L, Vertical: -4096L, Across: 0L, Density: 0.0),
            (Around: 4096L, Vertical: -257L, Across: 8193L, Density: 2.0),
            (Around: 8194L, Vertical: -1L, Across: -4097L, Density: 4.0)
        };
        var expected = new Dictionary<(long, long, long, double), CaveWallNoiseResult>();
        foreach (var request in requests)
        {
            expected.Add(
                (request.Around, request.Vertical, request.Across, request.Density),
                applicator.Apply(
                    request.Around,
                    request.Vertical,
                    request.Across,
                    request.Density,
                    profile));
        }
        Array.Reverse(requests);
        foreach (var request in requests)
        {
            CaveWallNoiseResult actual = applicator.Apply(
                request.Around,
                request.Vertical,
                request.Across,
                request.Density,
                profile);
            if (actual != expected[(request.Around, request.Vertical, request.Across, request.Density)])
                throw new InvalidOperationException("wall noise changed with reverse request order");
        }
    }

    /// <summary>
    /// Regression guard for the optimized path (zero-mask skip + per-thread lattice cache):
    /// final density, mask and displacement must match the unoptimized reference path
    /// bit for bit across cave (nonzero mask) and non-cave (zero mask) samples, including
    /// repeated queries (cache hits), ring seam wrap, and interleaved applicators.
    /// </summary>
    private static int ValidateOptimizedMatchesReference(
        CaveWallNoiseApplicator applicator,
        CaveWallNoiseApplicator otherSeed,
        CaveWallNoiseProfile profile)
    {
        double[] densities = { -40.0, -16.0, -8.0, -3.5, -0.25, 0.0, 0.5, 2.0, 3.999, 4.0, 9.0, 64.0 };
        int compared = 0;
        // A dense 37^3-style lattice (cache hits) plus a ring-seam and negative-coordinate sweep.
        for (int pass = 0; pass < 2; pass++)
        for (long z = 0; z < 37; z += 3)
        for (long y = 0; y < 37; y += 3)
        for (long x = 0; x < 37; x += 3)
        {
            long around = pass == 0 ? 4090 + x : -150 + x * 7;
            long vertical = pass == 0 ? -300 + y : 1000 - y * 11;
            long across = pass == 0 ? 77 + z : -9000 + z * 13;
            double density = densities[(int)((x + y * 3 + z * 5) % densities.Length)];
            // Touch the other applicator in between so a shared/stale cache would be exposed.
            otherSeed.Apply(around, vertical, across, density, profile);
            AssertBitIdentical(applicator, around, vertical, across, density, profile);
            AssertBitIdentical(applicator, around, vertical, across, density, profile);
            compared += 2;
        }
        foreach (double density in densities)
        foreach (long coordinate in new[] { long.MinValue, -4097L, -1L, 0L, 1L, 4096L, 4097L, long.MaxValue })
        {
            AssertBitIdentical(applicator, coordinate, coordinate / 3, -coordinate / 5, density, profile);
            compared++;
        }
        return compared;
    }

    private static void AssertBitIdentical(
        CaveWallNoiseApplicator applicator,
        long around,
        long vertical,
        long across,
        double density,
        CaveWallNoiseProfile profile)
    {
        CaveWallNoiseResult optimized = applicator.Apply(around, vertical, across, density, profile);
        CaveWallNoiseResult reference = applicator.ApplyReference(around, vertical, across, density, profile);
        bool sameNoiseWhenSampled = optimized.InfluenceMask == 0.0
            ? optimized.NormalizedNoise == 0.0
            : BitConverter.DoubleToInt64Bits(optimized.NormalizedNoise) == BitConverter.DoubleToInt64Bits(reference.NormalizedNoise);
        if (BitConverter.DoubleToInt64Bits(optimized.FinalDensityMeters) != BitConverter.DoubleToInt64Bits(reference.FinalDensityMeters)
            || BitConverter.DoubleToInt64Bits(optimized.InfluenceMask) != BitConverter.DoubleToInt64Bits(reference.InfluenceMask)
            || optimized.AppliedDisplacementMeters != reference.AppliedDisplacementMeters
            || optimized.TopologyDensityMeters != reference.TopologyDensityMeters
            || !sameNoiseWhenSampled)
        {
            throw new InvalidOperationException(
                $"optimized wall noise diverged from reference at {around}/{vertical}/{across} density={density}: optimized={optimized}, reference={reference}");
        }
    }

    private static void AssertUnchanged(CaveWallNoiseResult result, string context)
    {
        if (result.InfluenceMask != 0.0
            || result.AppliedDisplacementMeters != 0.0
            || result.FinalDensityMeters != result.TopologyDensityMeters)
        {
            throw new InvalidOperationException($"wall noise changed {context}");
        }
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"expected {typeof(TException).Name}");
    }
}
