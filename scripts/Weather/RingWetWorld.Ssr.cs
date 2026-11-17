using System;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
namespace RingworldTechDemo.Weather;

/// <summary>
/// Perf option G2 (docs/perf-pass-2026-11.md section 0.14): the main viewport's engine SSR is traced only while the wet world is valid
/// (<see cref="RingWetWorldSettings.SsrOnlyWhileWet"/>, default on). Godot has no SSR intensity, so the blend raises both SSR fade exponents
/// by -Gain * ln(weight), which fades every reflection continuously toward zero; at the floor ssr_enabled goes off, at weight 1 the baseline returns exactly.
/// With the setting off and no override nothing here touches the Environment.
/// </summary>
public partial class RingWetWorld
{
    public const float SsrFadeSeconds = 3.0f, SsrWeightFloor = 0.01f, SsrFadeExponentGain = 0.72f;
    private const float SsrWeightOne = 0.9999f;
    /// <summary>Gate: SSR stays on while surfaces near the camera are visibly wet (hysteresis 0.05 on, 0.02 for 10 s off) or glossy POI architecture is loaded nearby.</summary>
    public const float SsrGateIntervalSeconds = 0.5f, SsrGateRingSeconds = 1.0f, SsrWetOnThreshold = 0.05f, SsrWetOffThreshold = 0.02f, SsrDryHoldSeconds = 10.0f;
    private static readonly float[] SsrGateRadii = { 1000f, 3000f, 5500f };
    [Export] public NodePath EmitterSitesPath { get; set; } = new("../EmitterSites");
    [Export] public NodePath AtmosphereProjectorsPath { get; set; } = new("../AtmosphereProjectors");
    [Export] public NodePath WeatherLungPath { get; set; } = new("../WeatherLung");
    /// <summary>Diagnostics for the bench: the last wet signal, the hysteresis state and the POI keep.</summary>
    public float SsrGateWetSignal { get; private set; }
    public bool SsrGateWet { get; private set; }
    public bool SsrGatePoiNear { get; private set; }
    private float _ssrDryHeld, _ssrRingSignal;
    private ulong _ssrGateTicks, _ssrRingTicks;
    private EmitterPoiStreamer? _ssrEmitters;
    private AtmosphereProjectorStreamer? _ssrProjectors;
    private WeatherLungStreamer? _ssrLung;

    /// <summary>Environment of the main viewport (the WorldEnvironment node's); falls back to the viewport's World3D environment.</summary>
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");
    /// <summary>Current blend weight: 1 = baseline SSR, 0 = off.</summary>
    public float SsrPolicyWeight { get; private set; } = 1f;
    /// <summary>Tests and captures: >= 0 sets the target (clamped 0..1) and engages the policy even when the setting is off.</summary>
    public float SsrPolicyTargetOverride { get; set; } = -1f;
    /// <summary>Tests and captures: >= 0 pins the weight (clamped 0..1), engaged, no animation.</summary>
    public float SsrPolicyWeightOverride { get; set; } = -1f;
    /// <summary>Times this policy flipped ssr_enabled.</summary>
    public int SsrToggleCount { get; private set; }
    /// <summary>True while the policy owns the SSR parameters (it has modified the environment and not yet restored the baseline).</summary>
    public bool SsrPolicyEngaged => _ssrModified;

    private Godot.Environment? _ssrEnvironment;
    private bool _ssrModified, _ssrBaseEnabled, _ssrWrittenEnabled;
    private float _ssrBaseIn, _ssrBaseOut, _ssrWrittenIn, _ssrWrittenOut;
    private ulong _ssrLastTicks;

    public static float SsrFadeExtraExponent(float weight)
    {
        if (!float.IsFinite(weight) || weight >= 1f) return 0f;
        return -SsrFadeExponentGain * MathF.Log(MathF.Max(weight, SsrWeightFloor));
    }

