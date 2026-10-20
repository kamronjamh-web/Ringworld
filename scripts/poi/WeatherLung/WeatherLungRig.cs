using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Poi;

/// <summary>Inputs of one rig update (everything the K9 formulas need).</summary>
public struct WeatherLungPose
{
    public double Phase;
    public double Sun;
    public double VaneAmplitudeDeg;
    public double GillAmplitudeDeg;
    public double RippleLag;
    public int TurbineRevolutions;
    public float NozzleEmissionScale;
    public float HeatGridEmissionScale;

    public static WeatherLungPose Defaults => new()
    {
        Phase = 0.0, Sun = 1.0,
        VaneAmplitudeDeg = WeatherLungBreathMath.DefaultVaneAmplitudeDeg, GillAmplitudeDeg = WeatherLungBreathMath.DefaultGillAmplitudeDeg,
        RippleLag = WeatherLungBreathMath.DefaultRippleLag, TurbineRevolutions = 3,
        NozzleEmissionScale = 0.25f, HeatGridEmissionScale = 1.0f,
    };
}

/// <summary>
/// Binds the 59 Moving pivots of WeatherLungDetail.tscn by their <c>wl_*</c> metadata (written by tools/poi/build_weather_lung.gd) and writes
/// ABSOLUTE poses from the breath math (the GLB rest pose is not phase 0, K6). Also drives the shared nozzle and heat-grid materials.
/// No allocation after <see cref="Bind"/>.
/// </summary>
public sealed class WeatherLungRig
{
    public const int Vanes = 5, Gills = 16, Rotors = 2, Rings = 2, Heliostats = 24;

    private readonly Node3D?[] _vane = new Node3D?[Vanes], _ramCyl = new Node3D?[Vanes], _ramRod = new Node3D?[Vanes];
    private readonly Vector3[] _vanePos = new Vector3[Vanes], _vaneAxis = new Vector3[Vanes], _ramCylPos = new Vector3[Vanes], _ramCylAxis = new Vector3[Vanes];
    private readonly Vector3[] _pin = new Vector3[Vanes], _base = new Vector3[Vanes], _ramRodRest = new Vector3[Vanes];
    private readonly double[] _l0 = new double[Vanes];
    private readonly Node3D?[] _gill = new Node3D?[Gills];
    private readonly Vector3[] _gillPos = new Vector3[Gills], _gillAxis = new Vector3[Gills];
    private readonly Node3D?[] _rotor = new Node3D?[Rotors], _ring = new Node3D?[Rings];
    private readonly Vector3[] _rotorPos = new Vector3[Rotors], _rotorAxis = new Vector3[Rotors], _ringPos = new Vector3[Rings], _ringAxis = new Vector3[Rings];
    private readonly Node3D?[] _heli = new Node3D?[Heliostats];
    private readonly Vector3[] _heliPos = new Vector3[Heliostats], _heliAxis = new Vector3[Heliostats];
    private readonly double[] _heliTrack = new double[Heliostats];
    private readonly Material?[] _nozzle = new Material?[WeatherLungBreathMath.NozzleCount];
    private Material? _heat;
    private readonly float[] _nozzleLast = new float[WeatherLungBreathMath.NozzleCount];
    private float _heatLast = -1f;
    private int _bound;

    public bool IsBound => _bound == 59;
    public int PivotCount => _bound;
    public int NozzleMaterialCount { get; private set; }
    public bool HasHeatMaterial => _heat != null;

    /// <summary>Binds from a Detail instance (any node holding Arch/Moving). Returns false and an error when the rig is incomplete.</summary>
    public bool Bind(Node3D detail, out string error)
    {
        if (!BindPivots(detail, out error)) return false;
        BindMaterials(detail);
        MarkBound();
        return true;
    }

    /// <summary>First half of <see cref="Bind"/> (pivots only); <see cref="BindMaterials"/> completes the binding. Split so the streamer can spread the cost over frames.</summary>
    public bool BindPivots(Node3D detail, out string error)
    {
        Unbind();
        error = string.Empty;
        Node? moving = detail.GetNodeOrNull("Arch/Moving");
        if (moving == null) { error = "Arch/Moving not found"; return false; }
        Span<int> counts = stackalloc int[7];
        BindRecursive(moving, counts);
        string[] names = { "vane", "ramcyl", "ramrod", "rotor", "ring", "gill", "heliostat" };
        int[] expect = { Vanes, Vanes, Vanes, Rotors, Rings, Gills, Heliostats };
        for (int i = 0; i < expect.Length; i++)
        {
            if (counts[i] == expect[i]) continue;
            error = $"{names[i]}: {counts[i]} pivots bound, {expect[i]} expected";
            Unbind();
            return false;
        }
        return true;
    }

    /// <summary>Completes a staged bind (<see cref="BindPivots"/> then <see cref="BindMaterials"/>).</summary>
    public void MarkBound() => _bound = 59;

    public void Unbind()
    {
        Array.Clear(_vane); Array.Clear(_ramCyl); Array.Clear(_ramRod); Array.Clear(_gill); Array.Clear(_rotor); Array.Clear(_ring); Array.Clear(_heli);
        Array.Clear(_nozzle);
        _heat = null;
        NozzleMaterialCount = 0;
        _heatLast = -1f;
        Array.Fill(_nozzleLast, -1f);
        _bound = 0;
    }

