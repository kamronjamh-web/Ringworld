using System;

namespace RingworldTechDemo.Weather;

/// <summary>Surface class a rain column lands on. Foliage is soil under forest or grassland; Metal is a POI, building or roof.</summary>
public enum RainMaterial { Soil = 0, Foliage = 1, Hard = 2, Water = 3, Metal = 4 }

/// <summary>Near one-shot families; the index is the library order.</summary>
public enum RainShotKind { Plip = 0, LeafDrip = 1, Tick = 2, RoofPlink = 3, CaveDrip = 4, EavesDribble = 5, FatPlip = 6, HailHit = 7 }

/// <summary>Where the listener is relative to cover; drives bed gain, filters, roof voices, thunder muffling and wind.</summary>
public enum RainShelterClass { Open = 0, Roof = 1, Indoors = 2, Cave = 3, DeepCave = 4 }

/// <summary>Per-class mix targets (docs/storm-revamp-s5-rain-audio-design.md, shelter classes).</summary>
public readonly record struct RainShelterParams(float BedGain, float BedCutoffHz, float RoofGain, float ThunderDb, float ThunderCutoffHz, float WindGain)
{
    /// <summary>The surface field keeps a milder filter than the bed: the direct path through an opening stays clear (geometric mean with open air).</summary>
    public float FieldCutoffHz => MathF.Sqrt(BedCutoffHz * RainSoundField.OpenCutoffHz);
}

/// <summary>One probed column: how much rain lands near listener height there, and on what.</summary>
public readonly record struct RainColumnProbe(bool Found, float NaturalTop, float Top, RainMaterial Material, bool Constructed);

/// <summary>Terrain-agnostic source of rain exposure. The terrain implementation exists today; an occlusion-map adapter plugs in later.</summary>
public interface IRainExposureProvider
{
    /// <summary>Landing surface of an integer ring column. Counts against the caller's per-frame query budget.</summary>
    bool TryProbeColumn(long around, int across, out RainColumnProbe probe);
    /// <summary>True when solid matter (terrain or constructed) occupies this ring-space point; used for head-height wall tests.</summary>
    bool IsSolidAt(long around, double vertical, int across);
}

/// <summary>Pure, allocation-free math for the enveloping rain sound field: sector exposure, material mixes, shelter classification, slews and schedulers.</summary>
public static class RainSoundField
{
    public const int SectorCount = 4;
    public const int MaterialCount = 5;
    public const int ShotKindCount = 8;
    /// <summary>Time constant of the drip-off after rain stops; wetness rises faster than it falls.</summary>
    public const float AftermathTauSeconds = 90f;
    public const float WetnessRiseTauSeconds = 10f;
    public const int ColumnsPerSector = 6;
    public const int ColumnCount = SectorCount * ColumnsPerSector;
    public const float OpenCutoffHz = 18000f;
    public const float TeleportMeters = 96f;
    public const float MaxGainRatePerSecond = 0.8f;
    public const float GainTauSeconds = 0.9f;
    public const float MinGainRatePerSecond = 0.15f;
    public const float MaxCutoffOctavesPerSecond = 2.5f;
    public const float MaterialMinimumWeight = 0.01f;
    public const float SideOpenThreshold = 0.35f;
    public const float ClassHysteresis = 0.1f;
    public const float ClassDwellSeconds = 0.4f;
    public const float DeepCoverMeters = 24f;
    public static readonly float[] ColumnRadii = { 3f, 7f, 14f };
    public const float ColumnAngleOffsetDegrees = 22f;

    /// <summary>Ring-frame angle of a sector axis, measured in the (around, across) plane: +around, +across, -around, -across.</summary>
    public static float SectorAngle(int sector) => sector * MathF.PI * 0.5f;

    /// <summary>Cosine lobe, zero for directions more than 90 degrees away from the sector axis.</summary>
    public static float Lobe(float sectorAngle, float sourceAngle) => MathF.Max(0f, MathF.Cos(sectorAngle - sourceAngle));

    public static RainShelterParams Params(RainShelterClass shelter) => shelter switch
    {
        RainShelterClass.Roof => new(.55f, 6000f, 1f, -2f, 10000f, .8f),
        RainShelterClass.Indoors => new(.15f, 1400f, .7f, -8f, 1500f, .25f),
        RainShelterClass.Cave => new(.2f, 1100f, 0f, -10f, 900f, .3f),
        RainShelterClass.DeepCave => new(0f, 1100f, 0f, -16f, 600f, 0f),
        _ => new(1f, OpenCutoffHz, 0f, 0f, OpenCutoffHz, 1f)
    };

