#ifndef RING_SHADOW_COMPOSE
#define RING_SHADOW_COMPOSE
// Shared direct-sun shadow composition (docs/shadow-revamp-plan.md S3). Every terms field is a visibility: 1 = lit, 0 = fully shadowed.
// geometry = the real-time shadow-map ATTENUATION (the caller applies shadow_floor), contact = reserved for S6 contact shadows,
// terrain = macro/planet terrain mask (S5 merges the heightfield here), cloud = projected / analytic cloud shadow.
#include "res://materials/ring_cloud_shadow.gdshaderinc" // shadow_overlap_darkening

// 0 off, 1 sun visibility, 2 geometry, 3 contact, 4 terrain, 5 cloud. Written by the Shadows > Debug menu (ShadowDebugMode).
global uniform int ringworld_shadow_debug_mode;

const uint RING_SHADOW_DEFAULT = 0u;
const uint RING_SHADOW_COMBINE_PRODUCT = 1u; // legacy vegetation path: geometry * cloud, nothing else
const uint RING_SHADOW_TERRAIN_RECEIVER = 2u; // reserved (S5)
const uint RING_SHADOW_PLANET = 4u; // reserved (S7)
const uint RING_SHADOW_NO_CONTACT = 8u; // reserved (S6)

struct RingShadowTerms {
	float geometry;
	float contact;
	float terrain;
	float cloud;
};

// world and screen_uv are unused until S5/S6; the signature is fixed now so callers need not change then.
RingShadowTerms ring_shadow_terms(vec3 world, vec2 screen_uv, float attenuation,
		float cloud_occlusion, float terrain_occlusion, uint flags) {
	RingShadowTerms t;
	t.geometry = attenuation; // raw (a point or spot light can exceed 1); the min path clamps it, the product path does not
	t.contact = 1.0;
	t.cloud = 1.0 - clamp(cloud_occlusion, 0.0, 1.0);
	t.terrain = 1.0 - clamp(terrain_occlusion, 0.0, 1.0);
	return t;
}

// DEFAULT reproduces combine_ring_shadow_visibility exactly (strongest single estimate, blended toward the compounded product by
// shadow_overlap_darkening). COMBINE_PRODUCT is the legacy vegetation path.
float ring_shadow_sun_visibility(RingShadowTerms t, uint flags) {
	if ((flags & RING_SHADOW_COMBINE_PRODUCT) != 0u) {
		return t.geometry * t.cloud;
	}
	float close = clamp(min(t.geometry, t.contact), 0.0, 1.0);
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
	if (mode == 4) return t.terrain;
	return t.cloud;
}
#endif
