#ifndef RING_SHADOW_COMPOSE
#define RING_SHADOW_COMPOSE
// Shared direct-sun shadow composition (docs/shadow-revamp-plan.md S3). Every terms field is a visibility: 1 = lit, 0 = fully shadowed.
// geometry = the real-time shadow-map ATTENUATION (the caller applies shadow_floor), contact = S6 screen-space contact shadow (1 unless the receiver assigns ring_contact_visibility),
// terrain = macro/planet terrain mask, height = terrain height-shadow clipmap (S5; 1 unless the receiver's ON branch fills it, see
// ring_height_shadow.gdshaderinc), cloud = projected / analytic cloud shadow.
#include "res://materials/ring_cloud_shadow.gdshaderinc" // shadow_overlap_darkening
#include "res://materials/ring_height_shadow.gdshaderinc"

// 0 off, 1 sun visibility, 2 geometry, 3 contact, 4 terrain (min of height-shadow clipmap and macro), 5 cloud. Written by the Shadows > Debug menu (ShadowDebugMode).
global uniform int ringworld_shadow_debug_mode;

const uint RING_SHADOW_DEFAULT = 0u;
const uint RING_SHADOW_COMBINE_PRODUCT = 1u; // legacy vegetation path: geometry * cloud, nothing else
const uint RING_SHADOW_TERRAIN_RECEIVER = 2u; // reserved (S5)
const uint RING_SHADOW_PLANET = 4u; // reserved (S7)
const uint RING_SHADOW_NO_CONTACT = 8u; // receiver takes no screen-space contact shadow (trees: their own shadow maps; oceans: blend_mix, the texel holds the seabed)
const uint RING_SHADOW_CONTACT_GRASS = 16u; // contact strength from the grass slot of ringworld_contact_shadow_params (grass cards, standing grass)

// S6 screen-space contact shadows (scripts/Shadows/RingContactShadows.cs): R8 visibility at the main view's internal resolution.
// params = (enabled, surface strength, grass strength, main-view far fingerprint |z/w| at NDC z = 0), key = (internal width, internal height, 0, 0); both are zero while the feature is off.
global uniform sampler2D ringworld_contact_shadow : filter_nearest;
global uniform vec4 ringworld_contact_shadow_params;
global uniform vec4 ringworld_contact_shadow_key;

struct RingShadowTerms {
	float geometry;
	float contact;
	float terrain;
	float cloud;
	float height; // S5: terrain height-shadow clipmap, joins the close term (full strength, floored by the caller)
};

// Contact-shadow visibility of the fragment at frag_coord (1 = lit). Returns 1 when the receiver opts out, the feature is off, the light is not the primary sun,
// or this is not the main view the texture was written for (other viewports: different size, or a far fingerprint that differs from the main view's, e.g. the
// scaled far viewports).
float ring_contact_visibility(vec2 frag_coord, vec2 viewport_size, mat4 inv_projection, float sun_weight, uint flags) {
	vec4 contact_params = ringworld_contact_shadow_params;
	if ((flags & RING_SHADOW_NO_CONTACT) != 0u || contact_params.x <= 0.0 || sun_weight <= 0.0) {
		return 1.0;
	}
	if (ivec2(viewport_size) != ivec2(ringworld_contact_shadow_key.xy)) {
		return 1.0;
	}
	vec4 far_plane = inv_projection[3]; // = inv_projection * vec4(0, 0, 0, 1), NDC z = 0 (reverse-Z far)
	float far_distance = abs(far_plane.z / far_plane.w);
	if (abs(far_distance - contact_params.w) > 0.01 * contact_params.w) {
		return 1.0;
	}
	float c = texelFetch(ringworld_contact_shadow, ivec2(frag_coord), 0).r;
	float strength = (flags & RING_SHADOW_CONTACT_GRASS) != 0u ? contact_params.z : contact_params.y;
	return 1.0 - strength * sun_weight * (1.0 - c);
}

// world and screen_uv are unused until S5/S6; the signature is fixed now so callers need not change then.
RingShadowTerms ring_shadow_terms(vec3 world, vec2 screen_uv, float attenuation,
		float cloud_occlusion, float terrain_occlusion, uint flags) {
	RingShadowTerms t;
	t.geometry = attenuation; // raw (a point or spot light can exceed 1); the min path clamps it, the product path does not
	t.contact = 1.0;
	t.cloud = 1.0 - clamp(cloud_occlusion, 0.0, 1.0);
	t.terrain = 1.0 - clamp(terrain_occlusion, 0.0, 1.0);
	t.height = 1.0;
	return t;
}

// DEFAULT reproduces combine_ring_shadow_visibility exactly (strongest single estimate, blended toward the compounded product by
// shadow_overlap_darkening). COMBINE_PRODUCT is the legacy vegetation path.
float ring_shadow_sun_visibility(RingShadowTerms t, uint flags) {
	if ((flags & RING_SHADOW_COMBINE_PRODUCT) != 0u) {
		return t.geometry * t.contact * t.cloud;
	}
	float close = clamp(min(min(t.geometry, t.contact), t.height), 0.0, 1.0);
	return mix(
		min(close, min(t.cloud, t.terrain)),
		close * t.cloud * t.terrain,
		clamp(shadow_overlap_darkening, 0.0, 1.0));
}

bool ring_shadow_debug_enabled() {
	return ringworld_shadow_debug_mode > 0;
}

float ring_shadow_debug_value(RingShadowTerms t, float sun_visibility) {
	int mode = ringworld_shadow_debug_mode;
	if (mode == 1) return sun_visibility;
	if (mode == 2) return clamp(t.geometry, 0.0, 1.0);
	if (mode == 3) return t.contact;
	if (mode == 4) return min(t.height, t.terrain);
	return t.cloud;
}
#endif
