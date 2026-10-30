using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo;

/// <summary>
/// Megastorm weather injection (docs/megastorm-design.md WO3, sections 3.2, 4.3 and 5). The megastorm is not a natural macro cell: the model
/// (<see cref="IMegastormWeatherSource"/>, installed by the runtime node) emits four slab records (W wall, A anvil, T trailing deck, P precursor) as
/// ellipses with a 1e7 m across semi-axis, so every consumer of the weather field (macro map, storm light D, S6 layers, rain, curtains, arrival,
/// wind, audio) sees them through the same three system collections as the natural systems, with no per-consumer code:
/// <list type="bullet">
/// <item><see cref="BuildNearbyWeatherSystems"/> (CPU macro weather: <c>QueryRawWeatherAtTime</c>, the legacy formation placement),</item>
/// <item><see cref="CollectRenderWeatherSystems"/> (the camera-independent render systems: Sunshine upload, far layer build, S6 layers),</item>
/// <item><c>CollectStormGeometry</c> (storm arrival, rain history, wet world, rainbow).</item>
/// </list>
/// Records live in WORLD space at the query time; the natural source frame is world minus wind x t, so the injection stores each record's centre as
/// <c>world - wind * t</c> and the wind advection puts it back. The record's own motion is its <see cref="MegastormRecord.DriftMetersPerSecond"/>
/// (<c>dir * v</c> around, no wind term): arrival geometry carries it as the per-system drift, so the maths needs no change.
/// <para>
/// Natural-weather suppression (4.3): <see cref="MegastormSuppressionAtSource"/> evaluates S at a natural system's advected centre and
/// <see cref="TryBuildWeatherSystem"/> multiplies the lifecycle envelope by (1 - S). <see cref="EffectiveSystemEnvelope"/> gives the same
/// number to the other envelope readers (storm geometry and its rho, the render system's mature density, the leading-edge lightning rho).
/// With no source installed, or S = 0, the envelope is the lifecycle envelope bit for bit (the natural-weather fingerprint golden does not move).
/// </para>
/// <para>
/// Kind audit (section 5; <c>WeatherSystemKind.Megastorm = 6</c>, <c>CloudFormationProfile.MegastormWall = 12</c>, <c>MegastormTrailing = 13</c>). Helpers:
/// <see cref="IsFrontKind"/> now includes the Megastorm, <see cref="IsSevere"/> is kind &gt;= ExceptionalFront. Site by site:
/// <list type="table">
/// <item><term>IsFrontKind / CloudLayerTable.LowersBase</term><description>kind 6 is a front kind: FrontOnly rows lower the base under it.</description></item>
/// <item><term>RingLocalWeatherEffects.ResolveCloudBounds, RingDistantRainCurtains (naturalFront), RingSunshineCloudBridge.ResolveNaturalStormCloudAltitude</term><description>use IsFrontKind: rain under the wall falls from the storm-front base (500 m), not the fair-weather base.</description></item>
/// <item><term>ResolveFormationProfile (CPU local weather)</term><description>Megastorm records resolve to Cumulonimbus; the T record (id -30003) to Nimbostratus and P (id -30004) to Altostratus, so the CPU profile matches the render record's table row.</description></item>
/// <item><term>ResolveFormationScale (legacy multi-cell chance)</term><description>kind 6 takes the ExceptionalFront terms.</description></item>
/// <item><term>ResolveSystemFormationProfile, BuildNaturalRenderSystem's front flag, lightning scanners (<c>TryBuildWeatherSystem</c> kinds), TryGetRandomActiveStormCell, IsLightningEligible</term><description>natural systems only: kind 6 is never produced by TryBuildWeatherSystem, so the megastorm is excluded from the natural scanner and sampler by construction (its strikes come from the megastorm stream, WO10). The T record is an ExceptionalFront by kind but has a negative id and never enters those scanners either.</description></item>
/// <item><term>StormArrivalMath.KindWeight / GustKindScale / HailAt</term><description>already treat kinds &gt;= 5 as superstorm (weight 1.5, full gust scale). HailAt places generic swaths by the slab ellipse rule; megastorm hail placement and the kind-6 time-based bands are WO6.</description></item>
/// <item><term>Wind StormFactor, RingWindQuery</term><description>kind 6 falls into the default weight (0.3) until WO6 sets 1.25 and adds the wall updrafts.</description></item>
/// <item><term>RingLocalWeatherEffects.RainFog (haze onset), UpperAtmosphereSprite (chance), StormArrivalMath.KindWeight</term><description>already ">= ExceptionalFront" / ">= 5": kind 6 is severe with no change.</description></item>
/// </list>
/// </para>
/// Slab consumers (a major semi-axis of 1e7 m): audited in the WO3 report. Membership maths (<c>n</c>, influence, ownership, arrival rims) is exact for a slab;
/// the camera-independent shadow reach (<c>ResolveCameraIndependentShadowSystems</c>: distance + max(major, minor)) and the wet-world dry probe
/// (<c>max(major, minor)</c> reach) read the major semi-axis as an extent and are over-inclusive, never wrong in the visual result.
/// </summary>
public partial class RingVolumetricCloudLayer
{
    /// <summary>
    /// The megastorm seam: the model (or a wrapper owned by the runtime node) that supplies the bundle records and the suppression weight. Null (the default)
    /// is the untriggered, zero-cost state: every injection site is one null check. Set it from the runtime node on the main thread; the layer reads it from
    /// the main, physics and worker threads, so a source must be thread safe for reads (the model is a pure function of its state).
    /// </summary>
    public IMegastormWeatherSource? MegastormSource { get; set; }

