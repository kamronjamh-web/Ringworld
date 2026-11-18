#ifndef RING_SKY_REFLECTION_GDSHADERINC
#define RING_SKY_REFLECTION_GDSHADERINC

#include "res://materials/ring_reflection_model.gdshaderinc"

// Camera sky access (underground, underwater), published by day_side_atmosphere.gd; 1 = the open sky.
global uniform float ringworld_sky_reflection;

// RADIANCE.a is the share of the sky radiance map this fragment may NOT reflect. visibility is what the caller knows (haze transmittance); the global is the
// camera's sky access times it. 1 writes nothing visible (a black environment at model 0 makes every write a no-op).
vec4 ring_sky_reflection_radiance(float visibility) {
	return vec4(0.0, 0.0, 0.0, 1.0 - clamp(ringworld_sky_reflection * visibility, 0.0, 1.0));
}

#endif
