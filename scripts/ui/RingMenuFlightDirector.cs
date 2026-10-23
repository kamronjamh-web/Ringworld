using System;
using Godot;
using RingworldTechDemo.Loading;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.UI;

/// <summary>
/// Flies the held world's Explorer behind the start menu through its scripted-flight command. Modes: <see cref="StartLoiter"/> (slow
/// orbit around the save spot at cruise altitude), <see cref="Rush"/> (fast horizontal run to a pose), <see cref="Descend"/> (eased
/// descent onto a pose), <see cref="Ascend"/> (eased climb back to cruise) and <see cref="Hold"/>. Altitude above ground is a
/// P-controller; whatever the mode, upward motion is limited so the camera never exceeds <see cref="CeilingMeters"/> above ground.
/// </summary>
public partial class RingMenuFlightDirector : Node
{
    /// <summary>Hard ceiling above ground for the menu camera (the same band as the snapshot placement).</summary>
    public const float CeilingMeters = 18000f;

    [Signal] public delegate void ArrivedEventHandler();
    [Signal] public delegate void FailedEventHandler(string message);

    public enum Mode { Idle, Loiter, Rush, Descend, Ascend, Hold }

    /// <summary>Altitude above ground the loiter and rush hold.</summary>
    public float CruiseAltitudeMeters { get; set; } = 16000f;
    /// <summary>Loiter stays within this horizontal distance of the home pose.</summary>
    public float LoiterRadiusMeters { get; set; } = 30000f;
    /// <summary>Slow, cinematic loiter ground speed.</summary>
    public float LoiterSpeedMetersPerSecond { get; set; } = 120f;
    /// <summary>Cruise camera pitch in degrees (negative looks down).</summary>
    public float CruisePitchDegrees { get; set; } = -25f;
    /// <summary>Top horizontal speed of <see cref="Rush"/>.</summary>
    public float RushSpeedMetersPerSecond { get; set; } = 1200f;
    /// <summary>Duration of <see cref="Descend"/> and <see cref="Ascend"/>.</summary>
    public float DescentSeconds { get; set; } = 12f;

    public Mode CurrentMode { get; private set; } = Mode.Idle;
    /// <summary>Radius of the orbit flown by the loiter (half the loiter radius, so the whole orbit stays inside it).</summary>
    public float OrbitRadiusMeters => Mathf.Max(1500f, LoiterRadiusMeters * .5f);
    public Vector3 CommandedVelocity => _command;

    private const float GuardMeters = 150f;
    private const float AltitudeGain = 1f;
    private const float MaxClimbMetersPerSecond = 400f;
    private const float MaxSinkMetersPerSecond = 600f;
    private const float Acceleration = 40f;
    private const float YawRateDegrees = 12f;
    private const float PitchRateDegrees = 10f;
    private const float EdgeMarginMeters = 4000f;

    private RingWorldLoadingBridge? _bridge;
    private RingVoxelExplorerController? _explorer;
    private RingCanonicalPosition _home;
    private Vector3 _command;
    private float _yawDegrees;
    private float _pitchDegrees;
    private float _targetPitch;
    private double _phase;
    private double _orbitAround;
    private double _orbitAcross;
    private double _elapsed;
    private double _timeout;
    private RingCanonicalPosition _from;
    private RingCanonicalPosition _to;
    private float _fromAltitude;
    private float _targetYaw;
    private bool _hasTargetYaw;

    /// <summary>The spot the loiter circles (the saved player position unless a caller overrides it).</summary>
    public RingCanonicalPosition Home { get => _home; set => _home = value; }

    /// <summary>Script-friendly <see cref="Home"/> setter: the next <see cref="StartLoiter"/> circles this spot.</summary>
    public void SetHome(double aroundMeters, double verticalMeters, double acrossMeters)
        => _home = new RingCanonicalPosition(aroundMeters, verticalMeters, acrossMeters);

