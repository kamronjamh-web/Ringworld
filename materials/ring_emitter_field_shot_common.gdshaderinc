// Emitter field shots (the orb each ground emitter fires straight up toward the ring hub until it meets the containment field), shared by:
//   ring_emitter_field_shot.gdshader        near layer, additive orb and dispersal flare (depth_draw_never)
//   ring_emitter_field_shot_core.gdshader   near layer, depth-only core pass (EFS_CORE_PASS, next_pass of the near material)
//   ring_emitter_field_shot_far.gdshader    far-viewport layer in scaled space (EFS_FAR_LAYER)
// One MultiMesh instance per drawn shot (EmitterFieldShotLayer / EmitterFieldShotFarLayer): the instance transform ORIGIN is the orb centre (basis identity),
// INSTANCE_CUSTOM = (orb envelope, flare, spread 0..1, energy). The mesh is one quad with UV = the corners (-1..1). Everything else is a uniform, so a steady
// frame writes only the instances.
// The orb is the false sun's orb (ring_beam_orb.gdshaderinc, same look settings: orb_look, orb_core, core_depth), billboarded at the instance origin. While a
// flare runs, the halo is widened by flare_look.x x spread and a halo-only term is added, so the dispersal reads as the orb flaring out at the field.
#ifdef EFS_FAR_LAYER
#define EG_FAR_LAYER
#endif
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_emitter_grid_layers.gdshaderinc"
#include "res://materials/ring_beam_ribbon.gdshaderinc"
#include "res://materials/ring_beam_orb.gdshaderinc"

// x halo e-fold radius (m), y HDR emission (EclipseBeamSettings.OrbHaloMeters, OrbEmission).
uniform vec2 orb_look = vec2(1500.0, 16.0);
// x core radius (m), y minimum core radius (pixels) (EclipseBeamSettings.OrbRadiusMeters, OrbMinPixelRadius).
uniform vec2 orb_core = vec2(350.0, 2.5);
// Far compensation exponent of the widened profile (EclipseBeamSettings.FarCompensation).
uniform float far_compensation = 0.45;
// x = coverage * level above which the core writes depth, y = minimum full width of the footprint in pixels (EclipseBeamSettings.CoreDepthThreshold, CoreDepthMinPixels).
uniform vec2 core_depth = vec2(0.35, 4.0);
// Brightness factor while the hub day is above 0.5 (EclipseBeamSettings.DayVisibility; the CPU twin of ehb_day_visibility's ev_shape.y).
uniform float day_visibility = 0.6;
// x halo widening at full spread (the halo radius becomes orb_look.x * (1 + (x - 1) * spread) while the flare runs), y flare gain over the orb's own halo
// (x ORB_HALO_AMPLITUDE x orb emission): at the flare peak the burst halo is twice as bright as the orb's halo and spreads to 2.5 times its radius.
uniform vec2 flare_look = vec2(2.5, 2.0);
// Close-range halo cap: the halo e-fold never exceeds this fraction of the view distance (0.12 = about 7 degrees), so an orb launching
// a few kilometres away is a bright ball, not a full-screen wash. No effect beyond about 12.5 km (1,500 m / 0.12), so far views are the false sun's orb.
uniform float close_halo_ratio = 0.12;

varying vec4 v_profile;
varying float v_atmosphere;
varying vec2 v_weights;
varying float v_depth_half;
varying vec2 v_pos;
// x orb envelope, y flare, z billboard half size (world units).
varying flat vec3 v_shot;

float efs_day_visibility(float hub_day) {
	return mix(1.0, day_visibility, smoothstep(0.45, 0.55, clamp(hub_day, 0.0, 1.0)));
}

void vertex() {
	vec4 shot = INSTANCE_CUSTOM;
	vec3 w_c = MODEL_MATRIX[3].xyz;
	vec3 camera = EG_CAMERA;
	float inv_scale = eg_inv_scale();
	vec3 view_c = (VIEW_MATRIX * vec4(w_c, 1.0)).xyz;
	float real_c = distance(w_c, camera) * inv_scale;
#ifdef EG_FAR_LAYER
	bool in_range = real_c > eg_far_start();
#else
	bool in_range = real_c < max_distance;
#endif
	bool draw_it = in_range && (shot.x > 0.0 || shot.y > 0.0) && view_c.z < -near_clip;
	v_shot = vec3(shot.x, shot.y, 0.0);
	v_pos = vec2(0.0);
	if (!draw_it) {
		v_profile = vec4(1.0, 1.0, 0.0, 0.0);
		v_atmosphere = 0.0;
		v_weights = vec2(0.0);
		v_depth_half = 0.0;
		POSITION = vec4(2.0, 2.0, 2.0, 1.0);
	} else {
		float mpp = ribbon_meters_per_pixel(length(view_c), PROJECTION_MATRIX[1][1], VIEWPORT_SIZE.y);
		float core = max(orb_core.x, 0.001) * world_scale;
		float halo_m = orb_look.x * (shot.y > 0.0 ? 1.0 + (flare_look.x - 1.0) * clamp(shot.z, 0.0, 1.0) : 1.0);
		halo_m = min(halo_m, max(close_halo_ratio, 1.0e-3) * real_c);
		float halo = max(halo_m, 0.001) * world_scale;
		vec4 profile = ribbon_profile(core, halo, orb_core.y, mpp, far_compensation);
		float depth_half = ribbon_depth_half(core_depth.y, mpp);
		float half_size = orb_half_size(profile, depth_half, mpp);
		POSITION = PROJECTION_MATRIX * vec4(view_c + vec3(UV * half_size, 0.0), 1.0);
		v_pos = UV * half_size;
		v_profile = profile;
		v_atmosphere = ribbon_atmosphere_visibility(camera * inv_scale, w_c * inv_scale, ring_center * inv_scale, 1.0);
		v_weights = vec2(eg_near_fade(real_c), eg_band_weight(real_c));
		v_depth_half = depth_half;
		v_shot.z = half_size;
	}
}

void fragment() {
#ifdef EG_FAR_LAYER
	float layer_weight = eg_layer_weight(v_weights, 0.0);
#else
	float layer_weight = eg_layer_weight(v_weights, texture(eg_scene_depth, SCREEN_UV).r);
#endif
	float orb_r = length(v_pos);
	float lit = efs_day_visibility(ringworld_hub_day) * v_atmosphere * layer_weight;
	float orb_level = v_shot.x * lit;
#ifdef EFS_CORE_PASS
	// Depth only where the lit orb core is bright, widened to the minimum depth footprint (the flare writes none).
	if (!orb_depth_core_covers(orb_level, v_profile, core_depth, orb_r, v_depth_half)) {
		discard;
	}
	ALBEDO = vec3(0.0);
	ALPHA = 1.0;
#else
	vec4 orb = orb_radiance(orb_r, orb_level, v_profile, v_shot.z, orb_look.y, beam_color);
	// The flare: a halo-only radial term (no core), brighter and wider than the orb's own halo, coloured toward white.
	float edge = 1.0 - smoothstep(0.7, 1.0, orb_r / max(v_shot.z, 1.0e-6));
	float flare = v_shot.y * flare_look.y * ORB_HALO_AMPLITUDE * orb_look.y * exp(-orb_r / max(v_profile.y, 1.0e-6)) * v_profile.w * lit * edge;
	if (orb.a + flare < 0.0001) {
		discard;
	}
	ALBEDO = orb.rgb + mix(beam_color, vec3(1.0), 0.35) * flare;
	ALPHA = 1.0;
#endif
}
