using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO12 rim wall top walk: side resolution, landing on the cap, lifting out of the wall body, walking, the outer/inner clamps and a jump. Pure C#; no scene.
/// Run: Godot --headless --path . --script res://tools/rim_wall_top_walk_test.gd. Prints RIM_WALL_TOP_WALK=PASS.
/// </summary>
public partial class RimWallTopWalkTest : RefCounted
{
    private const double W = 318_000.0;
    private const double D = 128.0;
    private const double Top = 10_400.0;
    private const double Bottom = -22_304.0;
    private const double Half = 0.3;
    private const double Dt = 1.0 / 60.0;

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok) _failures.Add(message); }

    private static void Step(int side, ref double around, ref double vertical, ref double across, ref Vector3 velocity, out bool grounded)
        => RimWallTopWalk.Step(ref around, ref vertical, ref across, ref velocity, Dt, side, W, D, Top, Half, out grounded);

    public int Run()
    {
        try
        {
            Sides();
            Landing();
            Lift();
            Walking();
            Clamps();
            Jump();
        }
        catch (Exception exception)
        {
            _failures.Add("exception: " + exception);
        }
        if (_failures.Count == 0)
        {
            GD.Print("RIM_WALL_TOP_WALK=PASS");
            return 0;
        }
        foreach (string failure in _failures) GD.PrintErr("RIM_WALL_TOP_WALK FAIL: " + failure);
        GD.Print("RIM_WALL_TOP_WALK=FAIL");
        return 1;
    }

    private void Sides()
    {
        Check(!RimWallTopWalk.TryResolveSide(100.0, Top, W, D, Bottom, out _), "habitat interior must not resolve a side");
        Check(!RimWallTopWalk.TryResolveSide(0.0, Top, W, D, Bottom, out _), "across 0 is the inner face (habitat), not side 0");
        Check(RimWallTopWalk.TryResolveSide(-64.0, Top, W, D, Bottom, out int s0) && s0 == 0, "across -64 resolves side 0");
        Check(RimWallTopWalk.TryResolveSide(-D, Top, W, D, Bottom, out s0) && s0 == 0, "across -D resolves side 0");
        Check(RimWallTopWalk.TryResolveSide(W + 64.0, Top, W, D, Bottom, out int s1) && s1 == 1, "across W+64 resolves side 1");
        Check(RimWallTopWalk.TryResolveSide(W + D, Top, W, D, Bottom, out s1) && s1 == 1, "across W+D resolves side 1");
        Check(!RimWallTopWalk.TryResolveSide(W, Top, W, D, Bottom, out _), "across W is the inner face, not side 1");
        Check(!RimWallTopWalk.TryResolveSide(-200.0, Top, W, D, Bottom, out _), "across -200 is beyond the wall");
        Check(!RimWallTopWalk.TryResolveSide(W + 200.0, Top, W, D, Bottom, out _), "across W+200 is beyond the wall");
        Check(!RimWallTopWalk.TryResolveSide(-64.0, Bottom - 1.0, W, D, Bottom, out _), "below the structural bottom must not resolve");
    }

    private void Landing()
    {
        foreach (int side in new[] { 0, 1 })
        {
            double around = 5_000.0, vertical = Top + 15.0, across = side == 0 ? -64.0 : W + 64.0;
            Vector3 velocity = Vector3.Zero;
            bool grounded = false;
            for (int i = 0; i < 600 && !grounded; i++)
            {
                velocity.Y -= 9.8f * (float)Dt;
                Step(side, ref around, ref vertical, ref across, ref velocity, out grounded);
            }
            Check(grounded, $"side {side}: falling from top+15 must land");
            Check(vertical == Top, $"side {side}: landing altitude {vertical} must equal the wall top exactly");
            Check(velocity.Y >= 0.0f, $"side {side}: landing must not keep downward velocity");
        }
    }

    private void Lift()
    {
        double around = 0.0, vertical = Top - 50.0, across = -64.0;
        Vector3 velocity = new(0.0f, -20.0f, 0.0f);
        Step(0, ref around, ref vertical, ref across, ref velocity, out bool grounded);
        Check(grounded && vertical == Top, $"inside the body: must lift to the top, got {vertical}");
        Check(velocity.Y >= 0.0f, "inside the body: downward velocity must be clamped");
    }

    private void Walking()
    {
        double around = 0.0, vertical = Top, across = -64.0;
        Vector3 velocity = new(6.0f, -0.1f, 0.0f);
        bool grounded = false;
        for (int i = 0; i < 60; i++) Step(0, ref around, ref vertical, ref across, ref velocity, out grounded);
        Check(grounded && vertical == Top, "walking along keeps the altitude and stays grounded");
        Check(Math.Abs(around - 6.0) < 0.01, $"walking along advances around by speed*time, got {around}");
        Check(across == -64.0, "walking along must not change across");
    }

    private void Clamps()
    {
        // Side 0: outer = -D (vacuum), inner = 0 (drop).
        double around = 0.0, vertical = Top, across = -64.0;
        Vector3 velocity = new(0.0f, 0.0f, -100.0f);
        for (int i = 0; i < 200; i++) Step(0, ref around, ref vertical, ref across, ref velocity, out _);
        Check(Math.Abs(across - (-D + Half)) < 1e-9 && velocity.Z == 0.0f, $"side 0 outer clamp, got {across}");
        across = -64.0; velocity = new Vector3(0.0f, 0.0f, 100.0f);
        for (int i = 0; i < 200; i++) Step(0, ref around, ref vertical, ref across, ref velocity, out _);
        Check(Math.Abs(across - (-Half)) < 1e-9 && velocity.Z == 0.0f, $"side 0 inner clamp, got {across}");
        // Side 1: inner = W (drop), outer = W + D (vacuum).
        across = W + 64.0; velocity = new Vector3(0.0f, 0.0f, 100.0f);
        for (int i = 0; i < 200; i++) Step(1, ref around, ref vertical, ref across, ref velocity, out _);
        Check(Math.Abs(across - (W + D - Half)) < 1e-9 && velocity.Z == 0.0f, $"side 1 outer clamp, got {across}");
        across = W + 64.0; velocity = new Vector3(0.0f, 0.0f, -100.0f);
        for (int i = 0; i < 200; i++) Step(1, ref around, ref vertical, ref across, ref velocity, out _);
        Check(Math.Abs(across - (W + Half)) < 1e-9 && velocity.Z == 0.0f, $"side 1 inner clamp, got {across}");
    }

    private void Jump()
    {
        double around = 0.0, vertical = Top, across = -64.0;
        Vector3 velocity = new(0.0f, 7.0f, 0.0f);
        Step(0, ref around, ref vertical, ref across, ref velocity, out bool grounded);
        Check(!grounded && vertical > Top, "a jump must leave the cap");
        bool left = !grounded;
        bool landed = false;
        for (int i = 0; i < 600 && !landed; i++)
        {
            velocity.Y -= 9.8f * (float)Dt;
            Step(0, ref around, ref vertical, ref across, ref velocity, out landed);
        }
        Check(left && landed && vertical == Top, "a jump must land again exactly on the cap");
    }
}