    /// <summary>
    /// Takes the held world's explorer under scripted flight and reads the home pose (the saved spot) from the bridge. False when the
    /// explorer is unavailable. Saves stay held until the bridge is told otherwise.
    /// </summary>
    public bool Bind(RingWorldLoadingBridge bridge)
    {
        Release();
        _explorer = bridge.BeginScriptedFlight();
        if (_explorer == null) return false;
        _bridge = bridge;
        Godot.Collections.Dictionary home = bridge.GetHomePose();
        _home = _explorer.CanonicalPosition;
        if (home.Count > 0 && home.TryGetValue("valid", out Variant valid) && valid.AsBool())
            _home = new RingCanonicalPosition(home["around_m"].AsDouble(), home["vertical_m"].AsDouble(), home["across_m"].AsDouble());
        _yawDegrees = Mathf.RadToDeg(_explorer.SavedYaw);
        _pitchDegrees = _explorer.SavedPitch;
        _targetPitch = CruisePitchDegrees;
        _command = Vector3.Zero;
        CurrentMode = Mode.Hold;
        return true;
    }

    /// <summary>Gives the explorer back to the held state and ends the scripted flight.</summary>
    public void Release()
    {
        if (_bridge != null && GodotObject.IsInstanceValid(_bridge)) _bridge.EndScriptedFlight();
        _bridge = null;
        _explorer = null;
        CurrentMode = Mode.Idle;
    }

    /// <summary>One-off placement of the camera (forces a single re-anchor); not part of any per-frame path.</summary>
    public bool Place(double aroundMeters, double verticalMeters, double acrossMeters, float yawDegrees, float pitchDegrees)
    {
        if (_explorer == null || !_explorer.PlaceScriptedFlight(new RingCanonicalPosition(aroundMeters, verticalMeters, acrossMeters), yawDegrees, pitchDegrees))
            return false;
        _yawDegrees = yawDegrees;
        _pitchDegrees = pitchDegrees;
        _command = Vector3.Zero;
        return true;
    }

    public void StartLoiter()
    {
        if (_explorer == null) return;
        RingCanonicalPosition position = _explorer.CanonicalPosition;
        double offsetAround = WrapDelta(position.AroundMeters - _home.AroundMeters, _explorer.ScriptedFlightCircumferenceMeters);
        double offsetAcross = position.AcrossMeters - _home.AcrossMeters;
        _phase = Math.Sqrt(offsetAround * offsetAround + offsetAcross * offsetAcross) < 500.0 ? 0.0 : Math.Atan2(offsetAcross, offsetAround);
        // The orbit passes through the start, so the camera never swings out to a distant circle; its far side is at most the loiter radius from home.
        float radius = Mathf.Min(OrbitRadiusMeters, LoiterRadiusMeters);
        _orbitAround = position.AroundMeters - radius * Math.Cos(_phase);
        _orbitAcross = position.AcrossMeters - radius * Math.Sin(_phase);
        _targetPitch = CruisePitchDegrees;
        _hasTargetYaw = false;
        CurrentMode = Mode.Loiter;
    }

    /// <summary>Fast run to the horizontal position of <paramref name="target"/> at cruise altitude; emits Arrived once close.</summary>
    public void Rush(RingCanonicalPosition target)
    {
        if (_explorer == null) return;
        _to = target;
        _elapsed = 0.0;
        _timeout = HorizontalDistance(_explorer.CanonicalPosition, target) / Math.Max(50f, RushSpeedMetersPerSecond) * 3.0 + 30.0;
        _hasTargetYaw = false;
        CurrentMode = Mode.Rush;
    }

    /// <summary>Eased descent (over <see cref="DescentSeconds"/>) onto <paramref name="target"/> with the given view; Arrived when it lands.</summary>
    public void Descend(RingCanonicalPosition target, float yawDegrees, float pitchDegrees)
    {
        if (_explorer == null) return;
        _from = _explorer.CanonicalPosition;
        _to = target;
        _targetYaw = yawDegrees;
        _targetPitch = pitchDegrees;
        _hasTargetYaw = true;
        _elapsed = 0.0;
        CurrentMode = Mode.Descend;
    }

