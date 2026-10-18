using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Pure math for the latched grass culling view: widens an exact camera frustum so any camera within a
/// translation and rotation budget of the latch sees only grass that the widened frustum keeps. Planes follow
/// Camera3D.GetFrustum(): world space, negative signed distance is inside.</summary>
internal static class GrassCullingFrustum
{
    private const float DisableAngleDegrees = 85f;

    /// <summary>Angle (degrees) of the rotation taking one camera basis to the other.</summary>
    internal static float BasisAngleDegrees(Basis from, Basis to)
    {
        if (from == to) return 0f;
        Quaternion a = from.Orthonormalized().GetRotationQuaternion(), b = to.Orthonormalized().GetRotationQuaternion();
        // atan2 on the relative rotation stays accurate for the small angles that matter here (acos of a dot product does not).
        Quaternion relative = a.Inverse() * b;
        float vector = MathF.Sqrt(relative.X * relative.X + relative.Y * relative.Y + relative.Z * relative.Z);
        return Mathf.RadToDeg(2f * MathF.Atan2(vector, Math.Abs(relative.W)));
    }

    /// <summary>Widens <paramref name="exact"/> into <paramref name="frustum"/>/<paramref name="planes"/>.
    /// Any camera within <paramref name="movement"/> metres and <paramref name="angularDegrees"/> degrees of rotation of
    /// <paramref name="transform"/> keeps its whole live view inside the result.</summary>
    /// <remarks>A turn of R degrees moves each view-edge ray by at most R degrees, so each side plane is tilted until every
    /// edge ray is at least A degrees inside it (A at least R). That tilt is larger than A for the plane's own half-angle
    /// because the edge rays also lie on the neighbouring planes. The near plane retreats to the camera and the far plane
    /// grows to the slanted corner distance.</remarks>
    /// <param name="movement">Translation budget (m) pushed onto every plane; includes any apex offset the caller wants covered.</param>
    /// <param name="angularDegrees">Angular padding. Ignored when not perspective.</param>
    internal static void Pad(IReadOnlyList<Plane> exact, Transform3D transform, float near, float far, float movement,
        float angularDegrees, bool perspective, Vector4[] frustum, Plane[] planes)
    {
        int count = exact.Count;
        bool pad = movement > 0f || angularDegrees > 0f;
        Basis basis = transform.Basis.Orthonormalized();
        Vector3 forward = -basis.Z, right = basis.X, up = basis.Y, eye = transform.Origin;
        float angular = perspective ? Mathf.DegToRad(angularDegrees) : 0f;
        // Tangents of the live half-angles (horizontal planes, vertical planes), and the corner (diagonal) angle.
        float tanH = 0f, tanV = 0f;
        if (pad && perspective)
            for (int i = 0; i < count; i++)
            {
                Vector3 n = exact[i].Normal;
                float forwardDot = n.Dot(forward);
                if (Math.Abs(forwardDot) > .9999f) continue;
                float tangent = SideTangent(forwardDot);
                if (Math.Abs(n.Dot(up)) > Math.Abs(n.Dot(right))) tanV = Math.Max(tanV, tangent); else tanH = Math.Max(tanH, tangent);
            }
        float cornerScale = MathF.Sqrt(1f + tanH * tanH + tanV * tanV);
        // After a turn the live corners can sit at or behind the latched camera plane, where no near plane is valid.
        bool nearUsable = MathF.Atan(MathF.Sqrt(tanH * tanH + tanV * tanV)) + angular < Mathf.DegToRad(DisableAngleDegrees);
        float farExtra = pad ? far * (cornerScale - 1f) : 0f;
        for (int i = 0; i < count; i++)
        {
            Vector3 normal = exact[i].Normal;
            float d = exact[i].D;
            if (pad)
            {
                float forwardDot = normal.Dot(forward);
                if (Math.Abs(forwardDot) > .9999f)
                {
                    // Near moves back to the camera (a turn changes which depth counts as near); far also covers the slanted corners.
                    if (forwardDot < 0f && !nearUsable) d = 1e9f;
                    else d += movement + (forwardDot < 0f ? near : farExtra);
                }
                else if (perspective && angular > 0f)
                {
                    bool vertical = Math.Abs(normal.Dot(up)) > Math.Abs(normal.Dot(right));
                    float own = SideTangent(forwardDot);
                    // sin(tilt) = sin(A) * |edge ray| / |edge ray component along the plane's own direction|.
                    float sinTilt = MathF.Sin(angular) * cornerScale / MathF.Sqrt(1f + own * own);
                    float half = MathF.Atan(own);
                    if (sinTilt >= 1f || half + MathF.Asin(sinTilt) >= Mathf.DegToRad(DisableAngleDegrees)) { normal = Vector3.Up; d = 1e9f; }
                    else
                    {
                        // Side planes contain the camera origin: rotate the normal about the in-plane axis, outward.
                        float cosTilt = MathF.Sqrt(1f - sinTilt * sinTilt);
                        Vector3 axis = vertical ? right : up;
                        Vector3 turned = axis.Cross(normal) * sinTilt;
                        Vector3 a = (normal * cosTilt + turned).Normalized(), b = (normal * cosTilt - turned).Normalized();
                        normal = a.Dot(forward) < b.Dot(forward) ? a : b;
                        d = normal.Dot(eye) + movement;
                    }
                }
                else d += movement;
            }
            planes[i] = new Plane(normal, d);
            frustum[i] = new Vector4(normal.X, normal.Y, normal.Z, d);
        }
    }

    // A side plane's outward normal n has n.forward = -sin(half angle); returns tan(half angle), bounded away from 90 degrees.
    private static float SideTangent(float forwardDot)
    {
        float sine = Math.Clamp(-forwardDot, -.9999f, .9999f);
        return sine / MathF.Sqrt(1f - sine * sine);
    }
}