    public static float StepSsrWeight(float weight, float target, float deltaSeconds, float fadeSeconds)
    {
        weight = float.IsFinite(weight) ? Math.Clamp(weight, 0f, 1f) : 1f;
        target = float.IsFinite(target) ? Math.Clamp(target, 0f, 1f) : 1f;
        float step = !float.IsFinite(deltaSeconds) || deltaSeconds <= 0f ? 0f : fadeSeconds > 0f && float.IsFinite(fadeSeconds) ? deltaSeconds / fadeSeconds : 1f;
        if (weight < target) return MathF.Min(weight + step, target);
        return MathF.Max(weight - step, target);
    }

    /// <summary>Hysteresis: on at signal >= <see cref="SsrWetOnThreshold"/>; off only after the signal stayed below <see cref="SsrWetOffThreshold"/> for <see cref="SsrDryHoldSeconds"/>. NaN counts as 0.</summary>
    public static bool StepSsrWetGate(bool wasWet, float signal, float dryHeldSeconds, out float newDryHeld, float dt)
    {
        if (!float.IsFinite(signal)) signal = 0f;
        if (!float.IsFinite(dryHeldSeconds) || dryHeldSeconds < 0f) dryHeldSeconds = 0f;
        if (!float.IsFinite(dt) || dt < 0f) dt = 0f;
        if (!wasWet) { newDryHeld = 0f; return signal >= SsrWetOnThreshold; }
        if (signal >= SsrWetOffThreshold) { newDryHeld = 0f; return true; }
        newDryHeld = dryHeldSeconds + dt;
        return newDryHeld < SsrDryHoldSeconds;
    }

    private void UpdateSsrGate(ulong now)
    {
        float elapsed = _ssrGateTicks == 0 || now < _ssrGateTicks ? SsrGateIntervalSeconds : (now - _ssrGateTicks) / 1e6f;
        if (_ssrGateTicks != 0 && elapsed < SsrGateIntervalSeconds) return;
        _ssrGateTicks = now;
        float signal = 0f;
        if (Valid)
        {
            if (_forced) signal = MathF.Max(MathF.Max(_forceValue.X, _forceValue.Y), MathF.Max(_forceValue.Z, _forceValue.W));
            else
            {
                signal = MaxSsrSignal(CameraWetness);
                if (_weather != null && _weather.TryGetRainHistorySnapshot(out RainHistorySnapshot snapshot))
                {
                    if (_ssrRingTicks == 0 || now < _ssrRingTicks || (now - _ssrRingTicks) / 1e6f >= SsrGateRingSeconds)
                    {
                        _ssrRingTicks = now;
                        StormArrivalTuning tuning = RingStormArrivalSettings.Current.ToTuning(RingVolumetricCloudLayer.StormArrivalPrecipitationStart, _weather.Coverage);
                        float dry = RingWetWorldSettings.Current.DryingSpeed; bool snowing = _effects?.Snowing ?? false;
                        float ring = 0f;
                        foreach (float radius in SsrGateRadii)
                            for (int d = 0; d < 8; d++)
                            {
                                float angle = d * Mathf.Tau / 8f;
                                var delta = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                                ring = MathF.Max(ring, MaxSsrSignal(WetnessModel.EvaluateAt(snapshot, delta, tuning, 0f, CameraSunVisibility, WindMetersPerSecond, dry, snowing)));
                            }
                        _ssrRingSignal = ring;
                    }
                    signal = MathF.Max(signal, _ssrRingSignal);
                }
            }
        }
        SsrGateWetSignal = float.IsFinite(signal) ? signal : 0f;
        SsrGateWet = StepSsrWetGate(SsrGateWet, SsrGateWetSignal, _ssrDryHeld, out _ssrDryHeld, Math.Clamp(elapsed, 0f, 2f));
        SsrGatePoiNear = PoiNear();
    }

    private static float MaxSsrSignal(in WetnessState s) => MathF.Max(MathF.Max(s.Film, s.Soak), MathF.Max(s.Standing, s.RainNowTerm));

