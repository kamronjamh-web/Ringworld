using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo;

/// <summary>
/// Camera-independent Sunshine clouds (docs/cloud-camera-independence-plan.md, P2). With <see cref="CameraIndependentClouds"/> on, the
/// weather refresh uploads the nearest render weather systems (std140 block bound at 21 in the main compute, 8 in the shadow compute)
/// and writes constants instead of the camera's quantized weather into the sky-wide uniforms. With it off nothing here runs except one
/// "disabled" upload after a switch-off, and every legacy uniform path is untouched.
/// </summary>
public partial class RingSunshineCloudBridge
{
	/// <summary>Systems the shader block holds.</summary>
	public const int MaximumWeatherSystemUploadCount = 64;
	/// <summary>Header (4 x vec4) plus 64 systems (4 x vec4 each); must equal WEATHER_SYSTEMS_BYTES in SunshineClouds.gd.</summary>
	public const int WeatherSystemsBlockBytes = 64 + MaximumWeatherSystemUploadCount * 64;
	private static readonly StringName SetWeatherSystemsMethod = new("set_weather_systems");
	private static readonly StringName SetMacroWeatherMapMethod = new("set_macro_weather_map");
	private static readonly StringName SetCloudLayersMethod = new("set_cloud_layers");
	private static readonly StringName SetStormProfileMapMethod = new("set_storm_profile_map");
	private static readonly StringName SetMegastormStripMethod = new("set_megastorm_strip");
	private static readonly StringName SetMegastormFrameMethod = new("set_megastorm_frame");
	private static readonly StringName SetMegastormPrewarmMethod = new("set_megastorm_prewarm");
	/// <summary>Largest half extent of the baked storm profile map (m): 1024 texels over 2 x this is a 293 m texel, fine enough for mammatus pouches. Storms beyond it fall back to background cloud only.</summary>
	private const double StormMapMaxHalfExtentMeters = 150000.0;
	// Smoothing time of the camera-local ambient factors (variant (a) of the plan's ambient A/B).
	private const float CameraIndependentAmbientSmoothingSeconds = 1.0f;

	[ExportGroup("Camera-independent clouds")]
	/// <summary>Shape every cloud from world-anchored weather systems instead of the camera's quantized weather. Off = legacy path.</summary>
	[Export] public bool CameraIndependentClouds { get; set; } = true;
	/// <summary>Upload cap: Low 40, Medium/High 48, VeryHigh and above 64. Distant systems beyond it fade smoothly.</summary>
	[Export(PropertyHint.Range, "16,64,1")] public int WeatherSystemUploadCount { get; set; } = 48;
	/// <summary>Added to handoff end + light reach when selecting systems, so a system's entry is never visible.</summary>
	[Export(PropertyHint.Range, "0,50000,500")] public float WeatherSystemSelectionMarginMeters { get; set; } = 8000.0f;
	/// <summary>Over the cap, systems within this distance of the (N+1)-th system's edge fade to zero weight.</summary>
	[Export(PropertyHint.Range, "1000,100000,500")] public float OverflowFadeMeters { get; set; } = 15000.0f;
	/// <summary>Added to the weather layer's base Coverage for the shader's per-sample coverage.</summary>
	[Export(PropertyHint.Range, "-0.5,0.5,0.01")] public float SunshineBaseCoverageOffset { get; set; } = 0.0f;
	/// <summary>Top of the constant ray envelope (the floor is the lowest storm base, 500 m by default).</summary>
	[Export(PropertyHint.Range, "5000,18000,500")] public float StormEnvelopeCeilingMeters { get; set; } = RingCloudLayers.CeilingMeters;

	/// <summary>Perf ablation bits (0 = normal): 1 background band only (no per-ray sculpt intervals, no sub-mask band widening), 2 clear sculpt mask, 4 skip ray mask, 8 constant macro, 16 light march breaks on the background band instead of the sub-mask band, 32 legacy shader path, 64 single legacy background band (no S6 cloud layers), 128 no cirrus shell (High layer), 256 S6 deck colour off (layer-deck ambient keeps the sky chroma; A/B only), 512 megastorm strip fetch off (the strip is never enabled, so the wall body does not draw; A/B only). Re-uploaded every weather refresh.</summary>
	[Export(PropertyHint.Range, "0,1023,1")] public int CameraIndependentDebugMask { get; set; } = 0;

	[ExportSubgroup("Macro weather map")]
	/// <summary>Build the world-anchored macro weather map (frame-sliced compute) and read it with one fetch per cloud sample. Off = every sample loops over its ray's systems.</summary>
	[Export] public bool MacroWeatherMap { get; set; } = true;
	/// <summary>Map texel size in metres (smaller = finer, more build work). Raised automatically if the region would exceed the 1024 x 512 texture.</summary>
	[Export(PropertyHint.Range, "250,4000,50")] public float MacroMapResolution { get; set; } = 1000.0f;
	/// <summary>Render callbacks one map build is spread over (one slice per callback); all slices read one snapshot.</summary>
	[Export(PropertyHint.Range, "1,32,1")] public int MacroMapSlices { get; set; } = 8;
	/// <summary>Minimum time between map build starts; a build always finishes before the next starts, so slow frames run builds back to back.</summary>
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float MacroMapRefreshSeconds { get; set; } = 0.25f;

	/// <summary>Completed map builds (front/back swaps) since the renderer started.</summary>
	public long MacroMapBuildsCompleted => _clouds != null && GodotObject.IsInstanceValid(_clouds) ? (long)_clouds.Get("macro_map_builds_completed").AsInt64() : 0;
	/// <summary>Map slices dispatched since the renderer started (build GPU time is the GPU.CloudMacroMap timestamp scope).</summary>
	public long MacroMapSlicesDispatched => _clouds != null && GodotObject.IsInstanceValid(_clouds) ? (long)_clouds.Get("macro_map_slices_dispatched").AsInt64() : 0;
	/// <summary>True while the shaders read the front map (false: direct per-system evaluation).</summary>
	public bool MacroMapActive => _clouds != null && GodotObject.IsInstanceValid(_clouds) && _clouds.Get("macro_map_valid").AsBool();

