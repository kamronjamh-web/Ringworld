@tool
extends CompositorEffect
class_name SunshineCloudsGD

signal external_texture_retirement_requested(generation_id: int)

@export_tool_button("Refresh Compute", "Clear") var refresh_action = refresh_compute


@export_group("Basic Settings")
@export_range(0, 1) var clouds_coverage : float = 0.874
@export_range(0, 20) var clouds_density : float = 0.14
@export_range(0, 2) var atmospheric_density : float = 0.503
@export_range(0, 10) var lighting_density : float = 0.982
@export_range(0, 1) var fog_effect_ground : float = 1.0
@export_range(0, 1) var use_environment_fog : float = 0.0

@export_subgroup("Colors")
@export_range(0, 1) var clouds_anisotropy : float = 0.16
@export_range(0, 1) var clouds_powder : float = 0.5
@export var cloud_ambient_color : Color = Color(0.761, 0.784, 0.824, 1.0)
@export var cloud_ambient_tint : Color = Color(0.133, 0.2, 0.243, 1.0)

@export var atmosphere_color : Color = Color(1.0, 1.0, 1.0, 1.0)
@export var sampled_environment_fog_color : Color = Color(0.518, 0.553, 0.608, 1.0)
@export var ambient_occlusion_color : Color = Color(1.0, 0.0, 0.0, 1.0)

@export_subgroup("Structure")
@export_range(0, 1) var accumulation_decay : float = 0.7
@export_range(100, 1000000) var extra_large_noise_scale : float = 320000.0
@export_range(100, 500000) var large_noise_scale : float = 120000.0
@export_range(100, 100000) var medium_noise_scale : float = 20000.0
@export_range(100, 10000) var small_noise_scale : float = 8500
@export_range(0, 2) var clouds_sharpness : float = 0.746
@export_range(0, 3) var clouds_detail_power : float = 1.075
@export_range(0, 50000) var curl_noise_strength : float = 4500.0
@export_range(0, 2) var lighting_sharpness : float = 0.38
@export_range(0, 1) var wind_swept_range : float = 0.54
@export_range(0, 5000) var wind_swept_strength : float = 0.0

@export var cloud_floor : float = 2000.0
@export var cloud_ceiling : float = 25000.0

@export_subgroup("Performance")
@export var max_step_count : float = 128
@export var max_lighting_steps : float = 32
@export_enum("Native","Half","Quarter","Eighth") var resolution_scale = 0:
	get:
		return resolution_scale
	set(value):
		resolution_scale = value
		last_size = Vector2i(0, 0)
		lights_updated = true
@export_range(0, 8) var lod_bias : float = 1.0

@export_subgroup("Noise Textures")
@export var dither_noise : Texture3D
@export var height_gradient : Texture2D
@export var extra_large_noise_patterns : Texture2D
@export var large_scale_noise : Texture3D
@export var medium_scale_noise : Texture3D
@export var small_scale_noise : Texture3D
@export var curl_noise : Texture3D

@export_group("Advanced Settings")
@export_subgroup("Visuals")
@export_range(0, 1000) var dither_speed : float = 0.0
@export_range(0, 1, 0.01) var ray_start_jitter_strength : float = 1.0
@export var ring_width_meters : float = 0.0
# Capture-only diagnostics. Raw density is the ray-march result before the
# scene-depth mask; depth density is the final occlusion-tested matte.
@export var diagnostic_raw_density_matte : bool = false
@export var diagnostic_depth_density_matte : bool = false
# Capture-only diagnostic. Disables temporal color history while preserving the
# normal lit compositor result; this is intentionally off in production.
@export var diagnostic_disable_temporal_history : bool = false
# Capture-only diagnostic. Adds CPU readback usage to accumulation textures;
# keep disabled so normal gameplay allocations and rendering stay unchanged.
@export var diagnostic_temporal_readback : bool = false
# Opt-in main RenderingDevice timestamp scopes. The bridge mirrors the global profiler state.
@export var gpu_timestamp_diagnostics_enabled : bool = false
@export var gpu_timestamp_begin_marker : String = ""
@export var gpu_timestamp_end_marker : String = ""
@export var gpu_timestamp_cloud_shadow_near_begin_marker : String = ""
@export var gpu_timestamp_cloud_shadow_near_end_marker : String = ""
@export var gpu_timestamp_cloud_shadow_far_begin_marker : String = ""
@export var gpu_timestamp_cloud_shadow_far_end_marker : String = ""
# MenuFlyoverSunshineLayer alone enables this opt-in reconstruction shortcut.
# Keeping the default false preserves the normal Sunshine post-pass path.
@export var menu_fast_reconstruction : bool = false
# Menu-only: callbacks advance a deterministic sample even when menu animation
# is paused. Default false preserves gameplay behavior.
@export var menu_temporal_sampling : bool = false
@export var menu_cloud_frame_around_precise : float = 0.0
@export var menu_animation_seconds_precise : float = 0.0
var last_menu_sample_phase : float = 0.0
var last_menu_arc_delta : float = 0.0
var last_menu_bulk_delta := Vector2.ZERO
var last_menu_history_static : bool = false
var last_menu_history_discontinuity : bool = false
@export var diagnostic_menu_history_weights : bool = false
# Published by RingScaledFarViewport's POST_OPAQUE compositor.  This is an
# R32F physical ray distance in metres; null means main scene depth only.
@export var far_opaque_depth_texture : Texture2D
@export_range(0, 20) var blur_power : float = 0.1
@export_range(0, 6) var blur_quality : float = 1.0

@export_subgroup("Reflections")
@export var reflections_globalshaderparam : String = ""

@export_subgroup("Performance")
@export var min_step_distance : float = 400.0
@export var max_step_distance : float = 500.0
@export var lighting_travel_distance : float = 10000.0

@export_subgroup("Mask")
@export var extra_large_used_as_mask : bool = false
@export var mask_width_km : float = 32.0;

@export_group("Compute Shaders")
@export var pre_pass_compute_shader : RDShaderFile
@export var compute_shader : RDShaderFile
@export var post_pass_compute_shader : RDShaderFile
@export var cloud_shadow_compute_shader : RDShaderFile

@export_group("Internal Use")
@export var origin_offset : Vector3 = Vector3.ZERO
@export_subgroup("Ringworld Local Frame")
@export var cloud_frame_origin : Vector3 = Vector3.ZERO
@export var cloud_frame_around : Vector4 = Vector4(1.0, 0.0, 0.0, 0.0)
@export var cloud_frame_up : Vector4 = Vector4(0.0, 1.0, 0.0, 0.0)
@export var cloud_frame_across : Vector4 = Vector4(0.0, 0.0, 1.0, 0.0)
@export_subgroup("Ringworld Local Storm")
@export var storm_system : Vector4 = Vector4.ZERO
@export var storm_shape : Vector4 = Vector4.ZERO
@export var storm_detail : Vector4 = Vector4(4200.0, 2.6, 0.70, 0.0)
@export var background_altitude : Vector4 = Vector4.ZERO
@export var storm_altitude : Vector4 = Vector4.ZERO
@export_subgroup("Positions")
@export var wind_direction : Vector3 = Vector3.ZERO
var extra_large_scale_clouds_position : Vector3 = Vector3.ZERO
var large_scale_clouds_position : Vector3 = Vector3.ZERO
var medium_scale_clouds_position : Vector3 = Vector3.ZERO
var detail_clouds_position : Vector3 = Vector3.ZERO
var current_time : float = 0.0

@export_subgroup("Lights")
@export var directional_lights_data : Array[Vector4] = []
@export var point_lights_data : Array[Vector4] = []
@export var point_effector_data : Array[Vector4] = []

# RingworldTechDemo extension (eclipse beam cloud light): up to SEGMENT_LIGHT_MAX line lights in their own block after the point effectors of
# the lights uniform buffer (SegmentLightsBlock in SunshineCloudsCompute.glsl). Count 0 (the default) skips the shader loop entirely.
const SEGMENT_LIGHT_MAX := 8
const SEGMENT_LIGHT_OFFSET := 6272 # bytes: the directional lights, point lights and point effectors come first
const SEGMENT_LIGHT_BYTES := 16 + SEGMENT_LIGHT_MAX * 48 # header vec4 + 3 vec4 per segment
const LIGHT_BUFFER_BYTES := SEGMENT_LIGHT_OFFSET + SEGMENT_LIGHT_BYTES
var segment_light_count : int = 0
var segment_light_bytes : PackedByteArray = PackedByteArray()
var segment_lights_dirty : bool = true
var segment_light_phase_weight : float = 0.5
var segment_light_extinction : float = 0.0015

var positionQueries : Array[Vector3] = []
var positionQueryCallables : Array[Callable] = []
var positionQuerying : bool = false
var positionResetting : bool = false
const POSITION_QUERY_CAPACITY := 256
const POSITION_QUERY_BUFFER_BYTES := POSITION_QUERY_CAPACITY * 16
var positionQueryBatchCount : int = 0

const GENERAL_DATA_FLOAT_COUNT := 276
const GENERAL_DATA_BUFFER_BYTES := GENERAL_DATA_FLOAT_COUNT * 4
const GENERAL_DATA_MAIN_RING_SIZE := 4
const GENERAL_DATA_RING_SIZE := GENERAL_DATA_MAIN_RING_SIZE + 1 # slot 0 is shadows only

var lights_updated = false

var maskDrawnRid : RID = RID()

var rd : RenderingDevice
var shader : RID = RID()
var pipeline : RID = RID()

var prepass_shader : RID = RID()
var prepass_pipeline : RID = RID()

var postpass_shader : RID = RID()
var postpass_pipeline : RID = RID()

var display_shader : RID = RID()
var display_pipeline : RID = RID()
var display_vertex_format : int
var display_vertex_buffer : RID = RID()
var display_vertex_array : RID = RID()

var framebuffer_format : int

var nearest_sampler : RID = RID()
var linear_sampler : RID = RID()
var linear_sampler_no_repeat : RID = RID()

var general_data_buffer : RID = RID()
var general_data_buffers : Array[RID] = []
var general_data_uniform_sets : Array = []
var general_data_ring_index := 0
var render_callback_serial := 0
var retired_generations : Array[Dictionary] = []
var next_retired_generation_id := 1
var light_data_buffer : RID = RID()
var point_sample_data_buffer : RID = RID()
var accumulation_textures : Array[RID] = []
# The far ring is supplied through Godot's canvas-background path, which is
# drawn outside this compositor's normal scene-color ownership. Publish the
# exact temporally accumulated cloud color/density texture so that backdrop can
# use the same silhouette instead of trying to reproduce Sunshine's noise in a
# second shader.
var cloud_backdrop_texture_wrappers : Array[Texture2DRD] = []
var cloud_backdrop_published_texture_wrappers : Array[Texture2DRD] = []
var resized_depth : RID = RID()
var far_opaque_depth_fallback : RID = RID()
var last_size : Vector2i = Vector2i(0, 0)
var color_images : Array[RID] = []
var blit_screen_images : Array[RID] = []

# Independent GPU cloud-shadow producer.  These remain GPU resident and are
# deliberately separate from the callback/readback sampling fallback below.
const GPU_CLOUD_SHADOW_CASCADES := 2
const GPU_CLOUD_SHADOW_PARAM_BYTES := 96
const GPU_CLOUD_SHADOW_MAX_SLICES := 16
var cloud_shadow_shader : RID = RID()
var cloud_shadow_pipeline : RID = RID()
var cloud_shadow_param_buffers : Array[RID] = [RID(), RID()]
# Indices 0/1 are the published (front) cascade textures. Indices 2/3
# (cascade + GPU_CLOUD_SHADOW_CASCADES) are staging back buffers that exist only
# for sliced near-map refreshes; see dispatch_gpu_cloud_shadows.
var cloud_shadow_textures : Array[RID] = [RID(), RID(), RID(), RID()]
var cloud_shadow_texture_wrappers : Array[Texture2DRD] = [null, null]
# A wrapper becomes visible to main-thread receivers only after the callback
# that wrote its first compute result has ended.
var cloud_shadow_published_texture_wrappers : Array[Texture2DRD] = [null, null]
var cloud_shadow_uniform_sets : Array[RID] = [RID(), RID(), RID(), RID()]
# Slices of the in-flight refresh already written to the staging buffer, and the
# configuration snapshot every slice of that refresh uses, so one refresh always
# maps one consistent world window even if the bridge reconfigures meanwhile.
var cloud_shadow_slice_progress : Array[int] = [0, 0]
var cloud_shadow_slice_configs : Array[Dictionary] = [{}, {}]
var cloud_shadow_resolutions : Array[Vector2i] = [Vector2i.ZERO, Vector2i.ZERO]
var cloud_shadow_published_resolutions : Array[Vector2i] = [Vector2i.ZERO, Vector2i.ZERO]
var cloud_shadow_configs : Array[Dictionary] = [{}, {}]
var cloud_shadow_pending : Array[bool] = [false, false]
var cloud_shadow_next_cascade : int = 0
var cloud_shadow_dispatch_count : Array[int] = [0, 0]
var cloud_shadow_phase : Array[int] = [0, 0]
var cloud_shadow_reset_history : Array[bool] = [true, true]
var cloud_shadow_generation_enabled := true
var cloud_shadow_last_error := ""
# Resizing a cascade creates a new Texture2DRD.  The old wrapper can still be
# referenced by receiver materials submitted in an earlier frame, so retain
# the complete descriptor set until the render-device frame delay has passed.
var retired_cloud_shadow_cascades : Array[Dictionary] = []

var buffers : RenderSceneBuffersRD


var uniform_sets : Array[RID] = []
var general_data : PackedByteArray

# Ten vec4s, populated by RingSunshineCloudBridge. Keeping this as an
# immutable value array makes every compositor callback snapshot self-contained.
@export var ring_atmosphere_profile : Array[Vector4] = []
@export var skip_empty_cloud_mask_samples : bool = true
@export var depth_history_rejection_enabled : bool = true
@export_range(0.25, 4.0) var history_depth_tolerance_multiplier : float = 1.0
@export var cloud_history_compatibility_enabled : bool = true
@export_range(0.0, 0.95) var cloud_history_gate_alpha_low : float = 0.20
@export_range(0.05, 1.0) var cloud_history_gate_alpha_high : float = 0.65
@export_range(0.25, 8.0) var history_continuity_allowance_scale : float = 1.0
@export_range(1.5, 32.0) var history_continuity_hard_reset_ratio : float = 8.0
@export var lightning_history_isolation_enabled : bool = true
var _history_reset_requested : bool = false

# Reset temporal color only; GPU shadow and descriptor generations stay intact.
func request_history_reset() -> void:
	_history_reset_requested = true
@export var terrain_cloud_sun_global_texture : Texture2D
@export var terrain_cloud_sun_regional_texture : Texture2D
@export var terrain_cloud_sun_profile : Array[Vector4] = []
@export var terrain_cloud_sun_canonical_frame : Array[Vector4] = []
var _terrain_sun_neutral := RID()
var _terrain_sun_set := RID()
var _terrain_sun_shader := RID()
var _terrain_sun_bindings_present := false
var _terrain_sun_texture_rids : Array[RID] = []
var _terrain_sun_texture_refs : Array[Texture2D] = []
var _terrain_sun_retired : Array[Dictionary] = []

# Fixtures may supply a saved pre-atlas compute shader. Reflect only its
# descriptor decorations once per shader generation; never bind an absent set.
static func _has_terrain_sun_bindings(bytecode: PackedByteArray) -> bool:
	var descriptor_sets := {}; var bindings := {}
	var offset := 20 # SPIR-V header is five words.
	while offset + 4 <= bytecode.size():
		var instruction := bytecode.decode_u32(offset)
		var words := int(instruction >> 16)
		if words <= 0 or offset + words * 4 > bytecode.size(): return false
		if (instruction & 65535) == 71 and words >= 4: # OpDecorate
			var target := bytecode.decode_u32(offset + 4)
			var decoration := bytecode.decode_u32(offset + 8)
			var value := bytecode.decode_u32(offset + 12)
			if decoration == 34: descriptor_sets[target] = value
			elif decoration == 33: bindings[target] = value
		offset += words * 4
	var found := 0
	for target in descriptor_sets:
		if int(descriptor_sets[target]) == 1 and int(bindings.get(target, -1)) in [19,20]: found += 1
	return found == 2

func _retire_terrain_sun_sets(force := false) -> void:
	var remaining : Array[Dictionary] = []
	for entry in _terrain_sun_retired:
		if force or render_callback_serial >= int(entry.release_after):
			if rd.uniform_set_is_valid(entry.uniform_set): rd.free_rid(entry.uniform_set)
		else: remaining.append(entry)
	_terrain_sun_retired = remaining

