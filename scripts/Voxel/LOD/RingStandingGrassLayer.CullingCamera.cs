using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    /// <summary>Re-culls grass against a latched, padded camera instead of the exact camera each frame. The latch is
    /// refreshed when the camera moves or turns past the thresholds below, so small camera motion reuses the previous
    /// CPU and GPU culling results. Off restores exact per-frame culling. Shader fades always follow the live camera.</summary>
    [Export] public bool ThrottleGrassCulling { get; set; } = true;
    /// <summary>Camera travel (m) since the last latch that forces a re-cull. 0 re-culls on any movement.</summary>
    [Export(PropertyHint.Range, "0,4,0.01")] public float GrassCullingMovementThresholdMeters { get; set; } = 1f;
    /// <summary>Camera turn (degrees) since the last latch that forces a re-cull. Capped below the angular padding. 0 re-culls on any turn.</summary>
    [Export(PropertyHint.Range, "0,15,0.1")] public float GrassCullingRotationThresholdDegrees { get; set; } = 3f;
    /// <summary>Extra view-edge widening (degrees per side) of the latched view, so grass just off-screen stays ready while turning.</summary>
    [Export(PropertyHint.Range, "0,20,0.1")] public float GrassCullingAngularPaddingDegrees { get; set; } = 5f;
    /// <summary>Longest time (s) a moved camera may keep using the previous culling. 0 re-culls every frame the camera changes.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float GrassCullingMaximumIntervalSeconds { get; set; } = .25f;

    /// <summary>Times the latched culling camera was refreshed (a counter for measuring re-cull rate).</summary>
    public long GrassCullingRelatches { get; private set; }
    public string GrassCullingLastRelatchReason { get; private set; } = "none";

    // The culled set must remain a superset of what the live camera can see. A turn of at most R degrees is covered by
    // widening the latched view by A degrees (R is clamped half a degree below A, so a numerically exact turn of R is still
    // inside), and a move of at most M metres is covered by pushing the latched planes out by M. Cards additionally cap M by
    // the GPU distance margin, because card culling measures distance from the latched position.
    internal float EffectiveGrassCullingAngularPaddingDegrees => ThrottleGrassCulling
        ? Mathf.Clamp(float.IsFinite(GrassCullingAngularPaddingDegrees) ? GrassCullingAngularPaddingDegrees : 0f, 0f, 20f) : 0f;
    internal float EffectiveGrassCullingRotationThresholdDegrees => ThrottleGrassCulling
        ? Mathf.Min(Mathf.Clamp(float.IsFinite(GrassCullingRotationThresholdDegrees) ? GrassCullingRotationThresholdDegrees : 0f, 0f, 15f),
            Math.Max(0f, EffectiveGrassCullingAngularPaddingDegrees - .5f)) : 0f;
    internal float EffectiveGrassCullingMovementMeters
    {
        get
        {
            if (!ThrottleGrassCulling) return 0f;
            float movement = Mathf.Clamp(float.IsFinite(GrassCullingMovementThresholdMeters) ? GrassCullingMovementThresholdMeters : 0f, 0f, 4f);
            if (_style.IsCards)
                movement = Math.Min(movement, float.IsFinite(GrassCardComputeMarginMeters) ? Mathf.Clamp(GrassCardComputeMarginMeters, 0f, 8f) : 2f);
            return movement;
        }
    }
    private float EffectiveGrassCullingIntervalSeconds => ThrottleGrassCulling
        ? Mathf.Clamp(float.IsFinite(GrassCullingMaximumIntervalSeconds) ? GrassCullingMaximumIntervalSeconds : 0f, 0f, 1f) : 0f;

    private readonly record struct GrassCullLatchSettings(bool Throttle, float Movement, float Rotation, float Padding, float Interval);

    private bool _cullLatchValid;
    private GpuCameraState _cullState;
    private Transform3D _cullLayer;
    private GrassCullLatchSettings _cullSettings;
    private ulong _cullLatchUsec;
    private Vector3 _cullPosition;
    private Vector4[]? _cullFrustum;
    private Plane[]? _cullPlanes;

    /// <summary>The latched camera state that every grass culling consumer reads.</summary>
    private GpuCameraState LatchedGrassCameraState(Camera3D camera)
    {
        EvaluateGrassCullingCamera(camera);
        return _cullState;
    }

    private Vector3 LatchedGrassCameraPosition(Camera3D camera)
    {
        EvaluateGrassCullingCamera(camera);
        return _cullPosition;
    }

    private Vector4[] CachedCameraFrustum(Camera3D camera)
    {
        EvaluateGrassCullingCamera(camera);
        return _cullFrustum!;
    }

    private Plane[] CachedCameraFrustumPlanes(Camera3D camera)
    {
        EvaluateGrassCullingCamera(camera);
        return _cullPlanes!;
    }

    /// <summary>Compares the live camera with the latch and re-latches when a threshold is crossed. Idempotent within a
    /// frame: after a re-latch the latch equals the live camera, so repeated calls do nothing.</summary>
    private void EvaluateGrassCullingCamera(Camera3D camera)
    {
        GpuCameraState live = new(camera.GetInstanceId(), camera.GlobalTransform, camera.Projection, camera.KeepAspect,
            camera.Fov, camera.Size, camera.FrustumOffset, camera.Near, camera.Far, camera.HOffset, camera.VOffset,
            GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero);
        var settings = new GrassCullLatchSettings(ThrottleGrassCulling, EffectiveGrassCullingMovementMeters,
            EffectiveGrassCullingRotationThresholdDegrees, EffectiveGrassCullingAngularPaddingDegrees, EffectiveGrassCullingIntervalSeconds);
        string? reason = null;
        if (!_cullLatchValid) reason = "initial";
        else if (settings != _cullSettings) reason = "settings";
        else if (GlobalTransform != _cullLayer) reason = "layer";
        else if (live with { Transform = _cullState.Transform } != _cullState) reason = "projection";
        else if (live.Transform != _cullState.Transform)
        {
            if (!settings.Throttle) reason = "unthrottled";
            else if (live.Transform.Origin.DistanceTo(_cullState.Transform.Origin) > settings.Movement) reason = "translation";
            else if (live.Projection == Camera3D.ProjectionType.Perspective
                ? GrassCullingFrustum.BasisAngleDegrees(_cullState.Transform.Basis, live.Transform.Basis) > settings.Rotation
                : live.Transform.Basis != _cullState.Transform.Basis) reason = "rotation";
            // Only a moved camera ages out; a stationary one keeps the latch (and every cache that follows it).
            else if ((Time.GetTicksUsec() - _cullLatchUsec) * 1e-6 >= settings.Interval) reason = "interval";
        }
        if (reason == null) return;
        LatchGrassCullingCamera(camera, live, settings, reason);
    }


    private void LatchGrassCullingCamera(Camera3D camera, GpuCameraState live, GrassCullLatchSettings settings, string reason)
    {
        _cullLatchValid = true;
        _cullState = live;
        _cullLayer = GlobalTransform;
        _cullSettings = settings;
        _cullLatchUsec = Time.GetTicksUsec();
        _cullPosition = live.Transform.Origin;
        bool perspective = live.Projection == Camera3D.ProjectionType.Perspective;
        BuildLatchedFrustum(camera, live.Transform, settings.Throttle ? settings.Movement : 0f,
            perspective ? settings.Padding : 0f, perspective);
        GrassCullingRelatches++;
        GrassCullingLastRelatchReason = reason;
    }

    /// <summary>Builds the exact camera frustum, then (when throttled) widens it so any view within the movement and
    /// rotation thresholds of the latch lies inside it.</summary>
    private void BuildLatchedFrustum(Camera3D camera, Transform3D transform, float movement, float angularDegrees, bool perspective)
    {
        Godot.Collections.Array<Plane> planes = camera.GetFrustum();
        int count = planes.Count;
        if (_cullFrustum is null || _cullFrustum.Length != count) _cullFrustum = new Vector4[count];
        if (_cullPlanes is null || _cullPlanes.Length != count) _cullPlanes = new Plane[count];
        // Frustum/camera offsets move the apex off the camera origin; spend them as extra translation margin.
        if (movement > 0f || angularDegrees > 0f)
            movement += Math.Abs(camera.HOffset) + Math.Abs(camera.VOffset) + Math.Abs(camera.FrustumOffset.X) + Math.Abs(camera.FrustumOffset.Y);
        GrassCullingFrustum.Pad(planes, transform, camera.Near, camera.Far, movement, angularDegrees, perspective, _cullFrustum, _cullPlanes);
    }
}
