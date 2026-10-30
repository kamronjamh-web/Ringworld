shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, depth_test_disabled, cull_front, shadows_disabled, fog_disabled;
// Megastorm far wedge pass (docs/megastorm-design.md 3.1, 3.3; WO5): the wall body and the leading anvil beyond the Sunshine handoff, at any
// distance up to the antipode, drawn in the scaled far viewport by a camera-centred sphere (RingScaledFarViewport.Megastorm, node
// FarMegastorm; hidden, so free, while no megastorm is active). Per pixel the view ray is clipped analytically to the wedge between the
// half planes of the strip's x range, the altitude band and the across slab (ring_far_megastorm_geometry.gdshaderinc), then:
//   1. a coarse occupancy search (the column profile, no noise) finds where the ray first enters the body or the canopy,
//   2. bisection refines that entry,
//   3. a short geometric march shades the eroded column from the entry: density, sun, ambient and opacity mirror the Sunshine strip path
//      (RingStormProfileSample.glslinc: the collapsed column, its profile and erosion with the same moving-frame noise; ring_storm_sun_tau;
//      the storm ambient and the open-sky light under the overhang), weighted by the complement of the near march's handoff weight.
// The column is the shared texture-free include (ring_megastorm_column_terms) with its across noise from the 1D across table, which the C#
// twin builds (MegastormProfile.AcrossTerms). Night: the moving-frame flash field (ring_far_storm_lighting.gdshaderinc) lights the wall.
// Draw order: after the whole-ring shell (10-14), before the far layer sky (16), which ends its integration at megastorm_body_entry.
uniform sampler2D far_megastorm_depth : hint_depth_texture, filter_nearest;
// x / y near handoff start / end (physical m), z near ownership on, w Sunshine altitude crossfade (the far layer sky's values).
uniform vec4 near_handoff = vec4(0.0);
// x Sunshine cloud density multiplier, y sharpness, z reference step (m), w unused (the far layer sky's far_layers_march).
uniform vec4 far_layers_march = vec4(1.0, 0.746, 958.0, 0.0);
// Across noise table: (tower, mammatus, shaft) raw value noise over across 0 .. W (MegastormProfile.AcrossTerms).
uniform sampler2D far_megastorm_across : hint_default_black, filter_linear, repeat_disable;
// Sunshine's per-ray storm ambient (rgb, w = Storm darkness strength).
uniform vec4 far_megastorm_storm_ambient = vec4(0.0);
// Moving-frame erosion noise: Sunshine's medium and small noise, the same coordinates the strip path uses (around - travel mod scale).
uniform sampler3D far_megastorm_medium_noise : filter_linear_mipmap, repeat_enable;
uniform sampler3D far_megastorm_small_noise : filter_linear_mipmap, repeat_enable;
// x medium scale, y small scale, z medium offset (travel mod medium scale), w small offset (travel mod small scale).
uniform vec4 far_megastorm_noise = vec4(20000.0, 8500.0, 0.0, 0.0);
uniform vec3 far_megastorm_medium_position = vec3(0.0);
uniform vec3 far_megastorm_small_position = vec3(0.0);
// x camera around (m, wrapped: noise coordinates only), y slot time (s), z reduced flashes (0 / 1), w reduced peak scale.
uniform vec4 far_megastorm_flash = vec4(0.0, 0.0, 0.0, 0.4);
uniform int far_megastorm_flash_slot = 0;
// Look gains against the near march (handoff compare): x sun, y ambient, z flash, w opacity.
uniform vec4 far_megastorm_look = vec4(1.0, 1.0, 1.0, 1.0);
// Debug (RING_MEGASTORM_FAR_DEBUG; not in the menu): 1 = the clipped wedge interval in red, 2 = the coarse occupancy hit in green,
// 6 = altitude band hit, 9 = the shaded wall without haze, 10 = opacity in green, 11 = march statistics, 12 / 13 haze T / in-scatter.
uniform int far_megastorm_debug = 0;
global uniform mat4 ring_far_cloud_lights;
global uniform vec4 ring_far_cloud_ambient;
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_megastorm_profile.gdshaderinc"
#include "res://materials/ring_far_megastorm_geometry.gdshaderinc"
#include "res://materials/ring_far_storm_lighting.gdshaderinc"

varying vec3 world_position;