func _update_terrain_sun_set() -> void:
	_retire_terrain_sun_sets()
	if not _terrain_sun_neutral.is_valid():
		var format := RDTextureFormat.new()
		format.format = RenderingDevice.DATA_FORMAT_R32G32_SFLOAT
		format.width = 1; format.height = 1
		format.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT
		_terrain_sun_neutral = rd.texture_create(format, RDTextureView.new(), [PackedFloat32Array([1.0, 0.0]).to_byte_array()])
	var textures : Array[Texture2D] = [terrain_cloud_sun_global_texture, terrain_cloud_sun_regional_texture]
	var rids : Array[RID] = []
	for texture in textures:
		var texture_rid := _terrain_sun_neutral
		if texture != null and is_instance_valid(texture):
			var candidate := RenderingServer.texture_get_rd_texture(texture.get_rid())
			if candidate.is_valid(): texture_rid = candidate
		rids.append(texture_rid)
	if _terrain_sun_set.is_valid() and rd.uniform_set_is_valid(_terrain_sun_set) and rids == _terrain_sun_texture_rids and _terrain_sun_shader == shader: return
	if _terrain_sun_set.is_valid():
		_terrain_sun_retired.append({"uniform_set":_terrain_sun_set, "textures":_terrain_sun_texture_refs,
			"release_after":render_callback_serial + max(2, rd.get_frame_delay() + 2)})
	var uniforms : Array[RDUniform] = []
	for index in 2:
		var uniform := RDUniform.new()
		uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
		uniform.binding = 19 + index
		uniform.add_id(linear_sampler_no_repeat); uniform.add_id(rids[index])
		uniforms.append(uniform)
	_terrain_sun_set = rd.uniform_set_create(uniforms, shader, 1)
	_terrain_sun_shader = shader
	_terrain_sun_texture_rids = rids
	_terrain_sun_texture_refs = textures

@export var cloud_handoff : Vector4 = Vector4.ZERO
@export var cloud_weather_params : Vector4 = Vector4.ZERO
@export var cloud_weather_frame : Vector4 = Vector4.ZERO

var light_data : PackedByteArray

var accumulation_is_a : bool = false
var ignore_accumilation : bool = false

var first_run : bool = true
var previous_callback_camera_transform: Transform3D
var previous_callback_camera_projection: Projection
var gameplay_cloud_frame_snapshot := {}
var previous_callback_gameplay_cloud_frame := {}
var previous_callback_menu_around_precise : float = 0.0
var previous_callback_bulk_wind := Vector2.ZERO
var previous_callback_detail_wind := Vector3.ZERO
var previous_callback_density_state : Array = []
# Monotonic seconds of wind advection, advanced by SunshineCloudsDriver; paired
# with per-quantity velocities to separate steady drift from discontinuities.
var advection_clock : float = 0.0
var _continuity_velocities : Array = [null, null, null, null, null, null]
var _continuity_last_clock : float = 0.0
var _continuity_last_usec : int = 0
var filter_index = 0

var last_render_target : RID
var last_msaa_mode := RenderingServer.ViewportMSAA.VIEWPORT_MSAA_DISABLED
var msaa_mode := RenderingServer.ViewportMSAA.VIEWPORT_MSAA_DISABLED
var diagnostic_last_callback_target := RID()
var diagnostic_last_callback_size := Vector2i.ZERO
var diagnostic_last_callback_projection_y := NAN
# Read-only temporal-history observability. These snapshots intentionally stay
# on the compositor object: querying them neither touches RD resources nor
# changes the accumulation path.
var temporal_history_explicit_refresh_request_count := 0
var temporal_history_accumulation_rebuild_count := 0
var temporal_history_last_rebuild_reason := "none"
var temporal_history_render_target_change_count := 0
var temporal_history_zero_packed_decay_callback_count := 0
var diagnostic_last_callback_z_far := 0.0
var temporal_history_last_continuity_excess := 0.0
var temporal_history_last_continuity_term := ""
var temporal_history_last_continuity_multiplier := 1.0
var temporal_history_continuity_decay_callback_count := 0
var temporal_history_continuity_hard_reset_count := 0
var temporal_history_last_requested_decay := 0.0
var temporal_history_last_packed_decay := 0.0
var temporal_history_last_render_target_id := 0
var temporal_history_last_internal_size := Vector2i.ZERO
var temporal_history_last_raster_size := Vector2i.ZERO
var temporal_history_last_callback_source_reset := false
var temporal_history_explicit_refresh_pending := false
var diagnostic_renderer_snapshot_mutex := Mutex.new()
var diagnostic_renderer_snapshot := {}
var diagnostic_renderer_snapshot_serial := 0

func get_renderer_diagnostic_snapshot() -> Dictionary:
	diagnostic_renderer_snapshot_mutex.lock()
	var copy := diagnostic_renderer_snapshot.duplicate(true)
	diagnostic_renderer_snapshot_mutex.unlock()
	return copy

func publish_renderer_diagnostic_snapshot(size: Vector2i, raster_size: Vector2i, views: int, scale: int, msaa: int) -> void:
	if not gpu_timestamp_diagnostics_enabled:
		return
	diagnostic_renderer_snapshot_serial += 1
	var snapshot := {
		"callback_serial": render_callback_serial, "published_ticks_usec": Time.get_ticks_usec(), "snapshot_serial": diagnostic_renderer_snapshot_serial,
		"render_target_id": temporal_history_last_render_target_id, "internal_size": size, "raster_size": raster_size, "view_count": views, "resolution_divisor": scale, "resolution_enum": resolution_scale, "msaa": msaa,
		"temporal_rebuilds": temporal_history_accumulation_rebuild_count,
		"temporal_last_rebuild_reason": temporal_history_last_rebuild_reason,
		"temporal_zero_history_callbacks": temporal_history_zero_packed_decay_callback_count,
		"temporal_requested_decay": temporal_history_last_requested_decay,
		"temporal_packed_decay": temporal_history_last_packed_decay,
		"shadow_near_dispatches": cloud_shadow_dispatch_count[0], "shadow_far_dispatches": cloud_shadow_dispatch_count[1],
		"shadow_near_resolution": cloud_shadow_resolutions[0], "shadow_far_resolution": cloud_shadow_resolutions[1],
		"shadow_near_pending": cloud_shadow_pending[0], "shadow_far_pending": cloud_shadow_pending[1], "shadow_error": cloud_shadow_last_error,
		"retired_generations": retired_generations.size(),
	}
	diagnostic_renderer_snapshot_mutex.lock()
	diagnostic_renderer_snapshot = snapshot
	diagnostic_renderer_snapshot_mutex.unlock()

func get_temporal_history_diagnostics() -> Dictionary:
	return {
		"terrain_sun_descriptor_set_present": _terrain_sun_bindings_present,
		"history_policy": Vector4(float(depth_history_rejection_enabled), history_depth_tolerance_multiplier, float(cloud_history_compatibility_enabled), float(lightning_history_isolation_enabled)),
		"history_gate_band": Vector2(cloud_history_gate_alpha_low, cloud_history_gate_alpha_high),
		"callback_z_far": diagnostic_last_callback_z_far,
		"last_continuity_excess": temporal_history_last_continuity_excess,
		"last_continuity_term": temporal_history_last_continuity_term,
		"last_continuity_multiplier": temporal_history_last_continuity_multiplier,
		"continuity_decay_callback_count": temporal_history_continuity_decay_callback_count,
		"continuity_hard_reset_count": temporal_history_continuity_hard_reset_count,
		"callback_serial": render_callback_serial,
		"explicit_refresh_request_count": temporal_history_explicit_refresh_request_count,
		"accumulation_resource_rebuild_count": temporal_history_accumulation_rebuild_count,
		"last_accumulation_rebuild_reason": temporal_history_last_rebuild_reason,
		"render_target_change_count": temporal_history_render_target_change_count,
		"zero_packed_history_callback_count": temporal_history_zero_packed_decay_callback_count,
		"last_requested_decay": temporal_history_last_requested_decay,
		"last_packed_decay": temporal_history_last_packed_decay,
		"last_render_target_id": temporal_history_last_render_target_id,
		"last_internal_size": temporal_history_last_internal_size,
		"last_raster_size": temporal_history_last_raster_size,
		"ping_pong_selector_is_a": accumulation_is_a,
		"source_reset_since_last_callback": temporal_history_last_callback_source_reset,
		"explicit_refresh_pending": temporal_history_explicit_refresh_pending,
	}

# Main-thread bridge publication. Replace the dictionary reference so one
# compositor callback observes one immutable frame snapshot.
func set_gameplay_cloud_frame(frame: Transform3D, around: float, across: float, radius: float) -> void:
	gameplay_cloud_frame_snapshot = {"frame": frame, "around": around, "across": across, "radius": radius}

static func _gameplay_frame_is_finite(frame: Transform3D) -> bool:
	for value in [frame.basis.x, frame.basis.y, frame.basis.z, frame.origin]:
		if not is_finite(value.x) or not is_finite(value.y) or not is_finite(value.z):
			return false
	return true

# Express the previous callback camera in the current presentation coordinates.
# Canonical subtraction stays in doubles; only the small relative arc enters a
# Transform3D. This preserves history across floating-origin moves without
# adding another transform to the shader's existing uniform buffer.
static func gameplay_frame_remap(current: Dictionary, previous: Dictionary,
		previous_camera: Transform3D, jump_limit: float) -> Dictionary:
	var has_contract := not current.is_empty() or not previous.is_empty()
	var result := {"has_contract": has_contract, "valid": false, "mapped_camera": previous_camera}
	if current.is_empty() or previous.is_empty():
		return result
	var radius := float(current.get("radius", NAN))
	var old_radius := float(previous.get("radius", NAN))
	var around := float(current.get("around", NAN))
	var old_around := float(previous.get("around", NAN))
	var across := float(current.get("across", NAN))
	var old_across := float(previous.get("across", NAN))
	if not is_finite(radius) or radius <= 0.0 or radius != old_radius \
			or not is_finite(around) or not is_finite(old_around) \
			or not is_finite(across) or not is_finite(old_across):
		return result
	var circumference := TAU * radius
	var delta := fposmod(around - old_around + circumference * .5, circumference) - circumference * .5
	var across_delta := across - old_across
	if absf(delta) > jump_limit or absf(across_delta) > jump_limit:
		return result
	var frame: Transform3D = current.get("frame", Transform3D.IDENTITY)
	var old_frame: Transform3D = previous.get("frame", Transform3D.IDENTITY)
	if not _gameplay_frame_is_finite(frame) or not _gameplay_frame_is_finite(old_frame):
		return result
	var determinant := frame.basis.determinant()
	var old_determinant := old_frame.basis.determinant()
	if not is_finite(determinant) or not is_finite(old_determinant) \
			or absf(determinant) <= .000001 or absf(old_determinant) <= .000001:
		return result
	var theta := delta / radius
	var sine := sin(theta)
	var cosine := cos(theta)
	var arc := Transform3D(
		Basis(Vector3(cosine, sine, 0), Vector3(-sine, cosine, 0), Vector3(0, 0, 1)),
		Vector3(radius * sine, 2.0 * radius * pow(sin(theta * .5), 2.0), across_delta))
	var candidate := (old_frame * arc * frame.affine_inverse()).affine_inverse() * previous_camera
	if _gameplay_frame_is_finite(candidate):
		result.valid = true
		result.mapped_camera = candidate
	return result

func refresh_compute():
	temporal_history_explicit_refresh_request_count += 1
	temporal_history_explicit_refresh_pending = true
	# Publication is a contract with scene materials, not merely an RD handle.
	# Stop handing out the old wrappers immediately; their RIDs remain alive in
	# the delayed-retirement generation until submitted draws have completed.
	invalidate_gpu_cloud_shadow_publication()
	invalidate_cloud_backdrop_texture_publication()
	maskDrawnRid = RID()
	last_size = Vector2i.ZERO

func update_mask(newMask : RID):
	temporal_history_explicit_refresh_request_count += 1
	temporal_history_explicit_refresh_pending = true
	invalidate_gpu_cloud_shadow_publication()
	invalidate_cloud_backdrop_texture_publication()
	maskDrawnRid = newMask
	last_size = Vector2i.ZERO

# C# configures a cascade on the main thread; dispatch happens from the
# compositor callback on the render thread.  `center` is the zero-height
# ground datum, while floor/ceiling are distances along `up`.
func configure_gpu_cloud_shadow_cascade(cascade: int, resolution: int, center: Vector3,
		around: Vector3, across: Vector3, up: Vector3,
		canonical_center: Vector2, ring_radius: float, radius: float,
				sun_direction: Vector3, floor: float, ceiling: float,
				vertical_samples: int, extinction: float,
				reset_history: bool = false, slice_count: int = 1) -> bool:
	if cascade < 0 or cascade >= GPU_CLOUD_SHADOW_CASCADES:
		cloud_shadow_last_error = "Invalid cloud shadow cascade index."
		return false
	if resolution < 8 or resolution > 2048 or ring_radius <= 1.0 or radius <= 0.0 or ceiling <= floor or vertical_samples < 1 or vertical_samples > 64:
		cloud_shadow_last_error = "Invalid cloud shadow cascade configuration."
		return false
	if around.length_squared() < 0.0001 or across.length_squared() < 0.0001 or up.length_squared() < 0.0001 or sun_direction.length_squared() < 0.0001:
		cloud_shadow_last_error = "Cloud shadow axes and sun direction must be non-zero."
		return false
	cloud_shadow_configs[cascade] = {
		"resolution": resolution,
		"center": center,
		"around": around.normalized(),
		"across": across.normalized(),
		"up": up.normalized(),
		"canonical_center": canonical_center,
		"ring_radius": ring_radius,
		"radius": radius,
		"sun_direction": sun_direction.normalized(),
		"floor": floor,
		"ceiling": ceiling,
				"vertical_samples": vertical_samples,
				"extinction": maxf(extinction, 0.0),
		"reset_history": reset_history,
		# 0/1 = legacy single dispatch; N > 1 spreads one refresh over N frames.
		"slice_count": clampi(slice_count, 1, GPU_CLOUD_SHADOW_MAX_SLICES),
	}
	if reset_history:
		cloud_shadow_reset_history[cascade] = true
	cloud_shadow_pending[cascade] = true
	cloud_shadow_last_error = ""
	return true

func request_gpu_cloud_shadow_update(cascade: int = -1):
	if cascade < 0:
		for i in range(GPU_CLOUD_SHADOW_CASCADES):
			if not cloud_shadow_configs[i].is_empty(): cloud_shadow_pending[i] = true
	elif cascade < GPU_CLOUD_SHADOW_CASCADES and not cloud_shadow_configs[cascade].is_empty():
		cloud_shadow_pending[cascade] = true

func set_gpu_cloud_shadow_generation_enabled(value: bool):
	cloud_shadow_generation_enabled = value

func get_gpu_cloud_shadow_texture(cascade: int) -> Texture2DRD:
	if cascade < 0 or cascade >= GPU_CLOUD_SHADOW_CASCADES:
		return null
	return cloud_shadow_published_texture_wrappers[cascade]

# Returns Sunshine's exact screen-space cloud color in rgb and cloud density
# in alpha. Publication starts only after the first dispatch for this resource
# generation, so canvas consumers never sample an uninitialized RD texture.
func get_cloud_backdrop_texture(view: int = 0) -> Texture2DRD:
	if view < 0 or view >= cloud_backdrop_published_texture_wrappers.size():
		return null
	return cloud_backdrop_published_texture_wrappers[view]

func invalidate_cloud_backdrop_texture_publication():
	for view in range(cloud_backdrop_published_texture_wrappers.size()):
		cloud_backdrop_published_texture_wrappers[view] = null

# Called from the main thread immediately before a whole-compositor refresh.
# Dropping publication is deliberately non-destructive: receiver materials have
# already been rebound to their clear fallback, while the render callback keeps
# the old wrapper and RD resources alive through its retirement delay.
func invalidate_gpu_cloud_shadow_publication():
	cloud_shadow_published_texture_wrappers = [null, null]
	cloud_shadow_published_resolutions = [Vector2i.ZERO, Vector2i.ZERO]

func get_gpu_cloud_shadow_status() -> Dictionary:
	return {
		"available": cloud_shadow_pipeline.is_valid(),
		"error": cloud_shadow_last_error,
		"near_resolution": cloud_shadow_resolutions[0],
		"far_resolution": cloud_shadow_resolutions[1],
		"near_published_resolution": cloud_shadow_published_resolutions[0],
		"far_published_resolution": cloud_shadow_published_resolutions[1],
		"near_dispatches": cloud_shadow_dispatch_count[0],
		"far_dispatches": cloud_shadow_dispatch_count[1],
		"near_phase": cloud_shadow_phase[0],
		"far_phase": cloud_shadow_phase[1],
		"near_reset_history": cloud_shadow_reset_history[0],
		"far_reset_history": cloud_shadow_reset_history[1],
		"generation_enabled": cloud_shadow_generation_enabled,
		"near_slice_progress": cloud_shadow_slice_progress[0],
		"far_slice_progress": cloud_shadow_slice_progress[1],
		"near_pending": cloud_shadow_pending[0],
		"far_pending": cloud_shadow_pending[1],
		"retired_cascade_generations": retired_cloud_shadow_cascades.size(),
		"retired_compositor_generations": retired_generations.size(),
	}

func retire_gpu_cloud_shadow_cascade(cascade: int):
	var stage := cascade + GPU_CLOUD_SHADOW_CASCADES
	cloud_shadow_slice_progress[cascade] = 0
	cloud_shadow_slice_configs[cascade] = {}
	if not cloud_shadow_uniform_sets[cascade].is_valid() \
			and not cloud_shadow_param_buffers[cascade].is_valid() \
			and not cloud_shadow_textures[cascade].is_valid() \
			and not cloud_shadow_textures[stage].is_valid():
		return
	retired_cloud_shadow_cascades.append({
		"release_after": render_callback_serial + max(2, rd.get_frame_delay() + 2),
		"uniform_set": cloud_shadow_uniform_sets[cascade],
		"stage_uniform_set": cloud_shadow_uniform_sets[stage],
		"param_buffer": cloud_shadow_param_buffers[cascade],
		"texture": cloud_shadow_textures[cascade],
		"stage_texture": cloud_shadow_textures[stage],
		# Keep the Godot wrapper alive with the RID until the old GPU work is done.
		"wrapper": cloud_shadow_texture_wrappers[cascade],
	})
	cloud_shadow_uniform_sets[stage] = RID()
	cloud_shadow_textures[stage] = RID()

