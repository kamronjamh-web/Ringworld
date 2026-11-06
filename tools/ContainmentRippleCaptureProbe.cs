using System;
using System.Text;
using Godot;
using RingworldTechDemo;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

/// <summary>
/// Typed access for tools/containment_ripple_capture.gd (containment field WO4 captures, debug only): canonical-to-world, the camera canonical pose, aim angles,
/// debug spawns on the real field node, the bus counters per kind and the ripple diagnostics line. Reads the real game's field and bus (nothing is installed).
/// </summary>
public partial class ContainmentRippleCaptureProbe : RefCounted
{
    private static RingContainmentField? Field(Node world) => world.GetNodeOrNull<RingContainmentField>("ContainmentField");
    private static VoxelChunkGridManager? Grid(Node world) => world.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");

    public bool Ready(Node world) => Field(world) != null && RingContainmentDisturbanceHost.SharedBus != null && RingContainmentDisturbanceHost.Instance?.Profile is { IsValid: true };

    public Godot.Collections.Dictionary ProfileInfo(Node world)
    {
        var d = new Godot.Collections.Dictionary();
        if (RingContainmentDisturbanceHost.Instance?.Profile is not { } p) return d;
        d["wall_top"] = p.WallTopMeters; d["apex"] = p.ApexMeters; d["width"] = p.WidthMeters;
        return d;
    }

    public double FieldHeight(Node world, double across)
        => RingContainmentDisturbanceHost.Instance?.Profile is { } p ? ContainmentFieldGeometry.HeightAt(p, across) : double.NaN;

    /// <summary>Across (on the across-0 side) where the field is at height h.</summary>
    public double FieldAcrossAt(Node world, double heightMeters)
    {
        if (RingContainmentDisturbanceHost.Instance?.Profile is not { } p) return double.NaN;
        double lo = 0.0, hi = p.WidthMeters * 0.5;   // the height rises monotonically from the skirt to the apex
        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (ContainmentFieldGeometry.HeightAt(p, mid) < heightMeters) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    public Vector3 ToWorld(Node world, double around, double vertical, double across)
        => Grid(world)?.CanonicalRingPositionToWorld(new RingCanonicalPosition(around, vertical, across)) ?? Vector3.Zero;

    public Godot.Collections.Dictionary CameraCanonical(Node world)
    {
        var d = new Godot.Collections.Dictionary();
        Camera3D? cam = world.GetViewport()?.GetCamera3D();
        VoxelChunkGridManager? grid = Grid(world);
        if (cam == null || grid == null) return d;
        RingCanonicalPosition c = grid.WorldToCanonicalRingPosition64(cam.GlobalPosition);
        d["around"] = c.AroundMeters; d["vertical"] = c.VerticalMeters; d["across"] = c.AcrossMeters;
        return d;
    }

    /// <summary>(yaw_deg, pitch_deg) from the camera to a canonical point: yaw from the canonical deltas (0 = +around, +90 = +across), pitch from the world vector against the surface up.</summary>
    public Vector2 AimAngles(Node world, double around, double vertical, double across)
    {
        Camera3D? cam = world.GetViewport()?.GetCamera3D();
        VoxelChunkGridManager? grid = Grid(world);
        var explorer = world.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        if (cam == null || grid == null || explorer == null) return Vector2.Zero;
        RingCanonicalPosition c = grid.WorldToCanonicalRingPosition64(cam.GlobalPosition);
        double da = MegastormModel.WrapSigned(around - c.AroundMeters, grid.GetCanonicalRingCircumferenceMeters64()), dz = across - c.AcrossMeters;
        double yaw = Math.Atan2(dz, da) * 180.0 / Math.PI;
        Vector3 up = explorer.GlobalTransform.Basis.Y;
        Vector3 d = grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(around, vertical, across)) - cam.GlobalPosition;
        double horizontal = (d - up * d.Dot(up)).Length();
        double pitch = Math.Atan2(d.Dot(up), horizontal) * 180.0 / Math.PI;
        return new Vector2((float)yaw, (float)Math.Clamp(pitch, -85.0, 85.0));
    }

    public string SpawnFieldStrike(Node world, double around, double across, double energy)
    {
        RingContainmentField? f = Field(world);
        if (f == null) return "nofield";
        return f.SpawnDebugFieldStrike(new RingCanonicalPosition(around, FieldHeight(world, across), across), (float)energy).ToString();
    }

    public string SpawnLightningRipple(Node world, double around, double across)
    {
        RingContainmentField? f = Field(world);
        if (f == null) return "nofield";
        return f.SpawnDebugRipple(new RingCanonicalPosition(around, FieldHeight(world, across), across), 1.0f, 8000.0f, 4.0f, 0.3f, ContainmentDisturbanceKind.Lightning).ToString();
    }

    public void ForceRipples(Node world, double around, double across, int count, double spacing, bool lit)
        => Field(world)?.ForceDebugRipples(count, new RingCanonicalPosition(around, FieldHeight(world, across), across), spacing, lit);

    public void ClearForced(Node world) => Field(world)?.ClearDebugRipples();

    public void SetRippleStrength(Node world, double v) { if (Field(world) is { } f) f.RippleStrength = (float)v; }
    public void SetFieldEnabled(Node world, bool on) { if (Field(world) is { } f) f.Enabled = on; }

