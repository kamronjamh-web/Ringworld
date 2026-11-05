shader_type spatial;
// Containment field near patch (docs/containment-field-design.md 3.3-3.6, WO3; decisions 6-7 of 2026-11-04). Drawn in the transparent pass while the
// camera is BELOW the field (inside the contained volume): Sunshine clouds composite afterwards, so clouds in front of the field are not refracted. With the
// camera above the field the same patch is drawn after Sunshine by scripts/render/RingContainmentFieldComposite.gd instead (same maths, shared include).
//
// No base colour, no glow, no grid, no fresnel rim line: the field only refracts what is behind it and multiplies a faint cool sheen into it.
// Premultiplied: out = ALBEDO + dst * (1 - ALPHA) with ALBEDO = refracted * sheen gain * a, ALPHA = a. Fragments at or beyond the shimmer range have a = 0
// and are discarded, so the patch appears and disappears on fully transparent pixels.
//
// Mesh (ContainmentFieldNearPatch.Build): VERTEX anchor-relative in world orientation (the instance transform is a pure translation to the anchor),
// NORMAL = field normal (into the contained volume), TANGENT = around direction, UV = noise domain (m, relative to the anchor), UV2.x = arch length
// from the nearer root (m). Ripple patches (WO4) will share this shader through instance uniforms.
render_mode unshaded, cull_disabled, depth_draw_never, blend_premul_alpha, shadows_disabled, fog_disabled;

uniform sampler2D cf_screen : hint_screen_texture, filter_linear, repeat_disable;
uniform sampler2D cf_depth : hint_depth_texture, filter_nearest, repeat_disable;

#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_containment_field_core.gdshaderinc"

uniform float cf_shimmer_strength = 1.0;   // menu ContainmentShimmerStrength (RingContainmentField.DefaultShimmerStrength)
uniform float cf_sheen_strength = 1.0;     // menu ContainmentSheenStrength
uniform float cf_lattice_strength = 1.0;   // menu ContainmentLatticeStrength (RingContainmentField.DefaultLatticeStrength)
uniform vec2 cf_lattice_origin = vec2(0.0);   // ContainmentFieldNearPatch.LatticeOrigin (m)
uniform float cf_range_m = 3000.0;         // menu ContainmentShimmerRangeKm x 1000
uniform float cf_max_offset_px = 3.0;      // constant cap at 1080 lines, not a tunable
uniform vec4 cf_noise_origins = vec4(0.0); // per-octave noise origins (m), ContainmentFieldNearPatch.NoiseOrigins: weather clock drift, paused clock = frozen
uniform vec3 cf_ring_center_world = vec3(0.0);   // legacy haze fallback only (no ray frame)
uniform int cf_debug_mode = 0;             // --containment-debug: 1 coverage tint, 2 distance heat, 3 lattice mask

varying vec3 cf_tangent_view;
varying vec3 cf_normal_view;
varying float cf_haze_visibility;

void vertex() {
	cf_tangent_view = (MODELVIEW_MATRIX * vec4(TANGENT, 0.0)).xyz;
	cf_normal_view = (MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;
	// Regular ring haze between the camera and the field, per vertex: over a 6.6 km patch it varies smoothly (and by about 1 % at the wall top), so the
	// per-pixel cost stays with the shimmer.
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
	float dist = length(VERTEX);
	vec3 to_camera = -VERTEX / max(dist, 0.0001);
	vec3 n = normalize(cf_normal_view);
	float ndv = abs(dot(n, to_camera));
	float frag_depth = -VERTEX.z;
	float base = cf_distance_weight(dist, cf_range_m) * cf_grazing_fade(ndv) * cf_root_ramp(UV2.x);
	base *= cf_soft_intersection(cf_view_depth_at(SCREEN_UV, INV_PROJECTION_MATRIX) - frag_depth);
	if (base * max(max(cf_shimmer_strength, cf_sheen_strength), cf_lattice_strength) < 1.0 / 4096.0) discard;
	base *= cf_haze_visibility;
	float w_refraction = cf_shimmer_strength * base;
	float w_sheen = cf_sheen_strength * base;
	float w_lattice = cf_lattice_strength * base;
	float a = cf_alpha(w_refraction, w_sheen, w_lattice);
	vec2 lattice_p = UV + cf_lattice_origin;
	vec2 lattice_fw = fwidth(lattice_p);
	if (a < 1.0 / 1024.0) discard;
	float lattice = cf_lattice_mask(lattice_p, max(lattice_fw.x, lattice_fw.y));
	if (cf_debug_mode > 0) {
		ALBEDO = cf_debug_colour(cf_debug_mode, a, dist, cf_range_m, lattice * w_lattice) * a;
		ALPHA = a;
	} else {
		vec3 ta = normalize(cf_tangent_view);
		vec3 tb = normalize(cross(n, ta));
		float max_px = cf_max_offset_px * max(1.0, VIEWPORT_SIZE.y / 1080.0);
		vec2 offset = cf_offset_px(cf_shimmer_slope(UV, cf_noise_origins), ta, tb, min(w_refraction, 1.0), max_px);
		vec2 uv = SCREEN_UV + offset / VIEWPORT_SIZE;
		// Never pull colour from something in front of the field (no halo around the wall top or a POI).
		if (cf_view_depth_at(uv, INV_PROJECTION_MATRIX) < frag_depth) uv = SCREEN_UV;
		vec3 refracted = textureLod(cf_screen, uv, 0.0).rgb;
		ALBEDO = refracted * cf_sheen_gain(w_sheen * cf_sheen_angle(ndv)) * cf_lattice_gain(w_lattice, lattice, 0.0) * a;
		ALPHA = a;
	}
}
