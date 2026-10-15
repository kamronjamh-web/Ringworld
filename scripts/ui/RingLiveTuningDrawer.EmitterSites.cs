using System;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Surface;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private EmitterStandInLayer? _emitterStandIns;
    private EmitterPoiStreamer? _emitterPoiStreamer;
    private GameplayEmitterSiteDefaults? _emitterSiteDefaults;
    private string? _emitterSiteDefaultsPathOverride;
    private CheckButton? _emitterEnabled;
    private HSlider? _emitterMaxDistance, _emitterFade, _emitterMinPixels, _emitterRegularHandoff, _emitterMainHandoff;
    private Label? _emitterStatus, _emitterLive;
    private CheckButton? _emitterPoiEnabled;
    private HSlider? _emitterPoiRegularLoad, _emitterPoiMainLoad, _emitterPoiUnloadMargin, _emitterPoiDwell, _emitterPoiCollisionOn, _emitterPoiCollisionOffMargin;
    private Label? _emitterPoiLive, _emitterPoiRadii;
    private double _emitterPoiLiveDelay;
    private double _emitterSaveDelay, _emitterSaveRetry, _emitterLiveDelay;
    private EmitterBoreLayer? _emitterBores;
    private RingTerrain3DGameplay? _emitterGameplay;
    private bool _emitterGameplayApplied;
    private CheckButton? _emitterBoreEnabled, _emitterUndergroundRecovery;
    private HSlider? _emitterBoreRange, _emitterBoreLight;
    private Label? _emitterBoreLive;
    private double _emitterBoreLiveDelay;

    private void InitializeEmitterSiteTuning()
    {
        _emitterStandIns = GetNodeOrNull<EmitterStandInLayer>("../EmitterStandIns");
        _emitterSiteDefaults = new GameplayEmitterSiteDefaults(_emitterSiteDefaultsPathOverride);
        _emitterSiteDefaults.Load();
        _emitterPoiStreamer = GetNodeOrNull<EmitterPoiStreamer>("../EmitterSites");
        _emitterBores = GetNodeOrNull<EmitterBoreLayer>("../EmitterBores");
        _emitterSiteDefaults.Apply(_emitterStandIns);
        _emitterSiteDefaults.Apply(_emitterPoiStreamer);
        _emitterSiteDefaults.Apply(_emitterBores);
    }

    private void ProcessEmitterSiteTuning(double delta)
    {
        // The gameplay node is installed after the drawer; apply the saved aperture switch once it exists.
        if (!_emitterGameplayApplied)
        {
            _emitterGameplay ??= GetNodeOrNull<RingTerrain3DGameplay>("../Terrain3DGameplay");
            if (_emitterGameplay != null)
            {
                _emitterSiteDefaults?.Apply(_emitterGameplay);
                _emitterGameplayApplied = true;
            }
        }
        ProcessEmitterNavigation(delta);
        if (_open && _emitterBoreLive != null && (_emitterBoreLiveDelay -= delta) <= 0)
        {
            _emitterBoreLiveDelay = .25;
            string bores = _emitterBores?.DescribeStatus() ?? "Emitter bores are unavailable in this scene.";
            string surface = "";
            if (_emitterGameplay != null)
            {
                Godot.Collections.Dictionary diagnostics = _emitterGameplay.GetSurfaceDiagnostics();
                int holes = diagnostics.TryGetValue("native", out Variant native) && native.VariantType == Variant.Type.Dictionary
                    && native.AsGodotDictionary().TryGetValue("hole_vertices", out Variant count) ? count.AsInt32() : 0;
                bool pending = _emitterGameplay.ApertureReplacementPending;
                surface = $"\nSurface hole vertices: {holes}; window replacement {(pending ? "pending" : "settled")}.";
            }
            _emitterBoreLive.Text = bores + surface;
            bool present = _emitterBores != null && _emitterGameplay != null;
            foreach (HSlider? slider in new[] { _emitterBoreRange, _emitterBoreLight })
                if (slider != null && slider.Editable != present) slider.Editable = present;
            if (_emitterBoreEnabled != null && _emitterBoreEnabled.Disabled == present) _emitterBoreEnabled.Disabled = !present;
            if (_emitterUndergroundRecovery != null && _emitterUndergroundRecovery.Disabled == present) _emitterUndergroundRecovery.Disabled = !present;
        }
        if (_open && _emitterLive != null && (_emitterLiveDelay -= delta) <= 0)
        {
            _emitterLiveDelay = .25;
            _emitterLive.Text = _emitterStandIns?.DescribeStatus() ?? "Emitter stand-ins are unavailable in this scene.";
            bool bound = _emitterStandIns?.IsBound == true;
            foreach (HSlider? slider in new[] { _emitterMaxDistance, _emitterFade, _emitterMinPixels, _emitterRegularHandoff, _emitterMainHandoff })
                if (slider != null && slider.Editable != bound) slider.Editable = bound;
            if (_emitterEnabled != null && _emitterEnabled.Disabled == bound) _emitterEnabled.Disabled = !bound;
        }
        if (_open && _emitterPoiLive != null && (_emitterPoiLiveDelay -= delta) <= 0)
        {
            _emitterPoiLiveDelay = .25;
            _emitterPoiLive.Text = _emitterPoiStreamer?.DescribeStatus() ?? "Emitter POI streaming is unavailable in this scene.";
            RefreshEmitterPoiRadii();
            bool present = _emitterPoiStreamer != null;
            foreach (HSlider? slider in new[] { _emitterPoiRegularLoad, _emitterPoiMainLoad, _emitterPoiUnloadMargin, _emitterPoiDwell, _emitterPoiCollisionOn, _emitterPoiCollisionOffMargin })
                if (slider != null && slider.Editable != present) slider.Editable = present;
            if (_emitterPoiEnabled != null && _emitterPoiEnabled.Disabled == present) _emitterPoiEnabled.Disabled = !present;
        }
        if (_emitterSiteDefaults?.Dirty != true) return;
        _emitterSaveDelay -= delta;
        _emitterSaveRetry -= delta;
        if (_emitterSaveDelay <= 0 && _emitterSaveRetry <= 0 && !FlushEmitterSiteDefaults()) _emitterSaveRetry = 5;
    }

    public bool FlushEmitterSiteDefaults()
    {
        bool saved = _emitterSiteDefaults?.Save() ?? true;
        RefreshEmitterSiteStatus();
        return saved;
    }

    public void InstallEmitterSiteDefaults(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal) && !isolatedPath.StartsWith("user://generated/", StringComparison.Ordinal)) return;
        _emitterSiteDefaultsPathOverride = isolatedPath;
        if (_emitterSiteDefaults is null) return;
        _emitterSiteDefaults = new GameplayEmitterSiteDefaults(isolatedPath);
        _emitterSiteDefaults.Load();
        _emitterSiteDefaults.Apply(_emitterStandIns);
        _emitterSiteDefaults.Apply(_emitterPoiStreamer);
        _emitterSiteDefaults.Apply(_emitterBores);
        _emitterSiteDefaults.Apply(_emitterGameplay);
        RefreshEmitterSiteControls();
    }

    private void BuildEmitterStandInSection(VBoxContainer tabRoot)
    {
        var section = TuningMenuUi.CreateSection(tabRoot, "Emitter far stand-ins",
            "Distant silhouettes of the 595 beam emitter spires and the main spire, drawn out to the maximum distance. Handoff distances apply only to sites whose full POI is currently loaded.");
        _emitterLive = new Label { Name = "EmitterStandInLive", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        section.AddChild(_emitterLive);
        var s = _emitterSiteDefaults?.Settings ?? new GameplayEmitterSiteSettings();
        bool bound = _emitterStandIns?.IsBound == true;
        _emitterEnabled = new CheckButton { Name = "EmitterStandInEnabled", Text = "Far stand-ins enabled", ButtonPressed = s.StandInsEnabled, Disabled = !bound };
        _emitterEnabled.Toggled += _ => QueueEmitterSites();
        section.AddChild(_emitterEnabled);
        _emitterMaxDistance = AddEmitterSlider(section, "EmitterStandInMaxDistance", "Max distance (km)", GameplayEmitterSiteDefaults.MinimumMaxDistanceKm, GameplayEmitterSiteDefaults.MaximumMaxDistanceKm, 5, s.StandInMaxDistanceKm,
            "Farthest distance a stand-in is drawn. Also limited by the global terrain fade end and 98% of the camera far plane.");
        _emitterFade = AddEmitterSlider(section, "EmitterStandInFade", "Distance fade (km)", GameplayEmitterSiteDefaults.MinimumFadeKm, GameplayEmitterSiteDefaults.MaximumFadeKm, 5, s.StandInFadeKm,
            "Length of the dithered fade-out that ends at the maximum distance (never more than half of it).");
        _emitterMinPixels = AddEmitterSlider(section, "EmitterStandInMinPixels", "Min height (px)", GameplayEmitterSiteDefaults.MinimumMinPixels, GameplayEmitterSiteDefaults.MaximumMinPixels, .1, s.StandInMinPixels,
            "On-screen height below which a stand-in dithers out so sub-pixel spires do not shimmer.");
        _emitterRegularHandoff = AddEmitterSlider(section, "EmitterStandInRegularHandoff", "Spire handoff (m) (when POI loaded)", 0, GameplayEmitterSiteDefaults.MaximumRegularHandoffMeters, 100, s.RegularHandoffMeters,
            "Only for sites whose full POI is loaded: inside this distance the stand-in fades out over 1 km so the real model takes over.");
        _emitterMainHandoff = AddEmitterSlider(section, "EmitterStandInMainHandoff", "Main spire handoff (m) (when POI loaded)", 0, GameplayEmitterSiteDefaults.MaximumMainHandoffMeters, 100, s.MainHandoffMeters,
            "Only while the main spire POI is loaded: inside this distance the stand-in fades out over 2 km so the real model takes over.");
        var reset = new Button { Name = "EmitterStandInReset", Text = "Reset emitter stand-ins", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += ResetEmitterSites;
        section.AddChild(reset);
        _emitterStatus = new Label { Name = "EmitterStandInStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_emitterStatus);
        RefreshEmitterSiteStatus();
    }

    private void ResetEmitterSites()
    {
        _emitterSiteDefaults?.Reset();
        _emitterSiteDefaults?.Apply(_emitterStandIns);
        _emitterSiteDefaults?.Apply(_emitterPoiStreamer);
        _emitterSiteDefaults?.Apply(_emitterBores);
        _emitterSiteDefaults?.Apply(_emitterGameplay);
        RefreshEmitterSiteControls();
        FlushEmitterSiteDefaults();
    }

    private void BuildEmitterBoreSection(VBoxContainer tabRoot)
    {
        var section = TuningMenuUi.CreateSection(tabRoot, "Emitter bores",
            "Opens the beam bore under each spire: the ground has no lid, the liner is drawn, and an open iris rim 200 m down glows teal. Falling in drops you down the shaft. Applies with the next terrain window swap; the bore floor stays until the hole has closed.");
        _emitterBoreLive = new Label { Name = "EmitterBoreLive", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        section.AddChild(_emitterBoreLive);
        var s = _emitterSiteDefaults?.Settings ?? new GameplayEmitterSiteSettings();
        bool present = _emitterBores != null;
        _emitterBoreEnabled = new CheckButton { Name = "EmitterBoreEnabled", Text = "Bore apertures", ButtonPressed = s.BoreAperturesEnabled, Disabled = !present };
        _emitterBoreEnabled.Toggled += _ => QueueEmitterBoreSettings();
        section.AddChild(_emitterBoreEnabled);
        _emitterUndergroundRecovery = new CheckButton { Name = "EmitterUndergroundRecovery", Text = "Underground recovery", ButtonPressed = s.UndergroundRecoveryEnabled, Disabled = !present,
            TooltipText = "Teleports a player who falls out of a site's underground (below the cavern floor, or stuck in rock) to its arrival overlook." };
        _emitterUndergroundRecovery.Toggled += _ => QueueEmitterBoreSettings();
        section.AddChild(_emitterUndergroundRecovery);
        _emitterBoreRange = AddEmitterSlider(section, "EmitterBoreRange", "Bore visible range (m)", GameplayEmitterSiteDefaults.MinimumBoreRangeMeters, GameplayEmitterSiteDefaults.MaximumBoreRangeMeters, 500, s.BoreVisibleRangeMeters,
            "Distance within which a regular bore's liner and the far-terrain hole are drawn. The main bore scales with its larger radius.");
        _emitterBoreLight = AddEmitterSlider(section, "EmitterBoreLight", "Plug light energy", GameplayEmitterSiteDefaults.MinimumBoreLightEnergy, GameplayEmitterSiteDefaults.MaximumBoreLightEnergy, .1, s.BorePlugLightEnergy,
            "Brightness of the teal light above each plug that lights the liner walls. 0 hides it.");
        foreach (HSlider slider in new[] { _emitterBoreRange, _emitterBoreLight }) slider.Editable = present;
        var note = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted, Text = "Applies with the next terrain window swap; the bore floor stays until the hole has closed." };
        section.AddChild(note);
        var reset = new Button { Name = "EmitterBoreReset", Text = "Reset emitter settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += ResetEmitterSites;
        section.AddChild(reset);
    }

    private void QueueEmitterBoreSettings()
    {
        if (_emitterSiteDefaults == null || _emitterBoreEnabled == null || _emitterBoreRange == null || _emitterBoreLight == null || _emitterUndergroundRecovery == null) return;
        bool changed = _emitterSiteDefaults.SetBoreAperturesEnabled(_emitterBoreEnabled.ButtonPressed);
        changed |= _emitterSiteDefaults.SetUndergroundRecoveryEnabled(_emitterUndergroundRecovery.ButtonPressed);
        changed |= _emitterSiteDefaults.SetBoreVisibleRangeMeters((float)_emitterBoreRange.Value);
        changed |= _emitterSiteDefaults.SetBorePlugLightEnergy((float)_emitterBoreLight.Value);
        _emitterSiteDefaults.Apply(_emitterBores);
        _emitterSiteDefaults.Apply(_emitterGameplay);
        if (!changed) return;
        _emitterSaveDelay = .75;
        RefreshEmitterSiteStatus();
    }

    private void BuildEmitterPoiStreamerSection(VBoxContainer tabRoot)
    {
        var section = TuningMenuUi.CreateSection(tabRoot, "Emitter POI streaming",
            "Loads the full beam emitter spire models near the player. Load radii are raised to cover the model's own visibility ranges and the stand-in handoff, so the real model never pops in late.");
        _emitterPoiLive = new Label { Name = "EmitterPoiLive", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        section.AddChild(_emitterPoiLive);
        var s = _emitterSiteDefaults?.Settings ?? new GameplayEmitterSiteSettings();
        bool present = _emitterPoiStreamer != null;
        _emitterPoiEnabled = new CheckButton { Name = "EmitterPoiEnabled", Text = "POI streaming enabled", ButtonPressed = s.PoiStreamerEnabled, Disabled = !present };
        _emitterPoiEnabled.Toggled += _ => QueueEmitterSites();
        section.AddChild(_emitterPoiEnabled);
        _emitterPoiRegularLoad = AddEmitterSlider(section, "EmitterPoiRegularLoad", "Spire load radius (m)", GameplayEmitterSiteDefaults.MinimumRegularLoadMeters, GameplayEmitterSiteDefaults.MaximumRegularLoadMeters, 100, s.RegularLoadRadiusMeters,
            "Distance at which a regular spire's full model loads. Raised automatically to cover the model's visibility range and the stand-in handoff.");
        _emitterPoiMainLoad = AddEmitterSlider(section, "EmitterPoiMainLoad", "Main spire load radius (m)", GameplayEmitterSiteDefaults.MinimumMainLoadMeters, GameplayEmitterSiteDefaults.MaximumMainLoadMeters, 100, s.MainLoadRadiusMeters,
            "Distance at which the main spire's full model loads. Raised automatically to cover the model's visibility range and the stand-in handoff.");
        _emitterPoiUnloadMargin = AddEmitterSlider(section, "EmitterPoiUnloadMargin", "Unload margin (m)", GameplayEmitterSiteDefaults.MinimumUnloadMarginMeters, GameplayEmitterSiteDefaults.MaximumUnloadMarginMeters, 100, s.UnloadMarginMeters,
            "A model unloads this far beyond its load radius, so it does not flicker at the boundary.");
        _emitterPoiDwell = AddEmitterSlider(section, "EmitterPoiDwell", "Minimum loaded time (s)", GameplayEmitterSiteDefaults.MinimumDwellSeconds, GameplayEmitterSiteDefaults.MaximumDwellSeconds, .1, s.MinDwellSeconds,
            "A loaded model is kept at least this long before it can unload (unless its slot is needed for a closer site).");
        _emitterPoiCollisionOn = AddEmitterSlider(section, "EmitterPoiCollisionOn", "Collision on within (m)", GameplayEmitterSiteDefaults.MinimumCollisionOnMeters, GameplayEmitterSiteDefaults.MaximumCollisionOnMeters, 50, s.CollisionOnMeters,
            "Walkable collision of a loaded model switches on when the player is this close to its pad.");
        _emitterPoiCollisionOffMargin = AddEmitterSlider(section, "EmitterPoiCollisionOffMargin", "Collision off margin (m)", GameplayEmitterSiteDefaults.MinimumCollisionOffMarginMeters, GameplayEmitterSiteDefaults.MaximumCollisionOffMarginMeters, 50, s.CollisionOffMarginMeters,
            "Collision switches off this much farther than it switched on.");
        _emitterPoiRadii = new Label { Name = "EmitterPoiRadii", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_emitterPoiRadii);
        var reset = new Button { Name = "EmitterPoiReset", Text = "Reset emitter settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += ResetEmitterSites;
        section.AddChild(reset);
        foreach (HSlider slider in new[] { _emitterPoiRegularLoad, _emitterPoiMainLoad, _emitterPoiUnloadMargin, _emitterPoiDwell, _emitterPoiCollisionOn, _emitterPoiCollisionOffMargin })
            slider.Editable = present;
        RefreshEmitterPoiRadii();
    }

    private void RefreshEmitterPoiRadii()
    {
        if (_emitterPoiRadii == null) return;
        var s = _emitterSiteDefaults?.Settings ?? new GameplayEmitterSiteSettings();
        if (_emitterPoiStreamer == null)
        {
            _emitterPoiRadii.Text = $"Requested: spire load {s.RegularLoadRadiusMeters / 1000f:0.0} km, main {s.MainLoadRadiusMeters / 1000f:0.0} km (streamer not present).";
            return;
        }
        double regular = _emitterPoiStreamer.EffectiveLoadRadius(Terrain.Sites.SiteKind.Regular), main = _emitterPoiStreamer.EffectiveLoadRadius(Terrain.Sites.SiteKind.Main);
        _emitterPoiRadii.Text = $"Effective: spire load {regular / 1000d:0.0} km, unload {(regular + s.UnloadMarginMeters) / 1000d:0.0} km. Main load {main / 1000d:0.0} km, unload {(main + s.UnloadMarginMeters) / 1000d:0.0} km.";
    }

    private HSlider AddEmitterSlider(VBoxContainer parent, string name, string label, double min, double max, double step, double initial, string tooltip)
    {
        var row = new HBoxContainer { TooltipText = tooltip };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(160, 0), TooltipText = tooltip });
        var slider = new HSlider { Name = name, MinValue = min, MaxValue = max, Step = step, Value = initial, Editable = _emitterStandIns?.IsBound == true, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var number = new Label { Text = slider.Value.ToString("0.##"), CustomMinimumSize = new Vector2(55, 0) };
        slider.ValueChanged += value => { number.Text = value.ToString("0.##"); QueueEmitterSites(); };
        row.AddChild(slider);
        row.AddChild(number);
        parent.AddChild(row);
        return slider;
    }

    private void QueueEmitterSites()
    {
        QueueEmitterPoiSettings();
        QueueEmitterBoreSettings();
        QueueEmitterNavigationSettings();
        if (_emitterSiteDefaults == null || _emitterEnabled == null || _emitterMaxDistance == null || _emitterFade == null
            || _emitterMinPixels == null || _emitterRegularHandoff == null || _emitterMainHandoff == null) return;
        bool changed = _emitterSiteDefaults.SetStandInsEnabled(_emitterEnabled.ButtonPressed);
        changed |= _emitterSiteDefaults.SetStandInMaxDistanceKm((float)_emitterMaxDistance.Value);
        changed |= _emitterSiteDefaults.SetStandInFadeKm((float)_emitterFade.Value);
        changed |= _emitterSiteDefaults.SetStandInMinPixels((float)_emitterMinPixels.Value);
        changed |= _emitterSiteDefaults.SetRegularHandoffMeters((float)_emitterRegularHandoff.Value);
        changed |= _emitterSiteDefaults.SetMainHandoffMeters((float)_emitterMainHandoff.Value);
        _emitterSiteDefaults.Apply(_emitterStandIns);
        if (!changed) return;
        _emitterSaveDelay = .75;
        RefreshEmitterSiteStatus();
    }

    private void QueueEmitterPoiSettings()
    {
        if (_emitterSiteDefaults == null || _emitterPoiEnabled == null || _emitterPoiRegularLoad == null || _emitterPoiMainLoad == null || _emitterPoiUnloadMargin == null
            || _emitterPoiDwell == null || _emitterPoiCollisionOn == null || _emitterPoiCollisionOffMargin == null) return;
        bool changed = _emitterSiteDefaults.SetPoiStreamerEnabled(_emitterPoiEnabled.ButtonPressed);
        changed |= _emitterSiteDefaults.SetRegularLoadRadiusMeters((float)_emitterPoiRegularLoad.Value);
        changed |= _emitterSiteDefaults.SetMainLoadRadiusMeters((float)_emitterPoiMainLoad.Value);
        changed |= _emitterSiteDefaults.SetUnloadMarginMeters((float)_emitterPoiUnloadMargin.Value);
        changed |= _emitterSiteDefaults.SetMinDwellSeconds((float)_emitterPoiDwell.Value);
        changed |= _emitterSiteDefaults.SetCollisionOnMeters((float)_emitterPoiCollisionOn.Value);
        changed |= _emitterSiteDefaults.SetCollisionOffMarginMeters((float)_emitterPoiCollisionOffMargin.Value);
        _emitterSiteDefaults.Apply(_emitterPoiStreamer);
        RefreshEmitterPoiRadii();
        if (!changed) return;
        _emitterSaveDelay = .75;
        RefreshEmitterSiteStatus();
    }

    private void RefreshEmitterSiteControls()
    {
        if (_emitterSiteDefaults == null) return;
        var s = _emitterSiteDefaults.Settings;
        _emitterEnabled?.SetPressedNoSignal(s.StandInsEnabled);
        SetEmitterSliderNoSignal(_emitterMaxDistance, s.StandInMaxDistanceKm);
        SetEmitterSliderNoSignal(_emitterFade, s.StandInFadeKm);
        SetEmitterSliderNoSignal(_emitterMinPixels, s.StandInMinPixels);
        SetEmitterSliderNoSignal(_emitterRegularHandoff, s.RegularHandoffMeters);
        SetEmitterSliderNoSignal(_emitterMainHandoff, s.MainHandoffMeters);
        _emitterBoreEnabled?.SetPressedNoSignal(s.BoreAperturesEnabled);
        _emitterUndergroundRecovery?.SetPressedNoSignal(s.UndergroundRecoveryEnabled);
        SetEmitterSliderNoSignal(_emitterBoreRange, s.BoreVisibleRangeMeters);
        SetEmitterSliderNoSignal(_emitterBoreLight, s.BorePlugLightEnergy);
        _emitterPoiEnabled?.SetPressedNoSignal(s.PoiStreamerEnabled);
        SetEmitterSliderNoSignal(_emitterPoiRegularLoad, s.RegularLoadRadiusMeters);
        SetEmitterSliderNoSignal(_emitterPoiMainLoad, s.MainLoadRadiusMeters);
        SetEmitterSliderNoSignal(_emitterPoiUnloadMargin, s.UnloadMarginMeters);
        SetEmitterSliderNoSignal(_emitterPoiDwell, s.MinDwellSeconds);
        SetEmitterSliderNoSignal(_emitterPoiCollisionOn, s.CollisionOnMeters);
        SetEmitterSliderNoSignal(_emitterPoiCollisionOffMargin, s.CollisionOffMarginMeters);
        RefreshEmitterNavigationControls();
        RefreshEmitterPoiRadii();
        RefreshEmitterSiteStatus();
    }

    private static void SetEmitterSliderNoSignal(HSlider? slider, double value)
    {
        if (slider == null) return;
        slider.SetValueNoSignal(value);
        if (slider.GetParent().GetChild(2) is Label number) number.Text = slider.Value.ToString("0.##");
    }

    private void RefreshEmitterSiteStatus()
    {
        if (_emitterStatus == null || _emitterSiteDefaults == null) return;
        _emitterStatus.Text = _emitterSiteDefaults.LastError.Length > 0 ? "Save error: " + _emitterSiteDefaults.LastError
            : _emitterSiteDefaults.Dirty ? "Emitter stand-in settings pending save." : "Emitter stand-in settings saved.";
    }
}
