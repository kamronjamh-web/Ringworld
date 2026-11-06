shader_type spatial;
// Containment field ripple patches (docs/containment-field-design.md 3.5, 3.10.2; WO4). One small patch per live ripple of the disturbance bus, drawn in the
// transparent pass while the camera is BELOW the field (Sunshine clouds in front of the field composite afterwards). Same glass, seams and lighting as the near
// patch (ring_containment_field.gdshader), but only where a ripple is: the refraction ring wave, the energised seams, the flash-lit reveal ring and, for field
// strikes and megastorm contacts, the crawl and the contact glow (cf_ripple_eval). From any distance up to the far fade at 0.9-0.98 x the camera far plane.
// Where the near patch is drawing ripples itself (cf_near_active, instance occluder_b.w = 1) the part inside the shimmer range is left to it, so nothing is drawn twice.
//
// Mesh (ContainmentRipplePatches.BuildPatch = ContainmentFieldNearPatch.BuildGrid): VERTEX anchor-relative in world orientation, NORMAL = field normal (into the
// contained volume), TANGENT = around, UV = noise domain (m) relative to the ripple centre, UV2.x = arch length from the nearer root (m).
// Premultiplied: out = ALBEDO + dst * (1 - ALPHA), ALBEDO = shade * a + light, ALPHA = a.
render_mode unshaded, cull_disabled, depth_draw_never, blend_premul_alpha, shadows_disabled, fog_disabled;

uniform sampler2D cf_screen : hint_screen_texture, filter_linear, repeat_disable;
uniform sampler2D cf_depth : hint_depth_texture, filter_nearest, repeat_disable;

// Main-viewport only: ray frame comes from global shader parameters.
#define RING_ATMOSPHERE_FRAME_GLOBAL
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_containment_strike.gdshaderinc"
#include "res://materials/ring_containment_field_core.gdshaderinc"

uniform float cf_ripple_strength = 1.0;    // menu ContainmentRippleStrength: only a guard here (the CPU folds it into the wave and the strike level)
uniform float cf_glass_strength = 1.0;     // menu ContainmentSheenStrength
uniform float cf_lattice_strength = 1.0;   // menu ContainmentLatticeStrength
uniform float cf_range_m = 3000.0;         // the near shimmer range N
uniform float cf_near_active = 0.0;        // 1 when the near patch draws ripples this frame
uniform vec2 cf_far_fade = vec2(1.0e9, 1.1e9);   // (start, end) metres, ContainmentRipplePatches.FarFade(camera far)
uniform float cf_ripple_max_px = 6.0;      // CF_RIPPLE_MAX_PX at 1080 lines
uniform vec3 cf_ring_center_world = vec3(0.0);
uniform int cf_debug_mode = 0;
uniform vec3 cf_to_sun = vec3(0.0, 1.0, 0.0);
uniform vec3 cf_up = vec3(0.0, 1.0, 0.0);
uniform vec3 cf_sun_irradiance = vec3(0.0);
uniform vec3 cf_sky_radiance = vec3(0.0);
uniform vec3 cf_ground_radiance = vec3(0.0);
uniform float cf_seam_flow_phase = 0.0;
uniform float cf_seam_energy_scale = 1.0;

instance uniform vec4 cf_ripple_wave = vec4(0.0);        // state.Wave
instance uniform vec4 cf_ripple_crawl = vec4(0.0);       // state.Crawl
instance uniform vec4 cf_ripple_frame = vec4(0.0, 0.0, 1.0, 0.0);      // (lattice origin xy, metric k, strike level)
instance uniform vec4 cf_ripple_occluder_a = vec4(0.0);  // (top point world xyz, footprint radius m; 0 = none)
instance uniform vec4 cf_ripple_occluder_b = vec4(0.0);  // (local up world xyz, near cut flag 0/1)

varying vec3 cf_tangent_view;
varying vec3 cf_normal_view;
varying float cf_haze_visibility;

