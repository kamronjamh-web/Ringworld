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
	[Export(PropertyHint.Range, "5000,40000,500")] public float StormEnvelopeCeilingMeters { get; set; } = 20000.0f;

	/// <summary>Perf ablation bits (0 = normal): 1 background band only (no per-ray sculpt intervals, no sub-mask band widening), 2 clear sculpt mask, 4 skip ray mask, 8 constant macro, 16 light march breaks on the background band instead of the sub-mask band, 32 legacy shader path. Re-uploaded every weather refresh.</summary>
	[Export(PropertyHint.Range, "0,63,1")] public int CameraIndependentDebugMask { get; set; } = 0;

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
	private float _ciEnvelopeFloorMeters = 500.0f;
	private float _ciEnvelopeCeilingMeters = 20000.0f;
	private float _ciSelectionRadiusMeters;
	private bool _ciUploadedEnabled;
	private bool? _ciLastAppliedFlag;
	private int _ciRevisionSignature;
	private bool _ciHasRevisionSignature;
	private bool _ciHasAmbientState;
	private float _ciSmoothedStorm;
	private float _ciSmoothedHeavy;
	private ulong _ciLastAmbientMsec;

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
		_ciSelectionRadiusMeters = EffectiveHandoffEndMeters + CameraIndependentLightReachMeters
			+ Mathf.Max(0.0f, WeatherSystemSelectionMarginMeters);
		_ciCollected.Clear();
		_weather.CollectRenderWeatherSystems(camera, _ciSelectionRadiusMeters, time, _ciCollected,
			FixedCloudAltitudeDatumMeters, StormFrontCloudBaseAltitudeMeters);

		// Constant envelope: lowest storm base .. StormEnvelopeCeilingMeters. Only a spawned/manual system can widen it, and those
		// changes are structural (they bump the revision below).
		float stormBase = FixedCloudAltitudeDatumMeters + Mathf.Max(100.0f, StormFrontCloudBaseAltitudeMeters);
		float envelopeFloor = Mathf.Min(backgroundAltitude.FloorMeters, stormBase);
		float envelopeCeiling = Mathf.Max(Mathf.Max(StormEnvelopeCeilingMeters, backgroundAltitude.CeilingMeters), envelopeFloor + 1.0f);
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
					envelopeCeiling = Mathf.Max(envelopeCeiling, system.StormCeilingMeters);
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
		_localStormFactor = Mathf.Max(_ciSmoothedStorm, _ciSmoothedHeavy);
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
			PublishMacroWeatherMap(false);
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
		// z carries CameraIndependentDebugMask (small int, exact as a float); the envelope floor is CPU-side only (no shader reads it).
		WriteFloat(24, Math.Clamp(CameraIndependentDebugMask, 0, 63));
		WriteFloat(28, _ciEnvelopeCeilingMeters);
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
		for (int i = 0; i < count; i++)
		{
			UploadedWeatherSystem entry = _ciUploaded[i];
			RingVolumetricCloudLayer.RenderWeatherSystem system = entry.System;
			int offset = 64 + i * 64;
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
		}
		_ciUploadedEnabled = true;
		_clouds.Call(SetWeatherSystemsMethod, _ciBlock);
		PublishMacroWeatherMap(MacroWeatherMap);
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
		_clouds.Call(SetMacroWeatherMapMethod, true, _explorer.CanonicalPosition.AroundMeters, (double)_ciSelectionRadiusMeters,
			-1000.0, width + 1000.0, (double)Mathf.Clamp(MacroMapResolution, 250.0f, 4000.0f),
			Math.Clamp(MacroMapSlices, 1, 32), (double)Mathf.Clamp(MacroMapRefreshSeconds, 0.05f, 2.0f));
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