    [ThreadStatic] private static List<MegastormRecord>? _megastormBundleScratch;

    /// <summary>The ellipse orientation of a slab record: pi/2, so the major (across) axis is the sine axis and q reduces to around offset / half depth. Exact sine and cosine.</summary>
    private const float MegastormSlabOrientationRadians = Mathf.Pi * 0.5f;

    /// <summary>True for the four fixed megastorm bundle ids (-30001 .. -30004).</summary>
    public static bool IsMegastormRecordId(int systemId)
        => systemId <= MegastormModel.WallRecordId && systemId >= MegastormModel.PrecursorRecordId;

    /// <summary>
    /// The formation profile of a bundle record: its explicit profile, else the table row WO3 chooses for its role (T: MegastormTrailing, P: Altostratus,
    /// W/A: MegastormWall). T and P are not sculpt bodies; their look comes from the genus row through the S6 layers.
    /// </summary>
    public static CloudFormationProfile MegastormRecordProfile(in MegastormRecord record)
        => record.Profile >= 0
            ? (CloudFormationProfile)record.Profile
            : record.Role switch
            {
                MegastormRecordRole.Trailing => CloudFormationProfile.MegastormTrailing,
                MegastormRecordRole.Precursor => CloudFormationProfile.Altostratus,
                _ => CloudFormationProfile.MegastormWall
            };

    /// <summary>Per-record drift seam: a record's world velocity (around, across) for the arrival geometry. The megastorm moves only around the ring.</summary>
    public static Vector2 MegastormRecordDrift(in MegastormRecord record) => new(record.DriftMetersPerSecond, 0.0f);

    private List<MegastormRecord>? BuildMegastormBundle(double timeSeconds, double queryAcrossMeters)
    {
        IMegastormWeatherSource? source = MegastormSource;
        if (source == null) return null;
        List<MegastormRecord> bundle = _megastormBundleScratch ??= new List<MegastormRecord>(4);
        return source.BuildBundle(timeSeconds, queryAcrossMeters, bundle) > 0 ? bundle : null;
    }

    /// <summary>True when the record's support (its half depth x <paramref name="support"/>) comes within <paramref name="reachMeters"/> of the around coordinate.</summary>
    private static bool MegastormRecordInReach(in MegastormRecord record, double aroundMeters, double reachMeters, double circumference, double support)
        => Math.Abs(ShortestAroundDelta(aroundMeters, record.CenterAroundMeters, circumference)) - record.SemiAlongMeters * support < reachMeters;

    private double WorldCircumference() => Math.Max(1.0, _grid?.GetCanonicalRingCircumferenceMeters64() ?? 1.0);

