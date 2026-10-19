using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Atmosphere;

/// <summary>
/// LUT host of the physical haze model (P7-1 of docs/atmosphere-haze-revamp-plan.md). Builds the sky-view LUT (3 x RGBA16F 192 x 108, a
/// phase-factored cache of ring_haze_eval_sky used only by materials/ring_local_sky.gdshader, layout in
/// materials/atmosphere/ring_haze_sky_view.gdshaderinc) with a render-thread compute shader and publishes it as the shader globals
/// ringworld_haze_sky_lut0/1/2 (Texture2DRD) plus ringworld_haze_sky_lut_key = (camera_h_m, across_m, graze constant k, valid).
/// Consumers use the LUT only when valid and the key matches their camera; valid is 0 while disabled, not Physical, before the first
/// build, after a compile or build error, and when the camera is within 50 m of the air cap. The state is static (one set of globals);
/// GDScript loads the class with load("res://scripts/Atmosphere/RingAtmosphereLuts.cs").new() and calls the instance methods.
/// The LUT is rebuilt every frame the inputs change (sun, camera, RingAtmosphereModel.Revision, StormRevision); no slicing. It stores terms
/// per unit E_sun/day, so sun irradiance, cloud-shadow dimming and ambient never force a rebuild.
/// </summary>
public partial class RingAtmosphereLuts : RefCounted
{
    public const int SkyWidth = 192, SkyHeight = 108;
    private const string BuildShaderPath = "res://materials/atmosphere/ring_haze_sky_view_build.comp";
    private static readonly StringName[] LutGlobals = { "ringworld_haze_sky_lut0", "ringworld_haze_sky_lut1", "ringworld_haze_sky_lut2" };
    private static readonly StringName KeyGlobal = "ringworld_haze_sky_lut_key";
    private const float MinimumCapClearanceMeters = 50.0f;

    private readonly record struct BuildInputs(
        float Radius, Vector3 Outward, Vector3 Axis, float Height, float SideNegative, float SidePositive, Vector3 ToSun,
        ulong Revision, ulong StormRevision);

    private static readonly object Gate = new();
    private static readonly Texture2Drd?[] Textures = new Texture2Drd?[3];
    private static bool _globalsRegistered;
    private static BuildInputs? _lastInputs;
    private static Vector4 _publishedKey = new(float.NaN, 0, 0, 0);
    private static long _generation;
    private static volatile bool _ready, _failed;
    private static string _status = "Uninitialized";
    private static int _buildsQueued;
    private static int _buildsCompleted;
    private static float _lastGraze;
    private static float[] _lastParams = Array.Empty<float>();

    // Render-thread state.
    private static RenderingDevice? _rd;
    private static Rid _shader, _pipeline, _uniformBuffer, _uniformSet;
    private static readonly Rid[] Images = new Rid[3];

    /// <summary>Live-tuning toggle ("Sky haze LUT" in the Atmosphere tab, default on). Off publishes valid = 0 and the sky evaluates the analytic function.</summary>
    public static bool SkyViewEnabled { get; set; } = true;
    public static bool SkyViewReady => _ready && !_failed;
    public static string Status { get { lock (Gate) return _status; } }
    public static int BuildsCompleted => _buildsCompleted;
    public static Vector4 PublishedKey => _publishedKey;
    /// <summary>Graze constant k of the last queued build (0 before any request).</summary>
    public static float LastGrazeConstant => _lastGraze;

    // ---- GDScript-facing instance API (all state is static) ----

    /// <summary>
    /// Requests the sky-view LUT for the current camera. radius/height/sides in metres, outward = unit radial direction at the camera (away from
    /// the axis), toSun = the hub-blended primary light direction the sky shader's haze params carry (same space as outward/axis). Queues a
    /// render-thread rebuild when anything changed, publishes the key, and returns whether the LUT is usable this frame.
    /// </summary>
    public bool RequestSkyView(float radiusMeters, Vector3 outward, Vector3 axis, float cameraHeightMeters, float sideNegativeMeters, float sidePositiveMeters, Vector3 toSun)
    {
        float cap = RingAtmosphereModel.HazeCapHeightMeters;
        bool inputsFinite = float.IsFinite(radiusMeters) && float.IsFinite(cameraHeightMeters) && float.IsFinite(sideNegativeMeters) && float.IsFinite(sidePositiveMeters)
            && IsFinite(outward) && IsFinite(axis) && IsFinite(toSun) && outward.LengthSquared() > 1e-8f && axis.LengthSquared() > 1e-8f && toSun.LengthSquared() > 1e-8f;
        bool wanted = SkyViewEnabled && inputsFinite && RingAtmosphereModel.HazeModel == RingAtmosphereModel.HazeModelKind.Physical
            && radiusMeters > 1.0f && cameraHeightMeters < cap - MinimumCapClearanceMeters;
        if (!wanted || _failed)
        {
            PublishKey(new Vector4(0, 0, 0, 0));
            return false;
        }
        EnsureGlobals();
        var inputs = new BuildInputs(radiusMeters, outward.Normalized(), axis.Normalized(), cameraHeightMeters, sideNegativeMeters, sidePositiveMeters, toSun.Normalized(),
            RingAtmosphereModel.Revision, RingAtmosphereModel.StormRevision);
        BuildInputs? previous;
        lock (Gate) previous = _lastInputs;
        if (previous is not BuildInputs last || !last.Equals(inputs))
        {
            float k = GrazeConstant(radiusMeters, cap, cameraHeightMeters);
            float[] packed = PackBuildParams(inputs, k);
            lock (Gate) { _lastInputs = inputs; _lastParams = packed; _lastGraze = k; _buildsQueued++; }
            long generation = _generation;
            RenderingServer.CallOnRenderThread(Callable.From(() => BuildOnRenderThread(generation, packed)));
        }
        if (!_ready)
        {
            PublishKey(new Vector4(0, 0, 0, 0));
            return false;
        }
        PublishKey(new Vector4(cameraHeightMeters, (sideNegativeMeters - sidePositiveMeters) * 0.5f, _lastGraze, 1.0f));
        return true;
    }

