shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;
#include "res://materials/ring_terrain_cloud_sun_receiver.gdshaderinc"

uniform mat3 terrain_cloud_world_to_ring = mat3(1.0);
uniform vec3 terrain_cloud_ring_geometry = vec3(1.0, 0.0, 0.0);
uniform vec3 camera_world_position = vec3(0.0);
uniform vec3 ring_center = vec3(0.0);
uniform vec3 surface_up = vec3(-1.0, 0.0, 0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float handoff_in_start_meters = 0.0;
uniform float handoff_in_end_meters = 1.0;
uniform float handoff_start_meters = 7600.0;
uniform float handoff_end_meters = 10000.0;
uniform bool use_vertex_cloud_data = false;
uniform bool world_space_union_mesh = false;
uniform float edge_firmness : hint_range(0.5, 1.0) = 0.83;
uniform vec3 sunlit_color : source_color = vec3(0.92, 0.95, 0.99);
uniform vec3 middle_color : source_color = vec3(0.76, 0.82, 0.90);
uniform vec3 shadow_color : source_color = vec3(0.38, 0.45, 0.56);
uniform vec3 night_color : source_color = vec3(0.045, 0.065, 0.11);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform sampler2D cloud_layer_occlusion_texture : filter_linear, repeat_disable;
uniform bool cloud_layer_occlusion_enabled = false;
uniform vec3 cloud_layer_occlusion_center_world = vec3(0.0);
uniform vec3 cloud_layer_occlusion_axis_around = vec3(0.0, 0.0, 1.0);
uniform vec3 cloud_layer_occlusion_axis_across = vec3(0.0, 1.0, 0.0);
uniform float cloud_layer_occlusion_radius_meters = 50000.0;
uniform float cloud_layer_occlusion_altitude_min_meters = -2000.0;
uniform float cloud_layer_occlusion_altitude_max_meters = 18000.0;
uniform float cloud_self_shadow_strength : hint_range(0.0, 0.7) = 0.28;
uniform float cloud_layer_occlusion_strength : hint_range(0.0, 0.6) = 0.26;
#include "res://materials/ring_atmosphere.gdshaderinc"

varying vec3 world_position;
varying vec3 world_normal;
varying vec4 cloud_data;
varying float formation_height;
varying float optical_depth_factor;

vec2 sample_cloud_layer_occlusion(vec3 world_pos, vec3 up, vec3 sun_dir) {
	if (!cloud_layer_occlusion_enabled) {
		return vec2(0.0);
	}

	vec3 around_axis = normalize(cloud_layer_occlusion_axis_around);
	vec3 across_axis = normalize(cloud_layer_occlusion_axis_across);
	vec3 relative = world_pos - cloud_layer_occlusion_center_world;
	float height = max(0.0, dot(relative, up));
	float sun_vertical = max(0.16, dot(sun_dir, up));
	vec2 sun_planar = vec2(dot(sun_dir, around_axis), dot(sun_dir, across_axis));
	float radius = max(cloud_layer_occlusion_radius_meters, 1.0);
	vec2 shift = -sun_planar * (height / sun_vertical);
	float maximum_shift = radius * 0.30;
	if (dot(shift, shift) > maximum_shift * maximum_shift) {
		shift = normalize(shift) * maximum_shift;
	}
	vec2 projected = vec2(dot(relative, around_axis), dot(relative, across_axis)) + shift;
	vec2 uv = projected / (2.0 * radius) + vec2(0.5);
	if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
		return vec2(0.0);
	}

	float edge = 1.0 - smoothstep(0.88, 0.99, length((uv - vec2(0.5)) * 2.0));
	vec2 mask_sample = texture(cloud_layer_occlusion_texture, uv).rg;
	return vec2(mask_sample.r * edge, mask_sample.g);
}

void vertex() {
	cloud_data = use_vertex_cloud_data ? COLOR : INSTANCE_CUSTOM;
	formation_height = world_space_union_mesh
		? clamp(UV2.x, 0.0, 1.0)
		: clamp((VERTEX.y + 0.72) / 1.74, 0.0, 1.0);
	float detail_scale = world_space_union_mesh ? max(1.0, UV2.y) : 1.0;
	optical_depth_factor = world_space_union_mesh
		? smoothstep(450.0, 2400.0, detail_scale)
		: 0.0;
	vec3 detail_position = world_space_union_mesh ? VERTEX * 0.001 : VERTEX;
	float breathing = sin(TIME * 0.17 + cloud_data.g * 6.2831853) * 0.012;
	float broad_detail = sin(detail_position.x * 5.7 + cloud_data.g * 6.2831853)
		* sin(detail_position.z * 6.3 - detail_position.y * 3.1) * 0.018;
	float fine_detail = sin(
		(detail_position.x + detail_position.z) * 11.0
		+ detail_position.y * 7.0) * 0.006;
	VERTEX += NORMAL * (breathing + broad_detail + fine_detail) * detail_scale;
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize(MODEL_NORMAL_MATRIX * NORMAL);
}