func retire_completed_gpu_cloud_shadow_cascades(force: bool = false):
	var remaining : Array[Dictionary] = []
	for retired in retired_cloud_shadow_cascades:
		if not force and render_callback_serial < retired.release_after:
			remaining.append(retired)
			continue
		for key in ["uniform_set", "stage_uniform_set", "param_buffer", "texture", "stage_texture"]:
			var rid = retired[key]
			if rid is RID and rid.is_valid(): rd.free_rid(rid)
	retired_cloud_shadow_cascades = remaining

func ensure_gpu_cloud_shadow_cascade(cascade: int, configuration: Dictionary) -> bool:
	var resolution := Vector2i(configuration["resolution"], configuration["resolution"])
	if cloud_shadow_textures[cascade].is_valid() and cloud_shadow_resolutions[cascade] == resolution and cloud_shadow_uniform_sets[cascade].is_valid():
		return true
	var texture_format := RDTextureFormat.new()
	texture_format.texture_type = RenderingDevice.TEXTURE_TYPE_2D
	texture_format.format = RenderingDevice.DATA_FORMAT_R16G16_SFLOAT
	texture_format.width = resolution.x
	texture_format.height = resolution.y
	# CAN_COPY_TO receives the completed staging buffer of a sliced refresh;
	# CAN_COPY_FROM allows GPU readback (used by the slicing equivalence test).
	texture_format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT \
		| RenderingDevice.TEXTURE_USAGE_CAN_COPY_TO_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
	var replacement_texture := rd.texture_create(texture_format, RDTextureView.new())
	var replacement_param_buffer := rd.uniform_buffer_create(GPU_CLOUD_SHADOW_PARAM_BYTES)
	if not replacement_texture.is_valid() or not replacement_param_buffer.is_valid():
		if replacement_texture.is_valid(): rd.free_rid(replacement_texture)
		if replacement_param_buffer.is_valid(): rd.free_rid(replacement_param_buffer)
		cloud_shadow_last_error = "Unable to create cloud shadow texture or parameter buffer."
		return false
	var replacement_uniform_set := _build_cloud_shadow_uniform_set(replacement_texture, replacement_param_buffer)
	if not replacement_uniform_set.is_valid():
		rd.free_rid(replacement_param_buffer)
		rd.free_rid(replacement_texture)
		cloud_shadow_last_error = "Unable to create cloud shadow uniform set."
		return false
	var wrapper := Texture2DRD.new()
	wrapper.texture_rd_rid = replacement_texture
	# Commit only after the entire replacement is valid. Do not free a published
	# cascade from this callback: it may still be sampled by an already-submitted
	# receiver draw, so ownership moves to the delayed retirement queue.
	retire_gpu_cloud_shadow_cascade(cascade)
	cloud_shadow_uniform_sets[cascade] = replacement_uniform_set
	cloud_shadow_param_buffers[cascade] = replacement_param_buffer
	cloud_shadow_textures[cascade] = replacement_texture
	cloud_shadow_texture_wrappers[cascade] = wrapper
	cloud_shadow_resolutions[cascade] = resolution
	cloud_shadow_reset_history[cascade] = true
	cloud_shadow_phase[cascade] = 0
	cloud_shadow_last_error = ""
	return true

# Creates the staging back buffer a sliced refresh renders into. It is never
# sampled by receivers; only a completed refresh is copied into the front texture.
func ensure_gpu_cloud_shadow_stage(cascade: int) -> bool:
	var stage := cascade + GPU_CLOUD_SHADOW_CASCADES
	if cloud_shadow_textures[stage].is_valid() and cloud_shadow_uniform_sets[stage].is_valid():
		return true
	if not cloud_shadow_textures[cascade].is_valid() or not cloud_shadow_param_buffers[cascade].is_valid():
		return false
	var resolution := cloud_shadow_resolutions[cascade]
	var texture_format := RDTextureFormat.new()
	texture_format.texture_type = RenderingDevice.TEXTURE_TYPE_2D
	texture_format.format = RenderingDevice.DATA_FORMAT_R16G16_SFLOAT
	texture_format.width = resolution.x
	texture_format.height = resolution.y
	texture_format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
	var stage_texture := rd.texture_create(texture_format, RDTextureView.new())
	if not stage_texture.is_valid():
		return false
	var stage_set := _build_cloud_shadow_uniform_set(stage_texture, cloud_shadow_param_buffers[cascade])
	if not stage_set.is_valid():
		rd.free_rid(stage_texture)
		return false
	cloud_shadow_textures[stage] = stage_texture
	cloud_shadow_uniform_sets[stage] = stage_set
	cloud_shadow_slice_progress[cascade] = 0
	return true

func dispatch_gpu_cloud_shadows():
	if not cloud_shadow_generation_enabled or not cloud_shadow_pipeline.is_valid() or not general_data_buffer.is_valid():
		return
	# Requests can overlap after resource refreshes. Alternate at the producer
	# too, so recurring near requests cannot starve an already-pending far map.
	for offset_in_queue in range(GPU_CLOUD_SHADOW_CASCADES):
		var cascade := (cloud_shadow_next_cascade + offset_in_queue) % GPU_CLOUD_SHADOW_CASCADES
		if not cloud_shadow_pending[cascade] or cloud_shadow_configs[cascade].is_empty():
			continue
		var configuration = cloud_shadow_configs[cascade]
		# A sliced refresh keeps using the configuration snapshot taken at slice 0,
		# so every slice maps the same world window. If the bridge reconfigures to
		# a different resolution mid-refresh, discard the partial staging result
		# and restart from the new configuration.
		if cloud_shadow_slice_progress[cascade] > 0:
			var snapshot : Dictionary = cloud_shadow_slice_configs[cascade]
			if snapshot.is_empty() or snapshot["resolution"] != configuration["resolution"]:
				cloud_shadow_slice_progress[cascade] = 0
				cloud_shadow_slice_configs[cascade] = {}
			else:
				configuration = snapshot
		if not ensure_gpu_cloud_shadow_cascade(cascade, configuration):
			continue
		var stage := cascade + GPU_CLOUD_SHADOW_CASCADES
		var size = cloud_shadow_resolutions[cascade]
		var groups_y := int((size.y + 7) / 8)
		var slices := mini(int(configuration.get("slice_count", 1)), groups_y)
		if slices > 1 and not ensure_gpu_cloud_shadow_stage(cascade):
			slices = 1  # Staging buffer unavailable: fall back to the legacy single dispatch.
		var slice_index := cloud_shadow_slice_progress[cascade] if slices > 1 else 0
		if slice_index == 0:
			# A new refresh: snapshot the configuration used by all its slices.
			cloud_shadow_slice_configs[cascade] = configuration.duplicate()
		var first_group := slice_index * groups_y / slices
		var last_group := (slice_index + 1) * groups_y / slices
		var is_final_slice := slice_index >= slices - 1
		var bytes := PackedByteArray()
		bytes.resize(GPU_CLOUD_SHADOW_PARAM_BYTES)
		# The last component carries the first output row of this slice (0 for the
		# legacy full-map dispatch). It formerly held a phase/reset encoding that
		# the shader no longer reads.
		var vectors = [
			Vector4(configuration["center"].x, configuration["center"].y, configuration["center"].z, configuration["radius"]),
			Vector4(configuration["around"].x, configuration["around"].y, configuration["around"].z, configuration["canonical_center"].x),
			Vector4(configuration["across"].x, configuration["across"].y, configuration["across"].z, configuration["canonical_center"].y),
			Vector4(configuration["up"].x, configuration["up"].y, configuration["up"].z, configuration["ring_radius"]),
			Vector4(configuration["sun_direction"].x, configuration["sun_direction"].y, configuration["sun_direction"].z, configuration["floor"]),
			Vector4(configuration["ceiling"], configuration["vertical_samples"], configuration["extinction"], float(first_group * 8)),
		]
		var offset := 0
		for value in vectors:
			bytes.encode_float(offset, value.x); offset += 4
			bytes.encode_float(offset, value.y); offset += 4
			bytes.encode_float(offset, value.z); offset += 4
			bytes.encode_float(offset, value.w); offset += 4
		rd.buffer_update(cloud_shadow_param_buffers[cascade], 0, bytes.size(), bytes)
		var timestamp_begin := gpu_timestamp_cloud_shadow_near_begin_marker if cascade == 0 else gpu_timestamp_cloud_shadow_far_begin_marker
		var timestamp_end := gpu_timestamp_cloud_shadow_near_end_marker if cascade == 0 else gpu_timestamp_cloud_shadow_far_end_marker
		if gpu_timestamp_diagnostics_enabled and timestamp_begin != "" and timestamp_end != "":
			rd.capture_timestamp(timestamp_begin)
		var compute_list := rd.compute_list_begin()
		rd.compute_list_bind_compute_pipeline(compute_list, cloud_shadow_pipeline)
		rd.compute_list_bind_uniform_set(compute_list, cloud_shadow_uniform_sets[stage if slices > 1 else cascade], 0)
		rd.compute_list_dispatch(compute_list, int((size.x + 7) / 8), last_group - first_group, 1)
		rd.compute_list_end()
		if gpu_timestamp_diagnostics_enabled and timestamp_begin != "" and timestamp_end != "":
			rd.capture_timestamp(timestamp_end)
		cloud_shadow_next_cascade = (cascade + 1) % GPU_CLOUD_SHADOW_CASCADES
		if not is_final_slice:
			# Receivers keep sampling the previous, fully consistent front map.
			cloud_shadow_slice_progress[cascade] = slice_index + 1
			# One slice per frame, same one-dispatch-per-frame guarantee as below.
			break
		if slices > 1:
			# Publish atomically: the staging buffer now holds the complete map.
			rd.texture_copy(cloud_shadow_textures[stage], cloud_shadow_textures[cascade],
				Vector3.ZERO, Vector3.ZERO, Vector3(size.x, size.y, 1), 0, 0, 0, 0)
		cloud_shadow_slice_progress[cascade] = 0
		cloud_shadow_slice_configs[cascade] = {}
		cloud_shadow_pending[cascade] = false
		cloud_shadow_dispatch_count[cascade] += 1
		# The main thread can now bind this immutable wrapper on its next frame.
		cloud_shadow_published_texture_wrappers[cascade] = cloud_shadow_texture_wrappers[cascade]
		cloud_shadow_published_resolutions[cascade] = cloud_shadow_resolutions[cascade]
		cloud_shadow_phase[cascade] = 1 - cloud_shadow_phase[cascade]
		cloud_shadow_reset_history[cascade] = false
		cloud_shadow_configs[cascade]["reset_history"] = false
		# Resource refreshes can make both cascades pending together. Keep the
		# producer-level guarantee of at most one compute dispatch per frame.
		break

func add_sample(callable : Callable, position : Vector3):
	#if (positionQueries.size() == 32):
		#print("Max cloud position sample queue reached (32), query failed.")
		#return
	positionQueries.append(position)
	positionQueryCallables.append(callable)

func clear_samples():
	positionQueries.clear()
	positionQueryCallables.clear()

func _init():
	# Composite after transparent geometry so depth-writing surfaces such as the
	# close ocean participate in the cloud depth test.  Running before the
	# transparent pass lets water overpaint clouds even when the cloud volume is
	# physically between the camera and the water surface.
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	access_resolved_depth = true
	access_resolved_color = true
	needs_motion_vectors = true
	RenderingServer.call_on_render_thread(initialize_compute)

func _notification(what):
	if what != NOTIFICATION_PREDELETE or rd == null:
		return
	# A bound clear_compute callable targets an object that is being destroyed.
	# Capture owned resources now; the queued release must not access that object.
	var device := rd
	var sets: Array[RID] = uniform_sets.duplicate()
	for ring_sets in general_data_uniform_sets:
		sets.append_array(ring_sets)
	sets.append_array(cloud_shadow_uniform_sets)
	sets.append(_terrain_sun_set)
	var rids: Array[RID] = [pipeline, shader, prepass_pipeline, prepass_shader,
		postpass_pipeline, postpass_shader, display_pipeline, display_shader,
		display_vertex_array, display_vertex_buffer, nearest_sampler, linear_sampler,
		linear_sampler_no_repeat, light_data_buffer, point_sample_data_buffer,
		resized_depth, far_opaque_depth_fallback, cloud_shadow_pipeline,
		cloud_shadow_shader, _terrain_sun_neutral]
	rids.append_array(general_data_buffers)
	rids.append_array(accumulation_textures)
	rids.append_array(blit_screen_images)
	rids.append_array(cloud_shadow_param_buffers)
	rids.append_array(cloud_shadow_textures)
	var keep_alive: Array = [cloud_shadow_texture_wrappers.duplicate(),
		cloud_shadow_published_texture_wrappers.duplicate(),
		cloud_backdrop_texture_wrappers.duplicate(),
		cloud_backdrop_published_texture_wrappers.duplicate(),
		_terrain_sun_texture_refs.duplicate()]
	for generation in retired_generations:
		sets.append_array(generation.uniform_sets)
		sets.append_array(generation.shadow_uniform_sets)
		rids.append_array(generation.rids)
		keep_alive.append(generation.duplicate())
	for cascade in retired_cloud_shadow_cascades:
		sets.append(cascade.uniform_set)
		sets.append(cascade.stage_uniform_set)
		rids.append(cascade.param_buffer)
		rids.append(cascade.texture)
		rids.append(cascade.stage_texture)
		keep_alive.append(cascade.duplicate())
	for entry in _terrain_sun_retired:
		sets.append(entry.uniform_set)
		keep_alive.append(entry.duplicate())
	# Source noise/depth/color textures and terrain-sun receiver textures are
	# borrowed. Their RIDs are deliberately absent from the owned release list.
	var cleanup := {"sets": sets, "rids": rids, "keep_alive": keep_alive}
	RenderingServer.call_on_render_thread(SunshineCloudsGD._release_shutdown_cleanup.bind(device, cleanup))

static func _release_shutdown_cleanup(device: RenderingDevice, cleanup: Dictionary) -> void:
	if not is_instance_valid(device):
		cleanup.clear()
		return
	var freed := {}
	# Release descriptors before their buffers, textures, and shaders.
	for uniform_set: RID in cleanup["sets"]:
		if uniform_set.is_valid() and not freed.has(uniform_set):
			if device.uniform_set_is_valid(uniform_set): device.free_rid(uniform_set)
			freed[uniform_set] = true
	for rid: RID in cleanup["rids"]:
		if rid.is_valid() and not freed.has(rid):
			device.free_rid(rid)
			freed[rid] = true
	# Texture wrappers and retired generations stay alive through RID release.
	cleanup.clear()

func _retire_current_generation():
	var delay := max(2, rd.get_frame_delay() + 2)
	var generation_id := next_retired_generation_id
	next_retired_generation_id += 1
	var requires_external_texture_ack := not get_signal_connection_list(&"external_texture_retirement_requested").is_empty()
	retired_generations.append({
		"generation_id": generation_id,
		"release_delay": delay,
		"release_after": render_callback_serial + delay if not requires_external_texture_ack else -1,
		"awaiting_external_texture_ack": requires_external_texture_ack,
		"uniform_sets": uniform_sets.duplicate(),
		"shadow_uniform_sets": cloud_shadow_uniform_sets.duplicate(),
		# Texture2DRD is RefCounted. Keep both active and published wrappers alive
		# until the delayed RID release, because receiver materials can retain the
		# prior published object across a compositor rebuild.
		"shadow_texture_wrappers": cloud_shadow_texture_wrappers.duplicate()
			+ cloud_shadow_published_texture_wrappers.duplicate(),
		"cloud_backdrop_texture_wrappers": cloud_backdrop_texture_wrappers.duplicate()
			+ cloud_backdrop_published_texture_wrappers.duplicate(),
		"rids": [pipeline, shader, prepass_pipeline, prepass_shader, postpass_pipeline, postpass_shader,
			display_pipeline, display_shader, display_vertex_array, display_vertex_buffer,
			nearest_sampler, linear_sampler, linear_sampler_no_repeat, light_data_buffer,
			point_sample_data_buffer, resized_depth, cloud_shadow_pipeline, cloud_shadow_shader]
			+ [far_opaque_depth_fallback]
			+ accumulation_textures.duplicate() + blit_screen_images.duplicate()
			+ cloud_shadow_param_buffers.duplicate() + cloud_shadow_textures.duplicate()
	})
	uniform_sets.clear()
	cloud_shadow_uniform_sets = [RID(), RID(), RID(), RID()]
	cloud_shadow_param_buffers = [RID(), RID()]
	cloud_shadow_textures = [RID(), RID(), RID(), RID()]
	cloud_shadow_texture_wrappers = [null, null]
	cloud_shadow_published_texture_wrappers = [null, null]
	cloud_backdrop_texture_wrappers.clear()
	cloud_backdrop_published_texture_wrappers.clear()
	cloud_shadow_resolutions = [Vector2i.ZERO, Vector2i.ZERO]
	cloud_shadow_published_resolutions = [Vector2i.ZERO, Vector2i.ZERO]
	for cascade in range(GPU_CLOUD_SHADOW_CASCADES):
		cloud_shadow_pending[cascade] = not cloud_shadow_configs[cascade].is_empty()
		cloud_shadow_phase[cascade] = 0
		cloud_shadow_reset_history[cascade] = true
		cloud_shadow_slice_progress[cascade] = 0
		cloud_shadow_slice_configs[cascade] = {}
	pipeline = RID(); shader = RID(); prepass_pipeline = RID(); prepass_shader = RID()
	postpass_pipeline = RID(); postpass_shader = RID(); display_pipeline = RID(); display_shader = RID()
	display_vertex_array = RID(); display_vertex_buffer = RID()
	nearest_sampler = RID(); linear_sampler = RID(); linear_sampler_no_repeat = RID()
	light_data_buffer = RID(); point_sample_data_buffer = RID(); resized_depth = RID()
	far_opaque_depth_fallback = RID()
	cloud_shadow_pipeline = RID(); cloud_shadow_shader = RID()
	accumulation_textures.clear(); blit_screen_images.clear()
	if requires_external_texture_ack:
		call_deferred("_notify_external_texture_retirement", generation_id)