    /// <summary>Drops the GPU resources and invalidates the key (scene exit). Safe to call repeatedly.</summary>
    public void Release()
    {
        PublishKey(new Vector4(0, 0, 0, 0));
        long generation;
        lock (Gate) { generation = ++_generation; _lastInputs = null; _status = "Released"; }
        _ready = false;
        _failed = false;
        RenderingServer.CallOnRenderThread(Callable.From(() => FreeOnRenderThread(generation)));
    }

    public bool IsReady() => SkyViewReady;
    public string GetStatus() => Status;
    public int GetBuildsCompleted() => _buildsCompleted;
    public Vector4 GetPublishedKey() => _publishedKey;
    public float GetGrazeConstant() => _lastGraze;
    /// <summary>The 36 floats of the last queued build's uniform block (tests feed the same block to their probe shaders).</summary>
    public float[] GetLastBuildParams() { lock (Gate) return (float[])_lastParams.Clone(); }
    /// <summary>RD texture RIDs of lut0/lut1/lut2 (valid once <see cref="IsReady"/>); tests sample them directly.</summary>
    public Rid GetSkyViewTexture(int index) { lock (Gate) return index is >= 0 and < 3 ? Images[index] : default; }

    public void SetSkyViewEnabled(bool enabled) => SkyViewEnabled = enabled;

    // ---- Shared helpers (also used by tests) ----

    /// <summary>Graze constant k = 1 - ((R - cap) / (R - h0))^2, in the cancellation-free form the shader uses (ring_haze_sky_graze_constant).</summary>
    public static float GrazeConstant(float radiusMeters, float capMeters, float heightMeters)
    {
        float b = radiusMeters - heightMeters;
        return Mathf.Clamp((capMeters - heightMeters) * (2.0f * radiusMeters - capMeters - heightMeters) / Mathf.Max(b * b, 1.0f), 0.0f, 0.5f);
    }

