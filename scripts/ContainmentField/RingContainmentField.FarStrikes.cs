using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// The far strike layer's driver (docs/containment-field-design.md 3.10.3, WO4): the light of ripples that reach past the main camera's far fade band and of every far-list strike, in the
/// scaled far viewport (<see cref="RingContainmentFarStrikeLayer"/>), crossfading per fragment with the main-view patch. Runs every frame with ripples or far strikes, on either side of the field.
/// </summary>
public partial class RingContainmentField
{
    /// <summary>The scaled far viewport the far strike layer lives in.</summary>
    [Export] public NodePath FarViewportPath { get; set; } = new("../ScaledFarViewport");

    private const ulong FarBindRetryMilliseconds = 1000;
    private RingScaledFarViewport? _farViewport;
    private RingContainmentFarStrikeLayer? _farStrikeLayer;
    private ulong _farBindNextMsec;
    private float _farFadeCameraFar = -1.0f;

    /// <summary>Far strike layer quads drawn this frame (far-reaching ripples plus lit far-list strikes).</summary>
    public int FarStrikeQuadsDrawn { get; private set; }
    public RingContainmentFarStrikeLayer? FarStrikeLayer => _farStrikeLayer;

    /// <summary>Binds the layer lazily once the scaled world exists; retries about every second while unbound.</summary>
    private bool EnsureFarStrikeLayer()
    {
        if (_farStrikeLayer != null && GodotObject.IsInstanceValid(_farStrikeLayer) && _farViewport != null && GodotObject.IsInstanceValid(_farViewport)) return true;
        ulong now = Time.GetTicksMsec();
        if (now < _farBindNextMsec) return false;
        _farBindNextMsec = now + FarBindRetryMilliseconds;
        _farStrikeLayer = null;
        if (_farViewport == null || !GodotObject.IsInstanceValid(_farViewport)) _farViewport = GetNodeOrNull<RingScaledFarViewport>(FarViewportPath);
        if (_farViewport == null || !_farViewport.HasScaledWorld) return false;
        var layer = new RingContainmentFarStrikeLayer { Name = "ContainmentFarStrikes" };
        if (!layer.Build(_farViewport.CoordinateScale) || !_farViewport.AddScaledWorldChild(layer))
        {
            layer.Free();
            return false;
        }
        _farStrikeLayer = layer;
        _farFadeCameraFar = -1.0f;
        return true;
    }

    private void ClearFarStrikes()
    {
        FarStrikeQuadsDrawn = 0;
        if (_farStrikeLayer != null && GodotObject.IsInstanceValid(_farStrikeLayer)) _farStrikeLayer.Clear();
    }

    private void FreeFarStrikeLayer()
    {
        FarStrikeQuadsDrawn = 0;
        if (_farStrikeLayer != null && GodotObject.IsInstanceValid(_farStrikeLayer)) _farStrikeLayer.QueueFree();
        _farStrikeLayer = null;
    }