    private bool PoiNear()
    {
        _ssrEmitters ??= GetNodeOrNull<EmitterPoiStreamer>(EmitterSitesPath);
        _ssrProjectors ??= GetNodeOrNull<AtmosphereProjectorStreamer>(AtmosphereProjectorsPath);
        _ssrLung ??= GetNodeOrNull<WeatherLungStreamer>(WeatherLungPath);
        return (_ssrEmitters?.Core?.ActiveCount ?? 0) > 0 || (_ssrProjectors?.Core?.ActiveCount ?? 0) > 0
            || _ssrLung?.DetailState is WeatherLungDetailState.Loading or WeatherLungDetailState.Attaching or WeatherLungDetailState.Attached;
    }

    private Godot.Environment? ResolveSsrEnvironment()
    {
        if (_ssrEnvironment != null && GodotObject.IsInstanceValid(_ssrEnvironment)) return _ssrEnvironment;
        _ssrEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath)?.Environment ?? GetViewport()?.World3D?.Environment;
        return _ssrEnvironment;
    }

    private void UpdateSsrPolicy()
    {
        ulong now = Time.GetTicksUsec();
        float dt = _ssrLastTicks == 0 || now < _ssrLastTicks ? 0f : Math.Clamp((now - _ssrLastTicks) / 1e6f, 0f, 0.1f);
        _ssrLastTicks = now;
        bool engagedRequest = RingWetWorldSettings.Current.SsrOnlyWhileWet || SsrPolicyTargetOverride >= 0f || SsrPolicyWeightOverride >= 0f;
        if (!engagedRequest && !_ssrModified && SsrPolicyWeight >= 1f) return;
        if (RingWetWorldSettings.Current.SsrOnlyWhileWet) UpdateSsrGate(now);
        float target = !engagedRequest ? 1f : SsrPolicyTargetOverride >= 0f ? Math.Clamp(SsrPolicyTargetOverride, 0f, 1f) : SsrGateWet || SsrGatePoiNear ? 1f : 0f;
        Godot.Environment? env = ResolveSsrEnvironment();
        if (env == null) return;
        if (!_ssrModified)
        {
            _ssrBaseEnabled = env.SsrEnabled; _ssrBaseIn = env.SsrFadeIn; _ssrBaseOut = env.SsrFadeOut;
            if (!_ssrBaseEnabled) return; // someone else disabled SSR; never enable it
            _ssrWrittenEnabled = _ssrBaseEnabled; _ssrWrittenIn = _ssrBaseIn; _ssrWrittenOut = _ssrBaseOut;
        }
        float weight = SsrPolicyWeightOverride >= 0f ? Math.Clamp(SsrPolicyWeightOverride, 0f, 1f) : StepSsrWeight(SsrPolicyWeight, target, dt, SsrFadeSeconds);
        if (weight >= SsrWeightOne) weight = 1f;
        SsrPolicyWeight = weight;
        if (weight >= 1f)
        {
            if (_ssrModified) { WriteSsr(env, _ssrBaseEnabled, _ssrBaseIn, _ssrBaseOut); _ssrModified = false; }
            return;
        }
        _ssrModified = true;
        float extra = SsrFadeExtraExponent(weight);
        bool enabled = weight > SsrWeightFloor;
        WriteSsr(env, enabled, _ssrBaseIn + extra, _ssrBaseOut + extra);
    }

    private void RestoreSsrPolicy()
    {
        if (!_ssrModified) { SsrPolicyWeight = 1f; return; }
        Godot.Environment? env = ResolveSsrEnvironment();
        if (env != null) WriteSsr(env, _ssrBaseEnabled, _ssrBaseIn, _ssrBaseOut);
        _ssrModified = false; SsrPolicyWeight = 1f;
    }

    /// <summary>Writes only the values that changed since the last write; fade values go first so the enable flip never shows an unfaded frame.</summary>
    private void WriteSsr(Godot.Environment env, bool enabled, float fadeIn, float fadeOut)
    {
        if (fadeIn != _ssrWrittenIn) { env.SsrFadeIn = fadeIn; _ssrWrittenIn = fadeIn; }
        if (fadeOut != _ssrWrittenOut) { env.SsrFadeOut = fadeOut; _ssrWrittenOut = fadeOut; }
        if (enabled != _ssrWrittenEnabled) { env.SsrEnabled = enabled; _ssrWrittenEnabled = enabled; SsrToggleCount++; }
    }
}
