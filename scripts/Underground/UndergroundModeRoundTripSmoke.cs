using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Underground;

/// <summary>
/// Scene-level suspend/resume round trip, run with --underground-mode-roundtrip-smoke-test on the real integration
/// scene. Forces underground / surface repeatedly and asserts that nothing is freed, clocks keep ticking, every
/// participant returns to its baseline, the storm flash stays dark, sun/sky darken before any hide, and forced
/// exits restore everything within one frame. Optional --underground-roundtrip-groups=N limits the enabled groups.
/// </summary>
public partial class UndergroundModeRoundTripSmoke : Node
{
    private enum Phase { WaitReady, Settle, EnterWait, HideWait, Hold, ExitWait, RestoreWait, MeasureSurface, MeasureUnderground, Done }
    private const int Cycles = 20;
    private const int MeasureRounds = 3, MeasureFrames = 240, MeasureSettleFrames = 45;
    private const int MaxFramesPerPhase = 1500;

    private RingUndergroundModeController? _controller;
    private Node? _root;
    private Phase _phase = Phase.WaitReady;
    private int _frame, _phaseFrames, _cycle, _readyFrames, _holdFrames, _restoreFrames, _measureRound, _measureCounted;
    private ulong _lastTicks;
    private double _measureSum, _measureProcessSum;
    private readonly List<double> _surfaceProcessMs = new(), _undergroundProcessMs = new();
    private readonly List<double> _surfaceMs = new(), _undergroundMs = new();
    private UndergroundGroups _groups = UndergroundGroups.All;

    private Dictionary<string, string> _baseline = new(), _baselineStripped = new();
    private ulong[] _retained = Array.Empty<ulong>();
    private string _baselineHandles = "";
    private double _baselineWeatherClock, _baselineWindClock, _baselineSunElevation, _baselineAmbientEnergy, _baselineSunEnergy, _weatherAtSettle, _settleStartWeather;
    private long _baselineWindVersion;
    private bool _weatherClockRuns;
    private int _exitVariant;
    private int _expectRestoreFrame = -1;
    private int _firstDarkFrame, _hideFrame;
    private bool _flashTested;
    private int _suspendedStreak;
    private float _maxPulseWhileSuspended;
    private bool _baselineLocalShadowSuspended;
    private Dictionary<string, int> _holdChildCounts = new();
    private bool _clocksAsserted;
    private double _cycleStartWeather, _cycleStartSun;
    private long _cycleStartWind;
    private int _hideFrames, _totalHoldFrames;

