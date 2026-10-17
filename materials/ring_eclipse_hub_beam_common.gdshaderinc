// Eclipse spire to hub beams (docs/eclipse-false-sun-spec.md E.2, phase E4b), shared by:
//   ring_eclipse_hub_beam.gdshader       near layer, additive core light and halo (depth_draw_never)
//   ring_eclipse_hub_beam_core.gdshader  near layer, depth-only core pass (EB_CORE_PASS, next_pass of the halo material)
//   ring_eclipse_hub_beam_far.gdshader   far-viewport layer in scaled space (EB_FAR_LAYER)
// One MultiMesh instance per spire: origin = the beam origin (bore iris, crown or reactor, EclipseBeamSettings.BeamOrigin), basis X = hub - origin, INSTANCE_CUSTOM = (p_site, column, fire tau, kind).
// All beam state is on the GPU: the tip climb, handoff, cut and retract come from the ev_* uniforms and the per-beam fire tau
// (ring_eclipse_hub_beam_math.gdshaderinc), so a steady frame writes no per-beam data. The ribbon mesh is built from sub-segments with
// UV = (f, side), UV2 = (f_a, f_b) of the enclosing sub-segment (EclipseHubBeamLayer.BuildRibbonMesh).
#ifdef EB_FAR_LAYER
#define EG_FAR_LAYER
#endif
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_emitter_grid_layers.gdshaderinc"
#include "res://materials/ring_beam_ribbon.gdshaderinc"
#include "res://materials/ring_eclipse_hub_beam_math.gdshaderinc"

// x regular core width (m), y regular halo width (m), z minimum pixel width, w far compensation exponent.
uniform vec4 beam_widths = vec4(150.0, 1200.0, 1.5, 0.45);
// x main core width (m), y main halo width (m).
uniform vec2 main_widths = vec2(300.0, 2400.0);
uniform float main_emission_scale = 1.5;
uniform float halo_amplitude = 0.12;
// The halo fades in over this many metres above the beam origin, so it does not start with a hard horizontal edge (the bore core does).
uniform float base_fade_meters = 400.0;
// Close-range softening (tweaks T2): the light fades in over this many metres of view distance (smoothstep from 0); 0 = off.
uniform float close_soften_m = 1000.0;
// Sustain refuel orbs (tweaks T3): x halo e-fold radius (m), y HDR emission. The core radius and minimum pixel radius are ev_orb.zw.
uniform vec2 orb_look = vec2(1500.0, 16.0);
const float ORB_HALO_AMPLITUDE = 0.25;
// Depth-writing core (T2): x = coverage * level above which the core writes depth, y = minimum full width of the footprint in pixels.
uniform vec2 core_depth = vec2(0.35, 4.0);

varying float v_f;
varying float v_x;
varying float v_side;
varying vec4 v_profile;
varying float v_atmosphere;
varying vec2 v_weights;
varying float v_depth_half;
varying flat vec4 v_beam;
// x 1 on an orb quad (0 on a ribbon), y orb envelope, z orb quad half size (world units).
varying flat vec3 v_orb;
varying flat float v_length_m;
varying flat vec4 v_cloud;
varying flat vec3 v_origin_w;
varying flat vec3 v_span_w;

