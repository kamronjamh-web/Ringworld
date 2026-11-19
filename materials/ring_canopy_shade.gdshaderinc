// Forest lighting FL-3a (docs/forest-lighting-plan.md section 5): the canopy raster's receiver side. Under trees the floor, POIs and trunks see a fraction of the sky and
// receive a little sunlight re-emitted by the sunlit canopy above. Everything here is dormant (blocked 0, species 1, no light) unless the canopy switch is on and a raster
// is published; receivers wrap every call in `if (ring_canopy_shade_on())` so nothing new runs while it is off.
// Globals (published by RingForestLighting): ringworld_canopy_cover (RGBAH raster with mips: R = b, G = b * base, B = b * top, A = b * species, premultiplied by the blocked
// fraction b; base and top relative to the frame's reference altitude), ringworld_canopy_frame (world -> anchor tangent frame; its translation column's w is the reference
// altitude, so (frame * vec4(world, 1)).w = reference), ringworld_canopy_params (x ring radius minus vertical base offset, y texel metres, z half size metres, w 1 = published).
// ForestCanopyRaster texel layout: u = (aroundLocal + half) / (2 half) along the image x axis, v = (acrossLocal + half) / (2 half) along y.
#ifndef RING_CANOPY_SHADE_INCLUDED
#define RING_CANOPY_SHADE_INCLUDED
#include "res://materials/ring_forest_lighting.gdshaderinc"
#include "res://materials/ring_shadow_compose.gdshaderinc"

global uniform sampler2D ringworld_canopy_cover : filter_linear_mipmap;
global uniform mat4 ringworld_canopy_frame;
global uniform vec4 ringworld_canopy_params;

// Canopy diffuse transmittance per unit blocked sky, broadleaf (plan section 2); the conifer factor 0.6 is in the species factor.
const vec3 RING_CANOPY_TAU = vec3(0.025, 0.050, 0.010);
// Texel size of the mip that is read: the cos-weighted sky footprint of a receiver 10 to 15 m under the crowns.
const float RING_CANOPY_SAMPLE_TEXEL_METERS = 12.0;

bool ring_canopy_shade_on() { return ringworld_forest_lighting.z > 0.5; }

// (blocked, species factor) at a world position: blocked = sky fraction hidden by the canopy above the receiver, 0 in the open, above the crowns and beyond 500 m from the raster anchor.
vec2 ring_canopy_sample(vec3 world) {
	if (!ring_canopy_shade_on() || ringworld_canopy_params.w < 0.5) {
		return vec2(0.0, 1.0);
	}
	vec4 f = ringworld_canopy_frame * vec4(world, 1.0);
	float radius = max(ringworld_canopy_params.x, 1.0);
	float q = f.x / max(radius - f.z, 1.0);
	// Small-angle series inside 10 km of the anchor (|q| <= 0.002), exact beyond (as ring_height_shadow_sample).
	float angle = abs(q) <= 0.002 ? q - q * q * q * 0.333333333 : atan(f.x, radius - f.z);
	vec2 p = vec2(radius * angle, f.y);
	float fade = 1.0 - smoothstep(380.0, 500.0, length(p));
	if (fade <= 0.0) {
		return vec2(0.0, 1.0);
	}
	float half_size = ringworld_canopy_params.z;
	vec2 uv = (p + half_size) / (2.0 * half_size);
	vec4 texel = textureLod(ringworld_canopy_cover, uv, clamp(log2(RING_CANOPY_SAMPLE_TEXEL_METERS / max(ringworld_canopy_params.y, 0.001)), 0.0, 9.0));
	float b = texel.r;
	if (b <= 1e-4) {
		return vec2(0.0, 1.0);
	}
	float base = texel.g / b;
	float top = texel.b / b;
	float species = texel.a / b;
	float altitude_rel = f.z - f.w;
	float height_weight = 1.0 - smoothstep(base, top, altitude_rel);
	return vec2(clamp(b * height_weight * fade, 0.0, 1.0), species);
}

float ring_canopy_sky_visibility(vec2 s) {
	return 1.0 - s.x;
}

// Sunlight re-emitted downward by the sunlit canopy, for the primary sun only (add to DIFFUSE_LIGHT; Godot multiplies by ALBEDO afterwards). All vectors in one space (view space
// in light()); `above` = the receiver's sun visibility above the canopy times its primary-sun and direct-light factors (ring_canopy_above).
vec3 ring_canopy_transmitted(vec3 light_color, vec3 to_light, vec3 normal, vec3 up, vec2 s, float above) {
	return (light_color / PI) * max(dot(up, to_light), 0.0) * RING_CANOPY_TAU * s.y * s.x * (0.5 + 0.5 * dot(normal, up)) * above;
}

// Sun visibility above the canopy: the receiver's terms with the sun-map (geometry) and contact terms set to 1 (the receiver is under the canopy, where the transmitted light
// lives), cloud, terrain/macro, height shadow and storm kept.
float ring_canopy_above(RingShadowTerms terms, uint combine_flags) {
	terms.geometry = 1.0;
	terms.contact = 1.0;
	return ring_shadow_sun_visibility(terms, combine_flags);
}

// Ring up in view space (the canopy frame's third row: the zenith at the raster anchor, within 500 m of every receiver that uses it).
vec3 ring_canopy_up_view(mat4 view_matrix) {
	return normalize(mat3(view_matrix) * vec3(ringworld_canopy_frame[0][2], ringworld_canopy_frame[1][2], ringworld_canopy_frame[2][2]));
}
#endif