	/// <summary>The Sunshine clouds resource, for test readback only (macro map texture and state, uploaded blocks).</summary>
	public GodotObject? GetSunshineCloudsForTests()
		=> _clouds != null && GodotObject.IsInstanceValid(_clouds) ? _clouds : null;

	/// <summary>The Sunshine storm profile map driver (RingStormProfileMap.gd: valid, texture, front_* state), for test readback only.</summary>
	public GodotObject? GetStormProfileMapDriver()
		=> _clouds != null && GodotObject.IsInstanceValid(_clouds) ? _clouds.Get("storm_profile_map").AsGodotObject() : null;

	/// <summary>The Sunshine megastorm strip driver (RingMegastormStrip.gd: valid, texture, builds_completed, front_config), for test readback only.</summary>
	public GodotObject? GetMegastormStripDriver()
		=> _clouds != null && GodotObject.IsInstanceValid(_clouds) ? _clouds.Get("megastorm_strip").AsGodotObject() : null;

	/// <summary>Debug ablation (harness only): overrides Sunshine's atmospheric density (its own haze on clouds).</summary>
	public void DebugSetCloudAtmosphericDensity(float density)
	{
		if (_clouds != null && GodotObject.IsInstanceValid(_clouds)) _clouds.Set("atmospheric_density", density);
	}

	/// <summary>The per-ray storm ambient uniform as Sunshine holds it (rgb colour, w strength), for test readback only.</summary>
	public Vector4 GetStormAmbientUniform()
		=> _clouds != null && GodotObject.IsInstanceValid(_clouds) ? _clouds.Get("ring_storm_ambient").AsVector4() : Vector4.Zero;

	/// <summary>Storm floor / ceiling (canonical metres) of the first uploaded weather system, from the block Sunshine holds (test readback only).</summary>
	public Vector2 GetFirstWeatherSystemBand()
	{
		if (_clouds == null || !GodotObject.IsInstanceValid(_clouds)) return Vector2.Zero;
		byte[] bytes = _clouds.Get("weather_systems_bytes").AsByteArray();
		if (bytes.Length < 64 + 64) return Vector2.Zero;
		return new Vector2(BitConverter.ToSingle(bytes, 64 + 32), BitConverter.ToSingle(bytes, 64 + 36));
	}

	/// <summary>Systems currently uploaded (0 while the flag is off).</summary>
	public int UploadedWeatherSystemCount { get; private set; }
	/// <summary>Candidates the last refresh found inside the selection radius, before the cap.</summary>
	public int WeatherSystemCandidateCount { get; private set; }
	/// <summary>Highest candidate count seen since the flag was switched on.</summary>
	public int WeatherSystemCandidatePeak { get; private set; }
	/// <summary>Refreshes whose candidate count exceeded the cap (the overflow fade was active).</summary>
	public long WeatherSystemOverflowCount { get; private set; }
	/// <summary>Bumped only by structural changes (manual system added/removed, cap changed); feeds the density-continuity terms.</summary>
	public int WeatherSystemsRevision { get; private set; }
	/// <summary>Constant ray envelope the shader clips against (floor, ceiling) in metres.</summary>
	public Vector2 CameraIndependentEnvelopeMeters => new(_ciEnvelopeFloorMeters, _ciEnvelopeCeilingMeters);
	/// <summary>Camera-local smoothed storm level that drives the ambient colour (flag on only).</summary>
	public float CameraIndependentAmbientStorm => _localStormFactor;

	private readonly record struct UploadedWeatherSystem(RingVolumetricCloudLayer.RenderWeatherSystem System, float Fade, double EdgeDistanceMeters);

	private readonly List<RingVolumetricCloudLayer.RenderWeatherSystem> _ciCollected = new(96);
	private readonly List<UploadedWeatherSystem> _ciManual = new(8);
	private readonly List<UploadedWeatherSystem> _ciNatural = new(96);
	private readonly List<UploadedWeatherSystem> _ciUploaded = new(MaximumWeatherSystemUploadCount);
	private readonly byte[] _ciBlock = new byte[WeatherSystemsBlockBytes];
	private readonly byte[] _ciLayersBlock = new byte[RingCloudLayers.BlockBytes];
	// Megastorm near state (docs/megastorm-design.md 3.6): true while the wall or anvil record is in the uploaded block.
	private bool _megastormNear;
	private bool _megastormStripEnabledApplied;
	private bool _megastormFrameActiveApplied;
	// The Sunshine resource the megastorm prewarm was requested on (null = not yet).
	private GodotObject? _megastormPrewarmOwner;
	private readonly List<int> _ciMegastormSlots = new(4);
	private readonly List<RingVolumetricCloudLayer.RenderWeatherSystem> _ciMegastormScratch = new(4);
	private const float MegastormFastMacroRefreshSeconds = 0.05f;
	private const int MegastormFastMacroSlices = 2;
	private const float MegastormFastShadowUpdatesPerSecond = 6.7f;
	private float _ciEnvelopeFloorMeters = 500.0f;
	private float _ciEnvelopeCeilingMeters = RingCloudLayers.CeilingMeters;
	private float _ciSelectionRadiusMeters;
	private bool _ciUploadedEnabled;
	private bool? _ciLastAppliedFlag;
	private int _ciRevisionSignature;
	private bool _ciHasRevisionSignature;
	private bool _ciHasAmbientState;
	private float _ciSmoothedStorm;
	private float _ciSmoothedHeavy;
	private ulong _ciLastAmbientMsec;

	private MegastormModel? MegastormRenderModel => _weather?.MegastormSource switch
	{
		RingMegastorm node => node.Model,
		MegastormModel model => model,
		_ => null
	};