    private WorldEnvironment? _environment;
    private DirectionalLight3D? _sunLight, _planetLight;
    private Node? _atmosphere;
    private RingVolumetricCloudLayer? _weather;
    private RingDayNightCycle? _dayNight;
    private RingLocalWeatherEffects? _effects;
    private OmniLight3D? _flash;
    private RingTerrainLodCoordinator? _coordinator;
    private RingTerrainGlobalLod? _global;
    private RingScaledFarViewport? _far;
    private RingGraphicsQualityController? _graphics;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        ProcessPriority = -109; // after the mode controller (-110), before the atmosphere compose.
        foreach (string argument in OS.GetCmdlineUserArgs())
            if (argument.StartsWith("--underground-roundtrip-groups=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(argument["--underground-roundtrip-groups=".Length..], out int mask))
                _groups = (UndergroundGroups)mask & UndergroundGroups.All;
        _root = GetParent();
        _controller = _root?.GetNodeOrNull<RingUndergroundModeController>("UndergroundMode");
        _environment = _root?.GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
        _sunLight = _root?.GetNodeOrNull<DirectionalLight3D>("StarLight");
        _planetLight = _root?.GetNodeOrNull<DirectionalLight3D>("PlanetLight");
        _atmosphere = _root?.GetNodeOrNull("DaySideAtmosphere");
        _weather = _root?.GetNodeOrNull<RingVolumetricCloudLayer>("RingVolumetricClouds");
        _dayNight = _root?.GetNodeOrNull<RingDayNightCycle>("RingDayNightCycle");
        _effects = _root?.GetNodeOrNull<RingLocalWeatherEffects>("LocalWeatherEffects");
        _flash = _effects?.GetNodeOrNull<OmniLight3D>("LocalStormFlash");
        _coordinator = _root?.GetNodeOrNull<RingTerrainLodCoordinator>("TerrainLodCoordinator");
        _global = _root?.GetNodeOrNull<RingTerrainGlobalLod>("GlobalTerrainLod3");
        _far = _root?.GetNodeOrNull<RingScaledFarViewport>("ScaledFarViewport");
        _graphics = _root?.GetNodeOrNull<RingGraphicsQualityController>("GraphicsQuality");
        if (_controller == null) Fail("UndergroundMode controller missing from the scene");
        _lastTicks = Time.GetTicksUsec();
    }

    public override void _Process(double delta)
    {
        if (_controller == null || _phase == Phase.Done) return;
        _frame++;
        _phaseFrames++;
        if (_phaseFrames > MaxFramesPerPhase) { Fail($"timed out in phase {_phase} (frame {_frame}, mode {_controller.Mode}, forced '{_controller.ForcedExitReason}', cycle {_cycle})"); return; }
        if (_baseline.Count > 0 && !PerFrameChecks()) return;
        switch (_phase)
        {
            case Phase.WaitReady: StepWaitReady(); break;
            case Phase.Settle: StepSettle(); break;
            case Phase.EnterWait: StepEnterWait(); break;
            case Phase.HideWait: StepHideWait(); break;
            case Phase.Hold: StepHold(); break;
            case Phase.ExitWait: StepExitWait(); break;
            case Phase.RestoreWait: StepRestoreWait(); break;
            case Phase.MeasureSurface:
            case Phase.MeasureUnderground: StepMeasure(); break;
        }
    }

    private void StepWaitReady()
    {
        bool ready = _controller!.IsAvailable && _controller.ForcedExitReason.Length == 0 && _controller.Participants.Count > 0;
        _readyFrames = ready ? _readyFrames + 1 : 0;
        if (_readyFrames < 90 || _frame < 150) return;
        // Fast, deterministic transitions: no hold before entering and a short blend ramp.
        var parameters = _controller.Parameters;
        parameters.EnterHoldSeconds = 0f;
        parameters.ExitHoldSeconds = 0f;
        parameters.BlendSeconds = .05f;
        _controller.Parameters = parameters;
        _controller.EnabledGroups = _groups;
        SetPhase(Phase.Settle);
        _settleStartWeather = _weather?.CloudSimulationTimeSeconds ?? 0;
    }

    private void StepSettle()
    {
        if (_phaseFrames < 90) return;
        _weatherClockRuns = _weather != null && _weather.CloudSimulationTimeSeconds > _settleStartWeather;
        CaptureBaseline();
        int present = _controller!.Participants.Count(p => p.Present);
        GD.Print($"UNDERGROUND_MODE_ROUNDTRIP participants={present}/{_controller.Participants.Count} absent=[{string.Join(",", _controller.Participants.Where(p => !p.Present).Select(p => p.Name))}] groups={_groups} weatherClockRuns={_weatherClockRuns}");
        foreach ((string name, string value) in _baseline) GD.Print($"UNDERGROUND_MODE_ROUNDTRIP baseline {name}: {value}");
        GD.Print($"UNDERGROUND_MODE_ROUNDTRIP baseline handles: {_baselineHandles}");
        if (present < 8) { Fail($"only {present} participants bound"); return; }
        // The drawer section must have built every new control (group toggles, cave lighting). Toggling is left to the settings smoke so no user file is written.
        foreach (string control in new[] { "UndergroundGroupAudioWeather", "UndergroundGroupVegetation", "UndergroundGroupClouds", "UndergroundGroupWater", "UndergroundGroupFarTerrain", "UndergroundGroupShadows", "UndergroundDeepHide", "UndergroundLightingWeight", "UndergroundCaveAmbientEnergy", "UndergroundCaveAmbientColor", "UndergroundCaveFogColor",
            "UndergroundCaveRadiusAround", "UndergroundCaveRadiusAcross", "UndergroundCaveRadiusVertical", "UndergroundCaveWorkerBonus", "UndergroundCaveInstallBudget", "UndergroundCaveInstalls", "UndergroundCaveRetainMargin", "UndergroundCaveShrinkDelay", "UndergroundCaveActivationHysteresis",
            "UndergroundCoupleFog", "UndergroundFogEndFraction", "UndergroundUnfilledFogReach" })
            if (_root!.FindChild(control, true, false) == null) { Fail($"tuning drawer control {control} was not built"); return; }
        StartCycle();
    }

    private void CaptureBaseline()
    {
        _baseline = Snapshot();
        _baselineStripped = _baseline.ToDictionary(p => p.Key, p => StripContinuous(p.Value));
        _retained = RetainedIds();
        _baselineHandles = HandleSignature();
        _baselineAmbientEnergy = _environment?.Environment?.AmbientLightEnergy ?? 0;
        _baselineSunEnergy = _sunLight?.LightEnergy ?? 0;
        _baselineLocalShadowSuspended = _graphics?.LocalSunShadowSuspended ?? false;
        Check(_retained.All(id => id == 0 || IsAlive(id)), "baseline retained ids invalid");
    }

    private void StartCycle()
    {
        _exitVariant = _cycle % 3;
        _cycleStartWeather = _weather?.CloudSimulationTimeSeconds ?? 0;
        _cycleStartSun = _dayNight?.SunElevationDegrees ?? 0;
        _cycleStartWind = RingWaterModel.CurrentWindClockVersion;
        _holdFrames = 0;
        _hideFrame = _firstDarkFrame = 0;
        _controller!.Enabled = true;
        _controller.ModeOverride = UndergroundModeOverride.ForceUnderground;
        SetPhase(Phase.EnterWait);
    }

    private void StepEnterWait()
    {
        if (!_controller!.SuspensionActive) return;
        SetPhase(Phase.HideWait);
    }

    private void StepHideWait()
    {
        bool needHide = (_groups & UndergroundGroups.FarTerrain) != 0;
        if (needHide && !_controller!.HideTierActive) return;
        _hideFrame = _frame;
        _holdChildCounts = Snapshot().ToDictionary(p => p.Key, p => ChildCount(p.Value));
        SetPhase(Phase.Hold);
    }

    private void StepHold()
    {
        _holdFrames++;
        _totalHoldFrames++;
        if (_controller!.HideTierActive) _hideFrames++;
        if (!_controller.SuspensionActive) { Fail($"suspension dropped during hold (mode {_controller.Mode}, reason '{_controller.ForcedExitReason}')"); return; }
        if (_cycle == 0 && !_flashTested && _holdFrames == 2 && _effects != null)
        {
            _flashTested = true;
            _effects.TriggerLightningPreview();
        }
        // Light levels must have been driven to the underground values.
        if (_holdFrames >= 6 && _environment?.Environment is { } environment && (_groups != UndergroundGroups.None))
        {
            Check(_sunLight == null || _sunLight.LightEnergy <= .01f, $"sun energy {_sunLight?.LightEnergy} not darkened underground");
            Check(_planetLight == null || _planetLight.LightEnergy <= .01f, "planet light not darkened underground");
            Check(environment.AmbientLightEnergy <= _controller.CaveAmbientEnergy + .002f, $"ambient energy {environment.AmbientLightEnergy} above cave ambient {_controller.CaveAmbientEnergy}");
            Check(environment.AmbientLightSkyContribution <= .01f, $"sky ambient contribution {environment.AmbientLightSkyContribution} not faded");
        }
        if (_holdFrames < 6) return;
        // The first cycle also proves the long-running clocks advance while suspended.
        if (_cycle == 0 && !_clocksAsserted)
        {
            double weather = _weather?.CloudSimulationTimeSeconds ?? 0;
            bool weatherOk = !_weatherClockRuns || weather > _cycleStartWeather;
            bool windOk = RingWaterModel.CurrentWindClockVersion != _cycleStartWind || !_weatherClockRuns;
            bool sunOk = _dayNight == null || !Mathf.IsEqualApprox((float)_dayNight.SunElevationDegrees, (float)_cycleStartSun);
            if (!(weatherOk && windOk && sunOk) && _holdFrames < 900) return;
            Check(weatherOk, $"cloud weather clock stalled while suspended ({_cycleStartWeather} -> {weather})");
            Check(windOk, "water wind clock stalled while suspended");
            Check(sunOk, $"time of day stalled while suspended (sun elevation {_cycleStartSun} -> {_dayNight?.SunElevationDegrees})");
            _clocksAsserted = true;
        }
        BeginExit();
    }

    private void BeginExit()
    {
        // Held layers keep every child node: nothing is freed or recreated during the hold.
        foreach ((string name, string value) in Snapshot())
            if (HeldLayer(name) && _holdChildCounts.TryGetValue(name, out int held))
                Check(ChildCount(value) == held, $"{name} child count changed during the hold: {held} -> {ChildCount(value)}");
        _restoreFrames = 0;
        _expectRestoreFrame = _frame + 1;
        switch (_exitVariant)
        {
            case 0: _controller!.ModeOverride = UndergroundModeOverride.ForceSurface; break;
            case 1: _controller!.Enabled = false; break;
            default: _controller!.ModeOverride = UndergroundModeOverride.Auto; _expectRestoreFrame = -1; break;
        }
        SetPhase(Phase.ExitWait);
    }

    private void StepExitWait()
    {
        if (_exitVariant != 2)
        {
            if (_frame < _expectRestoreFrame) return;
            // One frame after the forced exit trigger every participant must already be back at baseline.
            Check(_controller!.Mode == UndergroundMode.Surface && _controller.EnclosureBlend == 0f && !_controller.SuspensionActive,
                $"forced exit ({(_exitVariant == 0 ? "override" : "disabled")}) left mode {_controller.Mode} blend {_controller.EnclosureBlend}");
            CompareToBaseline(strip: false, "forced exit");
            Check(!_controller.HideTierActive, "hide tier still active one frame after forced exit");
            _controller.Enabled = true;
            _controller.ModeOverride = UndergroundModeOverride.ForceSurface;
            SetPhase(Phase.RestoreWait);
            return;
        }
        // Normal exit trigger: the same frame SuspensionActive clears, binary participants must already be resumed
        // (enforced by PerFrameChecks); wait for the blend to ramp out, then expect the full baseline.
        if (_controller!.Mode != UndergroundMode.Surface) return;
        CompareToBaseline(strip: false, "normal exit");
        SetPhase(Phase.RestoreWait);
    }

    private void StepRestoreWait()
    {
        _restoreFrames++;
        if (_restoreFrames < 4) return;
        foreach ((string name, string value) in Snapshot())
            if (HeldLayer(name) && _holdChildCounts.TryGetValue(name, out int held))
                Check(ChildCount(value) * 2 >= held, $"{name} lost over half its children across resume: {held} -> {ChildCount(value)} (state was rebuilt)");
        if (_baselineSunEnergy > .5) Check(_sunLight != null && _sunLight.LightEnergy > _baselineSunEnergy * .5, $"sun energy {_sunLight?.LightEnergy} did not recover (baseline {_baselineSunEnergy})");
        if (_environment?.Environment is { } environment && _groups != UndergroundGroups.None)
            Check(_baselineAmbientEnergy < _controller!.CaveAmbientEnergy + .1 || environment.AmbientLightEnergy > _baselineAmbientEnergy * .7,
                $"ambient energy {environment.AmbientLightEnergy} did not recover (baseline {_baselineAmbientEnergy})");
        Check(_graphics == null || _graphics.LocalSunShadowSuspended == _baselineLocalShadowSuspended, "sun shadow handoff did not return to its baseline");
        Check(_retained.All(id => id == 0 || IsAlive(id)), "an instance was freed by a round trip");
        Check(HandleSignature() == _baselineHandles, $"GPU/texture handles changed: '{_baselineHandles}' -> '{HandleSignature()}'");
        _cycle++;
        if (_cycle % 5 == 0) GD.Print("UNDERGROUND_MODE_ROUNDTRIP cycle=" + _cycle + " children: " + string.Join(" ", Snapshot().Select(p => p.Key + "=" + ChildCount(p.Value))));
        if (_cycle < Cycles) { StartCycle(); return; }
        _controller!.ModeOverride = UndergroundModeOverride.ForceSurface;
        _measureRound = 0;
        StartMeasure(underground: false);
    }

    private void StartMeasure(bool underground)
    {
        _controller!.ModeOverride = underground ? UndergroundModeOverride.ForceUnderground : UndergroundModeOverride.ForceSurface;
        _measureCounted = 0;
        _measureSum = _measureProcessSum = 0;
        _lastTicks = Time.GetTicksUsec();
        SetPhase(underground ? Phase.MeasureUnderground : Phase.MeasureSurface);
    }

    private void StepMeasure()
    {
        ulong now = Time.GetTicksUsec();
        double ms = (now - _lastTicks) / 1000.0;
        _lastTicks = now;
        if (_phaseFrames <= MeasureSettleFrames) return;
        _measureSum += ms;
        _measureProcessSum += (Performance.GetMonitor(Performance.Monitor.TimeProcess) + Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess)) * 1000.0;
        if (++_measureCounted < MeasureFrames) return;
        (_phase == Phase.MeasureUnderground ? _undergroundMs : _surfaceMs).Add(_measureSum / _measureCounted);
        (_phase == Phase.MeasureUnderground ? _undergroundProcessMs : _surfaceProcessMs).Add(_measureProcessSum / _measureCounted);
        if (_phase == Phase.MeasureSurface) { StartMeasure(underground: true); return; }
        if (++_measureRound < MeasureRounds) { StartMeasure(underground: false); return; }
        Finish();
    }