    /// <summary>Sector index and ring-frame offset (around, across) in metres of one of the 24 exposure columns.</summary>
    public static void ColumnGeometry(int index, out int sector, out float aroundMeters, out float acrossMeters)
    {
        sector = index / ColumnsPerSector;
        int within = index % ColumnsPerSector;
        float radius = ColumnRadii[within / 2];
        float angle = SectorAngle(sector) + (within % 2 == 0 ? -1f : 1f) * ColumnAngleOffsetDegrees * MathF.PI / 180f;
        aroundMeters = MathF.Cos(angle) * radius;
        acrossMeters = MathF.Sin(angle) * radius;
    }

    /// <summary>Distance weight of a column: near columns matter more.</summary>
    public static float ColumnWeight(int index) => 1f / (1f + ColumnRadii[index % ColumnsPerSector / 2] / 6f);

    /// <summary>How much rain lands at a surface this far above (+) or below (-) the listener's head: full from 6 m below to 1 m above.</summary>
    public static float LandedNearListener(float surfaceMinusListener)
    {
        if (!float.IsFinite(surfaceMinusListener)) return 0f;
        if (surfaceMinusListener > 5f) return 0f;
        if (surfaceMinusListener > 1f) return (5f - surfaceMinusListener) / 4f;
        if (surfaceMinusListener >= -6f) return 1f;
        if (surfaceMinusListener > -16f) return (surfaceMinusListener + 16f) / 10f;
        return 0f;
    }

    public const float WallTransmission = 0.15f;

    /// <summary>Exposure of one column before distance weighting: landing near head height, reduced behind a wall.</summary>
    public static float ColumnExposure(bool found, float surfaceMinusListener, bool wallBetween) =>
        !found ? 0f : LandedNearListener(surfaceMinusListener) * (wallBetween ? WallTransmission : 1f);

    /// <summary>Surface gains for one sector: intensity x exposure x sqrt(material fraction), so total power is constant across material mixes. Materials under 1% of the mix stay silent.</summary>
    public static void SurfaceGains(float intensity, float sectorExposure, ReadOnlySpan<float> materialWeights, Span<float> gains)
    {
        float total = 0f;
        for (int m = 0; m < MaterialCount; m++) total += SafeWeight(materialWeights[m]);
        float threshold = MaterialMinimumWeight * total;
        float kept = 0f;
        for (int m = 0; m < MaterialCount; m++) { float w = SafeWeight(materialWeights[m]); if (w >= threshold) kept += w; }
        float scale = Math.Clamp(float.IsFinite(intensity) ? intensity : 0f, 0f, 1f) * Math.Clamp(float.IsFinite(sectorExposure) ? sectorExposure : 0f, 0f, 1f);
        for (int m = 0; m < MaterialCount; m++)
        {
            float w = SafeWeight(materialWeights[m]);
            gains[m] = total > 1e-6f && w >= threshold ? scale * MathF.Sqrt(w / kept) : 0f;
        }
    }

    /// <summary>Surface wetness above which exposure and shelter probing keep running after the rain stops, so eave drips under a roof continue through the aftermath.</summary>
    public const float PostRainProbeWetness = 0.05f;

    /// <summary>True while the rain field must keep probing terrain: rain (or a storm-arrival downpour) is falling, or the post-rain wetness is still above <see cref="PostRainProbeWetness"/>. Snow and an underground suspension always stop it.</summary>
    public static bool ShouldProbeExposure(bool snowing, bool undergroundSuspended, float rainIntensity, float arrivalFat, float wetness)
        => !snowing && !undergroundSuspended && (rainIntensity > .0001f || arrivalFat > .02f || wetness > PostRainProbeWetness);

    /// <summary>Surface wetness: one-pole toward the rain intensity, fast while rising (tau 10 s) and slow while falling (tau 90 s).</summary>
    public static float UpdateWetness(float wetness, float intensity, double dt)
    {
        if (!float.IsFinite(wetness)) wetness = 0f;
        wetness = Math.Clamp(wetness, 0f, 1f);
        float target = Math.Clamp(float.IsFinite(intensity) ? intensity : 0f, 0f, 1f);
        if (!double.IsFinite(dt) || dt <= 0.0) return wetness;
        double tau = target > wetness ? WetnessRiseTauSeconds : AftermathTauSeconds;
        float next = wetness + (target - wetness) * (float)(1.0 - Math.Exp(-dt / tau));
        return Math.Clamp(next, 0f, 1f);
    }