    /// <summary>Eased climb (over <see cref="DescentSeconds"/>) straight up to cruise altitude; Arrived at the top.</summary>
    public void Ascend()
    {
        if (_explorer == null) return;
        _fromAltitude = _explorer.CurrentAltitudeAboveTerrainMeters;
        _targetPitch = CruisePitchDegrees;
        _hasTargetYaw = false;
        _elapsed = 0.0;
        CurrentMode = Mode.Ascend;
    }

    /// <summary>Stops in place (the explorer stays under scripted flight).</summary>
    public void Hold() => CurrentMode = Mode.Hold;

    public override void _PhysicsProcess(double delta)
    {
        if (CurrentMode == Mode.Idle) return;
        if (_explorer == null || !GodotObject.IsInstanceValid(_explorer))
        {
            CurrentMode = Mode.Idle;
            EmitSignal(SignalName.Failed, "The menu world's explorer is gone.");
            return;
        }
        if (!_explorer.ScriptedFlightActive) return;
        float dt = Mathf.Min((float)delta, .05f);
        RingCanonicalPosition position = _explorer.CanonicalPosition;
        float altitude = _explorer.CurrentAltitudeAboveTerrainMeters;
        double circumference = _explorer.ScriptedFlightCircumferenceMeters;
        float targetAltitude = Mathf.Min(CruiseAltitudeMeters, CeilingMeters - 500f);
        Vector2 horizontal = Vector2.Zero; // (around, across) m/s
        float vertical = ClimbFor(targetAltitude, altitude);
        bool servo = false;
        bool arrived = false;

        switch (CurrentMode)
        {
            case Mode.Loiter:
                horizontal = LoiterVelocity(position, circumference, dt);
                break;
            case Mode.Rush:
            {
                _elapsed += dt;
                double dAround = WrapDelta(_to.AroundMeters - position.AroundMeters, circumference);
                double dAcross = _to.AcrossMeters - position.AcrossMeters;
                double distance = Math.Sqrt(dAround * dAround + dAcross * dAcross);
                // Brake over the last stretch so the run ends gently; never faster than the rush speed.
                float speed = Mathf.Min(RushSpeedMetersPerSecond, (float)distance * .4f);
                if (distance > 1.0) horizontal = new Vector2((float)(dAround / distance), (float)(dAcross / distance)) * speed;
                if (distance < 250.0) arrived = true;
                else if (_elapsed > _timeout) { Fail("Rush did not arrive in time."); return; }
                break;
            }
            case Mode.Descend:
            {
                _elapsed += dt;
                float eased = Ease(_elapsed);
                double wantAround = _from.AroundMeters + WrapDelta(_to.AroundMeters - _from.AroundMeters, circumference) * eased;
                double wantAcross = _from.AcrossMeters + (_to.AcrossMeters - _from.AcrossMeters) * eased;
                double wantVertical = _from.VerticalMeters + (_to.VerticalMeters - _from.VerticalMeters) * eased;
                horizontal = new Vector2((float)(WrapDelta(wantAround - position.AroundMeters, circumference) / dt), (float)((wantAcross - position.AcrossMeters) / dt));
                vertical = (float)((wantVertical - position.VerticalMeters) / dt);
                servo = true;
                arrived = _elapsed >= Math.Max(.5f, DescentSeconds);
                break;
            }
            case Mode.Ascend:
            {
                _elapsed += dt;
                float wantAltitude = Mathf.Lerp(_fromAltitude, targetAltitude, Ease(_elapsed));
                vertical = Mathf.Clamp(AltitudeGain * 3f * (wantAltitude - altitude), -MaxSinkMetersPerSecond, 3000f);
                servo = true;
                arrived = _elapsed >= Math.Max(.5f, DescentSeconds);
                break;
            }
        }

        // The ceiling holds in every mode and survives the acceleration limit below.
        float maxUp = CeilingMeters - GuardMeters - altitude;
        vertical = Mathf.Min(vertical, maxUp);
        var desired = new Vector3(horizontal.X, vertical, horizontal.Y);
        _command = servo ? desired : _command.MoveToward(desired, Acceleration * (CurrentMode == Mode.Rush ? 6f : 1f) * dt);
        if (_command.Y > maxUp) _command.Y = maxUp;

        UpdateView(dt);
        _explorer.SetScriptedFlightCommand(_command, _yawDegrees, _pitchDegrees);
        if (!arrived) return;
        CurrentMode = Mode.Hold;
        _command = Vector3.Zero;
        _explorer.SetScriptedFlightCommand(Vector3.Zero, _yawDegrees, _pitchDegrees);
        EmitSignal(SignalName.Arrived);
    }

