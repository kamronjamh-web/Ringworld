using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Atmosphere;

/// <summary>Per-instance Physical haze frame for vegetation. Parameters is the 40-float std140 block the vegetation compute reads
/// (10 vec4, unstormed values; the shader applies the storm). Key changes whenever any input the terms depend on changes (except the camera / sun
/// motion, which callers threshold themselves).</summary>
public readonly record struct RingVegetationHazeFrame(bool Enabled, Vector3 CameraWorld, Vector3 ToSun, float[] Parameters, ulong Key);

/// <summary>
/// Host of the per-instance vegetation haze atlases (materials/atmosphere/ring_vegetation_haze_terms.gdshaderinc). The GPU vegetation compute
/// (RingGpuVegetationCompute) writes one RGBA16F atlas per compute instance; this class owns the two channel Texture2DRD wrappers published as the
/// globals ringworld_vegetation_haze_grass / _trees and the vec4 ringworld_vegetation_haze_state (x grass live, y trees live). A channel is live
/// only while its owning compute instance has an atlas and is writing it; consumers fall back to per-vertex haze otherwise.
/// </summary>
public static class RingVegetationHaze
{
    public enum Channel { None = -1, Grass = 0, Trees = 1 }
    public const int AtlasWidth = 2048, SlotsPerRow = 1024, ParameterFloats = 40, MaximumAtlasRows = 16384;
    public const string TermsIncludePath = "res://materials/atmosphere/ring_vegetation_haze_terms.gdshaderinc";
    private static readonly StringName[] TextureGlobals = { "ringworld_vegetation_haze_grass", "ringworld_vegetation_haze_trees" };
    private static readonly StringName StateGlobal = "ringworld_vegetation_haze_state";

    private static readonly object Gate = new();
    private static readonly Texture2Drd?[] Textures = new Texture2Drd?[2];
    private static readonly object?[] Owners = new object?[2];
    private static readonly bool[] Live = new bool[2];
    private static Vector4 _published;
    private static bool _enabled = true;
    private static ulong _revision;

    /// <summary>Live-tuning toggle ("Per-instance vegetation haze", default on). Setting a different value bumps <see cref="Revision"/>.</summary>
    public static bool Enabled
    {
        get { lock (Gate) return _enabled; }
        set { lock (Gate) { if (_enabled == value) return; _enabled = value; _revision++; } }
    }

    public static ulong Revision { get { lock (Gate) return _revision; } }

    /// <summary>Current published ringworld_vegetation_haze_state (x grass live, y trees live).</summary>
    public static Vector4 State { get { lock (Gate) return _published; } }

    /// <summary>Lazily creates the channel's Texture2DRD and registers it once as its shader global. The render thread attaches the RD texture later.</summary>
    public static Texture2Drd ChannelTexture(Channel channel)
    {
        if (channel is not (Channel.Grass or Channel.Trees)) throw new ArgumentOutOfRangeException(nameof(channel));
        int index = (int)channel;
        lock (Gate)
        {
            if (Textures[index] is { } existing) return existing;
            var texture = new Texture2Drd();
            Textures[index] = texture;
            RenderingServer.GlobalShaderParameterSet(TextureGlobals[index], texture);
            return texture;
        }
    }

    /// <summary>Claims the channel for owner when live; clears the live flag when the current owner stops. Publishes the state only on change.</summary>
    public static void SetChannelLive(Channel channel, object owner, bool live)
    {
        if (channel is not (Channel.Grass or Channel.Trees)) return;
        int index = (int)channel;
        lock (Gate)
        {
            if (live) { Owners[index] = owner; Live[index] = true; }
            else if (ReferenceEquals(Owners[index], owner)) Live[index] = false;
            else return;
            Publish();
        }
    }

    public static void ReleaseChannel(Channel channel, object owner)
    {
        if (channel is not (Channel.Grass or Channel.Trees)) return;
        int index = (int)channel;
        lock (Gate)
        {
            if (!ReferenceEquals(Owners[index], owner)) return;
            Owners[index] = null; Live[index] = false;
            Publish();
        }
    }

    public static bool IsChannelOwner(Channel channel, object owner)
    {
        if (channel is not (Channel.Grass or Channel.Trees)) return false;
        lock (Gate) return ReferenceEquals(Owners[(int)channel], owner);
    }

    private static void Publish()
    {
        var state = new Vector4(Live[0] ? 1.0f : 0.0f, Live[1] ? 1.0f : 0.0f, 0.0f, 0.0f);
        if (state == _published) return;
        _published = state;
        RenderingServer.GlobalShaderParameterSet(StateGlobal, state);
    }