void vertex() {
	cf_tangent_view = (MODELVIEW_MATRIX * vec4(TANGENT, 0.0)).xyz;
	cf_normal_view = (MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;
	cf_haze_visibility = 1.0;
	if (ring_atmosphere_ray_frame_valid) {
		vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		cf_haze_visibility = ring_haze_visibility(ring_haze_sample(ring_atmosphere_camera_world_position, world, cf_ring_center_world, 1.0));
	}
}

float cf_view_depth_at(vec2 uv, mat4 inv_projection) {
	float raw = textureLod(cf_depth, uv, 0.0).r;
	if (raw <= 0.0) return 1.0e30;   // reversed-Z: 0 is the far plane / sky
	vec4 v = inv_projection * vec4(uv * 2.0 - 1.0, raw, 1.0);
	return v.w != 0.0 ? -v.z / v.w : 1.0e30;
}

void fragment() {
	if (cf_ripple_strength <= 0.0) discard;
	float dist = length(VERTEX);
	vec3 to_camera = -VERTEX / max(dist, 0.0001);
	vec3 n = normalize(cf_normal_view);
	vec3 ta = normalize(cf_tangent_view);
	vec3 tb = normalize(cross(ta, n));   // the increasing-arch tangent
	float ndv = abs(dot(n, to_camera));
	float frag_depth = -VERTEX.z;
	float fades = cf_grazing_fade(ndv) * cf_root_ramp(UV2.x);
	fades *= cf_soft_intersection(cf_view_depth_at(SCREEN_UV, INV_PROJECTION_MATRIX) - frag_depth);
	fades *= 1.0 - smoothstep(cf_far_fade.x, cf_far_fade.y, dist);
	if (cf_ripple_occluder_b.w > 0.5 && cf_near_active > 0.5) fades *= 1.0 - cf_distance_weight(dist, cf_range_m);   // the near patch draws that part
	if (cf_ripple_occluder_a.w > 0.0) {
		// Own-storm occlusion (3.5): the segment camera -> fragment crossing the storm top inside its footprint means the storm hides this part of the ripple.
		vec3 cam = CAMERA_POSITION_WORLD;
		vec3 frag_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		vec3 seg = frag_world - cam;
		float denom = dot(cf_ripple_occluder_b.xyz, seg);
		if (abs(denom) > 1.0e-6) {
			float t = dot(cf_ripple_occluder_b.xyz, cf_ripple_occluder_a.xyz - cam) / denom;
			if (t > 0.0 && t < 1.0 && length(cam + seg * t - cf_ripple_occluder_a.xyz) < cf_ripple_occluder_a.w) fades *= 0.15;
		}
	}
	if (fades < 1.0 / 32768.0) discard;   // nothing the later thresholds would keep

	vec4 frame = cf_ripple_frame;
	vec2 d = UV * vec2(frame.z, 1.0);
	vec2 fwd = fwidth(d);
	float pixel_m = max(fwd.x, fwd.y);
	vec2 slope = vec2(0.0);
	float weight = 0.0;
	float boost = 0.0;
	vec3 light = vec3(0.0);
	cf_ripple_eval(d, pixel_m, cf_ripple_wave, cf_ripple_crawl, frame.w, slope, weight, boost, light);
	float w = weight * fades * cf_haze_visibility;
	light *= fades * cf_haze_visibility;
	if (w < 1.0 / 1024.0 && max(light.x, max(light.y, light.z)) < 1.0 / 4096.0) discard;
	float a = clamp(4.0 * w, 0.0, 1.0);

	if (cf_debug_mode > 0) {
		ALBEDO = vec3(1.0, 0.0, 1.0) * a + light;
		ALPHA = a;
	} else {
		vec2 lattice_p = UV + frame.xy;
		vec2 lattice_fw = fwidth(lattice_p);
		float lattice_pixel = max(lattice_fw.x, lattice_fw.y);
		vec4 seam = cf_seam(lattice_p, lattice_pixel);
		float scale = max(1.0, VIEWPORT_SIZE.y / 1080.0);
		vec2 offset = cf_ripple_offset_px(slope, ta, tb, fades, cf_ripple_max_px * scale)
			+ cf_seam_offset_px(seam, ta, tb, cf_lattice_strength * w, scale);
		vec2 uv = SCREEN_UV + offset / VIEWPORT_SIZE;
		// Never pull colour from something in front of the field (no halo around the wall top or a POI).
		if (cf_view_depth_at(uv, INV_PROJECTION_MATRIX) < frag_depth) uv = SCREEN_UV;
		vec3 refracted = textureLod(cf_screen, uv, 0.0).rgb;
		vec3 l = normalize((VIEW_MATRIX * vec4(cf_to_sun, 0.0)).xyz);
		vec3 up = normalize((VIEW_MATRIX * vec4(cf_up, 0.0)).xyz);
		vec3 shade = cf_glass_shade(refracted, n, ta, tb, to_camera, l, up, cf_sun_irradiance, cf_sky_radiance, cf_ground_radiance,
			cf_glass_strength * w, cf_lattice_strength * w, seam, cf_seam_energy_filtered(lattice_p, lattice_pixel, cf_seam_flow_phase),
			cf_seam_energy_scale, boost * fades, cf_haze_visibility);
		ALBEDO = shade * a + light;
		ALPHA = a;
	}
}