	/// <summary>GPU near cloud shadow refresh rate: raised to at least 6.7 Hz while the megastorm wall is near (anvil shadow edges move at 350 m/s; the
	/// crossfade hides the 52 m steps, see the WO4b motion check).</summary>
	private float EffectiveGpuNearShadowUpdatesPerSecond
		=> _megastormNear ? Mathf.Max(GpuNearShadowUpdatesPerSecond, MegastormFastShadowUpdatesPerSecond) : GpuNearShadowUpdatesPerSecond;

	/// <summary>Light-march reach: the longest directional light ray is four lighting step distances.</summary>
	private float CameraIndependentLightReachMeters => 4.0f * Mathf.Max(0.5f, LightingTravelKm) * 1000.0f;

	/// <summary>
	/// Collects, orders, caps and fades the render weather systems around the camera, derives the constant envelope and refreshes the
	/// camera-local ambient factors. Called from RefreshWeather before any uniform is written (flag on only).
	/// </summary>
	private void PrepareCameraIndependentWeather(in CloudAltitudeRange backgroundAltitude)
	{
		if (_weather == null || _explorer == null || _grid == null) return;
		RingCanonicalPosition camera = _explorer.CanonicalPosition;
		double time = _weather.CloudSimulationTimeSeconds;
		double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
		Vector2 wind = _weather.WeatherWindMetersPerSecond;
		double sourceAround = WrapCircumference(camera.AroundMeters - wind.X * time, circumference);
		double sourceAcross = camera.AcrossMeters - wind.Y * time;

		ResolveEffectiveCloudHandoff();
		// Lead the camera by its travel speed: the selection and the macro map are refreshed every WeatherRefreshSeconds
		// and the map is built over MacroMapRefreshSeconds + MacroMapSlices callbacks, so a fast camera would otherwise
		// reach the map edge (storms vanish at the leading handoff band from about 12 km/s).
		float leadSeconds = Mathf.Max(0.05f, WeatherRefreshSeconds) + Mathf.Clamp(MacroMapRefreshSeconds, 0.05f, 2.0f)
			+ Math.Clamp(MacroMapSlices, 1, 32) / 30.0f;
		_ciSelectionRadiusMeters = EffectiveHandoffEndMeters + CameraIndependentLightReachMeters
			+ Mathf.Max(0.0f, WeatherSystemSelectionMarginMeters)
			+ Mathf.Clamp(_smoothedTravelSpeed, 0.0f, SuspendSpeedMetersPerSecond) * leadSeconds;
		_ciCollected.Clear();
		_weather.CollectRenderWeatherSystems(camera, _ciSelectionRadiusMeters, time, _ciCollected,
			FixedCloudAltitudeDatumMeters, StormFrontCloudBaseAltitudeMeters);

		// Constant envelope: lowest storm base .. StormEnvelopeCeilingMeters. Only a spawned/manual system can widen it, and those
		// changes are structural (they bump the revision below).
		float stormBase = FixedCloudAltitudeDatumMeters + Mathf.Max(100.0f, StormFrontCloudBaseAltitudeMeters);
		float envelopeFloor = Mathf.Min(backgroundAltitude.FloorMeters, stormBase);
		float envelopeCeiling = Mathf.Min(RingCloudLayers.CeilingMeters,
			Mathf.Max(Mathf.Max(StormEnvelopeCeilingMeters, backgroundAltitude.CeilingMeters), envelopeFloor + 1.0f));
		_ciManual.Clear();
		_ciNatural.Clear();
		foreach (RingVolumetricCloudLayer.RenderWeatherSystem system in _ciCollected)
		{
			double referenceAround = system.IsStatic ? camera.AroundMeters : sourceAround;
			double referenceAcross = system.IsStatic ? camera.AcrossMeters : sourceAcross;
			double deltaAround = ShortestCircumferenceDelta(referenceAround, system.CenterAroundMeters, circumference);
			double deltaAcross = system.CenterAcrossMeters - referenceAcross;
			double edge = Math.Sqrt(deltaAround * deltaAround + deltaAcross * deltaAcross)
				- system.MajorRadiusMeters * system.SupportMultiplier;
			if (system.IsStatic)
			{
				_ciManual.Add(new UploadedWeatherSystem(system, 1.0f, edge));
				if (system.StormCeilingMeters > system.StormFloorMeters + 1.0f)
				{
					envelopeFloor = Mathf.Min(envelopeFloor, system.StormFloorMeters);
					envelopeCeiling = Mathf.Min(RingCloudLayers.CeilingMeters, Mathf.Max(envelopeCeiling, system.StormCeilingMeters));
				}
			}
			else
			{
				_ciNatural.Add(new UploadedWeatherSystem(system, 1.0f, edge));
			}
		}
		_ciEnvelopeFloorMeters = envelopeFloor;
		_ciEnvelopeCeilingMeters = envelopeCeiling;

		int cap = Math.Clamp(WeatherSystemUploadCount, 1, MaximumWeatherSystemUploadCount);
		WeatherSystemCandidateCount = _ciManual.Count + _ciNatural.Count;
		WeatherSystemCandidatePeak = Math.Max(WeatherSystemCandidatePeak, WeatherSystemCandidateCount);
		_ciManual.Sort(static (a, b) => a.EdgeDistanceMeters.CompareTo(b.EdgeDistanceMeters));
		_ciNatural.Sort(static (a, b) => a.EdgeDistanceMeters.CompareTo(b.EdgeDistanceMeters));
		_ciUploaded.Clear();
		for (int i = 0; i < _ciManual.Count && _ciUploaded.Count < cap; i++) _ciUploaded.Add(_ciManual[i]);
		int naturalCap = Math.Max(0, cap - _ciUploaded.Count);
		bool overflow = _ciNatural.Count > naturalCap;
		// Over the cap the (N+1)-th system's edge distance is the boundary: a system whose edge is within OverflowFadeMeters of it
		// carries a smoothstep weight, so a system that swaps in/out across the cap boundary has ~0 weight at the swap.
		double limitEdge = overflow ? _ciNatural[naturalCap].EdgeDistanceMeters : double.PositiveInfinity;
		double fadeMeters = Math.Max(1.0, OverflowFadeMeters);
		for (int i = 0; i < naturalCap && i < _ciNatural.Count; i++)
		{
			UploadedWeatherSystem entry = _ciNatural[i];
			if (overflow)
			{
				float t = (float)Math.Clamp((limitEdge - entry.EdgeDistanceMeters) / fadeMeters, 0.0, 1.0);
				entry = entry with { Fade = t * t * (3.0f - 2.0f * t) };
			}
			_ciUploaded.Add(entry);
		}
		if (overflow) WeatherSystemOverflowCount++;
		UploadedWeatherSystemCount = _ciUploaded.Count;

		// Structural revision: manual systems added/removed, or the cap changed. Ordinary lifecycle drift never bumps it.
		var signature = new HashCode();
		signature.Add(cap);
		foreach (UploadedWeatherSystem entry in _ciManual) signature.Add(entry.System.SystemId);
		int revisionSignature = signature.ToHashCode();
		if (_ciHasRevisionSignature && revisionSignature != _ciRevisionSignature)
		{
			WeatherSystemsRevision++;
			_clouds?.Set("weather_systems_revision", WeatherSystemsRevision);
		}
		_ciRevisionSignature = revisionSignature;
		_ciHasRevisionSignature = true;

		UpdateCameraIndependentAmbient(camera, time, circumference, wind);
	}

