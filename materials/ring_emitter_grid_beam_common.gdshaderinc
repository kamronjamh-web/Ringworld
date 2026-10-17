// Emitter relay grid link ribbons (docs/emitter-grid-climate-machines-spec.md G2-G5), shared by:
//   ring_emitter_grid_beam.gdshader       near layer, additive halo + core light (depth_draw_never)
//   ring_emitter_grid_beam_core.gdshader  near layer, depth-only core pass (EG_CORE_PASS, next_pass of the halo material)
//   ring_emitter_grid_beam_far.gdshader   far-viewport layer in scaled space (EG_FAR_LAYER)
// One MultiMesh instance per link: origin = endpoint A, basis X = B - A, INSTANCE_CUSTOM = (P0, P1, column, kind).
// The mesh is 16 independent sub-segments of 4 vertices: UV = (f, side), UV2 = (f_a, f_b) of the enclosing sub-segment.
#include "res://materials/ring_emitter_grid_pulse.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_emitter_grid_layers.gdshaderinc"

// x core width (m), y halo width (m), z minimum pixel width, w far compensation exponent.
uniform vec4 beam_widths = vec4(40.0, 600.0, 1.5, 0.6);
uniform float halo_amplitude = 0.12;
// Debug: forces the residual to 1 and ignores the collapse rule.
uniform float show_all = 0.0;
// Depth-writing core (T2): x = CoreDepthThreshold (coverage * I above which the core writes depth),
// y = CoreDepthMinPixels (minimum full width of the depth footprint, so it survives Sunshine's 2x2 furthest-depth prepass).
uniform vec2 core_depth = vec2(0.35, 4.0);

varying vec3 v_world;
varying float v_f;
varying float v_x;
varying float v_side;
varying vec4 v_profile;
varying vec2 v_visibility;
varying vec2 v_weights;
varying float v_depth_half;
varying flat vec4 v_link;

