extends Node

@export var player_path: NodePath = NodePath("../Player")
@export var gravity_controller_path: NodePath = NodePath("../RingWorld/GravityController")
@export var canonical_grid_path: NodePath = NodePath("../ChunkGridManager")
@export var sun_light_path: NodePath = NodePath("../StarLight")
@export var planet_light_path: NodePath = NodePath("../PlanetLight")
@export var celestial_clock_path: NodePath = NodePath("../RingDayNightCycle")
@export var world_environment_path: NodePath = NodePath("../SpaceEnvironment")
@export var cloud_lighting_bridge_path: NodePath = NodePath("../SunshineCloudBridge")
@export var eclipse_event_path: NodePath = NodePath("../EclipseEvent")
@export var underground_environment_path: NodePath = NodePath("../UndergroundEnvironment")
@export var underground_mode_path: NodePath = NodePath("../UndergroundMode")
@export var ring_sky_shader_path: String = "res://materials/ring_local_sky.gdshader"

@export_group("Day/Night Split")
@export var day_start_dot: float = -0.2
@export var day_full_dot: float = 0.35
@export var blend_speed: float = 2.5
@export var invert_day_side: bool = false

@export_group("Sun Near-Bubble")
@export var day_sun_energy: float = 4.0
@export var night_sun_energy: float = 0.02
@export var day_shadow_distance: float = 4000.0
@export var night_shadow_distance: float = 2000.0
@export var shadow_fade_ratio: float = 0.82
@export var keep_half_ring_lit_globally: bool = true

@export_group("Grazing Eclipse Presentation")
# Geometry remains in RingDayNightCycle. Legacy presentation controls are kept
# for serialized-scene compatibility; received-light day/night is canonical.
@export var eclipse_lighting_enabled: bool = true
# Deprecated compatibility export. Canonical direct sunlight follows raw solar
# visibility and therefore reaches exact zero at totality.
@export_range(0.0, 1.0, 0.01) var eclipse_direct_darkness: float = 1.0
# Deprecated compatibility export. It no longer lifts eclipse ambient above the
# effective night baseline, so totality cannot retain a daytime ambient floor.
@export_range(0.0, 1.0, 0.01) var eclipse_ambient_floor: float = 0.25
@export var eclipse_sky_color: Color = Color(0.13, 0.19, 0.29, 1.0)
@export var eclipse_fog_color: Color = Color(0.20, 0.27, 0.36, 1.0)
@export_range(0.0, 1.0, 0.01) var eclipse_sky_tint_strength: float = 0.42
@export_range(0.0, 1.0, 0.01) var eclipse_fog_tint_strength: float = 0.28

@export_group("Ambient Blend")
@export var day_ambient_color: Color = Color(0.53, 0.58, 0.65, 1.0)
@export var night_ambient_color: Color = Color(0.34, 0.40, 0.52, 1.0)
@export var day_ambient_energy: float = 0.35
@export var night_ambient_energy: float = 0.30
@export_range(0.0, 1.0, 0.01) var ambient_sky_contribution: float = 0.35

@export_group("Underground lighting")
@export var underground_ambient_color: Color = Color(0.002, 0.003, 0.006, 1.0)
@export_range(0.0, 0.1, 0.001) var underground_ambient_energy: float = 0.003
@export_range(0.0, 1.0, 0.001) var underground_fog_sky_affect: float = 0.0
@export_range(0.0, 1.0, 0.01) var underground_fog_aerial_perspective: float = 0.0

@export_group("Cloud-filtered Daylight")
@export var cloud_filtered_lighting_enabled: bool = true
@export var storm_ambient_color: Color = Color(0.16, 0.21, 0.29, 1.0)
@export_range(0.0, 1.0, 0.01) var storm_sky_contribution_scale: float = 0.16
@export_range(0.0, 1.0, 0.01) var storm_fog_light_scale: float = 0.28
@export_range(0.0, 1.0, 0.01) var storm_fog_sun_scatter_scale: float = 0.12
@export_range(0.1, 8.0, 0.1) var cloud_lighting_blend_speed: float = 1.4

@export_group("Constrained Eye Adaptation")
@export var storm_auto_exposure_enabled: bool = true
@export_range(1.0, 800.0, 1.0) var auto_exposure_min_sensitivity: float = 50.0
@export_range(1.0, 1600.0, 1.0) var auto_exposure_max_sensitivity: float = 160.0
@export_range(0.01, 8.0, 0.01) var auto_exposure_speed: float = 0.65
@export_range(0.05, 1.0, 0.01) var auto_exposure_scale: float = 0.30

@export_group("HDR Output")
@export var use_agx_tonemapping: bool = true
@export_range(6.0, 32.0, 0.1) var agx_white: float = 16.29
@export_range(0.8, 1.5, 0.01) var agx_contrast: float = 1.20
@export_range(0.5, 1.5, 0.01) var tonemap_exposure: float = 1.0

@export_group("Sky Blend")
@export var day_sky_top_color: Color = Color(0.12, 0.28, 0.52, 1.0)
@export var day_sky_horizon_color: Color = Color(0.50, 0.60, 0.69, 1.0)
@export var day_ground_bottom_color: Color = Color(0.08, 0.12, 0.18, 1.0)
@export var day_ground_horizon_color: Color = Color(0.44, 0.54, 0.64, 1.0)
@export var night_sky_top_color: Color = Color(0.002, 0.004, 0.012, 1.0)
@export var night_sky_horizon_color: Color = Color(0.008, 0.015, 0.035, 1.0)
@export var night_ground_bottom_color: Color = Color(0.002, 0.003, 0.008, 1.0)
@export var night_ground_horizon_color: Color = Color(0.006, 0.012, 0.025, 1.0)

@export_group("Localized Atmosphere Altitude")
@export var sky_fade_start_height_meters: float = 8000.0
@export var space_black_height_meters: float = 20000.0
@export_range(100.0, 10000.0, 100.0) var ring_side_atmosphere_fade_meters: float = 1500.0
@export var altitude_blend_speed: float = 1.5
@export var space_sky_color: Color = Color(0.0, 0.0, 0.0, 1.0)
@export var space_ambient_color: Color = Color(0.002, 0.003, 0.006, 1.0)
@export var space_ambient_energy: float = 0.015

@export_group("Space Stars")
@export var stars_enabled: bool = true
@export_range(0.0, 100000.0, 100.0) var stars_fade_start_height_meters: float = 20000.0
@export_range(1000.0, 150000.0, 100.0) var stars_full_height_meters: float = 26000.0
@export_range(32.0, 1024.0, 1.0) var star_cell_density: float = 420.0
@export_range(0.0, 0.25, 0.001) var star_density: float = 0.045
@export_range(0.0, 8.0, 0.05) var star_brightness: float = 2.2
@export var star_color: Color = Color(0.76, 0.84, 1.0, 1.0)
@export var stars_visible_on_night_side: bool = true
@export_range(0.0, 1.0, 0.01) var night_surface_star_visibility: float = 0.62
@export_range(0.05, 0.8, 0.01) var night_star_twilight_end_day_factor: float = 0.35

@export_group("Opposite-Sun Planet")
@export var planet_enabled: bool = true
@export_range(0.25, 89.9, 0.05) var planet_angular_radius_degrees: float = 30.8242334
@export_range(0.0, 4.0, 0.05) var planet_brightness: float = 1.15
@export_range(0.5, 24.0, 0.25) var planet_detail_scale: float = 7.0
@export var planet_ocean_color: Color = Color(0.035, 0.13, 0.28, 1.0)
@export var planet_land_color: Color = Color(0.25, 0.34, 0.20, 1.0)
@export var planet_highland_color: Color = Color(0.48, 0.40, 0.27, 1.0)
@export var planet_cloud_color: Color = Color(0.78, 0.84, 0.88, 1.0)
@export var planet_atmosphere_color: Color = Color(0.18, 0.52, 1.0, 1.0)
@export_range(0.0, 4.0, 0.05) var planet_atmosphere_rim_brightness: float = 1.35
@export var gas_giant_light_band_color: Color = Color(0.83, 0.68, 0.45, 1.0)
@export var gas_giant_dark_band_color: Color = Color(0.38, 0.20, 0.12, 1.0)
@export var gas_giant_cream_color: Color = Color(0.94, 0.83, 0.62, 1.0)
@export var gas_giant_storm_color: Color = Color(0.78, 0.28, 0.13, 1.0)
@export_range(4.0, 32.0, 0.5) var gas_giant_band_count: float = 13.0

@export_group("Celestial Atmospheric Scattering")
@export_range(0.0, 8.0, 0.05) var exterior_day_scattering_boost: float = 1.0
@export_range(0.0, 8.0, 0.05) var exterior_night_scattering_boost: float = 1.8
@export_range(0.0, 2.0, 0.01) var atmosphere_sun_forward_scattering: float = 0.30
@export_range(0.0, 2.0, 0.01) var planet_haze_scattering_energy: float = 0.18

@export_group("Planet Reflected Light")
@export var planet_light_enabled: bool = true
@export_range(0.0, 2.0, 0.01) var planet_light_night_energy: float = 0.32
@export var planet_light_color: Color = Color(0.62, 0.72, 0.90, 1.0)
@export_range(-0.25, 0.25, 0.01) var planet_light_horizon_fade_start_dot: float = -0.02
@export_range(0.01, 0.8, 0.01) var planet_light_horizon_full_dot: float = 0.24
@export_range(0.05, 0.8, 0.01) var planet_light_twilight_end_day_factor: float = 0.32
@export_range(0.0, 1.0, 0.01) var planet_light_specular: float = 0.45
@export var planet_light_cast_shadows: bool = true
@export_range(0.0, 5.0, 0.05) var planet_light_angular_distance_degrees: float = 0.50
@export_range(100.0, 25000.0, 100.0) var planet_light_shadow_distance_meters: float = 6000.0
@export_range(2, 4, 2) var planet_light_shadow_cascades: int = 4
@export_range(0.5, 1.0, 0.01) var planet_light_shadow_fade_ratio: float = 0.85

@export_group("Opposite Side Visibility")
@export var preserve_opposite_side_visibility: bool = true
@export var day_side_opposite_visibility: float = 0.2
@export var night_side_opposite_visibility: float = 0.35

@export_group("Day Fog")
# This is deliberately only a thin background veil. Terrain, hull, and cloud
# shaders integrate the shared curved ring-local atmosphere, so the central
# vacuum adds no fog and the opposite side remains visible.
@export var day_fog_density: float = 0.000004
@export var day_fog_color: Color = Color(0.50, 0.60, 0.69, 1.0)
@export var day_fog_energy: float = 0.85
@export var day_fog_aerial_perspective: float = 0.82
@export var day_fog_sky_affect: float = 0.62
@export var day_fog_sun_scatter: float = 0.16
@export var enable_global_depth_fog: bool = false

@export_group("Night Fog")
@export var night_fog_density: float = 0.000002
@export var night_fog_color: Color = Color(0.04, 0.07, 0.12, 1.0)
@export var night_fog_energy: float = 0.38
@export var night_fog_aerial_perspective: float = 0.2
@export var night_fog_sky_affect: float = 0.18
@export var night_fog_sun_scatter: float = 0.04