void vertex() {
	vec4 beam = INSTANCE_CUSTOM;
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
	bool is_main = beam.w > 0.5;
	float length_m = length(MODEL_MATRIX[0].xyz) * inv_scale;
#ifdef EG_FAR_LAYER
	// The far layer carries no cloud data (its MultiMesh has no colours); the deck is a near-spire effect.
	vec4 cloud = vec4(0.0);
#else
	vec4 cloud = COLOR;
#endif

	// Tweaks T3: the orb quad (UV2 = (-1, -1)) is one camera-facing billboard at the orb's position along the beam.
	if (UV2.x < -0.5) {
		vec2 orb = ehb_orb(beam.z, beam.w, beam.x, beam.y);
		vec3 w_c = origin + span * orb.y;
		vec3 view_c = (VIEW_MATRIX * vec4(w_c, 1.0)).xyz;
		float real_c = distance(w_c, camera) * inv_scale;
#ifdef EG_FAR_LAYER
		bool orb_range = real_c > eg_far_start();
#else
		bool orb_range = real_c < max_distance;
#endif
		bool orb_draw = orb_range && orb.x > 0.0 && ev_level > 0.0 && view_c.z < -near_clip;
		v_f = orb.y;
		v_beam = beam;
		v_cloud = vec4(0.0);
		v_length_m = length_m;
		v_origin_w = origin;
		v_span_w = span;
		if (!orb_draw) {
			v_x = 0.0;
			v_side = 0.0;
			v_profile = vec4(1.0, 1.0, 0.0, 0.0);
			v_atmosphere = 0.0;
			v_weights = vec2(0.0);
			v_depth_half = 0.0;
			v_orb = vec3(1.0, 0.0, 1.0);
			POSITION = vec4(2.0, 2.0, 2.0, 1.0);
		} else {
			float orb_mpp = ribbon_meters_per_pixel(length(view_c), PROJECTION_MATRIX[1][1], VIEWPORT_SIZE.y);
			float orb_core = max(ev_orb.z, 0.001) * world_scale;
			float orb_halo = max(orb_look.x, 0.001) * world_scale;
			vec4 orb_profile = ribbon_profile(orb_core, orb_halo, ev_orb.w, orb_mpp, beam_widths.w);
			float orb_depth_half = ribbon_depth_half(core_depth.y, orb_mpp);
			float half_size = max(8.0 * orb_profile.y, max(5.0 * orb_profile.x, orb_depth_half + orb_mpp));
			POSITION = PROJECTION_MATRIX * vec4(view_c + vec3(UV * half_size, 0.0), 1.0);
			v_x = UV.x * half_size;
			v_side = UV.y * half_size;
			v_profile = orb_profile;
			v_atmosphere = ribbon_atmosphere_visibility(camera * inv_scale, w_c * inv_scale, ring_center * inv_scale, 1.0);
			v_weights = vec2(eg_near_fade(real_c), eg_band_weight(real_c));
			v_depth_half = orb_depth_half;
			v_orb = vec3(1.0, orb.x, half_size);
		}
	} else {

#ifdef EG_FAR_LAYER
	bool in_range = mid_distance + half_segment > eg_far_start();
#else
	bool in_range = mid_distance - half_segment < max_distance;
#endif
	// Tweaks T3: the Sustain refuel is an orb (the quad above); the beam body is never relit.
	bool lit = ehb_lit(beam.z, beam.w, f_a);

	vec3 view_a = (VIEW_MATRIX * vec4(w_a, 1.0)).xyz;
	vec3 view_b = (VIEW_MATRIX * vec4(w_b, 1.0)).xyz;
	bool is_end = UV.x > 0.5 * (f_a + f_b);
	vec3 view_here = is_end ? view_b : view_a;
	vec3 view_other = is_end ? view_a : view_b;
	float f_here = UV.x;
	float f_other = is_end ? f_a : f_b;
	bool draw_it = in_range && lit;
	// Clip against the near plane by sliding this vertex toward the other end of its sub-segment.
	if (!ribbon_clip_near(view_here, f_here, view_other, f_other, near_clip)) {
		draw_it = false;
	}
	if (!draw_it) {
		v_f = f_here;
		v_x = 0.0;
		v_side = 0.0;
		v_profile = vec4(1.0, 1.0, 0.0, 0.0);
		v_atmosphere = 0.0;
		v_weights = vec2(0.0);
		v_depth_half = 0.0;
		v_beam = beam;
		v_orb = vec3(0.0);
		v_length_m = 1.0;
		v_cloud = vec4(0.0);
		v_origin_w = origin;
		v_span_w = span;
		POSITION = vec4(2.0, 2.0, 2.0, 1.0);
	} else {
		vec3 world_here = (INV_VIEW_MATRIX * vec4(view_here, 1.0)).xyz;
		float distance_here = length(view_here);
		vec3 side_view = ribbon_side_vector((VIEW_MATRIX * vec4(span, 0.0)).xyz, view_here, distance_here);

		float meters_per_pixel = ribbon_meters_per_pixel(distance_here, PROJECTION_MATRIX[1][1], VIEWPORT_SIZE.y);
		float core_sigma = max(0.5 * (is_main ? main_widths.x : beam_widths.x), 0.001) * world_scale;
		float halo_length = max(0.25 * (is_main ? main_widths.y : beam_widths.y), 0.001) * world_scale;
		vec4 profile = ribbon_profile(core_sigma, halo_length, beam_widths.z, meters_per_pixel, beam_widths.w);
		float core_eff = profile.x;
		float halo_eff = profile.y;
#ifdef EB_CORE_PASS
		float depth_half = ribbon_depth_half(core_depth.y, meters_per_pixel);
		float half_width = ribbon_half_width_core(core_eff, depth_half, meters_per_pixel);
#else
		float depth_half = 0.0;
		// Six halo e-folds (the grid uses four): the beams are far brighter than the grid links, so a four e-fold edge shows as a hard vertical step.
		// In-cloud diffusion widens the halo over the deck: the strip of a sub-segment that overlaps the deck is made wide enough for the widened halo.
		float cloud_widen = 1.0;
		if (cloud.w > 0.5 && cloud.x > 0.0 && cloud_params.w > 0.5 && f_b * length_m > cloud.y - 400.0 && f_a * length_m < cloud.z + 400.0) {
			cloud_widen = 1.0 + cloud_params.x * cloud.x;
		}
		float half_width = max(3.5 * core_eff, 6.0 * halo_eff * cloud_widen);
#endif

		vec3 view_expanded = view_here + side_view * (UV.y * half_width);
		POSITION = PROJECTION_MATRIX * vec4(view_expanded, 1.0);

		float real_distance = distance(world_here, camera) * inv_scale;
		// The beams ignore the background B: the atmosphere only attenuates them (background 1).
		float atmosphere_visibility = ribbon_atmosphere_visibility(camera * inv_scale, world_here * inv_scale, ring_center * inv_scale, 1.0);

		v_f = f_here;
		v_x = UV.y * half_width;
		v_side = UV.y;
		v_profile = profile;
		v_atmosphere = atmosphere_visibility;
		v_weights = vec2(eg_near_fade(real_distance), eg_band_weight(real_distance));
		v_depth_half = depth_half;
		v_beam = beam;
		v_orb = vec3(0.0);
		v_length_m = length_m;
		v_cloud = cloud;
		v_origin_w = origin;
		v_span_w = span;
	}
	}
}