    /// <summary>
    /// Events per second of each one-shot kind. <paramref name="exposedMaterial"/> is the mean over the sectors of exposure x material fractions
    /// (sum at most one). Deep caves are silent; every rate is finite and non-negative.
    /// </summary>
    public static void ShotRates(float intensity, float wetness, RainShelterClass shelter, ReadOnlySpan<float> exposedMaterial, Span<float> rates, float fatDrop = 0f, float hail = 0f)
    {
        rates.Clear();
        if (shelter == RainShelterClass.DeepCave) return;
        intensity = Math.Clamp(float.IsFinite(intensity) ? intensity : 0f, 0f, 1f);
        wetness = Math.Clamp(float.IsFinite(wetness) ? wetness : 0f, 0f, 1f);
        float baseRate = RainShotScheduler.RateFor(intensity);
        float after = MathF.Max(0f, wetness - intensity);
        bool roofish = shelter == RainShelterClass.Roof || shelter == RainShelterClass.Indoors;
        float sum = 0f;
        for (int m = 0; m < MaterialCount; m++) sum += SafeWeight(exposedMaterial[m]);
        float soil = SafeWeight(exposedMaterial[(int)RainMaterial.Soil]), foliage = SafeWeight(exposedMaterial[(int)RainMaterial.Foliage]);
        float hard = SafeWeight(exposedMaterial[(int)RainMaterial.Hard]), water = SafeWeight(exposedMaterial[(int)RainMaterial.Water]);
        float metal = SafeWeight(exposedMaterial[(int)RainMaterial.Metal]);
        float foliageShare = foliage / MathF.Max(1e-4f, sum);
        rates[(int)RainShotKind.Plip] = baseRate * (water + 0.25f * soil);
        rates[(int)RainShotKind.LeafDrip] = baseRate * foliage + 1.5f * after * foliageShare;
        rates[(int)RainShotKind.Tick] = baseRate * (hard + metal);
        rates[(int)RainShotKind.RoofPlink] = roofish ? 2f * intensity * Params(shelter).RoofGain : 0f;
        rates[(int)RainShotKind.EavesDribble] = roofish ? 0.35f * wetness : 0f;
        rates[(int)RainShotKind.CaveDrip] = shelter == RainShelterClass.Cave ? 0.6f * MathF.Max(intensity, wetness) : 0f;
        // Storm arrival (S3): the sparse big drops of the skirt, audible only before the rain tier covers them, and hail impacts (louder and denser on a roof).
        fatDrop = Math.Clamp(float.IsFinite(fatDrop) ? fatDrop : 0f, 0f, 1f);
        hail = Math.Clamp(float.IsFinite(hail) ? hail : 0f, 0f, 1f);
        rates[(int)RainShotKind.FatPlip] = shelter == RainShelterClass.Open ? FatPlipRatePerSecond * fatDrop * MathF.Min(1f, sum) * (1f - HeavyTier(intensity)) : 0f;
        rates[(int)RainShotKind.HailHit] = roofish ? HailRoofRatePerSecond * hail * Params(shelter).RoofGain : shelter == RainShelterClass.Open ? HailRatePerSecond * hail * MathF.Min(1f, sum) : 0f;
        for (int k = 0; k < ShotKindCount; k++) if (!float.IsFinite(rates[k]) || rates[k] < 0f) rates[k] = 0f;
    }

