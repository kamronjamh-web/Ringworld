// Emitter relay grid: per-layer uniforms and the near/far layer weights (docs/emitter-grid-climate-machines-spec.md G4.1/G4.4).
// Shared by the beam and head shaders of both layers. Defines:
//   EG_FAR_LAYER  the scaled far-viewport layer (positions in scaled units, camera from CAMERA_POSITION_WORLD).
// All distances in uniforms are real metres; world_scale converts world units to metres (1 near, CoordinateScale far).
//
// Layer ownership of a beam fragment at real view distance d:
//   near_fade(d) = 1 - smoothstep(max_distance - fade_meters, max_distance, d)   (the near layer's own range)
//   band(d)      = 1 - smoothstep(eg_handoff.x, eg_handoff.y, d)                   (cloud ownership: 1 where the
//                  near clouds (Sunshine) own the sky, 0 where the far-viewport clouds do)
//   near layer:  near_fade(d) where an opaque main-camera surface lies behind the fragment (the far viewport is hidden
//                there, and no far-viewport cloud can be in front of that surface either), otherwise min(near_fade, band).
//   far layer:   1 - min(near_fade, band). The far viewport is a canvas backdrop, so it only shows where the main camera
//                drew nothing; there the two weights sum to exactly 1.

uniform vec3 camera_world_position = vec3(0.0);
uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform vec3 beam_color : source_color = vec3(0.70, 0.92, 1.0);
uniform float base_emission = 6.0;
// World units per real metre: 1 in the main world, the far viewport CoordinateScale in scaled space.
uniform float world_scale = 1.0;
// The near layer's range (metres). The far layer receives the same values to take over exactly where the near layer fades.
uniform float max_distance = 700000.0;
uniform float fade_meters = 100000.0;
// Camera near plane in world units.
uniform float near_clip = 0.1;
// x/y: band (metres) over which beams seen against the far backdrop hand off from the near to the far layer.
// Defaults put the band out of reach (near layer owns everything, far layer only beyond max_distance).
uniform vec2 eg_handoff = vec2(1.0e12, 2.0e12);

#ifdef EG_FAR_LAYER
#define EG_CAMERA CAMERA_POSITION_WORLD
#else
#define EG_CAMERA camera_world_position
// Main-camera opaque depth (copied after the opaque pass). Reverse Z: 0 = nothing drawn (the far backdrop shows there).
uniform sampler2D eg_scene_depth : hint_depth_texture, filter_nearest, repeat_disable;
#endif

float eg_inv_scale() {
	return 1.0 / max(world_scale, 1.0e-9);
}

float eg_near_fade(float real_distance) {
	return 1.0 - smoothstep(max(max_distance - fade_meters, 0.0), max_distance, real_distance);
}

float eg_band_weight(float real_distance) {
	return 1.0 - smoothstep(eg_handoff.x, max(eg_handoff.y, eg_handoff.x + 1.0), real_distance);
}

// Nearest real distance at which the far layer can carry any weight.
float eg_far_start() {
	return min(eg_handoff.x, max(max_distance - fade_meters, 0.0));
}

// weights = (near_fade, band) interpolated from the vertex stage; raw_depth = main opaque depth at the pixel (near only).
float eg_layer_weight(vec2 weights, float raw_depth) {
#ifdef EG_FAR_LAYER
	return 1.0 - min(weights.x, weights.y);
#else
	bool backdrop = raw_depth <= 0.0 || raw_depth >= 1.0;
	return backdrop ? min(weights.x, weights.y) : weights.x;
#endif
}