@export_group("Underwater")
@export var underwater_fog_color: Color = Color(0.008, 0.115, 0.145, 1.0)
@export var underwater_deep_fog_color: Color = Color(0.002, 0.028, 0.050, 1.0)
@export_range(0.001, 0.2, 0.001) var underwater_fog_density: float = 0.010
@export_range(0.0, 0.02, 0.0001) var underwater_depth_density_gain: float = 0.00022
@export_range(0.005, 0.2, 0.001) var underwater_max_fog_density: float = 0.032
@export_range(1.0, 250.0, 1.0) var underwater_optical_depth_meters: float = 45.0
@export_range(0.0, 1.0, 0.01) var underwater_fog_energy: float = 0.56
@export_range(0.0, 1.0, 0.01) var underwater_deep_fog_energy: float = 0.20
@export_range(0.0, 1.0, 0.01) var underwater_fog_sky_affect: float = 0.82
@export_range(0.0, 0.25, 0.005) var underwater_sun_scatter: float = 0.015
@export_range(0.0, 1.0, 0.01) var underwater_ambient_energy: float = 0.30
@export_range(0.0, 1.0, 0.01) var underwater_deep_ambient_energy: float = 0.07
@export var underwater_ambient_color: Color = Color(0.018, 0.155, 0.175, 1.0)
@export var underwater_deep_ambient_color: Color = Color(0.004, 0.042, 0.068, 1.0)
@export var underwater_sun_color: Color = Color(0.48, 0.82, 0.86, 1.0)
@export var underwater_deep_sun_color: Color = Color(0.16, 0.44, 0.58, 1.0)
@export_range(0.0, 1.0, 0.01) var underwater_sun_energy_scale: float = 0.52
@export_range(0.0, 1.0, 0.01) var underwater_deep_sun_energy_scale: float = 0.10
@export_range(0.1, 20.0, 0.1) var underwater_transition_speed: float = 6.0

@export_group("Underwater No-Goggles View")
@export var underwater_view_shader_path: String = "res://materials/underwater_no_goggles.gdshader"
@export_range(0.0, 4.0, 0.05) var underwater_focus_blur_lod: float = 1.15
@export_range(0.0, 1.0, 0.01) var underwater_view_tint_strength: float = 0.24
@export_range(0.0, 1.0, 0.01) var underwater_red_absorption: float = 0.30

var player: Node3D = null
var gravity_controller = null
var canonical_grid = null
var sun_light: DirectionalLight3D = null
var planet_light: DirectionalLight3D = null
var celestial_clock: Node = null
var world_environment: WorldEnvironment = null
var cloud_lighting_bridge: Node = null
var eclipse_event: Node = null
var underground_environment: Node = null
var underground_mode: Node = null
# Final darkening (1 - sky access) applied to sun, planet light and sky this frame. The underground mode
# controller reads it next frame and only hides shadow-casting surface LODs once it is fully dark.
var underground_lighting_darkness: float = 0.0
var ring_sky_material: ShaderMaterial = null
# Change-only write caches (last value written per name); cleared when the owning object changes.
var _sky_param_cache: Dictionary = {}
var _sky_param_cache_owner: ShaderMaterial = null
var _underwater_param_cache: Dictionary = {}
var _underwater_param_cache_owner: ShaderMaterial = null
var _output_write_cache: Dictionary = {}
var _output_write_owner: Object = null
var _eye_write_cache: Dictionary = {}
var _eye_write_owner: Object = null
var _canonical_provider_player: Node3D = null
var _canonical_provider_grid: Node = null
var _canonical_provider_cached: bool = false
# Public render-facing daylight: geometric daylight after raw solar occlusion.
# Keep the astronomical value separately so eclipse presentation never changes
# RingDayNightCycle's orbit/timing contract.
var current_day_factor: float = 0.0
var current_geometric_day_factor: float = 0.0
var current_solar_visibility: float = 1.0
var current_atmosphere_factor: float = 1.0
var current_underwater_factor: float = 0.0
var underwater_view_layer: CanvasLayer = null
var underwater_view_rect: ColorRect = null
var underwater_view_material: ShaderMaterial = null
var surface_sun_color: Color = Color.WHITE
var surface_sun_color_captured: bool = false
var underwater_optics_test_reported: bool = false
var current_cloud_ambient_transmittance: float = 1.0
var current_eclipse_coverage: float = 0.0
var current_gameplay_eclipse_darkness: float = 0.0
var current_giant_direction: Vector3 = Vector3.ZERO
var current_giant_angular_radius_degrees: float = 0.0
# Pitch-black eclipse (spec D.1). Neutral values leave every lighting term exactly as before; the EclipseEvent
# director owns the darkness model and these are its per-frame outputs.
var eclipse_darkness_weight: float = 0.0
var eclipse_ambient_multiplier: float = 1.0
var eclipse_atmosphere_night_multiplier: float = 1.0
var eclipse_sky_night_multiplier: float = 1.0
var eclipse_cloud_ambient_multiplier: float = 1.0
var eclipse_star_floor: float = 0.0
# Eclipse hub sun ("noon everywhere", spec C). Published by the EclipseEvent director each frame; exactly 0 outside the event, where every
# term below leaves the lighting untouched. The two shader globals are ringworld_hub_day and ringworld_hub_light_weight.
var current_hub_day: float = 0.0
# The part of current_day_factor that comes from the real sun. The eclipse (partial-phase) sky and fog tints belong to the real sun's
# dimming, so they use this and not the hub-lit day.
var current_real_sun_day_factor: float = 0.0
var current_hub_weight: float = 0.0
var current_hub_direct_energy: float = 0.0
var current_hub_level: float = 0.0
var current_hub_color: Color = Color(0.92, 0.97, 1.0, 1.0)
var current_hub_sky_tint_strength: float = 0.0
var current_hub_halo_intensity: float = 0.0
var current_hub_in_scatter: float = 0.0
var _published_hub_day: float = 0.0
var _published_haze_sun: Vector3 = Vector3(-1.0, -1.0, -1.0)
var _published_air_sun_visibility: float = -1.0
var _published_haze_ambient_day: Vector3 = Vector3(-1.0, -1.0, -1.0)
var _published_haze_ambient_night: Vector3 = Vector3(-1.0, -1.0, -1.0)
var _haze_bridge_pending: bool = false
# Model-derived haze ambient (RingHazeSkyIrradiance); recomputed only when its inputs move (see _publish_haze_lighting).
var _haze_sky: RefCounted = null
var _haze_sky_load_failed: bool = false
# Sky-view LUT host (RingAtmosphereLuts, P7-1): caches ring_haze_eval_sky for ring_local_sky; see _update_sky_haze_lut.
var _atmosphere_luts: RefCounted = null
var _atmosphere_luts_load_failed: bool = false
var _haze_cache_valid: bool = false
var _haze_cache_up_dot: float = 0.0
var _haze_cache_giant_dot: float = 0.0
var _haze_cache_height: float = 0.0
var _haze_cache_revision: int = -1
var _haze_cache_sun_e: Vector3 = Vector3.ZERO
var _haze_cache_planet_e: Vector3 = Vector3.ZERO
var _haze_cache_albedo: float = -1.0
var _haze_cache_gain: float = -1.0
var _haze_cache_storm_revision: int = -1
var _haze_ambient_day_raw: Vector3 = Vector3.ZERO
var _haze_ambient_planet: Vector3 = Vector3.ZERO
var _haze_lup_day: Vector3 = Vector3.ZERO
var _haze_lup_planet: Vector3 = Vector3.ZERO
var _published_haze_lup_day: Vector3 = Vector3(-1.0, -1.0, -1.0)
var _published_haze_lup_night: Vector3 = Vector3(-1.0, -1.0, -1.0)
var _published_hub_weight: float = 0.0
@export var hub_sky_tint_color: Color = Color(0.85, 0.95, 1.0, 1.0)

@export_group("Physical Haze")
# Inputs of the physical haze model (HazeModel = Physical). Brightness scales the published sun irradiance; the tint is a per-channel
# multiplier on it (sky, objects and clouds share the published lighting, so they stay consistent). Ground albedo and multi-scatter gain
# shape the model-derived haze ambient; the night floor is the isotropic ambient with no giant light (starlight, airglow).
@export_range(0.0, 4.0, 0.01) var haze_brightness: float = 0.52
@export var haze_sky_tint: Color = Color(1.0, 1.0, 1.01, 1.0)
@export_range(0.0, 1.0, 0.01) var haze_ground_albedo: float = 0.11
@export_range(0.0, 2.0, 0.01) var haze_multiscatter_gain: float = 0.99
@export var haze_night_floor: Color = Color(0.004, 0.005, 0.008, 1.0)
# P9 cloud feedback. Strength of the cloud shadow on the haze's sun term (0 = clear-sky haze always, 1 = full effect). The signal is the
# Sunshine bridge's camera direct-sun transmittance (cloud shadow at the camera, already smoothed there), smoothed again here at
# cloud_lighting_blend_speed. current_air_sun_visibility scales the published haze sun irradiance only (never ambient or transmittance).
@export_range(0.0, 1.0, 0.01) var haze_cloud_shadowing: float = 1.0
var current_air_sun_visibility: float = 1.0
# P9 storm grey-Mie haze. haze_storm_strength is the Atmosphere-tab gain on the bridge's storm factor; current_haze_storm (smoothed like the
# air sun visibility) is pushed to RingAtmosphereModel through the sky helper. A storm also removes up to STORM_SUN_OCCLUSION of the direct
# sun in the air (same value as RingAtmosphereModel.StormSunOcclusion). Physical model only; 0 is bit-identical to clear weather.
const STORM_SUN_OCCLUSION := 0.85
@export_range(0.0, 1.0, 0.01) var haze_storm_strength: float = 1.0
var current_haze_storm: float = 0.0


func _ready() -> void:
	_publish_solar_visibility(1.0)
	_resolve_references()
	_capture_surface_sun_color()
	_ensure_underwater_view_effect()
	if world_environment != null and world_environment.environment != null:
		_configure_hdr_output(world_environment.environment)
		_configure_eye_adaptation()
		world_environment.environment.ambient_light_sky_contribution = clampf(
			ambient_sky_contribution,
			0.0,
			1.0)
	if "--certified-smoke-test" in OS.get_cmdline_user_args():
		var midpoint := (sky_fade_start_height_meters + space_black_height_meters) * 0.5
		var surface_factor := _atmosphere_factor_for_height(0.0)
		var midpoint_factor := _atmosphere_factor_for_height(midpoint)
		var space_factor := _atmosphere_factor_for_height(space_black_height_meters)
		if surface_factor < 0.999 or midpoint_factor < 0.45 or midpoint_factor > 0.55 or space_factor > 0.001:
			push_error("Altitude sky atmosphere self-test failed: surface=%.3f midpoint=%.3f space=%.3f" % [surface_factor, midpoint_factor, space_factor])
		else:
			print("Altitude sky atmosphere self-test passed: fade=%.0f-%.0fm, surface=%.3f midpoint=%.3f space=%.3f" % [sky_fade_start_height_meters, space_black_height_meters, surface_factor, midpoint_factor, space_factor])
	if "--certified-smoke-test" in OS.get_cmdline_user_args() or "--underground-environment-smoke-test" in OS.get_cmdline_user_args():
		_run_ring_interior_boundary_smoke_test()


