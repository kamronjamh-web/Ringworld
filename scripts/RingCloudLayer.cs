using System.Collections.Generic;
using System;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// Main-view far cloud layer: a camera-centred curved patch (the whole ring width, PatchAroundRadiusMeters
/// around) at the middle of the far cloud band. RingScaledFarViewport binds it the whole-ring weather atlas and
/// the Sunshine density resources, so it draws the same clouds as the scaled far shell. It covers main-viewport
/// ground at altitude, where the scaled far viewport (a backdrop behind that ground) cannot show its clouds; the
/// main-view shader discards pixels without main geometry, where the far viewport's own clouds are composited.
/// RingVolumetricCloudLayer shows it while Sunshine does not own the clouds and fades it in through the
/// Sunshine altitude crossfade (SetHandoffCrossfade closes the Sunshine handoff hole).
/// </summary>
public partial class RingCloudLayer : MeshInstance3D
{
    // Build the existing shell over several frames when the launcher opts in.
    [Export] public bool UseStagedStartup { get; set; }
    private IEnumerator<bool>? _startupBuild;
    private bool _startupComplete;
    public string StartupError { get; private set; } = string.Empty;
    public bool StartupComplete => StartupError.Length == 0 && (!BuildEnabled || (!UseStagedStartup || (_startupComplete && Mesh != null)));
    public Godot.Collections.Dictionary GetLoadingStatus() => new() { ["ready"] = StartupComplete, ["phase"] = "whole ring fallback", ["error"] = StartupError };
    private void ServiceStartupGeometry()
    {
        if (_startupBuild == null) return;
        try
        {
            long start = Stopwatch.GetTimestamp();
            for (int units = 0; units < 4; units++)
            {
                if (!_startupBuild.MoveNext()) { _startupBuild.Dispose(); _startupBuild = null; _startupComplete = Mesh != null; if (!_startupComplete) FailStartup("The cloud shell did not produce a mesh."); break; }
                if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 3) break;
            }
        }
        catch (Exception error) { FailStartup(error.Message); }
    }
    private void FailStartup(string error)
    {
        if (!UseStagedStartup || StartupError.Length > 0) return;
        StartupError = "Ring cloud startup failed: " + error;
        _startupBuild?.Dispose(); _startupBuild = null;
        SetProcess(false);
    }
    public override void _ExitTree() { _startupBuild?.Dispose(); _startupBuild = null; }
    private IEnumerable<bool> EnumerateStartup()
    {
        foreach (bool unit in EnumerateBuildCloudShell()) yield return unit;
    }

	[Export] public bool BuildEnabled { get; set; } = true;
	[Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
	[Export] public NodePath FocusPath { get; set; } = new("../Explorer");
	[Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
	[Export(PropertyHint.Range, "100,10000,100")] public float AltitudeMeters { get; set; } = 2200.0f;
	[ExportGroup("Far cloud appearance")]
	[Export(PropertyHint.Range, "0,1,0.01")] public float FarCoverage { get; set; } = 0.46f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float FarOpacity { get; set; } = 0.68f;
	[Export(PropertyHint.Range, "0.01,0.4,0.01")] public float FarSoftness { get; set; } = 0.20f;
	[ExportGroup("Far-field depth compression")]
	[Export] public bool EnableFarSpace { get; set; } = true;
	[Export(PropertyHint.Range, "100000,2000000,10000")] public float FarSpaceStartMeters { get; set; } = 850000.0f;
	[Export(PropertyHint.Range, "200000,2000000,10000")] public float FarSpaceRenderEndMeters { get; set; } = 1050000.0f;
	[ExportGroup("Camera-centred patch")]
	[Export(PropertyHint.Range, "100000,5000000,10000")] public float PatchAroundRadiusMeters { get; set; } = 1500000.0f;
	[Export(PropertyHint.Range, "16,512,2")] public int PatchAroundSegments { get; set; } = 160;
	[Export(PropertyHint.Range, "4,128,1")] public int PatchAcrossSegments { get; set; } = 32;
	// The patch is recentred when the camera has moved this far from its centre (vertices are spaced
	// a few km apart near the centre, so a small shift is invisible).
	[Export(PropertyHint.Range, "1000,200000,1000")] public float PatchRecenterMeters { get; set; } = 25000.0f;
	[ExportGroup("Local cloud handoff")]
	[Export] public bool LocalVolumetricHandoffEnabled { get; set; }
	[Export(PropertyHint.Range, "100,30000,100")] public float LocalHandoffStartMeters { get; set; } = 7000.0f;
	[Export(PropertyHint.Range, "1000,50000,100")] public float LocalHandoffEndMeters { get; set; } = 12000.0f;

	private VoxelChunkGridManager? _grid;
	private DirectionalLight3D? _sunLight;
	private Node3D? _focus;
	private ShaderMaterial? _material;
	/// <summary>The shell material. RingScaledFarViewport binds the whole-ring weather atlas and the Sunshine far
	/// density resources to it, so this main-view shell draws the same clouds as the scaled far shell; without
	/// them the shader keeps the shell clear.</summary>
	public ShaderMaterial? ShellMaterial => _material;

	private bool _benchmarkCloudLoad;
	private Vector3 _lastCameraWorldPosition = new(float.PositiveInfinity, 0.0f, 0.0f);
	private Vector3 _lastSunDirection = new(float.PositiveInfinity, 0.0f, 0.0f);
	private double _staticUniformRefreshElapsed = double.PositiveInfinity;

	public override void _Ready()
	{
		if (!UseStagedStartup) { InitializeStartupOwner(); return; }
		try { InitializeStartupOwner(); }
		catch (Exception error) { FailStartup(error.Message); }
	}

	private void InitializeStartupOwner()
	{
		if (!BuildEnabled) return;
		_grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
		_sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
		_focus = GetNodeOrNull<Node3D>(FocusPath);
		if (_grid == null)
		{
			GD.PushWarning("RingCloudLayer could not resolve the voxel grid manager.");
			FailStartup("The voxel grid manager is unavailable.");
			return;
		}

		CreateMaterial();
		if (UseStagedStartup && _material == null) { FailStartup("The cloud material is unavailable."); return; }
		if (UseStagedStartup) _startupBuild = EnumerateStartup().GetEnumerator(); else BuildCloudShell();
	}

	public override void _Process(double delta)
	{
        ServiceStartupGeometry();
		// Most uniforms describe immutable ring/configuration data. Avoid
		// resubmitting them every render frame while standing still; the shader's
		// built-in time still drives cloud motion independently.
		_staticUniformRefreshElapsed += System.Math.Max(0.0, delta);
		UpdatePatch();
		UpdateShaderUniforms(_staticUniformRefreshElapsed >= 0.25);
	}

	private void CreateMaterial()
	{
		Shader? shader = GD.Load<Shader>("res://materials/ring_cloud_layer_main.gdshader");
		if (shader == null) return;
		_material = new ShaderMaterial
		{
			Shader = shader,
			RenderPriority = 10
		};
		_material.SetShaderParameter("coverage", FarCoverage);
		_material.SetShaderParameter("opacity", FarOpacity);
		_material.SetShaderParameter("softness", FarSoftness);
		// No camera-local far clipmap in the main view: only the Sunshine handoff applies.
		_material.SetShaderParameter("far_clipmap_handoff_start_meters", 0.0f);
		_material.SetShaderParameter("far_clipmap_handoff_end_meters", 1.0f);
		if (_grid != null)
			RingAtmosphereModel.ConfigureMaterial(_material, _grid.GetCanonicalRingCircumferenceMeters64() / System.Math.Tau, 1.0f);
		MaterialOverride = _material;
		UpdateShaderUniforms(forceStaticRefresh: true);
	}

	private void BuildCloudShell() { foreach (bool _ in EnumerateBuildCloudShell()) { } }

    private IEnumerable<bool> EnumerateBuildCloudShell()
	{
		if (_grid == null) yield break;
		RebuildPatch();
		yield return true;
	}

	private double _patchCenterAround = double.NaN, _patchCenterAcross;
	private float _patchAltitude = float.NaN;
	private Vector3 _patchCenterWorld;

	/// <summary>Altitude of the patch: the middle of the far cloud band the shadows and the far shell use.</summary>
	private float ResolvePatchAltitude()
	{
		Vector4 shadow = RingFarWeatherShadowGlobals.Shadow;
		return shadow.W > shadow.Z + 1.0f ? (shadow.Z + shadow.W) * 0.5f : AltitudeMeters;
	}

	private void UpdatePatch()
	{
		if (_grid == null || _focus == null || !Visible || Mesh == null) return;
		RingCanonicalPosition camera = _grid.WorldToCanonicalRingPosition64(_focus.GlobalPosition);
		double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
		double around = camera.AroundMeters - _patchCenterAround;
		around -= Math.Round(around / circumference) * circumference;
		bool moved = double.IsNaN(_patchCenterAround)
			|| Math.Abs(around) > PatchRecenterMeters
			|| Math.Abs(camera.AcrossMeters - _patchCenterAcross) > PatchRecenterMeters;
		// A floating-origin rebase moves the world under the patch.
		bool rebased = !moved && _grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(
			_patchCenterAround, _patchAltitude, _patchCenterAcross)).DistanceSquaredTo(_patchCenterWorld) > 1.0f;
		bool altitudeChanged = !Mathf.IsEqualApprox(ResolvePatchAltitude(), _patchAltitude, 1.0f);
		if (moved || rebased || altitudeChanged) RebuildPatch();
	}

	private void RebuildPatch()
	{
		if (_grid == null) return;
		RingCanonicalPosition camera = _focus != null
			? _grid.WorldToCanonicalRingPosition64(_focus.GlobalPosition)
			: new RingCanonicalPosition(0.0, 0.0, 0.0);
		double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
		double width = Math.Max(1.0, _grid.RingAcrossWidthMeters);
		double radius = Math.Min(PatchAroundRadiusMeters, circumference * 0.45);
		int aroundCount = Math.Max(16, PatchAroundSegments);
		int acrossCount = Math.Max(4, PatchAcrossSegments);
		_patchCenterAround = camera.AroundMeters;
		_patchCenterAcross = camera.AcrossMeters;
		_patchAltitude = ResolvePatchAltitude();
		RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
		var vertices = new Vector3[(aroundCount + 1) * (acrossCount + 1)];
		var uvs = new Vector2[vertices.Length];
		for (int i = 0; i <= aroundCount; i++)
		{
			// Dense near the camera (about 2.8 km at the default size), coarse at the edge (about 35 km,
			// still within 30 m of the curved ring).
			double t = i / (double)aroundCount * 2.0 - 1.0;
			double offset = radius * Math.Sign(t) * (0.15 * Math.Abs(t) + 0.85 * t * t);
			double aroundMeters = _patchCenterAround + offset;
			for (int j = 0; j <= acrossCount; j++)
			{
				double across = j / (double)acrossCount * width;
				int index = i * (acrossCount + 1) + j;
				vertices[index] = ToLocal(geometry.CanonicalToWorld(aroundMeters, _patchAltitude, across));
				// Unwrapped around UV: the shader takes fract per fragment, so a patch over the ring seam stays continuous.
				uvs[index] = new Vector2((float)(aroundMeters / circumference), (float)(across / width));
			}
		}
		var indices = new int[aroundCount * acrossCount * 6];
		int cursor = 0;
		for (int i = 0; i < aroundCount; i++)
		for (int j = 0; j < acrossCount; j++)
		{
			int a = i * (acrossCount + 1) + j, b = a + acrossCount + 1;
			indices[cursor++] = a; indices[cursor++] = b; indices[cursor++] = a + 1;
			indices[cursor++] = b; indices[cursor++] = b + 1; indices[cursor++] = a + 1;
		}
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices;
		arrays[(int)Mesh.ArrayType.TexUV] = uvs;
		arrays[(int)Mesh.ArrayType.Index] = indices;
		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		Mesh = mesh;
		_patchCenterWorld = geometry.CanonicalToWorld(_patchCenterAround, _patchAltitude, _patchCenterAcross);
		CastShadow = ShadowCastingSetting.Off;
		IgnoreOcclusionCulling = true;
		RenderingServer.InstanceSetIgnoreCulling(GetInstance(), true);
	}

	private void UpdateShaderUniforms(bool forceStaticRefresh = true)
	{
		if (_material == null || _grid == null) return;
		Vector3 cameraWorldPosition = _focus?.GlobalPosition ?? Vector3.Zero;
		if (!_lastCameraWorldPosition.IsEqualApprox(cameraWorldPosition))
		{
			_material.SetShaderParameter("camera_world_position", cameraWorldPosition);
			_lastCameraWorldPosition = cameraWorldPosition;
		}
		Vector3 atmosphereSun = _sunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
		Camera3D? activeCamera = GetViewport()?.GetCamera3D();
		RingAtmosphereRayFrame atmosphereFrame = activeCamera == null
			? RingAtmosphereRayFrame.Invalid
			: RingAtmosphereRayFrame.FromMainCamera(_grid, activeCamera, atmosphereSun);
		atmosphereFrame.Apply(_material);

		if (forceStaticRefresh)
		{
			_staticUniformRefreshElapsed = 0.0;
			_material.SetShaderParameter("ring_center", _grid.GetCanonicalRingCenterWorld());
			double diameter = _grid.GetCanonicalRingCircumferenceMeters64() / System.Math.PI;
			float realEnd = (float)System.Math.Max(
				FarSpaceStartMeters + 1.0,
				diameter + _grid.RingAcrossWidthMeters + AltitudeMeters + 10000.0);
			_material.SetShaderParameter("far_space_enabled", EnableFarSpace);
			_material.SetShaderParameter("far_space_start_meters", FarSpaceStartMeters);
			_material.SetShaderParameter("far_space_real_end_meters", realEnd);
			_material.SetShaderParameter(
				"far_space_render_end_meters",
				Mathf.Max(FarSpaceStartMeters + 1.0f, FarSpaceRenderEndMeters));
			_material.SetShaderParameter("local_volumetric_handoff_enabled", LocalVolumetricHandoffEnabled);
			_material.SetShaderParameter("local_handoff_start_meters", LocalHandoffStartMeters);
			_material.SetShaderParameter("local_handoff_end_meters", Mathf.Max(LocalHandoffStartMeters + 1.0f, LocalHandoffEndMeters));
			_material.SetShaderParameter(
				"ring_circumference_km",
				(float)(_grid.GetCanonicalRingCircumferenceMeters64() * 0.001));
			_material.SetShaderParameter("ring_width_km", _grid.RingAcrossWidthMeters * 0.001f);
		}

		if (_sunLight == null) return;
		Vector3 sunDirection = _sunLight.GlobalTransform.Basis.Z.Normalized();
		if (_lastSunDirection.IsEqualApprox(sunDirection)) return;
		_material.SetShaderParameter("sun_direction", sunDirection);
		_lastSunDirection = sunDirection;
	}

	private float _handoffCrossfade;

	/// <summary>Closes the Sunshine handoff hole (0 = hole as configured, 1 = full disk) while Sunshine fades out.</summary>
	public void SetHandoffCrossfade(float crossfade)
	{
		crossfade = Mathf.Clamp(crossfade, 0.0f, 1.0f);
		if (Mathf.IsEqualApprox(crossfade, _handoffCrossfade)) return;
		_handoffCrossfade = crossfade;
		_material?.SetShaderParameter("main_view_handoff_crossfade", _handoffCrossfade);
	}

	public void SetBenchmarkCloudLoad(bool enabled)
	{
		_benchmarkCloudLoad = enabled;
		if (_material == null) return;
		_material.SetShaderParameter("coverage", enabled ? 0.22f : FarCoverage);
		_material.SetShaderParameter("opacity", enabled ? 0.88f : FarOpacity);
		_material.SetShaderParameter("softness", enabled ? 0.24f : FarSoftness);
	}

	public void SetLocalVolumetricHandoff(bool enabled, float fadeStartMeters, float fadeEndMeters)
	{
		LocalVolumetricHandoffEnabled = enabled;
		LocalHandoffStartMeters = Mathf.Max(0.0f, fadeStartMeters);
		LocalHandoffEndMeters = Mathf.Max(LocalHandoffStartMeters + 1.0f, fadeEndMeters);
		UpdateShaderUniforms(forceStaticRefresh: true);
	}
}
