using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>Windowed walk test of Cards adaptive grouping (grass-cards S6 plan, C.5-2): Cards at reach 400 with GPU culling
/// and grouping on, an eye-height camera on the focus looking along +around. It walks 300 m out and back at a fixed speed
/// and audits every frame that each wanted, in-view leaf is drawn exactly once (its own near output or an active owner
/// group, never both, never neither after it was drawn), that each group splits at most once per pass, and that no split is
/// caused by a tier change. Then it oscillates +-(grouping hysteresis / 2) around a split and an admission boundary
/// (0 group transitions after warm-up), and +-(streaming hysteresis / 2 - 0.25) there (0 builds). Optionally repeats the
/// walk with 4x4 groups. Run via tools/grass_card_grouping_walk_test.gd (windowed).</summary>
public partial class GrassCardGroupingWalkTest : Node
{
    [Signal] public delegate void CompletedEventHandler(bool passed);
    private const long Circumference = 981_748L * 32L;
    private const int Width = 318_000;
    private const float EyeHeight = 1.7f;
    private const float PitchDegrees = 3f;
    public bool Exercise4x4 { get; set; } = true;
    public float MetersPerFrame { get; set; } = .1f;
    /// <summary>When false, one-frame holes on a tier handoff (pre-existing GPU re-registration gap) are reported, not gated.</summary>
    public bool GateTierHandoff { get; set; } = true;
    private static bool _gateTierHandoff = true;

    private RingStandingGrassLayer _layer = null!;
    private RingTerrainSampler _sampler = null!;
    private RingLodSurfaceEditSnapshot _clean = null!;
    private RingLodGeometrySnapshot _geometry;
    private Camera3D _camera = null!;
    private RingCanonicalPosition _home;
    private double _around;
    private int _frame;
    private string _phase = "settle";
    private bool _ok = true;
    private readonly List<(int Frame, string Phase, RingGrassGroupKey Key, string What, bool Active, double Around)> _events = new();
    private Dictionary<RingGrassTileKey, LeafState> _leafState = new();
    private Dictionary<RingGrassTileKey, LeafState> _leafScratch = new();
    private readonly Dictionary<string, Stats> _stats = new();

    private readonly record struct LeafState(bool Must, bool Covered, bool EverCovered, int Streak, ulong Hash, bool Owned, int DataChangeFrame, int OwnerLossFrame);
    private sealed class Stats
    {
        public int Frames, Doubles, PendingVisible, Leaked, TransitionGaps, OwnerGaps, UnreadyVisible, UncoveredLeafFrames;
        public int TierHandoffGaps, GroupLossGaps, OtherGaps;
        public int MaxTransitionStreak, MaxLatencyStreak, OwnershipFailures, MaxMust;
        public double MinCoverage = 1;
        public readonly List<string> Samples = new();
        public bool CoverageOk => Doubles == 0 && PendingVisible == 0 && Leaked == 0 && GroupLossGaps == 0 && OtherGaps == 0 && (!_gateTierHandoff || TierHandoffGaps == 0) && OwnerGaps == 0 && UnreadyVisible == 0 && OwnershipFailures == 0;
        public override string ToString() => $"frames={Frames} doubles={Doubles} pending_visible={PendingVisible} leaked_leaf={Leaked} transition_gaps={TransitionGaps} "
            + $"(tier_handoff={TierHandoffGaps} group_loss={GroupLossGaps} other={OtherGaps}) "
            + $"owner_gaps={OwnerGaps} unready_visible={UnreadyVisible} ownership_failures={OwnershipFailures} uncovered_leaf_frames={UncoveredLeafFrames} "
            + $"max_latency_streak={MaxLatencyStreak} max_transition_streak={MaxTransitionStreak} min_coverage={MinCoverage:P1} max_in_view_leaves={MaxMust}";
    }

    public override async void _Ready()
    {
        try { EmitSignal(SignalName.Completed, await RunAsync()); }
        catch (Exception exception) { GD.PushError("GRASS_CARD_WALK=FAIL " + exception); EmitSignal(SignalName.Completed, false); }
    }

    private Stats S(string phase) => _stats.TryGetValue(phase, out var s) ? s : _stats[phase] = new Stats();