func _notify_external_texture_retirement(generation_id: int):
	emit_signal("external_texture_retirement_requested", generation_id)

func acknowledge_external_texture_retirement(generation_id: int):
	for generation in retired_generations:
		if generation.generation_id != generation_id or not generation.awaiting_external_texture_ack:
			continue
		generation.awaiting_external_texture_ack = false
		generation.release_after = render_callback_serial + generation.release_delay
		return

func _free_generation(generation: Dictionary):
	for uniform_set in generation.uniform_sets:
		if rd.uniform_set_is_valid(uniform_set): rd.free_rid(uniform_set)
	for uniform_set in generation.shadow_uniform_sets:
		if rd.uniform_set_is_valid(uniform_set): rd.free_rid(uniform_set)
	for rid in generation.rids:
		if rid is RID and rid.is_valid(): rd.free_rid(rid)

func _retire_completed_generations(force: bool = false):
	var remaining : Array[Dictionary] = []
	for generation in retired_generations:
		if force or (not generation.awaiting_external_texture_ack and render_callback_serial >= generation.release_after):
			_free_generation(generation)
		else:
			remaining.append(generation)
	retired_generations = remaining

func clear_compute(preserve_general_data_buffers: bool = false, retire_generation: bool = false):
	if rd:
		if retire_generation:
			_retire_current_generation()
			return
		for uniform_set in uniform_sets:
			if rd.uniform_set_is_valid(uniform_set):
				rd.free_rid(uniform_set)
		uniform_sets.clear()
		# Retired descriptors still reference the shared GenericData ring, so
		# drain them before a full clear releases that ring.
		retire_completed_gpu_cloud_shadow_cascades(true)
		_retire_completed_generations(true)
		_retire_terrain_sun_sets(true)
		if _terrain_sun_set.is_valid() and rd.uniform_set_is_valid(_terrain_sun_set): rd.free_rid(_terrain_sun_set)
		_terrain_sun_set = RID()
		_terrain_sun_texture_refs.clear(); _terrain_sun_texture_rids.clear()
		if _terrain_sun_neutral.is_valid(): rd.free_rid(_terrain_sun_neutral)
		_terrain_sun_neutral = RID()
		clear_gpu_cloud_shadow_resources()
		if pipeline.is_valid():
			rd.free_rid(pipeline)
		pipeline = RID()

		if shader.is_valid():
			rd.free_rid(shader)
		shader = RID()

		if prepass_pipeline.is_valid():
			rd.free_rid(prepass_pipeline)
		prepass_pipeline = RID()

		if prepass_shader.is_valid():
			rd.free_rid(prepass_shader)
		prepass_shader = RID()

		if postpass_pipeline.is_valid():
			rd.free_rid(postpass_pipeline)
		postpass_pipeline = RID()

		if postpass_shader.is_valid():
			rd.free_rid(postpass_shader)
		postpass_shader = RID()

		if display_pipeline.is_valid():
			rd.free_rid(display_pipeline)
		display_pipeline = RID()

		if display_shader.is_valid():
			rd.free_rid(display_shader)
		display_shader = RID()

		if display_vertex_array.is_valid():
			rd.free_rid(display_vertex_array)
		display_vertex_array = RID()

		if display_vertex_buffer.is_valid():
			rd.free_rid(display_vertex_buffer)
		display_vertex_buffer = RID()

		if nearest_sampler.is_valid():
			rd.free_rid(nearest_sampler)
		nearest_sampler = RID()
		
		if linear_sampler.is_valid():
			rd.free_rid(linear_sampler)
		linear_sampler = RID()
		
		if linear_sampler_no_repeat.is_valid():
			rd.free_rid(linear_sampler_no_repeat)
		linear_sampler_no_repeat = RID()
		
		if not preserve_general_data_buffers:
			for buffer in general_data_buffers:
				if buffer.is_valid():
					rd.free_rid(buffer)
			general_data_buffers.clear()
		general_data_uniform_sets.clear()
		general_data_buffer = general_data_buffers[0] if preserve_general_data_buffers else RID()
		
		if light_data_buffer.is_valid():
			rd.free_rid(light_data_buffer)
		light_data_buffer = RID()
		
		if point_sample_data_buffer.is_valid():
			rd.free_rid(point_sample_data_buffer)
		point_sample_data_buffer = RID()
		
		if resized_depth.is_valid():
			rd.free_rid(resized_depth)
		resized_depth = RID()
		if far_opaque_depth_fallback.is_valid():
			rd.free_rid(far_opaque_depth_fallback)
		far_opaque_depth_fallback = RID()
		
		if accumulation_textures.size() > 0:
			for item in accumulation_textures:
				if item.is_valid():
					rd.free_rid(item)
			accumulation_textures.clear()
		cloud_backdrop_texture_wrappers.clear()
		cloud_backdrop_published_texture_wrappers.clear()

		if blit_screen_images.size() > 0:
			for item in blit_screen_images:
				if item.is_valid():
					rd.free_rid(item)
			blit_screen_images.clear()

func clear_gpu_cloud_shadow_resources():
	if not rd:
		return
	for cascade in range(GPU_CLOUD_SHADOW_CASCADES):
		if cloud_shadow_uniform_sets[cascade].is_valid():
			rd.free_rid(cloud_shadow_uniform_sets[cascade])
		cloud_shadow_uniform_sets[cascade] = RID()
		if cloud_shadow_param_buffers[cascade].is_valid():
			rd.free_rid(cloud_shadow_param_buffers[cascade])
		cloud_shadow_param_buffers[cascade] = RID()
		if cloud_shadow_textures[cascade].is_valid():
			rd.free_rid(cloud_shadow_textures[cascade])
		cloud_shadow_textures[cascade] = RID()
		var stage := cascade + GPU_CLOUD_SHADOW_CASCADES
		if cloud_shadow_uniform_sets[stage].is_valid():
			rd.free_rid(cloud_shadow_uniform_sets[stage])
		cloud_shadow_uniform_sets[stage] = RID()
		if cloud_shadow_textures[stage].is_valid():
			rd.free_rid(cloud_shadow_textures[stage])
		cloud_shadow_textures[stage] = RID()
		cloud_shadow_slice_progress[cascade] = 0
		cloud_shadow_slice_configs[cascade] = {}
		cloud_shadow_texture_wrappers[cascade] = null
		cloud_shadow_published_texture_wrappers[cascade] = null
		cloud_shadow_resolutions[cascade] = Vector2i.ZERO
		cloud_shadow_published_resolutions[cascade] = Vector2i.ZERO
		cloud_shadow_pending[cascade] = not cloud_shadow_configs[cascade].is_empty()
	if cloud_shadow_pipeline.is_valid():
		rd.free_rid(cloud_shadow_pipeline)
	cloud_shadow_pipeline = RID()
	if cloud_shadow_shader.is_valid():
		rd.free_rid(cloud_shadow_shader)
	cloud_shadow_shader = RID()

func initialize_compute():
	first_run = true
	previous_callback_menu_around_precise = menu_cloud_frame_around_precise
	previous_callback_bulk_wind = Vector2(extra_large_scale_clouds_position.x, extra_large_scale_clouds_position.z)
	previous_callback_detail_wind = detail_clouds_position
	if not rd:
		rd = RenderingServer.get_rendering_device()
		if not rd:
				enabled = false
				printerr("No rendering device on load.")
				return
	var preserve_general_data_buffers := general_data_buffers.size() == GENERAL_DATA_RING_SIZE
	var retire_generation := pipeline.is_valid() or prepass_pipeline.is_valid() or postpass_pipeline.is_valid()
	clear_compute(preserve_general_data_buffers, retire_generation)
	
	var sampler_state = RDSamplerState.new()
	sampler_state.min_filter = RenderingDevice.SAMPLER_FILTER_NEAREST
	sampler_state.mag_filter = RenderingDevice.SAMPLER_FILTER_NEAREST
	sampler_state.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
	sampler_state.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
	sampler_state.repeat_w = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
	nearest_sampler = rd.sampler_create(sampler_state)
	
	var linear_sampler_state = RDSamplerState.new()
	linear_sampler_state.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	linear_sampler_state.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	linear_sampler_state.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
	linear_sampler_state.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
	linear_sampler_state.repeat_w = RenderingDevice.SAMPLER_REPEAT_MODE_REPEAT
	linear_sampler = rd.sampler_create(linear_sampler_state)
	
	var linear_sampler_state_no_repeat = RDSamplerState.new()
	linear_sampler_state_no_repeat.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	linear_sampler_state_no_repeat.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	linear_sampler_state_no_repeat.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	linear_sampler_state_no_repeat.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	linear_sampler_state_no_repeat.repeat_w = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	linear_sampler_no_repeat = rd.sampler_create(linear_sampler_state_no_repeat)
	
	if not dither_noise:
		dither_noise = ResourceLoader.load("res://addons/SunshineClouds2/NoiseTextures/bluenoise_Dither.png")
	if not height_gradient:
		height_gradient = ResourceLoader.load("res://addons/SunshineClouds2/NoiseTextures/HeightGradient.tres")
	if not extra_large_noise_patterns:
		extra_large_noise_patterns = ResourceLoader.load("res://addons/SunshineClouds2/NoiseTextures/ExtraLargeScaleNoise.tres")
	if not large_scale_noise:
		large_scale_noise = ResourceLoader.load("res://addons/SunshineClouds2/NoiseTextures/LargeScaleNoise.tres")
	if not medium_scale_noise:
		medium_scale_noise = ResourceLoader.load("res://addons/SunshineClouds2/NoiseTextures/MediumScaleNoise.tres")
	if not small_scale_noise:
		small_scale_noise = ResourceLoader.load("res://addons/SunshineClouds2/NoiseTextures/SmallScaleNoise.tres")
	if not curl_noise:
		curl_noise = ResourceLoader.load("res://addons/SunshineClouds2/NoiseTextures/curl_noise_varied.tga")
	
	if not compute_shader:
		compute_shader = ResourceLoader.load("res://addons/SunshineClouds2/SunshineCloudsCompute.glsl")
	if not cloud_shadow_compute_shader:
		cloud_shadow_compute_shader = ResourceLoader.load("res://addons/SunshineClouds2/SunshineCloudShadowCompute.glsl")
	if not pre_pass_compute_shader:
		pre_pass_compute_shader = ResourceLoader.load("res://addons/SunshineClouds2/SunshineCloudsPreCompute.glsl")
	var display_shader_file : RDShaderFile
	if msaa_mode == RenderingServer.ViewportMSAA.VIEWPORT_MSAA_DISABLED:
		post_pass_compute_shader = ResourceLoader.load("res://addons/SunshineClouds2/SunshineCloudsPostCompute.glsl")
		display_shader_file = ResourceLoader.load("res://addons/SunshineClouds2/SunshineCloudsDisplay.glsl")
	else:
		post_pass_compute_shader = ResourceLoader.load("res://addons/SunshineClouds2/SunshineCloudsPostCompute.msaa.glsl")
		display_shader_file = ResourceLoader.load("res://addons/SunshineClouds2/SunshineCloudsDisplay.msaa.glsl")

	if not compute_shader or not pre_pass_compute_shader or not post_pass_compute_shader or not display_shader_file:
		enabled = false
		printerr("No Shader found on load.")
		clear_compute()
		return
	
	
	var prepass_shader_spirv = pre_pass_compute_shader.get_spirv()
	prepass_shader = rd.shader_create_from_spirv(prepass_shader_spirv)
	if prepass_shader.is_valid():
		prepass_pipeline = rd.compute_pipeline_create(prepass_shader)
	else:
		enabled = false
		printerr("Prepass Shader failed to compile.")
		clear_compute()
		return
	
	
	var shader_spirv = compute_shader.get_spirv()
	_terrain_sun_bindings_present = _has_terrain_sun_bindings(shader_spirv.get_stage_bytecode(RenderingDevice.SHADER_STAGE_COMPUTE))
	shader = rd.shader_create_from_spirv(shader_spirv)
	if shader.is_valid():
		pipeline = rd.compute_pipeline_create(shader)
	else:
		enabled = false
		printerr("Shader failed to compile.")
		clear_compute()
		return
	
	
	var postpass_shader_spirv = post_pass_compute_shader.get_spirv()
	postpass_shader = rd.shader_create_from_spirv(postpass_shader_spirv)
	if postpass_shader.is_valid():
		postpass_pipeline = rd.compute_pipeline_create(postpass_shader)
	else:
		enabled = false
		printerr("Post pass Shader failed to compile.")
		clear_compute()
		return


	var display_shader_spirv = display_shader_file.get_spirv()
	display_shader = rd.shader_create_from_spirv(display_shader_spirv)
	if not display_shader.is_valid():
		enabled = false
		printerr("Display Shader failed to compile.")
		clear_compute()
		return

	# Shadow production is optional: a failed import must never disable the
	# established Sunshine compositor or its CPU density-query fallback.
	if cloud_shadow_compute_shader:
		cloud_shadow_shader = rd.shader_create_from_spirv(cloud_shadow_compute_shader.get_spirv())
		if cloud_shadow_shader.is_valid():
			cloud_shadow_pipeline = rd.compute_pipeline_create(cloud_shadow_shader)
			cloud_shadow_last_error = ""
		else:
			cloud_shadow_last_error = "Cloud shadow compute shader failed to compile."
	else:
		cloud_shadow_last_error = "Cloud shadow compute shader resource was not found."


	var display_vertex_attributes : Array[RDVertexAttribute] = [RDVertexAttribute.new()]
	display_vertex_attributes[0].format = RenderingDevice.DataFormat.DATA_FORMAT_R32G32_SFLOAT
	display_vertex_attributes[0].frequency = RenderingDevice.VERTEX_FREQUENCY_VERTEX
	display_vertex_attributes[0].location = 0
	display_vertex_attributes[0].offset = 0
	display_vertex_attributes[0].stride = 8

	display_vertex_format = rd.vertex_format_create(display_vertex_attributes)

	# full-screen quad
	var display_vertex_data : PackedByteArray = PackedFloat32Array([
		-1.0, -1.0,
		 1.0, -1.0,
		-1.0,  1.0,
		-1.0,  1.0,
		 1.0, -1.0,
		 1.0,  1.0,
	]).to_byte_array()
	display_vertex_buffer = rd.vertex_buffer_create(display_vertex_data.size(), display_vertex_data)
	display_vertex_array = rd.vertex_array_create(6, display_vertex_format, [display_vertex_buffer])

	last_msaa_mode = msaa_mode

func initialize_raster_pipelines(color_texture : RID, depth_texture : RID):
	var rd := RenderingServer.get_rendering_device()
	assert(rd != null)

	var framebuffer_attachmentformats : Array[RDAttachmentFormat] = [ RDAttachmentFormat.new(), RDAttachmentFormat.new() ]
	framebuffer_attachmentformats[0].format = rd.texture_get_format(color_texture).format
	framebuffer_attachmentformats[0].samples = rd.texture_get_format(color_texture).samples
	framebuffer_attachmentformats[0].usage_flags = rd.texture_get_format(color_texture).usage_bits
	framebuffer_attachmentformats[1].format = rd.texture_get_format(depth_texture).format
	framebuffer_attachmentformats[1].samples = rd.texture_get_format(depth_texture).samples
	framebuffer_attachmentformats[1].usage_flags = rd.texture_get_format(depth_texture).usage_bits
	framebuffer_format = rd.framebuffer_format_create(framebuffer_attachmentformats);

	var pipeline_rasterization_state := RDPipelineRasterizationState.new()

	var pipeline_multisample_state := RDPipelineMultisampleState.new()
	match msaa_mode:
		RenderingServer.ViewportMSAA.VIEWPORT_MSAA_2X:
			pipeline_multisample_state.sample_count = RenderingDevice.TextureSamples.TEXTURE_SAMPLES_2
		RenderingServer.ViewportMSAA.VIEWPORT_MSAA_4X:
			pipeline_multisample_state.sample_count = RenderingDevice.TextureSamples.TEXTURE_SAMPLES_4
		RenderingServer.ViewportMSAA.VIEWPORT_MSAA_8X:
			pipeline_multisample_state.sample_count = RenderingDevice.TextureSamples.TEXTURE_SAMPLES_8
		_:
			pipeline_multisample_state.sample_count = RenderingDevice.TextureSamples.TEXTURE_SAMPLES_1

	var pipeline_depthstencil_state := RDPipelineDepthStencilState.new()

	var pipeline_colorblend_state := RDPipelineColorBlendState.new()
	var pipeline_colorblend_state_attachment := RDPipelineColorBlendStateAttachment.new()
	pipeline_colorblend_state.attachments.append(pipeline_colorblend_state_attachment)

	display_pipeline = rd.render_pipeline_create(display_shader, framebuffer_format, display_vertex_format, RenderingDevice.RenderPrimitive.RENDER_PRIMITIVE_TRIANGLES, pipeline_rasterization_state, pipeline_multisample_state, pipeline_depthstencil_state, pipeline_colorblend_state)

