// Pulse head glow sprites (docs/emitter-grid-climate-machines-spec.md G3/G4.2), shared by the near layer
// (ring_emitter_grid_head.gdshader) and the far layer (ring_emitter_grid_head_far.gdshader, EG_FAR_LAYER).
// Camera-facing quads with a minimum pixel size, faded in between head_near_m and head_full_m of view distance (nearer the
// ribbon's head streak is continuous). INSTANCE_CUSTOM.r = brightness gain. The CPU writes one instance per live head.
#include "res://materials/ring_emitter_grid_pulse.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_emitter_grid_layers.gdshaderinc"

uniform float sprite_min_pixels = 3.0;
uniform float head_near_m = 300000.0;
uniform float head_full_m = 600000.0;

varying vec2 v_uv;
varying float v_level;
varying vec2 v_weights;

void vertex() {
	vec3 camera = EG_CAMERA;
	float inv_scale = eg_inv_scale();
	vec3 center_world = MODEL_MATRIX[3].xyz;
	vec3 center_view = (VIEW_MATRIX * vec4(center_world, 1.0)).xyz;
	float real_distance = distance(center_world, camera) * inv_scale;
	float fade_in = smoothstep(head_near_m, max(head_full_m, head_near_m + 1.0), real_distance);
	vec2 weights = vec2(eg_near_fade(real_distance), eg_band_weight(real_distance));
#ifdef EG_FAR_LAYER
	float reach = 1.0 - min(weights.x, weights.y);
#else
	float reach = weights.x;
#endif
	if (center_view.z > -near_clip || fade_in * reach <= 0.0001) {
		v_uv = vec2(0.0);
		v_level = 0.0;
		v_weights = vec2(0.0);
		POSITION = vec4(2.0, 2.0, 2.0, 1.0);
	} else {
		float distance_view = length(center_view);
		float meters_per_pixel = distance_view / (abs(PROJECTION_MATRIX[1][1]) * VIEWPORT_SIZE.y * 0.5);
		// The quad spans 4x the minimum size so the Gaussian core (sigma = size / 2) fits.
		float quad_half = 2.0 * max(sprite_min_pixels, 0.5) * meters_per_pixel;
		vec3 corner = center_view + vec3(VERTEX.xy * 2.0 * quad_half, 0.0);
		POSITION = PROJECTION_MATRIX * vec4(corner, 1.0);

		vec3 sun = normalize(sun_direction);
		vec3 up_here = center_world - ring_center;
		up_here.y = 0.0;
		up_here = length(up_here) > 0.0 ? -normalize(up_here) : vec3(-1.0, 0.0, 0.0);
		vec3 up_camera = camera - ring_center;
		up_camera.y = 0.0;
		up_camera = length(up_camera) > 0.0 ? -normalize(up_camera) : vec3(-1.0, 0.0, 0.0);
		float day_camera = eg_day_at(dot(up_camera, sun), ringworld_solar_visibility);
		float day_here = eg_day_at(dot(up_here, sun), ringworld_solar_visibility);
		float b = eg_background(day_camera, day_here, real_distance);
		vec4 atmosphere = sample_ring_atmosphere(camera * inv_scale, center_world * inv_scale, ring_center * inv_scale, b);
		v_uv = VERTEX.xy * 2.0;
		// The head is the pulse peak (profile 1), so its level is Peak(B) attenuated by the atmosphere.
		v_level = eg_intensity(1.0, b, 0.0, atmosphere.a) * fade_in * INSTANCE_CUSTOM.r;
		v_weights = weights;
	}
}

void fragment() {
#ifdef EG_FAR_LAYER
	float layer_weight = eg_layer_weight(v_weights, 0.0);
#else
	float layer_weight = eg_layer_weight(v_weights, texture(eg_scene_depth, SCREEN_UV).r);
#endif
	float r2 = dot(v_uv, v_uv);
	// sigma = 1/4 of the half-extent: core exp(-8 r^2) plus a soft skirt.
	float shape = exp(-8.0 * r2) + 0.1 * exp(-3.0 * sqrt(r2));
	float edge = 1.0 - smoothstep(0.85, 1.0, sqrt(r2));
	float value = v_level * layer_weight * shape * edge * base_emission;
	if (value < 0.0001) {
		discard;
	}
	ALBEDO = beam_color * value;
	ALPHA = 1.0;
}