	/// <summary>
	/// Variant (a) of the plan's ambient A/B: the ambient colour and heavy-overcast transmittance stay camera-local, but are driven by the
	/// shader's own per-sample coverage/storm formula evaluated at the camera (uploaded systems, continuous cluster term) and smoothed
	/// with a ~1 s exponential, so they no longer step with the quantized CPU weather.
	/// </summary>
	private void UpdateCameraIndependentAmbient(in RingCanonicalPosition camera, double time, double circumference, Vector2 wind)
	{
		if (_weather == null) return;
		EvaluateCameraIndependentMacro(camera.AroundMeters, camera.AcrossMeters, time, circumference, wind,
			out float coverage, out float storm);
		float heavy = SmoothUnit((coverage - HeavyOvercastStartCoverage) / Mathf.Max(0.01f, 1.0f - HeavyOvercastStartCoverage));
		ulong now = Time.GetTicksMsec();
		float blend = 1.0f;
		if (_ciHasAmbientState)
		{
			float dt = Mathf.Clamp((now - _ciLastAmbientMsec) * 0.001f, 0.0f, 5.0f);
			blend = 1.0f - MathF.Exp(-dt / CameraIndependentAmbientSmoothingSeconds);
		}
		_ciHasAmbientState = true;
		_ciLastAmbientMsec = now;
		_ciSmoothedStorm = Mathf.Lerp(_ciSmoothedStorm, storm, blend);
		_ciSmoothedHeavy = Mathf.Lerp(_ciSmoothedHeavy, heavy, blend);
		_heavyOvercastFactor = _ciSmoothedHeavy;
		_localStormFactor = SmoothCameraStormLevel(camera, time, circumference, wind, coverage, heavy, blend); // storm-owned D (StormLight.cs)
	}

	/// <summary>
	/// C# mirror of ring_ws_update_macro (RingWeatherSystems.glslinc) for one canonical position: natural systems by product +
	/// strongest (EvaluateMacroWeather), manual systems folded by max with the (n - 0.55) / 0.75 falloff (ApplyManualWeather),
	/// coverage = clamp(base + continuous cluster + macroBias + densityBias, 0.015, 0.985), storm = clamp(0.65 influence + 0.55 precip).
	/// </summary>
	private void EvaluateCameraIndependentMacro(double aroundMeters, double acrossMeters, double time, double circumference,
		Vector2 wind, out float coverage, out float storm)
	{
		double sourceAround = WrapCircumference(aroundMeters - wind.X * time, circumference);
		double sourceAcross = acrossMeters - wind.Y * time;
		float combined = 0.0f;
		float strongest = 0.0f;
		float strongestDensity = 0.0f;
		float strongestPrecipitation = 0.0f;
		foreach (UploadedWeatherSystem entry in _ciUploaded)
		{
			RingVolumetricCloudLayer.RenderWeatherSystem system = entry.System;
			if (system.IsStatic) continue;
			double deltaAround = ShortestCircumferenceDelta(sourceAround, system.CenterAroundMeters, circumference);
			double deltaAcross = system.CenterAcrossMeters - sourceAcross;
			double major = system.MajorRadiusMeters;
			if (deltaAround * deltaAround + deltaAcross * deltaAcross >= major * major) continue;
			double along = system.OrientationCosine * deltaAround + system.OrientationSine * deltaAcross;
			double across = -system.OrientationSine * deltaAround + system.OrientationCosine * deltaAcross;
			double minor = system.MinorRadiusMeters;
			double normalizedSquared = along * along / Math.Max(1.0, major * major) + across * across / Math.Max(1.0, minor * minor);
			if (normalizedSquared >= 1.0) continue;
			float influence = 1.0f - SmoothUnit(((float)Math.Sqrt(normalizedSquared) - 0.32f) / 0.68f);
			float weighted = influence * system.Influence * entry.Fade;
			combined = 1.0f - (1.0f - combined) * (1.0f - weighted);
			if (weighted <= strongest) continue;
			strongest = weighted;
			strongestDensity = system.Influence;
			strongestPrecipitation = system.Precipitation;
		}
		combined = Mathf.Clamp(combined, 0.0f, 1.0f);
		strongest = Mathf.Clamp(strongest, 0.0f, 1.0f);
		float macroBias = (combined - 0.28f) * 0.46f;
		float densityBias = strongest * (strongestDensity - 0.5f) * 0.24f;
		float cluster = _weather?.SampleContinuousClusterTerm(aroundMeters, acrossMeters, time) ?? 0.0f;
		float cloudCoverage = Mathf.Clamp(
			(_weather?.Coverage ?? 0.0f) + SunshineBaseCoverageOffset + cluster + macroBias + densityBias, 0.015f, 0.985f);
		float precipitation = Mathf.Clamp(
			strongestPrecipitation * combined * SmoothUnit((cloudCoverage - 0.35f) / 0.55f), 0.0f, 1.0f);
		float systemInfluence = combined;
		foreach (UploadedWeatherSystem entry in _ciUploaded)
		{
			RingVolumetricCloudLayer.RenderWeatherSystem system = entry.System;
			if (!system.IsStatic) continue;
			double deltaAround = ShortestCircumferenceDelta(system.CenterAroundMeters, aroundMeters, circumference);
			double deltaAcross = acrossMeters - system.CenterAcrossMeters;
			double x = deltaAround * system.OrientationCosine + deltaAcross * system.OrientationSine;
			double z = -deltaAround * system.OrientationSine + deltaAcross * system.OrientationCosine;
			float normalized = (float)Math.Sqrt(x * x / ((double)system.MajorRadiusMeters * system.MajorRadiusMeters)
				+ z * z / ((double)system.MinorRadiusMeters * system.MinorRadiusMeters));
			float influence = (1.0f - SmoothUnit((normalized - 0.55f) / 0.75f)) * system.Influence * entry.Fade;
			if (influence <= 0.0001f) continue;
			systemInfluence = Mathf.Max(systemInfluence, influence);
			cloudCoverage = Mathf.Max(cloudCoverage, Mathf.Lerp(cloudCoverage, system.ManualCoverageTarget, influence));
			precipitation = Mathf.Max(precipitation, system.Precipitation * influence);
		}
		coverage = cloudCoverage;
		storm = Mathf.Clamp(systemInfluence * 0.65f + precipitation * 0.55f, 0.0f, 1.0f);
	}