    /// <summary>
    /// Instances: every pool slot whose patch reaches past the far fade start (<see cref="ContainmentRipplePatches.NeedsFarQuad"/>) plus every far-list strike with a lit flash. A and the strike level are
    /// multiplied by the air transmittance observer -> contact (3.10.3). <paramref name="farFlashBase"/> is the index of the first far strike in <c>_flashScratch</c> (the capped flashes).
    /// </summary>
    private void UpdateFarStrikes(Camera3D camera, in RingLodGeometrySnapshot snapshot, in RingCanonicalPosition cameraCanonical, IReadOnlyList<ContainmentRipple?> slots, int slotCount,
        IReadOnlyList<ContainmentFarStrike>? far, int farCount, int farFlashBase, double now)
    {
        if (!EnsureFarStrikeLayer() || _farStrikeLayer == null) { FarStrikeQuadsDrawn = 0; return; }
        RingContainmentFarStrikeLayer layer = _farStrikeLayer;
        double radius = snapshot.RingRadiusMeters, circumference = snapshot.CircumferenceMeters;
        (double fadeStart, double fadeEnd) = ContainmentRipplePatches.FarFade(camera.Far);
        if (camera.Far != _farFadeCameraFar) { layer.SetFarFade(new Vector2((float)fadeStart, (float)fadeEnd)); _farFadeCameraFar = camera.Far; }
        layer.Begin(slotCount + farCount);
        for (int i = 0; i < slotCount; i++)
        {
            ContainmentRipple? r = slots[i];
            if (r == null) continue;
            ContainmentRippleRenderState state = _rippleStates[i];
            if (!state.Visible) continue;
            var contact = new RingCanonicalPosition(r.Surface.AroundMeters, r.Surface.VerticalMeters, r.Surface.AcrossMeters);
            double centre = ContainmentRipplePatches.ChordDistance(cameraCanonical, contact, radius, circumference);
            if (!ContainmentRipplePatches.NeedsFarQuad(centre, ContainmentRipplePatches.RenderPatchRadius(r), fadeStart, state)) continue;
            AddFarInstance(layer, r.Surface, state, cameraCanonical, contact, radius, circumference, false);
        }
        for (int k = 0; k < farCount; k++)
        {
            ContainmentFarStrike f = far![k];
            float flash = _flashScratch[farFlashBase + k];
            if (!(flash > 0.0f)) continue;
            ContainmentRippleRenderState state = ContainmentRipplePatches.EvaluateFar(f, now, flash, RippleStrength, LightningVisualPolicy.ReducedFlashes);
            if (!state.Visible || !(state.StrikeLevel > 0.0f)) continue;
            var contact = new RingCanonicalPosition(f.Surface.AroundMeters, f.Surface.VerticalMeters, f.Surface.AcrossMeters);
            AddFarInstance(layer, f.Surface, state, cameraCanonical, contact, radius, circumference, true);
        }
        layer.End();
        FarStrikeQuadsDrawn = layer.DrawnCount;
    }

    private void AddFarInstance(RingContainmentFarStrikeLayer layer, in ContainmentFieldPoint surface, in ContainmentRippleRenderState state, in RingCanonicalPosition observer,
        in RingCanonicalPosition contact, double radius, double circumference, bool farList)
    {
        if (_farViewport == null) return;
        float transmittance = UpperAtmosphereLightning.PathTransmittance(observer, contact, radius, circumference);
        if (!(transmittance > 0.0f)) return;
        float scale = layer.CoordinateScale;
        double wrapped = surface.AroundMeters % circumference;
        if (wrapped < 0.0) wrapped += circumference;
        (double sin, double cos) = Math.SinCos(wrapped / radius);
        var around = new Vector3((float)-sin, 0.0f, (float)cos);
        var up = new Vector3((float)-cos, 0.0f, (float)-sin);
        var across = new Vector3(0.0f, 1.0f, 0.0f);
        Vector3 normal = up * (float)surface.NormalVertical + across * (float)surface.NormalAcross;
        Vector3 arch = up * (float)surface.NormalAcross - across * (float)surface.NormalVertical;
        float rmax = state.Wave.Z, rc = state.Crawl.X;
        float p = ContainmentRipplePatches.PatchFactor * Math.Max(rmax, rc) * scale;
        Vector3 origin = _farViewport.ScaledPoint(surface.AroundMeters, surface.VerticalMeters, surface.AcrossMeters);
        var transform = new Transform3D(new Basis(around * p, arch * p, normal), origin);
        float bias = state.Crawl.W;
        // seed x 256 + far-list flag x 128 + 7-bit rim bias (exact in float32 up to 2^24). A far-list strike (> 350 km) is never drawn by the main view, so the
        // far shader draws it at full weight; a pool ripple is crossfaded with its main-view patch by distance (the far fade band near the camera far plane).
        float packedSeed = state.Crawl.Z * 256.0f + (farList ? 128.0f : 0.0f) + MathF.Round((Math.Clamp(bias, -1.0f, 1.0f) + 1.0f) * 63.5f);
        var custom = new Color(state.StrikeLevel * transmittance, rc, state.Crawl.Y, packedSeed);
        var wave = new Color(state.Wave.X * transmittance, state.Wave.Y, rmax, state.Wave.W);
        layer.Add(transform, custom, wave);
    }
}
