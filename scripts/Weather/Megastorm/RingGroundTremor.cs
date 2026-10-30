using System;
using Godot;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo;

/// <summary>
/// Publishes the megastorm ground tremor (docs/megastorm-design.md 5.2, WO6c): the <c>ring_ground_tremor</c> global shader parameter (amplitude, shiver
/// frequency, accumulated phase) that the puddle ripples, the near water surface and the vegetation gust term read, and a small camera micro-shake
/// from the same amplitude. <see cref="MegastormSample.TremorLevel"/> is exactly 0 with no megastorm; then this node never writes the global (its
/// project default is 0), never touches the camera, and costs one bool check per frame. When the tremor ends it writes the zero state once and
/// restores the camera.
/// </summary>
public partial class RingGroundTremor : Node
{
    public const string GlobalName = "ring_ground_tremor";
    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath MegastormPath { get; set; } = new("../RingMegastorm");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");

    /// <summary>Test seam: replaces the tremor level at the explorer.</summary>
    public Func<float>? TremorProvider { get; set; }

    private RingMegastorm? _megastorm;
    private RingVoxelExplorerController? _explorer;
    private Camera3D? _camera;
    private Vector3 _baseRotation;
    private bool _shaking, _published;
    private double _phase;
    private float _level, _shakeDegrees;
    private Vector3 _lastShakeOffset;

    /// <summary>The last amplitude written to the global (0 until a tremor starts).</summary>
    public float PublishedLevel => _published ? _level : 0f;
    public float ShakeDegrees => _shakeDegrees;
    /// <summary>Test hooks: how many times the global was written and the last value.</summary>
    public int WriteCount { get; private set; }
    public Vector4 LastWritten { get; private set; }
    public bool Shaking => _shaking;
    /// <summary>Largest per-frame change of the shake angle in degrees since the last reset (for the no-spike check).</summary>
    public float MaxShakeStepDegrees { get; private set; }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _megastorm = GetNodeOrNull<RingMegastorm>(MegastormPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
    }

    public override void _Process(double delta)
    {
        if (GetTree().Paused) return;
        float level = 0f;
        if (Enabled)
        {
            if (TremorProvider != null) level = TremorProvider();
            else if (_megastorm != null && _explorer != null && _megastorm.Triggered) level = _megastorm.QueryNow(_explorer.CanonicalPosition).TremorLevel;
        }
        Step(level, delta);
    }

    public override void _ExitTree() => Step(0f, 0.0);

    /// <summary>One update with a tremor level; public so a test can drive it.</summary>
    public void Step(float level, double dt)
    {
        level = float.IsFinite(level) ? Math.Clamp(level, 0f, 1f) : 0f;
        if (level <= 0f && !_published) return;
        _level = level;
        if (level > 0f)
        {
            _phase = (_phase + Math.Tau * MegastormAudioMix.ShiverHz * Math.Max(0.0, dt)) % Math.Tau;
            _published = true;
            Write(new Vector4(level, MegastormAudioMix.ShiverHz, (float)_phase, 0f));
            ApplyShake(MegastormAudioMix.ShakeDegrees(level));
            return;
        }
        // Back to rest: write the zero state once and put the camera back exactly.
        _published = false;
        _phase = 0.0;
        Write(new Vector4(0f, MegastormAudioMix.ShiverHz, 0f, 0f));
        ApplyShake(0f);
    }

    private void Write(Vector4 value)
    {
        LastWritten = value;
        WriteCount++;
        RenderingServer.GlobalShaderParameterSet(GlobalName, value);
    }

    public void ResetShakePeak() => MaxShakeStepDegrees = 0f;

    // The offset is a smooth sum of sines on the shared phase (MegastormAudioMix.ShakeOffsetRadians): nothing can spike between two frames.
    private void ApplyShake(float degrees)
    {
        if (degrees <= 0f)
        {
            if (_shaking && GodotObject.IsInstanceValid(_camera)) _camera!.Rotation = _baseRotation;
            if (_shakeDegrees != 0f) MaxShakeStepDegrees = Math.Max(MaxShakeStepDegrees, _shakeDegrees);
            _shaking = false; _shakeDegrees = 0f; _lastShakeOffset = Vector3.Zero;
            return;
        }
        if (!_shaking)
        {
            _camera = _explorer != null ? _explorer.GetNodeOrNull<Camera3D>(_explorer.CameraPath) : null;
            if (_camera == null) { _shakeDegrees = degrees; return; }
            _baseRotation = _camera.Rotation;
            _shaking = true;
        }
        (float pitch, float yaw, float roll) = MegastormAudioMix.ShakeOffsetRadians((float)_phase, degrees);
        var offset = new Vector3(pitch, yaw, roll);
        if (GodotObject.IsInstanceValid(_camera)) _camera!.Rotation = _baseRotation + offset;
        MaxShakeStepDegrees = Math.Max(MaxShakeStepDegrees, Mathf.RadToDeg((offset - _lastShakeOffset).Length()));
        _lastShakeOffset = offset;
        _shakeDegrees = degrees;
    }
}