    private void Finish()
    {
        _controller!.ModeOverride = UndergroundModeOverride.Auto;
        _phase = Phase.Done;
        if (_failed) return;
        if (_effects != null && (_groups & UndergroundGroups.AudioWeather) != 0) Check(_maxPulseWhileSuspended > 0f, "the lightning preview never became active while suspended, so the flash suppression was not exercised");
        Check(_controller.HideOrderingViolations == 0, $"{_controller.HideOrderingViolations} frames applied a hide before sun/sky were darkened");
        double surface = _surfaceMs.Count > 0 ? _surfaceMs.Average() : 0, underground = _undergroundMs.Count > 0 ? _undergroundMs.Average() : 0;
        double surfaceProcess = _surfaceProcessMs.Count > 0 ? _surfaceProcessMs.Average() : 0, undergroundProcess = _undergroundProcessMs.Count > 0 ? _undergroundProcessMs.Average() : 0;
        GD.Print($"UNDERGROUND_MODE_ROUNDTRIP_CPU process_ms surface={surfaceProcess:0.000} underground={undergroundProcess:0.000} delta={surfaceProcess - undergroundProcess:0.000}");
        GD.Print($"UNDERGROUND_MODE_ROUNDTRIP_CPU surface_frame_ms={surface:0.000} underground_frame_ms={underground:0.000} delta_ms={surface - underground:0.000} surface_rounds=[{string.Join(",", _surfaceMs.Select(v => v.ToString("0.000")))}] underground_rounds=[{string.Join(",", _undergroundMs.Select(v => v.ToString("0.000")))}]");
        GD.Print($"UNDERGROUND_MODE_ROUNDTRIP_SMOKE_TEST=PASS cycles={Cycles} holdFrames={_totalHoldFrames} hideFrames={_hideFrames} violations={_controller.HideOrderingViolations}");
        GetTree().Quit(0);
    }