func _process(delta: float) -> void:
	underground_lighting_darkness = 0.0
	_resolve_references()

	if player == null or sun_light == null or world_environment == null:
		_publish_solar_visibility(1.0)
		_publish_night_light_scale(1.0)
		_publish_hub_light(0.0, 0.0)
		return
	if not _has_canonical_atmosphere_provider() and gravity_controller == null:
		_publish_solar_visibility(1.0)
		_publish_night_light_scale(1.0)
		_publish_hub_light(0.0, 0.0)
		return

	var environment: Environment = world_environment.environment
	if environment == null:
		_publish_solar_visibility(1.0)
		_publish_night_light_scale(1.0)
		_publish_hub_light(0.0, 0.0)
		return
	_configure_hdr_output(environment)
	_configure_eye_adaptation()
	_ensure_ring_sky(environment)

	# Day side factor is based on local ring surface up and sun direction.
	var surface_up := _get_surface_up()
	var light_ray_direction: Vector3 = -sun_light.global_transform.basis.z.normalized()
	var to_sun: Vector3 = -light_ray_direction
	if celestial_clock != null:
		to_sun = _get_celestial_value("ToSun", "to_sun", to_sun) as Vector3
	var ndotl: float = surface_up.dot(to_sun)

	if invert_day_side:
		ndotl = -ndotl

	var target_day_factor: float = smoothstep(day_start_dot, day_full_dot, ndotl)
	if celestial_clock != null:
		target_day_factor = float(_get_celestial_value("DaylightFactor", "daylight_factor", target_day_factor))
	# Smooth the geometric day/night transition first. Solar visibility is applied
	# afterwards so an exact totality is immediately and exactly night, including
	# during preview/scrub jumps.
	current_geometric_day_factor = lerpf(
		current_geometric_day_factor,
		target_day_factor,
		minf(1.0, blend_speed * delta))
	_evaluate_giant(to_sun)
	current_eclipse_coverage = clampf(float(_get_celestial_value(
			"GiantEclipseCoverage", "giant_eclipse_coverage", 0.0)), 0.0, 1.0) if eclipse_lighting_enabled else 0.0
	_publish_solar_visibility(1.0 - current_eclipse_coverage)
	_update_eclipse_darkness(delta)
	_update_hub_light()
	# The hub sun lights every point from its zenith: its day is the same everywhere and never routes through the blend lerp above
	# (the timeline is already smooth). max(x, 0) is x, so the day factor is unchanged while the hub is off.
	var real_sun_day_factor := current_geometric_day_factor * current_solar_visibility
	current_day_factor = maxf(real_sun_day_factor, current_hub_day)
	current_real_sun_day_factor = real_sun_day_factor
	current_gameplay_eclipse_darkness = float(_get_celestial_value(
			"GameplayEclipseDarkness", "gameplay_eclipse_darkness", current_eclipse_coverage)) if eclipse_lighting_enabled else 0.0
	current_giant_direction = _get_celestial_value("GiantDirection", "giant_direction", -to_sun) as Vector3
	current_giant_angular_radius_degrees = float(_get_celestial_value(
		"GiantAngularRadiusDegrees", "giant_angular_radius_degrees", planet_angular_radius_degrees))
	_update_cloud_day_factor(current_day_factor)
	var player_height := _get_player_height_meters()
	var ring_interior_factor := _structural_shell_atmosphere_factor()
	var target_atmosphere_factor := _atmosphere_factor_for_height(player_height) * ring_interior_factor
	var star_visibility := _star_visibility_for_height_and_day(player_height, current_day_factor)
	star_visibility = maxf(star_visibility, 1.0 - ring_interior_factor)
	# Totality: the sky is black and the stars come out fully (floor is 0 outside an eclipse).
	star_visibility = maxf(star_visibility, eclipse_star_floor)
	# Exterior space must take effect this frame. Blending here would retain the
	# interior atmosphere beyond a wall for several frames.
	if ring_interior_factor <= 0.0:
		current_atmosphere_factor = 0.0
	else:
		current_atmosphere_factor = lerpf(
			current_atmosphere_factor,
			target_atmosphere_factor,
			minf(1.0, maxf(0.01, altitude_blend_speed) * delta))
	var target_cloud_ambient := _get_cloud_ambient_transmittance()
	current_cloud_ambient_transmittance = lerpf(
		current_cloud_ambient_transmittance,
		target_cloud_ambient,
		1.0 - exp(-maxf(0.1, cloud_lighting_blend_speed) * delta))
	var cloud_storm_factor := 1.0 - current_cloud_ambient_transmittance
	current_air_sun_visibility = lerpf(
		current_air_sun_visibility,
		_get_air_sun_visibility_target(),
		1.0 - exp(-maxf(0.1, cloud_lighting_blend_speed) * delta))
	var haze_storm_target := _get_haze_storm_target()
	current_haze_storm = lerpf(
		current_haze_storm,
		haze_storm_target,
		1.0 - exp(-maxf(0.1, cloud_lighting_blend_speed) * delta))
	if haze_storm_target <= 0.0 and current_haze_storm < 0.001:
		current_haze_storm = 0.0

	# Keep global hemisphere lighting stable: day/night on the ring should not depend on player position.
	# Atmosphere owns dynamic daylight presentation. A graphics-quality controller,
	# when present, owns the static sun-shadow settings so its preset and live-menu
	# choices are not rewritten every frame.
	_capture_surface_sun_color()
	sun_light.light_color = surface_sun_color
	if current_hub_weight > 0.0:
		sun_light.light_color = surface_sun_color.lerp(current_hub_color, clampf(current_hub_weight, 0.0, 1.0))
	var quality_owns_sun_shadows := sun_light.has_meta("ringworld_shadow_quality_owned") \
		and bool(sun_light.get_meta("ringworld_shadow_quality_owned"))
	if not quality_owns_sun_shadows:
		sun_light.shadow_enabled = true
		sun_light.light_angular_distance = 0.0
	var shadow_distance: float = day_shadow_distance
	# Eclipse geometry is global: direct sunlight must vanish at totality even
	# while the globally lit ring hemisphere is being rendered.
	var eclipse_day_coverage := current_gameplay_eclipse_darkness * current_real_sun_day_factor
	var eclipse_direct_factor := current_solar_visibility
	if keep_half_ring_lit_globally:
		sun_light.light_energy = maxf(day_sun_energy, 0.0) * eclipse_direct_factor
	else:
		sun_light.light_energy = lerpf(night_sun_energy, day_sun_energy, current_day_factor) * eclipse_direct_factor
		shadow_distance = lerpf(night_shadow_distance, day_shadow_distance, current_day_factor)
	# Hub sun: the primary light carries the larger of the real sun's energy and the hub's (the real sun is eclipsed while the hub is lit).
	if current_hub_direct_energy > sun_light.light_energy:
		sun_light.light_energy = current_hub_direct_energy

	if not quality_owns_sun_shadows:
		shadow_distance = maxf(shadow_distance, 100.0)
		sun_light.set("directional_shadow_max_distance", shadow_distance)
		sun_light.set("directional_shadow_fade_start", clampf(shadow_fade_ratio, 0.0, 1.0))
	_update_planet_light(surface_up, current_giant_direction, to_sun, current_day_factor)

	var effective_day_density: float = day_fog_density
	var effective_night_density: float = night_fog_density
	if preserve_opposite_side_visibility:
		effective_day_density = _cap_density_for_opposite_visibility(day_fog_density, day_side_opposite_visibility)
		effective_night_density = _cap_density_for_opposite_visibility(night_fog_density, night_side_opposite_visibility)

	var haze_enabled := bool(ProjectSettings.get_setting("ringworld/runtime/haze_enabled", true))
	# The far ring is rendered in a scaled secondary viewport. Ordinary depth
	# fog cannot produce the same physical distance in both views, so it remains
	# an optional diagnostic layer; the curved air-shell shaders own normal haze.
	environment.fog_enabled = haze_enabled and enable_global_depth_fog
	environment.fog_density = lerpf(effective_night_density, effective_day_density, current_day_factor) * current_atmosphere_factor
	var eclipse_fog_factor := eclipse_day_coverage * clampf(eclipse_fog_tint_strength, 0.0, 1.0)
	environment.fog_light_color = night_fog_color.lerp(day_fog_color, current_day_factor).lerp(
		storm_ambient_color,
		cloud_storm_factor * 0.72).lerp(eclipse_fog_color, eclipse_fog_factor)
	environment.fog_light_energy = lerpf(night_fog_energy, day_fog_energy, current_day_factor) \
		* lerpf(1.0, storm_fog_light_scale, cloud_storm_factor) \
		* lerpf(1.0, 0.78, eclipse_day_coverage) \
		* eclipse_ambient_multiplier
	environment.fog_aerial_perspective = lerpf(night_fog_aerial_perspective, day_fog_aerial_perspective, current_day_factor)
	environment.fog_sky_affect = lerpf(night_fog_sky_affect, day_fog_sky_affect, current_day_factor) * current_atmosphere_factor
	environment.fog_sun_scatter = lerpf(night_fog_sun_scatter, day_fog_sun_scatter, current_day_factor) \
		* lerpf(1.0, storm_fog_sun_scatter_scale, cloud_storm_factor) \
		* eclipse_direct_factor
	var surface_ambient_color := night_ambient_color.lerp(day_ambient_color, current_day_factor).lerp(
		storm_ambient_color,
		cloud_storm_factor * 0.88)
	var surface_ambient_energy := lerpf(night_ambient_energy, day_ambient_energy, current_day_factor) \
		* current_cloud_ambient_transmittance \
		* eclipse_ambient_multiplier
	environment.ambient_light_color = space_ambient_color.lerp(surface_ambient_color, current_atmosphere_factor)
	environment.ambient_light_energy = lerpf(space_ambient_energy, surface_ambient_energy, current_atmosphere_factor)
	_publish_haze_lighting(surface_up, to_sun)
	# A contribution of 1.0 makes Godot ignore the explicit ambient color and
	# energy above. Blend a restrained sky irradiance term with those controls
	# so the visible sun disk and bright horizon do not flatten every shadow.
	environment.ambient_light_sky_contribution = clampf(
		ambient_sky_contribution * lerpf(1.0, storm_sky_contribution_scale, cloud_storm_factor),
		0.0,
		1.0)
	var camera_underwater := player.has_meta("ring_camera_underwater") \
		and bool(player.get_meta("ring_camera_underwater", false))
	var underwater_depth := maxf(0.0, float(player.get_meta("ring_water_depth_meters", 0.0)))
	var underwater_target := 1.0 if camera_underwater else 0.0
	current_underwater_factor = move_toward(
		current_underwater_factor,
		underwater_target,
		maxf(0.1, underwater_transition_speed) * delta)
	var underwater_depth_factor := 1.0 - exp(
		-underwater_depth / maxf(1.0, underwater_optical_depth_meters))
	if current_underwater_factor > 0.001:
		_apply_underwater_environment(
			environment,
			underwater_depth,
			underwater_depth_factor,
			current_underwater_factor)
	_update_underwater_view_effect(current_underwater_factor, underwater_depth_factor)
	# The C# controller only reports canonical, density-backed roof access.
	# This script owns Environment and directional-light color/energy; the clock
	# owns sun direction and graphics quality owns its static shadow settings.
	var underground_sky_access := _get_underground_sky_access()
	_apply_underground_lighting(environment, underground_sky_access, _get_enclosure_blend() * _get_structural_sky_access())
	underground_lighting_darkness = 1.0 - clampf(underground_sky_access, 0.0, 1.0)
	var cave_fog_weight := _get_enclosure_blend() * _get_structural_sky_access() * (1.0 - current_underwater_factor) * _underground_cave_fog_weight()
	_apply_underground_fog_coupling(environment, cave_fog_weight, _underground_cave_fog_density())
	_publish_enclosed_cloud_fog(environment, cave_fog_weight)
	star_visibility *= 1.0 - current_underwater_factor
	star_visibility *= underground_sky_access
	_update_sky(
		environment,
		current_day_factor,
		current_atmosphere_factor * underground_sky_access,
		star_visibility,
		surface_up,
		to_sun,
		current_giant_direction)


