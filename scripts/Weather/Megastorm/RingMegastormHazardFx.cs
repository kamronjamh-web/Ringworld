using System;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo;

/// <summary>
/// Hazard visuals of the megastorm (docs/megastorm-design.md 5.5, WO11). Publishes the blast globals the tree, grass and ocean readers use
/// (<c>ring_megastorm_blast*</c>, materials/ring_megastorm_blast.gdshaderinc), feeds the gust debris node a burst level (the dust sheet at the boom and the
/// wind-carried sheet of the wall and the reversed wake), and attaches the shock distortion compositor effect while the front is within 6 km ahead to 1.5 km behind.
/// Everything is a function of <see cref="MegastormSample"/> and <see cref="MegastormHazardFx"/>, so it follows a rewind; untriggered it costs one bool check per
/// frame and writes the zero state once when a storm ends (the globals' project defaults are 0).
/// </summary>
public partial class RingMegastormHazardFx : Node
{
    public const string BlastGlobal = "ring_megastorm_blast", BlastNGlobal = "ring_megastorm_blast_n", BlastUpGlobal = "ring_megastorm_blast_up", BlastOriginGlobal = "ring_megastorm_blast_origin";
    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath MegastormPath { get; set; } = new("../RingMegastorm");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath DebrisPath { get; set; } = new("../GustFrontDebris");

    /// <summary>Test seam: replaces the query at the explorer.</summary>
    public Func<MegastormSample>? SampleProvider { get; set; }
    /// <summary>Test seam: the canonical observer when there is no explorer.</summary>
    public Func<RingCanonicalPosition>? ObserverProvider { get; set; }

    private RingMegastorm? _megastorm;
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private RingGustFrontDebris? _debris;
    private CompositorEffect? _shock;
    private WorldEnvironment? _shockEnvironment;
    private bool _published, _shockAttached;
    private readonly float[] _shockParams = new float[12];

    /// <summary>Last published blast state (x strength, y front offset, z bend angle, w slam).</summary>
    public Vector4 LastBlast { get; private set; }
    public Vector3 LastN { get; private set; }
    public int WriteCount { get; private set; }
    public float LastBurst { get; private set; }
    public float LastDebrisLevel { get; private set; }
    public float LastShockAmplitude { get; private set; }
    public bool ShockAttached => _shockAttached;
    public long ShockDispatches => _shock != null ? _shock.Get("dispatches").AsInt64() : 0;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _megastorm = GetNodeOrNull<RingMegastorm>(MegastormPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        _debris = GetNodeOrNull<RingGustFrontDebris>(DebrisPath);
    }

    public override void _Process(double delta)
    {
        if (GetTree().Paused) return;
        MegastormSample s = default;
        RingCanonicalPosition observer = default;
        bool have = false;
        if (Enabled)
        {
            if (SampleProvider != null) { s = SampleProvider(); observer = ObserverProvider?.Invoke() ?? default; have = s.Active; }
            else if (_megastorm != null && _explorer != null && _megastorm.Triggered)
            {
                observer = _explorer.CanonicalPosition;
                s = _megastorm.QueryNow(observer);
                have = s.Active;
            }
        }
        Step(have ? s : default, observer);
    }

    public override void _ExitTree()
    {
        Step(default, default);
        DetachShock();
        if (_shock != null) { _shock.Dispose(); _shock = null; }
    }

    /// <summary>One update; public so a test can drive it. An inactive sample is the rest state.</summary>
    public void Step(in MegastormSample s, RingCanonicalPosition observer)
    {
        bool active = s.Active;
        double xi = s.XiMeters;
        float strength = active ? MegastormHazardFx.BlastStrength(s.Hazards.BoomPeakKPa) : 0.0f;
        bool blast = active && strength > 0.0f && MegastormHazardFx.BlastZone(xi);

        // Debris: the burst at the peak and the wind-carried sheet.
        double speed = Math.Max(1.0, s.SpeedMetersPerSecond);
        double front = active ? MegastormHazardFx.FrontOffset(xi, speed) : 0.0;
        float burst = active ? MegastormHazardFx.BurstLevel((front - MegastormHazardFx.BlastRiseMeters * 0.5) / speed, strength) : 0.0f;
        float level = active ? Math.Max(burst, MegastormHazardFx.WindDebrisLevel(xi, 1.0f)) : 0.0f;
        LastBurst = burst; LastDebrisLevel = level;
        if (_debris != null && GodotObject.IsInstanceValid(_debris)) { _debris.MegastormLevel = level; _debris.MegastormDust = burst; }

        if (_grid == null) { if (!blast && _published) WriteBlast(default, Vector3.Zero, Vector3.Zero, Vector3.Zero); ShockStep(false, s, default, default, default, 0.0f); return; }
        if (!blast)
        {
            if (_published) { _published = false; WriteBlast(new Vector4(0.0f, 0.0f, MegastormHazardFx.BlastBendRadians, 0.0f), LastN, Vector3.Up, Vector3.Zero); }
            ShockStep(false, s, default, default, default, 0.0f);
            return;
        }
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Vector3 origin = geometry.CanonicalToWorld(observer.AroundMeters, observer.VerticalMeters, observer.AcrossMeters);
        Vector3 n = (geometry.CanonicalToWorld(observer.AroundMeters + 10.0, observer.VerticalMeters, observer.AcrossMeters) - origin).Normalized() * Math.Sign(s.Direction == 0 ? 1 : s.Direction);
        Vector3 up = (geometry.CanonicalToWorld(observer.AroundMeters, observer.VerticalMeters + 10.0, observer.AcrossMeters) - origin).Normalized();
        _published = true;
        WriteBlast(new Vector4(strength, (float)front, MegastormHazardFx.BlastBendRadians, strength), n, up, origin);
        ShockStep(MegastormHazardFx.ShockZone(xi), s, origin, n, up, strength);
    }