    private async Task<bool> RunAsync()
    {
        _gateTierHandoff = GateTierHandoff;
        var settings = (VoxelTerrainSettings)GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres").Duplicate(true);
        using var sampler = new RingTerrainSampler(settings, true, Circumference, 5_000_000.0, Width, 8000);
        _sampler = sampler;
        await Task.Run(() => RingGrassCardAssets.GetBlocking(RingGrassCardAtlasParameters.VariantD));
        _clean = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        _home = new RingCanonicalPosition(186_000.0 + 24, 0, 141_000.0 + 24);
        _geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, Circumference / Math.Tau, 0, 0, Circumference, -1000, 1000, _home);
        _camera = new Camera3D { Current = true, Far = 800, Fov = 70 };
        AddChild(_camera);
        AddChild(new DirectionalLight3D { LightEnergy = 1.2f });
        _layer = new RingStandingGrassLayer { GrassRenderStyle = RingStandingGrassLayer.GrassRenderStyleKind.Cards, GrassCardReachMeters = 400,
            MaximumTileBuildsInFlight = 4, MaximumTileUploadsPerFrame = 8, MaximumTileUploadMilliseconds = 20 };
        AddChild(_layer);
        _layer.InitializeForTests();
        _layer.AdaptiveGroupEventForTests = (key, what, active) => _events.Add((_frame, _phase, key, what, active, _around - _home.AroundMeters));
        Place(_home.AroundMeters);
        await Settle("settle", 300_000);
        var profile = _layer.StyleProfile;
        GD.Print($"GRASS_CARD_WALK setup reach={_layer.GrassCardReachMeters} effective_start={_layer.EffectiveGrassGroupingStartMeters:F1} far_start={_layer.EffectiveGrassGroupingFarStartMeters:F1} "
            + $"group_hysteresis={_layer.GrassGroupingHysteresisMeters} last_tier_start={profile.LastTierStartMeters:F1} streaming_H={profile.HysteresisMeters} lookahead={profile.LookaheadMeters} "
            + $"tiles={_layer.ResidentGrassTileCount} groups={_layer.AdaptiveGrassGroupCount} batches={_layer.GpuCullingBatchCount} status='{_layer.GpuCullingStatus}' grouping='{_layer.AdaptiveGrassGroupingStatus}' speed={MetersPerFrame}m/frame");
        _ok &= _layer.GpuCullingStatus.StartsWith("Cards: GPU") && _layer.AdaptiveGrassGroupCount > 0;
        Report("settle", false);

        // ---- Walk 300 m out and back (2x2 groups) ----
        double home = _home.AroundMeters;
        await Walk("walk_out", home, home + 300);
        await Walk("walk_back", home + 300, home);
        _ok &= Report("walk_out", true) & Report("walk_back", true);

        // ---- Oscillation around a group split boundary and an admission boundary ----
        float groupHalf = _layer.GrassGroupingHysteresisMeters * .5f;
        float streamHalf = Math.Max(.25f, profile.HysteresisMeters * .5f - .25f);
        var split = _events.Where(e => e.Phase == "walk_out" && e.What == "split:distance" && e.Active).Select(e => (double?)e.Around).FirstOrDefault();
        var admit = _events.Where(e => e.Phase == "walk_back" && e.What == "create").Select(e => (double?)e.Around).FirstOrDefault();
        if (split is double s0)
        {
            _ok &= await Oscillate("osc_split_group", home + s0, groupHalf, gateBuilds: false);
            _ok &= await Oscillate("osc_split_stream", home + s0, streamHalf, gateBuilds: true);
        }
        else { GD.PushError("no outbound distance split to oscillate around"); _ok = false; }
        if (admit is double a0)
        {
            _ok &= await Oscillate("osc_admit_group", home + a0, groupHalf, gateBuilds: false);
            _ok &= await Oscillate("osc_admit_stream", home + a0, streamHalf, gateBuilds: true);
        }
        else { GD.PushError("no return-pass group creation to oscillate around"); _ok = false; }