func _apply_underground_lighting(environment: Environment, sky_access: float, cave_weight: float = 0.0) -> void:
	var access := clampf(sky_access, 0.0, 1.0)
	var attributes := world_environment.camera_attributes as CameraAttributesPractical \
		if world_environment != null else null
	if access >= 0.999:
		return
	# Decision D2: designed caves keep a faint, tunable ambient and fog colour so large shapes read as
	# silhouettes. The weight is the enclosure blend, zero inside the structural cavity so its near-black
	# lighting contract is unchanged.
	var cave_mix := clampf(cave_weight, 0.0, 1.0)
	var cave_ambient := _underground_cave_ambient_color()
	var dark_ambient_color := underground_ambient_color.lerp(cave_ambient, cave_mix)
	var dark_ambient_energy := lerpf(underground_ambient_energy, _underground_cave_ambient_energy(), cave_mix)
	environment.ambient_light_color = dark_ambient_color.lerp(
		environment.ambient_light_color, access)
	environment.ambient_light_energy = lerpf(
		dark_ambient_energy, environment.ambient_light_energy, access)
	environment.ambient_light_sky_contribution *= access
	var cave_fog_mix := cave_mix * (1.0 - access)
	environment.fog_light_color = environment.fog_light_color.lerp(_underground_cave_fog_color(), cave_fog_mix)
	environment.fog_sky_affect = lerpf(
		underground_fog_sky_affect, environment.fog_sky_affect, access)
	environment.fog_sun_scatter *= access
	environment.fog_light_energy *= access
	environment.fog_light_energy = lerpf(environment.fog_light_energy, 1.0, cave_fog_mix)
	environment.fog_aerial_perspective *= access
	if sun_light != null:
		sun_light.light_energy *= access
	if planet_light != null:
		planet_light.light_energy *= access
		# A fully darkened planet light needs no shadow-map pass; the next frame re-evaluates it from scratch.
		if planet_light.light_energy <= 0.001:
			planet_light.shadow_enabled = false
	if attributes != null:
		# Disable adaptation in darkness, then tightly bound it through the
		# opening transition instead of allowing a sudden jump to surface ISO.
		var adaptation_access := smoothstep(0.15, 1.0, access)
		attributes.auto_exposure_enabled = storm_auto_exposure_enabled and adaptation_access > 0.0
		attributes.auto_exposure_max_sensitivity = lerpf(
			attributes.auto_exposure_min_sensitivity,
			maxf(attributes.auto_exposure_min_sensitivity, auto_exposure_max_sensitivity),
			adaptation_access)
		attributes.auto_exposure_scale = lerpf(
			minf(0.10, auto_exposure_scale),
			auto_exposure_scale,
			adaptation_access)


# Deep caves only: the controller publishes a zero weight unless its coarse underground latch is on. Surface depth
# fog is off by default, so a fog-off baseline counts as zero density and zero sky tint rather than the stored values.
func _apply_underground_fog_coupling(environment: Environment, weight: float, cave_density: float) -> void:
	var w := clampf(weight, 0.0, 1.0)
	if w <= 0.0 or cave_density <= 0.0:
		return
	var base_on := environment.fog_enabled
	var base_density := environment.fog_density if base_on else 0.0
	var base_sky := environment.fog_sky_affect if base_on else 0.0
	environment.fog_enabled = true
	environment.fog_density = lerpf(base_density, maxf(base_density, cave_density), w)
	environment.fog_sky_affect = lerpf(base_sky, underground_fog_sky_affect, w)
	environment.fog_aerial_perspective = lerpf(environment.fog_aerial_perspective, underground_fog_aerial_perspective, w)
	environment.fog_light_color = environment.fog_light_color.lerp(_underground_cave_fog_color(), w)
	environment.fog_light_energy = lerpf(environment.fog_light_energy, 1.0, w)
	environment.fog_sun_scatter = lerpf(environment.fog_sun_scatter, 0.0, w)


# Sunshine clouds are composited after Godot fog, so in caves and underwater the bridge fades them by the same exp fog at cloud depth.
# Reads the final Environment values (cave coupling and water already applied); off whenever the Environment fog is off.
func _publish_enclosed_cloud_fog(environment: Environment, cave_fog_weight: float) -> void:
	if cloud_lighting_bridge == null or not cloud_lighting_bridge.has_method("SetEnclosedFog"):
		return
	var weight := maxf(cave_fog_weight, current_underwater_factor) if environment.fog_enabled else 0.0
	cloud_lighting_bridge.call("SetEnclosedFog", environment.fog_density, environment.fog_sky_affect, weight)


func _apply_underwater_environment(
		environment: Environment,
		depth_meters: float,
		depth_factor: float,
		transition: float) -> void:
	var clear_water_density := minf(
		maxf(underwater_fog_density, underwater_max_fog_density),
		underwater_fog_density + depth_meters * underwater_depth_density_gain)
	var water_color := underwater_fog_color.lerp(underwater_deep_fog_color, depth_factor)
	var water_fog_energy := lerpf(underwater_fog_energy, underwater_deep_fog_energy, depth_factor)
	var water_ambient_color := underwater_ambient_color.lerp(underwater_deep_ambient_color, depth_factor)
	var water_ambient_energy := lerpf(
		underwater_ambient_energy,
		underwater_deep_ambient_energy,
		depth_factor)

	environment.fog_enabled = true
	environment.fog_density = lerpf(environment.fog_density, clear_water_density, transition)
	environment.fog_light_color = environment.fog_light_color.lerp(water_color, transition)
	environment.fog_light_energy = lerpf(environment.fog_light_energy, water_fog_energy, transition)
	# Aerial perspective samples the bright ring sky and was the source of the
	# white underwater wash. Water uses its own blue-green in-scattering instead.
	environment.fog_aerial_perspective = lerpf(environment.fog_aerial_perspective, 0.0, transition)
	environment.fog_sky_affect = lerpf(environment.fog_sky_affect, underwater_fog_sky_affect, transition)
	environment.fog_sun_scatter = lerpf(
		environment.fog_sun_scatter,
		underwater_sun_scatter * current_day_factor,
		transition)
	environment.ambient_light_color = environment.ambient_light_color.lerp(water_ambient_color, transition)
	environment.ambient_light_energy = lerpf(environment.ambient_light_energy, water_ambient_energy, transition)
	environment.ambient_light_sky_contribution = lerpf(
		environment.ambient_light_sky_contribution,
		0.02,
		transition)

	_capture_surface_sun_color()
	var water_sun_color := underwater_sun_color.lerp(underwater_deep_sun_color, depth_factor)
	var water_sun_scale := lerpf(
		underwater_sun_energy_scale,
		underwater_deep_sun_energy_scale,
		depth_factor)
	sun_light.light_color = surface_sun_color.lerp(water_sun_color, transition)
	sun_light.light_energy *= lerpf(1.0, water_sun_scale, transition)
	if planet_light != null:
		planet_light.light_energy *= 1.0 - transition * 0.90
	_validate_underwater_optics(environment, clear_water_density, water_color, transition)


func _validate_underwater_optics(
		environment: Environment,
		density: float,
		water_color: Color,
		transition: float) -> void:
	if underwater_optics_test_reported \
		or transition < 0.999 \
		or "--swimming-smoke-test" not in OS.get_cmdline_user_args():
		return
	underwater_optics_test_reported = true
	var visibility_at_25m := exp(-density * 25.0)
	var density_valid := density <= maxf(underwater_fog_density, underwater_max_fog_density) + 0.0001
	var color_valid := water_color.b > water_color.r and water_color.g > water_color.r
	var aerial_valid := environment.fog_aerial_perspective < 0.001
	if not density_valid or not color_valid or not aerial_valid or visibility_at_25m < 0.40:
		push_error(
			"Underwater optics self-test failed: density=%.4f, visibility25m=%.3f, color=%s, aerial=%.3f" \
			% [density, visibility_at_25m, water_color, environment.fog_aerial_perspective])
		return
	print(
		"Underwater optics self-test passed: density=%.4f, visibility25m=%.3f, blueGreen=%s, aerialPerspective=%.3f, noGogglesBlur=%.2f." \
		% [density, visibility_at_25m, water_color, environment.fog_aerial_perspective, underwater_focus_blur_lod])


func _capture_surface_sun_color() -> void:
	if surface_sun_color_captured or sun_light == null:
		return
	surface_sun_color = sun_light.light_color
	surface_sun_color_captured = true


func _ensure_underwater_view_effect() -> void:
	if underwater_view_rect != null:
		return
	var shader := load(underwater_view_shader_path) as Shader
	if shader == null:
		push_error("Underwater no-goggles shader could not be loaded: %s" % underwater_view_shader_path)
		return
	underwater_view_material = ShaderMaterial.new()
	underwater_view_material.shader = shader
	underwater_view_layer = CanvasLayer.new()
	underwater_view_layer.name = "UnderwaterNoGogglesLayer"
	# Layer zero processes the 3D view before the gameplay HUD CanvasLayers.
	underwater_view_layer.layer = 0
	add_child(underwater_view_layer)
	underwater_view_rect = ColorRect.new()
	underwater_view_rect.name = "UnderwaterNoGogglesView"
	underwater_view_rect.mouse_filter = Control.MOUSE_FILTER_IGNORE
	underwater_view_rect.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	underwater_view_rect.material = underwater_view_material
	underwater_view_rect.visible = false
	underwater_view_layer.add_child(underwater_view_rect)


func _update_underwater_view_effect(strength: float, depth_factor: float) -> void:
	_ensure_underwater_view_effect()
	if underwater_view_rect == null or underwater_view_material == null:
		return
	underwater_view_rect.visible = strength > 0.001
	if underwater_view_material != _underwater_param_cache_owner:
		_underwater_param_cache_owner = underwater_view_material
		_underwater_param_cache.clear()
	_set_param_if_changed(underwater_view_material, _underwater_param_cache, "effect_strength", clampf(strength, 0.0, 1.0))
	_set_param_if_changed(underwater_view_material, _underwater_param_cache, "depth_factor", clampf(depth_factor, 0.0, 1.0))
	_set_param_if_changed(underwater_view_material, _underwater_param_cache, "focus_blur_lod", maxf(0.0, underwater_focus_blur_lod))
	_set_param_if_changed(underwater_view_material, _underwater_param_cache, "tint_strength", clampf(underwater_view_tint_strength, 0.0, 1.0))
	_set_param_if_changed(underwater_view_material, _underwater_param_cache, "red_absorption", clampf(underwater_red_absorption, 0.0, 1.0))


func _set_param_if_changed(material: ShaderMaterial, cache: Dictionary, param: StringName, value: Variant) -> void:
	if cache.has(param) and typeof(cache[param]) == typeof(value) and cache[param] == value:
		return
	cache[param] = value
	material.set_shader_parameter(param, value)


func _set_sky_param(param: StringName, value: Variant) -> void:
	_set_param_if_changed(ring_sky_material, _sky_param_cache, param, value)


func _ensure_ring_sky(environment: Environment) -> void:
	if ring_sky_material != null:
		return
	if environment.sky == null:
		environment.sky = Sky.new()
	var shader := load(ring_sky_shader_path) as Shader
	if shader == null:
		push_error("Ring-aware sky shader could not be loaded: %s" % ring_sky_shader_path)
		return
	ring_sky_material = ShaderMaterial.new()
	ring_sky_material.shader = shader
	environment.sky.sky_material = ring_sky_material
	# The local-up uniform changes as the player travels around the ring.
	environment.sky.process_mode = Sky.PROCESS_MODE_REALTIME
	environment.sky.radiance_size = Sky.RADIANCE_SIZE_256
	print("Ring-aware sky ready: horizon follows player surface-up.")


func _configure_hdr_output(environment: Environment) -> void:
	# AgX preserves hue and highlight detail from the deliberately strong ring
	# sunlight. The scaled far viewport copies these properties so its canvas
	# handoff uses the same output response.
	if environment != _output_write_owner:
		_output_write_owner = environment
		_output_write_cache.clear()
	var tonemap_values := {
		"tonemap_mode": Environment.TONE_MAPPER_AGX if use_agx_tonemapping else Environment.TONE_MAPPER_LINEAR,
		"tonemap_agx_white": maxf(6.0, agx_white),
		"tonemap_agx_contrast": clampf(agx_contrast, 0.8, 1.5),
		"tonemap_exposure": maxf(0.01, tonemap_exposure),
	}
	for key: String in tonemap_values:
		if _output_write_cache.has(key) and _output_write_cache[key] == tonemap_values[key]:
			continue
		_output_write_cache[key] = tonemap_values[key]
		environment.set(key, tonemap_values[key])


