using Godot;
using System;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

// Anchored analytic water (W1 precision 2026-11-20, smooth turns 10.9). The analytic waves are seam-closed: every around-ring phase gradient is
// quantised to an integer number n of cycles per turn. The shader used to build the around coordinate from atan(world) * R in float32, which steps
// every ~1 m at 1e7 m round the ring. The CPU now decides each train's n and across wavenumber in double (RingWaterWaveTrains) and keeps a continuous
// phase at an anchor near the observer; the shader adds n times the small local angle of the pixel from the anchor and the across wavenumber times
// the local across offset. A wind-bearing change therefore pivots the pattern on the anchor instead of scrambling it.
public static partial class RingWaterModel
{
    // xyz: the anchor in the main view's world frame; w: its distance from the ring axis (m).
    private static readonly StringName WaveAnchorWorldName = "ring_water_wave_anchor_world";
    // xyz: the around tangent at the anchor (direction of increasing around angle); w: 1 when the anchor is valid, 0 keeps the legacy coordinate.
    private static readonly StringName WaveAnchorTangentName = "ring_water_wave_anchor_tangent";
    // Per kind (ocean, lake, river) and family (main waves, warps): a mat4 whose flat element 3t, 3t+1, 3t+2 (column i / 4, row i % 4) holds train t's
    // around cycles n, scaled across wavenumber and anchor phase in cycles (time phase and constant offset folded in).
    private static readonly StringName[] TrainGlobalNames =
    {
        "ring_water_trains_ocean_main", "ring_water_trains_ocean_warp",
        "ring_water_trains_lake_main", "ring_water_trains_lake_warp",
        "ring_water_trains_river_main", "ring_water_trains_river_warp",
    };
    // The river profile scales the coordinate by 2.15 before adding the advection (ring_water_profile_*_filtered).
    private const double RiverCoordinateScale = 2.15;

    /// <summary>Debug A/B switch for perf tooling only (not a menu row): false publishes an invalid anchor, so the shader keeps the legacy absolute coordinate.</summary>
    public static bool WaveAnchorEnabled { get; set; } = true;
    private static Vector4 _anchorWorld, _anchorTangent;
    private static bool _anchorPublished;
    private static readonly RingWaterWaveTrains.TrainState[] TrainStates = new RingWaterWaveTrains.TrainState[RingWaterWaveTrains.KindCount * 2 * RingWaterWaveTrains.TrainCount];
    private static readonly Projection[] TrainTables = new Projection[RingWaterWaveTrains.KindCount * 2];
    private static readonly bool[] TrainTablePublished = new bool[RingWaterWaveTrains.KindCount * 2];
    private static double _trainRadius = double.NaN, _trainWaveLength = double.NaN;
    public static Vector4 PublishedWaveAnchorWorld => _anchorWorld;
    public static Vector4 PublishedWaveAnchorTangent => _anchorTangent;
    /// <summary>Last published train table of a kind (0 ocean, 1 lake, 2 river) and family (0 main, 1 warp); headless-safe stand-in for the global.</summary>
    public static Projection PublishedTrainTable(int kind, int family) => TrainTables[kind * 2 + family];

    /// <summary>The river around-fraction offset of the advection: (clock * speed * 1.8) / (2.15 * 2 pi R), as the legacy advection term.</summary>
    public static double RiverAdvectionFraction(double clock, double waveSpeed, double materialRadius)
    {
        double circumference = Math.Tau * Math.Max(1.0, materialRadius);
        double advance = clock * waveSpeed * 1.8 / (RiverCoordinateScale * circumference);
        return double.IsFinite(advance) ? advance - Math.Floor(advance) : 0.0;
    }

    /// <summary>
    /// Publishes the wave anchor at the observer (vertical 0) and the train tables once per frame and on every floating-origin change. Without a
    /// grid, an observer, a curved ring layout or the wind clock, the anchor is invalid and the shader keeps the legacy coordinate.
    /// </summary>
    public static void UpdateWaveAnchor(VoxelChunkGridManager? grid, RingCanonicalPosition? observer)
    {
        if (!WaveAnchorEnabled || !_windClockEnabled || grid == null || !GodotObject.IsInstanceValid(grid) || observer is not { } position
            || !grid.UseRingCurvedPlacement || !grid.UseHabitableRingAxisLayout)
        {
            InvalidateWaveAnchor();
            return;
        }
        RingLodGeometrySnapshot geometry = grid.CaptureLodGeometrySnapshot();
        double radius = Math.Max(1.0, geometry.RingRadiusMeters);
        double circumference = geometry.CircumferenceMeters > 0.0 ? geometry.CircumferenceMeters : Math.Tau * radius;
        double around = position.AroundMeters % circumference;
        if (around < 0.0) around += circumference;
        // The geometry places a canonical point at angle around / radius (RingLodGeometrySnapshot.CanonicalToAbsoluteLocal), which is the shader's
        // atan(radial.z, radial.x) for an identity grid basis.
        double angle = around / radius;
        Vector3 world = geometry.CanonicalToWorld(around, 0.0, position.AcrossMeters);
        double axisDistance = radius - geometry.RingVerticalBaseOffsetMeters;
        // Analytic tangent (a finite difference of two float world points would carry their rounding into every local coordinate).
        Vector3 tangent = (geometry.GridGlobalTransform.Basis * new Vector3((float)-Math.Sin(angle), 0.0f, (float)Math.Cos(angle))).Normalized();
        UpdateTrainTables(angle / Math.Tau, position.AcrossMeters, grid.GetCanonicalRingRadiusMeters());
        PublishWaveAnchor(new Vector4(world.X, world.Y, world.Z, (float)axisDistance), new Vector4(tangent.X, tangent.Y, tangent.Z, 1.0f));
    }