    private void WriteBlast(Vector4 blast, Vector3 n, Vector3 up, Vector3 origin)
    {
        LastBlast = blast; LastN = n; WriteCount++;
        RenderingServer.GlobalShaderParameterSet(BlastGlobal, blast);
        if (blast.X > 0.0f)
        {
            RenderingServer.GlobalShaderParameterSet(BlastNGlobal, n);
            RenderingServer.GlobalShaderParameterSet(BlastUpGlobal, up);
            RenderingServer.GlobalShaderParameterSet(BlastOriginGlobal, origin);
        }
    }

    private void ShockStep(bool wanted, in MegastormSample s, Vector3 origin, Vector3 n, Vector3 up, float strength)
    {
        float setting = RingMegastormSettings.Current.ShockDistortion;
        float amplitude = 0.0f;
        if (wanted)
        {
            int height = (int)(GetViewport()?.GetVisibleRect().Size.Y ?? 1080.0f);
            amplitude = MegastormHazardFx.ShockAmplitudePixels(setting, strength, LightningVisualPolicy.ReducedFlashes, s.XiMeters, height);
        }
        LastShockAmplitude = amplitude;
        if (amplitude <= 0.0f) { DetachShock(); return; }
        if (!_shockAttached && !AttachShock()) return;
        double speed = Math.Max(1.0, s.SpeedMetersPerSecond);
        float[] p = { n.X, n.Y, n.Z, (float)MegastormHazardFx.FrontOffset(s.XiMeters, speed), origin.X, origin.Y, origin.Z, amplitude,
            MegastormHazardFx.ShockBandHalfWidthMeters, MegastormHazardFx.ShockTailWidthMeters, 0.0f, 0.0f };
        for (int i = 0; i < p.Length; i++) _shockParams[i] = p[i];
        _shock!.Call("publish_frame", _shockParams, true);
    }

    private bool AttachShock()
    {
        if (RenderingServer.GetRenderingDevice() == null) return false;
        Viewport? viewport = GetViewport();
        if (viewport == null) return false;
        _shock ??= GD.Load<GDScript>("res://scripts/render/RingShockDistortionComposite.gd")?.New().AsGodotObject() as CompositorEffect;
        if (_shock == null) return false;
        if (_shockEnvironment == null || !GodotObject.IsInstanceValid(_shockEnvironment))
        {
            _shockEnvironment = null;
            foreach (Node candidate in viewport.FindChildren("*", "WorldEnvironment", true, false))
                if (candidate is WorldEnvironment owner && owner.GetViewport() == viewport) { _shockEnvironment = owner; break; }
        }
        if (_shockEnvironment == null) return false;
        _shockEnvironment.Compositor ??= new Compositor();
        var effects = _shockEnvironment.Compositor.CompositorEffects;
        if (!effects.Contains(_shock))
        {
            // Before the rain streak pass and the rain haze composite (which keeps itself last), so the haze still sits over the refracted scene.
            int index = effects.Count;
            for (int i = 0; i < effects.Count; i++)
            {
                string path = (effects[i].GetScript().As<Script>()?.ResourcePath) ?? "";
                if (path.EndsWith("RingRainStreaksComposite.gd") || path.EndsWith("RingRainHazeComposite.gd")) { index = i; break; }
            }
            effects.Insert(index, _shock);
            _shockEnvironment.Compositor.CompositorEffects = effects;
        }
        _shockAttached = true;
        return true;
    }

    private void DetachShock()
    {
        if (!_shockAttached) return;
        _shockAttached = false;
        _shock?.Call("publish_frame", _shockParams, false);
        if (_shockEnvironment != null && GodotObject.IsInstanceValid(_shockEnvironment) && _shockEnvironment.Compositor != null && _shock != null)
        {
            var effects = _shockEnvironment.Compositor.CompositorEffects;
            if (effects.Remove(_shock)) _shockEnvironment.Compositor.CompositorEffects = effects;
        }
    }

    public Godot.Collections.Dictionary GetHazardFxDiagnostics() => new()
    {
        ["blast"] = LastBlast, ["n"] = LastN, ["writes"] = WriteCount, ["burst"] = LastBurst, ["debris_level"] = LastDebrisLevel,
        ["shock_amplitude"] = LastShockAmplitude, ["shock_attached"] = _shockAttached, ["shock_dispatches"] = ShockDispatches
    };
}