func _configure_eye_adaptation() -> void:
	if world_environment == null:
		return
	var attributes := world_environment.camera_attributes as CameraAttributesPractical
	if attributes == null:
		attributes = CameraAttributesPractical.new()
		world_environment.camera_attributes = attributes
	if attributes != _eye_write_owner:
		_eye_write_owner = attributes
		_eye_write_cache.clear()
	var min_sensitivity := maxf(1.0, auto_exposure_min_sensitivity)
	var eye_values := {
		"auto_exposure_enabled": storm_auto_exposure_enabled,
		"auto_exposure_min_sensitivity": min_sensitivity,
		"auto_exposure_max_sensitivity": maxf(min_sensitivity, auto_exposure_max_sensitivity),
		"auto_exposure_speed": maxf(0.01, auto_exposure_speed),
		"auto_exposure_scale": clampf(auto_exposure_scale, 0.05, 1.0),
	}
	for key: String in eye_values:
		if _eye_write_cache.has(key) and _eye_write_cache[key] == eye_values[key]:
			continue
		_eye_write_cache[key] = eye_values[key]
		attributes.set(key, eye_values[key])


func _get_cloud_ambient_transmittance() -> float:
	if not cloud_filtered_lighting_enabled or cloud_lighting_bridge == null:
		return 1.0
	if cloud_lighting_bridge.has_method("GetCameraAmbientTransmittance"):
		return clampf(
			float(cloud_lighting_bridge.call("GetCameraAmbientTransmittance")),
			0.02,
			1.0)
	if cloud_lighting_bridge.has_method("get_camera_ambient_transmittance"):
		return clampf(
			float(cloud_lighting_bridge.call("get_camera_ambient_transmittance")),
			0.02,
			1.0)
	return 1.0


# Fraction of sunlight reaching the air around the camera: the bridge's camera direct-sun transmittance, blended by haze_cloud_shadowing.
func _get_air_sun_visibility_target() -> float:
	var transmittance := 1.0
	if cloud_filtered_lighting_enabled and cloud_lighting_bridge != null:
		if cloud_lighting_bridge.has_method("GetCameraDirectSunTransmittance"):
			transmittance = clampf(float(cloud_lighting_bridge.call("GetCameraDirectSunTransmittance")), 0.0, 1.0)
		elif cloud_lighting_bridge.has_method("get_camera_direct_sun_transmittance"):
			transmittance = clampf(float(cloud_lighting_bridge.call("get_camera_direct_sun_transmittance")), 0.0, 1.0)
	return air_sun_visibility_for(transmittance, current_haze_storm)


# Air sun visibility for a camera-column cloud transmittance and a storm strength: the cloud shadow blended by haze_cloud_shadowing, capped by
# the storm's own sun occlusion. storm = 0 gives exactly the cloud-only value.
func air_sun_visibility_for(cloud_t: float, storm: float) -> float:
	return minf(lerpf(1.0, cloud_t, clampf(haze_cloud_shadowing, 0.0, 1.0)), 1.0 - clampf(storm, 0.0, 1.0) * STORM_SUN_OCCLUSION)


# Storm strength target: the bridge's local storm factor times haze_storm_strength; 0 without cloud lighting, a bridge or the Physical model.
func _get_haze_storm_target() -> float:
	if not cloud_filtered_lighting_enabled or cloud_lighting_bridge == null or _haze_sky == null:
		return 0.0
	if not cloud_lighting_bridge.has_method("GetHazeStormFactor") or not bool(_haze_sky.call("IsPhysical")):
		return 0.0
	return clampf(haze_storm_strength * float(cloud_lighting_bridge.call("GetHazeStormFactor")), 0.0, 1.0)


func _scale_rgb(color: Color, multiplier: float) -> Color:
	if multiplier == 1.0:
		return color
	return Color(color.r * multiplier, color.g * multiplier, color.b * multiplier, color.a)


# Pure-model darkness (spec D.1, scripts/Celestial/Eclipse/EclipseDarknessModel.cs): the director owns the weight, smoothing
# and per-term multipliers; this script applies them. Without a director (other scenes) everything stays neutral.
func _update_eclipse_darkness(delta: float) -> void:
	if eclipse_event != null and is_instance_valid(eclipse_event) and eclipse_event.has_method("UpdateDarkness"):
		eclipse_event.call("UpdateDarkness", current_eclipse_coverage, delta)
		eclipse_darkness_weight = float(eclipse_event.get("DarknessWeight"))
		eclipse_ambient_multiplier = float(eclipse_event.get("DarknessAmbientMultiplier"))
		eclipse_atmosphere_night_multiplier = float(eclipse_event.get("DarknessAtmosphereNightMultiplier"))
		eclipse_sky_night_multiplier = float(eclipse_event.get("DarknessSkyNightMultiplier"))
		eclipse_cloud_ambient_multiplier = float(eclipse_event.get("DarknessCloudAmbientMultiplier"))
		eclipse_star_floor = float(eclipse_event.get("DarknessStarFloor"))
	else:
		_reset_eclipse_darkness()
	_publish_night_light_scale(eclipse_atmosphere_night_multiplier)
	# The scaled far viewport blends its frozen ambient snapshot toward the live (darkened) main ambient by this weight.
	if world_environment != null:
		if eclipse_darkness_weight > 0.0 or world_environment.has_meta("ringworld_eclipse_darkness"):
			world_environment.set_meta("ringworld_eclipse_darkness", eclipse_darkness_weight)
	_update_cloud_eclipse_ambient()


func _reset_eclipse_darkness() -> void:
	eclipse_darkness_weight = 0.0
	eclipse_ambient_multiplier = 1.0
	eclipse_atmosphere_night_multiplier = 1.0
	eclipse_sky_night_multiplier = 1.0
	eclipse_cloud_ambient_multiplier = 1.0
	eclipse_star_floor = 0.0


func _update_cloud_eclipse_ambient() -> void:
	if cloud_lighting_bridge == null:
		return
	if cloud_lighting_bridge.has_method("SetEclipseDarkness"):
		cloud_lighting_bridge.call("SetEclipseDarkness", eclipse_cloud_ambient_multiplier, eclipse_atmosphere_night_multiplier)


func _update_cloud_day_factor(day_factor: float) -> void:
	if cloud_lighting_bridge == null:
		return
	# Local cloud presentation follows received daylight. Cloud-density attenuation
	# remains an independent bridge output used by the ambient blend above.
	if cloud_lighting_bridge.has_method("SetLocalDayFactor"):
		cloud_lighting_bridge.call("SetLocalDayFactor", day_factor)
	elif cloud_lighting_bridge.has_method("set_local_day_factor"):
		cloud_lighting_bridge.call("set_local_day_factor", day_factor)


func _publish_solar_visibility(visibility: float) -> void:
	current_solar_visibility = clampf(visibility, 0.0, 1.0)
	RenderingServer.global_shader_parameter_set(
		"ringworld_solar_visibility",
		current_solar_visibility)


# Radiometric inputs of the physical haze model (materials/ring_haze_shared.gdshaderinc), in the units of lit surfaces in the main
# viewport. Sun: Godot (non-physical light units) renders a matte surface at NdotL = 1 as albedo * colour * energy, whereas the haze
# model's phase functions are 4PI-normalised and want the sun's irradiance E_irr with surface radiance (albedo / PI) * E_irr, so
# E_irr = PI * colour * energy. It also carries haze_brightness, haze_sky_tint and the eclipse / hub sky tints (unit-luminance multipliers
# derived like _update_sky's palette lerps), so objects, clouds and the sky all see the same tinted light and stay convergent during events.
# Day/night, eclipse visibility and horizon are applied in the shader (ringworld_effective_solar_day).
# Ambient: ambient_light_color * ambient_light_energy multiplies the albedo in Godot, i.e. it is the radiance of a white surface, which is
# what the haze model's isotropic multi-scatter wants. It is derived from the model itself (RingHazeSkyIrradiance: the sky's own single
# scatter plus ground bounce, summed as a multi-scatter series), not from the art-boosted environment fill ambient. Day ambient comes from
# the sun, night ambient from the giant's light (live, already gated by horizon / phase / day) plus a tiny floor. Recomputed only when the
# sun / giant direction, camera height, model parameters or light energies move; published on >0.5% relative change.
# Haze sun irradiance as published to shaders (cloud term included). Public so tests build the production value.
func haze_sun_irradiance_published() -> Vector3:
	return _haze_sun_irradiance() * current_air_sun_visibility


# Haze sun irradiance before the cloud term (see _publish_haze_lighting).
func _haze_sun_irradiance() -> Vector3:
	var sun_color := Vector3(surface_sun_color.r, surface_sun_color.g, surface_sun_color.b)
	var eclipse_sky_factor := current_eclipse_coverage * current_real_sun_day_factor * clampf(eclipse_sky_tint_strength, 0.0, 1.0)
	var eclipse_mult := Vector3.ONE.lerp(_unit_luminance_tint(eclipse_sky_color), eclipse_sky_factor)
	var hub_tint := 0.0
	if current_hub_day > 0.0:
		hub_tint = current_hub_sky_tint_strength * current_hub_day * (1.0 - current_solar_visibility)
	var hub_mult := Vector3.ONE.lerp(_unit_luminance_tint(hub_sky_tint_color), hub_tint)
	var sun_tint := Vector3(haze_sky_tint.r, haze_sky_tint.g, haze_sky_tint.b)
	return sun_color * (PI * maxf(day_sun_energy, 0.0) * maxf(haze_brightness, 0.0)) * sun_tint * eclipse_mult * hub_mult