	/// <summary>
	/// Live toggle of <see cref="CameraIndependentClouds"/>: the next RefreshWeather already switches the uniform path, so this only asks
	/// Sunshine to drop its temporal history once, so the old and new cloud shapes do not smear into each other.
	/// </summary>
	private void NotifyCameraIndependentFlagApplied()
	{
		bool flag = CameraIndependentClouds;
		bool changed = _ciLastAppliedFlag.HasValue && _ciLastAppliedFlag.Value != flag;
		_ciLastAppliedFlag = flag;
		if (changed && _clouds != null && GodotObject.IsInstanceValid(_clouds) && _clouds.HasMethod("request_history_reset"))
			_clouds.Call("request_history_reset");
	}

	/// <summary>
	/// Packs the uploaded systems into the std140 block and hands it to Sunshine (one buffer_update in the render callback). Also
	/// publishes a single "disabled" block after the flag is switched off, so the shaders return to the legacy path.
	/// </summary>
	private void PublishWeatherSystems(bool enabled)
	{
		if (_clouds == null || !GodotObject.IsInstanceValid(_clouds) || !_clouds.HasMethod(SetWeatherSystemsMethod)) return;
		if (!enabled)
		{
			if (!_ciUploadedEnabled) return;
			_ciUploadedEnabled = false;
			UploadedWeatherSystemCount = 0;
			WeatherSystemCandidateCount = 0;
			_ciHasAmbientState = false;
			Array.Clear(_ciBlock);
			_clouds.Call(SetWeatherSystemsMethod, _ciBlock);
			_megastormNear = false;
			_ciMegastormSlots.Clear();
			PublishMegastormStrip();
			PublishMacroWeatherMap(false);
			PublishStormProfileMap(false);
			return;
		}

		Array.Clear(_ciBlock);
		int count = _ciUploaded.Count;
		float sculptStrength = SculptLocalizedStorms ? Mathf.Clamp(StormSculptStrength, 0.0f, 1.0f) : 0.0f;
		RingVolumetricCloudLayer weather = _weather!;
		int seed = _grid?.TerrainSampler?.Seed ?? 1337;
		int smallCells = Math.Max(2, weather.SmallWeatherClusterCells);
		int largeCells = Math.Max(4, weather.LargeWeatherClusterCells);
		// header0: count, enabled, base coverage (without the user bias), coverage bias
		WriteFloat(0, count);
		WriteFloat(4, 1.0f);
		WriteFloat(8, weather.Coverage + SunshineBaseCoverageOffset);
		WriteFloat(12, CloudCoverageBias);
		// header1: heavy-overcast start, sculpt strength, envelope floor/ceiling
		WriteFloat(16, HeavyOvercastStartCoverage);
		WriteFloat(20, sculptStrength);
		// z carries CameraIndependentDebugMask (small int, exact as a float); w is the downwind angle of the weather wind (radians,
		// canonical around/across frame) the storm shape shears toward.
		WriteFloat(24, Math.Clamp(CameraIndependentDebugMask, 0, 1023));
		Vector2 stormWind = weather.WeatherWindMetersPerSecond;
		WriteFloat(28, stormWind.LengthSquared() > 1e-8f ? MathF.Atan2(stormWind.Y, stormWind.X) : 0.0f);
		// header2: background floor/ceiling, selection radius, revision
		WriteFloat(32, _backgroundCloudFloorMeters);
		WriteFloat(36, _backgroundCloudCeilingMeters);
		WriteFloat(40, _ciSelectionRadiusMeters);
		WriteFloat(44, WeatherSystemsRevision);
		// header3: P5 cluster parameters (cell, strength, small + 256 * large cells) and the exact integer seed bits
		WriteFloat(48, Mathf.Max(1000.0f, weather.FormationCellMeters));
		WriteFloat(52, weather.WeatherClusteringStrength);
		WriteFloat(56, smallCells + 256 * largeCells);
		BinaryPrimitives.WriteInt32LittleEndian(_ciBlock.AsSpan(60, 4), seed);
		bool megastormNear = false;
		_ciMegastormSlots.Clear();
		for (int i = 0; i < count; i++)
		{
			UploadedWeatherSystem entry = _ciUploaded[i];
			WriteWeatherSystemSlot(i, entry);
			int systemId = entry.System.SystemId;
			if (RingVolumetricCloudLayer.IsMegastormRecordId(systemId))
			{
				_ciMegastormSlots.Add(i);
				megastormNear |= systemId == MegastormModel.WallRecordId || systemId == MegastormModel.AnvilRecordId;
			}
		}
		_megastormNear = megastormNear;
		// Near: the records were collected at the refresh time; rewrite them at the predicted publish time so the refresh is not a step back.
		if (megastormNear) RefreshMegastormSlots(PredictedMegastormPublishTime());
		_ciUploadedEnabled = true;
		_clouds.Call(SetWeatherSystemsMethod, _ciBlock);
		PublishMegastormStrip();
		PublishMacroWeatherMap(MacroWeatherMap);
		PublishCloudLayers();
		PublishStormProfileMap(!DebugLegacyStormShape);
	}