    // ---- storm arrival audio (S3 WO-C3) ----
    public const float FatPlipRatePerSecond = 3f, HailRatePerSecond = 11f, HailRoofRatePerSecond = 9f;
    /// <summary>The heavy rain tier (the bed's light/heavy crossfade): 0 below intensity 0.35, 1 above 0.85.</summary>
    public static float HeavyTier(float intensity)
    {
        float t = Math.Clamp(((float.IsFinite(intensity) ? Math.Clamp(intensity, 0f, 1f) : 0f) - 0.35f) / 0.5f, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
    /// <summary>Wind speaker ducking by the rain tier: heavy rain takes up to 25 percent off the wind bed.</summary>
    public static float WindTierDuck(float intensity) => 1f - 0.25f * HeavyTier(intensity);
    /// <summary>Rain beds and surface field ducking under hail (the hail bed owns the high end) and the gust roar (the light bed only).</summary>
    public static float HailRainDuck(float hail) => 1f - 0.4f * Math.Clamp(float.IsFinite(hail) ? hail : 0f, 0f, 1f);
    public static float GustLightBedDuck(float gust) => 1f - 0.3f * Math.Clamp(float.IsFinite(gust) ? gust : 0f, 0f, 1f);
    /// <summary>
    /// Gust front roar gain per sector: the swell (smoothstep of the weight) with a cosine lobe toward the storm, so the loudest speaker faces it and the
    /// others keep 30 percent. Zero weight is silence.
    /// </summary>
    public static void GustRoarSectorGains(float gustFront, float stormAngle, Span<float> gains)
    {
        float g = Math.Clamp(float.IsFinite(gustFront) ? gustFront : 0f, 0f, 1f);
        g = g * g * (3f - 2f * g);
        if (!float.IsFinite(stormAngle)) stormAngle = 0f;
        for (int s = 0; s < SectorCount; s++) gains[s] = g * (0.3f + 0.7f * Lobe(SectorAngle(s), stormAngle));
    }
    /// <summary>Hail bed gain per sector: intensity times the sector's rain exposure; under a roof the impacts own the roof, so the roof gain stands in for the exposure.</summary>
    public static void HailSectorGains(float hail, ReadOnlySpan<float> sectorExposure, float roofGain, bool roofish, Span<float> gains)
    {
        float h = Math.Clamp(float.IsFinite(hail) ? hail : 0f, 0f, 1f);
        for (int s = 0; s < SectorCount; s++)
        {
            float exposure = s < sectorExposure.Length && float.IsFinite(sectorExposure[s]) ? Math.Clamp(sectorExposure[s], 0f, 1f) : 0f;
            gains[s] = h * MathF.Max(exposure, roofish ? Math.Clamp(roofGain, 0f, 1f) : 0f);
        }
    }
    /// <summary>Leaf skitter gain: the gust front weight times the biome's leaf share (forest 1, grassland 0.6, else 0).</summary>
    public static float LeafSkitterGain(float gustFront, float leafShare)
        => Math.Clamp(float.IsFinite(gustFront) ? gustFront : 0f, 0f, 1f) * Math.Clamp(float.IsFinite(leafShare) ? leafShare : 0f, 0f, 1f);

    private static float SafeWeight(float value) => float.IsFinite(value) && value > 0f ? value : 0f;

    /// <summary>Wind gain per sector: W(1-a) + a*lobe toward the upwind direction, a = 0.5 * wind/16. Zero wind gives zero.</summary>
    public static void WindSectorGains(float windMetersPerSecond, float windTarget01, float upwindAngle, Span<float> gains)
    {
        float wind = float.IsFinite(windMetersPerSecond) ? MathF.Max(0f, windMetersPerSecond) : 0f;
        float w = Math.Clamp(float.IsFinite(windTarget01) ? windTarget01 : 0f, 0f, 1f);
        float a = 0.5f * Math.Clamp(wind / 16f, 0f, 1f);
        if (!float.IsFinite(upwindAngle)) upwindAngle = 0f;
        for (int s = 0; s < SectorCount; s++) gains[s] = w * (1f - a) + a * Lobe(SectorAngle(s), upwindAngle);
    }

    /// <summary>Angle (ring frame) the wind blows from, given its (around, across) velocity.</summary>
    public static float UpwindAngle(float windAround, float windAcross) =>
        (float.IsFinite(windAround) && float.IsFinite(windAcross) && (windAround != 0f || windAcross != 0f)) ? MathF.Atan2(-windAcross, -windAround) : 0f;

    /// <summary>Seeded slow gust multiplier in [1 - depth, 1], per sector, 0.08 to 0.25 Hz.</summary>
    public static float GustMultiplier(int seed, int sector, double seconds, float depth = 0.18f)
    {
        uint h = Hash((uint)seed * 2654435761u + (uint)(sector + 1) * 40503u);
        float frequency = 0.08f + 0.17f * (h & 0xFFFF) / 65535f;
        float phase = (h >> 16) / 65535f * MathF.Tau;
        float wave = 0.5f + 0.5f * MathF.Sin((float)(seconds * frequency * Math.Tau) + phase);
        return 1f - Math.Clamp(depth, 0f, 1f) * wave;
    }

    /// <summary>
    /// Folds distant storm probes into sector gains: per sector the strongest probe weighted by the cosine lobe.
    /// The loudest sector equals <paramref name="distantTotal"/> (the existing distant target), so the total level is unchanged.
    /// </summary>
    public static void DistantSectorGains(ReadOnlySpan<float> probeAngles, ReadOnlySpan<float> probeStrengths, float distantTotal, Span<float> gains)
    {
        Span<float> raw = stackalloc float[SectorCount];
        float peak = 0f;
        int count = Math.Min(probeAngles.Length, probeStrengths.Length);
        for (int s = 0; s < SectorCount; s++)
        {
            float best = 0f;
            for (int p = 0; p < count; p++)
            {
                float strength = float.IsFinite(probeStrengths[p]) ? Math.Clamp(probeStrengths[p], 0f, 1f) : 0f;
                best = MathF.Max(best, strength * Lobe(SectorAngle(s), probeAngles[p]));
            }
            raw[s] = best;
            peak = MathF.Max(peak, best);
        }
        float total = Math.Clamp(float.IsFinite(distantTotal) ? distantTotal : 0f, 0f, 1f);
        for (int s = 0; s < SectorCount; s++) gains[s] = peak > 1e-4f ? total * raw[s] / peak : 0f;
    }

    /// <summary>One-pole approach to a target with a hard rate cap, both in linear gain per second.</summary>
    public static float SlewGain(float current, float target, double dt, float tauSeconds = GainTauSeconds, float maxRatePerSecond = MaxGainRatePerSecond)
    {
        if (!float.IsFinite(current)) current = 0f;
        if (!float.IsFinite(target)) target = 0f;
        if (!double.IsFinite(dt) || dt <= 0.0) return current;
        float difference = target - current;
        float step = difference * (float)(1.0 - Math.Exp(-dt / Math.Max(0.02, tauSeconds)));
        float cap = Math.Max(0f, maxRatePerSecond) * (float)dt;
        // A one-pole tail never arrives; a small linear floor lets a fading layer reach true silence (and a rising one its target).
        float floor = Math.Min(MinGainRatePerSecond * (float)dt, cap);
        if (MathF.Abs(step) < floor) step = MathF.CopySign(floor, difference);
        step = Math.Clamp(step, -cap, cap);
        if (MathF.Abs(step) >= MathF.Abs(difference)) return target;
        return current + step;
    }

    /// <summary>Moves a cutoff toward a target in log2 space at no more than <paramref name="maxOctavesPerSecond"/>.</summary>
    public static float SlewCutoffHz(float current, float target, double dt, float maxOctavesPerSecond = MaxCutoffOctavesPerSecond)
    {
        if (!float.IsFinite(target) || target <= 0f) target = OpenCutoffHz;
        if (!float.IsFinite(current) || current <= 0f) return target;
        if (!double.IsFinite(dt) || dt <= 0.0) return current;
        float octaves = MathF.Log2(target / current);
        float cap = maxOctavesPerSecond * (float)dt;
        if (MathF.Abs(octaves) <= cap) return target;
        return current * MathF.Pow(2f, MathF.CopySign(cap, octaves));
    }

    /// <summary>Linear gain to dB with a floor; zero gain is -80 dB.</summary>
    public static float GainToDb(float gain) => gain <= 1e-4f ? -80f : 20f * MathF.Log10(gain);

    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
        return x;
    }
}

/// <summary>Exposure of 24 columns around the listener, aggregated into four ring-fixed sectors with bounded per-update change.</summary>
public sealed class RainExposureField
{
    private readonly float[] _contribution = new float[RainSoundField.ColumnCount];
    private readonly RainMaterial[] _material = new RainMaterial[RainSoundField.ColumnCount];
    private readonly bool[] _known = new bool[RainSoundField.ColumnCount];
    private readonly float[] _sectorWeight = new float[RainSoundField.SectorCount];
    private int _knownCount;