void vertex() {
	vec4 link = INSTANCE_CUSTOM;
	float f_a = UV2.x;
	float f_b = UV2.y;
	vec3 origin = MODEL_MATRIX[3].xyz;
	vec3 span = MODEL_MATRIX[0].xyz;
	vec3 w_a = origin + span * f_a;
	vec3 w_b = origin + span * f_b;
	vec3 w_mid = 0.5 * (w_a + w_b);
	vec3 camera = EG_CAMERA;
	float inv_scale = eg_inv_scale();
	float mid_distance = distance(w_mid, camera) * inv_scale;
	float half_segment = 0.5 * distance(w_a, w_b) * inv_scale;

	float p_a = mix(link.x, link.y, f_a);
	float p_b = mix(link.x, link.y, f_b);
	vec3 up_mid = w_mid - ring_center;
	up_mid.y = 0.0;
	up_mid = length(up_mid) > 0.0 ? -normalize(up_mid) : vec3(-1.0, 0.0, 0.0);
	vec3 sun = normalize(sun_direction);
	vec3 up_camera = camera - ring_center;
	up_camera.y = 0.0;
	up_camera = length(up_camera) > 0.0 ? -normalize(up_camera) : vec3(-1.0, 0.0, 0.0);
	float day_camera = eg_day_at_hub(dot(up_camera, sun), ringworld_solar_visibility, ringworld_hub_day);
	float day_mid = eg_day_at_hub(dot(up_mid, sun), ringworld_solar_visibility, ringworld_hub_day);
	float b_mid = eg_background(day_camera, day_mid, mid_distance);

#ifdef EG_FAR_LAYER
	bool in_range = mid_distance + half_segment > eg_far_start();
#else
	bool in_range = mid_distance - half_segment < max_distance;
#endif
#ifdef EG_CORE_PASS
	// The residual alone can only write depth when it exceeds the threshold (never at the defaults).
	bool lit = eg_interval_lit(p_a, p_b, link.z, link.w) || eg_residual(b_mid) > core_depth.x || show_all > 0.5;
#else
	bool lit = eg_interval_lit(p_a, p_b, link.z, link.w) || eg_residual(b_mid) >= 0.001 || show_all > 0.5;
#endif

	vec3 view_a = (VIEW_MATRIX * vec4(w_a, 1.0)).xyz;
	vec3 view_b = (VIEW_MATRIX * vec4(w_b, 1.0)).xyz;
	bool is_end = UV.x > 0.5 * (f_a + f_b);
	vec3 view_here = is_end ? view_b : view_a;
	vec3 view_other = is_end ? view_a : view_b;
	float f_here = UV.x;
	float f_other = is_end ? f_a : f_b;
	bool draw_it = in_range && lit;
	// Clip against the near plane by sliding this vertex toward the other end of its sub-segment.
	if (view_here.z > -near_clip) {
		if (view_other.z > -near_clip) {
			draw_it = false;
		} else {
			float k = (-near_clip * 1.01 - view_here.z) / (view_other.z - view_here.z);
			view_here = mix(view_here, view_other, k);
			f_here = mix(f_here, f_other, k);
		}
	}
	if (!draw_it) {
		v_world = w_mid;
		v_f = f_here;
		v_x = 0.0;
		v_side = 0.0;
		v_profile = vec4(1.0, 1.0, 0.0, 0.0);
		v_visibility = vec2(0.0);
		v_weights = vec2(0.0);
		v_depth_half = 0.0;
		v_link = link;
		POSITION = vec4(2.0, 2.0, 2.0, 1.0);
	} else {
		vec3 world_here = (INV_VIEW_MATRIX * vec4(view_here, 1.0)).xyz;
		float distance_here = length(view_here);
		vec3 dir_view = normalize((VIEW_MATRIX * vec4(span, 0.0)).xyz);
		vec3 side_view = cross(dir_view, view_here / max(distance_here, 1.0e-9));
		float side_length = length(side_view);
		side_view = side_length > 0.0001 ? side_view / side_length : vec3(1.0, 0.0, 0.0);

		// World units per pixel. abs(): the RenderingDevice projection carries a Y flip.
		float meters_per_pixel = distance_here / (abs(PROJECTION_MATRIX[1][1]) * VIEWPORT_SIZE.y * 0.5);
		float core_sigma = max(0.5 * beam_widths.x, 0.001) * world_scale;
		float halo_length = max(0.25 * beam_widths.y, 0.001) * world_scale;
		float footprint = 0.35 * beam_widths.z * meters_per_pixel;
		float core_eff = max(core_sigma, footprint);
		float halo_eff = max(halo_length, footprint);
		// Energy conservation: widening a profile to the pixel footprint scales its amplitude by (true / drawn)^FarCompensation.
		float core_gain = pow(core_sigma / core_eff, beam_widths.w);
		float halo_gain = pow(halo_length / halo_eff, beam_widths.w);
#ifdef EG_CORE_PASS
		// Half of the minimum depth footprint; the strip is one pixel wider so the footprint is never clipped by rasterisation.
		float depth_half = 0.5 * max(core_depth.y, 0.0) * meters_per_pixel;
		float half_width = max(3.5 * core_eff, depth_half + meters_per_pixel);
#else
		float depth_half = 0.0;
		float half_width = max(3.5 * core_eff, 4.0 * halo_eff);
#endif

		vec3 view_expanded = view_here + side_view * (UV.y * half_width);
		POSITION = PROJECTION_MATRIX * vec4(view_expanded, 1.0);

		float real_distance = distance(world_here, camera) * inv_scale;
		vec3 up_here = world_here - ring_center;
		up_here.y = 0.0;
		up_here = length(up_here) > 0.0 ? -normalize(up_here) : vec3(-1.0, 0.0, 0.0);
		float day_here = eg_day_at_hub(dot(up_here, sun), ringworld_solar_visibility, ringworld_hub_day);
		float b_here = eg_background(day_camera, day_here, real_distance);
		// The atmosphere model works in real metres (identity in the main world).
		vec4 atmosphere = sample_ring_atmosphere(camera * inv_scale, world_here * inv_scale, ring_center * inv_scale, b_here);

		v_world = world_here;
		v_f = f_here;
		v_x = UV.y * half_width;
		v_side = UV.y;
		v_profile = vec4(core_eff, halo_eff, core_gain, halo_gain);
		v_visibility = vec2(b_here, 1.0 - atmosphere.a);
		v_weights = vec2(eg_near_fade(real_distance), eg_band_weight(real_distance));
		v_depth_half = depth_half;
		v_link = link;
	}
}

void fragment() {
#ifdef EG_FAR_LAYER
	float layer_weight = eg_layer_weight(v_weights, 0.0);
#else
	float layer_weight = eg_layer_weight(v_weights, texture(eg_scene_depth, SCREEN_UV).r);
#endif
	float p = v_link.x + v_f * (v_link.y - v_link.x);
	float pulse = eg_pulse_at_link(p, v_link.z, v_link.w);
	float b = v_visibility.x;
	float residual = show_all > 0.5 ? 1.0 : eg_residual(b);
	float level = (eg_peak(b) * pulse + residual) * v_visibility.y * layer_weight;
#ifdef EG_CORE_PASS
	// Depth only where the lit core is bright (coverage * I > threshold): the travelling pulse, never the faint residual,
	// so the hole it cuts in clouds behind it moves with the pulse. Widened to core_depth.y pixels so that it registers in
	// Sunshine's 2x2 furthest-depth prepass.
	float peak = level * v_profile.z;
	float threshold = max(core_depth.x, 1.0e-4);
	if (peak <= threshold) {
		discard;
	}
	float natural_half = v_profile.x * sqrt(2.0 * log(peak / threshold));
	if (abs(v_x) > max(natural_half, v_depth_half)) {
		discard;
	}
	ALBEDO = vec3(0.0);
	ALPHA = 1.0;
#else
	float cross_profile = v_profile.z * exp(-0.5 * v_x * v_x / (v_profile.x * v_profile.x))
		+ halo_amplitude * v_profile.w * exp(-abs(v_x) / v_profile.y);
	float edge = 1.0 - smoothstep(0.8, 1.0, abs(v_side));
	float value = level * cross_profile * edge * base_emission;
	if (value < 0.0001) {
		discard;
	}
	ALBEDO = beam_color * value;
	ALPHA = 1.0;
#endif
}
