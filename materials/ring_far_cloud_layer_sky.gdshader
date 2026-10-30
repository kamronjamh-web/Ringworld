shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, depth_test_disabled, cull_front, shadows_disabled, fog_disabled;
// S6 WO6 far cloud layers (docs/storm-revamp-s6-cloud-layers-design.md section 3.5): the low / mid volumetric layers and the cirrus shell
// beyond the Sunshine handoff, drawn in the scaled far viewport by a camera-centred sphere (RingScaledFarViewport.CloudLayers,
// FarCloudLayerSky). Each pixel integrates its view ray analytically over the curved layer hull from the handoff start to the far terrain
// (depth texture) or the whole-ring shell's crossfade, weighted by the complement of the near march's handoff weight, so the near and
// far owners split every ray by distance (no seam, no double cloud). Density is the near layer density (ring_far_cloud_layers.gdshaderinc,
// same map texels, table, gradient and noise); the cirrus shell is the near's own include, composited at its crossing by ray order.
// Megastorm (docs/megastorm-design.md 3.3, 3.4; WO5; one uniform branch, far_megastorm_enabled): the layers gain the trailing / precursor
// coverage analytically (the atlases exclude the megastorm), beyond the disk fade a short march draws that coverage alone (the whole-ring
// shell has no megastorm), and the integration ends at the wall body's entry (megastorm_body_entry), so the layers in front of the wall
// composite over the wedge pass (drawn before this one) and nothing behind it is drawn.
uniform sampler2D far_layers_depth : hint_depth_texture, filter_nearest;
uniform float terrain_cloud_receiver_height_meters = 3000.0;
#include "res://materials/ring_terrain_cloud_sun_receiver.gdshaderinc"
uniform float ring_circumference_meters = 31415926.0;
uniform float ring_width_meters = 318000.0;
// x / y near handoff start / end (physical m, the Sunshine cloud_handoff), z near ownership on (> 0.5), w Sunshine altitude crossfade.
uniform vec4 near_handoff = vec4(0.0);
// The whole-ring shell's crossfade (physical m): the sky owns rays up to x and hands over to the shell by y.
uniform vec2 disk_fade = vec2(191000.0, 245000.0);
uniform sampler2D weather_atlas : hint_default_white, filter_linear, repeat_enable;
uniform bool weather_atlas_enabled = false;
uniform float weather_time_seconds = 0.0;
uniform vec2 weather_wind_uv_per_second = vec2(0.0);
// Look tuning against the near-only reference (cloud_layers_capture handoff shots).
uniform float far_layers_opacity_gain = 1.0;
uniform float far_layers_sun_gain = 1.0;
uniform float far_layers_ambient_gain = 1.0;
// Deck colour (S6 3.8, mirrors the near RING_LAYER_DECK_COLOUR): 1 moves an overcast deck's ambient chroma toward the sun + sky irradiance, 0 = off.
uniform float far_layers_deck_colour = 1.0;
global uniform mat4 ring_far_cloud_lights;
global uniform vec4 ring_far_cloud_ambient;
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_far_cloud_density.gdshaderinc"
#include "res://materials/ring_cloud_weather_shared.gdshaderinc"
#include "res://materials/ring_far_cloud_layers.gdshaderinc"
#include "res://materials/ring_megastorm_profile.gdshaderinc"
#include "res://materials/ring_far_megastorm_geometry.gdshaderinc"
#define RING_CIRRUS_TEX2D(uv, lod) textureLod(far_density_extra_large_noise_patterns, (uv), (lod)).a
#define RING_CIRRUS_TEX2D_SIZE float(textureSize(far_density_extra_large_noise_patterns, 0).x)
#include "res://materials/ring_cirrus_shell.gdshaderinc"

varying vec3 world_position;