    /// <summary>Largest sector exposure change one column update may cause.</summary>
    public const float MaxSectorStep = 1f / 6f;

    public RainExposureField()
    {
        for (int i = 0; i < RainSoundField.ColumnCount; i++) _sectorWeight[i / RainSoundField.ColumnsPerSector] += RainSoundField.ColumnWeight(i);
    }

    public bool FullySeeded => _knownCount == RainSoundField.ColumnCount;

    /// <summary>Forget everything (teleport or world change): sectors fall silent until the columns are probed again.</summary>
    public void Invalidate()
    {
        Array.Clear(_contribution);
        Array.Clear(_known);
        Array.Clear(_material);
        _knownCount = 0;
    }

    /// <summary>Records a probe. The first reading of a column is taken directly; later readings move the sector by at most 1/6.</summary>
    public void ApplyColumn(int index, float exposure, RainMaterial material)
    {
        if ((uint)index >= RainSoundField.ColumnCount) return;
        float target = Math.Clamp(float.IsFinite(exposure) ? exposure : 0f, 0f, 1f) * RainSoundField.ColumnWeight(index);
        if (!_known[index]) { _known[index] = true; _knownCount++; _contribution[index] = target; }
        else
        {
            float cap = _sectorWeight[index / RainSoundField.ColumnsPerSector] * MaxSectorStep;
            _contribution[index] += Math.Clamp(target - _contribution[index], -cap, cap);
        }
        _material[index] = material;
    }

