using System;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.UI;

/// <summary>Optional, non-authoritative celestial inspection and time-control panel.</summary>
public partial class CelestialDebugHud : CanvasLayer
{
    [Export] public NodePath ClockPath { get; set; } = new("../RingDayNightCycle");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath EclipsePath { get; set; } = new("../EclipseEvent");
    private RingDayNightCycle? _clock;
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private Label? _readout;
    private CheckButton? _pause;
    private SpinBox? _longitude;
    private Label? _previewStatus;
    private EclipseEventDirector? _eclipse;
    private Label? _eclipseReadout, _eclipseStrip;
    private double _refresh;
    private bool _previewActive;
    private double _previewSimulationSeconds;
    private bool _previewPaused;
    private float _previewYaw;
    private float _previewPitch;
    private Input.MouseModeEnum _mouseModeBeforeOpen = Input.MouseModeEnum.Captured;
    private bool _mouseOwnershipBeforeOpen;

    public override void _Ready()
    {
        Layer = 13;
        ProcessMode = ProcessModeEnum.Always;
        _clock = GetNodeOrNull<RingDayNightCycle>(ClockPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        _eclipse = GetNodeOrNull<EclipseEventDirector>(EclipsePath);
        Build();
        Visible = false;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F9 })
        {
            SetOpen(!Visible);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _refresh += delta;
        if (_refresh < .125d) return;
        _refresh = 0d;
        Refresh();
    }

