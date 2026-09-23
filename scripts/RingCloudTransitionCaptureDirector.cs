using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// Opt-in visual evidence capture for the ring-spin cloud transition. It is
/// deliberately armed only by <c>--capture-cloud-transition</c>; regular play
/// never changes its clock, viewport, or capture output.
/// </summary>
public partial class RingCloudTransitionCaptureDirector : Node
{
	[Export] public NodePath DayNightCyclePath { get; set; } = new("../RingDayNightCycle");
	[Export] public NodePath FarViewportPath { get; set; } = new("../ScaledFarViewport");
	[Export] public NodePath SunshineBridgePath { get; set; } = new("../SunshineCloudBridge");
	[Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
	[Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
	[Export(PropertyHint.Range, "1,600,1")] public int WarmupFrames { get; set; } = 120;
	[Export(PropertyHint.Range, "8,64,1")] public int TransitionCaptureCount { get; set; } = 16;
	[Export(PropertyHint.Range, "4,30,0.5")] public float TransitionSweepSeconds { get; set; } = 14.0f;

	private Node? _dayNightCycle;
	private RingScaledFarViewport? _farViewport;
	private RingSunshineCloudBridge? _sunshineBridge;
	private RingVoxelExplorerController? _explorer;
	private VoxelChunkGridManager? _grid;
	private RingCanonicalPosition _capturePosition;
	private bool _poseFrozen;
	private bool _warmupComplete;
	private bool _active;
	private int _captureIndex;
	private int _frames;
	private double _sweepElapsed;
	private readonly List<TransitionCapture> _captureSequence = new();

	private readonly record struct TransitionCapture(int Index, float CycleFraction, float YawDegrees, float PitchDegrees, string File);

	public override void _Ready()
	{
		_active = Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--capture-cloud-transition");
		if (!_active)
		{
			SetProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		ProcessPriority = 100;
		_dayNightCycle = GetNodeOrNull<Node>(DayNightCyclePath);
		_farViewport = GetNodeOrNull<RingScaledFarViewport>(FarViewportPath);
		_sunshineBridge = GetNodeOrNull<RingSunshineCloudBridge>(SunshineBridgePath);
		_explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
		_grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
		if (_dayNightCycle == null || _farViewport == null || _sunshineBridge == null || _explorer == null || _grid?.TerrainSampler == null)
		{
			Fail("ring day/night, far-clipmap, Sunshine bridge, Explorer, or grid node is unavailable");
			return;
		}
		if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
		{
			Fail("headless display server cannot produce cloud-transition screenshots");
			return;
		}

		// Match the certified-scale location visible in the supplied recording
		// instead of the small integration scene's default ocean spawn.
		if (_grid.GetCanonicalRingCircumferenceMeters64() > 1_000_000.0)
		{
			double around = 25_114_100.0 % _grid.GetCanonicalRingCircumferenceMeters64();
			double across = Math.Clamp(238_400.0, 0.0, _grid.RingAcrossWidthMeters);
			int surface = _grid.CaptureLodSurfaceEditSnapshot().GetSurfaceHeight(
				_grid.TerrainSampler,
				(long)Math.Round(around),
				Mathf.RoundToInt((float)across));
			_capturePosition = new RingCanonicalPosition(around, surface + 1.05, across);
			_explorer.SetBenchmarkCanonicalPosition(_capturePosition);
			_poseFrozen = true;
		}

		// This is intentionally the ring clock only: it neither changes
		// Engine.TimeScale nor moves the streamed terrain/player. At 100x a
		// 1,200-second ring day lasts 12 seconds, so this 14-second sweep
		// necessarily crosses day, both terminators, and night.
		_explorer.SetPresentationView(-150.0f, -34.0f);
		_dayNightCycle.Call("set_speed_multiplier", 100.0f);
		GD.Print("CLOUD_TRANSITION_CAPTURE=START mode=moving_view speed=100x captures=" + TransitionCaptureCount);
	}

	public override void _Process(double delta)
	{
		if (!_active || _dayNightCycle == null) return;
		if (++_frames > 2400)
		{
			Fail("timed out waiting for rendered cloud transition frames");
			return;
		}
		if (Engine.GetFramesDrawn() <= 0) return;

		// Keep the clock advancing normally at its diagnostic-only 100x
		// multiplier. Do not pin its fraction: this run deliberately tests
		// continuous day/sunset/night transitions during a moving camera view.
		_dayNightCycle.Call("set_speed_multiplier", 100.0f);
		if (!_poseFrozen)
		{
			if (_frames < Math.Max(1, WarmupFrames)) return;
			_capturePosition = _explorer!.CanonicalPosition;
			_explorer.SetBenchmarkCanonicalPosition(_capturePosition);
			_poseFrozen = true;
		}
		else
		{
			// Benchmark ownership keeps physics, streaming focus, and the
			// camera-relative far viewport anchored to the identical pose for
			// every capture in the transition sweep.
			_explorer!.SetBenchmarkCanonicalPosition(_capturePosition);
		}
		if (!_warmupComplete)
		{
			if (_frames < Math.Max(1, WarmupFrames)) return;
			_warmupComplete = true;
		}
		float duration = Mathf.Max(4.0f, TransitionSweepSeconds);
		int count = Mathf.Clamp(TransitionCaptureCount, 8, 64);
		_sweepElapsed += Math.Max(0.0, delta);
		float progress = Mathf.Clamp((float)(_sweepElapsed / duration), 0.0f, 1.0f);
		ApplySweepView(progress);

		// The evenly spaced captures record a complete mouse-look-like scan
		// while the sun phase remains continuous. The manifest records the
		// exact live phase and view angles for each numbered image.
		float nextCaptureProgress = _captureIndex / (float)(count - 1);
		if (progress + 0.0001f < nextCaptureProgress) return;
		float yaw = Mathf.Lerp(-150.0f, 150.0f, nextCaptureProgress);
		float pitch = Mathf.Lerp(-34.0f, 28.0f, Mathf.Sin(nextCaptureProgress * Mathf.Pi));
		_explorer!.SetPresentationView(yaw, pitch);
		float fraction = _dayNightCycle.Get("cycle_fraction").AsSingle();
		// The manifest retains phase and pose; a stable image name lets the
		// next diagnostic run replace the prior one instead of accumulating.
		string filename = $"{_captureIndex:00}_transition.png";
		if (!SaveCapture(filename)) return;
		_captureSequence.Add(new TransitionCapture(_captureIndex, fraction, yaw, pitch, filename));
		GD.Print($"CLOUD_TRANSITION_CAPTURE=FRAME index={_captureIndex} phase={fraction:0.000} yaw={yaw:0.0} pitch={pitch:0.0}");
		_captureIndex++;
		if (_captureIndex < count) return;

		RestoreRenderers();
		WriteManifest("PASS", null);
		GD.Print("CLOUD_TRANSITION_CAPTURE=PASS");
		_active = false;
		GetTree().Quit(0);
	}

	private bool SaveCapture(string filename)
	{
		Image image = GetViewport().GetTexture().GetImage();
		if (image.IsEmpty())
		{
			Fail("root viewport returned an empty image");
			return false;
		}
		string directory = RingGeneratedOutputPaths.Directory("captures/cloud_transition_capture");
		Error error = image.SavePng(Path.Combine(directory, filename));
		if (error == Error.Ok) return true;
		Fail($"could not save {filename}: {error}");
		return false;
	}

	private void Fail(string reason)
	{
		WriteManifest("FAIL", reason);
		GD.PushError($"CLOUD_TRANSITION_CAPTURE=FAIL: {reason}");
		_active = false;
		GetTree().Quit(1);
	}

	private void RestoreRenderers()
	{
		if (_farViewport != null)
		{
			_farViewport.FarCloudClipmapEnabled = true;
			_farViewport.SetFallbackCloudShellEnabled(true);
		}
		if (_sunshineBridge != null) _sunshineBridge.Enabled = true;
		_explorer?.ClearBenchmarkOverride();
	}

	private void ApplySweepView(float progress)
	{
		if (_explorer == null) return;
		// One broad deterministic yaw scan with a horizon-to-sky-and-back
		// pitch arc exercises the cloud shell from the angles a mouse look
		// would reveal without injecting input events into gameplay.
		float yaw = Mathf.Lerp(-150.0f, 150.0f, progress);
		float pitch = Mathf.Lerp(-34.0f, 28.0f, Mathf.Sin(progress * Mathf.Pi));
		_explorer.SetPresentationView(yaw, pitch);
	}

	private void WriteManifest(string status, string? error)
	{
		string directory = RingGeneratedOutputPaths.Directory("captures/cloud_transition_capture");
		var manifest = new
		{
			status,
			error,
			renderer = RenderingServer.GetCurrentRenderingMethod(),
			frames_drawn = Engine.GetFramesDrawn(),
			warmup_frames = WarmupFrames,
			clock_speed_multiplier = 100,
			sweep_seconds = TransitionSweepSeconds,
			captures = TransitionCaptureCount,
			yaw_degrees = "-150 to +150 linearly",
			pitch_degrees = "-34 + 62*sin(pi*progress)",
			clock = "continuous; no cycle fraction pinning",
			capture_sequence = _captureSequence
		};
		File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
	}
}
