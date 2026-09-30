using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// A lightweight, seamless cloud shell that follows the ring's curvature.
/// The shader supplies motion and breakup; the mesh only establishes altitude.
/// </summary>
public partial class RingCloudLayer : MeshInstance3D
{
	[Export] public bool BuildEnabled { get; set; } = true;
	[Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
	[Export] public NodePath FocusPath { get; set; } = new("../Explorer");
	[Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
	[Export(PropertyHint.Range, "100,10000,100")] public float AltitudeMeters { get; set; } = 2200.0f;
	[Export(PropertyHint.Range, "100,1000000,100")] public int RingWidthMeters { get; set; } = 20000;
	[Export(PropertyHint.Range, "64,1024,32")] public int AroundSegments { get; set; } = 256;
	[Export(PropertyHint.Range, "4,128,1")] public int AcrossSegments { get; set; } = 16;
	[ExportGroup("Far cloud appearance")]
	[Export(PropertyHint.Range, "0,1,0.01")] public float FarCoverage { get; set; } = 0.46f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float FarOpacity { get; set; } = 0.68f;
	[Export(PropertyHint.Range, "0.01,0.4,0.01")] public float FarSoftness { get; set; } = 0.20f;
	[ExportGroup("Far-field depth compression")]
	[Export] public bool EnableFarSpace { get; set; } = true;
	[Export(PropertyHint.Range, "100000,2000000,10000")] public float FarSpaceStartMeters { get; set; } = 850000.0f;
	[Export(PropertyHint.Range, "200000,2000000,10000")] public float FarSpaceRenderEndMeters { get; set; } = 1050000.0f;
	[ExportGroup("Local cloud handoff")]
	[Export] public bool LocalVolumetricHandoffEnabled { get; set; }
	[Export(PropertyHint.Range, "100,30000,100")] public float LocalHandoffStartMeters { get; set; } = 7000.0f;
	[Export(PropertyHint.Range, "1000,50000,100")] public float LocalHandoffEndMeters { get; set; } = 12000.0f;

	private VoxelChunkGridManager? _grid;
	private DirectionalLight3D? _sunLight;
	private Node3D? _focus;
	private ShaderMaterial? _material;
	private bool _benchmarkCloudLoad;
	private Vector3 _lastCameraWorldPosition = new(float.PositiveInfinity, 0.0f, 0.0f);
	private Vector3 _lastSunDirection = new(float.PositiveInfinity, 0.0f, 0.0f);
	private double _staticUniformRefreshElapsed = double.PositiveInfinity;

	public override void _Ready()
	{
		if (!BuildEnabled) return;
		_grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
		_sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
		_focus = GetNodeOrNull<Node3D>(FocusPath);
		if (_grid == null)
		{
			GD.PushWarning("RingCloudLayer could not resolve the voxel grid manager.");
			return;
		}

		CreateMaterial();
		BuildCloudShell();
	}

	public override void _Process(double delta)
	{
		// Most uniforms describe immutable ring/configuration data. Avoid
		// resubmitting them every render frame while standing still; the shader's
		// built-in time still drives cloud motion independently.
		_staticUniformRefreshElapsed += System.Math.Max(0.0, delta);
		UpdateShaderUniforms(_staticUniformRefreshElapsed >= 0.25);
	}

	private void CreateMaterial()
	{
		Shader? shader = GD.Load<Shader>("res://materials/ring_cloud_layer.gdshader");
		if (shader == null) return;
		_material = new ShaderMaterial
		{
			Shader = shader,
			RenderPriority = 10
		};
		_material.SetShaderParameter("coverage", FarCoverage);
		_material.SetShaderParameter("opacity", FarOpacity);
		_material.SetShaderParameter("softness", FarSoftness);
		if (_grid != null)
			RingAtmosphereModel.ConfigureMaterial(_material, _grid.GetCanonicalRingCircumferenceMeters64() / System.Math.Tau, 1.0f);
		MaterialOverride = _material;
		UpdateShaderUniforms(forceStaticRefresh: true);
	}

	private void BuildCloudShell()
	{
		if (_grid == null) return;

		int aroundCount = Mathf.Max(64, AroundSegments);
		int acrossCount = Mathf.Max(4, AcrossSegments);
		double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
		double aroundSpacing = circumference / aroundCount;
		double acrossSpacing = RingWidthMeters / (double)acrossCount;
		var surface = new SurfaceTool();
		surface.Begin(Mesh.PrimitiveType.Triangles);

		for (int acrossIndex = 0; acrossIndex < acrossCount; acrossIndex++)
		for (int aroundIndex = 0; aroundIndex < aroundCount; aroundIndex++)
		{
			AddVertex(surface, aroundIndex, acrossIndex + 1, aroundCount, acrossCount, aroundSpacing, acrossSpacing);
			AddVertex(surface, aroundIndex + 1, acrossIndex, aroundCount, acrossCount, aroundSpacing, acrossSpacing);
			AddVertex(surface, aroundIndex, acrossIndex, aroundCount, acrossCount, aroundSpacing, acrossSpacing);
			AddVertex(surface, aroundIndex + 1, acrossIndex, aroundCount, acrossCount, aroundSpacing, acrossSpacing);
			AddVertex(surface, aroundIndex, acrossIndex + 1, aroundCount, acrossCount, aroundSpacing, acrossSpacing);
			AddVertex(surface, aroundIndex + 1, acrossIndex + 1, aroundCount, acrossCount, aroundSpacing, acrossSpacing);
		}

		surface.Index();
		Mesh = surface.Commit();
		CastShadow = ShadowCastingSetting.Off;
		IgnoreOcclusionCulling = true;
		RenderingServer.InstanceSetIgnoreCulling(GetInstance(), true);
		GD.Print($"Ring cloud shell: altitude={AltitudeMeters:0}m, around={aroundCount}, across={acrossCount}");
	}

	private void AddVertex(
		SurfaceTool surface,
		int aroundIndex,
		int acrossIndex,
		int aroundCount,
		int acrossCount,
		double aroundSpacing,
		double acrossSpacing)
	{
		if (_grid == null) return;
		float u = aroundIndex / (float)aroundCount;
		float v = acrossIndex / (float)acrossCount;
		Vector3 world = _grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(
			aroundIndex * aroundSpacing,
			AltitudeMeters,
			acrossIndex * acrossSpacing));
		surface.SetUV(new Vector2(u, v));
		surface.AddVertex(ToLocal(world));
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
				diameter + RingWidthMeters + AltitudeMeters + 10000.0);
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
			_material.SetShaderParameter("ring_width_km", RingWidthMeters * 0.001f);
			_material.SetShaderParameter("cloud_seed", _grid.TerrainSampler?.Seed ?? 1337);
		}

		if (_sunLight == null) return;
		Vector3 sunDirection = _sunLight.GlobalTransform.Basis.Z.Normalized();
		if (_lastSunDirection.IsEqualApprox(sunDirection)) return;
		_material.SetShaderParameter("sun_direction", sunDirection);
		_lastSunDirection = sunDirection;
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
