using Godot;

namespace RingworldTechDemo;

/// <summary>
/// Coalesces the continuous per-frame sun direction for consumers whose work scales with their material or sector count (shader
/// parameter writes, atmosphere ray frames). With sun stabilization off the key light is rewritten every frame (about 0.002 degrees per
/// frame at the default day length), so an exact-equality "sun changed" gate rebuilds every frame. This returns the last accepted
/// direction until the sun has moved by <see cref="DefaultEpsilonDegrees"/>, which is far below any visible lighting step. The directional
/// light itself and its shadows stay continuous; only these derived uniforms advance in steps.
/// </summary>
public sealed class RingSunDirectionThrottle
{
    /// <summary>Equal to the largest configurable sun shadow step; a lighting uniform stepping by this much is not visible.</summary>
    public const float DefaultEpsilonDegrees = 0.05f;

    private static readonly float DefaultCosine = Mathf.Cos(Mathf.DegToRad(DefaultEpsilonDegrees));

    private Vector3 _accepted = new(float.PositiveInfinity, 0.0f, 0.0f);

    /// <summary>Returns the accepted direction: the input when it has moved past the epsilon (or is the first/invalid-history input), otherwise the previous one.</summary>
    public Vector3 Resolve(Vector3 sunDirection)
    {
        if (_accepted.IsFinite() && sunDirection.IsFinite() && sunDirection.Dot(_accepted) >= DefaultCosine)
            return _accepted;
        _accepted = sunDirection;
        return _accepted;
    }

    /// <summary>Forgets the accepted direction so the next <see cref="Resolve"/> passes the live sun through.</summary>
    public void Reset() => _accepted = new Vector3(float.PositiveInfinity, 0.0f, 0.0f);
}
