using System;
using Godot;
using RingworldTechDemo.Atmosphere;

namespace RingworldTechDemo.Shadows;

/// <summary>Snapshot the vegetation compute needs to write per-instance shadow terms: the 108-float std140 block (see materials/vegetation_gpu/ring_vegetation_shadow_terms.glslinc,
/// block ShadowFrame) plus the render-device textures the block refers to. Key changes whenever an input the stored terms depend on changes except the camera and
/// the cloud scroll (callers threshold those themselves); Crossfading is true while two heightfield layers are mixed (every frame changes the mix).</summary>
public readonly record struct RingVegetationShadowFrame(bool Enabled, Vector3 CameraWorld, float[] Parameters, ulong Key, bool Crossfading, bool CloudActive,
    Rid HeightL0, Rid HeightL1, Rid CloudNear, Rid CloudFar, int LightId);

/// <summary>
/// Host of the per-instance vegetation shadow atlases (docs/shadow-revamp-plan.md S7b), the sibling of <see cref="RingVegetationHaze"/>. The GPU vegetation compute
/// (RingGpuVegetationCompute) writes one RGBA16F atlas per compute instance next to its haze atlas (same slot index and tag); this class owns the two channel
/// Texture2DRD wrappers published as the globals ringworld_vegetation_shadow_grass / _trees and the vec4 ringworld_vegetation_shadow_state (x grass live, y trees live,
/// z unified compose). Consumers fall back to the per-vertex terms whenever a channel is not live. Default OFF ("Per-instance vegetation shadows").
/// </summary>
public static class RingVegetationShadow
{
    public const int ParameterFloats = 108, DefaultRefreshHz = 10, MinimumRefreshHz = 2, MaximumRefreshHz = 30;
    public const string TermsIncludePath = "res://materials/vegetation_gpu/ring_vegetation_shadow_terms.glslinc";
    private static readonly StringName[] TextureGlobals = { "ringworld_vegetation_shadow_grass", "ringworld_vegetation_shadow_trees" };
    private static readonly StringName StateGlobal = "ringworld_vegetation_shadow_state";

    private static readonly object Gate = new();
    private static readonly Texture2Drd?[] Textures = new Texture2Drd?[2];
    private static readonly object?[] Owners = new object?[2];
    private static readonly bool[] Live = new bool[2];
    private static Vector4 _published;
    private static bool _enabled, _unified;
    private static int _refreshHz = DefaultRefreshHz;
    private static ulong _revision;

    /// <summary>Live-tuning toggle ("Per-instance vegetation shadows", default off). A different value bumps <see cref="Revision"/>.</summary>
    public static bool Enabled
    {
        get { lock (Gate) return _enabled; }
        set { lock (Gate) { if (_enabled == value) return; _enabled = value; _revision++; } }
    }

    /// <summary>Look-change key (VegetationUnifiedShadowCompose, default off): trees and standing grass compose their shadow terms like the other receivers.</summary>
    public static bool UnifiedCompose
    {
        get { lock (Gate) return _unified; }
        set { lock (Gate) { if (_unified == value) return; _unified = value; _revision++; Publish(); } }
    }

    /// <summary>Cloud refresh rate (Hz): already-compacted rows get their cloud terms rewritten this often while a cloud map is shown.</summary>
    public static int RefreshHz
    {
        get { lock (Gate) return _refreshHz; }
        set { lock (Gate) _refreshHz = Math.Clamp(value, MinimumRefreshHz, MaximumRefreshHz); }
    }

    public static ulong Revision { get { lock (Gate) return _revision; } }
    public static Vector4 State { get { lock (Gate) return _published; } }