func _publish_haze_lighting(surface_up: Vector3, to_sun: Vector3) -> void:
	var sun := _haze_sun_irradiance()
	# P9: the sun term the shaders / Sunshine profile see is dimmed by the camera-column cloud visibility. The CPU ambient below keeps
	# the undimmed `sun` (the overcast sky is dimmed by current_cloud_ambient_transmittance instead, so it is not dimmed twice).
	var published_sun := sun * current_air_sun_visibility
	if absf(current_air_sun_visibility - _published_air_sun_visibility) > 0.002:
		_published_air_sun_visibility = current_air_sun_visibility
		RenderingServer.global_shader_parameter_set("ringworld_air_sun_visibility", current_air_sun_visibility)
	var planet_energy := Vector3.ZERO
	if planet_light != null:
		planet_energy = Vector3(planet_light.light_color.r, planet_light.light_color.g, planet_light.light_color.b) \
				* (PI * maxf(planet_light.light_energy, 0.0))
	if _haze_sky == null and not _haze_sky_load_failed:
		var script := load("res://scripts/Atmosphere/RingHazeSkyIrradiance.cs") as Script
		if script != null:
			_haze_sky = script.new() as RefCounted
		_haze_sky_load_failed = _haze_sky == null
	if _haze_sky != null:
		var frame := _viewer_atmosphere_frame(surface_up)
		var up_dot := surface_up.normalized().dot(to_sun.normalized())
		var giant_dot := surface_up.normalized().dot(current_giant_direction.normalized()) if current_giant_direction.length_squared() > 0.0 else 0.0
		var height := float(frame["height"])
		_haze_sky.call("SetStorm", current_haze_storm)
		var revision := int(_haze_sky.call("GetRevision"))
		var storm_revision := int(_haze_sky.call("GetStormRevision"))
		if not _haze_cache_valid \
				or absf(up_dot - _haze_cache_up_dot) > 0.002 \
				or absf(giant_dot - _haze_cache_giant_dot) > 0.002 \
				or absf(height - _haze_cache_height) > maxf(25.0, 0.02 * absf(_haze_cache_height)) \
				or revision != _haze_cache_revision \
				or storm_revision != _haze_cache_storm_revision \
				or _relative_change(sun, _haze_cache_sun_e) > 0.005 \
				or _relative_change(planet_energy, _haze_cache_planet_e) > 0.005 \
				or not is_equal_approx(haze_ground_albedo, _haze_cache_albedo) \
				or not is_equal_approx(haze_multiscatter_gain, _haze_cache_gain):
			_haze_cache_valid = true
			_haze_cache_up_dot = up_dot
			_haze_cache_giant_dot = giant_dot
			_haze_cache_height = height
			_haze_cache_revision = revision
			_haze_cache_storm_revision = storm_revision
			_haze_cache_sun_e = sun
			_haze_cache_planet_e = planet_energy
			_haze_cache_albedo = haze_ground_albedo
			_haze_cache_gain = haze_multiscatter_gain
			var radius := _get_ring_radius_meters()
			var half_width := _get_ring_width_meters() * 0.5
			var across := float(frame["across"])
			var outward: Vector3 = frame["outward"]
			var axis: Vector3 = frame["axis"]
			var sun_light_e: Vector3 = sun * (_haze_sky.call("GetSunTint") as Vector3)
			var day_result: Array = _haze_sky.call("Compute", radius, outward, axis, height, half_width + across, half_width - across,
					to_sun.normalized(), sun_light_e, haze_ground_albedo, haze_multiscatter_gain, 0)
			_haze_ambient_day_raw = day_result[3]
			_haze_lup_day = day_result[2]
			if planet_energy.length_squared() > 0.000000000001 and current_giant_direction.length_squared() > 0.0:
				var night_result: Array = _haze_sky.call("Compute", radius, outward, axis, height, half_width + across, half_width - across,
						current_giant_direction.normalized(), planet_energy, haze_ground_albedo, haze_multiscatter_gain, 0)
				_haze_ambient_planet = night_result[3]
				_haze_lup_planet = night_result[2]
			else:
				_haze_ambient_planet = Vector3.ZERO
				_haze_lup_planet = Vector3.ZERO
	var night_floor := Vector3(haze_night_floor.r, haze_night_floor.g, haze_night_floor.b) * eclipse_atmosphere_night_multiplier
	# current_cloud_ambient_transmittance stands in for the storm's sky occlusion until the cloud-feedback step (P9).
	var day_ambient := _haze_ambient_day_raw * current_cloud_ambient_transmittance
	var night_ambient := _haze_ambient_planet + night_floor
	var changed := false
	if _relative_change(published_sun, _published_haze_sun) > 0.005:
		_published_haze_sun = published_sun
		changed = true
		RenderingServer.global_shader_parameter_set("ringworld_haze_sun_irradiance", published_sun)
	if _relative_change(day_ambient, _published_haze_ambient_day) > 0.005:
		_published_haze_ambient_day = day_ambient
		changed = true
		RenderingServer.global_shader_parameter_set("ringworld_haze_ambient_day", day_ambient)
	if _relative_change(night_ambient, _published_haze_ambient_night) > 0.005:
		_published_haze_ambient_night = night_ambient
		changed = true
		RenderingServer.global_shader_parameter_set("ringworld_haze_ambient_night", night_ambient)
	# Sunshine and the far cloud mask pack the same values into their CPU-built profile (compute shaders cannot read globals).
	if (changed or _haze_bridge_pending) and cloud_lighting_bridge != null and cloud_lighting_bridge.has_method("SetHazeLighting"):
		cloud_lighting_bridge.call("SetHazeLighting", _published_haze_sun, _published_haze_ambient_day, _published_haze_ambient_night)
		_haze_bridge_pending = false
	elif changed:
		_haze_bridge_pending = true
	# Cloud ambient follows the mean sky radiance (day: sun, night: giant light + floor) when the bridge derives it from the sky.
	var lup_night := _haze_lup_planet + night_floor
	if cloud_lighting_bridge != null and cloud_lighting_bridge.has_method("SetHazeSkyRadiance") \
			and (_relative_change(_haze_lup_day, _published_haze_lup_day) > 0.005 or _relative_change(lup_night, _published_haze_lup_night) > 0.005):
		_published_haze_lup_day = _haze_lup_day
		_published_haze_lup_night = lup_night
		cloud_lighting_bridge.call("SetHazeSkyRadiance", _haze_lup_day, lup_night)


# A palette colour as a unit-luminance per-channel multiplier (hue only), so a tint lerp changes colour without changing brightness.
func _unit_luminance_tint(color: Color) -> Vector3:
	var luminance := maxf(color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722, 0.0001)
	return Vector3(color.r, color.g, color.b) / luminance


# Largest per-channel difference of a and b relative to the larger of their peak components (0 when both are ~0).
func _relative_change(a: Vector3, b: Vector3) -> float:
	var peak := maxf(maxf(maxf(absf(a.x), absf(a.y)), absf(a.z)), maxf(maxf(absf(b.x), absf(b.y)), absf(b.z)))
	if peak < 0.000001:
		return 0.0
	return maxf(maxf(absf(a.x - b.x), absf(a.y - b.y)), absf(a.z - b.z)) / peak


# Viewer position in the ring's atmosphere frame: outward unit vector, ring axis, signed offset across the ring from its centre line,
# radial vector from the axis and altitude above the nominal floor (ring_radius - |radial|). Canonical path when the player publishes
# canonical atmosphere coordinates, otherwise derived from the camera world position.
func _viewer_atmosphere_frame(surface_up: Vector3) -> Dictionary:
	var ring_radius := _get_ring_radius_meters()
	var ring_axis := (canonical_grid as Node3D).global_basis.y.normalized() \
			if canonical_grid is Node3D else Vector3.UP
	var viewer_across := 0.0
	var viewer_radial := -surface_up.normalized() * ring_radius
	var viewer_outward := -surface_up.normalized()
	if _has_canonical_atmosphere_provider():
		var canonical_position := player.call("GetCanonicalAtmospherePositionMeters") as Vector3
		viewer_across = canonical_position.z - _get_ring_width_meters() * 0.5
		viewer_radial = viewer_outward * maxf(0.0, ring_radius - canonical_position.y)
	else:
		var viewer_position: Vector3 = _get_viewer_position()
		var relative_viewer: Vector3 = viewer_position - Vector3(gravity_controller.get_ring_center())
		viewer_across = relative_viewer.dot(ring_axis)
		viewer_radial = relative_viewer - ring_axis * viewer_across
		viewer_outward = viewer_radial.normalized() if viewer_radial.length_squared() > 0.000001 else -surface_up.normalized()
	return {
		"outward": viewer_outward,
		"axis": ring_axis,
		"across": viewer_across,
		"radial": viewer_radial,
		"height": ring_radius - viewer_radial.length(),
	}


func _publish_night_light_scale(scale: float) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_night_light_scale", clampf(scale, 0.0, 1.0))


# Pulls the hub lighting outputs from the director (spec C.1). Without a director everything stays 0.
func _update_hub_light() -> void:
	if eclipse_event != null and is_instance_valid(eclipse_event) and eclipse_event.has_method("SetHubLevelOverride"):
		current_hub_day = clampf(float(eclipse_event.get("HubLightDay")), 0.0, 1.0)
		current_hub_weight = clampf(float(eclipse_event.get("HubLightWeight")), 0.0, 1.0)
		current_hub_direct_energy = maxf(float(eclipse_event.get("HubLightDirect")), 0.0)
		current_hub_level = maxf(float(eclipse_event.get("HubLightLevel")), 0.0)
		current_hub_color = eclipse_event.get("HubLightColor") as Color
		current_hub_sky_tint_strength = clampf(float(eclipse_event.get("HubSkyTintStrength")), 0.0, 1.0)
		current_hub_halo_intensity = maxf(float(eclipse_event.get("HubZenithHaloIntensity")), 0.0)
		current_hub_in_scatter = maxf(float(eclipse_event.get("HubSkyInScatter")), 0.0)
	else:
		current_hub_day = 0.0
		current_hub_weight = 0.0
		current_hub_direct_energy = 0.0
		current_hub_level = 0.0
		current_hub_sky_tint_strength = 0.0
		current_hub_halo_intensity = 0.0
		current_hub_in_scatter = 0.0
	_publish_hub_light(current_hub_day, current_hub_weight)


func _publish_hub_light(day: float, weight: float) -> void:
	if day == 0.0 and weight == 0.0:
		current_hub_day = 0.0
		current_hub_weight = 0.0
		current_hub_direct_energy = 0.0
		current_hub_level = 0.0
		current_hub_sky_tint_strength = 0.0
		current_hub_halo_intensity = 0.0
		current_hub_in_scatter = 0.0
	if day != _published_hub_day:
		_published_hub_day = day
		RenderingServer.global_shader_parameter_set("ringworld_hub_day", day)
	if weight != _published_hub_weight:
		_published_hub_weight = weight
		RenderingServer.global_shader_parameter_set("ringworld_hub_light_weight", weight)


func _exit_tree() -> void:
	# Drop the C# helper before the .NET host shuts down.
	if _haze_sky != null:
		_haze_sky.call("SetStorm", 0.0)
	_haze_sky = null
	if _atmosphere_luts != null:
		_atmosphere_luts.call("Release")
	_atmosphere_luts = null
	_haze_cache_valid = false
	# Global shader parameters survive scene changes; reset before returning to
	# menus or scenes with no celestial clock.
	_publish_solar_visibility(1.0)
	_publish_night_light_scale(1.0)
	_publish_hub_light(0.0, 0.0)


