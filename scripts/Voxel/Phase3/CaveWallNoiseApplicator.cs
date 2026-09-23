using System;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>
/// Per-feature wall-noise limits supplied after topology has produced a density.
/// Positive density is open cave space and negative density is solid.
/// </summary>
public readonly record struct CaveWallNoiseProfile(
    double MaximumDisplacementMeters,
    double ProtectedOpenCoreMeters,
    double WallInfluenceMeters);

/// <summary>Complete immutable result of one post-topology wall-noise application.</summary>
public readonly record struct CaveWallNoiseResult(
    double TopologyDensityMeters,
    double NormalizedNoise,
    double InfluenceMask,
    double AppliedDisplacementMeters,
    double FinalDensityMeters);

/// <summary>
/// Stateless deterministic wall texture applied only after designed topology.
/// It preserves topology density at and beyond a declared open core and cannot
/// affect solid space beyond a finite wall band.
/// </summary>
public sealed class CaveWallNoiseApplicator
{
    public const int Version = 1;
    public const int PeriodicLatticeVersion = 1;
    public const int WallMaskVersion = 1;
    public const int OctaveCount = 3;
    public const long BaseWavelengthMeters = 128;
    public const double MaximumAllowedDisplacementMeters = 64.0;
    public const double MaximumAllowedProtectedOpenCoreMeters = 8192.0;
    public const double MaximumAllowedWallInfluenceMeters = 8192.0;

    private const ulong IdentitySalt = 0x57414C4C4E4F4953UL;
    private const ulong LatticeSalt = 0x57414C4C4C415431UL;
    private static readonly int[] OctaveWeights = { 4, 2, 1 };

    public CaveGenerationContract Contract { get; }
    public long AroundLengthMeters { get; }
    public ulong Identity { get; }

    public CaveWallNoiseApplicator(CaveGenerationContract contract, long aroundLengthMeters)
    {
        if (contract.Version != CaveGenerationContract.CurrentVersion
            || contract.Identity != CaveGenerationContract.CalculateIdentity(
                contract.TerrainSeed,
                contract.Version))
        {
            throw new ArgumentException(
                "Cave wall noise requires a valid cave-generation contract.",
                nameof(contract));
        }
        if (aroundLengthMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(aroundLengthMeters));

        Contract = contract;
        AroundLengthMeters = aroundLengthMeters;
        ulong identity = MixIdentity(contract.Identity, IdentitySalt);
        identity = MixIdentity(identity, aroundLengthMeters);
        identity = MixIdentity(identity, Version);
        identity = MixIdentity(identity, PeriodicLatticeVersion);
        identity = MixIdentity(identity, WallMaskVersion);
        identity = MixIdentity(identity, OctaveCount);
        identity = MixIdentity(identity, BaseWavelengthMeters);
        identity = MixIdentity(identity, BitConverter.DoubleToInt64Bits(MaximumAllowedDisplacementMeters));
        identity = MixIdentity(identity, BitConverter.DoubleToInt64Bits(MaximumAllowedProtectedOpenCoreMeters));
        identity = MixIdentity(identity, BitConverter.DoubleToInt64Bits(MaximumAllowedWallInfluenceMeters));
        foreach (int weight in OctaveWeights)
            identity = MixIdentity(identity, weight);
        Identity = Avalanche(identity);
    }

    /// <summary>
    /// Applies bounded noise to an already-resolved topology density. Any connected
    /// topology core whose density is at least ProtectedOpenCoreMeters is unchanged.
    /// </summary>
    public CaveWallNoiseResult Apply(
        long around,
        long vertical,
        long across,
        double topologyDensityMeters,
        CaveWallNoiseProfile profile)
    {
        Validate(topologyDensityMeters, profile);
        double normalizedNoise = SampleNormalized(around, vertical, across);
        double influenceMask = ResolveInfluenceMask(topologyDensityMeters, profile);
        double appliedDisplacement = normalizedNoise
            * profile.MaximumDisplacementMeters
            * influenceMask;
        appliedDisplacement = Math.Clamp(
            appliedDisplacement,
            -profile.MaximumDisplacementMeters,
            profile.MaximumDisplacementMeters);
        double finalDensity = topologyDensityMeters + appliedDisplacement;
        return new CaveWallNoiseResult(
            topologyDensityMeters,
            normalizedNoise,
            influenceMask,
            appliedDisplacement,
            finalDensity);
    }

    /// <summary>Samples a ring-periodic immutable three-octave value-noise field.</summary>
    public double SampleNormalized(long around, long vertical, long across)
    {
        long canonicalAround = Mod(around, AroundLengthMeters);
        double weightedNoise = 0.0;
        int totalWeight = 0;
        for (int octave = 0; octave < OctaveCount; octave++)
        {
            long wavelength = BaseWavelengthMeters >> octave;
            int weight = OctaveWeights[octave];
            weightedNoise += SampleOctave(
                canonicalAround,
                vertical,
                across,
                wavelength,
                octave) * weight;
            totalWeight += weight;
        }
        return Math.Clamp(weightedNoise / totalWeight, -1.0, 1.0);
    }