void fragment() {
#ifdef EG_FAR_LAYER
	float layer_weight = eg_layer_weight(v_weights, 0.0);
#else
	float layer_weight = eg_layer_weight(v_weights, texture(eg_scene_depth, SCREEN_UV).r);
#endif
	if (v_orb.x > 0.5) {
		// Tweaks T3: the orb is a radial profile (white Gaussian core, teal exponential halo) around the billboard centre.
		float orb_r = length(vec2(v_x, v_side));
		float orb_level = ehb_orb_level(v_orb.y, ringworld_hub_day, v_atmosphere, layer_weight);
#ifdef EB_CORE_PASS
		// Depth only where the lit orb core is bright, widened to the minimum depth footprint: clouds behind an orb do not dim it.
		if (!ribbon_depth_core_covers(orb_level * v_profile.z, max(core_depth.x, 1.0e-4), v_profile.x, orb_r, v_depth_half)) {
			discard;
		}
		ALBEDO = vec3(0.0);
		ALPHA = 1.0;
#else
		float orb_total = ribbon_cross_profile(orb_r, v_profile, ORB_HALO_AMPLITUDE);
		float orb_core_only = ribbon_cross_profile(orb_r, v_profile, 0.0);
		float orb_edge = 1.0 - smoothstep(0.7, 1.0, orb_r / max(v_orb.z, 1.0e-6));
		float orb_value = orb_level * orb_total * orb_edge * orb_look.y;
		if (orb_value < 0.0001) {
			discard;
		}
		ALBEDO = mix(beam_color, vec3(1.0), clamp(orb_core_only / max(orb_total, 1.0e-6), 0.0, 1.0)) * orb_value;
		ALPHA = 1.0;
#endif
	} else {
	float level = ehb_level(v_beam.z, v_beam.w, v_f, ringworld_hub_day, v_atmosphere, layer_weight);
#ifdef EB_CORE_PASS
	// Depth only where the lit core is bright (coverage * level > threshold), widened to core_depth.y pixels so that it registers in
	// Sunshine's 2x2 furthest-depth prepass. Clouds behind the beam then no longer dim it (T2).
	// Phase E4e: inside dense cloud the depth core is cut, so Sunshine blends that segment of the beam.
	float cloud_factor = ehb_cloud_factor(v_cloud, v_f * v_length_m);
	float peak = level * v_profile.z * ehb_cloud_core_keep(cloud_factor);
	float threshold = max(core_depth.x, 1.0e-4);
	if (!ribbon_depth_core_covers(peak, threshold, v_profile.x, v_x, v_depth_half)) {
		discard;
	}
	ALBEDO = vec3(0.0);
	ALPHA = 1.0;
#else
	float base_fade = base_fade_meters > 0.0 ? smoothstep(0.0, 1.0, v_f * v_length_m / base_fade_meters) : 1.0;
	// Phase E4e: in-cloud diffusion. The halo widens by 1 + widening k and brightens by 1 + boost k over the deck span.
	float cloud_factor = ehb_cloud_factor(v_cloud, v_f * v_length_m);
	vec4 profile = v_profile;
	profile.y *= 1.0 + cloud_params.x * cloud_factor;
	float total = ribbon_cross_profile(v_x, profile, halo_amplitude * base_fade * (1.0 + cloud_params.z * cloud_factor));
	float core = ribbon_cross_profile(v_x, profile, 0.0);
	float edge = ribbon_edge(v_side);
	float emission = base_emission * (v_beam.w > 0.5 ? main_emission_scale : 1.0);
	float value = level * total * edge * emission;
	if (close_soften_m > 0.0) {
		// View distance to the beam axis point of this fragment: a camera at the bore rim is not whited out by the core.
		float view_m = distance(v_origin_w + v_span_w * v_f, EG_CAMERA) * eg_inv_scale();
		value *= smoothstep(0.0, 1.0, view_m / close_soften_m);
	}
	if (value < 0.0001) {
		discard;
	}
	// Beam teal with a white core.
	vec3 color = mix(beam_color, vec3(1.0), clamp(core / max(total, 1.0e-6), 0.0, 1.0));
	ALBEDO = color * value;
	ALPHA = 1.0;
#endif
	}
}