func _render_callback(effect_callback_type, render_data):
	render_callback_serial += 1
	retire_completed_gpu_cloud_shadow_cascades()
	_retire_completed_generations()
	if rd == null:
		initialize_compute()
	elif pipeline.is_valid() and height_gradient and extra_large_noise_patterns and large_scale_noise and medium_scale_noise and small_scale_noise and dither_noise and curl_noise:
		buffers = render_data.get_render_scene_buffers() as RenderSceneBuffersRD
		if buffers:
			msaa_mode = buffers.get_msaa_3d()
			var is_msaa_on := msaa_mode != RenderingServer.ViewportMSAA.VIEWPORT_MSAA_DISABLED

			var size = buffers.get_internal_size()
			if size.x == 0 and size.y == 0:
				return
			
			var resscale = int(pow(2.0, float(resolution_scale)))
			#match resolution_scale:
				#0: 
					#resscale = 1
				#1: 
					#resscale = 2
				#2: 
					#resscale = 4
				#3: 
					#resscale = 8
			
			var new_size = size / resscale
			temporal_history_last_internal_size = size
			temporal_history_last_raster_size = new_size
			temporal_history_last_callback_source_reset = temporal_history_explicit_refresh_pending
			temporal_history_explicit_refresh_pending = false
			var view_count = buffers.get_view_count()
			var rendersceneData : RenderSceneData = render_data.get_render_scene_data();
			# Rebuilt ping-pong textures have no valid temporal history, even when
			# the render-target RID itself is unchanged (for example DR, view,
			# color-format, or MSAA changes). Seed them from this callback's
			# current raymarch, then restore the configured decay next callback.
			var accumulation_resources_rebuilt := false
			
			if size != last_size or uniform_sets == null or uniform_sets.size() != view_count * 4 or color_images.size() == 0 or color_images[0] != buffers.get_color_layer(0) or blit_screen_images.size() == 0 or msaa_mode != last_msaa_mode:
				accumulation_resources_rebuilt = true
				previous_callback_gameplay_cloud_frame = {}
				temporal_history_accumulation_rebuild_count += 1
				if temporal_history_last_callback_source_reset:
					temporal_history_last_rebuild_reason = "explicit_refresh"
				elif size != last_size:
					temporal_history_last_rebuild_reason = "size"
				elif uniform_sets == null:
					temporal_history_last_rebuild_reason = "uniform_sets"
				elif uniform_sets.size() != view_count * 4:
					temporal_history_last_rebuild_reason = "view_count"
				elif color_images.size() == 0 or color_images[0] != buffers.get_color_layer(0):
					temporal_history_last_rebuild_reason = "color_attachment"
				elif blit_screen_images.size() == 0:
					temporal_history_last_rebuild_reason = "blit"
				else:
					temporal_history_last_rebuild_reason = "msaa"
				initialize_compute()
				initialize_raster_pipelines(buffers.get_color_layer(0, is_msaa_on), buffers.get_depth_layer(0, is_msaa_on))

				accumulation_textures.clear()
				cloud_backdrop_texture_wrappers.clear()
				cloud_backdrop_published_texture_wrappers.clear()
				uniform_sets.clear()

				color_images.clear()
				for item in blit_screen_images:
					if item.is_valid():
						rd.free_rid(item)
				blit_screen_images.clear()

				if general_data_buffers.size() != GENERAL_DATA_RING_SIZE:
					general_data_buffers.clear()
					for ring_slot in range(GENERAL_DATA_RING_SIZE):
						general_data_buffers.append(rd.uniform_buffer_create(GENERAL_DATA_BUFFER_BYTES))
					general_data_ring_index = 0
				general_data_uniform_sets.clear()
				for ring_slot in range(GENERAL_DATA_RING_SIZE):
					general_data_uniform_sets.append([])
				general_data_buffer = general_data_buffers[0] # fixed slot used by cloud shadows

				# A valid zero-distance texture keeps the optional far-depth sampler
				# bound before the far compositor has produced its first frame.
				var far_depth_format := RDTextureFormat.new()
				far_depth_format.format = RenderingDevice.DATA_FORMAT_R32_SFLOAT
				far_depth_format.width = 1; far_depth_format.height = 1
				far_depth_format.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT
				far_opaque_depth_fallback = rd.texture_create(far_depth_format, RDTextureView.new(), [PackedByteArray([0, 0, 0, 0])])
				var far_depth_rid := far_opaque_depth_fallback
				if far_opaque_depth_texture != null and is_instance_valid(far_opaque_depth_texture):
					var candidate := RenderingServer.texture_get_rd_texture(far_opaque_depth_texture.get_rid())
					if candidate.is_valid(): far_depth_rid = candidate
				for view in range(view_count):
					color_images.append(buffers.get_color_layer(view, false))

					var depth_image : RID = buffers.get_depth_layer(view, false)

					var blankImageData : PackedByteArray = []
					blankImageData.resize(new_size.x * new_size.y * 4 * 4)
					
					var base_colorformat : RDTextureFormat = rd.texture_get_format(color_images[view])

					var blit_screen_format : RDTextureFormat = rd.texture_get_format(buffers.get_color_layer(view, is_msaa_on))
					blit_screen_format.usage_bits |= RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT
					blit_screen_images.append(rd.texture_create(blit_screen_format, RDTextureView.new()))

					base_colorformat.format = RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT
					base_colorformat.width = new_size.x
					base_colorformat.height = new_size.y
					if diagnostic_temporal_readback:
						base_colorformat.usage_bits |= RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
					
					accumulation_textures.append(rd.texture_create(base_colorformat, RDTextureView.new(), [blankImageData]))
					accumulation_textures.append(rd.texture_create(base_colorformat, RDTextureView.new(), [blankImageData]))

					var backdrop_wrapper := Texture2DRD.new()
					backdrop_wrapper.texture_rd_rid = accumulation_textures[view * 7 + 1]
					cloud_backdrop_texture_wrappers.append(backdrop_wrapper)
					cloud_backdrop_published_texture_wrappers.append(null)
					accumulation_textures.append(rd.texture_create(base_colorformat, RDTextureView.new(), [blankImageData]))
					accumulation_textures.append(rd.texture_create(base_colorformat, RDTextureView.new(), [blankImageData]))
					accumulation_textures.append(rd.texture_create(base_colorformat, RDTextureView.new(), [blankImageData]))
					accumulation_textures.append(rd.texture_create(base_colorformat, RDTextureView.new(), [blankImageData]))
					
					#reflections
					accumulation_textures.append(rd.texture_create(base_colorformat, RDTextureView.new(), [blankImageData]))
					
					var depthformat : RDTextureFormat = rd.texture_get_format(depth_image)
					depthformat.width = new_size.x
					depthformat.height = new_size.y
					depthformat.format = RenderingDevice.DATA_FORMAT_R32_SFLOAT
					depthformat.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT
					if diagnostic_temporal_readback:
						depthformat.usage_bits |= RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
					resized_depth = rd.texture_create(depthformat, RDTextureView.new(), [])

					#region Prepass Compute Shader
					var prepass_uniforms_array : Array[RDUniform] = []
					var prepass_depth_uniform = RDUniform.new()
					prepass_depth_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					prepass_depth_uniform.binding = 0
					prepass_depth_uniform.add_id(nearest_sampler)
					prepass_depth_uniform.add_id(depth_image)
					prepass_uniforms_array.append(prepass_depth_uniform)
					
					var prepass_depth_output_uniform = RDUniform.new()
					prepass_depth_output_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
					prepass_depth_output_uniform.binding = 1
					prepass_depth_output_uniform.add_id(resized_depth)
					prepass_uniforms_array.append(prepass_depth_output_uniform)
					
					var prepass_camera_uniform = RDUniform.new()
					prepass_camera_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
					prepass_camera_uniform.binding = 2
					prepass_camera_uniform.add_id(general_data_buffer)
					prepass_uniforms_array.append(prepass_camera_uniform)
					
					var prepass_uniform_set := rd.uniform_set_create(prepass_uniforms_array, prepass_shader, 0)
					uniform_sets.append(prepass_uniform_set)
					general_data_uniform_sets[0].append(prepass_uniform_set)
					#endregion

					#region Base Compute Shader
					var uniforms_array : Array[RDUniform] = []
					var output_data_uniform = RDUniform.new()
					output_data_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
					output_data_uniform.binding = 0
					output_data_uniform.add_id(accumulation_textures[view * 7])
					uniforms_array.append(output_data_uniform)
					
					var output_color_uniform = RDUniform.new()
					output_color_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
					output_color_uniform.binding = 1
					output_color_uniform.add_id(accumulation_textures[view * 7 + 1])
					uniforms_array.append(output_color_uniform)
					
					var accum1A_uniform = RDUniform.new()
					accum1A_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
					accum1A_uniform.binding = 2
					accum1A_uniform.add_id(accumulation_textures[view * 7 + 2])
					uniforms_array.append(accum1A_uniform)
					
					var accum1B_uniform = RDUniform.new()
					accum1B_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
					accum1B_uniform.binding = 3
					accum1B_uniform.add_id(accumulation_textures[view * 7 + 3])
					uniforms_array.append(accum1B_uniform)
					
					var accum2A_uniform = RDUniform.new()
					accum2A_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
					accum2A_uniform.binding = 4
					accum2A_uniform.add_id(accumulation_textures[view * 7 + 4])
					uniforms_array.append(accum2A_uniform)
					
					var accum2B_uniform = RDUniform.new()
					accum2B_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
					accum2B_uniform.binding = 5
					accum2B_uniform.add_id(accumulation_textures[view * 7 + 5])
					uniforms_array.append(accum2B_uniform)
					
					var depth_uniform = RDUniform.new()
					depth_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					depth_uniform.binding = 6
					depth_uniform.add_id(nearest_sampler)
					depth_uniform.add_id(resized_depth)
					uniforms_array.append(depth_uniform)
					
					var extra_noise_uniform = RDUniform.new()
					extra_noise_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					extra_noise_uniform.binding = 7
					extra_noise_uniform.add_id(linear_sampler)
					extra_noise_uniform.add_id(maskDrawnRid if extra_large_used_as_mask && maskDrawnRid.is_valid() else RenderingServer.texture_get_rd_texture(extra_large_noise_patterns.get_rid()))
					uniforms_array.append(extra_noise_uniform)
					
					var noise_uniform = RDUniform.new()
					noise_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					noise_uniform.binding = 8
					noise_uniform.add_id(linear_sampler)
					noise_uniform.add_id(RenderingServer.texture_get_rd_texture(large_scale_noise.get_rid()))
					uniforms_array.append(noise_uniform)
					
					var medium_noise_uniform = RDUniform.new()
					medium_noise_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					medium_noise_uniform.binding = 9
					medium_noise_uniform.add_id(linear_sampler)
					medium_noise_uniform.add_id(RenderingServer.texture_get_rd_texture(medium_scale_noise.get_rid()))
					uniforms_array.append(medium_noise_uniform)
					
					var small_noise_uniform = RDUniform.new()
					small_noise_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					small_noise_uniform.binding = 10
					small_noise_uniform.add_id(linear_sampler)
					small_noise_uniform.add_id(RenderingServer.texture_get_rd_texture(small_scale_noise.get_rid()))
					uniforms_array.append(small_noise_uniform)
					
					var curl_noise_uniform = RDUniform.new()
					curl_noise_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					curl_noise_uniform.binding = 11
					curl_noise_uniform.add_id(linear_sampler)
					curl_noise_uniform.add_id(RenderingServer.texture_get_rd_texture(curl_noise.get_rid()))
					uniforms_array.append(curl_noise_uniform)
					
					var dither_noise_uniform = RDUniform.new()
					dither_noise_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					dither_noise_uniform.binding = 12
					dither_noise_uniform.add_id(nearest_sampler)
					dither_noise_uniform.add_id(RenderingServer.texture_get_rd_texture(dither_noise.get_rid()))
					uniforms_array.append(dither_noise_uniform)
					
					var height_gradient_uniform = RDUniform.new()
					height_gradient_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					height_gradient_uniform.binding = 13
					height_gradient_uniform.add_id(linear_sampler_no_repeat)
					height_gradient_uniform.add_id(RenderingServer.texture_get_rd_texture(height_gradient.get_rid()))
					uniforms_array.append(height_gradient_uniform)
					
					
					var camera_uniform = RDUniform.new()
					camera_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
					camera_uniform.binding = 14
					camera_uniform.add_id(general_data_buffer)
					uniforms_array.append(camera_uniform)
					
					light_data_buffer = rd.uniform_buffer_create(LIGHT_BUFFER_BYTES)
					var light_data_uniform = RDUniform.new()
					light_data_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
					light_data_uniform.binding = 15
					light_data_uniform.add_id(light_data_buffer)
					uniforms_array.append(light_data_uniform)
					
					var sampleData : PackedByteArray = []
					sampleData.resize(POSITION_QUERY_BUFFER_BYTES)
					point_sample_data_buffer = rd.storage_buffer_create(POSITION_QUERY_BUFFER_BYTES, sampleData)
					var point_sample_data_uniform = RDUniform.new()
					point_sample_data_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
					point_sample_data_uniform.binding = 16
					point_sample_data_uniform.add_id(point_sample_data_buffer)
					uniforms_array.append(point_sample_data_uniform)
					
					var cameraData = rendersceneData.get_uniform_buffer()
					var camera_data_uniform = RDUniform.new()
					camera_data_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
					camera_data_uniform.binding = 17
					camera_data_uniform.add_id(cameraData)
					uniforms_array.append(camera_data_uniform)
					var far_depth_uniform = RDUniform.new()
					far_depth_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					far_depth_uniform.binding = 18
					far_depth_uniform.add_id(nearest_sampler)
					far_depth_uniform.add_id(far_depth_rid)
					uniforms_array.append(far_depth_uniform)
					
					var compute_uniform_set := rd.uniform_set_create(uniforms_array, shader, 0)
					uniform_sets.append(compute_uniform_set)
					general_data_uniform_sets[0].append(compute_uniform_set)
					#endregion

					#region Post Pass Compute Shader
					var postpass_uniforms_array : Array[RDUniform] = []
					var prepass_color_data_uniform = RDUniform.new()
					prepass_color_data_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					prepass_color_data_uniform.binding = 0
					prepass_color_data_uniform.add_id(linear_sampler_no_repeat)
					prepass_color_data_uniform.add_id(accumulation_textures[view * 7])
					postpass_uniforms_array.append(prepass_color_data_uniform)
					
					var prepass_color_uniform = RDUniform.new()
					prepass_color_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					prepass_color_uniform.binding = 1
					prepass_color_uniform.add_id(linear_sampler_no_repeat)
					prepass_color_uniform.add_id(accumulation_textures[view * 7 + 1])
					postpass_uniforms_array.append(prepass_color_uniform)
					
					
					var postpass_reflections_uniform = RDUniform.new()
					postpass_reflections_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
					postpass_reflections_uniform.binding = 2
					postpass_reflections_uniform.add_id(accumulation_textures[view * 7 + 6])
					postpass_uniforms_array.append(postpass_reflections_uniform)
					
					if (reflections_globalshaderparam != ""):
						var newTexture = Texture2DRD.new()
						newTexture.texture_rd_rid = accumulation_textures[view * 7 + 6]
						RenderingServer.global_shader_parameter_set(reflections_globalshaderparam, newTexture)

					var postpass_input_screen_uniform = RDUniform.new()
					postpass_input_screen_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					postpass_input_screen_uniform.binding = 3
					postpass_input_screen_uniform.add_id(nearest_sampler)
					postpass_input_screen_uniform.add_id(buffers.get_color_layer(view, is_msaa_on))
					postpass_uniforms_array.append(postpass_input_screen_uniform)

					var postpass_output_screen_uniform = RDUniform.new()
					postpass_output_screen_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
					postpass_output_screen_uniform.binding = 4
					postpass_output_screen_uniform.add_id(blit_screen_images[view])
					postpass_uniforms_array.append(postpass_output_screen_uniform)

					var postpass_depth_uniform = RDUniform.new()
					postpass_depth_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					postpass_depth_uniform.binding = 5
					postpass_depth_uniform.add_id(nearest_sampler)
					postpass_depth_uniform.add_id(buffers.get_depth_layer(view, is_msaa_on))
					postpass_uniforms_array.append(postpass_depth_uniform)
					
					var postpass_camera_uniform = RDUniform.new()
					postpass_camera_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
					postpass_camera_uniform.binding = 6
					postpass_camera_uniform.add_id(general_data_buffer)
					postpass_uniforms_array.append(postpass_camera_uniform)
					
					var postpass_light_data_uniform = RDUniform.new()
					postpass_light_data_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
					postpass_light_data_uniform.binding = 7
					postpass_light_data_uniform.add_id(light_data_buffer)
					postpass_uniforms_array.append(postpass_light_data_uniform)
					
					var postpass_camera_data_uniform = RDUniform.new()
					postpass_camera_data_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
					postpass_camera_data_uniform.binding = 8
					postpass_camera_data_uniform.add_id(cameraData)
					postpass_uniforms_array.append(postpass_camera_data_uniform)
					var postpass_far_depth_uniform = RDUniform.new()
					postpass_far_depth_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					postpass_far_depth_uniform.binding = 9
					postpass_far_depth_uniform.add_id(nearest_sampler)
					postpass_far_depth_uniform.add_id(far_depth_rid)
					postpass_uniforms_array.append(postpass_far_depth_uniform)
					
					var postpass_uniform_set := rd.uniform_set_create(postpass_uniforms_array, postpass_shader, 0)
					uniform_sets.append(postpass_uniform_set)
					general_data_uniform_sets[0].append(postpass_uniform_set)
					#endregion

					#region Display Shader
					var display_uniforms_array : Array[RDUniform] = []
					var display_screen_texture_uniform = RDUniform.new()
					display_screen_texture_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
					display_screen_texture_uniform.binding = 0
					display_screen_texture_uniform.add_id(nearest_sampler)
					display_screen_texture_uniform.add_id(blit_screen_images[view])
					display_uniforms_array.append(display_screen_texture_uniform)

					uniform_sets.append(rd.uniform_set_create(display_uniforms_array, display_shader, 0))

					# RenderingDevice may reorder buffer_update commands around draws.
					# Bind each in-flight camera slot through its own descriptors so a
					# later callback cannot make an earlier cloud pass observe its UBO.
					for ring_slot in range(1, GENERAL_DATA_RING_SIZE):
						var ring_buffer := general_data_buffers[ring_slot]
						var ring_prepass_camera := RDUniform.new()
						ring_prepass_camera.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
						ring_prepass_camera.binding = 2
						ring_prepass_camera.add_id(ring_buffer)
						prepass_uniforms_array[2] = ring_prepass_camera
						general_data_uniform_sets[ring_slot].append(rd.uniform_set_create(prepass_uniforms_array, prepass_shader, 0))

						var ring_compute_camera := RDUniform.new()
						ring_compute_camera.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
						ring_compute_camera.binding = 14
						ring_compute_camera.add_id(ring_buffer)
						uniforms_array[14] = ring_compute_camera
						general_data_uniform_sets[ring_slot].append(rd.uniform_set_create(uniforms_array, shader, 0))

						var ring_postpass_camera := RDUniform.new()
						ring_postpass_camera.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
						ring_postpass_camera.binding = 6
						ring_postpass_camera.add_id(ring_buffer)
						postpass_uniforms_array[6] = ring_postpass_camera
						general_data_uniform_sets[ring_slot].append(rd.uniform_set_create(postpass_uniforms_array, postpass_shader, 0))
					#endregion

				lights_updated = true
				segment_lights_dirty = true

			var framebuffer := FramebufferCacheRD.get_cache_multipass([buffers.get_color_layer(0, is_msaa_on), buffers.get_depth_layer(0, is_msaa_on)], [], view_count)
			var deferred_msaa_end_markers: Array[String] = []
			assert(framebuffer_format == rd.framebuffer_get_format(framebuffer))

			var cameraTR : Transform3D = rendersceneData.get_cam_transform();
			var viewProj : Projection = rendersceneData.get_cam_projection();
			
			var rendertarget: RID = buffers.get_render_target()
			temporal_history_last_render_target_id = rendertarget.get_id()
			if diagnostic_raw_density_matte and (rendertarget != diagnostic_last_callback_target \
					or size != diagnostic_last_callback_size \
					or not is_equal_approx(viewProj.y.y, diagnostic_last_callback_projection_y)):
				print("SUNSHINE_CAMERA_SIGNATURE time=", snappedf(current_time, 0.001),
					" target=", rendertarget, " size=", size,
					" frame_delay=", rd.get_frame_delay(),
					" general_buffer=", general_data_buffer,
					" proj_x=", viewProj.x, " proj_y=", viewProj.y,
					" proj_z=", viewProj.z, " proj_w=", viewProj.w,
					" cam_up=", cameraTR.basis.y,
					" cam_forward=", -cameraTR.basis.z)
				diagnostic_last_callback_target = rendertarget
				diagnostic_last_callback_size = size
				diagnostic_last_callback_projection_y = viewProj.y.y
			if accumulation_resources_rebuilt or rendertarget != last_render_target:
				if rendertarget != last_render_target:
					temporal_history_render_target_change_count += 1
				last_render_target = rendertarget
				ignore_accumilation = true
			else:
				ignore_accumilation = false
			
			
			last_size = size
			
			# Encode a bounded batch before publishing samplePointsCount. Previously
			# the full queued count could be sent to a 32-element SSBO, which let a
			# large cloud-shadow request read beyond the GPU buffer.
			if (!positionQuerying && !positionResetting && positionQueries.size() > 0):
				encode_sample_points()
			general_data_ring_index = 1 + (general_data_ring_index % GENERAL_DATA_MAIN_RING_SIZE)
			update_matrices(cameraTR, viewProj, new_size)
			dispatch_gpu_cloud_shadows()
			if lights_updated or directional_lights_data.size() == 0:
				update_lights()
			if segment_lights_dirty:
				update_segment_lights()
			
			var prepass_x_groups = ((size.x - 1) / 8) + 1
			var prepass_y_groups = ((size.y - 1) / 8) + 1
			var x_groups = ((size.x - 1) / 8 / resscale) + 1
			var y_groups = ((size.y - 1) / 8 / resscale) + 1
			
			for view in view_count:
				var gpu_timestamp_scope_active := false
				var gpu_timestamp_end_marker_for_scope := ""
				var gpu_timestamp_prepass_begin := ""
				var gpu_timestamp_prepass_end := ""
				var gpu_timestamp_raymarch_begin := ""
				var gpu_timestamp_raymarch_end := ""
				var gpu_timestamp_reconstruct_begin := ""
				var gpu_timestamp_reconstruct_end := ""
				var gpu_timestamp_composite_begin := ""
				var gpu_timestamp_composite_end := ""
				if gpu_timestamp_diagnostics_enabled and view == 0 and gpu_timestamp_begin_marker != "" and gpu_timestamp_end_marker != "":
					var gpu_timestamp_begin_marker_for_scope := gpu_timestamp_begin_marker
					var gpu_timestamp_candidate_end_marker := gpu_timestamp_end_marker
					if gpu_timestamp_begin_marker_for_scope.contains(":Clouds:") and gpu_timestamp_candidate_end_marker.contains(":Clouds:"):
						gpu_timestamp_scope_active = true
						gpu_timestamp_end_marker_for_scope = gpu_timestamp_candidate_end_marker
						gpu_timestamp_prepass_begin = gpu_timestamp_begin_marker_for_scope.replace(":Clouds:", ":CloudPrepass:")
						gpu_timestamp_prepass_end = gpu_timestamp_candidate_end_marker.replace(":Clouds:", ":CloudPrepass:")
						gpu_timestamp_raymarch_begin = gpu_timestamp_begin_marker_for_scope.replace(":Clouds:", ":CloudRaymarch:")
						gpu_timestamp_raymarch_end = gpu_timestamp_candidate_end_marker.replace(":Clouds:", ":CloudRaymarch:")
						gpu_timestamp_reconstruct_begin = gpu_timestamp_begin_marker_for_scope.replace(":Clouds:", ":CloudReconstruct:")
						gpu_timestamp_reconstruct_end = gpu_timestamp_candidate_end_marker.replace(":Clouds:", ":CloudReconstruct:")
						gpu_timestamp_composite_begin = gpu_timestamp_begin_marker_for_scope.replace(":Clouds:", ":CloudComposite:")
						gpu_timestamp_composite_end = gpu_timestamp_candidate_end_marker.replace(":Clouds:", ":CloudComposite:")
						rd.capture_timestamp(gpu_timestamp_begin_marker_for_scope)
				if gpu_timestamp_scope_active:
					rd.capture_timestamp(gpu_timestamp_prepass_begin)
				var prepass_list = rd.compute_list_begin()
				rd.compute_list_bind_compute_pipeline(prepass_list, prepass_pipeline)
				rd.compute_list_bind_uniform_set(prepass_list, general_data_uniform_sets[general_data_ring_index][view * 3], 0)
				rd.compute_list_dispatch(prepass_list, x_groups, y_groups, 1)
				rd.compute_list_end()
				if gpu_timestamp_scope_active:
					rd.capture_timestamp(gpu_timestamp_prepass_end)

				if gpu_timestamp_scope_active:
					rd.capture_timestamp(gpu_timestamp_raymarch_begin)
				if _terrain_sun_bindings_present: _update_terrain_sun_set()
				var compute_list = rd.compute_list_begin()
				rd.compute_list_bind_compute_pipeline(compute_list, pipeline)
				rd.compute_list_bind_uniform_set(compute_list, general_data_uniform_sets[general_data_ring_index][view * 3 + 1], 0)
				if _terrain_sun_bindings_present: rd.compute_list_bind_uniform_set(compute_list, _terrain_sun_set, 1)
				rd.compute_list_dispatch(compute_list, x_groups, y_groups, 1)
				rd.compute_list_end()
				if gpu_timestamp_scope_active:
					rd.capture_timestamp(gpu_timestamp_raymarch_end)

				if gpu_timestamp_scope_active:
					rd.capture_timestamp(gpu_timestamp_reconstruct_begin)
				var postpass_list = rd.compute_list_begin()
				rd.compute_list_bind_compute_pipeline(postpass_list, postpass_pipeline)
				rd.compute_list_bind_uniform_set(postpass_list, general_data_uniform_sets[general_data_ring_index][view * 3 + 2], 0)
				rd.compute_list_dispatch(postpass_list, prepass_x_groups, prepass_y_groups, 1)
				rd.compute_list_end()
				if gpu_timestamp_scope_active:
					rd.capture_timestamp(gpu_timestamp_reconstruct_end)

				if gpu_timestamp_scope_active:
					rd.capture_timestamp(gpu_timestamp_composite_begin)
				var display_list := rd.draw_list_begin(framebuffer, RenderingDevice.DRAW_DEFAULT_ALL)
				rd.draw_list_bind_render_pipeline(display_list, display_pipeline)
				rd.draw_list_bind_uniform_set(display_list, uniform_sets[view * 4 + 3], 0)
				rd.draw_list_bind_vertex_array(display_list, display_vertex_array)
				rd.draw_list_draw(display_list, false, 1)
				rd.draw_list_end()
				if gpu_timestamp_scope_active:
					if is_msaa_on: deferred_msaa_end_markers.append(gpu_timestamp_composite_end)
					else: rd.capture_timestamp(gpu_timestamp_composite_end)
				if gpu_timestamp_end_marker_for_scope != "":
					if is_msaa_on: deferred_msaa_end_markers.append(gpu_timestamp_end_marker_for_scope)
					else: rd.capture_timestamp(gpu_timestamp_end_marker_for_scope)

				# The compute commands that initialize color/density have now been
				# submitted. Main-thread backdrop materials can bind this immutable
				# wrapper on their following frame.
				if view < cloud_backdrop_texture_wrappers.size():
					cloud_backdrop_published_texture_wrappers[view] = cloud_backdrop_texture_wrappers[view]
			if is_msaa_on:
				# Godot resolves once before the POST_TRANSPARENT callback list.
				# Publish all completed multiview display draws for subsequent
				# effects and tonemapping, including dry frames.
				for view in view_count:
					rd.texture_resolve_multisample(buffers.get_color_layer(view, true), buffers.get_color_layer(view, false))
				for marker in deferred_msaa_end_markers: rd.capture_timestamp(marker)
			publish_renderer_diagnostic_snapshot(size, new_size, view_count, resscale, msaa_mode)

			if (!positionResetting && positionQuerying):
				positionResetting = true
				rd.buffer_get_data_async(point_sample_data_buffer, retrieve_position_queries.bind())
			#call_deferred("update_callbacktype", cameraTR.origin.y)
			#if (cameraTR.origin.y > cloud_floor):
				#if (self.effect_callback_type != CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT):
					#self.effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
			#else:
				#if (self.effect_callback_type != CompositorEffect.EFFECT_CALLBACK_TYPE_PRE_TRANSPARENT):
					#self.effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_PRE_TRANSPARENT

