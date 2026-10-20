using Godot;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Voxel.LOD;
namespace RingworldTechDemo.UI;
public partial class RingLiveTuningDrawer
{
    private RingGraphicsQualityController? _shadowGraphics;
    private GameplayShadowDefaults _shadowDefaults = null!;
    private Label? _shadowStatus;
    private double _shadowSaveDelay, _shadowRetryDelay, _shadowRefreshDelay;
    private readonly System.Collections.Generic.Dictionary<string, Range> _shadowRanges = new();
    private readonly System.Collections.Generic.Dictionary<string, Label> _shadowValueLabels = new();
    private readonly System.Collections.Generic.Dictionary<string, OptionButton> _shadowOptions = new();
    private CheckButton? _shadowEnabled;
    private CheckButton? _localShadowHandoffEnabled;
    private CheckButton? _planetShadowsEnabled;
    private CheckButton? _terrainMacroShadowEnabled;
    private CheckButton? _terrainMacroWallShadowsEnabled;
    private CheckButton? _heightShadowEnabled, _heightShadowOverlayToggle, _heightShadowNearMap, _heightShadowPlanetNearMap, _contactShadowEnabled, _vegetationShadowInstance, _vegetationUnifiedCompose;
    private Label? _vegetationShadowStatus;
    private Label? _contactShadowStatus;
    private Label? _heightShadowStatus;
    private RingHeightShadowOverlay? _heightShadowOverlay;
    private OptionButton? _heightShadowOverlayMode;
    private CheckButton? _sunStabilizationEnabled;
    private CheckButton? _pauseDayNight;
    private string? _shadowDefaultsPathOverride;
    private void InitializeShadowTuning() { _shadowGraphics = GetNodeOrNull<RingGraphicsQualityController>(GraphicsQualityPath); _shadowDefaults = new GameplayShadowDefaults(_shadowDefaultsPathOverride); _shadowDefaults.Initialize(_shadowGraphics); }
    private void ProcessShadowTuning(double delta) { if (_open) { _shadowRefreshDelay -= delta; if (_shadowRefreshDelay <= 0) { _shadowRefreshDelay=.25; RefreshShadowControls(); } } if (_shadowDefaults?.Dirty != true) return; _shadowSaveDelay -= delta; _shadowRetryDelay -= delta; if (_shadowSaveDelay <= 0 && _shadowRetryDelay <= 0 && !FlushShadowDefaults()) _shadowRetryDelay=5; }
    private bool FlushShadowDefaults() { bool saved = _shadowDefaults?.Save() ?? true; UpdateShadowStatus(); return saved; }
    public void InstallShadowDefaults(string isolatedPath) { string generated=ProjectSettings.GlobalizePath("user://generated").Replace('\\','/'); string normalized=isolatedPath.Replace('\\','/'); if (!isolatedPath.StartsWith("res://.godot/",System.StringComparison.Ordinal) && !isolatedPath.StartsWith("user://generated/",System.StringComparison.Ordinal) && !normalized.StartsWith(generated+"/",System.StringComparison.Ordinal)) return; _shadowDefaultsPathOverride=isolatedPath; if (_shadowDefaults is null) return; _shadowDefaults=new GameplayShadowDefaults(isolatedPath); _shadowDefaults.Initialize(_shadowGraphics); RefreshShadowControls(); }
    private void BuildPersistentShadowControls(VBoxContainer tab)
    {
        tab.AddChild(new Label { Text = "Directional shadow quality. Changes are saved and reapply after graphics presets.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        VBoxContainer quality = TuningMenuUi.CreateSection(tab, "Quality");
        _shadowEnabled = new CheckButton { Name="SunShadows", Text="Sun shadows", ButtonPressed=_shadowGraphics?.RequestedSunShadowsEnabled ?? true }; _shadowEnabled.Toggled += v => SetShadowValue("Enabled", v); quality.AddChild(_shadowEnabled);
        _localShadowHandoffEnabled = new CheckButton { Name="LocalShadowHandoff", Text="Suspend sun shadows below local horizon", ButtonPressed=_shadowGraphics?.LocalShadowHandoffEnabled ?? true, Disabled=_shadowGraphics == null, TooltipText="Keeps planet shadows available while the sun is safely below the local ring horizon." }; _localShadowHandoffEnabled.Toggled += value => SetShadowValue("LocalShadowHandoffEnabled", value); quality.AddChild(_localShadowHandoffEnabled);
        AddShadowSlider(quality, "SunShadowOffElevationDegrees", "Suspend below elevation (degrees)", -45, -1, .1, _shadowGraphics?.EffectiveSunShadowOffElevationDegrees ?? -18);
        AddShadowSlider(quality, "SunShadowOnElevationDegrees", "Resume above elevation (degrees)", -45, -1, .1, _shadowGraphics?.EffectiveSunShadowOnElevationDegrees ?? -16);
        AddShadowDepthOption(quality, _shadowGraphics?.EffectiveDirectionalShadowDepthBits ?? 16); AddShadowAtlasOption(quality, _shadowGraphics?.EffectiveDirectionalShadowAtlasSize ?? 8192); AddShadowOption(quality, "Filter", "Soft-shadow filter", new[] { "Hard", "Soft very low", "Soft low", "Soft medium", "Soft high", "Soft ultra" }, new[] { 0,1,2,3,4,5 }, _shadowGraphics?.EffectiveDirectionalShadowFilter ?? 3);
        VBoxContainer sunMap = TuningMenuUi.CreateSection(tab, "Sun map");
        AddShadowSpinBox(sunMap, "MaxDistanceMeters", "Max distance (m) (near map)", RingGraphicsQualityController.MinimumShadowDistanceMeters, RingGraphicsQualityController.MaximumShadowDistanceMeters, 10, _shadowGraphics?.EffectiveShadowDistanceMeters ?? 6000,
            "How far the sun shadow map reaches. Terrain shadow casters follow this range. The requested distance is kept across presets.");
        AddShadowOption(sunMap, "CascadeCount", "Cascades (near map)", new[] { "2", "4" }, new[] { 2, 4 }, _shadowGraphics?.EffectiveSunCascadeCount ?? 4);
        sunMap.AddChild(new Label { Text = "Two cascades are cheaper; only the near cascade distance below splits them.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        VBoxContainer planetLight = TuningMenuUi.CreateSection(tab, "Planet light");
        _planetShadowsEnabled = new CheckButton { Name="PlanetShadows", Text="Planet light shadows", ButtonPressed=_shadowGraphics?.EffectivePlanetShadowsEnabled ?? true, Disabled=_shadowGraphics == null, TooltipText="Shadow map for the gas giant's light on the planet-lit night side." };
        _planetShadowsEnabled.Toggled += value => SetShadowValue("PlanetShadowsEnabled", value);
        planetLight.AddChild(_planetShadowsEnabled);
        AddShadowSpinBox(planetLight, "PlanetShadowDistanceMeters", "Max distance (m) (near map)", RingGraphicsQualityController.MinimumPlanetShadowDistanceMeters, RingGraphicsQualityController.MaximumPlanetShadowDistanceMeters, 100, _shadowGraphics?.EffectivePlanetShadowDistanceMeters ?? 6000);
        AddShadowOption(planetLight, "PlanetCascadeCount", "Cascades (near map)", new[] { "2", "4" }, new[] { 2, 4 }, _shadowGraphics?.EffectivePlanetCascadeCount ?? 4);
        VBoxContainer terrainMacro = TuningMenuUi.CreateSection(tab, "Terrain macro shadows");
        _terrainMacroShadowEnabled = new CheckButton { Name="TerrainMacroShadows", Text="Enable terrain macro shadows", ButtonPressed=_shadowGraphics?.EffectiveTerrainMacroShadowsEnabled ?? true, Disabled=_shadowGraphics == null, TooltipText="Uses the terrain height summary to cast distant mountain shadows from sunlight and nighttime planet light. Strength, trace, and handoff controls apply to both." };
        _terrainMacroShadowEnabled.Toggled += value => SetShadowValue("TerrainMacroEnabled", value);
        terrainMacro.AddChild(_terrainMacroShadowEnabled);
        _terrainMacroWallShadowsEnabled = new CheckButton { Name="TerrainMacroWallShadows", Text="Enable structural wall casting", ButtonPressed=_shadowGraphics?.EffectiveTerrainMacroWallShadowsEnabled ?? true, Disabled=_shadowGraphics == null, TooltipText="Includes the ring's structural walls as terrain macro shadow casters." };
        _terrainMacroWallShadowsEnabled.Toggled += value => SetShadowValue("TerrainMacroWallShadowsEnabled", value);
        terrainMacro.AddChild(_terrainMacroWallShadowsEnabled);
        AddShadowSlider(terrainMacro, "TerrainMacroStrength", "Terrain shadow strength", 0, .8, .01, _shadowGraphics?.EffectiveTerrainMacroShadowStrength ?? .34);
        AddShadowSpinBox(terrainMacro, "TerrainMacroTraceSteps", "Terrain trace steps", 1, 32, 1, _shadowGraphics?.EffectiveTerrainMacroShadowTraceSteps ?? 10);
        AddShadowSpinBox(terrainMacro, "TerrainMacroMaxTraceMeters", "Maximum trace distance (m)", 4000, 320000, 1000, _shadowGraphics?.EffectiveTerrainMacroShadowMaxTraceMeters ?? 96000);
        AddShadowSpinBox(terrainMacro, "TerrainMacroFadeStartMeters", "Macro handoff near (m)", 0, 50000, 100, _shadowGraphics?.EffectiveTerrainMacroShadowFadeStartMeters ?? 3200);
        AddShadowSpinBox(terrainMacro, "TerrainMacroFadeEndMeters", "Macro handoff far (m)", 100, 60000, 100, _shadowGraphics?.EffectiveTerrainMacroShadowFadeEndMeters ?? 6500);
        BuildHeightShadowControls(tab);
        BuildContactShadowControls(tab);
        BuildVegetationShadowControls(tab);
        VBoxContainer cascades = TuningMenuUi.CreateSection(tab, "Cascade coverage");
        AddShadowSpinBox(cascades, "NearCascadeMeters", "Near cascade distance (m)", RingGraphicsQualityController.MinimumNearCascadeMeters, RingGraphicsQualityController.MaximumNearCascadeMeters, 1, _shadowGraphics?.EffectiveNearCascadeMeters ?? RingGraphicsQualityController.DefaultNearCascadeMeters,
            "Focuses near shadow coverage, up to 40% of the shadow range. The requested distance is kept across presets.");
        AddShadowSlider(cascades, "Split2", "Cascade split 2", .02,.70,.001, _sun?.DirectionalShadowSplit2 ?? .06); AddShadowSlider(cascades, "Split3", "Cascade split 3", .03,.95,.001, _sun?.DirectionalShadowSplit3 ?? .28); AddShadowSlider(cascades, "FadeStart", "Shadow fade start (near map)", .50,.98,.01, _sun?.DirectionalShadowFadeStart ?? .88);
        VBoxContainer bias = TuningMenuUi.CreateSection(tab, "Bias / softness");
        AddShadowSlider(bias, "Bias", "Shadow bias", 0,1,.01, _sun?.ShadowBias ?? .08); AddShadowSlider(bias, "NormalBias", "Normal bias", 0,4,.05, _sun?.ShadowNormalBias ?? 1.5); AddShadowSlider(bias, "SunAngularDistance", "Sun softness (degrees)", 0, 3, .01, _sun?.LightAngularDistance ?? 0,
            "Zero disables contact-hardening shadows; the selected shadow filter still applies.");
        _sunStabilizationEnabled = new CheckButton
        {
            Name = "SunStabilizationEnabled",
            Text = "Stabilize moving sun",
            ButtonPressed = _dayNightCycle?.SunShadowStabilizationEnabled ?? true,
            Disabled = _dayNightCycle == null,
            TooltipText = "Holds the directional light between small sun-angle changes to reduce moving-shadow shimmer."
        };
        _sunStabilizationEnabled.Toggled += value => SetShadowValue("SunStabilizationEnabled", value);
        VBoxContainer stabilization = TuningMenuUi.CreateSection(tab, "Stabilization");
        stabilization.AddChild(_sunStabilizationEnabled);
        HSlider sunStep = AddShadowSlider(stabilization, "SunStepDegrees", "Sun update step (degrees)", 0, .05, .001, _dayNightCycle?.SunShadowStepDegrees ?? .01,
            "The directional light updates after this angular change. The clock, sky and eclipses keep moving.");
        sunStep.Editable = _dayNightCycle != null;
        stabilization.AddChild(new Label
        {
            Text = "Larger steps can make shadows jump; smaller steps are used near the horizon.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = RingUiStyle.Muted
        });
        _pauseDayNight = new CheckButton
        {
            Name = "PauseDayNightTest",
            Text = "Pause day/night (test)",
            ButtonPressed = _dayNightCycle?.Paused ?? false,
            Disabled = _dayNightCycle == null,
            TooltipText = "Temporary test control for isolating sun motion. This setting is not saved."
        };
        _pauseDayNight.Toggled += value => { if (_dayNightCycle != null) _dayNightCycle.Paused = value; };
        stabilization.AddChild(_pauseDayNight);
        VBoxContainer debug = TuningMenuUi.CreateSection(tab, "Debug");
        AddShadowOption(debug, "ShadowDebugMode", "Shadow debug view", new[] { "Off", "Sun visibility", "Geometry", "Contact", "Terrain", "Cloud" }, new[] { 0, 1, 2, 3, 4, 5 }, _shadowGraphics?.ShadowDebugMode ?? 0);
        debug.AddChild(new Label { Text = "Shows one sun-shadow term as white (lit) to black (shadowed) on shadow-receiving surfaces. Saved with the other shadow settings.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        var row = new HBoxContainer(); var save = new Button { Name="ShadowSaveNow", Text="Save shadows", SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; save.Pressed += () => FlushShadowDefaults(); row.AddChild(save); var reset = new Button { Name="ShadowResetDefaults", Text="Reset shadow overrides", SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; reset.Pressed += () => { _shadowDefaults.Reset(_shadowGraphics); _shadowSaveDelay=0; FlushShadowDefaults(); RefreshShadowControls(); }; row.AddChild(reset); tab.AddChild(row);
        _shadowStatus = new Label { Name="ShadowRuntimeStatus", AutowrapMode=TextServer.AutowrapMode.WordSmart, Modulate=RingUiStyle.Muted }; tab.AddChild(_shadowStatus); UpdateShadowStatus();
    }
    private void BuildHeightShadowControls(VBoxContainer tab)
    {
        VBoxContainer height = TuningMenuUi.CreateSection(tab, "Terrain height shadows");
        _heightShadowEnabled = new CheckButton { Name = "HeightShadowEnabled", Text = "Enable terrain height shadows", ButtonPressed = _shadowGraphics?.EffectiveHeightShadowEnabled ?? false, Disabled = _shadowGraphics == null,
            TooltipText = "Builds a sliced GPU shadow-line height map around the camera (two levels, ring-curvature aware). Terrain, water and trees use it for terrain shadows beyond the near sun map, which then shrinks to the near-map distance (see Near map)." };
        _heightShadowEnabled.Toggled += value => SetShadowValue("HeightShadowEnabled", value);
        height.AddChild(_heightShadowEnabled);
        _heightShadowNearMap = new CheckButton { Name = "HeightShadowNearMapEnabled", Text = "Near sun map while height shadows are ready", ButtonPressed = _shadowGraphics?.EffectiveHeightShadowNearMapEnabled ?? true, Disabled = _shadowGraphics == null,
            TooltipText = "Shrinks the sun shadow map to a 2-cascade near map (fade start 0.8) once the sun height shadows cover the camera; the height shadows own everything past it. Max distance, cascade count and fade start under Sun map are ignored while it is active; the near cascade distance still applies. Only effective with height shadows enabled." };
        _heightShadowNearMap.Toggled += value => SetShadowValue("HeightShadowNearMapEnabled", value);
        height.AddChild(_heightShadowNearMap);
        AddShadowOption(height, "HeightShadowNearMapDistanceMeters", "Near map distance", new[] { "300 m", "400 m", "500 m" }, new[] { 300, 400, 500 }, _shadowGraphics?.EffectiveHeightShadowNearMapDistanceMeters ?? 400);
        _heightShadowPlanetNearMap = new CheckButton { Name = "HeightShadowPlanetNearMapEnabled", Text = "Near planet map at night while height shadows are ready", ButtonPressed = _shadowGraphics?.EffectiveHeightShadowPlanetNearMapEnabled ?? true, Disabled = _shadowGraphics == null,
            TooltipText = "Shrinks the planet-light shadow map to a 2-cascade near map (fade start 0.8) once the planet height shadows cover the camera at night; the height shadows own everything past it. Planet distance and cascade count under Planet light are ignored while it is active (shown as near map); the twilight overlap keeps the planet map at its normal range. Only effective with height shadows enabled." };
        _heightShadowPlanetNearMap.Toggled += value => SetShadowValue("HeightShadowPlanetNearMapEnabled", value);
        height.AddChild(_heightShadowPlanetNearMap);
        AddShadowSpinBox(height, "HeightShadowPlanetNearMapDistanceMeters", "Near planet map distance (m)", 100, 250, 10, _shadowGraphics?.EffectiveHeightShadowPlanetNearMapDistanceMeters ?? 250, "Range of the near planet map (also the terrain shadow caster range at night).");
        AddShadowOption(height, "HeightShadowL0TexelMeters", "Detail / range", new[] { "2 m texels, 4 km range", "4 m texels, 8 km range", "8 m texels, 16 km range" }, new[] { 2, 4, 8 }, _shadowGraphics?.EffectiveHeightShadowL0TexelMeters ?? 4);
        AddShadowOption(height, "HeightShadowLevels", "Levels", new[] { "1 (near only)", "2 (near + far)" }, new[] { 1, 2 }, _shadowGraphics?.EffectiveHeightShadowLevels ?? 2);
        AddShadowSlider(height, "HeightShadowSunStepDegrees", "Sun step (degrees)", .02, .5, .01, _shadowGraphics?.EffectiveHeightShadowSunStepDegrees ?? .1,
            "Rebuild after the light moved this far. Low sun moves shadow tips hundreds of metres per 0.1 degree; the crossfade hides the jump.");
        AddShadowSpinBox(height, "HeightShadowRowsPerFrame", "Rows per frame (max)", 16, 1024, 16, _shadowGraphics?.EffectiveHeightShadowRowsPerFrame ?? 64,
            "Upper bound of the sliced build; a full build is 2048 rows. The GPU budget below can lower it automatically at low sun.");
        AddShadowSlider(height, "HeightShadowBudgetMs", "GPU budget per frame (ms)", .05, 2, .05, _shadowGraphics?.EffectiveHeightShadowBudgetMs ?? .15,
            "The build shrinks its slices until their measured GPU time fits this budget (the build then spans more frames).");
        AddShadowSlider(height, "HeightShadowPenumbraDegrees", "Penumbra softness (degrees)", 0, 2, .05, _shadowGraphics?.EffectiveHeightShadowPenumbraDegrees ?? .5);
        AddShadowSlider(height, "HeightShadowCrossfadeSeconds", "Crossfade (seconds)", 0, 3, .1, _shadowGraphics?.EffectiveHeightShadowCrossfadeSeconds ?? 1);
        AddShadowSpinBox(height, "HeightShadowFillWorkers", "Height fill workers", 1, 4, 1, _shadowGraphics?.EffectiveHeightShadowFillWorkers ?? 2, "Advanced: background threads that sample terrain heights after a move or teleport (low priority).");
        _heightShadowStatus = new Label { Name = "HeightShadowStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        height.AddChild(_heightShadowStatus);
        _heightShadowOverlayToggle = new CheckButton { Name = "HeightShadowOverlayToggle", Text = "Height shadow overlay (not saved)", ButtonPressed = _heightShadowOverlay != null, TooltipText = "Top-down 256 px debug view of the published height-shadow globals around the camera." };
        _heightShadowOverlayToggle.Toggled += SetHeightShadowOverlay;
        height.AddChild(_heightShadowOverlayToggle);
        _heightShadowOverlayMode = new OptionButton { Name = "HeightShadowOverlayMode", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        for (int i = 0; i < RingHeightShadowOverlay.ModeNames.Length; i++) _heightShadowOverlayMode.AddItem(RingHeightShadowOverlay.ModeNames[i], i);
        _heightShadowOverlayMode.ItemSelected += index => { if (_heightShadowOverlay != null) _heightShadowOverlay.Mode = _heightShadowOverlayMode.GetItemId((int)index); };
        height.AddChild(_heightShadowOverlayMode);
        UpdateHeightShadowStatus();
    }
    private void BuildContactShadowControls(VBoxContainer tab)
    {
        VBoxContainer contact = TuningMenuUi.CreateSection(tab, "Contact shadows");
        _contactShadowEnabled = new CheckButton { Name = "ContactShadowEnabled", Text = "Enable contact shadows", ButtonPressed = _shadowGraphics?.EffectiveContactShadowEnabled ?? false, Disabled = _shadowGraphics == null,
            TooltipText = "Screen-space ray march on the depth prepass toward the sun: fine shadows under small props, rocks and grass that the sun shadow map is too coarse for. Main view only; off while sun shadows are suspended." };
        _contactShadowEnabled.Toggled += value => SetShadowValue("ContactShadowEnabled", value);
        contact.AddChild(_contactShadowEnabled);
        AddShadowSlider(contact, "ContactShadowLengthMeters", "Length (m)", .25, 4, .05, _shadowGraphics?.EffectiveContactShadowLengthMeters ?? 1.5,
            "World-space length of the sun ray (also limited to 10% of the screen height).");
        AddShadowSlider(contact, "ContactShadowThicknessMeters", "Thickness (m)", .05, 1, .05, _shadowGraphics?.EffectiveContactShadowThicknessMeters ?? .25,
            "Surfaces thicker than this are treated as thin: a ray passing behind them is not shadowed.");
        AddShadowSlider(contact, "ContactShadowStrength", "Strength", 0, 1, .05, _shadowGraphics?.EffectiveContactShadowStrength ?? 1);
        AddShadowSlider(contact, "ContactShadowFadeMeters", "Fade distance (m)", 25, 400, 5, _shadowGraphics?.EffectiveContactShadowFadeMeters ?? 150,
            "Contact shadows fade out between 70% of this distance and the distance itself.");
        AddShadowSlider(contact, "ContactShadowGrassStrength", "Grass strength", 0, 1, .05, _shadowGraphics?.EffectiveContactShadowGrassStrength ?? .35,
            "Strength on grass blades and cards (they also receive shadows from neighbouring blades).");
        AddShadowOption(contact, "ContactShadowSteps", "Steps", new[] { "8", "12", "16", "24", "32" }, new[] { 8, 12, 16, 24, 32 }, _shadowGraphics?.EffectiveContactShadowSteps ?? 16);
        _contactShadowStatus = new Label { Name = "ContactShadowStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        contact.AddChild(_contactShadowStatus);
        UpdateContactShadowStatus();
    }
    private void BuildVegetationShadowControls(VBoxContainer tab)
    {
        VBoxContainer vegetation = TuningMenuUi.CreateSection(tab, "Vegetation shadows");
        _vegetationShadowInstance = new CheckButton { Name = "VegetationShadowInstanceEnabled", Text = "Per-instance vegetation shadows", ButtonPressed = _shadowGraphics?.EffectiveVegetationShadowInstanceEnabled ?? false, Disabled = _shadowGraphics == null,
            TooltipText = "GPU-culled grass cards, standing grass and trees take their terrain height-shadow and cloud-shadow terms from a per-instance atlas the vegetation culling pass writes (like per-instance haze) instead of sampling them per vertex. Falls back per vertex wherever the atlas has no data. Needs GPU vegetation culling; adds one RGBA16F atlas the size of the haze atlas." };
        _vegetationShadowInstance.Toggled += value => SetShadowValue("VegetationShadowInstanceEnabled", value);
        vegetation.AddChild(_vegetationShadowInstance);
        AddShadowSpinBox(vegetation, "VegetationShadowRefreshHz", "Cloud refresh rate (Hz)", 2, 30, 1, _shadowGraphics?.EffectiveVegetationShadowRefreshHz ?? 10,
            "How often already-culled vegetation gets its cloud-shadow terms rewritten while a cloud map is shown (heightfield changes, crossfades and camera moves refresh immediately).");
        _vegetationUnifiedCompose = new CheckButton { Name = "VegetationUnifiedShadowCompose", Text = "Unified vegetation shadow composition (reserved, no effect yet)", ButtonPressed = _shadowGraphics?.EffectiveVegetationUnifiedShadowCompose ?? false, Disabled = _shadowGraphics == null,
            TooltipText = "Trees combine map, terrain and cloud shadow with the shared min rule instead of the legacy product, and standing grass takes the cloud as a direct-light term instead of darkening albedo and ambient occlusion. Changes the look; off by default." };
        _vegetationUnifiedCompose.Toggled += value => SetShadowValue("VegetationUnifiedShadowCompose", value);
        vegetation.AddChild(_vegetationUnifiedCompose);
        _vegetationShadowStatus = new Label { Name = "VegetationShadowStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        vegetation.AddChild(_vegetationShadowStatus);
        UpdateVegetationShadowStatus();
    }
    private void UpdateVegetationShadowStatus()
    {
        if (_vegetationShadowStatus != null && _shadowGraphics != null) _vegetationShadowStatus.Text = _shadowGraphics.EffectiveVegetationShadowStatus;
    }
    private void UpdateContactShadowStatus()
    {
        if (_contactShadowStatus != null && _shadowGraphics != null) _contactShadowStatus.Text = _shadowGraphics.EffectiveContactShadowStatus;
    }
    private void SetHeightShadowOverlay(bool on)
    {
        if (on && _heightShadowOverlay == null && _heightShadowOverlayToggle?.GetParent() is Node parent)
        {
            _heightShadowOverlay = new RingHeightShadowOverlay { Mode = _heightShadowOverlayMode?.GetSelectedId() ?? 0 };
            parent.AddChild(_heightShadowOverlay);
            RingTerrainHeightShadow.Instance?.RequestUnresolvedStats();
        }
        else if (!on && _heightShadowOverlay != null)
        {
            _heightShadowOverlay.QueueFree();
            _heightShadowOverlay = null;
        }
    }
    private void UpdateHeightShadowStatus()
    {
        if (_heightShadowStatus == null || _shadowGraphics == null) return;
        if (_shadowGraphics.EffectiveHeightShadowEnabled && _open) RingTerrainHeightShadow.Instance?.RequestUnresolvedStats();
        _heightShadowStatus.Text = _shadowGraphics.EffectiveHeightShadowStatus
            + (_shadowGraphics.NearSunMapActive ? $" Sun map: near mode ({_shadowGraphics.EffectiveShadowDistanceMeters:0} m, 2 cascades)." : _shadowGraphics.EffectiveHeightShadowEnabled ? " Sun map: full range." : "")
            + (_shadowGraphics.NearPlanetMapActive ? $" Planet map: near mode ({_shadowGraphics.EffectiveHeightShadowPlanetNearMapDistanceMeters} m, 2 cascades)." : _shadowGraphics.EffectiveHeightShadowEnabled ? " Planet map: full range." : "");
    }
    private HSlider AddShadowSlider(VBoxContainer parent, string key, string label, double min, double max, double step, double initial, string tooltip = "")
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(150, 0) });
        var slider = new HSlider
        {
            Name = "Shadow" + key,
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = initial,
            TooltipText = tooltip,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var value = new Label { Name = "Shadow" + key + "Value", CustomMinimumSize = new Vector2(55, 0), Text = initial.ToString("0.###") };
        slider.ValueChanged += changed => { value.Text = changed.ToString("0.###"); SetShadowValue(key, (float)changed); };
        _shadowRanges[key] = slider;
        _shadowValueLabels[key] = value;
        row.AddChild(slider);
        row.AddChild(value);
        parent.AddChild(row);
        return slider;
    }
    private SpinBox AddShadowSpinBox(VBoxContainer parent, string key, string label, double min, double max, double step, double initial, string tooltip = "")
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(150, 0) });
        var spin = new SpinBox { Name = "Shadow" + key, MinValue = min, MaxValue = max, Step = step, Value = initial, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        spin.ValueChanged += changed => SetShadowValue(key, (float)changed);
        _shadowRanges[key] = spin;
        row.AddChild(spin);
        parent.AddChild(row);
        return spin;
    }
    private void AddShadowOption(VBoxContainer parent,string key,string label,string[] labels,int[] values,int initial) { var row=new HBoxContainer(); row.AddChild(new Label { Text=label, CustomMinimumSize=new Vector2(150,0) }); var option=new OptionButton { Name="Shadow"+key, SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; for(int i=0;i<labels.Length;i++) { option.AddItem(labels[i],values[i]); if(values[i]==initial) option.Select(i); } option.ItemSelected += i => SetShadowValue(key,option.GetItemId((int)i)); _shadowOptions[key]=option; row.AddChild(option); parent.AddChild(row); }
    private void AddShadowDepthOption(VBoxContainer parent, int initial)
    {
        var row = new HBoxContainer(); row.AddChild(new Label { Text = "Shadow depth precision", CustomMinimumSize = new Vector2(150, 0) });
        var option = new OptionButton { Name = "ShadowDepthBits", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Use 32-bit if surfaces develop speckled self-shadowing." };
        option.AddItem("16-bit (lower memory)", 16); option.AddItem("32-bit (higher precision)", 32);
        SetOptionSelection(option, initial);
        option.ItemSelected += index => { SetShadowValue("DepthBits", option.GetItemId((int)index)); RefreshShadowControls(); };
        _shadowOptions["DepthBits"] = option; row.AddChild(option); parent.AddChild(row);
        parent.AddChild(new Label { Text = "Use 32-bit if surfaces develop speckled self-shadowing.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
    }
    private void AddShadowAtlasOption(VBoxContainer parent, int initial)
    {
        var row = new HBoxContainer(); row.AddChild(new Label { Text = "Atlas resolution", CustomMinimumSize = new Vector2(150, 0) });
        var option = new OptionButton { Name = "ShadowAtlasSize", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        int limit = _shadowGraphics?.DirectionalShadowAtlasHardwareLimit ?? RingGraphicsQualityController.UnverifiedDirectionalShadowAtlasLimit;
        bool unverified = _shadowGraphics?.DirectionalShadowAtlasLimitIsUnverified ?? true;
        for (int index = 0; index < RingGraphicsQualityController.DirectionalShadowAtlasSizes.Count; index++)
        {
            int size = RingGraphicsQualityController.DirectionalShadowAtlasSizes[index];
            option.AddItem(size.ToString(), size);
            bool supported = size <= limit;
            option.GetPopup().SetItemDisabled(index, !supported);
            if (size == initial) option.Select(index);
        }
        option.ItemSelected += index =>
        {
            int size = option.GetItemId((int)index);
            if (_shadowGraphics?.IsDirectionalShadowAtlasSizeSupported(size) != true) { RefreshShadowControls(); return; }
            SetShadowValue("AtlasSize", size);
        };
        _shadowOptions["AtlasSize"] = option; row.AddChild(option); parent.AddChild(row);
        UpdateShadowAtlasOptionPresentation(option, _shadowGraphics?.EffectiveDirectionalShadowDepthBits ?? 16, limit, unverified);
        parent.AddChild(new Label { Text = "Estimates cover raw shadow depth storage; they are not measured VRAM use.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
    }
    private static string FormatShadowDepthMemory(ulong bytes)
    {
        const double gib = 1024d * 1024d * 1024d;
        return bytes >= 512UL * 1024UL * 1024UL ? $"{bytes / gib:0.#} GiB" : $"{bytes / (1024d * 1024d):0.#} MiB";
    }
    private static void SetOptionSelection(OptionButton option, int value) { for (int i = 0; i < option.ItemCount; i++) if (option.GetItemId(i) == value) { option.Select(i); return; } }
    private static void UpdateShadowAtlasOptionPresentation(OptionButton option, int depthBits, int limit, bool unverified)
    {
        for (int index = 0; index < option.ItemCount; index++)
        {
            int size = option.GetItemId(index); bool supported = size <= limit;
            ulong bytes = RingGraphicsQualityController.DirectionalShadowAtlasMinimumDepthBytes(size, depthBits);
            string memory = FormatShadowDepthMemory(bytes);
            string text = $"{size} ({memory} raw depth)";
            string tooltip = supported
                ? $"Estimated raw depth storage: {memory} at {depthBits}-bit; other graphics features use additional memory."
                : unverified ? $"Unavailable: no verified RenderingDevice limit; conservative fallback is {limit}px. Estimated raw depth storage: {memory} at {depthBits}-bit."
                : $"Unavailable: GPU texture/framebuffer limit is {limit}px. Estimated raw depth storage: {memory} at {depthBits}-bit.";
            if (option.GetItemText(index) != text) option.SetItemText(index, text);
            if (option.GetPopup().GetItemTooltip(index) != tooltip) option.GetPopup().SetItemTooltip(index, tooltip);
        }
    }
    private void SetShadowValue(string key, Variant value) { if (_shadowDefaults.SetOverride(_shadowGraphics,key,value)) { _shadowSaveDelay=.5; _shadowRetryDelay=0; UpdateShadowStatus(); } }
    private void RefreshShadowControls()
    {
        if (_shadowGraphics == null) return;
        if (_sun != null)
        {
            SetRange("Split2", _sun.DirectionalShadowSplit2);
            SetRange("Split3", _sun.DirectionalShadowSplit3);
            SetRange("FadeStart", _sun.DirectionalShadowFadeStart);
            SetRange("Bias", _sun.ShadowBias);
            SetRange("NormalBias", _sun.ShadowNormalBias);
            SetRange("SunAngularDistance", _sun.LightAngularDistance);
            _shadowEnabled?.SetPressedNoSignal(_shadowGraphics.RequestedSunShadowsEnabled);
            _localShadowHandoffEnabled?.SetPressedNoSignal(_shadowGraphics.LocalShadowHandoffEnabled);
        }
        if (_dayNightCycle != null)
        {
            SetRange("SunStepDegrees", _dayNightCycle.SunShadowStepDegrees);
            _sunStabilizationEnabled?.SetPressedNoSignal(_dayNightCycle.SunShadowStabilizationEnabled);
            _pauseDayNight?.SetPressedNoSignal(_dayNightCycle.Paused);
        }
        SetOption("AtlasSize", _shadowGraphics.EffectiveDirectionalShadowAtlasSize);
        SetOption("DepthBits", _shadowGraphics.EffectiveDirectionalShadowDepthBits);
        SetOption("Filter", _shadowGraphics.EffectiveDirectionalShadowFilter);
        SetOption("ShadowDebugMode", _shadowGraphics.ShadowDebugMode);
        SetRange("MaxDistanceMeters", _shadowGraphics.EffectiveShadowDistanceMeters);
        SetOption("CascadeCount", _shadowGraphics.EffectiveSunCascadeCount);
        if (!_shadowGraphics.NearPlanetMapActive) // the near planet map forces its own distance and 2 cascades; the rows keep the user's values
        {
            SetRange("PlanetShadowDistanceMeters", _shadowGraphics.EffectivePlanetShadowDistanceMeters);
            SetOption("PlanetCascadeCount", _shadowGraphics.EffectivePlanetCascadeCount);
        }
        _planetShadowsEnabled?.SetPressedNoSignal(_shadowGraphics.EffectivePlanetShadowsEnabled);
        SetRange("NearCascadeMeters", _shadowGraphics.EffectiveNearCascadeMeters);
        _heightShadowEnabled?.SetPressedNoSignal(_shadowGraphics.EffectiveHeightShadowEnabled);
        SetOption("HeightShadowL0TexelMeters", _shadowGraphics.EffectiveHeightShadowL0TexelMeters);
        SetOption("HeightShadowLevels", _shadowGraphics.EffectiveHeightShadowLevels);
        _heightShadowNearMap?.SetPressedNoSignal(_shadowGraphics.EffectiveHeightShadowNearMapEnabled);
        SetOption("HeightShadowNearMapDistanceMeters", _shadowGraphics.EffectiveHeightShadowNearMapDistanceMeters);
        _heightShadowPlanetNearMap?.SetPressedNoSignal(_shadowGraphics.EffectiveHeightShadowPlanetNearMapEnabled);
        SetRange("HeightShadowPlanetNearMapDistanceMeters", _shadowGraphics.EffectiveHeightShadowPlanetNearMapDistanceMeters);
        SetRange("HeightShadowSunStepDegrees", _shadowGraphics.EffectiveHeightShadowSunStepDegrees);
        SetRange("HeightShadowRowsPerFrame", _shadowGraphics.EffectiveHeightShadowRowsPerFrame);
        SetRange("HeightShadowBudgetMs", _shadowGraphics.EffectiveHeightShadowBudgetMs);
        SetRange("HeightShadowPenumbraDegrees", _shadowGraphics.EffectiveHeightShadowPenumbraDegrees);
        SetRange("HeightShadowCrossfadeSeconds", _shadowGraphics.EffectiveHeightShadowCrossfadeSeconds);
        SetRange("HeightShadowFillWorkers", _shadowGraphics.EffectiveHeightShadowFillWorkers);
        _contactShadowEnabled?.SetPressedNoSignal(_shadowGraphics.EffectiveContactShadowEnabled);
        SetRange("ContactShadowLengthMeters", _shadowGraphics.EffectiveContactShadowLengthMeters);
        SetRange("ContactShadowThicknessMeters", _shadowGraphics.EffectiveContactShadowThicknessMeters);
        SetRange("ContactShadowStrength", _shadowGraphics.EffectiveContactShadowStrength);
        SetRange("ContactShadowFadeMeters", _shadowGraphics.EffectiveContactShadowFadeMeters);
        SetRange("ContactShadowGrassStrength", _shadowGraphics.EffectiveContactShadowGrassStrength);
        SetOption("ContactShadowSteps", _shadowGraphics.EffectiveContactShadowSteps);
        _vegetationShadowInstance?.SetPressedNoSignal(_shadowGraphics.EffectiveVegetationShadowInstanceEnabled);
        _vegetationUnifiedCompose?.SetPressedNoSignal(_shadowGraphics.EffectiveVegetationUnifiedShadowCompose);
        SetRange("VegetationShadowRefreshHz", _shadowGraphics.EffectiveVegetationShadowRefreshHz);
        SetRange("TerrainMacroStrength", _shadowGraphics.EffectiveTerrainMacroShadowStrength);
        SetRange("TerrainMacroTraceSteps", _shadowGraphics.EffectiveTerrainMacroShadowTraceSteps);
        SetRange("TerrainMacroMaxTraceMeters", _shadowGraphics.EffectiveTerrainMacroShadowMaxTraceMeters);
        SetRange("TerrainMacroFadeStartMeters", _shadowGraphics.EffectiveTerrainMacroShadowFadeStartMeters);
        SetRange("TerrainMacroFadeEndMeters", _shadowGraphics.EffectiveTerrainMacroShadowFadeEndMeters);
        SetRange("SunShadowOffElevationDegrees", _shadowGraphics.EffectiveSunShadowOffElevationDegrees);
        SetRange("SunShadowOnElevationDegrees", _shadowGraphics.EffectiveSunShadowOnElevationDegrees);
        _terrainMacroShadowEnabled?.SetPressedNoSignal(_shadowGraphics.EffectiveTerrainMacroShadowsEnabled);
        _terrainMacroWallShadowsEnabled?.SetPressedNoSignal(_shadowGraphics.EffectiveTerrainMacroWallShadowsEnabled);
        if (_shadowOptions.TryGetValue("AtlasSize", out OptionButton? atlas))
            UpdateShadowAtlasOptionPresentation(atlas, _shadowGraphics.EffectiveDirectionalShadowDepthBits, _shadowGraphics.DirectionalShadowAtlasHardwareLimit, _shadowGraphics.DirectionalShadowAtlasLimitIsUnverified);
        UpdateShadowStatus();
        UpdateHeightShadowStatus();
        UpdateContactShadowStatus();
        UpdateVegetationShadowStatus();
    }
    private void SetRange(string key,float value) { if (_shadowRanges.TryGetValue(key,out Range? range)) range.SetValueNoSignal(value); if (_shadowValueLabels.TryGetValue(key,out Label? label)) label.Text=value.ToString("0.###"); }
    private void SetOption(string key,int value) { if (_shadowOptions.TryGetValue(key,out OptionButton? option)) SetOptionSelection(option, value); }
    private void UpdateShadowStatus()
    {
        if (_shadowStatus == null || _shadowGraphics == null) return;
        string state = _shadowDefaults.LastError.Length > 0
            ? "Shadow settings could not be saved; retrying."
            : _shadowDefaults.Dirty ? "Shadow settings pending." : "Shadow settings saved.";
        string stabilization = _dayNightCycle == null
            ? " Sun stabilization unavailable."
            : $" Sun step: {_dayNightCycle.EffectiveSunShadowStepDegrees:0.###}°, lag: {_dayNightCycle.SunShadowAngularLagDegrees:0.###}°.";
        string atlasLimit = _shadowGraphics.DirectionalShadowAtlasLimitIsUnverified
            ? $" Atlas limit: {_shadowGraphics.DirectionalShadowAtlasHardwareLimit}px conservative fallback (unverified)."
            : $" GPU atlas limit: {_shadowGraphics.DirectionalShadowAtlasHardwareLimit}px.";
        string requested = _shadowGraphics.RequestedDirectionalShadowAtlasSize == _shadowGraphics.EffectiveDirectionalShadowAtlasSize
            ? ""
            : $" Requested {_shadowGraphics.RequestedDirectionalShadowAtlasSize}px.";
        ulong depthBytes = _shadowGraphics.EffectiveDirectionalShadowAtlasMinimumDepthBytes;
        _shadowStatus.Text = $"{state} Effective: {_shadowGraphics.EffectiveDirectionalShadowAtlasSize}px atlas, "
            + $"{_shadowGraphics.EffectiveDirectionalShadowDepthBits}-bit depth ({FormatShadowDepthMemory(depthBytes)} estimated raw depth), filter {_shadowGraphics.EffectiveDirectionalShadowFilter}, "
            + $"near cascade {_shadowGraphics.EffectiveNearCascadeMeters:0} m, total range {_shadowGraphics.EffectiveShadowDistanceMeters:0} m."
            + $" Sun handoff: {_shadowGraphics.LocalShadowHandoffReason}."
            + requested + atlasLimit + $"{stabilization} Presets coordinate this range with terrain and cloud-shadow handoffs.";
    }
}
