using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>Headless check of the latched grass culling frustum: for random camera poses, every point inside the live
/// frustum of any camera within the movement (M) and rotation (R) budget must lie inside the latched padded frustum
/// (angular padding A, with R at most A - 0.5 degrees). Pure math; run with tools/grass_culling_frustum_test.gd.</summary>
public partial class GrassCullingFrustumTest : RefCounted
{
    public Godot.Collections.Dictionary Run()
    {
        try
        {
            var random = new Random(7319);
            long points = 0;
            // (movement m, angular padding deg, rotation deg, vertical fov deg, aspect, near, far)
            (float M, float A, float R, float Fov, float Aspect, float Near, float Far)[] cases =
            {
                (1f, 5f, 3f, 75f, 16f / 9f, .05f, 4000f),
                (1f, 5f, 4.5f, 75f, 16f / 9f, .05f, 4000f),
                (4f, 20f, 15f, 100f, 21f / 9f, .1f, 1500f),
                (0f, 5f, 4.5f, 60f, 1f, .3f, 800f),
                (2f, 1f, .5f, 90f, 4f / 3f, .05f, 400f),
                (1f, 20f, 15f, 140f, 16f / 9f, .05f, 2000f),
                (.5f, 0f, 0f, 75f, 16f / 9f, .05f, 4000f),
            };
            foreach (var c in cases)
            {
                for (int pose = 0; pose < 40; pose++)
                {
                    Transform3D latched = RandomPose(random);
                    float halfV = Mathf.DegToRad(c.Fov * .5f), halfH = MathF.Atan(MathF.Tan(halfV) * c.Aspect);
                    var exact = ExactPlanes(latched, halfH, halfV, c.Near, c.Far);
                    var frustum = new Vector4[6]; var planes = new Plane[6];
                    GrassCullingFrustum.Pad(exact, latched, c.Near, c.Far, c.M, c.A, true, frustum, planes);
                    for (int trial = 0; trial < 25; trial++)
                    {
                        // Worst-case budget use most of the time, interior values otherwise.
                        float move = trial % 3 == 0 ? c.M : c.M * (float)random.NextDouble();
                        float turn = trial % 3 == 1 ? c.R : c.R * (float)random.NextDouble();
                        Vector3 axis = RandomUnit(random);
                        Transform3D live = new(latched.Basis * new Basis(axis, Mathf.DegToRad(turn)), latched.Origin + RandomUnit(random) * move);
                        Check(Math.Abs(GrassCullingFrustum.BasisAngleDegrees(latched.Basis, live.Basis) - turn) < .05f, "basis angle matches the applied turn");
                        for (int sample = 0; sample < 40; sample++)
                        {
                            float depth = sample % 4 == 0 ? c.Near : sample % 4 == 1 ? c.Far : c.Near + (c.Far - c.Near) * (float)Math.Pow(random.NextDouble(), 3);
                            float sx = Edge(random, sample, 0), sy = Edge(random, sample, 1);
                            Vector3 local = new(sx * MathF.Tan(halfH) * depth, sy * MathF.Tan(halfV) * depth, -depth);
                            Vector3 world = live * local;
                            for (int plane = 0; plane < 6; plane++)
                            {
                                float distance = frustum[plane].X * world.X + frustum[plane].Y * world.Y + frustum[plane].Z * world.Z - frustum[plane].W;
                                // Allow float error proportional to the coordinate scale.
                                float tolerance = 1e-2f;
                                Check(distance <= tolerance, $"live point outside padded plane {plane} by {distance} (M={c.M} A={c.A} R={c.R} fov={c.Fov} depth={depth})");
                            }
                            points++;
                        }
                    }
                }
            }
            // With no budget the padded frustum is exactly the live frustum (throttle off or all thresholds zero).
            Transform3D exactPose = RandomPose(random);
            var exactPlanes = ExactPlanes(exactPose, Mathf.DegToRad(60f), Mathf.DegToRad(40f), .1f, 500f);
            var exactFrustum = new Vector4[6]; var exactOut = new Plane[6];
            GrassCullingFrustum.Pad(exactPlanes, exactPose, .1f, 500f, 0f, 0f, true, exactFrustum, exactOut);
            for (int i = 0; i < 6; i++)
                Check(exactOut[i].Normal == exactPlanes[i].Normal && exactOut[i].D == exactPlanes[i].D, "zero budget leaves the frustum exact");
            // A moved camera must still be rejected by a far-away point (padding is bounded, not infinite).
            var rejectPlanes = new Plane[6]; var rejectFrustum = new Vector4[6];
            var basePlanes = ExactPlanes(Transform3D.Identity, Mathf.DegToRad(50f), Mathf.DegToRad(30f), .1f, 500f);
            GrassCullingFrustum.Pad(basePlanes, Transform3D.Identity, .1f, 500f, 1f, 5f, true, rejectFrustum, rejectPlanes);
            Vector3 behind = new(0, 0, 50);
            bool rejected = false;
            for (int i = 0; i < 6; i++) rejected |= rejectFrustum[i].X * behind.X + rejectFrustum[i].Y * behind.Y + rejectFrustum[i].Z * behind.Z - rejectFrustum[i].W > 0;
            Check(rejected, "a point well behind the camera is still culled");
            return new() { ["pass"] = true, ["points_checked"] = points };
        }
        catch (Exception error) { return new() { ["pass"] = false, ["error"] = error.Message }; }
    }

    // -1/1 (frustum edge) on half the samples, interior otherwise.
    private static float Edge(Random random, int sample, int axis)
        => ((sample >> (axis + 2)) & 1) == 0 ? (random.Next(2) == 0 ? -1f : 1f) : (float)(random.NextDouble() * 2 - 1);

    private static Transform3D RandomPose(Random random)
        => new(new Basis(RandomUnit(random), (float)(random.NextDouble() * Math.PI * 2)),
            new Vector3((float)(random.NextDouble() - .5) * 400f, (float)(random.NextDouble() - .5) * 400f, (float)(random.NextDouble() - .5) * 400f));

    private static Vector3 RandomUnit(Random random)
    {
        while (true)
        {
            var v = new Vector3((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1));
            if (v.LengthSquared() is > .01f and <= 1f) return v.Normalized();
        }
    }

    /// <summary>World-space planes in Camera3D.GetFrustum() order (near, far, left, top, right, bottom); inside is negative.</summary>
    private static Plane[] ExactPlanes(Transform3D pose, float halfH, float halfV, float near, float far)
    {
        Vector3[] normals =
        {
            new(0, 0, 1), new(0, 0, -1), new(-MathF.Cos(halfH), 0, MathF.Sin(halfH)), new(0, MathF.Cos(halfV), MathF.Sin(halfV)),
            new(MathF.Cos(halfH), 0, MathF.Sin(halfH)), new(0, -MathF.Cos(halfV), MathF.Sin(halfV))
        };
        float[] distances = { -near, far, 0, 0, 0, 0 };
        var planes = new Plane[6];
        for (int i = 0; i < 6; i++)
        {
            Vector3 world = pose.Basis * normals[i];
            planes[i] = new Plane(world, distances[i] + world.Dot(pose.Origin));
        }
        return planes;
    }

    private static void Check(bool condition, string detail)
    { if (!condition) throw new InvalidOperationException(detail); }
}