func retrieve_position_queries(data : PackedByteArray):
	
	var idx = 0
	var retrieved := 0
	while idx < POSITION_QUERY_BUFFER_BYTES \
		and retrieved < positionQueryBatchCount \
		and positionQueryCallables.size() > 0:
		var position : Vector3 = Vector3.ZERO
		position.x = data.decode_float(idx)
		idx += 4
		position.y = data.decode_float(idx)
		idx += 4
		position.z = data.decode_float(idx)
		idx += 4
		var density = data.decode_float(idx)
		idx += 4
		
		positionQueryCallables[0].call(position, density)
		positionQueryCallables.remove_at(0)
		retrieved += 1
		
	
	positionQueryBatchCount = 0
	positionQuerying = false
	positionResetting = false


#func update_callbacktype(lastY : float):
	#if (lastY > cloud_floor):
		#if (self.effect_callback_type != CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT):
			#self.effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	#else:
		#if (self.effect_callback_type != CompositorEffect.EFFECT_CALLBACK_TYPE_PRE_TRANSPARENT):
			#self.effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_PRE_TRANSPARENT

# Density continuity is an approximation at the configured integration scale,
# not a claim that a moving field is identical. Shape/power changes beyond
# 0.1% are excess. Advected quantities (noise positions, storm centre, weather
# offset) are judged against a per-quantity learned velocity * dt, so steady wind
# drift and long frames are not excess. Excess <= 1 keeps history; between 1 and
# history_continuity_hard_reset_ratio history decays in proportion; beyond it
# (or on structural/texture/seed/effector change) history is hard reset.
# Index 7 (advection_clock) is bookkeeping and never compared.
func _capture_density_state() -> Array:
	var shape: Array = [clouds_coverage, clouds_density, clouds_sharpness, clouds_detail_power,
		wind_swept_range, wind_swept_strength, curl_noise_strength, extra_large_noise_scale,
		large_noise_scale, medium_noise_scale, small_noise_scale, cloud_floor, cloud_ceiling,
		min_step_distance, max_step_distance, max_step_count, lod_bias, ring_width_meters,
		cloud_frame_up.w, cloud_handoff.x, cloud_handoff.y, cloud_handoff.z, float(extra_large_used_as_mask), mask_width_km, cloud_weather_params.x,
		cloud_weather_params.y, cloud_weather_params.z, cloud_weather_frame.w]
	for value in [storm_shape,storm_detail,background_altitude,storm_altitude]:
		shape.append_array([value.x,value.y,value.z,value.w])
	shape.append_array([storm_system.z,storm_system.w])
	return [shape, [extra_large_noise_patterns,height_gradient,large_scale_noise,medium_scale_noise,
		small_scale_noise,curl_noise], [extra_large_scale_clouds_position,large_scale_clouds_position,
		medium_scale_clouds_position,detail_clouds_position], storm_system,cloud_weather_frame,
		cloud_weather_params.w, point_effector_data.duplicate(), advection_clock]

const ADVECTION_PREDICTION_SLACK := 0.5
const UNPREDICTED_ADVECTION_SPEED := 1000.0

func _continuity_hard(term: String) -> Dictionary:
	_continuity_velocities = [null, null, null, null, null, null]
	return {"excess": INF, "term": term, "hard": true, "multiplier": 0.0}

# Seconds between callbacks: the driver's advection clock when it advanced
# (frame-rate independent), otherwise wall clock.
func _consume_continuity_dt() -> float:
	var now_usec := Time.get_ticks_usec()
	var clock_dt := advection_clock - _continuity_last_clock
	var dt: float = clock_dt if clock_dt > 0.000001 else float(now_usec - _continuity_last_usec) / 1000000.0
	_continuity_last_clock = advection_clock
	_continuity_last_usec = now_usec
	return clampf(dt, 0.0001, 2.0)

func _evaluate_density_continuity(current: Array, previous: Array, dt: float) -> Dictionary:
	if previous.size() != current.size() or current.size() < 7: return _continuity_hard("structure")
	if current[0].size() != previous[0].size(): return _continuity_hard("structure")
	if current[1] != previous[1]: return _continuity_hard("textures")
	if current[5] != previous[5]: return _continuity_hard("seed")
	if current[6] != previous[6]: return _continuity_hard("effectors")
	dt = maxf(dt, 0.0001)
	var allowance_scale := clampf(history_continuity_allowance_scale, 0.25, 8.0) if is_finite(history_continuity_allowance_scale) else 1.0
	var hard_ratio := clampf(history_continuity_hard_reset_ratio, 1.5, 32.0) if is_finite(history_continuity_hard_reset_ratio) else 8.0
	var excess := 0.0
	var term := ""
	for index in current[0].size():
		var a := float(current[0][index]); var b := float(previous[0][index])
		if not is_finite(a) or not is_finite(b): return _continuity_hard("nonfinite_shape%d" % index)
		var ratio := absf(a-b) / ((0.0001 + 0.001*maxf(absf(a),absf(b))) * allowance_scale)
		if ratio > excess:
			excess = ratio; term = "shape%d" % index
	var deltas: Array[Vector3] = []
	var bases: Array[float] = []
	var names: Array[String] = []
	var scales := [extra_large_noise_scale,large_noise_scale,medium_noise_scale,small_noise_scale]
	var step_base := maxf(min_step_distance,1.0)*0.1
	for index in 4:
		var delta: Vector3 = current[2][index]-previous[2][index]
		if not _gameplay_vector_is_finite(delta): return _continuity_hard("nonfinite_noise%d" % index)
		var period := float(scales[index])
		if period > 1.0:
			delta = Vector3(delta.x - period*roundf(delta.x/period), delta.y - period*roundf(delta.y/period), delta.z - period*roundf(delta.z/period))
		deltas.append(delta)
		bases.append(minf(step_base, maxf(period,1.0)*0.001) * allowance_scale)
		names.append("noise%d" % index)
	var weather_base := minf(step_base, maxf(cloud_weather_params.y,1.0)*0.001) * allowance_scale
	var storm_now: Vector4 = current[3]; var storm_before: Vector4 = previous[3]
	var storm_delta := Vector3(storm_now.x-storm_before.x, storm_now.y-storm_before.y, 0.0)
	if not _gameplay_vector_is_finite(storm_delta): return _continuity_hard("nonfinite_storm")
	deltas.append(storm_delta); bases.append(weather_base); names.append("storm")
	var weather: Vector4 = current[4]; var old_weather: Vector4 = previous[4]
	if weather.x != old_weather.x: return _continuity_hard("weather_circumference")
	var around_delta := weather.y-old_weather.y
	if weather.x > 0.0: around_delta = fposmod(around_delta+weather.x*0.5,weather.x)-weather.x*0.5
	var weather_delta := Vector3(around_delta, weather.z-old_weather.z, 0.0)
	if not _gameplay_vector_is_finite(weather_delta): return _continuity_hard("nonfinite_weather")
	deltas.append(weather_delta); bases.append(weather_base); names.append("weather")
	if _continuity_velocities.size() != 6:
		_continuity_velocities = [null, null, null, null, null, null]
	for index in 6:
		var delta := deltas[index]
		var known = _continuity_velocities[index]
		var residual: float
		var allowance: float
		if known == null:
			residual = delta.length()
			allowance = bases[index] + UNPREDICTED_ADVECTION_SPEED*dt
		else:
			var predicted: Vector3 = (known as Vector3)*dt
			residual = (delta-predicted).length()
			allowance = bases[index] + ADVECTION_PREDICTION_SLACK*predicted.length()
		var ratio := residual / allowance
		if ratio > excess:
			excess = ratio; term = names[index]
		if ratio < hard_ratio:
			var observed := delta/dt
			_continuity_velocities[index] = observed if known == null else (known as Vector3).lerp(observed, 0.5)
		else:
			_continuity_velocities[index] = null
	var hard := excess >= hard_ratio
	var multiplier := 1.0
	if hard: multiplier = 0.0
	elif excess > 1.0: multiplier = 1.0 - (excess-1.0)/(hard_ratio-1.0)
	return {"excess": excess, "term": term, "hard": hard, "multiplier": multiplier}