func _update_sky(
		environment: Environment,
		day_factor: float,
		atmosphere_factor: float,
		star_visibility: float,
		surface_up: Vector3,
		to_sun: Vector3,
		giant_direction: Vector3) -> void:
	_ensure_ring_sky(environment)
	if ring_sky_material == null:
		return
	var top_color := _scale_rgb(night_sky_top_color, eclipse_sky_night_multiplier).lerp(day_sky_top_color, day_factor)
	var horizon_color := _scale_rgb(night_sky_horizon_color, eclipse_sky_night_multiplier).lerp(day_sky_horizon_color, day_factor)
	var eclipse_sky_factor := current_eclipse_coverage * current_real_sun_day_factor * clampf(eclipse_sky_tint_strength, 0.0, 1.0)
	top_color = top_color.lerp(eclipse_sky_color, eclipse_sky_factor)
	horizon_color = horizon_color.lerp(eclipse_fog_color, eclipse_sky_factor)
	# Hub sun (spec C.5): while the hub, not the real sun, lights the sky, lean the palette toward the cool hub tint. 0 with the hub off.
	if current_hub_day > 0.0:
		var hub_tint := current_hub_sky_tint_strength * current_hub_day * (1.0 - current_solar_visibility)
		top_color = top_color.lerp(hub_sky_tint_color, hub_tint)
		horizon_color = horizon_color.lerp(hub_sky_tint_color, hub_tint)
	if ring_sky_material != _sky_param_cache_owner:
		_sky_param_cache_owner = ring_sky_material
		_sky_param_cache.clear()
	var ring_radius := _get_ring_radius_meters()
	var viewer_frame := _viewer_atmosphere_frame(surface_up)
	var ring_axis: Vector3 = viewer_frame["axis"]
	var viewer_across: float = viewer_frame["across"]
	var viewer_radial: Vector3 = viewer_frame["radial"]
	var viewer_outward: Vector3 = viewer_frame["outward"]
	_set_sky_param("local_surface_up", -viewer_outward)
	_set_sky_param("ring_axis_direction", ring_axis)
	# The sky shader uses normalized ray geometry, but still needs the real radius
	# to convert its normalized air path back into physical extinction metres.
	# GraphicsQuality also registers the material with RingAtmosphereModel in the
	# exploration build; setting the radius here keeps standalone demo scenes
	# correct without changing the shared authored atmosphere controls.
	# Also written by RingAtmosphereModel.ConfigureMaterial (after registration), so never cached.
	ring_sky_material.set_shader_parameter("ring_radius_meters", ring_radius)
	_set_sky_param("camera_radial_ratio", viewer_radial.length() / ring_radius)
	_set_sky_param("camera_across_ratio", viewer_across / ring_radius)
	_set_sky_param("sky_camera_height_meters", float(viewer_frame["height"]))
	_set_sky_param(
		"ring_half_width_ratio",
		maxf(_get_ring_width_meters() * 0.5 / ring_radius, 0.000001))
	_set_sky_param("ring_interior_factor", _structural_shell_atmosphere_factor())
	var active_day_start_dot := float(_get_celestial_value(
			"DaylightStartDot", "daylight_start_dot", day_start_dot))
	var active_day_full_dot := float(_get_celestial_value(
			"DaylightFullDot", "daylight_full_dot", day_full_dot))
	_set_sky_param("day_start_dot", active_day_start_dot)
	_set_sky_param("day_full_dot", active_day_full_dot)
	_set_sky_param("sky_top_color", Vector3(top_color.r, top_color.g, top_color.b))
	_set_sky_param("sky_horizon_color", Vector3(horizon_color.r, horizon_color.g, horizon_color.b))
	_set_sky_param("space_color", Vector3(space_sky_color.r, space_sky_color.g, space_sky_color.b))
	_set_sky_param("atmosphere_factor", atmosphere_factor)
	_set_sky_param("sun_direction", to_sun.normalized())
	# Hub zenith halo (spec E.1): the hub hangs over the local zenith. hub_level 0 draws nothing.
	_set_sky_param("hub_direction", -viewer_outward)
	_set_sky_param("hub_level", current_hub_level)
	_set_sky_param("hub_color", Vector3(current_hub_color.r, current_hub_color.g, current_hub_color.b))
	_set_sky_param("hub_halo_intensity", current_hub_halo_intensity)
	_set_sky_param("hub_in_scatter", current_hub_in_scatter)
	_set_sky_param("sun_color", Vector3(sun_light.light_color.r, sun_light.light_color.g, sun_light.light_color.b))
	_set_sky_param("sun_disk_angular_radius_degrees", maxf(0.01, float(_get_celestial_value(
			"StarAngularRadiusDegrees", "star_angular_radius_degrees", 0.265))))
	# Compatibility uniform mirrors the canonical shared raw solar visibility.
	# Do not use GameplayEclipseDarkness: its legacy safety cap is not received
	# stellar light and must not prevent totality.
	_set_sky_param("eclipse_coverage", current_eclipse_coverage)
	_set_sky_param("eclipse_solar_transmittance", current_solar_visibility)
	_set_sky_param("ring_atmosphere_planet_direction", giant_direction.normalized())
	_set_sky_param("atmosphere_sun_forward_scattering", atmosphere_sun_forward_scattering)
	_set_sky_param("exterior_day_scattering_boost", exterior_day_scattering_boost)
	_set_sky_param("exterior_night_scattering_boost", exterior_night_scattering_boost)
	_set_sky_param("stars_enabled", stars_enabled)
	_set_sky_param("star_visibility", star_visibility)
	_set_sky_param("star_cell_density", star_cell_density)
	_set_sky_param("star_density", star_density)
	_set_sky_param("star_brightness", star_brightness)
	_set_sky_param("star_color", Vector3(star_color.r, star_color.g, star_color.b))
	# The procedural star domain is ring-local. Feed it the authoritative frame
	# angle rather than elapsed render time, so pause, scrubbing, and restored
	# simulation state all reproduce the same sky.
	var starfield_rotation_radians := deg_to_rad(fposmod(float(_get_celestial_value(
			"RingRotationAngleDegrees", "ring_rotation_angle_degrees", 0.0)), 360.0))
	_set_sky_param("starfield_rotation_radians", starfield_rotation_radians)
	var giant_enabled := bool(_get_gas_giant_configuration_value("Enabled", planet_enabled))
	var physical_giant_radius := maxf(0.0, float(_get_gas_giant_configuration_value("RadiusKm", 1.0)))
	var visual_giant_enabled := planet_enabled and giant_enabled and physical_giant_radius > 0.0 \
		and current_giant_angular_radius_degrees > 0.0
	_set_sky_param("planet_enabled", visual_giant_enabled)
	_set_sky_param("giant_direction", giant_direction.normalized())
	var celestial_visual_seed := int(_get_celestial_value("DeterministicVisualSeed", "deterministic_visual_seed", 1))
	_set_sky_param("celestial_seed_offset", _celestial_seed_offset(celestial_visual_seed))
	# Zero is a valid physical result: do not resurrect the old inspector disk.
	_set_sky_param("planet_angular_radius_degrees", current_giant_angular_radius_degrees)
	_set_sky_param("planet_brightness", planet_brightness)
	_set_sky_param("planet_detail_scale", planet_detail_scale)
	_set_sky_param("gas_giant_light_band_color", Vector3(gas_giant_light_band_color.r, gas_giant_light_band_color.g, gas_giant_light_band_color.b))
	_set_sky_param("gas_giant_dark_band_color", Vector3(gas_giant_dark_band_color.r, gas_giant_dark_band_color.g, gas_giant_dark_band_color.b))
	_set_sky_param("gas_giant_cream_color", Vector3(gas_giant_cream_color.r, gas_giant_cream_color.g, gas_giant_cream_color.b))
	_set_sky_param("gas_giant_storm_color", Vector3(gas_giant_storm_color.r, gas_giant_storm_color.g, gas_giant_storm_color.b))
	_set_sky_param("gas_giant_band_count", gas_giant_band_count)
	_set_sky_param("gas_giant_axial_tilt_radians", deg_to_rad(float(_get_gas_giant_configuration_value("AxialTiltDegrees", 3.13))))
	var giant_rotation_period := maxf(1.0, float(_get_gas_giant_configuration_value("RotationPeriodSeconds", 35730.0)))
	var giant_simulation_seconds := float(celestial_clock.call("get_simulation_seconds")) if celestial_clock != null and celestial_clock.has_method("get_simulation_seconds") else 0.0
	_set_sky_param("gas_giant_spin_radians", fposmod(giant_simulation_seconds / giant_rotation_period * TAU, TAU))
	_set_sky_param("planet_ocean_color", Vector3(planet_ocean_color.r, planet_ocean_color.g, planet_ocean_color.b))
	_set_sky_param("planet_land_color", Vector3(planet_land_color.r, planet_land_color.g, planet_land_color.b))
	_set_sky_param("planet_highland_color", Vector3(planet_highland_color.r, planet_highland_color.g, planet_highland_color.b))
	_set_sky_param("planet_cloud_color", Vector3(planet_cloud_color.r, planet_cloud_color.g, planet_cloud_color.b))
	_set_sky_param("planet_atmosphere_color", Vector3(planet_atmosphere_color.r, planet_atmosphere_color.g, planet_atmosphere_color.b))
	_set_sky_param("planet_atmosphere_rim_brightness", planet_atmosphere_rim_brightness)
	_set_sky_param("planet_light_color", Vector3(planet_light_color.r, planet_light_color.g, planet_light_color.b))
	_set_sky_param("planet_haze_scattering_energy", planet_haze_scattering_energy)
	var giant_visible_phase := clampf((1.0 - giant_direction.normalized().dot(to_sun.normalized())) * 0.5, 0.0, 1.0)
	_set_sky_param("planet_haze_enabled", visual_giant_enabled and planet_light_enabled and giant_visible_phase > 0.05)
	_set_sky_param("planet_haze_horizon_start_dot", planet_light_horizon_fade_start_dot)
	_set_sky_param("planet_haze_horizon_full_dot", planet_light_horizon_full_dot)
	_set_sky_param("planet_haze_twilight_end_day_factor", planet_light_twilight_end_day_factor)
	_update_sky_haze_lut(viewer_frame, ring_radius, to_sun)


# Keeps the sky-view LUT (a phase-factored cache of ring_haze_eval_sky, used only by ring_local_sky) in step with the sky's camera and sun.
# Passes the hub-blended light direction exactly as ring_haze_uniform_params builds it. The host publishes ringworld_haze_sky_lut_key; the
# sky falls back to the analytic function whenever that key is invalid (Legacy model, toggle off, no build yet, camera mismatch).
func _update_sky_haze_lut(viewer_frame: Dictionary, ring_radius: float, to_sun: Vector3) -> void:
	if _atmosphere_luts == null and not _atmosphere_luts_load_failed:
		var script := load("res://scripts/Atmosphere/RingAtmosphereLuts.cs") as Script
		if script != null:
			_atmosphere_luts = script.new() as RefCounted
		_atmosphere_luts_load_failed = _atmosphere_luts == null
	if _atmosphere_luts == null:
		return
	var outward: Vector3 = viewer_frame["outward"]
	var up := -outward.normalized()
	var blended := to_sun.normalized().lerp(up, clampf(_published_hub_weight, 0.0, 1.0))
	blended = blended.normalized() if blended.length() > 0.0001 else up
	var half_width := _get_ring_width_meters() * 0.5
	var across := float(viewer_frame["across"])
	_atmosphere_luts.call("RequestSkyView", ring_radius, outward, viewer_frame["axis"], float(viewer_frame["height"]),
			half_width + across, half_width - across, blended)


func _get_viewer_position() -> Vector3:
	var active_camera := get_viewport().get_camera_3d()
	return active_camera.global_position if active_camera != null else player.global_position


func _get_player_height_meters() -> float:
	if player == null:
		return 0.0
	if _has_canonical_atmosphere_provider():
		return maxf(0.0, float(player.call("GetCanonicalAtmosphereHeightMeters")))
	if gravity_controller == null:
		return 0.0
	var center: Vector3 = gravity_controller.get_ring_center()
	var ring_axis: Vector3 = Vector3(gravity_controller.global_basis.y).normalized()
	var relative_viewer: Vector3 = _get_viewer_position() - center
	var radial: Vector3 = relative_viewer - ring_axis * relative_viewer.dot(ring_axis)
	# Distance from the habitable surface works on both sides of the shell: flying
	# inward reaches the central vacuum, while flying beneath the structural hull
	# also restores the black, star-filled exterior sky.
	return absf(float(gravity_controller.ring_radius) - radial.length())


func _atmosphere_factor_for_height(height_meters: float) -> float:
	var fade_start := maxf(0.0, sky_fade_start_height_meters)
	var fade_end := maxf(fade_start + 1.0, space_black_height_meters)
	return 1.0 - smoothstep(fade_start, fade_end, maxf(0.0, height_meters))


func _star_visibility_for_height_and_day(height_meters: float, day_factor: float) -> float:
	if not stars_enabled:
		return 0.0
	var fade_start := maxf(space_black_height_meters, stars_fade_start_height_meters)
	var fade_end := maxf(fade_start + 1.0, stars_full_height_meters)
	var space_visibility := smoothstep(fade_start, fade_end, maxf(0.0, height_meters))
	if not stars_visible_on_night_side:
		return space_visibility
	var twilight_end := maxf(0.05, night_star_twilight_end_day_factor)
	var night_factor := 1.0 - smoothstep(0.0, twilight_end, clampf(day_factor, 0.0, 1.0))
	var atmospheric_night_visibility := night_factor * clampf(night_surface_star_visibility, 0.0, 1.0)
	return maxf(space_visibility, atmospheric_night_visibility)