    // Invariants that must hold on every frame, checked after the controller applied the new state.
    private bool PerFrameChecks()
    {
        if (_controller == null) return true;
        bool suspended = _controller.SuspensionActive;
        _suspendedStreak = suspended ? _suspendedStreak + 1 : 0;
        if (suspended && _effects != null) _maxPulseWhileSuspended = Math.Max(_maxPulseWhileSuspended, _effects.CurrentRenderedLightningPulse);
        var current = Snapshot();
        if (!suspended)
        {
            // Resume is instant: the frame suspension clears, every binary participant is back at baseline.
            // Blend-driven fades (flash, ambience, thunder) are legitimately mid-ramp and are stripped.
            foreach ((string name, string value) in current)
                if (StripContinuous(value) != _baselineStripped.GetValueOrDefault(name, value))
                    return Fail($"{name} not at baseline while not suspended (mode {_controller.Mode}): '{StripContinuous(value)}' vs '{_baselineStripped[name]}'");
        }
        else
        {
            foreach (UndergroundParticipant participant in _controller.Participants)
            {
                if (!participant.Present || (participant.Group & _groups) == 0 || !ExpectsSuspension(participant)) continue;
                if (current[participant.Name] == _baseline[participant.Name])
                    return Fail($"{participant.Name} did not change while suspended: '{current[participant.Name]}'");
            }
        }
        bool anyHide = _coordinator?.UndergroundTier == UndergroundProxyTier.Hide || _global?.UndergroundHidden == true || _far?.RenderSuspended == true;
        float darkness = _atmosphere != null ? _atmosphere.Get("underground_lighting_darkness").AsSingle() : 0f;
        if (darkness >= .999f && _firstDarkFrame == 0) _firstDarkFrame = _frame;
        bool dark = darkness >= .999f && (_sunLight == null || _sunLight.LightEnergy <= .01f);
        if (anyHide && !dark) return Fail($"ordering violation: surface proxies hidden while sun/sky not darkened (darkness {darkness}, sun {_sunLight?.LightEnergy})");
        if (_graphics?.UndergroundShadowSuspended == true && !dark) return Fail("ordering violation: shadows suspended before sun/sky darkened");
        if (_hideFrame != 0 && _firstDarkFrame == 0) return Fail("hide applied before any darkened frame was published");
        // The flash light is read from the previous frame, so skip the one frame in which suspension just began.
        if (_suspendedStreak >= 2 && (_groups & UndergroundGroups.AudioWeather) != 0 && _flash != null && GodotObject.IsInstanceValid(_flash) && _flash.LightEnergy != 0f) return Fail($"storm flash energy {_flash.LightEnergy} while underground (cycle {_cycle}, phase {_phase}, holdFrames {_holdFrames}, suppression {_effects?.UndergroundFlashSuppression}, suspended {_effects?.UndergroundSuspended}, blend {_controller.EnclosureBlend})");
        if (_baseline.Count > 0 && !_retained.All(id => id == 0 || IsAlive(id))) return Fail("a retained instance was freed");
        if (suspended && _controller.HideTierActive && _hideFrame == 0) _hideFrame = _frame;
        if (_expectRestoreFrame == _frame && _exitVariant != 2) { /* compared in StepExitWait */ }
        return true;
    }