        // ---- 4x4 groups ----
        if (Exercise4x4)
        {
            await Walk("to_home_4x4", _around, home);
            _layer.GrassGroupingMaximumLevel = 2;
            await Settle("settle_4x4", 300_000);
            int level2 = _layer.AdaptiveGrassLevel2GroupCount;
            GD.Print($"GRASS_CARD_WALK 4x4 settled groups={_layer.AdaptiveGrassGroupCount} 2x2={_layer.AdaptiveGrassLevel1GroupCount} 4x4={level2} batches={_layer.GpuCullingBatchCount}");
            _ok &= Report("settle_4x4", false);
            await Walk("walk4_out", home, home + 300);
            await Walk("walk4_back", home + 300, home);
            int created4 = _events.Count(e => e.Phase.StartsWith("walk4") && e.Key.Level == 2 && e.What == "activate");
            GD.Print($"GRASS_CARD_WALK 4x4 walk level2_activations={created4} settled_level2={level2}");
            _ok &= Report("walk4_out", true) & Report("walk4_back", true);
            if (level2 == 0 && created4 == 0) GD.Print("GRASS_CARD_WALK 4x4 NOTE: no 4x4 group ever formed at reach 400 (informational)");
        }
        GD.Print(_ok ? "GRASS_CARD_WALK=PASS" : "GRASS_CARD_WALK=FAIL");
        _layer.AdaptiveGroupEventForTests = null;
        _layer.QueueFree();
        return _ok;
    }

    private void Place(double around)
    {
        _around = around;
        double across = _home.AcrossMeters;
        float height = _sampler.GetSurfaceHeight((long)Math.Floor(around), (int)Math.Floor(across));
        Vector3 eye = _geometry.CanonicalToWorld(around, height + EyeHeight, across);
        double angle = around / _geometry.RingRadiusMeters;
        Vector3 up = new((float)-Math.Cos(angle), 0f, (float)-Math.Sin(angle));
        Vector3 forward = new((float)-Math.Sin(angle), 0f, (float)Math.Cos(angle));
        float pitch = Mathf.DegToRad(PitchDegrees);
        Vector3 look = (forward * Mathf.Cos(pitch) - up * Mathf.Sin(pitch)).Normalized();
        _camera.GlobalTransform = new Transform3D(Basis.LookingAt(look, up), eye);
    }

    private async Task Step()
    {
        _layer.StepForTests(_sampler, _clean, new RingCanonicalPosition(_around, 0, _home.AcrossMeters), _geometry);
        Audit();
        _frame++;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task Walk(string phase, double from, double to)
    {
        _phase = phase;
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(to - from) / Math.Max(.01f, MetersPerFrame)));
        for (int i = 1; i <= steps; i++)
        {
            Place(from + (to - from) * i / steps);
            await Step();
        }
    }

    private async Task Settle(string phase, int milliseconds)
    {
        _phase = phase;
        ulong until = Time.GetTicksMsec() + (ulong)milliseconds;
        long lastCompleted = -1; int stable = 0;
        for (int i = 0; ; i++)
        {
            await Step();
            long completed = _layer.GpuCullingCompletedUpdates;
            stable = completed == lastCompleted ? stable + 1 : 0; lastCompleted = completed;
            if (i > 20 && stable >= 30 && _layer.PendingGrassTileBuildCount == 0 && _layer.PendingGrassUploadCount == 0 && _layer.IsAdaptiveGrassGroupingSettled) return;
            if (Time.GetTicksMsec() > until)
                throw new TimeoutException($"{phase} did not settle: builds={_layer.PendingGrassTileBuildCount} uploads={_layer.PendingGrassUploadCount} groupsSettled={_layer.IsAdaptiveGrassGroupingSettled} groups={_layer.AdaptiveGrassGroupCount}");
        }
    }

    private async Task<bool> Oscillate(string phase, double center, double amplitude, bool gateBuilds)
    {
        await Walk(phase + "_approach", _around, center - amplitude);
        await Settle(phase + "_settle", 120_000);
        double speed = Math.Max(.01f, MetersPerFrame);
        int half = Math.Max(2, (int)Math.Ceiling(2 * amplitude / speed));
        async Task Cycles(int count)
        {
            for (int c = 0; c < count; c++)
                for (int i = 1; i <= 2 * half; i++)
                {
                    double t = i <= half ? (double)i / half : 2.0 - (double)i / half;
                    Place(center - amplitude + 2 * amplitude * t);
                    await Step();
                }
        }
        _phase = phase + "_warm";
        await Cycles(2);
        _phase = phase;
        int builds = _layer.TotalGrassBuildCount; long uploads = _layer.TotalGrassUploadCount; int filters = _layer.GrassTierFilterCount;
        int eventsBefore = _events.Count;
        int frames = Math.Max(1, (int)Math.Ceiling(30.0 * 60.0 / (2 * half)));
        await Cycles(frames);
        var transitions = _events.Skip(eventsBefore).ToList();
        int dBuilds = _layer.TotalGrassBuildCount - builds, dFilters = _layer.GrassTierFilterCount - filters; long dUploads = _layer.TotalGrassUploadCount - uploads;
        Stats s = S(phase);
        // The group amplitude exceeds the streaming hysteresis, so reach-edge eviction (leaf_removed) and tier churn are expected there;
        // only distance/config transitions are gated. The streaming amplitude gates every transition and every build.
        int gatedTransitions = gateBuilds ? transitions.Count : transitions.Count(e => e.What is "split:distance" or "split:config" or "split:disabled" or "retire:pending" or "retire:beyond");
        bool pass = gatedTransitions == 0 && s.CoverageOk && (!gateBuilds || (dBuilds == 0 && dFilters == 0));
        string kinds = string.Join(",", transitions.GroupBy(e => e.What).Select(g => $"{g.Key}x{g.Count()}"));
        GD.Print($"GRASS_CARD_WALK {phase} center={center - _home.AroundMeters:F1}m amplitude=+-{amplitude:F2}m cycles={frames} group_transitions={transitions.Count} [{kinds}] "
            + $"builds+{dBuilds} uploads+{dUploads} tier_filters+{dFilters} gate_builds={gateBuilds} {s} ok={pass}");
        foreach (string sample in s.Samples) GD.Print("  " + sample);
        return pass;
    }

    private bool Report(string phase, bool gateSplits)
    {
        Stats s = S(phase);
        var events = _events.Where(e => e.Phase == phase).ToList();
        var splitsPerGroup = events.Where(e => e.What.StartsWith("split:") && e.Active).GroupBy(e => e.Key).Select(g => g.Count()).ToList();
        int maxSplits = splitsPerGroup.Count == 0 ? 0 : splitsPerGroup.Max();
        var nonUpgrade = events.Where(e => e.What.StartsWith("split:") && e.What != "split:upgrade" && e.Active).GroupBy(e => e.Key).Select(g => g.Count()).ToList();
        int maxNonUpgrade = nonUpgrade.Count == 0 ? 0 : nonUpgrade.Max();
        GD.Print($"GRASS_CARD_WALK {phase} max_splits_per_group_excluding_4x4_upgrade={maxNonUpgrade}");
        int tierSplits = events.Count(e => e.What is "split:leaf_changed" or "split:source_replaced");
        string histogram = string.Join(",", events.GroupBy(e => (e.What, e.Active)).OrderBy(g => g.Key.What).Select(g => $"{g.Key.What}{(g.Key.Active ? "" : "(pending)")}x{g.Count()}"));
        // A 2x2 group promoted into a 4x4 ("split:upgrade") is a level change, reported separately from the once-per-pass rule.
        bool pass = s.CoverageOk && (!gateSplits || (maxNonUpgrade <= 1 && tierSplits == 0));
        GD.Print($"GRASS_CARD_WALK {phase} {s} split_groups={splitsPerGroup.Count} max_splits_per_group={maxSplits} tier_splits={tierSplits} events=[{histogram}] ok={pass}");
        foreach (string sample in s.Samples) GD.Print("  " + sample);
        if (gateSplits && maxSplits > 1)
            foreach (var g in events.Where(e => e.What.StartsWith("split:") && e.Active).GroupBy(e => e.Key).Where(g => g.Count() > 1).Take(4))
                GD.Print($"  repeated split group={g.Key} at={string.Join(",", g.Select(e => $"{e.Around:F1}m:{e.What}"))}");
        return pass;
    }

    private void Audit()
    {
        Stats s = S(_phase);
        s.Frames++;
        var godotPlanes = _camera.GetFrustum();
        var planes = new Plane[godotPlanes.Count];
        for (int i = 0; i < planes.Length; i++) planes[i] = godotPlanes[i];
        Vector3 eye = _camera.GlobalPosition;
        float reach = _layer.GrassCardReachMeters;
        int must = 0, covered = 0;
        _leafScratch.Clear();
        foreach (var a in _layer.CaptureLeafCoverageForTests())
        {
            bool groupCovers = a.GroupDrawn && a.OwnerActive;
            bool isCovered = a.LeafDrawn || groupCovers;
            if (a.LeafDrawn && a.GroupDrawn) { s.Doubles++; Sample(s, "double", a, eye); }
            if (a.GroupDrawn && !a.OwnerActive) { s.PendingVisible++; Sample(s, "pending_group_visible", a, eye); }
            if (a.LeafDrawn && a.OwnerActive && !a.OwnerSplitting) { s.Leaked++; Sample(s, "leaf_drawn_under_active_group", a, eye); }
            if (a.LeafUnready || a.GroupUnready) { s.UnreadyVisible++; Sample(s, "visible_before_gpu_ready", a, eye); }
            bool mustDraw = a.BoundsValid && a.InstanceCount > 0 && InFrustum(a.RawBounds, planes) && Distance(a.RawBounds, eye) < reach - 4f;
            bool known = _leafState.TryGetValue(a.Key, out LeafState previous);
            ulong dataId = unchecked(a.PlacementHash * 31UL + (ulong)a.InstanceCount * 1000003UL + a.Tier);
            int dataChange = known && previous.Hash != dataId ? _frame : known ? previous.DataChangeFrame : int.MinValue / 2;
            bool owned = a.HasOwner && a.OwnerActive;
            int ownerLoss = known && previous.Owned && !owned ? _frame : known ? previous.OwnerLossFrame : int.MinValue / 2;
            int streak = 0;
            if (mustDraw)
            {
                must++;
                if (isCovered) covered++;
                else
                {
                    streak = previous.Streak + 1;
                    s.UncoveredLeafFrames++;
                    if (previous.Must && previous.Covered)
                    {
                        s.TransitionGaps++;
                        string kind = _frame - dataChange <= 4 ? "tier_handoff" : _frame - ownerLoss <= 8 ? "group_loss" : "other";
                        if (kind == "tier_handoff") s.TierHandoffGaps++; else if (kind == "group_loss") s.GroupLossGaps++; else s.OtherGaps++;
                        Sample(s, "transition_gap:" + kind, a, eye);
                    }
                    if (a.HasOwner && a.OwnerActive && !a.OwnerSplitting) { s.OwnerGaps++; Sample(s, "owner_gap", a, eye); }
                    if (previous.EverCovered) s.MaxTransitionStreak = Math.Max(s.MaxTransitionStreak, streak);
                    else s.MaxLatencyStreak = Math.Max(s.MaxLatencyStreak, streak);
                }
            }
            _leafScratch[a.Key] = new LeafState(mustDraw, isCovered, mustDraw && (previous.EverCovered || isCovered), streak, dataId, owned, dataChange, ownerLoss);
        }
        (_leafState, _leafScratch) = (_leafScratch, _leafState);
        s.MaxMust = Math.Max(s.MaxMust, must);
        if (must > 0) s.MinCoverage = Math.Min(s.MinCoverage, (double)covered / must);
        if (_frame % 15 == 0)
        {
            try { _layer.AssertAdaptiveGrassOwnershipForTests(); }
            catch (Exception exception) { s.OwnershipFailures++; if (s.Samples.Count < 12) s.Samples.Add($"frame={_frame} ownership: {exception.Message}"); }
        }
    }

    private void Sample(Stats s, string what, RingStandingGrassLayer.LeafCoverageAudit a, Vector3 eye)
    {
        if (s.Samples.Count >= 12) return;
        s.Samples.Add($"frame={_frame} {what} leaf=({a.Key.Around},{a.Key.Across}) tier={a.Tier} dist={Distance(a.RawBounds, eye):F1} at={_around - _home.AroundMeters:F1}m "
            + $"leaf_drawn={a.LeafDrawn} near_wanted={a.NearWanted} owner={(a.HasOwner ? a.Owner.ToString() : "-")} active={a.OwnerActive} splitting={a.OwnerSplitting} "
            + $"group_drawn={a.GroupDrawn} group_wanted={a.OwnerWanted} renderer={a.HasRenderer} batch={a.NearBatch} batch_ready={a.NearBatchReady} uploads={_layer.PendingGrassUploadCount}");
    }

    private static bool InFrustum(Aabb aabb, Plane[] planes)
    {
        Vector3 end = aabb.End;
        foreach (Plane plane in planes)
        {
            Vector3 support = new(plane.Normal.X >= 0 ? aabb.Position.X : end.X, plane.Normal.Y >= 0 ? aabb.Position.Y : end.Y, plane.Normal.Z >= 0 ? aabb.Position.Z : end.Z);
            if (plane.DistanceTo(support) > 0) return false;
        }
        return true;
    }

    private static float Distance(Aabb aabb, Vector3 point)
    {
        Vector3 end = aabb.End;
        Vector3 closest = new(Mathf.Clamp(point.X, aabb.Position.X, end.X), Mathf.Clamp(point.Y, aabb.Position.Y, end.Y), Mathf.Clamp(point.Z, aabb.Position.Z, end.Z));
        return point.DistanceTo(closest);
    }
}