    private void Build()
    {
        var panel = new PanelContainer { Theme = RingUiStyle.CreateTheme(), MouseFilter = Control.MouseFilterEnum.Stop };
        panel.AddThemeStyleboxOverride("panel", RingUiStyle.GlassPanel(.96f, 8));
        AddChild(panel);
        panel.AnchorLeft = 1; panel.AnchorRight = 1; panel.OffsetLeft = -430; panel.OffsetTop = 18; panel.OffsetRight = -18; panel.OffsetBottom = 700;
        var margin = new MarginContainer(); margin.AddThemeConstantOverride("margin_left", 14); margin.AddThemeConstantOverride("margin_right", 14); margin.AddThemeConstantOverride("margin_top", 12); margin.AddThemeConstantOverride("margin_bottom", 12); panel.AddChild(margin);
        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 7); margin.AddChild(column);
        var title = new Label { Text = "CELESTIAL DEBUG  •  F9 CLOSE" }; title.AddThemeColorOverride("font_color", RingUiStyle.AccentBright); column.AddChild(title);
        _readout = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }; _readout.AddThemeFontSizeOverride("font_size", 12); column.AddChild(_readout);
        _pause = new CheckButton { Text = "Pause celestial clock" }; _pause.Toggled += paused => { if (_clock != null) _clock.Paused = paused; }; column.AddChild(_pause);
        var speed = new HBoxContainer();
        foreach (float value in new[] { 1f, 10f, 100f }) { var b = new Button { Text = $"{value:0}×", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; float captured = value; b.Pressed += () => _clock?.SetSpeedMultiplier(captured); speed.AddChild(b); }
        column.AddChild(speed);
        var jumps = new HBoxContainer();
        var ringDay = new Button { Text = "+ RING DAY", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; ringDay.Pressed += () => Jump(_clock?.FullCycleSeconds ?? 0); jumps.AddChild(ringDay);
        var orbit = new Button { Text = "+ GIANT ORBIT", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; orbit.Pressed += () => Jump(_clock?.GiantOrbitSeconds ?? 0); jumps.AddChild(orbit); column.AddChild(jumps);
        var longitude = new HBoxContainer(); longitude.AddChild(new Label { Text = "Force longitude", CustomMinimumSize = new Vector2(112, 0) });
        _longitude = new SpinBox { MinValue = 0, MaxValue = 359.999, Step = .1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; longitude.AddChild(_longitude);
        var force = new Button { Text = "Force" }; force.Pressed += ForceLongitude; longitude.AddChild(force); column.AddChild(longitude);
        var release = new Button { Text = "Release player benchmark override" }; release.Pressed += () => _explorer?.ClearBenchmarkOverride(); column.AddChild(release);
        var previews = new HBoxContainer();
        var viewGiant = new Button { Text = "View giant", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        viewGiant.Pressed += PreviewGiant;
        previews.AddChild(viewGiant);
        var eclipse = new Button { Text = "Next visible partial eclipse", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        eclipse.Pressed += PreviewPartialEclipse;
        previews.AddChild(eclipse);
        column.AddChild(previews);
        var totalPreviewRow = new HBoxContainer();
        var totalEclipse = new Button { Text = "Next visible total eclipse", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        totalEclipse.Pressed += PreviewTotalEclipse;
        totalPreviewRow.AddChild(totalEclipse);
        column.AddChild(totalPreviewRow);
        var restorePreview = new Button { Text = "Restore preview" };
        restorePreview.Pressed += RestorePreview;
        column.AddChild(restorePreview);
        _previewStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _previewStatus.AddThemeFontSizeOverride("font_size", 12);
        _previewStatus.AddThemeColorOverride("font_color", RingUiStyle.AccentBright);
        column.AddChild(_previewStatus);
        BuildEclipseBlock(column);
        Refresh();
    }


    private void BuildEclipseBlock(VBoxContainer column)
    {
        column.AddChild(new HSeparator());
        var title = new Label { Text = "ECLIPSE EVENT" }; title.AddThemeColorOverride("font_color", RingUiStyle.AccentBright); column.AddChild(title);
        _eclipseReadout = new Label { Name = "EclipseReadout", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _eclipseReadout.AddThemeFontSizeOverride("font_size", 12);
        column.AddChild(_eclipseReadout);
        _eclipseStrip = new Label { Name = "EclipseRingStrip", TooltipText = "Fired sites along the loop; the main spire is at the left." };
        _eclipseStrip.AddThemeFontSizeOverride("font_size", 11);
        column.AddChild(_eclipseStrip);
        var row = new HBoxContainer();
        var previous = new Button { Text = "< Phase", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Jump the celestial clock to the previous phase start." };
        previous.Pressed += () => StepEclipsePhase(-1);
        row.AddChild(previous);
        var force = new Button { Text = "Force eclipse", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Jump the celestial clock to the pre-roll of the current or next eclipse. This changes world time." };
        force.Pressed += () => _eclipse?.ForceEclipseNow();
        row.AddChild(force);
        var next = new Button { Text = "Phase >", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Jump the celestial clock to the next phase start." };
        next.Pressed += () => StepEclipsePhase(1);
        row.AddChild(next);
        column.AddChild(row);
    }

    private void StepEclipsePhase(int direction)
    {
        if (_eclipse == null) return;
        int phase = (int)_eclipse.Phase;
        int last = (int)EclipseEventPhase.Return;
        int target = phase == (int)EclipseEventPhase.Idle ? (direction > 0 ? (int)EclipseEventPhase.PreRoll : last) : phase + direction;
        if (target < (int)EclipseEventPhase.PreRoll) target = (int)EclipseEventPhase.PreRoll;
        if (target > last) target = (int)EclipseEventPhase.Idle;
        _eclipse.JumpToPhase((EclipseEventPhase)target);
    }

    private void RefreshEclipse()
    {
        if (_eclipseReadout == null) return;
        _eclipse ??= GetNodeOrNull<EclipseEventDirector>(EclipsePath);
        _eclipseReadout.Text = _eclipse?.DescribeStatus() ?? "Eclipse event director unavailable in this scene.";
        if (_eclipseStrip != null) _eclipseStrip.Text = _eclipse?.Engine.BuildRingStrip() ?? "";
    }

    private void Jump(double seconds) { if (_clock != null && seconds > 0) _clock.SetSimulationSeconds(_clock.GetSimulationSeconds() + seconds); }

    private void ForceLongitude()
    {
        if (_explorer == null || _grid == null || _longitude == null) return;
        var current = _explorer.CanonicalPosition;
        double around = (_longitude.Value / 360d) * _grid.GetCanonicalRingCircumferenceMeters64();
        // This preserves vertical/across exactly. It intentionally uses the explicit
        // benchmark API, so release remains a separate, visible user action.
        _explorer.SetBenchmarkCanonicalPosition(current with { AroundMeters = around });
    }

    private void PreviewGiant()
    {
        if (_clock == null || _explorer == null) { SetPreviewStatus("Giant preview unavailable: clock or explorer missing."); return; }
        double start = _clock.GetSimulationSeconds();
        double day = Math.Max(1d, _clock.FullCycleSeconds);
        double step = Math.Max(1d, Math.Min(30d, day / 24d));
        CelestialFrame selected = _clock.EvaluateAtTime(start);
        double selectedTime = start;
        // The evaluated radius is authoritative for the effective nested
        // configuration; a disabled/zero-radius giant cannot be previewed.
        if (selected.GiantAngularRadiusDegrees <= 0f)
        {
            SetPreviewStatus("Giant preview unavailable: the active celestial configuration disables the giant.");
            return;
        }
        if (!float.IsFinite(selected.GiantElevationDegrees) || selected.GiantElevationDegrees <= 50f)
        {
            CelestialFrame? highest = float.IsFinite(selected.GiantElevationDegrees)
                && selected.GiantElevationDegrees >= 15f ? selected : null;
            double highestTime = start;
            for (double time = start + step; time <= start + day * 1.5d; time += step)
            {
                CelestialFrame candidate = _clock.EvaluateAtTime(time);
                if (!float.IsFinite(candidate.GiantElevationDegrees) || candidate.GiantElevationDegrees < 15f)
                    continue;
                if (candidate.GiantElevationDegrees >= 50f)
                {
                    selected = candidate;
                    selectedTime = time;
                    highest = null;
                    break;
                }
                if (highest == null || candidate.GiantElevationDegrees > highest.Value.GiantElevationDegrees)
                {
                    highest = candidate;
                    highestTime = time;
                }
            }
            if (highest != null)
            {
                selected = highest.Value;
                selectedTime = highestTime;
            }
            else if (!float.IsFinite(selected.GiantElevationDegrees) || selected.GiantElevationDegrees < 50f)
            {
                SetPreviewStatus("No giant above 15 degrees was found within the next 1.5 ring days.");
                return;
            }
        }
        ApplyPreview(selectedTime, selected.ToGiant);
        SetPreviewStatus($"Viewing naturally evaluated giant at +{FormatDuration(selectedTime - start)}.");
    }

    private void PreviewPartialEclipse()
    {
        if (_clock == null || _explorer == null) { SetPreviewStatus("Eclipse preview unavailable: clock or explorer missing."); return; }
        if (!_clock.GiantEnabled) { SetPreviewStatus("Eclipse preview unavailable: the active celestial configuration disables the giant."); return; }
        double start = _clock.GetSimulationSeconds();
        double day = Math.Max(1d, _clock.FullCycleSeconds);
        double orbit = Math.Max(day, _clock.GiantOrbitSeconds);
        // At the enlarged giant's grazing ingress/egress, a 60-second probe can
        // jump straight over the requested partial range. Fine sampling begins
        // only after the bounded conjunction bracket has been found.
        double fineStep = 1d;
        // Find conjunction neighbourhoods first, then sample just that bounded
        // window at a ring-day-relative cadence. This avoids evaluating every
        // minute of eight full giant orbits when no eclipse is geometrically near.
        // A short orbit can put a narrow conjunction entirely between old
        // half-day probes.  Resolve each orbit at least 720 times instead.
        double coarseStep = Math.Min(day * .5d, orbit / 720d);
        double searchEnd = start + orbit * 8d;
        CelestialFrame? fallback = null;
        double fallbackTime = 0d;
        double scannedThrough = double.NegativeInfinity;
        for (double coarseTime = start + coarseStep; coarseTime <= searchEnd; coarseTime += coarseStep)
        {
            CelestialFrame coarse = _clock.EvaluateAtTime(coarseTime);
            // The extra degree conservatively covers observer parallax and the
            // coarse probe offset; the actual acceptance still uses raw coverage.
            double conjunctionDegrees = coarse.GiantAngularRadiusDegrees
                + _clock.StarAngularRadiusDegrees + 1d;
            if (coarse.StarGiantAngularSeparationDegrees > conjunctionDegrees)
                continue;
            double halfWindow = conjunctionDegrees / 360d * orbit * 1.5d;
            double windowStart = Math.Max(start + fineStep, coarseTime - halfWindow);
            double windowEnd = Math.Min(searchEnd, coarseTime + halfWindow);
            // Adjacent coarse probes describe the same broad conjunction. Scan
            // each second only once, otherwise the large totality window would
            // repeatedly re-evaluate thousands of identical candidate times.
            if (windowEnd <= scannedThrough)
                continue;
            windowStart = Math.Max(windowStart, scannedThrough + fineStep);
            for (double time = windowStart; time <= windowEnd; time += fineStep)
            {
                CelestialFrame candidate = _clock.EvaluateAtTime(time);
                if (candidate.SunElevationDegrees < 15f || candidate.RawEclipseCoverage <= .001f || candidate.RawEclipseCoverage >= .999f)
                    continue;
                if (candidate.RawEclipseCoverage >= .50f && candidate.RawEclipseCoverage <= .80f)
                {
                    ApplyPreview(time, candidate.ToSun);
                    SetPreviewStatus($"Viewing natural partial eclipse ({candidate.RawEclipseCoverage:P0}) at +{FormatDuration(time - start)}.");
                    return;
                }
                if (fallback == null || candidate.RawEclipseCoverage > fallback.Value.RawEclipseCoverage)
                {
                    fallback = candidate;
                    fallbackTime = time;
                }
            }
            scannedThrough = windowEnd;
        }
        if (fallback != null)
        {
            ApplyPreview(fallbackTime, fallback.Value.ToSun);
            SetPreviewStatus($"No 50-80% event found; viewing natural partial ({fallback.Value.RawEclipseCoverage:P0}) at +{FormatDuration(fallbackTime - start)}.");
            return;
        }
        SetPreviewStatus("No visible partial eclipse above 15 degrees was found in the next 8 giant orbits.");
    }

    private void PreviewTotalEclipse()
    {
        if (_clock == null || _explorer == null) { SetPreviewStatus("Total eclipse preview unavailable: clock or explorer missing."); return; }
        if (!_clock.GiantEnabled) { SetPreviewStatus("Total eclipse preview unavailable: the active celestial configuration disables the giant."); return; }

        double start = _clock.GetSimulationSeconds();
        double day = Math.Max(1d, _clock.FullCycleSeconds);
        double orbit = Math.Max(day, _clock.GiantOrbitSeconds);
        // A totality lasts thousands of seconds in the authored physical preset,
        // so this bounded per-orbit scan is substantially finer than its window
        // without mutating the clock or inventing eclipse coverage.
        double step = Math.Min(day * .5d, orbit / 720d);
        double searchEnd = start + orbit * 8d;
        for (double time = start; time <= searchEnd; time += step)
        {
            CelestialFrame candidate = _clock.EvaluateAtTime(time);
            if (candidate.SunElevationDegrees < 15f || candidate.RawEclipseCoverage < .999999f)
                continue;
            ApplyPreview(time, candidate.ToSun);
            SetPreviewStatus($"Viewing natural total eclipse at +{FormatDuration(time - start)}.");
            return;
        }
        SetPreviewStatus("No visible total eclipse above 15 degrees was found in the next 8 giant orbits.");
    }

    private void ApplyPreview(double time, Vector3 direction)
    {
        if (_clock == null || _explorer == null) return;
        if (!_previewActive)
        {
            _previewActive = true;
            _previewSimulationSeconds = _clock.GetSimulationSeconds();
            _previewPaused = _clock.Paused;
            _previewYaw = Mathf.RadToDeg(_explorer.SavedYaw);
            _previewPitch = _explorer.SavedPitch;
        }
        _clock.SetSimulationSeconds(time);
        _clock.Paused = true;
        AimPresentationView(direction);
    }

    private void RestorePreview()
    {
        if (!_previewActive || _clock == null || _explorer == null) { SetPreviewStatus("No celestial preview is active."); return; }
        _clock.SetSimulationSeconds(_previewSimulationSeconds);
        _clock.Paused = _previewPaused;
        _explorer.SetPresentationView(_previewYaw, _previewPitch);
        _previewActive = false;
        SetPreviewStatus("Restored clock, pause state, and presentation view.");
    }

    private void AimPresentationView(Vector3 worldDirection)
    {
        if (_explorer == null || worldDirection.LengthSquared() <= .000001f) return;
        Vector3 localDirection = (_explorer.GlobalBasis.Inverse() * worldDirection).Normalized();
        float yawDegrees = Mathf.RadToDeg(Mathf.Atan2(-localDirection.X, -localDirection.Z));
        float pitchDegrees = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(localDirection.Y, -1f, 1f)));
        _explorer.SetPresentationView(yawDegrees, pitchDegrees);
    }

    private void SetPreviewStatus(string text)
    {
        if (_previewStatus != null) _previewStatus.Text = text;
    }

    private void SetOpen(bool open)
    {
        if (open)
        {
            _mouseModeBeforeOpen = Input.MouseMode;
            _mouseOwnershipBeforeOpen = GetTree().Root.GetMeta(
                RingLiveTuningDrawer.MouseOwnershipMeta, false).AsBool();
            GetTree().Root.SetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, true);
            Visible = true;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            return;
        }
        Visible = false;
        GetTree().Root.SetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, _mouseOwnershipBeforeOpen);
        // A pause menu that opened while this HUD was visible owns the visible
        // cursor. Restoring the prior mode only when unpaused avoids stealing it.
        if (!GetTree().Paused)
            Input.MouseMode = _mouseModeBeforeOpen;
    }

    private void Refresh()
    {
        if (_readout == null) return;
        RefreshEclipse();
        if (_clock == null) { _readout.Text = "Celestial clock unavailable."; return; }
        if (_pause != null && _pause.ButtonPressed != _clock.Paused) _pause.ButtonPressed = _clock.Paused;
        CelestialFrame f = _clock.CurrentFrame;
        _readout.Text = $"SIM {f.AbsoluteSeconds:0.0}s  •  ring {f.RingRotationAngleDegrees:0.00}°  •  orbit {f.OrbitalAngleDegrees:0.00}°  •  season {f.SeasonAngleDegrees:0.00}°\n"
            + $"sun {f.SunElevationDegrees:0.00}°  •  giant {f.GiantElevationDegrees:0.00}°  •  giant Ø {f.GiantAngularDiameterDegrees:0.000}°  •  separation {f.StarGiantAngularSeparationDegrees:0.000}°\n"
            + $"day/twilight/night {f.DaylightFactor:0.00}/{f.TwilightFactor:0.00}/{f.NightFactor:0.00}  •  {f.LightingPhase}  •  giant light {f.GiantLightingPhase:0.00}\n"
            + $"eclipse {f.EclipseType}: raw {f.RawEclipseCoverage:0.000}  •  gameplay {f.GameplayEclipseDarkness:0.000}\n"
            + $"sunrise {FormatDuration(f.SecondsUntilSunrise)}  •  sunset {FormatDuration(f.SecondsUntilSunset)}\n"
            + $"giant rise {FormatDuration(f.SecondsUntilGiantRise)}  •  set {FormatDuration(f.SecondsUntilGiantSet)}  •  seed {_clock.DeterministicVisualSeed}";
    }

    private static string FormatDuration(double seconds)
    {
        if (!double.IsFinite(seconds)) return "never";
        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0d, seconds));
        return span.TotalHours >= 1d
            ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }
}