    private bool ExpectsSuspension(UndergroundParticipant participant)
    {
        // Participants whose observable string only changes in the hide tier need that tier active.
        if (participant.Name is "TerrainProxies" or "GlobalTerrainLod3" or "ScaledFarViewport") return _controller!.HideTierActive || participant.Name == "TerrainProxies";
        if (participant.Name == "SunShadows") return _controller!.LightingDarkened;
        return true;
    }

    private Dictionary<string, string> Snapshot()
    {
        var result = new Dictionary<string, string>();
        foreach (UndergroundParticipant participant in _controller!.Participants)
            if (participant.Present) result[participant.Name] = participant.Describe();
        return result;
    }

    private void CompareToBaseline(bool strip, string reason)
    {
        var current = Snapshot();
        foreach ((string name, string value) in current)
        {
            string expected = StripChildren(_baseline.GetValueOrDefault(name, value));
            string actual = StripChildren(value);
            if (strip) { expected = StripContinuous(expected); actual = StripContinuous(actual); }
            Check(actual == expected, $"{reason}: {name} '{actual}' != baseline '{expected}'");
        }
    }

    private bool HeldLayer(string name) => name switch
    {
        "Grass" or "Trees" => (_groups & UndergroundGroups.Vegetation) != 0,
        "GlobalTerrainLod3" => (_groups & UndergroundGroups.FarTerrain) != 0,
        _ => false
    };