    public void SetStrikeScale(float frequency, bool reduced) => LightningVisualPolicy.Apply(frequency, 1.0f, reduced);

    public int Live(Node world) => RingContainmentDisturbanceHost.SharedBus?.LiveCount ?? -1;
    public int LiveOfKind(Node world, int kind) => RingContainmentDisturbanceHost.SharedBus?.LiveOf((ContainmentDisturbanceKind)kind) ?? 0;
    public int FarCount(Node world) => RingContainmentDisturbanceHost.SharedBus?.FarStrikes.Count ?? 0;

    /// <summary>True while the id is in a bus slot (or, when <paramref name="far"/>, in the far list).</summary>
    public bool Alive(Node world, long id, bool far)
    {
        ContainmentDisturbanceBus? bus = RingContainmentDisturbanceHost.SharedBus;
        if (bus == null) return false;
        if (far) { foreach (ContainmentFarStrike s in bus.FarStrikes) if (s.SourceId == id) return true; return false; }
        foreach (ContainmentRipple? r in bus.Slots) if (r != null && r.SourceId == id) return true;
        return false;
    }

    /// <summary>
    /// The newest live ripple of the kind (0 = FieldStrike, bus slots first, then the far list) whose id is not in <paramref name="seen"/>:
    /// {found, id, far, around, vertical, across, distance, age}; found false when none.
    /// </summary>
    public Godot.Collections.Dictionary Newest(Node world, int kind, Godot.Collections.Array<long> seen)
    {
        var result = new Godot.Collections.Dictionary { ["found"] = false };
        ContainmentDisturbanceBus? bus = RingContainmentDisturbanceHost.SharedBus;
        VoxelChunkGridManager? grid = Grid(world);
        var explorer = world.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        if (bus == null || grid == null || explorer == null) return result;
        ContainmentDisturbanceKind k = kind == 0 ? ContainmentDisturbanceKind.FieldStrike : (ContainmentDisturbanceKind)kind;
        double circumference = grid.GetCanonicalRingCircumferenceMeters64(), now = bus.Now, best = double.MinValue;
        RingCanonicalPosition o = explorer.CanonicalPosition;
        foreach (ContainmentRipple? r in bus.Slots)
        {
            if (r == null || r.Kind != k || seen.Contains(r.SourceId) || r.StartTimeSeconds <= best) continue;
            best = r.StartTimeSeconds;
            Fill(result, r.SourceId, false, r.Surface, r.StartTimeSeconds, now, o, circumference);
        }
        if (kind == 0 && !(bool)result["found"])
            foreach (ContainmentFarStrike s in bus.FarStrikes)
            {
                if (seen.Contains(s.SourceId) || s.StartTimeSeconds <= best) continue;
                best = s.StartTimeSeconds;
                Fill(result, s.SourceId, true, s.Surface, s.StartTimeSeconds, now, o, circumference);
            }
        return result;
    }

    private static void Fill(Godot.Collections.Dictionary d, long id, bool far, ContainmentFieldPoint p, double start, double now, RingCanonicalPosition o, double circumference)
    {
        double da = MegastormModel.WrapSigned(p.AroundMeters - o.AroundMeters, circumference), dz = p.AcrossMeters - o.AcrossMeters, dv = p.VerticalMeters - o.VerticalMeters;
        d["found"] = true; d["id"] = id; d["far"] = far; d["around"] = p.AroundMeters; d["vertical"] = p.VerticalMeters; d["across"] = p.AcrossMeters;
        d["distance"] = Math.Sqrt(da * da + dz * dz + dv * dv); d["age"] = now - start;
    }

    /// <summary>One line: weather time, bus live count and per-kind counts, far list, the field ripple diagnostics.</summary>
    public string DiagLine(Node world)
    {
        ContainmentDisturbanceBus? bus = RingContainmentDisturbanceHost.SharedBus;
        RingContainmentField? f = Field(world);
        var clouds = world.GetNodeOrNull<RingVolumetricCloudLayer>("RingVolumetricClouds");
        var sb = new StringBuilder();
        sb.Append($"t={clouds?.CloudSimulationTimeSeconds ?? double.NaN:0.000} live={bus?.LiveCount ?? -1} kinds=[");
        if (bus != null)
            foreach (ContainmentDisturbanceKind k in Enum.GetValues(typeof(ContainmentDisturbanceKind)))
            {
                int n = bus.LiveOf(k);
                if (n > 0) sb.Append($"{k}:{n} ");
            }
        sb.Append($"] far={bus?.FarStrikes.Count ?? -1}");
        if (f != null)
            sb.Append($" patches={f.RipplePatchesDrawn} post={f.RipplePostDrawn} near={f.NearRippleCount} farquads={f.FarStrikeQuadsDrawn} above={f.RippleSideAbove} us={f.LastRippleMicroseconds:0.0} rebuilds={f.RippleRebuildCount} strength={f.RippleStrength:0.##} enabled={f.Enabled} farlayer={(f.FarStrikeLayer != null)} camfar={world.GetViewport()?.GetCamera3D()?.Far:0}");
        else sb.Append(" nofield");
        return sb.ToString();
    }

    public double CameraFar(Node world) => world.GetViewport()?.GetCamera3D()?.Far ?? 0.0;

    public double LastRippleMicroseconds(Node world) => Field(world)?.LastRippleMicroseconds ?? -1.0;
}
