shader_type spatial;
render_mode cull_back, depth_draw_opaque;

// Far stand-in for emitter POIs (docs/emitter-poi-streaming-spec.md section B).
// INSTANCE_CUSTOM.r = presented by the POI streamer (near handoff fade band applies); .g = hard hidden.
uniform vec3 camera_world_position = vec3(0.0);
uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 albedo_color : source_color = vec3(0.18, 0.23, 0.28);
uniform float metallic_value : hint_range(0.0, 1.0) = 0.72;
uniform float roughness_value : hint_range(0.0, 1.0) = 0.48;
uniform bool use_instance_custom = true;
uniform float max_distance = 700000.0;
uniform float fade_meters = 100000.0;
uniform float min_pixels = 1.5;
uniform float near_begin = 11500.0;
uniform float near_margin = 1000.0;
uniform float object_height_m = 1800.0;
uniform vec3 distance_pivot = vec3(0.0);
// Eclipse crown glow (phase E4e, spec E.7): INSTANCE_CUSTOM.b = beam activity 0..1 lights the mesh between crown_glow_band.x and .y (mesh-local Y).
uniform vec2 crown_glow_band = vec2(0.0, 1.0);
uniform float crown_glow_emission = 3.0;
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"

varying vec3 world_position;
varying float ring_day_factor;
varying float standin_fade;
varying float standin_coverage;
varying float crown_glow_amount;

void vertex() {
	vec3 pivot_world = (MODEL_MATRIX * vec4(distance_pivot, 1.0)).xyz;
	float d = distance(pivot_world, camera_world_position);
	bool hidden = use_instance_custom && INSTANCE_CUSTOM.g > 0.5;
	bool presented = use_instance_custom && INSTANCE_CUSTOM.r > 0.5;
	float near_start = near_begin - near_margin;
	float far_fade = 1.0 - smoothstep(max(max_distance - fade_meters, 0.0), max_distance, d);
	float near_fade = presented ? smoothstep(near_start, near_begin, d) : 1.0;
	standin_fade = far_fade * near_fade;
	// abs(): the RenderingDevice projection carries a Y flip, so PROJECTION_MATRIX[1][1] is negative there.
	standin_coverage = d > 1.0
		? clamp(object_height_m / d * abs(PROJECTION_MATRIX[1][1]) * VIEWPORT_SIZE.y * 0.5 / max(min_pixels, 0.01), 0.0, 1.0)
		: 1.0;
	// 0 unless the eclipse event lights this crown; the multiplication by the band mask keeps everything else exact.
	crown_glow_amount = use_instance_custom && INSTANCE_CUSTOM.b > 0.0
		? INSTANCE_CUSTOM.b * smoothstep(crown_glow_band.x, crown_glow_band.y, VERTEX.y)
		: 0.0;
	if (hidden || d > max_distance || (presented && d < near_start)) {
		VERTEX = vec3(0.0);
	}
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001
		? -normalize(radial)
		: vec3(-1.0, 0.0, 0.0);
	ring_day_factor = ringworld_effective_solar_day(smoothstep(
		day_start_dot,
		day_full_dot,
		dot(surface_up, normalize(sun_direction))));
}

void fragment() {
	float noise = fract(52.9829189 * fract(dot(FRAGCOORD.xy, vec2(0.06711056, 0.00583715))));
	if (noise > standin_fade * standin_coverage) {
		discard;
	}
	vec3 lit_color = albedo_color * mix(vec3(0.30, 0.42, 0.62), vec3(1.0), ring_day_factor);
	vec4 atmosphere = sample_ring_atmosphere(
		camera_world_position,
		world_position,
		ring_center,
		ring_day_factor);
	ALBEDO = lit_color * (1.0 - atmosphere.a);
	EMISSION = atmosphere.rgb * atmosphere.a * atmosphere_scattering_energy;
	if (crown_glow_amount > 0.0) {
		// Beam teal; the glow is not attenuated by the day (the beams are the event) but by the same atmosphere opacity.
		EMISSION += vec3(0.70, 0.92, 1.0) * crown_glow_amount * crown_glow_emission * (1.0 - atmosphere.a);
	}
	METALLIC = metallic_value;
	ROUGHNESS = roughness_value;
	SPECULAR = 0.68;
	float cloud_occlusion = sample_local_cloud_shadow(world_position) * ring_day_factor;
	AO = 1.0 - cloud_occlusion;
	AO_LIGHT_AFFECT = 1.0;
}