    private float Ease(double elapsed)
    {
        float t = (float)Math.Clamp(elapsed / Math.Max(.5f, DescentSeconds), 0.0, 1.0);
        return t * t * (3f - 2f * t);
    }

    private static float ClimbFor(float targetAltitude, float altitude)
        => Mathf.Clamp(AltitudeGain * (targetAltitude - altitude), -MaxSinkMetersPerSecond, MaxClimbMetersPerSecond);

    private Vector2 LoiterVelocity(in RingCanonicalPosition position, double circumference, float dt)
    {
        float radius = Mathf.Min(OrbitRadiusMeters, LoiterRadiusMeters);
        double omega = LoiterSpeedMetersPerSecond / radius;
        _phase += omega * dt;
        double width = _explorer!.ScriptedFlightAcrossWidthMeters;
        double targetAround = _orbitAround + radius * Math.Cos(_phase);
        double targetAcross = _orbitAcross + radius * Math.Sin(_phase);
        if (width > 2.0 * EdgeMarginMeters) targetAcross = Math.Clamp(targetAcross, EdgeMarginMeters, width - EdgeMarginMeters);
        double errAround = WrapDelta(targetAround - position.AroundMeters, circumference);
        double errAcross = targetAcross - position.AcrossMeters;
        // Feed-forward of the orbit's tangent plus a gentle pull toward the moving target point.
        double feedAround = -radius * omega * Math.Sin(_phase);
        double feedAcross = radius * omega * Math.Cos(_phase);
        var velocity = new Vector2((float)(feedAround + errAround * .05), (float)(feedAcross + errAcross * .05));
        return velocity.LimitLength(LoiterSpeedMetersPerSecond * 2f);
    }

    private void UpdateView(float dt)
    {
        float yawTarget = _yawDegrees;
        if (_hasTargetYaw) yawTarget = _targetYaw;
        else if (new Vector2(_command.X, _command.Z).LengthSquared() > 25f)
            yawTarget = Mathf.RadToDeg(Mathf.Atan2(_command.Z, _command.X));
        float yawStep = Mathf.RadToDeg(Mathf.AngleDifference(Mathf.DegToRad(_yawDegrees), Mathf.DegToRad(yawTarget)));
        float maxYaw = (CurrentMode == Mode.Descend ? 60f : YawRateDegrees) * dt;
        _yawDegrees += Mathf.Clamp(yawStep, -maxYaw, maxYaw);
        float maxPitch = (CurrentMode == Mode.Descend ? 40f : PitchRateDegrees) * dt;
        _pitchDegrees += Mathf.Clamp(_targetPitch - _pitchDegrees, -maxPitch, maxPitch);
    }

    private void Fail(string message)
    {
        CurrentMode = Mode.Hold;
        _command = Vector3.Zero;
        _explorer?.SetScriptedFlightCommand(Vector3.Zero, _yawDegrees, _pitchDegrees);
        EmitSignal(SignalName.Failed, message);
    }

    private double HorizontalDistance(in RingCanonicalPosition a, in RingCanonicalPosition b)
    {
        double around = WrapDelta(b.AroundMeters - a.AroundMeters, _explorer!.ScriptedFlightCircumferenceMeters);
        double across = b.AcrossMeters - a.AcrossMeters;
        return Math.Sqrt(around * around + across * across);
    }

    private static double WrapDelta(double delta, double circumference)
        => circumference > 0.0 ? delta - Math.Round(delta / circumference) * circumference : delta;

    public override void _ExitTree() => Release();
}