    private static float[] PackBuildParams(BuildInputs i, float k)
    {
        Vector3 sigmaR = RingAtmosphereModel.HazeSigmaRayleigh;
        Vector4 storm = RingAtmosphereModel.HazeStormVector;
        return new[]
        {
            sigmaR.X, sigmaR.Y, sigmaR.Z, RingAtmosphereModel.HazeRayleighHeightMeters,
            RingAtmosphereModel.HazeSigmaAerosol, RingAtmosphereModel.HazeOmegaAerosol, RingAtmosphereModel.HazeAerosolHeightMeters, RingAtmosphereModel.HazeSigmaMist,
            RingAtmosphereModel.HazeOmegaMist, RingAtmosphereModel.HazeMistHeightMeters, RingAtmosphereModel.HazeCapHeightMeters, RingAtmosphereModel.HazeG,
            i.Radius, RingAtmosphereModel.FloorDepthMeters, i.Height, RingAtmosphereModel.HazeQuality,
            i.SideNegative, i.SidePositive, k, 0.0f,
            i.Outward.X, i.Outward.Y, i.Outward.Z, 0.0f,
            i.Axis.X, i.Axis.Y, i.Axis.Z, 0.0f,
            i.ToSun.X, i.ToSun.Y, i.ToSun.Z, 0.0f,
            storm.X, storm.Y, storm.Z, storm.W,
        };
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static void PublishKey(Vector4 key)
    {
        if (_publishedKey == key) return;
        _publishedKey = key;
        RenderingServer.GlobalShaderParameterSet(KeyGlobal, key);
    }

    /// <summary>Registers the three Texture2DRD wrappers as globals once (main thread); the render thread attaches the RD textures later.</summary>
    private static void EnsureGlobals()
    {
        if (_globalsRegistered) return;
        for (int i = 0; i < 3; i++)
        {
            var texture = Textures[i] ??= new Texture2Drd();
            RenderingServer.GlobalShaderParameterSet(LutGlobals[i], texture);
        }
        _globalsRegistered = true;
    }

    // ---- Render thread ----

    private static void BuildOnRenderThread(long generation, float[] packed)
    {
        try
        {
            if (generation != _generation || _failed) return;
            if (!EnsureDevice(generation)) return;
            var bytes = new byte[packed.Length * 4];
            Buffer.BlockCopy(packed, 0, bytes, 0, bytes.Length);
            _rd!.BufferUpdate(_uniformBuffer, 0, (uint)bytes.Length, bytes);
            _rd.CaptureTimestamp("ring_sky_lut_begin");
            long list = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(list, _pipeline);
            _rd.ComputeListBindUniformSet(list, _uniformSet, 0);
            _rd.ComputeListDispatch(list, (uint)((SkyWidth + 7) / 8), (uint)((SkyHeight + 7) / 8), 1);
            _rd.ComputeListEnd();
            _rd.CaptureTimestamp("ring_sky_lut_end");
            lock (Gate)
            {
                if (generation != _generation) return;
                _buildsCompleted++;
                _ready = true;
                _status = "Ready";
            }
        }
        catch (Exception error)
        {
            Fail("Sky-view LUT build failed: " + error.Message);
        }
    }

    private static bool EnsureDevice(long generation)
    {
        if (_pipeline.IsValid) return true;
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd is null) { Fail("Sky-view LUT unavailable: no RenderingDevice"); return false; }
        string source = ExpandIncludes(BuildShaderPath);
        if (string.IsNullOrEmpty(source)) { Fail("Sky-view LUT build shader missing: " + BuildShaderPath); return false; }
        using var shaderSource = new RDShaderSource { SourceCompute = source };
        using RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(shaderSource);
        if (!string.IsNullOrWhiteSpace(spirv.CompileErrorCompute)) { Fail("Sky-view LUT shader compile error: " + spirv.CompileErrorCompute); return false; }
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        if (!_shader.IsValid) { Fail("Sky-view LUT shader creation failed"); return false; }
        _pipeline = _rd.ComputePipelineCreate(_shader);
        var format = new RDTextureFormat
        {
            TextureType = RenderingDevice.TextureType.Type2D,
            Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            Width = SkyWidth,
            Height = SkyHeight,
            ArrayLayers = 1,
            Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
        };
        _uniformBuffer = _rd.UniformBufferCreate(36 * 4);
        var uniforms = new Godot.Collections.Array<RDUniform>();
        for (int i = 0; i < 3; i++)
        {
            Images[i] = _rd.TextureCreate(format, new RDTextureView());
            var image = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = i };
            image.AddId(Images[i]);
            uniforms.Add(image);
        }
        var block = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 3 };
        block.AddId(_uniformBuffer);
        uniforms.Add(block);
        _uniformSet = _rd.UniformSetCreate(uniforms, _shader, 0);
        if (!_pipeline.IsValid || !_uniformBuffer.IsValid || !_uniformSet.IsValid || !Images[0].IsValid || !Images[1].IsValid || !Images[2].IsValid)
        {
            FreeDeviceResources();
            Fail("Sky-view LUT allocation failed");
            return false;
        }
        for (int i = 0; i < 3; i++)
            if (Textures[i] is { } texture) texture.TextureRdRid = Images[i];
        return true;
    }

    private static void FreeOnRenderThread(long generation)
    {
        if (generation != _generation) return;
        foreach (Texture2Drd? texture in Textures)
            if (texture is not null) texture.TextureRdRid = default;
        FreeDeviceResources();
    }

    private static void FreeDeviceResources()
    {
        if (_rd is null) return;
        if (_uniformSet.IsValid && _rd.UniformSetIsValid(_uniformSet)) _rd.FreeRid(_uniformSet);
        foreach (Rid rid in new[] { _uniformBuffer, Images[0], Images[1], Images[2], _pipeline, _shader })
            if (rid.IsValid) _rd.FreeRid(rid);
        _uniformSet = default; _uniformBuffer = default; _pipeline = default; _shader = default;
        Images[0] = default; Images[1] = default; Images[2] = default;
    }

    private static void Fail(string message)
    {
        _failed = true;
        _ready = false;
        lock (Gate) _status = message;
        GD.PushError(message);
    }

    /// <summary>Expands #include "res://..." lines (each file once), since RDShaderSource has no include support.</summary>
    public static string ExpandIncludes(string path)
    {
        var seen = new HashSet<string>();
        var builder = new StringBuilder();
        return Expand(path, seen, builder) ? builder.ToString() : "";
    }

    private static bool Expand(string path, HashSet<string> seen, StringBuilder output)
    {
        if (!seen.Add(path)) return true;
        if (!FileAccess.FileExists(path)) return false;
        string text = FileAccess.GetFileAsString(path);
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("#include", StringComparison.Ordinal))
            {
                int open = trimmed.IndexOf('"');
                int close = trimmed.LastIndexOf('"');
                if (open < 0 || close <= open || !Expand(trimmed.Substring(open + 1, close - open - 1), seen, output)) return false;
            }
            else
            {
                output.Append(line.TrimEnd('\r')).Append('\n');
            }
        }
        return true;
    }
}