    private double SampleOctave(
        long canonicalAround,
        long vertical,
        long across,
        long wavelength,
        int octave)
    {
        long aroundPointCount = Math.Max(1, AroundLengthMeters / wavelength);
        long aroundIndex = Math.Min(canonicalAround / wavelength, aroundPointCount - 1);
        long aroundOrigin = checked(aroundIndex * wavelength);
        long aroundSegmentLength = aroundIndex == aroundPointCount - 1
            ? AroundLengthMeters - aroundOrigin
            : wavelength;
        long nextAroundIndex = aroundIndex == aroundPointCount - 1 ? 0 : aroundIndex + 1;
        double aroundFraction = (canonicalAround - aroundOrigin) / (double)aroundSegmentLength;

        long verticalIndex = FloorDiv(vertical, wavelength);
        long acrossIndex = FloorDiv(across, wavelength);
        double verticalFraction = Mod(vertical, wavelength) / (double)wavelength;
        double acrossFraction = Mod(across, wavelength) / (double)wavelength;
        double aroundBlend = Fade(aroundFraction);
        double verticalBlend = Fade(verticalFraction);
        double acrossBlend = Fade(acrossFraction);

        double c000 = LatticeValue(aroundIndex, verticalIndex, acrossIndex, octave);
        double c100 = LatticeValue(nextAroundIndex, verticalIndex, acrossIndex, octave);
        double c010 = LatticeValue(aroundIndex, verticalIndex + 1, acrossIndex, octave);
        double c110 = LatticeValue(nextAroundIndex, verticalIndex + 1, acrossIndex, octave);
        double c001 = LatticeValue(aroundIndex, verticalIndex, acrossIndex + 1, octave);
        double c101 = LatticeValue(nextAroundIndex, verticalIndex, acrossIndex + 1, octave);
        double c011 = LatticeValue(aroundIndex, verticalIndex + 1, acrossIndex + 1, octave);
        double c111 = LatticeValue(nextAroundIndex, verticalIndex + 1, acrossIndex + 1, octave);

        double lowVertical = Lerp(
            Lerp(c000, c100, aroundBlend),
            Lerp(c010, c110, aroundBlend),
            verticalBlend);
        double highVertical = Lerp(
            Lerp(c001, c101, aroundBlend),
            Lerp(c011, c111, aroundBlend),
            verticalBlend);
        return Lerp(lowVertical, highVertical, acrossBlend);
    }

    private double LatticeValue(long aroundIndex, long verticalIndex, long acrossIndex, int octave)
    {
        ulong octaveSalt = MixIdentity(LatticeSalt, octave);
        ulong key = Contract.GetCoordinateSaltKey(
            aroundIndex,
            verticalIndex,
            acrossIndex,
            octaveSalt ^ Identity);
        ulong random53Bits = Avalanche(key) >> 11;
        const double inverse53BitRange = 1.0 / (1UL << 53);
        return random53Bits * inverse53BitRange * 2.0 - 1.0;
    }

    private static double ResolveInfluenceMask(
        double topologyDensityMeters,
        CaveWallNoiseProfile profile)
    {
        if (profile.MaximumDisplacementMeters == 0.0
            || topologyDensityMeters >= profile.ProtectedOpenCoreMeters
            || topologyDensityMeters <= -profile.WallInfluenceMeters)
        {
            return 0.0;
        }

        return topologyDensityMeters >= 0.0
            ? 1.0 - SmoothStep(0.0, profile.ProtectedOpenCoreMeters, topologyDensityMeters)
            : SmoothStep(-profile.WallInfluenceMeters, 0.0, topologyDensityMeters);
    }

    private static void Validate(double topologyDensityMeters, CaveWallNoiseProfile profile)
    {
        if (!double.IsFinite(topologyDensityMeters))
            throw new ArgumentOutOfRangeException(nameof(topologyDensityMeters));
        if (!double.IsFinite(profile.MaximumDisplacementMeters)
            || profile.MaximumDisplacementMeters < 0.0
            || profile.MaximumDisplacementMeters > MaximumAllowedDisplacementMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }
        if (!double.IsFinite(profile.ProtectedOpenCoreMeters)
            || profile.ProtectedOpenCoreMeters <= 0.0
            || profile.ProtectedOpenCoreMeters > MaximumAllowedProtectedOpenCoreMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }
        if (!double.IsFinite(profile.WallInfluenceMeters)
            || profile.WallInfluenceMeters <= 0.0
            || profile.WallInfluenceMeters > MaximumAllowedWallInfluenceMeters
            || profile.WallInfluenceMeters < profile.MaximumDisplacementMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }
    }

    private static double Fade(double value) => value * value * (3.0 - 2.0 * value);

    private static double SmoothStep(double low, double high, double value)
    {
        double normalized = Math.Clamp((value - low) / (high - low), 0.0, 1.0);
        return normalized * normalized * (3.0 - 2.0 * normalized);
    }

    private static double Lerp(double first, double second, double weight) =>
        first + (second - first) * weight;

    private static long FloorDiv(long value, long divisor) =>
        value / divisor - (value < 0 && value % divisor != 0 ? 1 : 0);

    private static long Mod(long value, long divisor)
    {
        long remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private static ulong MixIdentity(ulong hash, long value) =>
        MixIdentity(hash, unchecked((ulong)value));

    private static ulong MixIdentity(ulong hash, int value) =>
        MixIdentity(hash, unchecked((ulong)(long)value));

    private static ulong MixIdentity(ulong hash, ulong value)
    {
        for (int index = 0; index < 8; index++)
        {
            hash ^= (byte)(value >> (index * 8));
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static ulong Avalanche(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
}