void vertex() {
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// Ray geometry about the ring axis (far-world origin, axis +Y), in physical metres. rc: camera radius, h: camera altitude,
// dr / dt: the ray's radial-outward / tangential (increasing angle) horizontal components.
struct FarRay { float rc; float h; float dr; float dt; float dy; float a; };

float far_ray_altitude(FarRay r, float t) {
	float x = r.rc + t * r.dr;
	float y = t * r.dt;
	return r.h - (2.0 * r.rc * t * r.dr + t * t * r.a) / (sqrt(x * x + y * y) + r.rc);
}

// Both crossings of altitude H (ascending order; -1 when missing).
vec2 far_ray_crossings(FarRay r, float H) {
	float R = ring_radius_meters;
	float b = r.rc * r.dr;
	float c = (H - r.h) * (2.0 * R - r.h - H);
	float disc = b * b - r.a * c;
	if (r.a < 1e-12 || disc < 0.0) return vec2(-1.0);
	float s = sqrt(disc);
	float q = -(b + (b < 0.0 ? -s : s));
	if (abs(q) < 1e-9) return vec2(-1.0);
	float r0 = q / r.a;
	float r1 = c / q;
	return vec2(min(r0, r1), max(r0, r1));
}

// Unit up (toward the axis) at ray distance t, in far-world axes.
vec3 far_ray_up(FarRay r, vec2 u, float t) {
	vec2 radial = (r.rc + t * r.dr) * u + (t * r.dt) * vec2(-u.y, u.x);
	vec2 n = normalize(radial);
	return vec3(-n.x, 0.0, -n.y);
}

// The complement of the near march's handoff weight (ring_cloud_handoff_near_weight), opened fully by the Sunshine altitude crossfade.
float far_near_complement(float t) {
	return near_handoff.z > 0.5 && near_handoff.y > near_handoff.x
		? mix(smoothstep(near_handoff.x, near_handoff.y, t), 1.0, clamp(near_handoff.w, 0.0, 1.0)) : 1.0;
}

// Volumetric layers: the near complement, faded out into the whole-ring shell (which draws the low / mid body beyond the disk).
float far_owner_weight(float t) {
	return far_near_complement(t) * (1.0 - smoothstep(disk_fade.x, max(disk_fade.y, disk_fade.x + 1.0), t));
}

void fragment() {
	if (!far_layers_enabled) discard;
	float scale = max(ring_atmosphere_coordinate_scale, 0.000001);
	float R = ring_radius_meters;
	float C = max(ring_circumference_meters, 1.0);
	float W = max(ring_width_meters, 1.0);
	vec3 cam = CAMERA_POSITION_WORLD;
	vec3 dir = normalize(world_position - cam);
	// Pixel angle for the cirrus footprint, taken in uniform control flow.
	float pixel_angle = max(length(dFdx(dir)), length(dFdy(dir)));
	float cam_r = length(cam.xz);
	if (cam_r <= 0.0) discard;
	vec2 u = cam.xz / cam_r;
	FarRay ray;
	ray.rc = cam_r / scale;
	ray.h = R - ray.rc;
	ray.dr = dot(dir.xz, u);
	ray.dt = dot(dir.xz, vec2(-u.y, u.x));
	ray.dy = dir.y;
	ray.a = dot(dir.xz, dir.xz);
	float around_c = atan(u.y, u.x) * R;
	if (around_c < 0.0) around_c += C;
	float across_c = cam.y / scale + 0.5 * W;

	// Far end: the far terrain (reverse-Z depth) or the shell crossfade end.
	float depth = textureLod(far_layers_depth, SCREEN_UV, 0.0).r;
	float t_terrain = 1e30;
	if (depth > 0.0) {
		vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
		t_terrain = length(view.xyz / view.w) / scale;
	}
	RingFarMsRay msray;
	// The megastorm geometry takes the ring radius in physical metres (ring_far_ms_radius: this material's ring_radius_meters is in scaled
	// units), so its altitudes and around offsets are exact whatever R the layer march above uses.
	msray.rc = ray.rc; msray.h = ring_far_ms_radius() - ray.rc; msray.dr = ray.dr; msray.dt = ray.dt; msray.dy = ray.dy; msray.a = ray.a;
	msray.across_c = across_c;
	if (far_megastorm_enabled) t_terrain = min(t_terrain, megastorm_body_entry(msray, 0.0, min(t_terrain, 2.0e7)));
	float t_end = min(t_terrain, disk_fade.y);
	bool near_owned = near_handoff.z > 0.5 && near_handoff.y > near_handoff.x && near_handoff.w < 0.001;
	float t_start = near_owned ? near_handoff.x : 0.0;
	if (min(t_terrain, 2.0e6) <= t_start) discard;

	vec2 shift = cloud_weather_frame.yz;
	vec2 wind = vec2(cos(far_ws_header.w), sin(far_ws_header.w));
	// The layer hull: the lowest base to the highest volumetric top cap.
	float hull_lo = min(far_cloud_layers[5].y, far_cloud_layers[5].x);
	float hull_hi = min(max(far_cloud_layers[5].z, far_cloud_layers[9].z), far_cloud_layers[0].x);

	// Break points of the hull along [t_start, t_end]; the march covers the first to the last in-hull piece.
	float pts[6];
	pts[0] = t_start; pts[1] = t_end;
	vec2 c_lo = far_ray_crossings(ray, hull_lo);
	vec2 c_hi = far_ray_crossings(ray, hull_hi);
	pts[2] = clamp(c_lo.x, t_start, t_end); pts[3] = clamp(c_lo.y, t_start, t_end);
	pts[4] = clamp(c_hi.x, t_start, t_end); pts[5] = clamp(c_hi.y, t_start, t_end);
	for (int i = 1; i < 6; i++) {
		float v = pts[i];
		int j = i - 1;
		while (j >= 0 && pts[j] > v) { pts[j + 1] = pts[j]; j--; }
		pts[j + 1] = v;
	}
	float ta = 1e30;
	float tb = -1.0;
	for (int i = 0; i < 5; i++) {
		if (pts[i + 1] - pts[i] < 1.0) continue;
		float alt = far_ray_altitude(ray, 0.5 * (pts[i] + pts[i + 1]));
		if (alt >= hull_lo && alt <= hull_hi) { ta = min(ta, pts[i]); tb = max(tb, pts[i + 1]); }
	}

	// Cirrus shell crossing (first ahead). The whole-ring shell has no cirrus, so beyond the disk this pass keeps the shell to the far
	// terrain (the opposite arc rises above the horizon; no curvature limit is needed).
	float t_ci = -1.0;
	if (far_cloud_layers[16].w > 0.5 && far_layers_march.w < 0.5) {
		vec2 c_ci = far_ray_crossings(ray, far_cloud_layers[13].x);
		t_ci = c_ci.x > 0.0 ? c_ci.x : c_ci.y;
		if (t_ci < t_start || t_ci > t_terrain || far_near_complement(t_ci) <= 0.0) t_ci = -1.0;
	}
	if (t_end <= t_start) { ta = 1e30; tb = -1.0; }

	// Megastorm beyond the disk fade: its T / P coverage alone (the natural layers there are the whole-ring shell's), fading in as the
	// volumetric layers fade out. The wedge of the coverage table's xi range, bowed by at most bow + meander.
	vec2 ms0 = vec2(1e30, -1.0);
	vec2 ms1 = vec2(1e30, -1.0);
	float ms_l0 = 0.0;
	float ms_l1 = 0.0;
	if (far_megastorm_enabled) {
		vec2 w = ring_far_ms_wedge(msray, far_megastorm_frame.z - far_megastorm_b.y - far_megastorm_b.z, far_megastorm_frame.w + far_megastorm_b.z);
		vec2 sl = ring_far_ms_slab(msray);
		float lo = max(max(w.x, sl.x), max(disk_fade.x, t_start));
		float hi = min(min(w.y, sl.y), t_terrain);
		if (hi > lo) {
			vec2 b0;
			vec2 b1;
			ring_far_ms_band(msray, hull_lo, hull_hi, b0, b1);
			ms0 = vec2(max(lo, b0.x), min(hi, b0.y));
			ms1 = vec2(max(lo, b1.x), min(hi, b1.y));
			ms_l0 = max(ms0.y - ms0.x, 0.0);
			ms_l1 = max(ms1.y - ms1.x, 0.0);
			if (ms_l0 <= 0.0) { ms0 = ms1; ms_l0 = ms_l1; ms_l1 = 0.0; }
		}
	}
	int ms_n = ms_l0 + ms_l1 >= 100.0 ? 8 : 0;
	float ms_dt = (ms_l0 + ms_l1) / 8.0;
	if (tb <= ta && t_ci < 0.0 && ms_n == 0) discard;

	float step_ref = max(far_layers_march.z, 1.0);
	float density_mul = far_layers_march.x;
	float sharp = max(far_layers_march.y, 0.01);
	vec3 ambient_base = ring_far_cloud_ambient.rgb;
	float g = clamp(ring_far_cloud_lights[1].w, 0.0, 0.9);
	vec3 col_sum = vec3(0.0);
	float trans = 1.0;
	float depth_num = 0.0;
	float depth_den = 0.0;
	bool ci_done = t_ci < 0.0;
	float terrain_sun = -1.0;
	// Deck colour sums over the volumetric samples (trans * a weights): the ambient term, its sun + sky source and the overhead cloud fraction.
	vec3 amb_sum = vec3(0.0);
	vec3 src_sum = vec3(0.0);
	float over_sum = 0.0;
	float vol_w = 0.0;

	int n = 0;
	float dt = 0.0;
	if (tb > ta) {
		n = int(clamp(ceil((tb - ta) / 4000.0), 3.0, 16.0));
		dt = (tb - ta) / float(n);
	}
	float lowpass = smoothstep(2000.0, 5000.0, dt);
	for (int i = 0; i <= 24; i++) {
		bool vol = i < n + ms_n;
		bool ms_only = i >= n;
		float sample_dt = ms_only ? ms_dt : dt;
		float t = 1e30;
		if (vol) {
			if (!ms_only) t = ta + (float(i) + 0.5) * dt;
			else {
				float uu = (float(i - n) + 0.5) * ms_dt;
				t = uu < ms_l0 ? ms0.x + uu : ms1.x + (uu - ms_l0);
			}
		}
		// The cirrus shell composites at its ray position, in front of the volumetric samples behind it.
		if (!ci_done && t_ci <= t) {
			ci_done = true;
			float around = around_c + R * atan(t_ci * ray.dt, ray.rc + t_ci * ray.dr);
			float across = across_c + t_ci * ray.dy;
			around -= C * floor(around / C);
			vec2 xz = vec2(around, across);
			vec2 atlas_uv = vec2(fract(around / C - weather_wind_uv_per_second.x * weather_time_seconds),
				clamp(across / W - weather_wind_uv_per_second.y * weather_time_seconds, 0.0, 1.0));
			float atlas_storm = weather_atlas_enabled ? textureLod(weather_atlas, atlas_uv, 0.0).g : 0.0;
			vec4 layers; vec4 storm;
			vec4 ms_cov = vec4(0.0);
			if (far_megastorm_enabled)
				ms_cov = ring_far_ms_coverage(ring_far_ms_x(msray, t_ci) - ring_megastorm_lead_offset(across, ring_far_ms_params()));
			far_layers_column(xz, atlas_uv, atlas_storm, shift, C, far_megastorm_enabled, ms_cov, layers, storm);
			vec4 high = far_layers_resolve(2, layers);
			if (high.x > 0.0) {
				vec3 up = far_ray_up(ray, u, t_ci);
				float mu = dot(dir, up);
				float footprint = t_ci * pixel_angle / max(abs(mu), 0.05);
				vec2 shell_xz = xz + far_cloud_layers[39].xz - far_density_medium_position.xz;
				float dens = ring_cirrus_density(shell_xz, high.x, high.w, wind, footprint);
				float alpha = ring_cirrus_alpha(dens, max(high.z - high.y, 1.0), far_cloud_layers[15].w, mu) * far_near_complement(t_ci);
				if (alpha > 0.0) {
					vec3 colour = ambient_base * far_cloud_layers[16].y;
					for (int l = 0; l < 2; l++) {
						vec4 ld = ring_far_cloud_lights[l * 2];
						vec3 lr = ring_far_cloud_lights[l * 2 + 1].rgb;
						if (ld.w < 0.5 || dot(lr, vec3(1.0)) <= 0.0) continue;
						vec3 L = normalize(ld.xyz);
						float sun_up = smoothstep(-0.05, 0.12, dot(L, up));
						colour += lr * pow(sun_up, 2.2)
							* ring_cirrus_phase(dot(L, dir), far_cloud_layers[16].x, high.w, high.x) * RING_CIRRUS_SUN_SCALE;
					}
					col_sum += trans * alpha * colour;
					depth_num += trans * alpha * t_ci;
					depth_den += trans * alpha;
					trans *= 1.0 - alpha;
				}
			}
		}
		if (!vol) { if (ci_done) break; else continue; }
		if (trans < 0.01) break;
		float alt = ms_only ? ring_far_ms_altitude(msray, t) : far_ray_altitude(ray, t);
		if (alt < hull_lo || alt > hull_hi) continue;
		float owner = ms_only ? far_near_complement(t) * smoothstep(disk_fade.x, max(disk_fade.y, disk_fade.x + 1.0), t) : far_owner_weight(t);
		if (owner <= 0.0) continue;
		float around = (ms_only ? atan(u.y, u.x) * ring_far_ms_radius() + ring_far_ms_radius() * atan(t * ray.dt, ray.rc + t * ray.dr)
			: around_c + R * atan(t * ray.dt, ray.rc + t * ray.dr));
		float across = across_c + t * ray.dy;
		around -= C * floor(around / C);
		vec2 xz = vec2(around, across);
		vec2 atlas_uv = vec2(fract(around / C - weather_wind_uv_per_second.x * weather_time_seconds),
			clamp(across / W - weather_wind_uv_per_second.y * weather_time_seconds, 0.0, 1.0));
		float atlas_storm = weather_atlas_enabled ? textureLod(weather_atlas, atlas_uv, 0.0).g : 0.0;
		vec4 layers; vec4 storm;
		vec4 ms_cov = vec4(0.0);
		if (far_megastorm_enabled)
			ms_cov = ring_far_ms_coverage(ring_far_ms_x(msray, t) - ring_megastorm_lead_offset(across, ring_far_ms_params()));
		if (ms_only) {
			layers = ms_cov;
			storm = vec4(0.0);
		} else {
			far_layers_column(xz, atlas_uv, atlas_storm, shift, C, far_megastorm_enabled, ms_cov, layers, storm);
		}
		vec4 low = far_layers_resolve(0, layers);
		vec4 mid = far_layers_resolve(1, layers);
		vec3 p = vec3(around, alt, across);
		float d_low = 0.0;
		float d_mid = 0.0;
		float closure_low = far_layers_closure(low, layers.w, storm.y, storm.x);
		float closure_mid = far_layers_closure(mid, layers.w, storm.y, storm.x);
		if (alt >= low.y && alt <= low.z)
			d_low = far_layer_density(0, low, p, storm.z, closure_low, ms_only ? 1.0 : lowpass);
		if (alt >= mid.y && alt <= mid.z)
			d_mid = far_layer_density(1, mid, p, storm.z, closure_mid, ms_only ? 1.0 : lowpass);
		float d = max(d_low, d_mid);
		if (d <= 0.0) continue;
		float a = clamp(pow(clamp(d * density_mul, 0.0, 4.0), sharp) * sample_dt / step_ref * far_layers_opacity_gain, 0.0, 1.0) * owner;
		if (a <= 0.0) continue;
		// Lighting: the layer containing the denser sample.
		bool in_low = d_low >= d_mid;
		vec4 own = in_low ? low : mid;
		float height = clamp((alt - own.y) / max(own.z - own.y, 1.0), 0.0, 1.0);
		float suppress = (1.0 - storm.y) * (1.0 - smoothstep(0.35, 0.80, storm.x));
		float deck = smoothstep(0.45, 0.85, own.w) * suppress;
		float ao = mix(mix(0.55, 1.0, height), far_layers_column_light(alt, low, mid), deck);
		float storm_dark = smoothstep(0.35, 0.80, storm.x);
		vec3 up = far_ray_up(ray, u, t);
		if (terrain_sun < 0.0) terrain_sun = ring_terrain_cloud_receiver_visibility(vec3(around, terrain_cloud_receiver_height_meters, across));
		vec3 sun = vec3(0.0);
		vec3 deck_src = ambient_base;
		for (int l = 0; l < 2; l++) {
			vec4 ld = ring_far_cloud_lights[l * 2];
			vec3 lr = ring_far_cloud_lights[l * 2 + 1].rgb;
			if (ld.w < 0.5 || dot(lr, vec3(1.0)) <= 0.0) continue;
			vec3 L = normalize(ld.xyz);
			float mu_s = dot(L, up);
			float light_up = smoothstep(-0.05, 0.12, mu_s);
			if (light_up <= 0.0) continue;
			deck_src += lr * light_up * max(mu_s, 0.0);
			float tsun = far_layers_sun_transmittance(alt, mu_s, low, mid, layers.z);
			float cos_theta = dot(dir, L);
			float hg = (1.0 - g * g) / pow(max(1.0 + g * g - 2.0 * g * cos_theta, 1e-4), 1.5);
			sun += lr * light_up * tsun * mix(1.0, hg, 0.5);
		}
		vec3 amb_term = ambient_base * far_cloud_layers[8 + (in_low ? 0 : 4)].y * ao * far_layers_ambient_gain * mix(1.0, 0.4, storm_dark);
		vec3 colour = sun * terrain_sun * far_layers_sun_gain * (1.0 - 0.75 * storm_dark) + amb_term;
		// Overhead cloud fraction: the closure (it already carries the storm suppression) x coverage of the low / mid layer at or above the sample.
		float overhead = 0.0;
		if (alt <= low.z && low.z > low.y + 1.0) overhead = max(overhead, closure_low * clamp(low.x, 0.0, 1.0));
		if (alt <= mid.z && mid.z > mid.y + 1.0) overhead = max(overhead, closure_mid * clamp(mid.x, 0.0, 1.0));
		amb_sum += trans * a * amb_term;
		src_sum += trans * a * deck_src;
		over_sum += trans * a * overhead;
		vol_w += trans * a;
		col_sum += trans * a * colour;
		depth_num += trans * a * t;
		depth_den += trans * a;
		trans *= 1.0 - a;
	}
	// Deck colour: the ray's ambient chroma moves toward the sun + sky source by its mean overhead fraction, brightness kept.
	if (far_layers_deck_colour > 0.0 && vol_w > 1e-6) {
		vec3 amb = amb_sum / vol_w;
		vec3 src = src_sum / vol_w;
		const vec3 luma_w = vec3(0.2126, 0.7152, 0.0722);
		float f = clamp(over_sum / vol_w * far_layers_deck_colour, 0.0, 1.0);
		col_sum += vol_w * (mix(amb, src * (dot(amb, luma_w) / max(dot(src, luma_w), 1e-6)), f) - amb);
	}
	float alpha = 1.0 - trans;
	if (alpha < 0.002) discard;
	float t_mean = depth_num / max(depth_den, 1e-6);
	vec3 target = cam + dir * (t_mean * scale);
	float day = ringworld_effective_solar_day(smoothstep(-0.2, 0.35, dot(vec3(-u.x, 0.0, -u.y), normalize(ring_atmosphere_to_sun))));
	RingHaze haze = ring_haze_sample(cam, target, vec3(0.0), day);
	ALBEDO = col_sum * haze.T + ring_haze_inscatter(haze) * alpha;
	ALPHA = alpha;
}
