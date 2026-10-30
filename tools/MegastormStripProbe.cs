using System;
using Godot;
using RingworldTechDemo;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Trigger, readback and analysis of the megastorm strip map (docs/megastorm-design.md 3.2, WO4). Driven by tools/megastorm_strip_gpu_test.gd.
/// The texture lives on the main RenderingDevice, so the read runs on the render thread; call RequestReadback, await a couple of frames until
/// ReadbackReady, then Analyze. Heights texel: (body base, body top, canopy underside, canopy top) in absolute metres (datum added to non-empty
/// intervals, clamped to 18 km); aux: (canopy coverage, mammatus, tower coverage, body coverage).
/// </summary>
public partial class MegastormStripProbe : RefCounted
{
    private const int CapacityX = 1024, CapacityY = 1024; // RingMegastormStrip.gd CAPACITY_X / CAPACITY_Y
    private const double XMin = -72000.0;
    private const double HeightToleranceMeters = 50.0, AuxTolerance = 0.02, MaximumHeightMeters = 18000.5;
    private const double Speed = 350.0, AgeSeconds = 100000.0, EdgeAheadOfCameraMeters = 10000.0;

    private byte[]? _heights;
    private byte[]? _aux;
    private Godot.Collections.Dictionary _config = new();
    private double _xTexel, _acrossTexel;
    private long _builds;

    public string ErrorText { get; private set; } = "";
    public bool ReadbackReady { get; private set; }

    private static GodotObject? Driver(Node world)
        => world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge").GetMegastormStripDriver();

    /// <summary>Front-buffer state: valid, builds completed, real texture, texture generation.</summary>
    public Godot.Collections.Dictionary State(Node world)
    {
        var state = new Godot.Collections.Dictionary { ["valid"] = false, ["builds"] = 0L, ["real"] = false, ["slices"] = 0L };
        GodotObject? driver = Driver(world);
        if (driver == null) return state;
        state["valid"] = driver.Get("valid").AsBool();
        state["builds"] = driver.Get("builds_completed").AsInt64();
        state["real"] = driver.Get("real_texture").AsBool();
        state["slices"] = driver.Get("slices_dispatched").AsInt64();
        state["generation"] = driver.Get("texture_generation").AsInt64();
        return state;
    }

    /// <summary>
    /// Triggers the megastorm through the world's RingMegastorm node so the camera (the capture base position) is 10 km behind the edge at the
    /// current weather time: seed 7, 350 m/s, +around, fully formed (age 100000 s, formation 1800 s).
    /// </summary>
    public bool Trigger(Node world)
    {
        try
        {
            var node = world.GetNode<RingMegastorm>("RingMegastorm");
            var clouds = world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");
            var explorer = world.GetNode<RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController>("Explorer");
            RingCanonicalPosition camera = explorer.CanonicalPosition;
            double now = clouds.CloudSimulationTimeSeconds;
            MegastormSettings settings = node.Model.Settings;
            double circumference = settings.CircumferenceMeters;
            // The lead depends on the seed (meander phase): read it from a scratch model with the same settings and seed 7.
            var scratch = new MegastormModel(settings);
            scratch.Trigger(new MegastormTrigger("strip_scratch", 0.0, 1, now, (float)Speed, 1800f, 7));
            double lead = scratch.LeadOffset(camera.AcrossMeters, now);
            double sNow = MegastormModel.WrapPositive(camera.AroundMeters + (EdgeAheadOfCameraMeters + lead), circumference);
            double leading = MegastormModel.WrapPositive(sNow - Speed * AgeSeconds, circumference);
            bool ok = node.Trigger(new MegastormTrigger("strip_gpu_test", leading, 1, now - AgeSeconds, (float)Speed, 1800f, 7));
            if (!ok) ErrorText = "RingMegastorm.Trigger refused (already triggered)";
            return ok;
        }
        catch (Exception error) { ErrorText = error.ToString(); return false; }
    }