    private void BindRecursive(Node node, Span<int> counts)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is not Node3D pivot || child is MeshInstance3D || !pivot.HasMeta("wl_kind"))
                continue;
            string kind = pivot.GetMeta("wl_kind").AsString();
            int index = pivot.GetMeta("wl_index").AsInt32();
            Vector3 axis = pivot.GetMeta("wl_axis").AsVector3();
            Vector3 pos = pivot.Position;
            switch (kind)
            {
                case "vane":
                    _vane[index] = pivot; _vanePos[index] = pos; _vaneAxis[index] = axis; counts[0]++;
                    break;
                case "ramcyl":
                    _ramCyl[index] = pivot; _ramCylPos[index] = pos; _ramCylAxis[index] = axis;
                    _pin[index] = pivot.GetMeta("wl_pin").AsVector3();
                    _base[index] = pivot.GetMeta("wl_base").AsVector3();
                    _l0[index] = pivot.GetMeta("wl_l0").AsDouble();
                    counts[1]++;
                    break;
                case "ramrod":
                    _ramRod[index] = pivot; _ramRodRest[index] = pos; counts[2]++;
                    break;
                case "rotor":
                    _rotor[index] = pivot; _rotorPos[index] = pos; _rotorAxis[index] = axis; counts[3]++;
                    break;
                case "ring":
                    _ring[index] = pivot; _ringPos[index] = pos; _ringAxis[index] = axis; counts[4]++;
                    break;
                case "gill":
                    _gill[index] = pivot; _gillPos[index] = pos; _gillAxis[index] = axis; counts[5]++;
                    break;
                case "heliostat":
                    _heli[index] = pivot; _heliPos[index] = pos; _heliAxis[index] = axis;
                    _heliTrack[index] = pivot.GetMeta("wl_track_angle").AsDouble();
                    counts[6]++;
                    break;
            }
            BindRecursive(pivot, counts);
        }
    }

    public void BindMaterials(Node detail)
    {
        var stack = new Stack<Node>();
        stack.Push(detail);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            foreach (Node c in n.GetChildren()) stack.Push(c);
            if (n is not MeshInstance3D { Mesh: { } mesh }) continue;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                if (mesh.SurfaceGetMaterial(s) is not Material material) continue;
                string name = material.ResourceName;
                if (name == "Heat_Exchanger_Glow") _heat = material;
                else if (name.StartsWith("Nozzle_Glow_", StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan("Nozzle_Glow_".Length), out int k) && k >= 0 && k < _nozzle.Length)
                    _nozzle[k] = material;
            }
        }
        NozzleMaterialCount = 0;
        for (int k = 0; k < _nozzle.Length; k++) if (_nozzle[k] != null) NozzleMaterialCount++;
    }

    /// <summary>Writes every pivot pose and the emissive materials for <paramref name="pose"/>. A pure function of the pose (no history).</summary>
    public void Apply(in WeatherLungPose pose)
    {
        if (!IsBound) return;
        double p = pose.Phase;
        for (int i = 0; i < Vanes; i++)
        {
            double th = WeatherLungBreathMath.Vane(p, i, pose.VaneAmplitudeDeg, pose.RippleLag);
            _vane[i]!.Transform = new Transform3D(new Basis(_vaneAxis[i], (float)th), _vanePos[i]);
            WeatherLungBreathMath.Ram(th, _vanePos[i], _pin[i], _base[i], _l0[i], out double psi, out double rodX);
            _ramCyl[i]!.Transform = new Transform3D(new Basis(_ramCylAxis[i], (float)psi), _ramCylPos[i]);
            Vector3 rest = _ramRodRest[i];
            _ramRod[i]!.Position = new Vector3((float)rodX, rest.Y, rest.Z);
        }
        for (int i = 0; i < Gills; i++)
            _gill[i]!.Transform = new Transform3D(new Basis(_gillAxis[i], (float)WeatherLungBreathMath.Gill(p, i, pose.GillAmplitudeDeg, pose.RippleLag)), _gillPos[i]);
        for (int i = 0; i < Rotors; i++)
        {
            double a = WeatherLungBreathMath.Turbine(p, i == 0 ? 1 : -1, pose.TurbineRevolutions) % (Math.PI * 2.0);
            _rotor[i]!.Transform = new Transform3D(new Basis(_rotorAxis[i], (float)a), _rotorPos[i]);
        }
        for (int i = 0; i < Rings; i++)
            _ring[i]!.Transform = new Transform3D(new Basis(_ringAxis[i], (float)WeatherLungBreathMath.Ring(p, i == 0 ? 1 : -1)), _ringPos[i]);
        for (int i = 0; i < Heliostats; i++)
            _heli[i]!.Transform = new Transform3D(new Basis(_heliAxis[i], (float)WeatherLungBreathMath.Heliostat(pose.Sun, _heliTrack[i])), _heliPos[i]);

        for (int k = 0; k < _nozzle.Length; k++)
        {
            Material? m = _nozzle[k];
            if (m == null) continue;
            float e = pose.NozzleEmissionScale * (float)WeatherLungBreathMath.Nozzle(p, k);
            if (MathF.Abs(e - _nozzleLast[k]) > 1e-3f) { PoiMaterialParams.SetEmissionEnergy(m, e); _nozzleLast[k] = e; }
        }
        if (_heat != null)
        {
            float h = pose.HeatGridEmissionScale * (float)WeatherLungBreathMath.HeatGrid(p, pose.Sun);
            if (MathF.Abs(h - _heatLast) > 1e-3f) { PoiMaterialParams.SetEmissionEnergy(_heat, h); _heatLast = h; }
        }
    }
}