    public static RingVegetationHazeFrame CreateFrame(RingAtmosphereRayFrame frame, double radiusMeters)
    {
        bool finite = frame.Valid && frame.CameraWorld.IsFinite() && float.IsFinite(frame.CameraHeightMeters)
            && frame.OutwardWorld.IsFinite() && frame.OutwardWorld.LengthSquared() > 0.000001f
            && frame.AxisWorld.IsFinite() && frame.AxisWorld.LengthSquared() > 0.000001f
            && float.IsFinite(frame.SidewallNegativeMeters) && float.IsFinite(frame.SidewallPositiveMeters)
            && float.IsFinite(frame.CoordinateScale) && frame.CoordinateScale > 0.0f
            && frame.ToSun.IsFinite() && frame.ToSun.LengthSquared() > 0.000001f;
        bool enabled = Enabled && RingAtmosphereModel.Enabled && RingAtmosphereModel.HazeModel == RingAtmosphereModel.HazeModelKind.Physical
            && finite && radiusMeters > 1.0 && Math.Abs(frame.CoordinateScale - 1.0f) < 1e-6f;
        ulong key = Mix(Mix(Mix(Mix(Mix(0x9E3779B97F4A7C15UL, RingAtmosphereModel.Revision), RingAtmosphereModel.StormRevision),
            Revision), (ulong)(int)RingAtmosphereModel.HazeModel), (ulong)BitConverter.DoubleToInt64Bits(radiusMeters));
        if (!enabled) return new RingVegetationHazeFrame(false, frame.CameraWorld, frame.ToSun, Array.Empty<float>(), key);
        Vector3 sigmaR = RingAtmosphereModel.HazeSigmaRayleigh;
        Vector4 storm = RingAtmosphereModel.HazeStormVector;
        Vector3 outward = frame.OutwardWorld.Normalized(), axis = frame.AxisWorld.Normalized(), toSun = frame.ToSun.Normalized();
        float[] parameters =
        {
            sigmaR.X, sigmaR.Y, sigmaR.Z, RingAtmosphereModel.HazeRayleighHeightMeters,
            RingAtmosphereModel.HazeSigmaAerosol, RingAtmosphereModel.HazeOmegaAerosol, RingAtmosphereModel.HazeAerosolHeightMeters, RingAtmosphereModel.HazeSigmaMist,
            RingAtmosphereModel.HazeOmegaMist, RingAtmosphereModel.HazeMistHeightMeters, RingAtmosphereModel.HazeCapHeightMeters, RingAtmosphereModel.HazeG,
            (float)radiusMeters, RingAtmosphereModel.FloorDepthMeters, frame.CameraHeightMeters, RingAtmosphereModel.HazeQuality,
            frame.SidewallNegativeMeters, frame.SidewallPositiveMeters, frame.CoordinateScale, 0.0f,
            outward.X, outward.Y, outward.Z, 0.0f,
            axis.X, axis.Y, axis.Z, 0.0f,
            toSun.X, toSun.Y, toSun.Z, 0.0f,
            frame.CameraWorld.X, frame.CameraWorld.Y, frame.CameraWorld.Z, 0.0f,
            storm.X, storm.Y, storm.Z, storm.W,
        };
        return new RingVegetationHazeFrame(true, frame.CameraWorld, frame.ToSun, parameters, key);
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value + 0x9E3779B97F4A7C15UL + (hash << 6) + (hash >> 2);
        hash *= 0xFF51AFD7ED558CCDUL;
        return hash ^ (hash >> 33);
    }

    /// <summary>Reads the ray frame a material already holds (same uniforms RingAtmosphereRayFrame.Apply writes) and ring_radius_meters.</summary>
    public static bool TryReadRayFrame(ShaderMaterial material, out RingAtmosphereRayFrame frame, out double radiusMeters)
    {
        frame = RingAtmosphereRayFrame.Invalid;
        radiusMeters = 0.0;
        if (material is null) return false;
        Variant valid = material.GetShaderParameter("ring_atmosphere_ray_frame_valid");
        Variant camera = material.GetShaderParameter("ring_atmosphere_camera_world_position");
        Variant height = material.GetShaderParameter("ring_atmosphere_camera_height_meters");
        Variant outward = material.GetShaderParameter("ring_atmosphere_outward_world");
        Variant axis = material.GetShaderParameter("ring_atmosphere_axis_world");
        Variant negative = material.GetShaderParameter("ring_atmosphere_sidewall_negative_meters");
        Variant positive = material.GetShaderParameter("ring_atmosphere_sidewall_positive_meters");
        Variant scale = material.GetShaderParameter("ring_atmosphere_coordinate_scale");
        Variant toSun = material.GetShaderParameter("ring_atmosphere_to_sun");
        Variant radius = material.GetShaderParameter("ring_radius_meters");
        if (valid.VariantType != Variant.Type.Bool || camera.VariantType != Variant.Type.Vector3 || height.VariantType != Variant.Type.Float
            || outward.VariantType != Variant.Type.Vector3 || axis.VariantType != Variant.Type.Vector3 || negative.VariantType != Variant.Type.Float
            || positive.VariantType != Variant.Type.Float || scale.VariantType != Variant.Type.Float || toSun.VariantType != Variant.Type.Vector3
            || radius.VariantType != Variant.Type.Float || !valid.AsBool()) return false;
        frame = new RingAtmosphereRayFrame(true, camera.AsVector3(), height.AsSingle(), outward.AsVector3(), axis.AsVector3(),
            negative.AsSingle(), positive.AsSingle(), scale.AsSingle(), toSun.AsVector3());
        radiusMeters = radius.AsDouble();
        return true;
    }
}
