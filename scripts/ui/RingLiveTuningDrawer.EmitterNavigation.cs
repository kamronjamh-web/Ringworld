using System;
using System.Globalization;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private static readonly string[] EmitterViewpointNames = { "Entry (ground)", "Gallery", "Far view", "High altitude" };
    private readonly EmitterSiteTeleport _emitterTeleport = new();
    private EmitterSiteNavigator? _emitterNavigator;
    private EmitterSiteRegistry? _emitterNavigatorRegistry;
    private Label? _emitterNavLive, _emitterNavResult;
    private OptionButton? _emitterNavViewpoint;
    private HSlider? _emitterNavFar, _emitterNavHigh;
    private SpinBox? _emitterNavSite;
    private double _emitterNavLiveDelay;
    private EmitterTeleportPhase _emitterNavLastState = EmitterTeleportPhase.Idle;

    /// <summary>State of the last site teleport: Idle, Holding, Arrived, TimedOut or Failed.</summary>
    public string EmitterTeleportState => _emitterTeleport.State.ToString();
    public string EmitterTeleportMessage => _emitterTeleport.Message;

    private RingVoxelExplorerController? EmitterNavExplorer() => GetNodeOrNull<RingVoxelExplorerController>("../Explorer");

    /// <summary>Navigator for the bound v5 world, built lazily (null for worlds without emitter sites).</summary>
    private EmitterSiteNavigator? EmitterNavigator()
    {
        EmitterSiteRegistry? registry = _grid?.TerrainSampler?.EmitterSites;
        if (registry == null || _grid == null) { _emitterNavigator = null; _emitterNavigatorRegistry = null; return null; }
        if (_emitterNavigator != null && ReferenceEquals(registry, _emitterNavigatorRegistry)) return _emitterNavigator;
        var snapshot = _grid.CaptureLodGeometrySnapshot();
        _emitterNavigator = new EmitterSiteNavigator(new EmitterSitePlacementSource(registry),
            snapshot.RingRadiusMeters - snapshot.RingVerticalBaseOffsetMeters, _grid.TerrainSampler!.SeaLevelY);
        _emitterNavigatorRegistry = registry;
        return _emitterNavigator;
    }

    private void BuildEmitterNavigationSection(VBoxContainer tabRoot)
    {
        var section = TuningMenuUi.CreateSection(tabRoot, "Emitter sites",
            "Nearest beam emitter site and teleports to the sites. 'Nearest entry' always lands on the ground approach, which is also the way out of a bore.");
        _emitterNavLive = new Label { Name = "EmitterNavLive", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        section.AddChild(_emitterNavLive);
        var s = _emitterSiteDefaults?.Settings ?? new GameplayEmitterSiteSettings();
        var viewpointRow = new HBoxContainer { TooltipText = "Where the buttons below put you relative to the spire." };
        viewpointRow.AddChild(new Label { Text = "Viewpoint", CustomMinimumSize = new Vector2(160, 0) });
        _emitterNavViewpoint = new OptionButton { Name = "EmitterNavViewpoint", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (string name in EmitterViewpointNames) _emitterNavViewpoint.AddItem(name);
        _emitterNavViewpoint.Selected = s.TeleportViewpoint;
        _emitterNavViewpoint.ItemSelected += _ => QueueEmitterNavigationSettings();
        viewpointRow.AddChild(_emitterNavViewpoint);
        section.AddChild(viewpointRow);
        _emitterNavFar = AddEmitterSlider(section, "EmitterNavFar", "Far view distance (km)", GameplayEmitterSiteDefaults.MinimumFarViewpointKm, GameplayEmitterSiteDefaults.MaximumFarViewpointKm, 1, s.FarViewpointDistanceKm,
            "Distance of the Far view from a regular spire (the main spire uses twice this). Lands on dry flat ground nearby, else hovers 200 m up.");
        _emitterNavHigh = AddEmitterSlider(section, "EmitterNavHigh", "High altitude (km)", GameplayEmitterSiteDefaults.MinimumHighViewpointKm, GameplayEmitterSiteDefaults.MaximumHighViewpointKm, 5, s.HighViewpointAltitudeKm,
            "Altitude above the pad of the High view (flight mode, offset sideways by 40% of the altitude).");
        foreach (HSlider slider in new[] { _emitterNavFar, _emitterNavHigh }) slider.Editable = true;

        AddEmitterNavButtons(section, ("Nearest entry", () => EmitterTeleportNearest(EmitterSiteClass.Any, false, true)),
            ("Main spire", () => EmitterTeleportMain()));
        AddEmitterNavButtons(section, ("Nearest island", () => EmitterTeleportNearest(EmitterSiteClass.Island, false, false)),
            ("Nearest mountain", () => EmitterTeleportNearest(EmitterSiteClass.Mountain, false, false)),
            ("Nearest land", () => EmitterTeleportNearest(EmitterSiteClass.Land, false, false)));

        var siteRow = new HBoxContainer { TooltipText = "Teleport to a site by number." };
        siteRow.AddChild(new Label { Text = "Site #", CustomMinimumSize = new Vector2(60, 0) });
        _emitterNavSite = new SpinBox { Name = "EmitterNavSite", MinValue = 0, MaxValue = 594, Step = 1, Rounded = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        siteRow.AddChild(_emitterNavSite);
        var go = new Button { Name = "EmitterNavGo", Text = "Go" };
        go.Pressed += () => TeleportToSelectedEmitterSite(0);
        var prev = new Button { Name = "EmitterNavPrev", Text = "Prev" };
        prev.Pressed += () => TeleportToSelectedEmitterSite(-1);
        var next = new Button { Name = "EmitterNavNext", Text = "Next" };
        next.Pressed += () => TeleportToSelectedEmitterSite(1);
        siteRow.AddChild(go); siteRow.AddChild(prev); siteRow.AddChild(next);
        section.AddChild(siteRow);
        _emitterNavResult = new Label { Name = "EmitterNavResult", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_emitterNavResult);
        var reset = new Button { Name = "EmitterNavReset", Text = "Reset emitter settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += ResetEmitterSites;
        section.AddChild(reset);
        RefreshEmitterNavigationLive();
    }

    private static void AddEmitterNavButtons(VBoxContainer section, params (string Text, Action Action)[] buttons)
    {
        var row = new HBoxContainer();
        foreach ((string text, Action action) in buttons)
        {
            var button = new Button { Name = "EmitterNav" + text.Replace(" ", ""), Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            button.Pressed += action;
            row.AddChild(button);
        }
        section.AddChild(row);
    }

    private int SelectedEmitterViewpoint() => _emitterNavViewpoint?.Selected ?? _emitterSiteDefaults?.Settings.TeleportViewpoint ?? 0;

    private void EmitterTeleportNearest(EmitterSiteClass cls, bool includeMain, bool forceEntry)
    {
        int site = FindNearestEmitterSite(cls, includeMain);
        if (site < 0) { SetEmitterNavResult(EmitterNavigator() == null ? "Emitter sites are unavailable (needs a v5 world)." : $"No {cls.ToString().ToLowerInvariant()} site found."); return; }
        RequestEmitterTeleport(site, forceEntry ? (int)EmitterViewpoint.Entry : SelectedEmitterViewpoint());
    }

    private void EmitterTeleportMain()
    {
        EmitterSiteNavigator? navigator = EmitterNavigator();
        if (navigator == null) { SetEmitterNavResult("Emitter sites are unavailable (needs a v5 world)."); return; }
        RequestEmitterTeleport(navigator.MainIndex, SelectedEmitterViewpoint());
    }

    private void TeleportToSelectedEmitterSite(int step)
    {
        EmitterSiteNavigator? navigator = EmitterNavigator();
        if (navigator == null || _emitterNavSite == null) { SetEmitterNavResult("Emitter sites are unavailable (needs a v5 world)."); return; }
        int count = navigator.Count;
        int site = ((int)_emitterNavSite.Value + step + count) % count;
        _emitterNavSite.Value = site;
        RequestEmitterTeleport(site, SelectedEmitterViewpoint());
    }

    private int FindNearestEmitterSite(EmitterSiteClass cls, bool includeMain)
    {
        EmitterSiteNavigator? navigator = EmitterNavigator();
        RingVoxelExplorerController? explorer = EmitterNavExplorer();
        if (navigator == null || explorer == null) return -1;
        RingCanonicalPosition position = explorer.CanonicalPosition;
        return navigator.FindNearest(position.AroundMeters, position.AcrossMeters, cls, includeMain);
    }

    private void SetEmitterNavResult(string text)
    {
        if (_emitterNavResult != null) _emitterNavResult.Text = text;
    }

    /// <summary>Test hook (works while the drawer is unbuilt): teleport to <paramref name="site"/> at viewpoint 0 entry, 1 gallery, 2 far, 3 high.</summary>
    public bool RequestEmitterTeleport(int site, int viewpoint)
    {
        EmitterSiteNavigator? navigator = EmitterNavigator();
        if (navigator == null) { SetEmitterNavResult("Emitter sites are unavailable (needs a v5 world)."); return false; }
        var settings = _emitterSiteDefaults?.Settings ?? new GameplayEmitterSiteSettings();
        bool started = _emitterTeleport.Begin(EmitterNavExplorer(), _emitterGameplay ??= GetNodeOrNull<RingTerrain3DGameplay>("../Terrain3DGameplay"),
            _emitterPoiStreamer, _grid, navigator, site, (EmitterViewpoint)Math.Clamp(viewpoint, 0, 3), settings.FarViewpointDistanceKm, settings.HighViewpointAltitudeKm);
        _emitterNavLastState = _emitterTeleport.State;
        SetEmitterNavResult(_emitterTeleport.Message);
        return started;
    }

    /// <summary>Test hook: site index for "nearest", "nearest island", "nearest mountain", "nearest land", "main" or a number; -1 when unknown.</summary>
    public int FindEmitterSite(string query)
    {
        EmitterSiteNavigator? navigator = EmitterNavigator();
        if (navigator == null) return -1;
        string text = query.Trim().ToLowerInvariant();
        if (text == "main") return navigator.MainIndex;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return navigator.TryGetSite(index, out _) ? index : -1;
        EmitterSiteClass cls = text.Contains("island") ? EmitterSiteClass.Island : text.Contains("mountain") ? EmitterSiteClass.Mountain
            : text.Contains("land") ? EmitterSiteClass.Land : EmitterSiteClass.Any;
        return FindNearestEmitterSite(cls, text == "nearest main");
    }

    /// <summary>Test hook: "main", "mountain", "island" or "land" for a site index; empty when unknown.</summary>
    public string EmitterSiteClassName(int site)
    {
        EmitterSiteNavigator? navigator = EmitterNavigator();
        if (navigator == null || !navigator.TryGetSite(site, out _)) return "";
        return site == navigator.MainIndex ? "main" : navigator.ClassOf(site).ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Test hook: cosine between the camera forward and the direction to the last teleport's aim point (1 = dead centre).
    /// Horizontal-only drops the component along local up. -2 when there is no teleport or camera.
    /// </summary>
    public double MeasureEmitterViewAlignment(bool horizontalOnly)
    {
        if (_emitterTeleport.State == EmitterTeleportPhase.Idle || _emitterTeleport.State == EmitterTeleportPhase.Failed) return -2d;
        EmitterViewpointPose pose = _emitterTeleport.Pose;
        return MeasureViewAlignment(pose.AimAround, pose.AimVertical, pose.AimAcross, horizontalOnly);
    }

    /// <summary>Test hook: the same cosine toward the middle of a site spire (pad plus half the crown height), independent of any teleport.</summary>
    public double MeasureEmitterViewAlignmentToSite(int site, bool horizontalOnly)
    {
        EmitterSiteNavigator? navigator = EmitterNavigator();
        if (navigator == null || !navigator.TryGetSite(site, out _)) return -2d;
        EmitterSitePlacement p = navigator.Placements.GetPlacement(site);
        return MeasureViewAlignment(p.AroundMeters, p.PadHeightMeters + navigator.Placements.GetCrownHeightMeters(site) * 0.5d, p.AcrossMeters, horizontalOnly);
    }

    private double MeasureViewAlignment(double aimAround, double aimVertical, double aimAcross, bool horizontalOnly)
    {
        RingVoxelExplorerController? explorer = EmitterNavExplorer();
        Camera3D? camera = explorer?.GetNodeOrNull<Camera3D>("Head/Camera3D");
        if (explorer == null || camera == null || _grid == null) return -2d;
        Vector3 aim = _grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(aimAround, aimVertical, aimAcross));
        Vector3 toAim = aim - camera.GlobalPosition;
        Vector3 forward = -camera.GlobalBasis.Z;
        if (horizontalOnly)
        {
            Vector3 up = explorer.GlobalBasis.Y.Normalized();
            toAim -= up * toAim.Dot(up);
            forward -= up * forward.Dot(up);
        }
        if (toAim.LengthSquared() < 1e-6f || forward.LengthSquared() < 1e-6f) return -2d;
        return forward.Normalized().Dot(toAim.Normalized());
    }

    private void ProcessEmitterNavigation(double delta)
    {
        EmitterTeleportPhase state = _emitterTeleport.Poll(delta);
        if (state != _emitterNavLastState || state == EmitterTeleportPhase.Holding)
        {
            _emitterNavLastState = state;
            SetEmitterNavResult(_emitterTeleport.Message);
        }
        if (_open && _emitterNavLive != null && (_emitterNavLiveDelay -= delta) <= 0)
        {
            _emitterNavLiveDelay = .25;
            RefreshEmitterNavigationLive();
        }
    }

    private void RefreshEmitterNavigationLive()
    {
        if (_emitterNavLive == null) return;
        EmitterSiteNavigator? navigator = EmitterNavigator();
        RingVoxelExplorerController? explorer = EmitterNavExplorer();
        if (navigator == null || explorer == null) { _emitterNavLive.Text = "Emitter sites: unavailable (needs v5 world)."; return; }
        if (_emitterNavSite != null && _emitterNavSite.MaxValue != navigator.Count - 1) _emitterNavSite.MaxValue = navigator.Count - 1;
        RingCanonicalPosition position = explorer.CanonicalPosition;
        int site = navigator.FindNearest(position.AroundMeters, position.AcrossMeters, EmitterSiteClass.Any, true, out double distance);
        if (site < 0) { _emitterNavLive.Text = "Emitter sites: none."; return; }
        EmitterSite record = navigator.Placements.Registry[site];
        (int row, int column) = navigator.GetRowColumn(site);
        double circumference = navigator.Placements.CircumferenceMeters;
        double dAround = EmitterPoiStreamerCore.SignedWrap(record.Around - position.AroundMeters, circumference), dAcross = record.Across - position.AcrossMeters;
        double bearing = EmitterSiteNavigator.BearingDegrees(dAround, dAcross);
        double relative = EmitterSiteNavigator.WrapDegrees(bearing - explorer.SavedYaw * (180d / Math.PI));
        string cell = column < 0 ? "main row" : $"row {row}, col {column}";
        string slotText = "slot none";
        if (_emitterPoiStreamer != null && _emitterPoiStreamer.FindSlotForSite(site) is int slot and >= 0)
            slotText = $"slot {slot}, collision {(_emitterPoiStreamer.IsSlotCollisionEnabled(slot) ? "on" : "off")}";
        string boreText = "bore inactive";
        if (_emitterBores?.IsBound == true)
            for (int i = 0; i < EmitterBoreLayer.SlotCount; i++)
                if (_emitterBores.GetActiveSite(i) == site) { boreText = $"bore active, collision {(_emitterBores.IsSlotCollisionEnabled(i) ? "on" : "off")}"; break; }
        bool inBore = distance < EmitterSiteApertures.LinerRadius(record.Kind);
        string head = FormattableString.Invariant($"Nearest #{site} ({cell}), {(record.IsMain ? "main" : "regular")} {navigator.ClassOf(site).ToString().ToLowerInvariant()}, pad {record.PadHeight} m, {distance / 1000d:0.00} km");
        string aim = FormattableString.Invariant($"Bearing {bearing:0} deg ({relative:+0;-0;0} deg relative to view); {slotText}; {boreText}");
        _emitterNavLive.Text = head + System.Environment.NewLine + aim + (inBore ? System.Environment.NewLine + "IN BORE: use Nearest entry to get out." : "");
    }

    private void QueueEmitterNavigationSettings()
    {
        if (_emitterSiteDefaults == null || _emitterNavViewpoint == null || _emitterNavFar == null || _emitterNavHigh == null) return;
        bool changed = _emitterSiteDefaults.SetTeleportViewpoint(_emitterNavViewpoint.Selected);
        changed |= _emitterSiteDefaults.SetFarViewpointDistanceKm((float)_emitterNavFar.Value);
        changed |= _emitterSiteDefaults.SetHighViewpointAltitudeKm((float)_emitterNavHigh.Value);
        if (!changed) return;
        _emitterSaveDelay = .75;
        RefreshEmitterSiteStatus();
    }

    private void RefreshEmitterNavigationControls()
    {
        if (_emitterSiteDefaults == null) return;
        var s = _emitterSiteDefaults.Settings;
        _emitterNavViewpoint?.Select(s.TeleportViewpoint);
        SetEmitterSliderNoSignal(_emitterNavFar, s.FarViewpointDistanceKm);
        SetEmitterSliderNoSignal(_emitterNavHigh, s.HighViewpointAltitudeKm);
    }
}