    // ---- site 1: CPU macro weather (BuildNearbyWeatherSystems) ----
    /// <summary>Appends the bundle as <see cref="WeatherSystemDefinition"/> records in the source frame (world minus wind x t), natural falloff (support 1.0).</summary>
    private void AppendMegastormSystems(double centerAroundMeters, double centerAcrossMeters, double timeSeconds, float queryRadiusMeters,
        List<WeatherSystemDefinition> systems)
    {
        List<MegastormRecord>? bundle = BuildMegastormBundle(timeSeconds, centerAcrossMeters);
        if (bundle == null) return;
        double circumference = WorldCircumference();
        Vector2 wind = WeatherWindMetersPerSecond;
        for (int i = 0; i < bundle.Count; i++)
        {
            MegastormRecord record = bundle[i];
            if (!MegastormRecordInReach(record, centerAroundMeters, Math.Max(0.0f, queryRadiusMeters), circumference, 1.0)) continue;
            double inverseMajor = 1.0 / Math.Max(1.0, record.SemiAcrossMeters * record.SemiAcrossMeters);
            double inverseMinor = 1.0 / Math.Max(1.0, record.SemiAlongMeters * record.SemiAlongMeters);
            systems.Add(new WeatherSystemDefinition(
                record.Id,
                (WeatherSystemKind)record.Kind,
                WrapAround(record.CenterAroundMeters - wind.X * timeSeconds, circumference),
                record.CenterAcrossMeters - wind.Y * timeSeconds,
                (float)record.SemiAcrossMeters,
                (float)record.SemiAlongMeters,
                MegastormSlabOrientationRadians,
                0.0f,
                1.0f,
                inverseMajor,
                inverseMinor,
                record.Density,
                record.Precipitation));
        }
    }

    // ---- site 2: camera-independent render systems (CollectRenderWeatherSystems) ----
    /// <summary>
    /// Appends the bundle as <see cref="RenderWeatherSystem"/> records. Natural-style (not static): centres are in the source frame. The record's flags
    /// choose Sculpt / LowersBase / Front; sculpt records are included out to the storm support (the table's StormSupportFactor x half depth) so the
    /// GPU ownership band is never culled before it arrives. Returns the number appended.
    /// </summary>
    private int AppendMegastormSystems(RingCanonicalPosition center, double radiusMeters, double timeSeconds, List<RenderWeatherSystem> output,
        float altitudeDatumMeters, float stormFrontBaseMeters)
    {
        List<MegastormRecord>? bundle = BuildMegastormBundle(timeSeconds, center.AcrossMeters);
        if (bundle == null) return 0;
        double circumference = WorldCircumference();
        Vector2 wind = WeatherWindMetersPerSecond;
        float sculptSupport = CloudLayerTable.Shared.StormSupportFactor;
        int appended = 0;
        for (int i = 0; i < bundle.Count; i++)
        {
            MegastormRecord record = bundle[i];
            bool sculpt = (record.Flags & MegastormRecordFlags.Sculpt) != 0;
            if (!MegastormRecordInReach(record, center.AroundMeters, radiusMeters, circumference, sculpt ? sculptSupport : 1.0)) continue;
            bool front = (record.Flags & MegastormRecordFlags.Front) != 0;
            bool lowersBase = (record.Flags & MegastormRecordFlags.LowersBase) != 0;
            WeatherSystemKind kind = (WeatherSystemKind)record.Kind;
            float stormFloor = 0.0f, stormCeiling = 0.0f;
            if (sculpt || front)
            {
                stormFloor = altitudeDatumMeters + Math.Max(100.0f, stormFrontBaseMeters);
                // Sculpt records (W/A): the strip's tower domes reach the hard cloud ceiling (the containment field's ceiling helper replaces
                // this when field WO6 lands). The non-sculpt front keeps its 7 km envelope.
                stormCeiling = sculpt
                    ? RingCloudLayers.CeilingMeters
                    : Math.Min(RingCloudLayers.CeilingMeters, altitudeDatumMeters + 7000.0f);
            }
            output.Add(new RenderWeatherSystem(
                record.Id,
                kind,
                WrapAround(record.CenterAroundMeters - wind.X * timeSeconds, circumference),
                record.CenterAcrossMeters - wind.Y * timeSeconds,
                (float)record.SemiAcrossMeters,
                (float)record.SemiAlongMeters,
                0.0f,
                1.0f,
                record.Density,
                record.Precipitation,
                stormFloor,
                stormCeiling,
                MegastormRecordProfile(record),
                (sculpt ? RenderWeatherFlagSculpt : 0u) | (front ? RenderWeatherFlagFront : 0u) | (lowersBase ? RenderWeatherFlagLowersBase : 0u),
                0.0f,
                1.0f,
                StormLifecycleMature));
            appended++;
        }
        return appended;
    }

