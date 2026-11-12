using System;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// A kinematic floating body (T3.md Spec F1 to F3). Its state is canonical (around, across, height), so a floating-origin anchor change needs no handler: every physics frame
/// the node transform is rebuilt from the state through RingLodGeometrySnapshot.CanonicalToWorld. It drifts with the flow, bobs at its draft, turns with the current and strands
/// on banks; it does not tumble and does not collide with other bodies. No RigidBody3D and no physics-server gravity. It does nothing until a flow source and a ground callback are set,
/// so worlds without water.current pay nothing. The manager (T3R-7) sets the sources, caps the awake count and runs the wake check.
/// </summary>
public partial class RingFloatingBody : Node3D
{
    public const double SleepSpeed = 0.05;
    public const double SleepSeconds = 3.0;
    public const double WakeWaterSpeed = 0.3;
    private const double SampleRadius = 0.5;
    private const double GroundProbe = 0.5;

    /// <summary>Mass in kg, kept for later contact use; the integrator does not read it.</summary>
    [Export] public double Mass { get; set; } = 50.0;
    /// <summary>Metres the origin floats below the surface. Zero or less means 40 % of the collision shape height.</summary>
    [Export] public double Draft { get; set; }
    /// <summary>Horizontal relaxation rate toward the water velocity, per second.</summary>
    [Export] public double Drag { get; set; } = RingBuoyancy.DefaultDrag;
    /// <summary>Flow samples per step, 1 to 4; the flow is averaged.</summary>
    [Export(PropertyHint.Range, "1,4,1")] public int SamplePoints { get; set; } = 1;
    [Export] public bool MayStrand { get; set; } = true;

    public IWaterFlowSource? Flow { get; set; }
    /// <summary>Ground height (canonical vertical) at an around and across, for the bank and the lake bed.</summary>
    public Func<double, double, double>? Ground { get; set; }
    public Func<RingLodGeometrySnapshot>? SnapshotSource { get; set; }

    public BuoyancyState State;
    public bool Sleeping { get; private set; }
    private double _slowSeconds;

    private double EffectiveDraft => Draft > 0.0 ? Draft : 0.4;

