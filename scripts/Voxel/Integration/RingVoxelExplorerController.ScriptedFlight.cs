using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Integration;

public partial class RingVoxelExplorerController
{
    private const float ScriptedFlightMaxSpeedMetersPerSecond = 20000.0f;
    private const float ScriptedHorizonResponse = 8.0f;

    private bool _scriptedFlight;
    private bool _scriptedPreviousNoclip;
    private Vector3 _scriptedVelocity;
    private float _scriptedYawRadians;
    private float _scriptedPitchDegrees;
    private RingCanonicalPosition? _scriptedStreamingHold;

    /// <summary>True while a director owns the body: keyboard, mouse look, wheel and the speed presets are ignored.</summary>
    public bool ScriptedFlightActive => _scriptedFlight;

    /// <summary>Number of times a scripted physics step moved the floating-origin anchor (a sector boundary was crossed).</summary>
    public int ScriptedFlightAnchorCount { get; private set; }

    /// <summary>Ring circumference in meters, for directors that wrap their routes.</summary>
    public double ScriptedFlightCircumferenceMeters => _grid?.GetCanonicalRingCircumferenceMeters64() ?? 0.0;

    /// <summary>Ring across-width in meters, for directors that keep their routes over the ring.</summary>
    public double ScriptedFlightAcrossWidthMeters => _grid != null ? _grid.RingAcrossWidthMeters : 0.0;

    /// <summary>
    /// Hands the body to a flight director. Every input path (the polled movement keys, mouse look, wheel, V, 1-5, capture, Esc) is
    /// bypassed until <see cref="EndScriptedFlight"/>; the body flies noclip along the commanded velocity.
    /// </summary>
    public bool BeginScriptedFlight()
    {
        if (_grid == null) return false;
        if (!_scriptedFlight)
        {
            _scriptedPreviousNoclip = _noclip;
            _scriptedFlight = true;
        }
        _benchmarkOverrideActive = false;
        _noclip = true;
        _grounded = false;
        _spawnTerrainSettling = false;
        _undergroundSpawnSettling = false;
        _scriptedVelocity = Vector3.Zero;
        _canonicalVelocity = Vector3.Zero;
        _scriptedYawRadians = _yaw;
        _scriptedPitchDegrees = _pitch;
        return true;
    }

    /// <summary>
    /// Sets the scripted motion: canonical velocity (around, vertical up, across) in m/s and the view yaw and pitch in degrees. Applied by
    /// every physics step; this never re-anchors by itself.
    /// </summary>
    public void SetScriptedFlightCommand(Vector3 canonicalVelocity, float yawDegrees, float pitchDegrees)
    {
        if (!_scriptedFlight) return;
        if (float.IsFinite(canonicalVelocity.X) && float.IsFinite(canonicalVelocity.Y) && float.IsFinite(canonicalVelocity.Z))
            _scriptedVelocity = canonicalVelocity.LimitLength(ScriptedFlightMaxSpeedMetersPerSecond);
        if (float.IsFinite(yawDegrees)) _scriptedYawRadians = Mathf.PosMod(Mathf.DegToRad(yawDegrees), Mathf.Tau);
        if (float.IsFinite(pitchDegrees)) _scriptedPitchDegrees = Mathf.Clamp(pitchDegrees, -85.0f, 85.0f);
    }

    /// <summary>
    /// One-off placement (the only scripted call that forces a re-anchor): jumps to the position and view, clears the streaming focus override
    /// so the normal chase resumes there, and zeroes the commanded velocity.
    /// </summary>
    public bool PlaceScriptedFlight(in RingCanonicalPosition target, float yawDegrees, float pitchDegrees)
    {
        if (!_scriptedFlight || !TryTeleportToCanonical(target, yawDegrees, pitchDegrees, true, out _)) return false;
        if (_scriptedStreamingHold is { } hold) _grid?.SetStreamingCanonicalFocusOverride(hold);
        else _grid?.ClearStreamingCanonicalFocusOverride();
        _scriptedVelocity = Vector3.Zero;
        _scriptedYawRadians = _yaw;
        _scriptedPitchDegrees = _pitch;
        RefreshAltitudeAboveTerrain();
        return true;
    }

    /// <summary>
    /// Pins (or, with null, releases) the grid's streaming focus on a spot other than the body while it flies (the start menu pre-streams the saved spot).
    /// <see cref="PlaceScriptedFlight"/> restores the pin after its teleport instead of clearing it.
    /// </summary>
    public void SetScriptedStreamingHold(RingCanonicalPosition? position)
    {
        _scriptedStreamingHold = position;
        if (position is { } hold) _grid?.SetStreamingCanonicalFocusOverride(hold);
        else _grid?.ClearStreamingCanonicalFocusOverride();
    }

    /// <summary>Returns input to the player. The horizon eases back to the surface through the normal curvature correction.</summary>
    public void EndScriptedFlight()
    {
        if (!_scriptedFlight) return;
        _scriptedFlight = false;
        _scriptedVelocity = Vector3.Zero;
        _canonicalVelocity = Vector3.Zero;
        _noclip = _scriptedPreviousNoclip;
        _curvatureAtmosphereStateInitialized = false;
        RequestCurvatureCorrection(NoclipExitCorrectionResponse);
    }

    private void ProcessScriptedFlight(float dt)
    {
        float speed = _scriptedVelocity.Length();
        if (speed > 0.0001f) MoveNoclipInPhysicalRingSpace(_scriptedVelocity / speed, speed, dt);
        else _canonicalVelocity = Vector3.Zero;

        // The anchor moves only when the body leaves its collision sector; it is counted so directors and tests can see it.
        double originAround = _collisionOriginAround, originAcross = _collisionOriginAcross;
        RecenterCollisionOrigin(force: false);
        if (originAround != _collisionOriginAround || originAcross != _collisionOriginAcross) ScriptedFlightAnchorCount++;

        _yaw = _scriptedYawRadians;
        _pitch = _scriptedPitchDegrees;
        ApplyCameraRotation();
        ApplyWorldTransform();
        CurrentAltitudeAboveTerrainMeters = ResolveAltitudeAboveTerrainMeters();
        AlignScriptedHorizon(dt);
    }

    /// <summary>
    /// The ring's up direction turns as the body travels around it. Unlike the gameplay correction (atmosphere bound, thresholded, periodic),
    /// the scripted horizon eases to the local surface up on every step at any altitude; only the body basis rotates, so the commanded view
    /// (set on the head) is untouched.
    /// </summary>
    private void AlignScriptedHorizon(float dt)
    {
        if (_grid == null || !_cameraBasisInitialized) return;
        Vector3 targetUp = ResolveCanonicalSurfaceUp();
        Basis current = GlobalBasis.Orthonormalized();
        CurrentHorizonErrorDegrees = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(current.Y.Dot(targetUp), -1.0f, 1.0f)));
        if (CurrentHorizonErrorDegrees <= 0.0001f) return;
        Basis target = BuildSurfaceAlignedBasis(current, targetUp);
        float blend = 1.0f - Mathf.Exp(-ScriptedHorizonResponse * Mathf.Max(0.0f, dt));
        GlobalBasis = current.Slerp(target, Mathf.Clamp(blend, 0.0f, 1.0f)).Orthonormalized();
        CurrentHorizonErrorDegrees = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(GlobalBasis.Y.Dot(targetUp), -1.0f, 1.0f)));
    }
}