    /// <summary>Sector exposure in 0..1 (contribution over the sector's full weight).</summary>
    public float SectorExposure(int sector)
    {
        float sum = 0f;
        for (int i = sector * RainSoundField.ColumnsPerSector, end = i + RainSoundField.ColumnsPerSector; i < end; i++) sum += _contribution[i];
        return Math.Clamp(sum / _sectorWeight[sector], 0f, 1f);
    }

    /// <summary>Mean exposure over all four sectors; the "sides open" measure of the shelter classifier.</summary>
    public float MeanExposure()
    {
        float sum = 0f;
        for (int s = 0; s < RainSoundField.SectorCount; s++) sum += SectorExposure(s);
        return sum / RainSoundField.SectorCount;
    }

    /// <summary>Material fractions of a sector (sum to one when anything lands there, else all zero).</summary>
    public void SectorMaterials(int sector, Span<float> fractions)
    {
        fractions.Clear();
        float total = 0f;
        for (int i = sector * RainSoundField.ColumnsPerSector, end = i + RainSoundField.ColumnsPerSector; i < end; i++)
        {
            fractions[(int)_material[i]] += _contribution[i];
            total += _contribution[i];
        }
        if (total <= 1e-5f) { fractions.Clear(); return; }
        for (int m = 0; m < RainSoundField.MaterialCount; m++) fractions[m] /= total;
    }
}

/// <summary>What the classifier needs each update.</summary>
public readonly record struct RainShelterInput(bool Suspended, bool HasCover, bool NaturalCover, float CoverDepthMeters, float SideExposure);

/// <summary>Shelter class with hysteresis on the side-open decision and a dwell time on every class change.</summary>
public sealed class RainShelterClassifier
{
    private bool _sidesOpen = true;
    private RainShelterClass _candidate;
    private double _candidateTime;

    public RainShelterClass Current { get; private set; }
    public bool SidesOpen => _sidesOpen;

    public void Reset(RainShelterClass value = RainShelterClass.Open)
    {
        Current = _candidate = value;
        _candidateTime = 0;
        _sidesOpen = true;
    }

    /// <summary>The instantaneous (undwelled) class for an input; sides-open hysteresis is applied inside <see cref="Update"/>.</summary>
    public static RainShelterClass Raw(in RainShelterInput input, bool sidesOpen)
    {
        if (input.Suspended || (input.HasCover && input.NaturalCover && input.CoverDepthMeters > RainSoundField.DeepCoverMeters)) return RainShelterClass.DeepCave;
        if (!input.HasCover) return RainShelterClass.Open;
        if (input.NaturalCover) return RainShelterClass.Cave;
        return sidesOpen ? RainShelterClass.Roof : RainShelterClass.Indoors;
    }