void fragment() {
	float camera_distance = distance(world_position, camera_world_position);
	float fade_in_visibility = handoff_in_start_meters <= 0.0
		? 1.0
		: smoothstep(
			handoff_in_start_meters,
			max(handoff_in_start_meters + 1.0, handoff_in_end_meters),
			camera_distance);
	float fade_out_visibility = 1.0 - smoothstep(
		handoff_start_meters,
		max(handoff_start_meters + 1.0, handoff_end_meters),
		camera_distance);
	float representation_visibility = fade_in_visibility * fade_out_visibility;
	if (representation_visibility < 0.5) {
		discard;
	}

	vec3 up = normalize(surface_up);
	vec3 sun = normalize(sun_direction);
	vec3 normal = normalize(world_normal);
	float day = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(up, sun)));
	float sunward = dot(normal, sun) * 0.5 + 0.5;
	float upward = dot(normal, up) * 0.5 + 0.5;
	float body_light = smoothstep(0.08, max(0.09, edge_firmness), sunward * 0.72 + upward * 0.28);
	vec3 daylight = mix(shadow_color, middle_color, upward * 0.55);
	daylight = mix(daylight, sunlit_color, body_light);
	vec3 solar_ambient = mix(shadow_color, middle_color, upward * 0.55);
	float precipitation = clamp(cloud_data.b, 0.0, 1.0);
	float formation_profile = clamp(cloud_data.a * 3.0, 0.0, 3.0);
	float storm_profile = smoothstep(1.45, 2.15, formation_profile);
	float anvil_profile = smoothstep(2.55, 2.95, formation_profile);
	solar_ambient = mix(solar_ambient, shadow_color * 0.78, precipitation * (1.0 - upward) * 0.46);
	solar_ambient = mix(solar_ambient, shadow_color * 0.72, storm_profile * (1.0 - upward) * 0.18);
	daylight = mix(
		daylight,
		shadow_color * 0.78,
		precipitation * (1.0 - upward) * 0.46);
	daylight = mix(
		daylight,
		shadow_color * 0.72,
		storm_profile * (1.0 - upward) * 0.18);
	daylight = mix(daylight, sunlit_color, anvil_profile * upward * body_light * 0.12);
	daylight *= 1.0 - precipitation * 0.08;
	solar_ambient *= 1.0 - precipitation * 0.08;
	float lower_body = 1.0 - smoothstep(0.10, 0.78, formation_height);
	float internal_occlusion = lower_body * mix(
		0.55,
		1.0,
		max(storm_profile, precipitation));
	daylight *= 1.0 - internal_occlusion * cloud_self_shadow_strength;
	solar_ambient *= 1.0 - internal_occlusion * cloud_self_shadow_strength;
	float dense_mass = max(
		max(storm_profile, precipitation),
		optical_depth_factor * 0.92);
	float deep_underside = lower_body
		* smoothstep(0.32, 0.92, dense_mass);
	daylight = mix(
		daylight,
		shadow_color * 0.16,
		deep_underside * 0.82);
	solar_ambient = mix(solar_ambient, shadow_color * 0.16, deep_underside * 0.82);
	vec2 layer_sample = sample_cloud_layer_occlusion(world_position, up, sun);
	float receiver_altitude = dot(
		world_position - cloud_layer_occlusion_center_world,
		up);
	float occluder_top_fraction = clamp(
		(layer_sample.g * 255.0 - 1.0) / 254.0,
		0.0,
		1.0);
	float occluder_top_altitude = mix(
		cloud_layer_occlusion_altitude_min_meters,
		cloud_layer_occlusion_altitude_max_meters,
		occluder_top_fraction);
	float upper_layer = smoothstep(
		100.0,
		800.0,
		occluder_top_altitude - receiver_altitude);
	float layer_occlusion = layer_sample.r * upper_layer;
	float layer_receiver = lower_body * mix(0.35, 1.0, storm_profile);
	daylight *= 1.0
		- layer_occlusion * layer_receiver * cloud_layer_occlusion_strength;
	solar_ambient *= (1.0 - layer_occlusion * layer_receiver * cloud_layer_occlusion_strength) * cloud_data.r;
	daylight *= cloud_data.r;
	vec3 ring_local = terrain_cloud_world_to_ring * (world_position - ring_center);
	float ring_radius = terrain_cloud_ring_geometry.x;
	vec3 receiver_metres = vec3(atan(ring_local.z, ring_local.x) * ring_radius, ring_radius - terrain_cloud_ring_geometry.y - length(ring_local.xz), ring_local.y - terrain_cloud_ring_geometry.z);
	float terrain_sun = ring_terrain_cloud_receiver_visibility(receiver_metres);
	daylight -= max(daylight - solar_ambient, vec3(0.0)) * (1.0 - terrain_sun);
	vec3 cloud_color = mix(night_color, daylight, day);
	RingHaze haze = ring_haze_sample(
		camera_world_position,
		world_position,
		ring_center,
		day);
	vec3 atmosphere_transmittance = haze.T;
	ALBEDO = cloud_color * atmosphere_transmittance;
	EMISSION = cloud_color * mix(0.16, 0.045, day) * atmosphere_transmittance
		+ ring_haze_inscatter(haze);
}