	/// <summary>Packs one uploaded system into its std140 slot (4 x vec4) of the block.</summary>
	private void WriteWeatherSystemSlot(int index, in UploadedWeatherSystem entry)
	{
		{
			RingVolumetricCloudLayer.RenderWeatherSystem system = entry.System;
			int offset = 64 + index * 64;
			WriteFloat(offset + 0, (float)system.CenterAroundMeters);
			WriteFloat(offset + 4, (float)system.CenterAcrossMeters);
			WriteFloat(offset + 8, system.MajorRadiusMeters);
			WriteFloat(offset + 12, system.MinorRadiusMeters);
			WriteFloat(offset + 16, system.OrientationCosine);
			WriteFloat(offset + 20, system.OrientationSine);
			WriteFloat(offset + 24, system.Influence);
			WriteFloat(offset + 28, system.Precipitation);
			// A natural system's storm band never exceeds the envelope (deterministic per system, so still camera independent).
			float stormFloor = system.StormFloorMeters;
			float stormCeiling = system.StormCeilingMeters;
			if (stormCeiling > stormFloor + 1.0f) stormCeiling = Mathf.Min(stormCeiling, _ciEnvelopeCeilingMeters);
			WriteFloat(offset + 32, stormFloor);
			WriteFloat(offset + 36, stormCeiling);
			WriteFloat(offset + 40, (float)(int)system.PhysicalProfile);
			WriteFloat(offset + 44, system.Flags);
			WriteFloat(offset + 48, system.ManualCoverageTarget);
			WriteFloat(offset + 52, system.SupportMultiplier);
			WriteFloat(offset + 56, entry.Fade);
			// d.w: storm lifecycle code (birth 0-0.3, develop 0.3-1, mature 1-2, dissipate 2-3; 1.5 for manual systems).
			WriteFloat(offset + 60, RingVolumetricCloudLayer.StormLifecycleOverride ?? system.Lifecycle);
		}
	}

	/// <summary>Weather-clock time the next publish will show: now plus two frames of latency and the shadow/map slack (now on a paused clock).</summary>
	private double PredictedMegastormPublishTime()
		=> _weather == null ? 0.0 : _weather.CloudSimulationTimeSeconds
			+ (_weather.CloudSimulationPaused ? 0.0 : 2.0 * Math.Clamp(_megastormFrameDelta, 0.0, 0.1) + 0.025);

	private double _megastormFrameDelta;

	/// <summary>
	/// Re-evaluates the megastorm records at <paramref name="timeSeconds"/> and rewrites their slots in the uploaded block (keeping each slot's
	/// fade). No upload; allocation-free (reused scratch lists).
	/// </summary>
	private void RefreshMegastormSlots(double timeSeconds)
	{
		if (_weather == null || _explorer == null || _ciMegastormSlots.Count == 0) return;
		_ciMegastormScratch.Clear();
		_weather.CollectMegastormRenderSystems(_explorer.CanonicalPosition, _ciSelectionRadiusMeters, timeSeconds, _ciMegastormScratch,
			FixedCloudAltitudeDatumMeters, StormFrontCloudBaseAltitudeMeters);
		for (int s = 0; s < _ciMegastormSlots.Count; s++)
		{
			int slot = _ciMegastormSlots[s];
			UploadedWeatherSystem entry = _ciUploaded[slot];
			for (int c = 0; c < _ciMegastormScratch.Count; c++)
			{
				if (_ciMegastormScratch[c].SystemId != entry.System.SystemId) continue;
				entry = entry with { System = _ciMegastormScratch[c] };
				_ciUploaded[slot] = entry;
				WriteWeatherSystemSlot(slot, entry);
				break;
			}
		}
	}

	/// <summary>
	/// Per-frame megastorm work (called from _Process after UpdateCloudFrame): the megastorm frame uniform for Sunshine (direction 0 = off) and,
	/// while the wall is near, the predicted-time record refresh plus one block upload. Costs a few field reads when no megastorm exists.
	/// </summary>
	private void UpdateMegastormPerFrame(double delta)
	{
		_megastormFrameDelta = delta;
		PublishMegastormFrame();
		if (!_megastormNear || !_ciUploadedEnabled || _clouds == null || !GodotObject.IsInstanceValid(_clouds)) return;
		RefreshMegastormSlots(PredictedMegastormPublishTime());
		_clouds.Call(SetWeatherSystemsMethod, _ciBlock);
	}