    public static Texture2Drd ChannelTexture(RingVegetationHaze.Channel channel)
    {
        if (channel is not (RingVegetationHaze.Channel.Grass or RingVegetationHaze.Channel.Trees)) throw new ArgumentOutOfRangeException(nameof(channel));
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

    public static void SetChannelLive(RingVegetationHaze.Channel channel, object owner, bool live)
    {
        if (channel is not (RingVegetationHaze.Channel.Grass or RingVegetationHaze.Channel.Trees)) return;
        int index = (int)channel;
        lock (Gate)
        {
            if (live) { Owners[index] = owner; Live[index] = true; }
            else if (ReferenceEquals(Owners[index], owner)) Live[index] = false;
            else return;
            Publish();
        }
    }

    public static void ReleaseChannel(RingVegetationHaze.Channel channel, object owner)
    {
        if (channel is not (RingVegetationHaze.Channel.Grass or RingVegetationHaze.Channel.Trees)) return;
        int index = (int)channel;
        lock (Gate)
        {
            if (!ReferenceEquals(Owners[index], owner)) return;
            Owners[index] = null; Live[index] = false;
            Publish();
        }
    }

    public static bool IsChannelOwner(RingVegetationHaze.Channel channel, object owner)
    {
        if (channel is not (RingVegetationHaze.Channel.Grass or RingVegetationHaze.Channel.Trees)) return false;
        lock (Gate) return ReferenceEquals(Owners[(int)channel], owner);
    }

    private static void Publish()
    {
        var state = new Vector4(Live[0] ? 1.0f : 0.0f, Live[1] ? 1.0f : 0.0f, _unified ? 1.0f : 0.0f, 0.0f);
        if (state == _published) return;
        _published = state;
        RenderingServer.GlobalShaderParameterSet(StateGlobal, state);
    }

    private static ulong _cachedFrameId = ulong.MaxValue, _cachedRevision;
    private static RingVegetationShadowFrame _cachedFrame;

    /// <summary>Builds the frame snapshot from the height-shadow node's current globals and the cloud projector's current receiver frame. Cached per process frame
    /// (the grass and tree layers each ask once; the second caller gets the first caller's snapshot).</summary>
    public static RingVegetationShadowFrame CreateFrame(Vector3 cameraWorld)
    {
        ulong id = Engine.GetProcessFrames();
        ulong revision = Revision;
        if (_cachedFrameId == id && _cachedRevision == revision) return _cachedFrame;
        RingVegetationShadowFrame frame;
        if (!Enabled) frame = Build(false, cameraWorld, RingTerrainHeightShadow.GlobalsSnapshot.Empty, default);
        else
        {
            RingTerrainHeightShadow.GlobalsSnapshot height = RingTerrainHeightShadow.Instance is { } node && GodotObject.IsInstanceValid(node)
                ? node.GetGlobalsSnapshot() : RingTerrainHeightShadow.GlobalsSnapshot.Empty;
            frame = Build(true, cameraWorld, height, RingCloudShadowProjector.GetVegetationFrame());
        }
        _cachedFrameId = id; _cachedRevision = revision; _cachedFrame = frame;
        return frame;
    }

    /// <summary>Pure assembly (also used by the test fixtures with synthetic inputs).</summary>
    public static RingVegetationShadowFrame Build(bool enabled, Vector3 cameraWorld, RingTerrainHeightShadow.GlobalsSnapshot height, RingCloudShadowProjector.VegetationFrame cloud)
    {
        bool usable = enabled && cameraWorld.IsFinite();
        if (!usable) return new RingVegetationShadowFrame(false, cameraWorld, Array.Empty<float>(), 0, false, false, default, default, default, default, 0);
        var data = new float[ParameterFloats];
        int at = 0;
        Put(data, ref at, height.Frame.X); Put(data, ref at, height.Frame.Y); Put(data, ref at, height.Frame.Z); Put(data, ref at, height.Frame.W);
        Put(data, ref at, height.L0Params); Put(data, ref at, height.L1Params);
        Put(data, ref at, height.L0Bounds); Put(data, ref at, height.L0PrevBounds); Put(data, ref at, height.L1Bounds); Put(data, ref at, height.L1PrevBounds);
        Put(data, ref at, height.State); Put(data, ref at, height.Shape); Put(data, ref at, height.Light);
        Put(data, ref at, new Vector4(cameraWorld.X, cameraWorld.Y, cameraWorld.Z, 0f));
        bool anyCloud = cloud.NearEnabled || cloud.FarEnabled;
        Put(data, ref at, new Vector4(cloud.Enabled && anyCloud ? 1f : 0f, cloud.Enabled && cloud.NearEnabled ? 1f : 0f, cloud.Enabled && cloud.FarEnabled ? 1f : 0f, cloud.HeightProjection ? 1f : 0f));
        Put(data, ref at, cloud.NearCenter); Put(data, ref at, cloud.NearAround); Put(data, ref at, cloud.NearAcross); Put(data, ref at, cloud.NearUp);
        Put(data, ref at, cloud.FarCenter); Put(data, ref at, cloud.FarAround); Put(data, ref at, cloud.FarAcross); Put(data, ref at, cloud.FarUp);
        Put(data, ref at, new Vector4(cloud.FarLink.X, cloud.FarLink.Y, cloud.FarLink.Z, 0f));
        Put(data, ref at, new Vector4(cloud.NearUvOffset.X, cloud.NearUvOffset.Y, cloud.FarUvOffset.X, cloud.FarUvOffset.Y));
        Put(data, ref at, new Vector4(cloud.NearRadius, cloud.FarRadius, cloud.RingRadius, 0f));
        Put(data, ref at, new Vector4(cloud.SunDirection.X, cloud.SunDirection.Y, cloud.SunDirection.Z, 0f));
        for (int i = 0; i < data.Length; i++) if (!float.IsFinite(data[i])) data[i] = 0f;
        ulong key = Mix(0x9E3779B97F4A7C15UL, Revision);
        foreach (Vector4 v in new[] { height.State with { Z = 0f }, height.L0Params, height.L1Params, height.L0Bounds, height.L0PrevBounds, height.L1Bounds, height.L1PrevBounds,
            height.Shape, height.Light })
            key = Mix(Mix(Mix(Mix(key, Bits(v.X)), Bits(v.Y)), Bits(v.Z)), Bits(v.W));
        // Cloud configuration only: the centres, offsets and sun direction move every frame (the refresh rate covers them).
        key = Mix(Mix(Mix(Mix(key, cloud.Enabled ? 1UL : 0UL), (cloud.NearEnabled ? 2UL : 0UL) | (cloud.FarEnabled ? 4UL : 0UL) | (cloud.HeightProjection ? 8UL : 0UL) | (cloud.FarLink.Z > 0.5f ? 16UL : 0UL)),
            Bits(cloud.NearRadius) ^ (Bits(cloud.FarRadius) << 7)), Bits(cloud.RingRadius));
        key = Mix(Mix(Mix(Mix(key, (ulong)height.L0.Id), (ulong)height.L1.Id), (ulong)cloud.NearRid.Id), (ulong)cloud.FarRid.Id);
        bool crossfading = height.State.Y > -0.5f && height.State.Z < 1f;
        int light = Math.Clamp((int)MathF.Round(height.State.W), 0, 2);
        return new RingVegetationShadowFrame(true, cameraWorld, data, key, crossfading, data[56] > 0.5f, height.L0, height.L1, cloud.NearRid, cloud.FarRid, light);
    }

    private static void Put(float[] data, ref int at, Vector4 v) { data[at++] = v.X; data[at++] = v.Y; data[at++] = v.Z; data[at++] = v.W; }
    private static void Put(float[] data, ref int at, Vector3 v) { Put(data, ref at, new Vector4(v.X, v.Y, v.Z, 0f)); }
    private static ulong Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value + 0x9E3779B97F4A7C15UL + (hash << 6) + (hash >> 2);
        hash *= 0xFF51AFD7ED558CCDUL;
        return hash ^ (hash >> 33);
    }
}