static func _gameplay_vector_is_finite(value: Vector3) -> bool:
	return is_finite(value.x) and is_finite(value.y) and is_finite(value.z)

func update_matrices(camera_tr, view_proj, new_size: Vector2i):
	var current_density_state: Array = [] if menu_temporal_sampling else _capture_density_state()
	var continuity_result := {"excess": 0.0, "term": "", "hard": false, "multiplier": 1.0}
	var continuity_dt := _consume_continuity_dt()
	if menu_temporal_sampling or first_run or ignore_accumilation:
		_continuity_velocities = [null, null, null, null, null, null]
	else:
		continuity_result = _evaluate_density_continuity(current_density_state, previous_callback_density_state, continuity_dt)
	temporal_history_last_continuity_excess = continuity_result.excess
	temporal_history_last_continuity_term = continuity_result.term
	temporal_history_last_continuity_multiplier = continuity_result.multiplier
	var density_continuous: bool = not menu_temporal_sampling and not first_run and not ignore_accumilation and float(continuity_result.excess) <= 1.0
	var current_gameplay_frame: Dictionary = gameplay_cloud_frame_snapshot
	if general_data.size() != GENERAL_DATA_BUFFER_BYTES:
		general_data.resize(GENERAL_DATA_BUFFER_BYTES)
	
	var idx = 0
	filter_index += 1
	if filter_index > 16:
		filter_index = 0
	# A route teleport has no trustworthy temporal correspondence. Evaluate it
	# against callback-paired double inputs, so ordinary CPU UpdateFrame bursts
	# and normal high-speed menu travel do not clear history spuriously.
	var menu_frame_discontinuity: bool = menu_temporal_sampling and not first_run \
		and absf(menu_cloud_frame_around_precise - previous_callback_menu_around_precise) > maxf(4000.0, 2.0 * max_step_distance)
	last_menu_history_discontinuity = menu_frame_discontinuity
	# Camera matrix (16 floats)
	#general_data.encode_float(idx, camera_tr.basis.x.x); idx += 4
	#general_data.encode_float(idx, camera_tr.basis.x.y); idx += 4
	#general_data.encode_float(idx, camera_tr.basis.x.z); idx += 4
	#general_data.encode_float(idx, 0); idx += 4
	#
	#general_data.encode_float(idx, camera_tr.basis.y.x); idx += 4
	#general_data.encode_float(idx, camera_tr.basis.y.y); idx += 4
	#general_data.encode_float(idx, camera_tr.basis.y.z); idx += 4
	#general_data.encode_float(idx, 0); idx += 4
	#
	#general_data.encode_float(idx, camera_tr.basis.z.x); idx += 4
	#general_data.encode_float(idx, camera_tr.basis.z.y); idx += 4
	#general_data.encode_float(idx, camera_tr.basis.z.z); idx += 4
	#general_data.encode_float(idx, 0); idx += 4
	#
	#general_data.encode_float(idx, camera_tr.origin.x); idx += 4
	#general_data.encode_float(idx, camera_tr.origin.y); idx += 4
	#general_data.encode_float(idx, camera_tr.origin.z); idx += 4
	#general_data.encode_float(idx, 1.0); idx += 4
#
	## Previous or current camera matrix
	#var mat = camera_tr if first_run else last_view_mat
	#general_data.encode_float(idx, mat.basis.x.x); idx += 4
	#general_data.encode_float(idx, mat.basis.x.y); idx += 4
	#general_data.encode_float(idx, mat.basis.x.z); idx += 4
	#general_data.encode_float(idx, 0); idx += 4
	#
	#general_data.encode_float(idx, mat.basis.y.x); idx += 4
	#general_data.encode_float(idx, mat.basis.y.y); idx += 4
	#general_data.encode_float(idx, mat.basis.y.z); idx += 4
	#general_data.encode_float(idx, 0); idx += 4
	#
	#general_data.encode_float(idx, mat.basis.z.x); idx += 4
	#general_data.encode_float(idx, mat.basis.z.y); idx += 4
	#general_data.encode_float(idx, mat.basis.z.z); idx += 4
	#general_data.encode_float(idx, 0); idx += 4
	#
	#general_data.encode_float(idx, mat.origin.x); idx += 4
	#general_data.encode_float(idx, mat.origin.y); idx += 4
	#general_data.encode_float(idx, mat.origin.z); idx += 4
	#general_data.encode_float(idx, 1.0); idx += 4
#
	## Projection matrix (16 floats)
	#general_data.encode_float(idx, view_proj.x.x); idx += 4
	#general_data.encode_float(idx, view_proj.x.y); idx += 4
	#general_data.encode_float(idx, view_proj.x.z); idx += 4
	#general_data.encode_float(idx, view_proj.x.w); idx += 4
	#
	#general_data.encode_float(idx, view_proj.y.x); idx += 4
	#general_data.encode_float(idx, view_proj.y.y); idx += 4
	#general_data.encode_float(idx, view_proj.y.z); idx += 4
	#general_data.encode_float(idx, view_proj.y.w); idx += 4
	#
	#general_data.encode_float(idx, view_proj.z.x); idx += 4
	#general_data.encode_float(idx, view_proj.z.y); idx += 4
	#general_data.encode_float(idx, view_proj.z.z); idx += 4
	#general_data.encode_float(idx, view_proj.z.w); idx += 4
	#
	#general_data.encode_float(idx, view_proj.w.x); idx += 4
	#general_data.encode_float(idx, view_proj.w.y); idx += 4
	#general_data.encode_float(idx, view_proj.w.z); idx += 4
	#general_data.encode_float(idx, view_proj.w.w); idx += 4
#
	## Previous or current camera matrix
	#var proj = view_proj if first_run else last_projection_mat
	#general_data.encode_float(idx, proj.x.x); idx += 4
	#general_data.encode_float(idx, proj.x.y); idx += 4
	#general_data.encode_float(idx, proj.x.z); idx += 4
	#general_data.encode_float(idx, proj.x.w); idx += 4
	#
	#general_data.encode_float(idx, proj.y.x); idx += 4
	#general_data.encode_float(idx, proj.y.y); idx += 4
	#general_data.encode_float(idx, proj.y.z); idx += 4
	#general_data.encode_float(idx, proj.y.w); idx += 4
	#
	#general_data.encode_float(idx, proj.z.x); idx += 4
	#general_data.encode_float(idx, proj.z.y); idx += 4
	#general_data.encode_float(idx, proj.z.z); idx += 4
	#general_data.encode_float(idx, proj.z.w); idx += 4
	#
	#general_data.encode_float(idx, proj.w.x); idx += 4
	#general_data.encode_float(idx, proj.w.y); idx += 4
	#general_data.encode_float(idx, proj.w.z); idx += 4
	#general_data.encode_float(idx, proj.w.w); idx += 4