	private void PublishMegastormFrame()
	{
		if (_clouds == null || !GodotObject.IsInstanceValid(_clouds) || !_clouds.HasMethod(SetMegastormFrameMethod)) return;
		MegastormModel? model = CameraIndependentClouds ? MegastormRenderModel : null;
		MegastormFrame frame = default;
		double t = 0.0;
		if (model != null && _weather != null)
		{
			t = _weather.CloudSimulationTimeSeconds;
			frame = model.Evaluate(t);
		}
		if (model == null || !frame.Active)
		{
			if (_megastormFrameActiveApplied)
			{
				_megastormFrameActiveApplied = false;
				_clouds.Call(SetMegastormFrameMethod, 0.0, 0.0, 0.0, 0.0, false);
			}
			return;
		}
		_megastormFrameActiveApplied = true;
		// Prewarm (WO4b): from the first frame a megastorm exists (its trigger, or the load of a save that has one), Sunshine allocates the strip
		// and compiles the megastorm march variants, so the wall's first approach does not stall on them. Once per Sunshine resource.
		if (!ReferenceEquals(_megastormPrewarmOwner, _clouds) && _clouds.HasMethod(SetMegastormPrewarmMethod))
		{
			_clouds.Call(SetMegastormPrewarmMethod, true);
			_megastormPrewarmOwner = _clouds;
		}
		double s = frame.AroundMeters;
		float sHi = (float)s;
		_clouds.Call(SetMegastormFrameMethod, (double)sHi, s - sHi, (double)frame.Direction, model.UnwrappedAroundAt(t), !RingMegastorm.DebugReprojectOff);
	}

	/// <summary>
	/// Publishes the megastorm strip configuration (docs/megastorm-design.md 3.2): enabled only while a megastorm is active, its wall or anvil
	/// record is uploaded and the A/B debug bit 512 is clear. Disabled is sent once on the transition.
	/// </summary>
	private void PublishMegastormStrip()
	{
		if (_clouds == null || !GodotObject.IsInstanceValid(_clouds) || !_clouds.HasMethod(SetMegastormStripMethod)) return;
		MegastormModel? model = CameraIndependentClouds ? MegastormRenderModel : null;
		MegastormFrame frame = default;
		double t = 0.0;
		if (model != null && _weather != null)
		{
			t = _weather.CloudSimulationTimeSeconds;
			frame = model.Evaluate(t);
		}
		bool enabled = model != null && frame.Active && _megastormNear && (CameraIndependentDebugMask & 512) == 0;
		if (!enabled)
		{
			if (!_megastormStripEnabledApplied) return;
			_megastormStripEnabledApplied = false;
			_clouds.Call(SetMegastormStripMethod, false, 0.0, 0.0, 0.0, 0.0, 318000.0, 0.0, 0.0, 40000.0, 0.0, 0.0, 0.0, 8, 2.0);
			return;
		}
		_megastormStripEnabledApplied = true;
		MegastormSettings settings = model!.Settings;
		// The ceiling is the hard cloud cap; the containment field's ceiling helper replaces this when field WO6 lands.
		_clouds.Call(SetMegastormStripMethod, true, t, (double)frame.Level, (double)frame.Strength, (double)model.State.Seed,
			settings.WidthMeters, settings.BowMeters, settings.MeanderAmplitudeMeters, settings.MeanderWavelengthMeters,
			model.MeanderPhaseWrapped(t), (double)RingCloudLayers.CeilingMeters, (double)FixedCloudAltitudeDatumMeters, 8, 2.0);
	}

	/// <summary>
	/// S6: publishes the cloud layer table (resources/clouds/cloud_layer_table.tres) and the sun direction in the ring-grid frame to the macro map
	/// build, which writes the layer coverages and weatherness into array layers 2 / 3. The march does not read them yet.
	/// </summary>
	private void PublishCloudLayers()
	{
		if (_clouds == null || !GodotObject.IsInstanceValid(_clouds) || !_clouds.HasMethod(SetCloudLayersMethod)) return;
		Vector3 sunLocal = Vector3.Zero;
		if (_sunLight != null && _grid != null)
			sunLocal = _grid.CaptureLodGeometrySnapshot().GridGlobalTransform.Basis.Orthonormalized().Inverse() * _sunLight.GlobalBasis.Z.Normalized();
		SyncCloudLayerTuning();
		RingCloudLayers.BuildBlock(CloudLayerTable.Shared, sunLocal, _ciLayersBlock);
		_clouds.Call(SetCloudLayersMethod, _ciLayersBlock);
	}

	/// <summary>
	/// Tells Sunshine which world region the macro weather map must cover and how to spread its build. The region is the selection radius
	/// (handoff end + light reach + margin, the same disc the uploaded systems cover) around the camera by the whole ring width; Sunshine
	/// snaps the origin to whole texels when a build starts. Map off (or flag off) returns every shader to the direct per-system path.
	/// </summary>
	private void PublishMacroWeatherMap(bool enabled)
	{
		if (_clouds == null || !GodotObject.IsInstanceValid(_clouds) || !_clouds.HasMethod(SetMacroWeatherMapMethod)) return;
		if (!enabled || _explorer == null || _grid == null)
		{
			_clouds.Call(SetMacroWeatherMapMethod, false, 0.0, 0.0, 0.0, 0.0, 1000.0, 1, 0.25);
			return;
		}
		double width = Math.Max(1.0, _grid.RingAcrossWidthMeters);
		// Megastorm near (3.6): 2 slices every 0.05 s so the D / rain / deck coverage edge steps about 29 m instead of 130 m per build.
		int slices = _megastormNear ? MegastormFastMacroSlices : Math.Clamp(MacroMapSlices, 1, 32);
		double refresh = _megastormNear ? MegastormFastMacroRefreshSeconds : (double)Mathf.Clamp(MacroMapRefreshSeconds, 0.05f, 2.0f);
		_clouds.Call(SetMacroWeatherMapMethod, true, _explorer.CanonicalPosition.AroundMeters, (double)_ciSelectionRadiusMeters,
			-1000.0, width + 1000.0, (double)Mathf.Clamp(MacroMapResolution, 250.0f, 4000.0f),
			slices, refresh);
	}