void vertex() {
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// RingStormProfileSample.glslinc constants.
const float FAR_MS_FADE = 1500.0;          // RING_STORM_FADE_METERS
const float FAR_MS_SUN_EXTINCTION = 3.0e-3; // RING_STORM_SUN_EXTINCTION
const float FAR_MS_SHADE_AMBIENT = 0.45;   // RING_MEGASTORM_SHADE_AMBIENT
const float FAR_MS_OPEN_AMBIENT = 1.8;     // RING_MEGASTORM_OPEN_AMBIENT
const float FAR_MS_UNDERSIDE_OPEN = 0.15;
const float FAR_MS_UNDER_OPEN = 0.6;
const float FAR_MS_OPEN_TAP = 1500.0;
// The strip extent (MegastormProfile.StripMinX / StripMaxX).
const float FAR_MS_X_MIN = -72000.0;
const float FAR_MS_X_MAX = 56000.0;

// The column at (x, across) in the strip sampler's convention (ring_megastorm_strip_fetch): each interval collapsed onto its base by its
// coverage x sculpt strength. heights = (body base, body top, canopy underside, canopy top), aux = (canopy cov, mammatus, tower, body cov).
void far_ms_column(float x, float across, RingMegastormParams p, out vec4 heights, out vec4 aux) {
	float W = max(p.b.x, 1.0);
	vec2 noise = textureLod(far_megastorm_across, vec2(clamp(across / W, 0.0, 1.0), 0.5), 0.0).xy;
	vec4 raw;
	ring_megastorm_column_terms(x, across, noise, p, raw, aux);
	float sculpt = clamp(p.c.z * 1.2, 0.0, 1.0);
	heights = vec4(raw.x, raw.x + max(raw.y - raw.x, 0.0) * clamp(1.25 * aux.w * sculpt, 0.0, 1.0),
		raw.z, raw.z + max(raw.w - raw.z, 0.0) * clamp(1.25 * aux.x * sculpt, 0.0, 1.0));
}

bool far_ms_contains(vec4 heights, float h) {
	return (h >= heights.x && h <= heights.y && heights.y > heights.x) || (h >= heights.z && h <= heights.w && heights.w > heights.z);
}

float far_ms_column_top(vec4 heights) {
	float top = -1e30;
	if (heights.y > heights.x) top = heights.y;
	if (heights.w > heights.z) top = max(top, heights.w);
	return top;
}

float far_ms_profile(float h, float base, float top) {
	return clamp(min(h - base, top - h) / FAR_MS_FADE, 0.0, 1.0);
}

// ring_storm_map_density + ring_storm_eroded for a strip column (erode depth one fade, the megastorm rule). Returns the density and
// writes whether the denser interval is the canopy.
float far_ms_density(vec4 heights, vec4 aux, float h, float medium_shape, float small_raw, out bool canopy) {
	float p0 = heights.y > heights.x ? far_ms_profile(h, heights.x, heights.y) : 0.0;
	float p1 = heights.w > heights.z ? far_ms_profile(h, heights.z, heights.w) : 0.0;
	canopy = p1 > p0;
	vec2 interval = canopy ? heights.zw : heights.xy;
	float horizontal_depth = canopy ? aux.x : aux.w;
	float profile = min(clamp(min(h - interval.x, interval.y - h) / FAR_MS_FADE, 0.0, 1.0), clamp(horizontal_depth / 0.6, 0.0, 1.0));
	float bottom = (h - interval.x) < (interval.y - h) ? 1.0 - smoothstep(0.0, 2.0 * FAR_MS_FADE, h - interval.x) : 0.0;
	float strength = mix(1.0, 0.25, bottom);
	float low_medium = strength * 0.90 * (1.0 - medium_shape);
	float shaped = clamp((profile - low_medium) / max(1.0 - low_medium, 0.001), 0.0, 1.0);
	float low_small = strength * 0.60 * small_raw;
	shaped = clamp((shaped - low_small) / max(1.0 - low_small, 0.001), 0.0, 1.0);
	return shaped * shaped * (3.0 - 2.0 * shaped);
}

// ring_storm_sun_tau with the analytic column: the column the sun ray crosses 2.5 km above the sample, overlap above the sample / dUp.
// sun_x / sun_c are the sun's x and across components, sun_up its up component at the sample.
float far_ms_sun_tau(float x, float across, float h, float sun_x, float sun_c, float sun_up, RingMegastormParams p) {
	if (sun_up < 0.06) return 0.0;
	float k = 2500.0 / sun_up;
	vec4 hts;
	vec4 unused_aux;
	far_ms_column(x + sun_x * k, across + sun_c * k, p, hts, unused_aux);
	float overlap = max(hts.y - max(hts.x, h), 0.0) + max(hts.w - max(hts.z, h), 0.0);
	return FAR_MS_SUN_EXTINCTION * overlap / sun_up;
}

// ring_megastorm_open_light: how much of the open sky band beyond the anvil's leading edge (towards -xi) a sample sees.
float far_ms_open_light(float x, float across, float h, vec4 heights, RingMegastormParams p) {
	if (!far_ms_contains(heights, h)) return FAR_MS_UNDER_OPEN;
	vec4 tap_heights;
	vec4 tap_aux;
	far_ms_column(x - FAR_MS_OPEN_TAP, across, p, tap_heights, tap_aux);
	if (far_ms_profile(h, heights.z, heights.w) > far_ms_profile(h, heights.x, heights.y))
		return mix(1.0, FAR_MS_UNDERSIDE_OPEN, clamp(tap_aux.x, 0.0, 1.0));
	return 1.0 - clamp(tap_aux.w, 0.0, 1.0);
}

// The pass for one pixel: (premultiplied colour, alpha), alpha < 0 when the pixel is empty.
vec4 far_ms_render(vec3 cam, vec3 dir, vec2 screen_uv, mat4 inv_projection) {
	float scale = max(ring_atmosphere_coordinate_scale, 0.000001);
	float R = ring_far_ms_radius();
	float cam_r = length(cam.xz);
	if (cam_r <= 0.0) return vec4(-1.0);
	vec2 u = cam.xz / cam_r;
	RingFarMsRay ray;
	ray.rc = cam_r / scale;
	ray.h = R - ray.rc;
	ray.dr = dot(dir.xz, u);
	ray.dt = dot(dir.xz, vec2(-u.y, u.x));
	ray.dy = dir.y;
	ray.a = dot(dir.xz, dir.xz);
	ray.across_c = cam.y / scale + 0.5 * far_megastorm_b.x;
	RingMegastormParams p = ring_far_ms_params();

	// Far end: the far terrain (reverse-Z depth). Near end: the near march's handoff start while the near owns the near range.
	float depth = textureLod(far_megastorm_depth, screen_uv, 0.0).r;
	float t_far = 1e30;
	if (depth > 0.0) {
		vec4 view = inv_projection * vec4(screen_uv * 2.0 - 1.0, depth, 1.0);
		t_far = length(view.xyz / view.w) / scale;
	}
	bool near_on = near_handoff.z > 0.5 && near_handoff.y > near_handoff.x;
	float t_near = near_on && near_handoff.w < 0.001 ? near_handoff.x : 0.0;

	// The wedge (strip x range, bowed by at most bow + meander) x the slab x the altitude band (ground for the rain under the base, to the
	// ceiling) x [t_near, t_far]: up to two pieces.
	float lead_pad = p.b.y + p.b.z;
	vec2 wedge = ring_far_ms_wedge(ray, FAR_MS_X_MIN - lead_pad, FAR_MS_X_MAX + lead_pad);
	vec2 slab = ring_far_ms_slab(ray);
	float lo = max(max(wedge.x, slab.x), t_near);
	float hi = min(min(wedge.y, slab.y), t_far);
	if (far_megastorm_debug == 1) return hi > lo ? vec4(0.25, 0.0, 0.0, 0.5) : vec4(0.0, 0.0, 0.15, 0.5);
	if (hi <= lo) return vec4(-1.0);
	float ceiling = min(18000.0, p.c.y);
	vec2 b0;
	vec2 b1;
	ring_far_ms_band(ray, 1.0, ceiling, b0, b1);
	vec2 s0 = vec2(max(lo, b0.x), min(hi, b0.y));
	vec2 s1 = vec2(max(lo, b1.x), min(hi, b1.y));
	float l0 = max(s0.y - s0.x, 0.0);
	float l1 = max(s1.y - s1.x, 0.0);
	if (far_megastorm_debug == 6) return l0 + l1 >= 1.0 ? vec4(0.0, 0.25, 0.0, 0.5) : vec4(0.25, 0.0, 0.25, 0.5);
	if (l0 + l1 < 1.0) return vec4(-1.0);
	if (l0 <= 0.0) { s0 = s1; l0 = l1; l1 = 0.0; }

	// 1-2. Coarse occupancy over both pieces, then bisection.
	const int COARSE = 20;
	float total = l0 + l1;
	float du = total / float(COARSE);
	float u_hit = -1.0;
	for (int i = 0; i < COARSE; i++) {
		float uu = (float(i) + 0.5) * du;
		float t = uu < l0 ? s0.x + uu : s1.x + (uu - l0);
		vec4 hts;
		vec4 aux;
		far_ms_column(ring_far_ms_x(ray, t), ring_far_ms_across(ray, t), p, hts, aux);
		float h = ring_far_ms_altitude(ray, t);
		bool rain = h < hts.x && hts.y > hts.x;
		if (far_ms_contains(hts, h) || rain) { u_hit = uu; break; }
	}
	if (far_megastorm_debug == 2) return u_hit >= 0.0 ? vec4(0.0, 0.25, 0.0, 0.5) : vec4(0.15, 0.0, 0.0, 0.5);
	if (u_hit < 0.0) return vec4(-1.0);
	float u_lo = max(u_hit - du, 0.0);
	float u_hi = u_hit;
	for (int i = 0; i < 5; i++) {
		float um = 0.5 * (u_lo + u_hi);
		float t = um < l0 ? s0.x + um : s1.x + (um - l0);
		vec4 hts;
		vec4 aux;
		far_ms_column(ring_far_ms_x(ray, t), ring_far_ms_across(ray, t), p, hts, aux);
		float h = ring_far_ms_altitude(ray, t);
		if (far_ms_contains(hts, h) || (h < hts.x && hts.y > hts.x)) u_hi = um; else u_lo = um;
	}

	// 3. Shading march from the entry: geometric steps from a fraction of the pixel footprint scale.
	float u_entry = u_lo;
	float t_entry = u_entry < l0 ? s0.x + u_entry : s1.x + (u_entry - l0);
	float step = clamp(0.004 * t_entry, 150.0, 2500.0);
	float density_mul = far_layers_march.x;
	float sharp = max(far_layers_march.y, 0.01);
	float step_ref = max(far_layers_march.z, 1.0);
	float noise_lowpass = smoothstep(4000.0, 12000.0, step);
	vec3 ambient_cam = ring_far_cloud_ambient.rgb;
	vec3 up_cam = vec3(-u.x, 0.0, -u.y);
	float storm_w = clamp(far_megastorm_storm_ambient.w, 0.0, 1.0);
	float cut_w = ring_far_storm_cut_weight(t_entry, near_handoff.y);
	float dirx = far_megastorm_frame.y;
	vec3 col_sum = vec3(0.0);
	float trans = 1.0;
	float depth_num = 0.0;
	float depth_den = 0.0;
	float uu = u_entry;
	float dbg_in = 0.0;
	float dbg_d = 0.0;
	float dbg_a = 0.0;
	for (int i = 0; i < 18; i++) {
		if (uu >= total || trans < 0.01) break;
		float t = uu < l0 ? s0.x + uu : s1.x + (uu - l0);
		float dt_step = step;
		uu += step;
		step *= 1.22;
		float owner = near_on ? mix(smoothstep(near_handoff.x, near_handoff.y, t), 1.0, clamp(near_handoff.w, 0.0, 1.0)) : 1.0;
		if (owner <= 0.0) continue;
		float x = ring_far_ms_x(ray, t);
		float across = ring_far_ms_across(ray, t);
		float h = ring_far_ms_altitude(ray, t);
		vec4 hts;
		vec4 aux;
		far_ms_column(x, across, p, hts, aux);
		float d = 0.0;
		bool canopy = false;
		bool in_column = far_ms_contains(hts, h);
		if (in_column) {
			// Noise coordinates: the strip path's moving-frame erosion (canonical around - travel, altitude, across).
			float around = far_megastorm_flash.x + R * atan(t * ray.dt, ray.rc + t * ray.dr);
			vec3 cpos = vec3(around, h, across);
			vec3 mc = cpos - far_megastorm_medium_position;
			mc.x -= far_megastorm_noise.z;
			vec3 sc = cpos - far_megastorm_small_position;
			sc.x -= far_megastorm_noise.w;
			float medium_shape = 1.0 - texture(far_megastorm_medium_noise, mc / max(far_megastorm_noise.x, 1.0)).b;
			float small_raw = mix(texture(far_megastorm_small_noise, sc / max(far_megastorm_noise.y, 1.0)).r, 0.5, noise_lowpass);
			d = far_ms_density(hts, aux, h, medium_shape, small_raw, canopy);
		} else if (h < hts.x && hts.y > hts.x) {
			// Rain under the shelf and the wall: a thin dark medium to the ground (the rain shaft profile).
			float shaft = textureLod(far_megastorm_across, vec2(clamp(across / max(p.b.x, 1.0), 0.0, 1.0), 0.5), 0.0).z;
			d = 0.12 * aux.w * (0.6 + 0.4 * shaft);
		}
		dbg_in += in_column ? 1.0 / 18.0 : 0.0;
		dbg_d = max(dbg_d, d);
		if (d <= 0.0) continue;
		float a = clamp(pow(clamp(d * density_mul, 0.0, 4.0), sharp) * dt_step / step_ref * far_megastorm_look.w, 0.0, 1.0) * owner;
		dbg_a = max(dbg_a, a);
		if (a <= 0.0) continue;

		// Local frame at the sample: up (toward the axis), the around tangent, across (+Y).
		vec2 radial = (ray.rc + t * ray.dr) * u + (t * ray.dt) * vec2(-u.y, u.x);
		vec2 n = normalize(radial);
		vec3 up = vec3(-n.x, 0.0, -n.y);
		vec3 tangent = vec3(-n.y, 0.0, n.x);
		float top = far_ms_column_top(hts);
		// Below the column's top fade nothing sees the open sky dome: the storm ambient in full with the open-sky factor; the top layer
		// keeps the plain storm ambient near the handoff and the sky ambient across the arch.
		bool buried = in_column && top - h > FAR_MS_FADE;
		float open = buried ? far_ms_open_light(x, across, h, hts, p) : 1.0;
		float storm_mix = buried ? storm_w : storm_w * cut_w;
		vec3 ambient = mix(ambient_cam, far_megastorm_storm_ambient.rgb * mix(FAR_MS_SHADE_AMBIENT, FAR_MS_OPEN_AMBIENT, open), storm_mix)
			* ring_far_remote_day_scale(ring_far_cloud_lights, up, up_cam) * far_megastorm_look.y;
		vec3 sun = vec3(0.0);
		for (int l = 0; l < 2; l++) {
			vec4 ld = ring_far_cloud_lights[l * 2];
			vec3 lr = ring_far_cloud_lights[l * 2 + 1].rgb;
			if (ld.w < 0.5 || dot(lr, vec3(1.0)) <= 0.0) continue;
			vec3 L = normalize(ld.xyz);
			float light_up = ring_far_light_up(L, up);
			if (light_up <= 0.0) continue;
			float sun_up = dot(L, up);
			float tau = in_column ? far_ms_sun_tau(x, across, h, -dirx * dot(L, tangent), L.y, sun_up, p) : 0.0;
			float weight = light_up * exp(-tau) * (1.0 - 0.85 * storm_w * cut_w);
			sun += lr * pow(weight, 2.2) * ring_far_storm_phase(dot(dir, L), clamp(ring_far_cloud_lights[1].w, 0.0, 0.9));
		}
		vec3 colour = sun * far_megastorm_look.x + ambient;
		col_sum += trans * a * colour;
		depth_num += trans * a * t;
		depth_den += trans * a;
		trans *= 1.0 - a;
	}
	float alpha = 1.0 - trans;
	if (far_megastorm_debug == 11) return vec4(dbg_in, dbg_d, dbg_a, 1.0);
	if (alpha < 0.002) return vec4(-1.0);
	float t_mean = depth_num / max(depth_den, 1e-6);

	// Night: the moving-frame flash field lights the wall around the opacity-weighted depth.
	float x_m = ring_far_ms_x(ray, t_mean);
	float glow = ring_ms_flash_glow(x_m, ring_far_ms_across(ray, t_mean), ring_far_ms_altitude(ray, t_mean), far_megastorm_flash_slot,
		far_megastorm_flash.y, far_megastorm_flash.z > 0.5, far_megastorm_flash.w, p);
	col_sum += alpha * glow * far_megastorm_look.z * vec3(0.78, 0.84, 1.0);

	if (far_megastorm_debug == 9) return vec4(col_sum / max(alpha, 1e-4), 1.0);
	if (far_megastorm_debug == 10) return vec4(vec3(0.0, alpha * 0.6, 0.0), 0.6);
	vec3 target = cam + dir * (t_mean * scale);
	float day = ringworld_effective_solar_day(smoothstep(-0.2, 0.35, dot(up_cam, normalize(ring_atmosphere_to_sun))));
	RingHaze haze = ring_haze_sample(cam, target, vec3(0.0), day);
	if (far_megastorm_debug == 12) return vec4(haze.T, 1.0);
	if (far_megastorm_debug == 13) return vec4(ring_haze_inscatter(haze), 1.0);
	return vec4(col_sum * haze.T + ring_haze_inscatter(haze) * alpha, alpha);
}

void fragment() {
	if (!far_megastorm_enabled) discard;
	vec3 cam = CAMERA_POSITION_WORLD;
	vec4 result = far_ms_render(cam, normalize(world_position - cam), SCREEN_UV, INV_PROJECTION_MATRIX);
	if (result.a < 0.0) discard;
	ALBEDO = result.rgb;
	ALPHA = result.a;
}
