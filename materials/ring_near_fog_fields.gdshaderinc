#ifndef RING_NEAR_FOG_FIELDS
#define RING_NEAR_FOG_FIELDS
// Terrain fields of the near ground fog (docs/sun-flare-shafts-near-fog-plan.md, NF-1 design N2): the level-0 terrain height T0 and the basin floor B,
// both built on the GPU by the height-shadow producer after every L0 refresh (scripts/Shadows/RingTerrainHeightShadow.Gpu.cs,
// materials/shadows/ring_near_fog_fields.comp). Globals only, no material uniforms.
//
// Map frame. A world position goes through ringworld_height_shadow_frame to f = (around, across, up) in the anchor tangent frame (up points from the anchor floor
// toward the ring axis, which sits at f.z = R, f.x = 0). The receiver maths of ring_height_shadow.gdshaderinc gives p = (R * angle, f.y), the anchor-local
// around/across metres. The canonical (ring-vertical) height is the distance from the floor circle of radius R to the point:
//   z = R - hypot(f.x, R - f.z) = (f.z * (2 R - f.z) - f.x^2) / (R + hypot(f.x, R - f.z))
// (the second form is the numerically stable one in float; it equals f.z - f.x^2 / (2 R) to O(f.z f.x^2 / R^2), the curvature term NF-0 validated to 0.003 m).
//
// T0 texels: R32F 1024^2, canonical vertical metres (the RingHeightShadowClipmap level-0 heights), same toroidal slots as ringworld_height_shadow_l0:
//   slot = floor(p / texel) & 1023, texel centre at p = index * texel.
// Basin B: R32F 64^2, one texel per 16x16 block of T0: slot = (floor(p / texel) >> 4) & 63, minimum of T0 over 320 m then a 3x3 blur. Its texel j is centred
//   at about p = (j + 0.5) * 16 * texel.
#include "res://materials/ring_near_fog_common.gdshaderinc"

// The fog-end compute (materials/ring_near_fog_end.comp) defines RING_NEAR_FOG_FIELDS_COMPUTE and supplies these names itself (sampler bindings, and macros onto its UBO),
// so the sampling code below is shared unchanged.
#ifndef RING_NEAR_FOG_FIELDS_COMPUTE
global uniform sampler2D ringworld_height_shadow_t0_l0;
global uniform sampler2D ringworld_near_fog_basin_l0;
global uniform vec4 ringworld_near_fog_t0_bounds; // anchor-local around min/max, across min/max of the window the T0 copy holds (empty while invalid)
global uniform vec4 ringworld_near_fog_fields_params; // L0 texel m, 1 / (1024 texel), valid (1), 0
global uniform vec4 ringworld_near_fog_ref_world; // camera world position (xyz), 1 while valid: the reference of ring_near_fog_frame_point
global uniform vec4 ringworld_near_fog_ref_frame; // the frame image of that position, computed in double precision on the CPU
#endif
// ringworld_height_shadow_frame and ringworld_height_shadow_shape are declared by ring_height_shadow.gdshaderinc; the compute defines RING_HEIGHT_SHADOW_COMPUTE (and the names, as macros
// onto its UBO and its lattice bindings) before this include, so the same height-shadow functions serve the fog-end pass (NF-5).
#include "res://materials/ring_height_shadow.gdshaderinc"

// The frame translates by about the ring radius, so M * world + t in float32 carries 0.25 to 0.5 m of error in f.z. With a valid reference (the camera) the image is
// taken as f_ref + mat3(M) * (world - ref), which is the same linear map but combines only small numbers (the receivers keep the plain form: they need metres).
vec3 ring_near_fog_frame_apply(vec3 world) {
	if (ringworld_near_fog_ref_world.w > 0.5) {
		return ringworld_near_fog_ref_frame.xyz + mat3(ringworld_height_shadow_frame) * (world - ringworld_near_fog_ref_world.xyz);
	}
	return (ringworld_height_shadow_frame * vec4(world, 1.0)).xyz;
}

// The same image from the offset to the reference (valid reference only): the fog-end compute builds world - ref from the camera offset directly.
vec3 ring_near_fog_frame_apply_rel(vec3 delta) {
	return ringworld_near_fog_ref_frame.xyz + mat3(ringworld_height_shadow_frame) * delta;
}

// Frame image f -> (p.x, p.y, canonical height z), p anchor-local around/across metres. Same angle maths as ring_height_shadow_sample.
vec3 ring_near_fog_point_from_frame(vec3 f) {
	float radius = max(ringworld_height_shadow_shape.z, 1.0);
	float q = f.x / max(radius - f.z, 1.0);
	float angle = abs(q) <= 0.002 ? q - q * q * q * 0.333333333 : atan(f.x, radius - f.z);
	float axis_distance = length(vec2(f.x, radius - f.z));
	return vec3(radius * angle, f.y, (f.z * (2.0 * radius - f.z) - f.x * f.x) / (radius + axis_distance));
}

// World position -> (p.x, p.y, canonical height z).
vec3 ring_near_fog_frame_point(vec3 world) {
	return ring_near_fog_point_from_frame(ring_near_fog_frame_apply(world));
}

