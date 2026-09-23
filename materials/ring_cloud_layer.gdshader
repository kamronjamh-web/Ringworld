shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_disabled;

uniform vec3 ring_center = vec3(0.0);
uniform vec3 camera_world_position = vec3(0.0);
uniform bool far_space_enabled = true;
uniform float far_space_start_meters = 850000.0;
uniform float far_space_real_end_meters = 10500000.0;
uniform float far_space_render_end_meters = 1150000.0;
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 day_cloud_color : source_color = vec3(0.88, 0.93, 1.0);
uniform vec3 day_cloud_shadow_color : source_color = vec3(0.36, 0.43, 0.53);
uniform vec3 night_cloud_color : source_color = vec3(0.055, 0.085, 0.15);
uniform float opacity : hint_range(0.0, 1.0) = 0.72;
uniform float coverage : hint_range(0.0, 1.0) = 0.50;
uniform float softness : hint_range(0.01, 0.4) = 0.18;
uniform float atmospheric_alpha_fade_strength : hint_range(0.0, 1.0) = 0.42;
uniform bool local_volumetric_handoff_enabled = false;
uniform float local_handoff_start_meters = 7000.0;
uniform float local_handoff_end_meters = 12000.0;
// A bounded, world-seeded macro-weather atlas is shared with the camera-local
// far canopy. This keeps cloud banks and storm flashes coherent around the
// entire ring instead of evaluating unrelated per-pixel fields.
uniform sampler2D weather_atlas : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform bool weather_atlas_enabled = false;
uniform float weather_time_seconds = 0.0;
uniform vec2 weather_wind_uv_per_second = vec2(0.0);
uniform vec4 far_lightning_event_a = vec4(0.0);
uniform vec4 far_lightning_event_b = vec4(0.0);
uniform float far_lightning_event_a_id = 0.0;
uniform float far_lightning_event_b_id = 0.0;
// A/B payloads stay raw source pulses; renderer ownership is a separate pair.
uniform vec2 far_lightning_event_weights = vec2(0.0);
uniform float far_clipmap_handoff_start_meters = 140000.0;
uniform float far_clipmap_handoff_end_meters = 180000.0;
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_far_cloud_field.gdshaderinc"

varying vec3 world_position;
varying float ring_day_factor;
varying vec3 inhabited_surface_up;

vec2 far_lightning_glow(vec2 canonical_uv, vec4 event_data) {
        if (event_data.w <= 0.0 || event_data.z <= 0.0) return vec2(0.0);
        vec2 delta = vec2(fract(canonical_uv.x - event_data.x + 0.5) - 0.5,
                (canonical_uv.y - event_data.y) * ring_width_km / max(ring_circumference_km, 1.0));
        float distance_normalized = length(delta) / event_data.z;
	// Use unwrapped canonical metric derivatives; wrapped deltas are discontinuous
	// at the physical seam and must never determine pixel footprint.
	vec2 metric_uv = vec2(canonical_uv.x,
		canonical_uv.y * ring_width_km / max(ring_circumference_km, 1.0));
	float pixel_metric = max(length(dFdx(metric_uv)), length(dFdy(metric_uv)));
	float core_radius = max(0.24, min(0.75, 1.5 * pixel_metric / event_data.z));
        float broad = (1.0 - smoothstep(0.35, 1.55, distance_normalized)) * event_data.w;
        float core = (1.0 - smoothstep(0.0, core_radius, distance_normalized)) * event_data.w;
        return vec2(core, broad);
}

void vertex() {
	POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	if (far_space_enabled) {
		vec3 view_position = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		float real_distance = length(view_position);
		if (real_distance > far_space_start_meters) {
			float distance_fraction = clamp(
				(real_distance - far_space_start_meters)
				/ max(far_space_real_end_meters - far_space_start_meters, 1.0),
				0.0,
				1.0);
			float render_distance = mix(far_space_start_meters, far_space_render_end_meters, distance_fraction);
			view_position *= render_distance / max(real_distance, 1.0);
			POSITION = PROJECTION_MATRIX * vec4(view_position, 1.0);
		}
	}
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 radial_out = length(radial) > 0.0001 ? normalize(radial) : vec3(1.0, 0.0, 0.0);
	inhabited_surface_up = -radial_out;
	ring_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(inhabited_surface_up, normalize(sun_direction))));
}