    /// <summary>Bridge per-frame record refresh (docs/megastorm-design.md 3.6): appends the megastorm render records at <paramref name="timeSeconds"/>. Clears nothing.</summary>
    public int CollectMegastormRenderSystems(RingCanonicalPosition center, double radiusMeters, double timeSeconds, List<RenderWeatherSystem> output,
        float altitudeDatumMeters, float stormFrontBaseMeters)
        => AppendMegastormSystems(center, radiusMeters, timeSeconds, output, altitudeDatumMeters, stormFrontBaseMeters);

    // ---- site 3: storm arrival geometry (CollectStormGeometry) ----
    /// <summary>
    /// Appends the bundle as <see cref="StormSystemGeometry"/>: offset = observer minus centre (the across centre is the query point, so the across
    /// offset is 0), envelope 1 and rho 0 (formation and level are already in the record's density and precipitation), drift from the record.
    /// </summary>
    private void AppendMegastormSystems(double aroundMeters, double acrossMeters, double timeSeconds, List<StormSystemGeometry> into)
    {
        List<MegastormRecord>? bundle = BuildMegastormBundle(timeSeconds, acrossMeters);
        if (bundle == null) return;
        double circumference = WorldCircumference();
        for (int i = 0; i < bundle.Count; i++)
        {
            MegastormRecord record = bundle[i];
            var offset = new Vector2((float)ShortestAroundDelta(record.CenterAroundMeters, aroundMeters, circumference), (float)(acrossMeters - record.CenterAcrossMeters));
            var geometry = new StormSystemGeometry(record.Id, record.Kind, offset, (float)record.SemiAcrossMeters, (float)record.SemiAlongMeters,
                0.0f, 1.0f, 1.0f, 0.0f, MegastormRecordDrift(record), record.Density, record.Precipitation, StormLifecycleMature);
            StormArrivalMath.NormalizedDistance(geometry, out float ray, out float dist);
            if (dist - ray > StormArrivalCullMeters) continue;
            into.Add(geometry);
        }
    }

    // ---- natural-weather suppression (4.3) ----
    /// <summary>S at a natural system's centre given in the SOURCE frame at <paramref name="timeSeconds"/> (advected to the world by the weather wind).</summary>
    private float MegastormSuppressionAtSource(double sourceAroundMeters, double sourceAcrossMeters, double timeSeconds)
    {
        IMegastormWeatherSource? source = MegastormSource;
        if (source == null) return 0.0f;
        Vector2 wind = WeatherWindMetersPerSecond;
        return source.SuppressionAt(WrapAround(sourceAroundMeters + wind.X * timeSeconds), sourceAcrossMeters + wind.Y * timeSeconds, timeSeconds);
    }

    /// <summary>The lifecycle envelope times (1 - S) for a built natural system; exactly <paramref name="lifecycleEnvelope"/> without a source or with S = 0.</summary>
    private float EffectiveSystemEnvelope(in WeatherSystemDefinition system, float lifecycleEnvelope, double timeSeconds)
    {
        if (MegastormSource == null) return lifecycleEnvelope;
        float suppression = MegastormSuppressionAtSource(system.SourceAroundMeters, system.SourceAcrossMeters, timeSeconds);
        return suppression > 0.0f ? lifecycleEnvelope * (1.0f - suppression) : lifecycleEnvelope;
    }

    /// <summary>Test hook: the bundle at a time and query across (the records the injection sites turn into systems), empty without a source.</summary>
    public int GetMegastormBundle(double timeSeconds, double queryAcrossMeters, List<MegastormRecord> into)
    {
        into.Clear();
        List<MegastormRecord>? bundle = BuildMegastormBundle(timeSeconds, queryAcrossMeters);
        if (bundle == null) return 0;
        into.AddRange(bundle);
        return into.Count;
    }
}