// T0 at p: bilinear over the four texels around p (texel centres at integer p / texel), toroidal slots.
float ring_near_fog_t0(vec2 p) {
	float texel = max(ringworld_near_fog_fields_params.x, 1.0);
	vec2 x = p / texel;
	vec2 base = floor(x);
	vec2 f = x - base;
	ivec2 i0 = ivec2(base);
	float h00 = texelFetch(ringworld_height_shadow_t0_l0, ivec2(i0.x & 1023, i0.y & 1023), 0).r;
	float h10 = texelFetch(ringworld_height_shadow_t0_l0, ivec2((i0.x + 1) & 1023, i0.y & 1023), 0).r;
	float h01 = texelFetch(ringworld_height_shadow_t0_l0, ivec2(i0.x & 1023, (i0.y + 1) & 1023), 0).r;
	float h11 = texelFetch(ringworld_height_shadow_t0_l0, ivec2((i0.x + 1) & 1023, (i0.y + 1) & 1023), 0).r;
	return mix(mix(h00, h10, f.x), mix(h01, h11, f.x), f.y);
}

// Basin floor at p: bilinear over the four basin texels (centres at (j + 0.5) block widths), toroidal.
float ring_near_fog_basin(vec2 p) {
	float block = 16.0 * max(ringworld_near_fog_fields_params.x, 1.0);
	vec2 x = p / block - 0.5;
	vec2 base = floor(x);
	vec2 f = x - base;
	ivec2 i0 = ivec2(base);
	float b00 = texelFetch(ringworld_near_fog_basin_l0, ivec2(i0.x & 63, i0.y & 63), 0).r;
	float b10 = texelFetch(ringworld_near_fog_basin_l0, ivec2((i0.x + 1) & 63, i0.y & 63), 0).r;
	float b01 = texelFetch(ringworld_near_fog_basin_l0, ivec2(i0.x & 63, (i0.y + 1) & 63), 0).r;
	float b11 = texelFetch(ringworld_near_fog_basin_l0, ivec2((i0.x + 1) & 63, (i0.y + 1) & 63), 0).r;
	return mix(mix(b00, b10, f.x), mix(b01, b11, f.x), f.y);
}

// True when the fields are published and p lies inside the T0 window with a 64 m margin.
bool ring_near_fog_fields_valid(vec2 p) {
	vec4 b = ringworld_near_fog_t0_bounds;
	return ringworld_near_fog_fields_params.z > 0.5
		&& min(min(p.x - b.x, b.y - p.x), min(p.y - b.z, b.w - p.y)) >= 64.0;
}

// Near-water field of the steam fog (scripts/Atmosphere/NearFog/RingNearFogWaterField.cs): RG32F 64^2, texel 8 m, R = water present, G = water surface canonical height (0 where dry).
// Texel (i, j) is sampled at p = origin + 8 (i, j); the texture is read bilinearly with texel centres at (i + 0.5) / 64. size 0 means no field (a 1x1 zero texture is bound).
#ifndef RING_NEAR_FOG_FIELDS_COMPUTE
global uniform sampler2D ringworld_near_fog_water : filter_linear, repeat_disable;
global uniform vec4 ringworld_near_fog_water_frame; // texel-0 centre around, across (m, relative to the height-shadow anchor, the same frame as p), texel m, size in texels (0 = none)
#endif

// (mask, surface height) at p (anchor-local around/across metres); (0, 0) outside the field. The height is the mask-weighted mean, so it stays the real level at the shore where the mask is fractional.
vec2 ring_near_fog_water(vec2 p) {
	vec4 fr = ringworld_near_fog_water_frame;
	if (fr.w < 0.5) {
		return vec2(0.0);
	}
	vec2 t = (p - fr.xy) / fr.z;
	if (min(t.x, t.y) < 0.0 || max(t.x, t.y) > fr.w - 1.0) {
		return vec2(0.0);
	}
	vec2 rg = textureLod(ringworld_near_fog_water, (t + 0.5) / fr.w, 0.0).rg;
	return vec2(rg.x, rg.y / max(rg.x, 1e-4));
}

// The froxel and fog-end density at a point: fp = ring_near_fog_frame_point image, drive, layer height, noise offset (anchor + wind, mod 4096) and the window distance fade
// (1 at the camera, 0 at the window end). 0 outside the valid fields. Before amount and gate.
float ring_near_fog_sample(vec3 fp, vec3 drive, float layer_h, vec2 noise_offset, float fade) {
	if (!ring_near_fog_fields_valid(fp.xy)) {
		return 0.0;
	}
	float t0 = ring_near_fog_t0(fp.xy);
	float basin = ring_near_fog_basin(fp.xy);
	vec2 water = ring_near_fog_water(fp.xy);
	float noise = ring_near_fog_noise(vec3(fp.x + noise_offset.x, fp.y + noise_offset.y, fp.z)) * fade;
	return ring_near_fog_density(fp.z, t0, basin, water, drive, RING_NEAR_FOG_SIGMA, layer_h, noise);
}
#endif