	/// <summary>
	/// Tells Sunshine where to bake the storm profile map (docs/storm-revamp-s4-sculpt-v2-design.md): a square around the camera with the
	/// macro map's half extent, plus the shape settings the bake reads (pillar radius, anvil multiplier / start, shear, mammatus) and the
	/// downwind direction and lean of the sheared towers. DebugLegacyStormShape (RING_STORM_SHAPE_LEGACY=1) switches the map off, which
	/// returns the shaders to HEAD's analytic storm loop (the debug A/B).
	/// </summary>
	private void PublishStormProfileMap(bool enabled)
	{
		if (_clouds == null || !GodotObject.IsInstanceValid(_clouds) || !_clouds.HasMethod(SetStormProfileMapMethod)) return;
		if (!enabled || _explorer == null || _grid == null || _weather == null)
		{
			_clouds.Call(SetStormProfileMapMethod, false, 0.0, 0.0, 1000.0, 4200.0, 2.6, 0.7, 0.65, 0.6, 0.0, 0.0, 0.0, 4, 0.25);
			return;
		}
		float pillar = Mathf.Clamp(StormPillarRadiusMeters, 1000.0f, 8000.0f);
		float shear = SanitizeFinite(AnvilShear, 0.65f, 0.0f, 1.0f);
		Vector2 wind = _weather.WeatherWindMetersPerSecond;
		float angle = wind.LengthSquared() > 1e-8f ? MathF.Atan2(wind.Y, wind.X) : 0.0f;
		RingCanonicalPosition camera = _explorer.CanonicalPosition;
		_clouds.Call(SetStormProfileMapMethod, true, camera.AroundMeters, camera.AcrossMeters, (double)Math.Min(_ciSelectionRadiusMeters, StormMapMaxHalfExtentMeters),
			(double)pillar, (double)Mathf.Clamp(AnvilRadiusMultiplier, 1.2f, 4.0f), (double)Mathf.Clamp(AnvilStartFraction, 0.5f, 0.9f),
			(double)shear, (double)SanitizeFinite(MammatusStrength, 0.6f, 0.0f, 1.0f), (double)MathF.Cos(angle), (double)MathF.Sin(angle),
			(double)(shear * pillar), 4, (double)Mathf.Clamp(MacroMapRefreshSeconds, 0.05f, 2.0f));
	}

	private void WriteFloat(int offset, float value)
		=> BinaryPrimitives.WriteSingleLittleEndian(_ciBlock.AsSpan(offset, 4), value);

	/// <summary>
	/// Shadow-map inputs that used to follow the camera's single system: the farthest reach (distance + major radius + 4 km) of any
	/// uploaded sculpt system whose ellipse contains the viewer, and whether one of them is a Cumulonimbus (anvil height fraction).
	/// </summary>
	private void ResolveCameraIndependentShadowSystems(in RingCanonicalPosition viewer, out float reachMeters, out bool anvil)
	{
		reachMeters = 0.0f;
		anvil = false;
		if (_weather == null || _grid == null) return;
		double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
		Vector2 wind = _weather.WeatherWindMetersPerSecond;
		double time = _weather.CloudSimulationTimeSeconds;
		double sourceAround = WrapCircumference(viewer.AroundMeters - wind.X * time, circumference);
		double sourceAcross = viewer.AcrossMeters - wind.Y * time;
		foreach (UploadedWeatherSystem entry in _ciUploaded)
		{
			RingVolumetricCloudLayer.RenderWeatherSystem system = entry.System;
			if (!system.Sculpts || entry.Fade <= 0.0f) continue;
			double deltaAround = ShortestCircumferenceDelta(
				system.IsStatic ? viewer.AroundMeters : sourceAround, system.CenterAroundMeters, circumference);
			double deltaAcross = system.CenterAcrossMeters - (system.IsStatic ? viewer.AcrossMeters : sourceAcross);
			double along = system.OrientationCosine * deltaAround + system.OrientationSine * deltaAcross;
			double across = -system.OrientationSine * deltaAround + system.OrientationCosine * deltaAcross;
			double major = Math.Max(1.0, system.MajorRadiusMeters * system.SupportMultiplier);
			double minor = Math.Max(1.0, system.MinorRadiusMeters * system.SupportMultiplier);
			if (RingVolumetricCloudLayer.IsMegastormRecordId(system.SystemId))
			{
				// Full-width slab: only the minor (around) axis bounds it; its across extent is 1e7 m.
				if (Math.Abs(across) >= minor) continue;
				reachMeters = Mathf.Max(reachMeters, (float)Math.Abs(across) + system.MinorRadiusMeters + 4000.0f);
				anvil |= system.PhysicalProfile == CloudFormationProfile.Cumulonimbus;
				continue;
			}
			if (along * along / (major * major) + across * across / (minor * minor) >= 1.0) continue;
			float distance = (float)Math.Sqrt(deltaAround * deltaAround + deltaAcross * deltaAcross);
			reachMeters = Mathf.Max(reachMeters, distance + Mathf.Max(system.MajorRadiusMeters, system.MinorRadiusMeters) + 4000.0f);
			anvil |= system.PhysicalProfile == CloudFormationProfile.Cumulonimbus;
		}
	}

	/// <summary>Floor and ceiling the shadow height samples and sun-projection compensation should use.</summary>
	private void ResolveShadowBand(out float floorMeters, out float ceilingMeters)
	{
		if (CameraIndependentClouds && _backgroundCloudCeilingMeters > _backgroundCloudFloorMeters + 1.0f)
		{
			floorMeters = _backgroundCloudFloorMeters;
			ceilingMeters = _backgroundCloudCeilingMeters;
			return;
		}
		floorMeters = _clouds?.Get("cloud_floor").AsSingle() ?? 0.0f;
		ceilingMeters = Mathf.Max(floorMeters + 1.0f, _clouds?.Get("cloud_ceiling").AsSingle() ?? 0.0f);
	}

	private static double WrapCircumference(double value, double circumference)
	{
		double wrapped = value % circumference;
		return wrapped < 0.0 ? wrapped + circumference : wrapped;
	}

	private static double ShortestCircumferenceDelta(double from, double to, double circumference)
	{
		double delta = WrapCircumference(to, circumference) - WrapCircumference(from, circumference);
		if (delta > circumference * 0.5) delta -= circumference;
		if (delta < -circumference * 0.5) delta += circumference;
		return delta;
	}
}