void fragment() {
	// The duplicated mesh vertex at U=1 is the same physical point as U=0.
	// Canonicalise it before evaluating the procedural field so the cloud shell
	// cannot show a different last column at the ring seam.
	float angle = fract(UV.x) * 6.28318530718;
	float across_km = (UV.y - 0.5) * ring_width_km;
	vec2 weather_uv = vec2(fract(UV.x), clamp(UV.y, 0.0, 1.0));
	weather_uv.x = fract(weather_uv.x - weather_wind_uv_per_second.x * weather_time_seconds);
	weather_uv.y = clamp(weather_uv.y - weather_wind_uv_per_second.y * weather_time_seconds, 0.0, 1.0);
	vec4 weather = texture(weather_atlas, weather_uv);
	float field = weather_atlas_enabled ? weather.r : ring_far_cloud_field_raw(angle, across_km);
	float storm_factor = weather_atlas_enabled ? weather.g : 0.0;
	float cloud = smoothstep(coverage, min(1.0, coverage + softness), field);
	float handoff_factor = 1.0;
	if (local_volumetric_handoff_enabled) {
		float camera_distance = distance(world_position, camera_world_position);
		handoff_factor *= smoothstep(local_handoff_start_meters, local_handoff_end_meters, camera_distance);
		// The global shell owns the horizon beyond the curved local clipmap.
		// Keeping a short overlap avoids a visible radial seam without drawing
		// two fully opaque cloud layers through the same mid-distance pixels.
		handoff_factor *= smoothstep(far_clipmap_handoff_start_meters, far_clipmap_handoff_end_meters, camera_distance);
	}
	cloud *= handoff_factor;
	float width_fade = smoothstep(0.0, 0.035, UV.y) * (1.0 - smoothstep(0.965, 1.0, UV.y));
	cloud *= width_fade;
	vec2 flash_a = far_lightning_glow(UV, far_lightning_event_a);
	vec2 flash_b = far_lightning_glow(UV, far_lightning_event_b);
	vec2 raw_flash = min(flash_a + flash_b, vec2(1.15, 1.35));
	vec2 far_flash = min(
		flash_a * far_lightning_event_weights.x + flash_b * far_lightning_event_weights.y,
		vec2(1.15, 1.35));
	// Shared events are already storm-validated on CPU. Keep their bounded
	// carrier through an atlas-clear texel and the short physical ring edge.
	float flash_edge = smoothstep(0.0, 0.0025, UV.y) * (1.0 - smoothstep(0.9975, 1.0, UV.y));
	// Distance ownership already supplies the local/clipmap/shell crossfade.
	// Do not multiply the cloud-body handoff here or flash alpha would square it.
	float flash_carrier = far_flash.y * flash_edge;
	if (cloud < 0.015 && flash_carrier < 0.001) discard;

	// Determine side physically rather than trusting mesh winding: the scaled
	// and legacy shells have different transforms. A camera toward ring centre
	// sees the sunlit exterior/top; one toward terrain sees the inhabited-side
	// underside. FRONT_FACING only resolves the exact grazing-angle fallback.
	float view_side = dot(normalize(camera_world_position - world_position), inhabited_surface_up);
	float exterior_weight = abs(view_side) > 0.001
		? step(0.0, view_side)
		: (FRONT_FACING ? 1.0 : 0.0);
	// Both faces use the same sky-facing local daylight. Only their brightness
	// differs; radial_out would incorrectly make the top night-coloured at
	// local noon.
	float face_day_factor = ring_day_factor;
	vec3 day_color = mix(day_cloud_shadow_color, day_cloud_color, clamp(cloud * 1.35, 0.0, 1.0));
	vec3 underside_color = mix(night_cloud_color, day_color * vec3(0.78, 0.83, 0.92), ring_day_factor);
	vec3 exterior_color = mix(night_cloud_color * 0.72, day_color * vec3(1.04, 1.02, 0.98), ring_day_factor);
	vec3 cloud_color = mix(underside_color, exterior_color, exterior_weight);
	vec4 atmosphere = sample_ring_atmosphere(camera_world_position, world_position, ring_center, face_day_factor);
	float atmosphere_occlusion = atmosphere.a * 0.82;
	vec3 base_albedo = cloud_color * (1.0 - atmosphere_occlusion);
	float base_alpha = cloud * opacity
		* (1.0 - atmosphere.a * atmospheric_alpha_fade_strength);
	// The CPU selects real seeded storm systems and publishes the same bounded
	// events to this whole-ring shell and the camera-local clipmap. This keeps a
	// distant flash coherent instead of breaking it into sub-pixel atlas sparks.
	float storm_gate = 1.0;
	vec3 far_glow = vec3(0.18, 0.28, 0.46) * far_flash.y * storm_gate * 0.55
		+ vec3(0.82, 0.91, 1.0) * far_flash.x * storm_gate * 2.4;
	float flash_mask = flash_edge * opacity
		* (1.0 - atmosphere.a * atmospheric_alpha_fade_strength);
	float reference_alpha = max(base_alpha, raw_flash.y * 0.22 * flash_mask);
	// Raw reference alpha restores the old full-weight response over an opaque
	// body; weighted radiance keeps partial ownership linear.
	vec3 flash_premultiplied = far_glow * reference_alpha;
	// Premultiplied blending lets zero-alpha flash radiance add over a bright
	// far backdrop instead of making a translucent carrier darken it. Physical
	// cloud opacity stays base-only; flash_carrier still bounds discard/radiance.
	ALBEDO = base_albedo * base_alpha + flash_premultiplied;
	EMISSION = (cloud_color * mix(0.22, 0.065, face_day_factor)
		+ atmosphere.rgb * atmosphere_occlusion * atmosphere_scattering_energy) * base_alpha;
	ALPHA = base_alpha;
}