#
	#last_projection_mat = view_proj
	#last_view_mat = camera_tr
	accumulation_is_a = not accumulation_is_a
	
	# Additional data (44 floats)
	var width = mask_width_km * 1000.0
	
	if (extra_large_used_as_mask):
		general_data.encode_float(idx, origin_offset.x + (width * 0.5) * -1.0); idx += 4
		general_data.encode_float(idx, origin_offset.y + (width * 0.5) * -1.0); idx += 4
		general_data.encode_float(idx, origin_offset.z + (width * 0.5) * -1.0); idx += 4
		general_data.encode_float(idx, width); idx += 4
	else:
		general_data.encode_float(idx, extra_large_scale_clouds_position.x); idx += 4
		general_data.encode_float(idx, extra_large_scale_clouds_position.y); idx += 4
		general_data.encode_float(idx, extra_large_scale_clouds_position.z); idx += 4
		general_data.encode_float(idx, extra_large_noise_scale); idx += 4
	
	#general_data.encode_float(idx, extra_large_scale_clouds_position.x); idx += 4
	#general_data.encode_float(idx, extra_large_scale_clouds_position.y); idx += 4
	#general_data.encode_float(idx, extra_large_scale_clouds_position.z); idx += 4
	#general_data.encode_float(idx, extra_large_noise_scale); idx += 4
	#
	general_data.encode_float(idx, large_scale_clouds_position.x); idx += 4
	general_data.encode_float(idx, large_scale_clouds_position.y); idx += 4
	general_data.encode_float(idx, large_scale_clouds_position.z); idx += 4
	general_data.encode_float(idx, lighting_sharpness); idx += 4

	general_data.encode_float(idx, medium_scale_clouds_position.x); idx += 4
	general_data.encode_float(idx, medium_scale_clouds_position.y); idx += 4
	general_data.encode_float(idx, medium_scale_clouds_position.z); idx += 4
	general_data.encode_float(idx, lighting_travel_distance); idx += 4

	general_data.encode_float(idx, detail_clouds_position.x); idx += 4
	general_data.encode_float(idx, detail_clouds_position.y); idx += 4
	general_data.encode_float(idx, detail_clouds_position.z); idx += 4
	general_data.encode_float(idx, atmospheric_density); idx += 4

	general_data.encode_float(idx, cloud_ambient_color.r * cloud_ambient_tint.r); idx += 4
	general_data.encode_float(idx, cloud_ambient_color.g * cloud_ambient_tint.g); idx += 4
	general_data.encode_float(idx, cloud_ambient_color.b * cloud_ambient_tint.b); idx += 4
	general_data.encode_float(idx, cloud_ambient_color.a * cloud_ambient_tint.a); idx += 4

	general_data.encode_float(idx, ambient_occlusion_color.r); idx += 4
	general_data.encode_float(idx, ambient_occlusion_color.g); idx += 4
	general_data.encode_float(idx, ambient_occlusion_color.b); idx += 4
	general_data.encode_float(idx, ambient_occlusion_color.a); idx += 4

	general_data.encode_float(idx, lerpf(atmosphere_color.r, sampled_environment_fog_color.r, use_environment_fog)); idx += 4
	general_data.encode_float(idx, lerpf(atmosphere_color.g, sampled_environment_fog_color.g, use_environment_fog)); idx += 4
	general_data.encode_float(idx, lerpf(atmosphere_color.b, sampled_environment_fog_color.b, use_environment_fog)); idx += 4
	general_data.encode_float(idx, lerpf(atmosphere_color.a, sampled_environment_fog_color.a, use_environment_fog)); idx += 4

	general_data.encode_float(idx, small_noise_scale); idx += 4
	general_data.encode_float(idx, min_step_distance); idx += 4
	general_data.encode_float(idx, max_step_distance); idx += 4
	general_data.encode_float(idx, lod_bias); idx += 4

	general_data.encode_float(idx, clouds_sharpness); idx += 4
	general_data.encode_float(idx, float(directional_lights_data.size()) / 2.0); idx += 4
	general_data.encode_float(idx, clouds_powder); idx += 4
	general_data.encode_float(idx, clouds_anisotropy); idx += 4

	general_data.encode_float(idx, cloud_floor); idx += 4
	general_data.encode_float(idx, cloud_ceiling); idx += 4
	general_data.encode_float(idx, float(max_step_count)); idx += 4
	general_data.encode_float(idx, float(max_lighting_steps)); idx += 4

	general_data.encode_float(idx, use_environment_fog); idx += 4
	general_data.encode_float(idx, float(blur_power)); idx += 4
	general_data.encode_float(idx, float(blur_quality)); idx += 4
	general_data.encode_float(idx, float(curl_noise_strength)); idx += 4
	
	general_data.encode_float(idx, wind_direction.x); idx += 4
	general_data.encode_float(idx, wind_direction.z); idx += 4
	general_data.encode_float(idx, fog_effect_ground); idx += 4
	general_data.encode_float(idx, positionQueryBatchCount); idx += 4
	
	general_data.encode_float(idx, float(point_lights_data.size()) / 2.0); idx += 4
	general_data.encode_float(idx, float(point_effector_data.size()) / 2.0); idx += 4
	general_data.encode_float(idx, wind_swept_range); idx += 4
	general_data.encode_float(idx, wind_swept_strength); idx += 4
	
	general_data.encode_float(idx, new_size.x); idx += 4
	general_data.encode_float(idx, new_size.y); idx += 4
	general_data.encode_float(idx, large_noise_scale); idx += 4
	general_data.encode_float(idx, medium_noise_scale); idx += 4
	
	# The menu path samples once per actual compositor callback. dither_speed is
	# expressed in slices per second, avoiding a 60 Hz integer alias;
	# zero still retains callback-driven temporal sampling.
	var reversed := 0
	var callback_slice := render_callback_serial % 64
	for bit_index in 6:
		reversed = (reversed << 1) | ((callback_slice >> bit_index) & 1)
	var callback_sample_phase := (float(reversed) + 0.5) / 64.0
	var menu_sample_phase := callback_sample_phase if menu_temporal_sampling else 0.0
	last_menu_sample_phase = menu_sample_phase
	var menu_dither_time := fposmod(menu_sample_phase + fposmod(menu_animation_seconds_precise * dither_speed / 64.0, 1.0), 1.0)
	# Menu retains its paired bit-reversal sequence. Gameplay uses a callback-
	# driven scalar rotation plus the optional driver phase. Its compute shader
	# adds this phase to a fixed spatial seed, distributing ray offsets over time
	# even when dither_speed is zero.
	var gameplay_dither_time := fposmod(float(render_callback_serial) * 0.6180339887498949 + current_time, 1.0)
	general_data.encode_float(idx, menu_dither_time if menu_temporal_sampling else gameplay_dither_time); idx += 4
	general_data.encode_float(idx, clouds_coverage); idx += 4
	general_data.encode_float(idx, clouds_density); idx += 4
	general_data.encode_float(idx, clouds_detail_power); idx += 4
	
	general_data.encode_float(idx, lighting_density); idx += 4
	# Gameplay rebase remap is CPU-only and uses the paired bridge snapshots.
	# Menu retains its existing callback contract unchanged.
	var gameplay_remap_valid := false
	var mapped_previous_camera := previous_callback_camera_transform
	var gameplay_frame_contract_supplied := not current_gameplay_frame.is_empty() or not previous_callback_gameplay_cloud_frame.is_empty()
	if not menu_temporal_sampling and not first_run:
		var remap_result := gameplay_frame_remap(current_gameplay_frame, previous_callback_gameplay_cloud_frame, previous_callback_camera_transform, maxf(4000.0, 2.0 * max_step_distance))
		gameplay_frame_contract_supplied = bool(remap_result.has_contract)
		gameplay_remap_valid = bool(remap_result.valid)
		mapped_previous_camera = remap_result.mapped_camera as Transform3D
	var packed_accumulation_decay := accumulation_decay if !ignore_accumilation and !menu_frame_discontinuity and (menu_temporal_sampling or first_run or not gameplay_frame_contract_supplied or gameplay_remap_valid) else 0.0
	if _history_reset_requested:
		packed_accumulation_decay = 0.0
		_history_reset_requested = false
	elif cloud_history_compatibility_enabled and not menu_temporal_sampling and not first_run:
		var continuity_multiplier: float = continuity_result.multiplier
		packed_accumulation_decay *= continuity_multiplier
		if bool(continuity_result.hard):
			packed_accumulation_decay = 0.0
			temporal_history_continuity_hard_reset_count += 1
		elif continuity_multiplier > 0.0 and continuity_multiplier < 1.0:
			temporal_history_continuity_decay_callback_count += 1
	temporal_history_last_requested_decay = accumulation_decay
	temporal_history_last_packed_decay = packed_accumulation_decay
	if packed_accumulation_decay == 0.0:
		temporal_history_zero_packed_decay_callback_count += 1
	general_data.encode_float(idx, packed_accumulation_decay); idx += 4
	if (accumulation_is_a):
		general_data.encode_float(idx, 1.0); idx += 4
	else:
		general_data.encode_float(idx, 0.0); idx += 4
	
	general_data.encode_float(idx, int(pow(2.0, float(resolution_scale)))); idx += 4

	general_data.encode_float(idx, cloud_frame_origin.x); idx += 4
	general_data.encode_float(idx, cloud_frame_origin.y); idx += 4
	general_data.encode_float(idx, cloud_frame_origin.z); idx += 4
	general_data.encode_float(idx, 0.0); idx += 4

	for value in [cloud_frame_around, cloud_frame_up, cloud_frame_across]:
		general_data.encode_float(idx, value.x); idx += 4
		general_data.encode_float(idx, value.y); idx += 4
		general_data.encode_float(idx, value.z); idx += 4
		general_data.encode_float(idx, value.w); idx += 4

	for value in [storm_system, storm_shape, storm_detail, background_altitude, storm_altitude]:
		general_data.encode_float(idx, value.x); idx += 4
		general_data.encode_float(idx, value.y); idx += 4
		general_data.encode_float(idx, value.z); idx += 4
		general_data.encode_float(idx, value.w); idx += 4

	general_data.encode_float(idx, ray_start_jitter_strength); idx += 4
	general_data.encode_float(idx, ring_width_meters); idx += 4
	# z is a signed capture mode: +1 raw density, -1 lit history-off, 0 normal.
	general_data.encode_float(idx, 1.0 if diagnostic_raw_density_matte else (-1.0 if diagnostic_disable_temporal_history else 0.0)); idx += 4
	general_data.encode_float(idx, 1.0 if diagnostic_depth_density_matte else 0.0); idx += 4

	# Pack the authoritative callback camera matrices for current-frame rays.
	# Transform3D/Projection axis members are matrix columns, matching GLSL's
	# std140 mat4 layout and the existing commented-out matrix packing above.
	var camera_columns = [camera_tr.basis.x, camera_tr.basis.y, camera_tr.basis.z, camera_tr.origin]
	for column_index in camera_columns.size():
		var value: Vector3 = camera_columns[column_index]
		general_data.encode_float(idx, value.x); idx += 4
		general_data.encode_float(idx, value.y); idx += 4
		general_data.encode_float(idx, value.z); idx += 4
		general_data.encode_float(idx, 1.0 if column_index == 3 else 0.0); idx += 4

	var inv_projection: Projection = view_proj.inverse()
	for value in [inv_projection.x, inv_projection.y, inv_projection.z, inv_projection.w]:
		general_data.encode_float(idx, value.x); idx += 4
		general_data.encode_float(idx, value.y); idx += 4
		general_data.encode_float(idx, value.z); idx += 4
		general_data.encode_float(idx, value.w); idx += 4

	# First-frame history maps to the current callback camera. Later frames use
	# the callback values saved after the preceding upload, not SceneData's
	# private previous-frame UBO payload.
	var previous_transform: Transform3D = camera_tr if first_run else mapped_previous_camera
	var previous_projection: Projection = view_proj if first_run else previous_callback_camera_projection
	var previous_view: Transform3D = previous_transform.affine_inverse()
	var previous_view_columns = [previous_view.basis.x, previous_view.basis.y, previous_view.basis.z, previous_view.origin]
	for column_index in previous_view_columns.size():
		var value: Vector3 = previous_view_columns[column_index]
		general_data.encode_float(idx, value.x); idx += 4
		general_data.encode_float(idx, value.y); idx += 4
		general_data.encode_float(idx, value.z); idx += 4
		general_data.encode_float(idx, 1.0 if column_index == 3 else 0.0); idx += 4

	for value in [previous_projection.x, previous_projection.y, previous_projection.z, previous_projection.w]:
		general_data.encode_float(idx, value.x); idx += 4
		general_data.encode_float(idx, value.y); idx += 4
		general_data.encode_float(idx, value.z); idx += 4
		general_data.encode_float(idx, value.w); idx += 4

	general_data.encode_float(idx, previous_transform.origin.x); idx += 4
	general_data.encode_float(idx, previous_transform.origin.y); idx += 4
	general_data.encode_float(idx, previous_transform.origin.z); idx += 4
	general_data.encode_float(idx, 1.0); idx += 4
	# Pair menu frame and macro wind data here, not in main-thread UpdateFrame.
	# The shader receives only small deltas, avoiding large canonical float
	# subtraction when the menu crosses a long ring arc.
	var previous_menu_around := menu_cloud_frame_around_precise if first_run else previous_callback_menu_around_precise
	var current_bulk_wind := Vector2(extra_large_scale_clouds_position.x, extra_large_scale_clouds_position.z)
	var previous_bulk_wind := current_bulk_wind if first_run else previous_callback_bulk_wind
	var bulk_delta := current_bulk_wind - previous_bulk_wind
	var previous_detail_wind := detail_clouds_position if first_run else previous_callback_detail_wind
	# Exact callback camera/projection equality is intentional: only a truly
	# static menu field may ignore jitter-only alpha/depth disagreement.
	var menu_history_static: bool = menu_temporal_sampling and not first_run and not ignore_accumilation \
		and camera_tr == previous_callback_camera_transform and view_proj == previous_callback_camera_projection \
		and menu_cloud_frame_around_precise == previous_menu_around and bulk_delta == Vector2.ZERO \
		and detail_clouds_position == previous_detail_wind
	last_menu_arc_delta = menu_cloud_frame_around_precise - previous_menu_around
	last_menu_bulk_delta = bulk_delta
	last_menu_history_static = menu_history_static
	general_data.encode_float(idx, 1.0 if menu_fast_reconstruction else 0.0); idx += 4
	general_data.encode_float(idx, 1.0 if menu_temporal_sampling else 0.0); idx += 4
	general_data.encode_float(idx, menu_cloud_frame_around_precise - previous_menu_around - bulk_delta.x); idx += 4
	general_data.encode_float(idx, -bulk_delta.y); idx += 4
	general_data.encode_float(idx, menu_sample_phase); idx += 4
	general_data.encode_float(idx, bulk_delta.x); idx += 4
	general_data.encode_float(idx, 1.0 if (menu_history_static if menu_temporal_sampling else density_continuous) else 0.0); idx += 4
	general_data.encode_float(idx, 1.0 if diagnostic_menu_history_weights else 0.0); idx += 4
	# Append-only shared atmosphere ABI. Missing/default values deliberately
	# disable the path for standalone addon scenes and menu fixtures.
	for profile_index in 10:
		var profile_value := ring_atmosphere_profile[profile_index] if profile_index < ring_atmosphere_profile.size() else Vector4.ZERO
		general_data.encode_float(idx, profile_value.x); idx += 4
		general_data.encode_float(idx, profile_value.y); idx += 4
		general_data.encode_float(idx, profile_value.z); idx += 4
		general_data.encode_float(idx, profile_value.w); idx += 4
	for value in [cloud_handoff, cloud_weather_params, cloud_weather_frame]:
		general_data.encode_float(idx, value.x); idx += 4
		general_data.encode_float(idx, value.y); idx += 4
		general_data.encode_float(idx, value.z); idx += 4
		general_data.encode_float(idx, value.w); idx += 4
	# Append-only cloud mask controls: x skip flag, y/z gameplay history gate alpha band,
	# w this callback's camera z_far. The engine SceneData UBO bound at the last
	# accumulation rebuild is a per-render-pass pool slot: once the frame's pass
	# order shifts it holds another pass (e.g. a 4096^2 shadow pass with z_far 5),
	# so its z_far must not classify this view's opaque depth.
	var gate_low := 0.20 if not is_finite(cloud_history_gate_alpha_low) else clampf(cloud_history_gate_alpha_low, 0.0, 0.95)
	var gate_high := 0.65 if not is_finite(cloud_history_gate_alpha_high) else clampf(cloud_history_gate_alpha_high, 0.05, 1.0)
	if gate_high < gate_low + 0.05:
		gate_high = minf(gate_low + 0.05, 1.0)
	# Unproject both depth extremes through the same inverse projection the shaders
	# use; the larger view distance is the far plane whichever depth convention
	# (reversed-Z or not) this projection follows. Projection.get_z_far() assumes
	# the GL convention and returns the near plane for these callback projections.
	var callback_z_far := 0.0
	for plane_depth in [0.0, 1.0]:
		var plane_point: Vector4 = inv_projection * Vector4(0.0, 0.0, plane_depth, 1.0)
		var plane_distance := absf(plane_point.z / plane_point.w) if absf(plane_point.w) > 1e-30 else 3.0e38
		if is_finite(plane_distance): callback_z_far = maxf(callback_z_far, minf(plane_distance, 3.0e38))
	diagnostic_last_callback_z_far = callback_z_far
	general_data.encode_float(idx, 1.0 if skip_empty_cloud_mask_samples else 0.0); idx += 4
	general_data.encode_float(idx, gate_low); idx += 4
	general_data.encode_float(idx, gate_high); idx += 4
	general_data.encode_float(idx, callback_z_far if is_finite(callback_z_far) and callback_z_far > 0.0 else 0.0); idx += 4
	for value in [1.0 if depth_history_rejection_enabled else 0.0, clampf(history_depth_tolerance_multiplier, 0.25, 4.0) if is_finite(history_depth_tolerance_multiplier) else 1.0, 1.0 if cloud_history_compatibility_enabled else 0.0, 1.0 if lightning_history_isolation_enabled else 0.0]:
		general_data.encode_float(idx, value); idx += 4
	for profile_index in 5:
		var value := terrain_cloud_sun_profile[profile_index] if terrain_cloud_sun_profile.size() == 5 else Vector4.ZERO
		for component in [value.x, value.y, value.z, value.w]:
			general_data.encode_float(idx, component); idx += 4
	for frame_index in 4:
		var value := terrain_cloud_sun_canonical_frame[frame_index] if terrain_cloud_sun_canonical_frame.size() == 4 else Vector4.ZERO
		for component in [value.x, value.y, value.z, value.w]:
			general_data.encode_float(idx, component); idx += 4
	previous_callback_menu_around_precise = menu_cloud_frame_around_precise
	previous_callback_bulk_wind = current_bulk_wind
	previous_callback_detail_wind = detail_clouds_position
	previous_callback_camera_transform = camera_tr
	previous_callback_camera_projection = view_proj
	previous_callback_density_state = current_density_state
	first_run = false
	#
	#general_data.encode_float(idx, last_size.x); idx += 4
	#general_data.encode_float(idx, last_size.y); idx += 4
	#general_data.encode_float(idx, 0.0); idx += 4
	#general_data.encode_float(idx, 0.0); idx += 4
	
	# Each callback owns one bounded UBO slot. The shadow pass retains its
	# fixed descriptor on slot zero, so mirror the same packed data there.
	var callback_data_buffer: RID = general_data_buffers[general_data_ring_index]
	rd.buffer_update(callback_data_buffer, 0, general_data.size(), general_data)
	rd.buffer_update(general_data_buffer, 0, general_data.size(), general_data)
	if not menu_temporal_sampling:
		previous_callback_gameplay_cloud_frame = current_gameplay_frame


# data = Vector4 triples per segment: (A.xyz, radius), (B.xyz, steady flag 1 or 0), (rgb = colour x intensity, 0). Packed for the shader as
# (A.xyz, radius), (unit direction, length), (rgb, steady flag) so the per-sample reject needs no sqrt. Called by the bridge only when the set
# changes (steady segments) or at a bounded rate (moving orbs and pulses). The packed bytes are swapped in whole, then uploaded once from the
# render callback; nothing is touched while the dirty flag is clear.
func set_segment_lights(data : Array, count : int) -> void:
	var n := clampi(count, 0, SEGMENT_LIGHT_MAX)
	if n * 3 > data.size():
		n = floori(data.size() / 3.0)
	var bytes := PackedByteArray()
	bytes.resize(SEGMENT_LIGHT_BYTES)
	bytes.encode_float(0, float(n))
	bytes.encode_float(4, segment_light_phase_weight)
	bytes.encode_float(8, segment_light_extinction)
	var idx := 16
	for i in range(n):
		var a : Vector4 = data[i * 3]
		var b : Vector4 = data[i * 3 + 1]
		var c : Vector4 = data[i * 3 + 2]
		var ab := Vector3(b.x - a.x, b.y - a.y, b.z - a.z)
		var length := maxf(ab.length(), 0.01)
		var dir := ab / length
		var packed := [a.x, a.y, a.z, a.w, dir.x, dir.y, dir.z, length, c.x, c.y, c.z, b.w]
		for value in packed:
			bytes.encode_float(idx, value)
			idx += 4
	segment_light_count = n
	segment_light_bytes = bytes
	segment_lights_dirty = true

func update_segment_lights():
	segment_lights_dirty = false
	if not light_data_buffer.is_valid():
		return
	if segment_light_bytes.size() != SEGMENT_LIGHT_BYTES:
		# Never set (or cleared): the count is zero, so the shader loop is skipped.
		segment_light_bytes.resize(SEGMENT_LIGHT_BYTES)
		segment_light_bytes.fill(0)
	rd.buffer_update(light_data_buffer, SEGMENT_LIGHT_OFFSET, SEGMENT_LIGHT_BYTES, segment_light_bytes)

func update_lights():
	lights_updated = false
	
	if light_data.size() != 6272: #32 + 1024 + 512 * 4 bytes for each float = 6272.
		light_data.resize(6272)
	
	if (directional_lights_data.size() == 0): #defaults to having a default light.
		directional_lights_data.append(Vector4(0.5, 1.0, 0.5, 16.0))
		directional_lights_data.append(Vector4(1.0, 1.0, 1.0, 1.0))
	
	var idx = 0
	for i in range(min(directional_lights_data.size(), 8)):
		light_data.encode_float(idx, directional_lights_data[i].x)
		idx += 4
		light_data.encode_float(idx, directional_lights_data[i].y)
		idx += 4
		light_data.encode_float(idx, directional_lights_data[i].z)
		idx += 4
		light_data.encode_float(idx, directional_lights_data[i].w)
		idx += 4
	
	
	idx = 128
	for i in range(min(point_lights_data.size(), 256)):
		light_data.encode_float(idx, point_lights_data[i].x)
		idx += 4
		light_data.encode_float(idx, point_lights_data[i].y)
		idx += 4
		light_data.encode_float(idx, point_lights_data[i].z)
		idx += 4
		light_data.encode_float(idx, point_lights_data[i].w)
		idx += 4
	
	idx = 4224
	for i in range(min(point_effector_data.size(), 128)):
		light_data.encode_float(idx, point_effector_data[i].x)
		idx += 4
		light_data.encode_float(idx, point_effector_data[i].y)
		idx += 4
		light_data.encode_float(idx, point_effector_data[i].z)
		idx += 4
		light_data.encode_float(idx, point_effector_data[i].w)
		idx += 4
	
	rd.buffer_update(light_data_buffer, 0, light_data.size(), light_data)

func encode_sample_points():
	positionQuerying = true
	positionQueryBatchCount = mini(POSITION_QUERY_CAPACITY, positionQueries.size())
	var sample_points_data_floats : PackedByteArray = []
	sample_points_data_floats.resize(POSITION_QUERY_BUFFER_BYTES)
	
	var idx = 0
	var encoded := 0
	while idx < POSITION_QUERY_BUFFER_BYTES \
		and encoded < positionQueryBatchCount \
		and positionQueries.size() > 0:
		
		sample_points_data_floats.encode_float(idx, positionQueries[0].x)
		idx += 4
		sample_points_data_floats.encode_float(idx, positionQueries[0].y)
		idx += 4
		sample_points_data_floats.encode_float(idx, positionQueries[0].z)
		idx += 4
		sample_points_data_floats.encode_float(idx, 0.0)
		idx += 4
		positionQueries.remove_at(0)
		encoded += 1
	
	
	rd.buffer_update(point_sample_data_buffer, 0, sample_points_data_floats.size(), sample_points_data_floats)

# Builds the producer descriptor set for one output texture. The front (published)
# and staging textures share every other binding, including the parameter buffer.
func _build_cloud_shadow_uniform_set(output_texture: RID, param_buffer: RID) -> RID:
	var uniforms: Array[RDUniform] = []
	var output_uniform := RDUniform.new()
	output_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
	output_uniform.binding = 0
	output_uniform.add_id(output_texture)
	uniforms.append(output_uniform)
	for binding in range(1, 5):
		var noise_uniform := RDUniform.new()
		noise_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
		noise_uniform.binding = binding
		noise_uniform.add_id(linear_sampler)
		var texture: RID = RenderingServer.texture_get_rd_texture([extra_large_noise_patterns, large_scale_noise, medium_scale_noise, small_scale_noise][binding - 1].get_rid())
		noise_uniform.add_id(texture)
		uniforms.append(noise_uniform)
	var gradient_uniform := RDUniform.new()
	gradient_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
	gradient_uniform.binding = 5
	gradient_uniform.add_id(linear_sampler_no_repeat)
	gradient_uniform.add_id(RenderingServer.texture_get_rd_texture(height_gradient.get_rid()))
	uniforms.append(gradient_uniform)
	var general_uniform := RDUniform.new()
	general_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
	general_uniform.binding = 6
	general_uniform.add_id(general_data_buffer)
	uniforms.append(general_uniform)
	var parameters_uniform := RDUniform.new()
	parameters_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
	parameters_uniform.binding = 7
	parameters_uniform.add_id(param_buffer)
	uniforms.append(parameters_uniform)
	return rd.uniform_set_create(uniforms, cloud_shadow_shader, 0)
