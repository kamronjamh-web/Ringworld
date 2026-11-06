using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// Ripples with the camera ABOVE the field (docs/containment-field-design.md 3.7, 3.10.2; WO4): the ripple patches are drawn after Sunshine by the composite
/// (scripts/render/ring_containment_ripple_post.glsl) from the same built patches and per-slot instance values as the spatial path, and the near patch's post version integrates its
/// overlapping ripples (see <c>PublishNearRipplesPost</c>). Vertex records are published on every patch rebuild (<see cref="PublishRippleRecords"/>); the frame state per frame.
/// </summary>
public partial class RingContainmentField
{
    private const int PostSlotFloats = 16, PostSharedFloats = 36, PostParamFloats = 92;
    private static readonly StringName PublishRippleMeshName = "publish_ripple_mesh";
    private static readonly StringName PublishRipplesName = "publish_ripples";

    private readonly Vector3[] _postAnchors = new Vector3[RipplePoolSize];
    private readonly float[] _postSlotParams = new float[RipplePoolSize * PostSlotFloats];
    private readonly float[] _postShared = new float[PostSharedFloats];
    private bool _ripplesPostPublished;
    private Vector3 _postAnchorWorld;

    /// <summary>Ripple patch slots drawn by the post pass this frame (camera above the field).</summary>
    public int RipplePostDrawn { get; private set; }
    /// <summary>Ripple slot draws the composite has issued (monotonic).</summary>
    public long RipplePostDispatches => _composite?.Get("ripple_dispatches").AsInt64() ?? 0;

    partial void PublishRipplesAbove(Camera3D camera, in RingLodGeometrySnapshot snapshot, in ContainmentFieldProfile profile, IReadOnlyList<ContainmentRipple?> slots,
        ContainmentRippleRenderState[] states, IReadOnlyList<ContainmentFarStrike>? farStrikes, double now, in FieldLighting lighting, float seamFlowPhase, float seamEnergyScale)
    {
        int count = 0;
        for (int i = 0; i < RipplePoolSize; i++)
            if (_rippleReady[i] && _ripplePool[i]?.Patch != null) count++;
        if (count == 0 || _grid == null) { RipplePostDrawn = 0; PublishRipplesInactive(); return; }
        EnsureComposite();
        if (_composite == null || !EnsureCompositorSlot()) { RipplePostDrawn = 0; return; }

        var list = new int[count];
        int n = 0;
        Array.Clear(_postSlotParams);
        for (int i = 0; i < RipplePoolSize; i++)
        {
            RipplePatchSlot? ps = _ripplePool[i];
            if (!_rippleReady[i] || ps?.Patch is not { } patch) continue;
            ContainmentRippleRenderState state = states[i];
            ContainmentFieldPoint anchor = patch.Anchor;
            _postAnchors[i] = snapshot.CanonicalToWorld(anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters);
            Vector2 lattice = ContainmentFieldNearPatch.LatticeOrigin(patch);
            int o = i * PostSlotFloats;
            _postSlotParams[o] = state.Wave.X; _postSlotParams[o + 1] = state.Wave.Y; _postSlotParams[o + 2] = state.Wave.Z; _postSlotParams[o + 3] = state.Wave.W;
            _postSlotParams[o + 4] = state.Crawl.X; _postSlotParams[o + 5] = state.Crawl.Y; _postSlotParams[o + 6] = state.Crawl.Z; _postSlotParams[o + 7] = state.Crawl.W;
            _postSlotParams[o + 8] = lattice.X; _postSlotParams[o + 9] = lattice.Y; _postSlotParams[o + 10] = (float)patch.SurfaceMetresPerUvX; _postSlotParams[o + 11] = state.StrikeLevel;
            _postSlotParams[o + 12] = _rippleNear[i] ? 1.0f : 0.0f;
            list[n++] = i;
        }

        (double fadeStart, double fadeEnd) = ContainmentRipplePatches.FarFade(camera.Far);
        Array.Clear(_postShared);
        _postShared[0] = ContainmentRippleShaderMaxPx; _postShared[1] = DebugMode;
        _postShared[4] = SheenStrength; _postShared[5] = LatticeStrength; _postShared[6] = (float)(ShimmerRangeKm * 1000.0); _postShared[7] = NearRippleCount > 0 ? 1.0f : 0.0f;
        _postShared[8] = (float)fadeStart; _postShared[9] = (float)fadeEnd;
        PackShared(12, lighting.ToSun); PackShared(16, lighting.Up); PackShared(20, lighting.SunIrradiance); PackShared(24, lighting.SkyRadiance); PackShared(28, lighting.GroundRadiance);
        _postShared[32] = seamFlowPhase; _postShared[33] = seamEnergyScale;
        _composite.Call(PublishRipplesName, true, _postAnchors, _postSlotParams, _postShared, list);
        _ripplesPostPublished = true;
        RipplePostDrawn = count;
    }

    private void PackShared(int index, Vector3 v) { _postShared[index] = v.X; _postShared[index + 1] = v.Y; _postShared[index + 2] = v.Z; _postShared[index + 3] = 0.0f; }

    /// <summary>Tells the composite the ripples are not drawn above (below the field, idle, none ready); once, not every frame.</summary>
    private void PublishRipplesInactive()
    {
        if (!_ripplesPostPublished) return;
        _ripplesPostPublished = false;
        _composite?.Call(PublishRipplesName, false, _postAnchors, _postSlotParams, _postShared, Array.Empty<int>());
    }

    /// <summary>
    /// The near patch's post pass draws its overlapping ripples itself: the selected ripples go into the near frame parameters (ripple_info, ripples[12]; the same values as the spatial
    /// <c>cf_near_ripples</c>) and the frame is published again. With no ripple the parameters ProcessNearPatch already published (count 0) stand.
    /// </summary>
    private void PublishNearRipplesPost(int nearCount)
    {
        if (nearCount <= 0 || !_compositeActive || _composite == null) return;
        _frameParams[40] = nearCount; _frameParams[41] = ContainmentRippleShaderMaxPx;
        for (int k = 0; k < nearCount * 3; k++)
        {
            Vector4 v = _nearRippleUniform[k];
            int o = 44 + 4 * k;
            _frameParams[o] = v.X; _frameParams[o + 1] = v.Y; _frameParams[o + 2] = v.Z; _frameParams[o + 3] = v.W;
        }
        _composite.Call(PublishFrameName, _postAnchorWorld, _frameParams, true);
    }

    /// <summary>Publishes a rebuilt ripple patch's vertex records (the near patch's 48-byte layout) to the composite.</summary>
    private void PublishRippleRecords(int slot, ContainmentNearPatchMesh patch)
    {
        EnsureComposite();
        if (_composite == null) return;
        int count = patch.Positions.Length;
        var floats = new float[count * 12];
        for (int k = 0; k < count; k++)
        {
            Vector3 p = patch.Positions[k], n = patch.Normals[k], t = patch.Tangents[k];
            Vector2 uv = patch.Uv[k], uv2 = patch.Uv2[k];
            int o = k * 12;
            floats[o] = p.X; floats[o + 1] = p.Y; floats[o + 2] = p.Z; floats[o + 3] = uv2.X;
            floats[o + 4] = n.X; floats[o + 5] = n.Y; floats[o + 6] = n.Z; floats[o + 7] = uv.X;
            floats[o + 8] = t.X; floats[o + 9] = t.Y; floats[o + 10] = t.Z; floats[o + 11] = uv.Y;
        }
        byte[] raw = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, raw, 0, raw.Length);
        _composite.Call(PublishRippleMeshName, slot, raw, patch.QuadsPerSide);
    }
}