    /// <summary>
    /// Advances every train's anchor phase to the anchor (around fraction <paramref name="aroundFraction"/>, across metres <paramref name="across"/>)
    /// with the current wind bearing and publishes the six tables. <paramref name="materialRadius"/> is the ring_radius_meters of the main-view
    /// water materials (their quantisation circumference).
    /// </summary>
    public static void UpdateTrainTables(double aroundFraction, double across, double materialRadius)
    {
        // A changed radius or wave length changes every quantisation; the phases restart from their absolute values (a one-off, like a retune).
        double waveLength = Math.Max(2.0, WaveLengthMeters);
        if (materialRadius != _trainRadius || waveLength != _trainWaveLength)
        {
            Array.Clear(TrainStates);
            _trainRadius = materialRadius;
            _trainWaveLength = waveLength;
        }
        Vector2 windDirection = _waterMeanWind.LengthSquared() > .0001f ? _waterMeanWind.Normalized() : new Vector2(.916f, .401f).Normalized();
        (double X, double Y) wind = (windDirection.X, windDirection.Y);
        double baseFrequency = Math.Tau / waveLength;
        double riverAdvection = RiverAdvectionFraction(_windClock, WaveSpeed, materialRadius);
        Span<float> values = stackalloc float[16];
        for (int kind = 0; kind < RingWaterWaveTrains.KindCount; kind++)
        {
            double scale = RingWaterWaveTrains.KindScale[kind];
            double around = kind == 2 ? aroundFraction + riverAdvection : aroundFraction;
            for (int family = 0; family < 2; family++)
            {
                values.Clear();
                for (int train = 0; train < RingWaterWaveTrains.TrainCount; train++)
                {
                    bool warp = family == 1;
                    (double X, double Y) heading = warp ? RingWaterWaveTrains.WarpDirections[train] : RingWaterWaveTrains.MainDirections[train];
                    double ratio = warp ? RingWaterWaveTrains.WarpRatios[train] : RingWaterWaveTrains.MainRatios[train];
                    double offset = warp ? RingWaterWaveTrains.WarpOffsets[train] : RingWaterWaveTrains.MainOffsets[train];
                    var (cycles, acrossWavenumber) = RingWaterWaveTrains.Quantize(
                        RingWaterWaveTrains.Rotate(heading, wind), baseFrequency * ratio, materialRadius, scale);
                    int stateIndex = (kind * 2 + family) * RingWaterWaveTrains.TrainCount + train;
                    RingWaterWaveTrains.Advance(ref TrainStates[stateIndex], cycles, acrossWavenumber * scale, around, across);
                    Vector3 timePhases = WaterPhases[warp ? train + 5 : train];
                    double timePhase = kind == 0 ? timePhases.X : kind == 1 ? timePhases.Y : timePhases.Z;
                    double phase = RingWaterWaveTrains.Frac(TrainStates[stateIndex].Phase + (timePhase + offset) / Math.Tau);
                    values[train * 3] = (float)cycles;
                    values[train * 3 + 1] = (float)acrossWavenumber;
                    values[train * 3 + 2] = (float)phase;
                }
                var table = new Projection(
                    new Vector4(values[0], values[1], values[2], values[3]),
                    new Vector4(values[4], values[5], values[6], values[7]),
                    new Vector4(values[8], values[9], values[10], values[11]),
                    new Vector4(values[12], values[13], values[14], values[15]));
                int tableIndex = kind * 2 + family;
                if (!TrainTablePublished[tableIndex] || TrainTables[tableIndex] != table)
                {
                    if (!_headlessTrainTables) PublishWaterGlobal(TrainGlobalNames[tableIndex], table);
                    TrainTables[tableIndex] = table;
                    TrainTablePublished[tableIndex] = true;
                }
            }
        }
    }

    // Tests drive UpdateTrainTables without a RenderingServer global table write.
    private static bool _headlessTrainTables;
    /// <summary>Test hook: sets the wind clock inputs the tables read and disables the global writes.</summary>
    public static void ConfigureTrainTablesForTest(double clock, Vector2 meanWind, float waveSpeed, float waveLength)
    {
        _headlessTrainTables = true;
        _windClock = clock;
        _waterMeanWind = meanWind;
        WaveSpeed = waveSpeed;
        WaveLengthMeters = waveLength;
        for (int index = 0; index < WaterPhases.Length; index++)
            WaterPhases[index] = new Vector3(
                RingWaterWindResponse.BoundedPhase(clock, waveSpeed, WaterPhaseRates[index]),
                RingWaterWindResponse.BoundedPhase(clock, waveSpeed, WaterPhaseRates[index] * .42),
                RingWaterWindResponse.BoundedPhase(clock, waveSpeed, WaterPhaseRates[index] * .18));
    }
    public static void ResetTrainStatesForTest() { Array.Clear(TrainStates); _trainRadius = double.NaN; }

    public static void InvalidateWaveAnchor()
    {
        if (_anchorPublished && _anchorTangent.W == 0.0f) return;
        PublishWaveAnchor(_anchorWorld, new Vector4(_anchorTangent.X, _anchorTangent.Y, _anchorTangent.Z, 0.0f));
    }

    private static void PublishWaveAnchor(Vector4 world, Vector4 tangent)
    {
        if (!_anchorPublished || _anchorWorld != world) PublishWaterGlobal(WaveAnchorWorldName, world);
        if (!_anchorPublished || _anchorTangent != tangent) PublishWaterGlobal(WaveAnchorTangentName, tangent);
        _anchorWorld = world;
        _anchorTangent = tangent;
        _anchorPublished = true;
    }
}