    public override void _Ready()
    {
        if (Draft <= 0.0) Draft = 0.4 * CollisionHeight();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Flow == null || Ground == null) return;
        Step(delta);
        if (SnapshotSource != null) GlobalTransform = ComputeWorldTransform(SnapshotSource());
    }

    public void Place(double around, double across, double height, double yaw = 0.0)
    {
        State = new BuoyancyState { Around = around, Across = across, Height = height, Yaw = yaw };
        Wake();
    }

    public void Wake()
    {
        Sleeping = false;
        _slowSeconds = 0.0;
    }

    /// <summary>The manager's two-second check: one sample at the body; wakes it when the water there moves faster than 0.3 m/s. Returns true when it woke.</summary>
    public bool WakeIfWaterMoves()
    {
        if (!Sleeping || Flow == null) return false;
        WaterFlowSample f = Flow.Sample(State.Around, State.Across, State.Height);
        if (f.Kind == InlandWaterKind.None || Math.Sqrt(f.VelocityAround * f.VelocityAround + f.VelocityAcross * f.VelocityAcross) <= WakeWaterSpeed) return false;
        Wake();
        return true;
    }

    /// <summary>One physics step. A sleeping body returns at once and takes no flow sample.</summary>
    public void Step(double dt)
    {
        if (Sleeping || Flow == null || Ground == null) return;
        BuoyancyInput input = Gather();
        var p = BuoyancyParams.Create(EffectiveDraft, Drag, MayStrand);
        RingBuoyancy.Step(ref State, p, input, dt);

        double speed = Math.Sqrt(State.VAround * State.VAround + State.VAcross * State.VAcross + State.VHeight * State.VHeight);
        if (speed < SleepSpeed) _slowSeconds += dt; else _slowSeconds = 0.0;
        if (_slowSeconds >= SleepSeconds)
        {
            Sleeping = true;
            State.VAround = 0.0;
            State.VAcross = 0.0;
            State.VHeight = 0.0;
        }
    }

    private BuoyancyInput Gather()
    {
        int n = Math.Clamp(SamplePoints, 1, 4);
        double cos = Math.Cos(State.Yaw), sin = Math.Sin(State.Yaw);
        double va = 0.0, vc = 0.0, surface = 0.0, front = double.NaN, back = double.NaN;
        int valid = 0;
        for (int i = 0; i < n; i++)
        {
            // Points: 1 the centre; 2 front and back; 3 adds the left; 4 adds the right (front is along the heading).
            double fx = 0.0, fy = 0.0;
            if (n > 1)
            {
                if (i == 0) fx = SampleRadius;
                else if (i == 1) fx = -SampleRadius;
                else if (i == 2) fy = SampleRadius;
                else fy = -SampleRadius;
            }
            double a = State.Around + fx * cos - fy * sin, c = State.Across + fx * sin + fy * cos;
            WaterFlowSample f = Flow!.Sample(a, c, State.Height);
            if (f.Kind == InlandWaterKind.None || double.IsNaN(f.Surface)) continue;
            va += f.VelocityAround;
            vc += f.VelocityAcross;
            surface += f.Surface;
            valid++;
            if (n > 1 && i == 0) front = f.Surface;
            else if (i == 1) back = f.Surface;
        }

        double ground = Ground!(State.Around, State.Across);
        var input = new BuoyancyInput { Ground = ground, Surface = double.NaN };
        if (valid == 0) return input;
        input.Surface = surface / valid;
        input.WaterAround = va / valid;
        input.WaterAcross = vc / valid;
        if (!double.IsNaN(front) && !double.IsNaN(back)) input.SurfaceSlope = (front - back) / (2.0 * SampleRadius);
        if (!MayStrand && input.Surface - ground < EffectiveDraft)
        {
            // The flow sample carries no channel offset, so deeper water is the downhill direction of the ground callback.
            double ga = Ground(State.Around + GroundProbe, State.Across) - Ground(State.Around - GroundProbe, State.Across);
            double gc = Ground(State.Around, State.Across + GroundProbe) - Ground(State.Around, State.Across - GroundProbe);
            double len = Math.Sqrt(ga * ga + gc * gc);
            if (len > 1e-9)
            {
                input.DeeperAround = -ga / len;
                input.DeeperAcross = -gc / len;
            }
        }
        return input;
    }

    /// <summary>The world transform of the body for a snapshot: position by CanonicalToWorld, heading and pitch from the canonical state.</summary>
    public Transform3D ComputeWorldTransform(in RingLodGeometrySnapshot snapshot)
    {
        ContainmentFieldPlacement.AxesAt(snapshot, State.Around, out Vector3 aroundDir, out Vector3 upDir, out Vector3 acrossDir);
        Vector3 forward = aroundDir * (float)Math.Cos(State.Yaw) + acrossDir * (float)Math.Sin(State.Yaw);
        Vector3 side = forward.Cross(upDir);
        var basis = new Basis(forward, upDir, side);
        basis = new Basis(side.Normalized(), (float)State.Pitch) * basis;
        return new Transform3D(basis, snapshot.CanonicalToWorld(State.Around, State.Height, State.Across));
    }

    private double CollisionHeight()
    {
        foreach (Node child in GetChildren())
        {
            if (child is not CollisionShape3D { Shape: { } shape }) continue;
            return shape switch
            {
                BoxShape3D b => b.Size.Y,
                SphereShape3D sp => sp.Radius * 2.0,
                CapsuleShape3D ca => ca.Height,
                CylinderShape3D cy => cy.Height,
                _ => 1.0,
            };
        }
        return 1.0;
    }
}