func _update_planet_light(surface_up: Vector3, planet_direction: Vector3, to_sun: Vector3, day_factor: float) -> void:
	if planet_light == null:
		return
	planet_direction = planet_direction.normalized()
	planet_light.global_basis = _basis_with_z_axis(planet_direction)
	planet_light.light_color = planet_light_color
	planet_light.light_specular = clampf(planet_light_specular, 0.0, 1.0)
	planet_light.light_angular_distance = clampf(planet_light_angular_distance_degrees, 0.0, 5.0)
	planet_light.directional_shadow_max_distance = maxf(100.0, planet_light_shadow_distance_meters)
	planet_light.directional_shadow_fade_start = clampf(planet_light_shadow_fade_ratio, 0.5, 1.0)
	planet_light.directional_shadow_blend_splits = true
	planet_light.directional_shadow_mode = DirectionalLight3D.SHADOW_PARALLEL_2_SPLITS if planet_light_shadow_cascades == 2 else DirectionalLight3D.SHADOW_PARALLEL_4_SPLITS

	var giant_enabled := bool(_get_gas_giant_configuration_value("Enabled", planet_enabled))
	var physical_giant_radius := maxf(0.0, float(_get_gas_giant_configuration_value("RadiusKm", 1.0)))
	if not planet_enabled or not planet_light_enabled or not giant_enabled \
		or physical_giant_radius <= 0.0 or current_giant_angular_radius_degrees <= 0.0:
		planet_light.light_energy = 0.0
		planet_light.shadow_enabled = false
		return
	var horizon_start := clampf(planet_light_horizon_fade_start_dot, -0.25, 0.25)
	var horizon_full := maxf(horizon_start + 0.01, planet_light_horizon_full_dot)
	var above_horizon := smoothstep(
		horizon_start,
		horizon_full,
		surface_up.normalized().dot(planet_direction))
	var twilight_end := maxf(0.05, planet_light_twilight_end_day_factor)
	var night_factor := 1.0 - smoothstep(0.0, twilight_end, clampf(day_factor, 0.0, 1.0))
	# At conjunction the giant is backlit (dark rim); at opposition it is full.
	var visible_phase := clampf((1.0 - planet_direction.dot(to_sun.normalized())) * 0.5, 0.0, 1.0)
	planet_light.light_energy = maxf(0.0, planet_light_night_energy) * above_horizon * night_factor * visible_phase
	# A zero-energy directional light does not need a shadow-map pass. This keeps
	# full-moon shadows local to the actual planet-lit portion of the night side.
	planet_light.shadow_enabled = planet_light_cast_shadows and planet_light.light_energy > 0.001


func _basis_with_z_axis(z_axis: Vector3) -> Basis:
	var z := z_axis.normalized()
	var reference := Vector3.UP
	if absf(reference.dot(z)) > 0.98:
		reference = Vector3.RIGHT
	var x := reference.cross(z).normalized()
	var y := z.cross(x).normalized()
	return Basis(x, y, z).orthonormalized()


func _cap_density_for_opposite_visibility(requested_density: float, target_visibility: float) -> float:
	if not _has_canonical_atmosphere_provider() and gravity_controller == null:
		return requested_density

	var ring_diameter := maxf(_get_ring_radius_meters() * 2.0, 1.0)
	var visibility: float = clampf(target_visibility, 0.01, 0.99)
	var density_cap: float = -log(visibility) / ring_diameter
	return minf(requested_density, density_cap)


func _resolve_references() -> void:
	if player == null:
		player = get_node_or_null(player_path) as Node3D

	if gravity_controller == null:
		gravity_controller = get_node_or_null(gravity_controller_path)

	if canonical_grid == null:
		canonical_grid = get_node_or_null(canonical_grid_path)

	if sun_light == null:
		sun_light = get_node_or_null(sun_light_path) as DirectionalLight3D

	if planet_light == null:
		planet_light = get_node_or_null(planet_light_path) as DirectionalLight3D

	if celestial_clock == null:
		celestial_clock = get_node_or_null(celestial_clock_path)

	if world_environment == null:
		world_environment = get_node_or_null(world_environment_path) as WorldEnvironment

	if eclipse_event == null:
		eclipse_event = get_node_or_null(eclipse_event_path)

	if cloud_lighting_bridge == null:
		cloud_lighting_bridge = get_node_or_null(cloud_lighting_bridge_path)

	if underground_environment == null:
		underground_environment = get_node_or_null(underground_environment_path)

	if underground_mode == null:
		underground_mode = get_node_or_null(underground_mode_path)


func _evaluate_giant(to_sun: Vector3) -> void:
	# RingDayNightCycle evaluates the immutable frame at priority -100.
	pass


func _get_celestial_value(pascal_property: String, legacy_property: String, fallback: Variant) -> Variant:
	if celestial_clock == null:
		return fallback
	var pascal_value: Variant = celestial_clock.get(pascal_property)
	if pascal_value != null:
		return pascal_value
	var legacy_value: Variant = celestial_clock.get(legacy_property)
	return legacy_value if legacy_value != null else fallback


func _get_gas_giant_configuration_value(property_name: String, fallback: Variant) -> Variant:
	# The clock owns the effective nested Configuration resource. Reading it here
	# keeps sky presentation in lockstep with the same physical giant it evaluates.
	if celestial_clock == null:
		return fallback
	var configuration: Variant = celestial_clock.get("Configuration")
	if configuration == null:
		return fallback
	var giant_configuration: Variant = configuration.get("GasGiant")
	if giant_configuration == null:
		return fallback
	var value: Variant = giant_configuration.get(property_name)
	return value if value != null else fallback


func _celestial_seed_offset(seed: int) -> Vector3:
	# Keep domains compact before they reach the GPU: high world seeds otherwise
	# lose their low bits in float arithmetic and can collapse to the same pattern.
	var compact_seed := posmod(seed, 104729)
	return Vector3(
		float(posmod(compact_seed * 73 + 19, 997)) / 997.0,
		float(posmod(compact_seed * 193 + 47, 991)) / 991.0,
		float(posmod(compact_seed * 389 + 71, 983)) / 983.0)


func _get_structural_sky_access() -> float:
	if underground_environment == null:
		return 1.0
	return clampf(float(underground_environment.get("CurrentSkyAccess")), 0.0, 1.0)


func _get_enclosure_blend() -> float:
	if underground_mode == null:
		return 0.0
	var blend: Variant = underground_mode.get("EnclosureBlend")
	return clampf(float(blend), 0.0, 1.0) if blend != null else 0.0


func _get_enclosure_lighting_weight() -> float:
	if underground_mode == null:
		return 0.0
	var weight: Variant = underground_mode.get("EnclosureLightingWeight")
	return clampf(float(weight), 0.0, 1.0) if weight != null else 0.0


# Effective sky access: the structural cavity's verified roof access, further reduced by the
# underground-mode enclosure blend so designed (terrain-domain) caves are truly dark (decision D1).
func _get_underground_sky_access() -> float:
	return minf(_get_structural_sky_access(), 1.0 - _get_enclosure_lighting_weight() * _get_enclosure_blend())


func _has_canonical_atmosphere_provider() -> bool:
	if player != _canonical_provider_player or canonical_grid != _canonical_provider_grid:
		_canonical_provider_player = player
		_canonical_provider_grid = canonical_grid
		_canonical_provider_cached = player != null \
			and canonical_grid != null \
			and player.has_method("GetCanonicalSurfaceUp") \
			and player.has_method("GetCanonicalAtmospherePositionMeters") \
			and player.has_method("GetCanonicalAtmosphereHeightMeters") \
			and canonical_grid.has_method("GetCanonicalRingRadiusMeters")
	return _canonical_provider_cached


func _get_surface_up() -> Vector3:
	if _has_canonical_atmosphere_provider():
		var canonical_up := player.call("GetCanonicalSurfaceUp") as Vector3
		if canonical_grid is Node3D:
			canonical_up = (canonical_grid as Node3D).global_basis.orthonormalized() * canonical_up
		return canonical_up.normalized()
	return gravity_controller.get_surface_up(player.global_position)


func _get_ring_radius_meters() -> float:
	if _has_canonical_atmosphere_provider():
		return maxf(1.0, float(canonical_grid.call("GetCanonicalRingRadiusMeters")))
	return maxf(float(gravity_controller.ring_radius), 1.0)


func _get_ring_width_meters() -> float:
	if _has_canonical_atmosphere_provider():
		return maxf(1.0, float(canonical_grid.get("RingAcrossWidthMeters")))
	return maxf(float(gravity_controller.ring_width), 1.0)


func _ring_interior_factor() -> float:
	var width := _get_ring_width_meters()
	var half_width := width * 0.5
	var across := half_width
	if _has_canonical_atmosphere_provider():
		var canonical_position := player.call("GetCanonicalAtmospherePositionMeters") as Vector3
		across = canonical_position.z
	elif gravity_controller != null:
		var relative_viewer := _get_viewer_position() - Vector3(gravity_controller.get_ring_center())
		across = relative_viewer.dot(Vector3.UP) + half_width
	return _ring_interior_factor_for_across(across, width)


# The visual atmosphere shares the same finite lower-hull boundary as shell
# lighting. Canonical coordinates are authoritative; only scenes without that
# provider retain the legacy across-only fallback.
func _structural_shell_atmosphere_factor() -> float:
	var width := _get_ring_width_meters()
	if _has_canonical_atmosphere_provider():
		# A canonical scene missing the structural-domain API must fail closed;
		# falling back to a geometric estimate here could light exterior vacuum.
		if not canonical_grid.has_method("GetStructuralBottomGlobalY"):
			return 0.0
		var canonical_position := player.call("GetCanonicalAtmospherePositionMeters") as Vector3
		return _ring_interior_factor_for_shell(
			canonical_position.z,
			width,
			canonical_position.y,
			float(canonical_grid.call("GetStructuralBottomGlobalY")))
	return _ring_interior_factor()


# Ring occupancy is [0, width) across and [structural bottom, +inf) vertically.
# The exact lower-hull plane remains in the shell; only positions below it are
# exterior vacuum.
func _ring_interior_factor_for_shell(
		across: float, width: float, vertical: float, structural_bottom: float) -> float:
	if vertical < structural_bottom:
		return 0.0
	return _ring_interior_factor_for_across(across, width)


# Canonical ring occupancy is the half-open interval [0, width). The hard
# guard prevents atmosphere or shell lighting from bleeding beyond either wall;
# the visual feather is deliberately contained inside that interval.
func _ring_interior_factor_for_across(across: float, width: float) -> float:
	if width <= 0.0 or across < 0.0 or across >= width:
		return 0.0
	var half_width := width * 0.5
	var feather := minf(maxf(100.0, ring_side_atmosphere_fade_meters), half_width)
	return 1.0 - smoothstep(half_width - feather, half_width, absf(across - half_width))


func _run_ring_interior_boundary_smoke_test() -> void:
	const TEST_WIDTH := 128.0
	const EPSILON := 0.001
	var points := [-EPSILON, 0.0, 1.0, TEST_WIDTH * 0.5, TEST_WIDTH - 1.0, TEST_WIDTH, TEST_WIDTH + EPSILON]
	var factors: Array[float] = []
	for point in points:
		factors.append(_ring_interior_factor_for_across(point, TEST_WIDTH))
	if factors[0] != 0.0 or factors[5] != 0.0 or factors[6] != 0.0 \
		or factors[1] < 0.0 or factors[2] <= 0.0 or factors[3] < 0.999 or factors[4] <= 0.0:
		push_error("Ring interior boundary smoke test failed: %s" % [factors])
	else:
		print("RING_INTERIOR_BOUNDARY_SMOKE_TEST=PASS")


# Cave ambient and fog colour are tuned from the Underground section of the grave-key drawer and owned by the
# underground-mode controller. These fallbacks apply only when that node is absent.
func _underground_mode_value(property_name: String, fallback: Variant) -> Variant:
	if underground_mode == null:
		return fallback
	var value: Variant = underground_mode.get(property_name)
	return value if value != null else fallback


func _underground_cave_ambient_color() -> Color:
	return _underground_mode_value("CaveAmbientColor", Color(0.16, 0.20, 0.30, 1.0)) as Color


func _underground_cave_ambient_energy() -> float:
	return clampf(float(_underground_mode_value("CaveAmbientEnergy", 0.035)), 0.0, 1.0)


func _underground_cave_fog_color() -> Color:
	return _underground_mode_value("CaveFogColor", Color(0.05, 0.07, 0.11, 1.0)) as Color


func _underground_cave_fog_density() -> float:
	return maxf(float(_underground_mode_value("CaveFogDensity", 0.0)), 0.0)


func _underground_cave_fog_weight() -> float:
	return clampf(float(_underground_mode_value("CaveFogWeight", 0.0)), 0.0, 1.0)