    private static int ChildCount(string describe)
    {
        foreach (string token in describe.Split(' '))
            if (token.StartsWith("children=") && int.TryParse(token["children=".Length..], out int count)) return count;
        return 0;
    }

    private static string StripChildren(string value) => string.Join(' ', value.Split(' ').Where(t => !t.StartsWith("children=") && !t.StartsWith("segments=")));

    private static string StripContinuous(string value)
    {
        var tokens = value.Split(' ').Where(t => !t.StartsWith("children=") && !t.StartsWith("segments=") && !t.StartsWith("flashSuppression=") && !t.StartsWith("blend=") && !t.StartsWith("thunderMuffled=") && !t.StartsWith("ownsLocal=") && !t.StartsWith("runtimeVisible="));
        return string.Join(' ', tokens);
    }

    private ulong[] RetainedIds() => _controller!.Participants.Where(p => p.Present).SelectMany(p => p.RetainedInstanceIds()).Where(id => id != 0).Distinct().ToArray();

    private static bool IsAlive(ulong id) => GodotObject.InstanceFromId(id) is { } instance && GodotObject.IsInstanceValid(instance);

    // GPU-backed handles that suspension must never recreate: sunshine shadow textures and far viewport textures.
    private string HandleSignature()
    {
        var parts = new List<string>();
        Node? driver = _root?.GetNodeOrNull("SunshineCloudsDriver");
        if (driver?.Get("clouds_resource").AsGodotObject() is Resource clouds && GodotObject.IsInstanceValid(clouds))
        {
            parts.Add($"clouds={clouds.GetInstanceId()}");
            for (int cascade = 0; cascade < 2; cascade++)
            {
                if (!clouds.HasMethod("get_gpu_cloud_shadow_texture")) break;
                if (clouds.Call("get_gpu_cloud_shadow_texture", cascade).AsGodotObject() is Texture2D texture && GodotObject.IsInstanceValid(texture))
                    parts.Add($"shadow{cascade}={texture.GetInstanceId()}:{texture.GetRid().Id}");
            }
        }
        if (_far?.OuterViewportForDiagnostics is { } outer) parts.Add($"farOuter={outer.GetInstanceId()}:{outer.GetTexture().GetRid().Id}");
        if (_far?.RegionalViewportForDiagnostics is { } regional) parts.Add($"farRegional={regional.GetInstanceId()}:{regional.GetTexture().GetRid().Id}");
        return string.Join(",", parts);
    }

    private void SetPhase(Phase next) { _phase = next; _phaseFrames = 0; }

    private void Check(bool condition, string message) { if (!condition) Fail(message); }

    private bool Fail(string message)
    {
        if (_phase == Phase.Done && _failed) return false;
        _failed = true;
        _phase = Phase.Done;
        GD.PushError($"UNDERGROUND_MODE_ROUNDTRIP_SMOKE_TEST=FAIL: {message}");
        GetTree().Quit(1);
        return false;
    }

    private bool _failed;
}