    public RainShelterClass Update(double dt, in RainShelterInput input)
    {
        float side = float.IsFinite(input.SideExposure) ? input.SideExposure : 0f;
        if (_sidesOpen && side <= RainSoundField.SideOpenThreshold - RainSoundField.ClassHysteresis) _sidesOpen = false;
        else if (!_sidesOpen && side >= RainSoundField.SideOpenThreshold + RainSoundField.ClassHysteresis) _sidesOpen = true;
        RainShelterClass raw = Raw(input, _sidesOpen);
        if (raw == Current) { _candidate = Current; _candidateTime = 0; return Current; }
        if (raw != _candidate) { _candidate = raw; _candidateTime = 0; }
        _candidateTime += double.IsFinite(dt) ? Math.Max(0.0, dt) : 0.0;
        if (_candidateTime >= RainSoundField.ClassDwellSeconds) { Current = _candidate; _candidateTime = 0; }
        return Current;
    }
}

/// <summary>Deterministic Poisson scheduler for near rain one-shots (WO7 will attach the voices). Silent when the library is empty.</summary>
public sealed class RainShotScheduler
{
    public const int MaxVoices = 6;
    public const double MinimumSpacingSeconds = 0.06;
    public const float RatePerIntensity = 4f;
    private uint _state;
    // Remaining unit-exponential mass until the next event. Consuming it at rate x dt makes this a time-varying Poisson process,
    // so a rate change (rain ramping up from zero) takes effect at once instead of waiting out an interval drawn at the old rate.
    private double _untilNext = double.NaN;
    private double _sinceLast = 1.0;
    private int _lastIndex = -1;

    public RainShotScheduler(int seed) => _state = (uint)seed * 747796405u + 2891336453u;

    /// <summary>Events per second for a rain intensity.</summary>
    public static float RateFor(float intensity) => Math.Clamp(float.IsFinite(intensity) ? intensity : 0f, 0f, 1f) * RatePerIntensity;

    /// <summary>Advances by dt and returns how many shots start now (0 or 1 per call: spacing and the voice cap forbid bursts).</summary>
    public int Advance(double dt, float ratePerSecond, int activeVoices, int libraryCount)
    {
        if (!double.IsFinite(dt) || dt <= 0.0) return 0;
        _sinceLast += dt;
        if (libraryCount <= 0 || !(ratePerSecond > 0f)) { _untilNext = double.NaN; return 0; }
        if (double.IsNaN(_untilNext)) _untilNext = UnitInterval();
        _untilNext -= dt * ratePerSecond;
        if (_untilNext > 0.0) return 0;
        if (activeVoices >= MaxVoices || _sinceLast < MinimumSpacingSeconds) { _untilNext = 0.0; return 0; }
        _untilNext = UnitInterval();
        _sinceLast = 0.0;
        return 1;
    }

    /// <summary>A library index that never repeats the previous pick when there is a choice.</summary>
    public int PickIndex(int count)
    {
        if (count <= 0) return -1;
        if (count == 1) return _lastIndex = 0;
        int index = (int)(NextUnit() * (count - 1));
        if (index >= _lastIndex && _lastIndex >= 0) index++;
        return _lastIndex = Math.Min(index, count - 1);
    }

    /// <summary>Overload with a caller-owned last index, so each library keeps its own no-repeat memory.</summary>
    public int PickIndex(int count, ref int lastIndex)
    {
        if (count <= 0) return -1;
        if (count == 1) return lastIndex = 0;
        int index = (int)(NextUnit() * (count - 1));
        if (index >= lastIndex && lastIndex >= 0) index++;
        return lastIndex = Math.Min(index, count - 1);
    }

    /// <summary>Weighted pick of a shot kind; -1 when every rate is zero.</summary>
    public int PickKind(ReadOnlySpan<float> rates)
    {
        float total = 0f;
        for (int k = 0; k < rates.Length; k++) if (float.IsFinite(rates[k]) && rates[k] > 0f) total += rates[k];
        if (!(total > 0f)) return -1;
        float pick = NextUnit() * total;
        int last = -1;
        for (int k = 0; k < rates.Length; k++)
        {
            if (!float.IsFinite(rates[k]) || rates[k] <= 0f) continue;
            last = k;
            pick -= rates[k];
            if (pick < 0f) return k;
        }
        return last;
    }

    public float NextPitchScale() => 1f + (NextUnit() * 2f - 1f) * 0.08f;
    public float NextGainDb() => (NextUnit() * 2f - 1f) * 3f;
    public float NextUnit()
    {
        _state ^= _state << 13; _state ^= _state >> 17; _state ^= _state << 5;
        return (_state & 0xFFFFFF) / 16777216f;
    }

    private double UnitInterval() => -Math.Log(1.0 - NextUnit());
}