    /// <summary>Queues the readback of both layers of the front buffer on the render thread (a frame or two later ReadbackReady is true).</summary>
    public void RequestReadback(Node world)
    {
        ReadbackReady = false;
        GodotObject? driver = Driver(world);
        if (driver == null || !driver.Get("valid").AsBool() || !driver.Get("real_texture").AsBool())
        {
            ErrorText = "megastorm strip is not valid"; ReadbackReady = true; _heights = null; return;
        }
        Rid texture = driver.Get("texture").AsRid();
        int buffer = (int)driver.Get("front_buffer").AsInt64();
        _config = driver.Get("front_config").AsGodotDictionary();
        _xTexel = driver.Get("x_texel").AsDouble();
        _acrossTexel = driver.Get("across_texel").AsDouble();
        _builds = driver.Get("builds_completed").AsInt64();
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            RenderingDevice rd = RenderingServer.GetRenderingDevice();
            _heights = rd.TextureGetData(texture, (uint)(buffer * 2));
            _aux = rd.TextureGetData(texture, (uint)(buffer * 2 + 1));
            ReadbackReady = true;
        }));
    }

    private static float H(byte[] data, int x, int y, int channel)
        => (float)BitConverter.ToHalf(data, ((y * CapacityX) + x) * 8 + channel * 2);

    private static float Expect(float value, bool nonEmpty, double datum)
        => nonEmpty ? (float)Math.Min(value + datum, 18000.0) : 0.0f;

    /// <summary>
    /// Every texel finite and at most 18 km; then 64 check points (8 x 8: the canopy ahead, the face, the wall and the transition along x; two rows
    /// within 6 km of each rim along across) against MegastormProfile.Column at the build's weather time. Returns "PASS ..." or "FAIL ...".
    /// </summary>
    public string Analyze(Node world)
    {
        if (_heights == null || _aux == null) return "FAIL no readback: " + ErrorText;
        var node = world.GetNode<RingMegastorm>("RingMegastorm");
        MegastormModel model = node.Model;
        double t = _config["t"].AsDouble(), datum = _config["datum"].AsDouble(), width = _config["width"].AsDouble();
        int nonEmpty = 0;
        for (int y = 0; y < CapacityY; y++)
            for (int x = 0; x < CapacityX; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float v = H(_heights, x, y, c);
                    if (!float.IsFinite(v)) return $"FAIL non-finite height at ({x},{y}) channel {c}";
                    if (v > MaximumHeightMeters) return $"FAIL height above 18 km at ({x},{y}) channel {c}: {v}";
                    float a = H(_aux, x, y, c);
                    if (!float.IsFinite(a)) return $"FAIL non-finite aux at ({x},{y}) channel {c}";
                }
                if (H(_heights, x, y, 1) > H(_heights, x, y, 0)) nonEmpty++;
            }
        double[] xs = { -60000.0, -30000.0, -9000.0, -1500.0, 4000.0, 14000.0, 33000.0, 52000.0 };
        double[] acrosses = { 1500.0, 4500.0, 60000.0, 130000.0, 190000.0, 260000.0, width - 4500.0, width - 1500.0 };
        double maxHeightError = 0.0, maxAuxError = 0.0;
        string worstHeight = "", worstAux = "";
        int bodyPoints = 0, canopyPoints = 0;
        foreach (double xm in xs)
        {
            int i = (int)Math.Round((xm - XMin) / _xTexel - 0.5);
            foreach (double am in acrosses)
            {
                int j = (int)Math.Round(am / _acrossTexel - 0.5);
                double x = XMin + (i + 0.5) * _xTexel, across = (j + 0.5) * _acrossTexel;
                MegastormColumn col = MegastormProfile.Column(model, x, across, t);
                bool body = col.BodyTop > col.BodyBase, canopy = col.CanopyTop > col.CanopyUnderside;
                if (body) bodyPoints++;
                if (canopy) canopyPoints++;
                float[] expectedHeights =
                {
                    Expect(col.BodyBase, body, datum), Expect(col.BodyTop, body, datum),
                    Expect(col.CanopyUnderside, canopy, datum), Expect(col.CanopyTop, canopy, datum),
                };
                float[] expectedAux = { col.CanopyCoverage, col.Mammatus, col.TowerCoverage, col.BodyCoverage };
                for (int c = 0; c < 4; c++)
                {
                    double heightError = Math.Abs(H(_heights, i, j, c) - expectedHeights[c]);
                    double auxError = Math.Abs(H(_aux, i, j, c) - expectedAux[c]);
                    if (heightError > maxHeightError) { maxHeightError = heightError; worstHeight = $"texel ({i},{j}) x={x:F0} across={across:F0} channel {c}: gpu {H(_heights, i, j, c):F1} expected {expectedHeights[c]:F1}"; }
                    if (auxError > maxAuxError) { maxAuxError = auxError; worstAux = $"texel ({i},{j}) x={x:F0} across={across:F0} channel {c}: gpu {H(_aux, i, j, c):F4} expected {expectedAux[c]:F4}"; }
                }
            }
        }
        string summary = $"builds={_builds} nonempty_texels={nonEmpty} body_points={bodyPoints} canopy_points={canopyPoints} max_height_error={maxHeightError:F2} m max_aux_error={maxAuxError:F4}";
        if (nonEmpty == 0 || bodyPoints == 0 || canopyPoints == 0) return "FAIL strip is empty or the check points miss the storm: " + summary;
        if (maxHeightError > HeightToleranceMeters) return $"FAIL height error {maxHeightError:F2} m > {HeightToleranceMeters} m at {worstHeight}; {summary}";
        if (maxAuxError > AuxTolerance) return $"FAIL aux error {maxAuxError:F4} > {AuxTolerance} at {worstAux}; {summary}";
        return "PASS " + summary;
    }
}
