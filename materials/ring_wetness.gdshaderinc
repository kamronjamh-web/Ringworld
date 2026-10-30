#ifndef RING_WETNESS
#define RING_WETNESS
// Wet world receivers (docs/storm-revamp-s2-wet-world-design.md 2.3 / 2.4). One per-location wetness W(x) = wet map x shelter, read by every receiver shader:
// terrain, voxel, proxies, POI architecture, trees, grass, water... so no subsystem needs its own switch. Producer: scripts/Weather/RingWetWorld.cs
// (map from S3's rain history + local drying; globals below). valid = 0 (clear weather, map off) makes ring_wet_sample return zero and every apply function the identity,
// so receivers stay bitwise identical to their pre-wet-world output. Callers wrap the work in `if (ring_wet_params.w > 0.5)` to also skip the fetches.
//   ring_wet_map        RGBA16F: R film (gloss), G soak (darkening), B standing water, A rain now
//   ring_wet_map_frame  (camera offset from the map corner around/across m, texel m, size texels)
//   ring_wet_frame      mat4: columns 0/1 = around/across axis.xyz + camera canonical metres mod 16384; column 2 = up axis.xyz + ring radius; column 3 = camera ring centre (world)
//   ring_wet_params     (wetness strength, wet roughness, puddle amount, valid)
//   ring_wet_events     (S3 FatDrop, HailIntensity, HailSize, hail cover) at the camera
//   ring_wet_wind       (world wind direction xyz, speed 0..1)
#include "res://materials/ring_storm_light.gdshaderinc"
#include "res://materials/ring_precip_occlusion.gdshaderinc"
#include "res://materials/ring_rain_ripples.gdshaderinc"

global uniform sampler2D ring_wet_map : filter_linear, repeat_disable;
global uniform vec4 ring_wet_map_frame;
global uniform mat4 ring_wet_frame;
global uniform vec4 ring_wet_params;
global uniform vec4 ring_wet_events;
global uniform vec4 ring_wet_wind;
global uniform sampler2D ring_wet_noise_tex : filter_linear_mipmap, repeat_enable;
global uniform float ring_wet_debug_mode;

struct RingWet { float film; float soak; float standing; float rain; };

// Arc-corrected metres from the camera along (around, across); the same conversion as ring_storm_level.
vec2 ring_wet_offset(vec3 world) {
	mat4 frame = ring_wet_frame;
	vec3 relative = world - frame[3].xyz;
	float tangent = dot(relative, frame[0].xyz);
	float across = dot(relative, frame[1].xyz);
	float inward = dot(relative, frame[2].xyz);
	float ring_radius = frame[2].w;
	float arc = ring_radius > 1.0 ? ring_radius * ring_storm_arc_angle(tangent, ring_radius - inward) : tangent;
	return vec2(arc, across);
}

// World-anchored metres modulo 16384 for noise and ripples (every tile size divides 16384).
vec2 ring_wet_plane(vec3 world) {
	return vec2(ring_wet_frame[0].w, ring_wet_frame[1].w) + ring_wet_offset(world);
}

// Rain now (0..1) from the macro weather map B channel at a world position, the far fallback beyond the wet window (same threshold as the streaks, 0.18).
float ring_wet_far_rain(vec3 world) {
	if (ringworld_storm_light.w < 0.5) return 0.0;
	mat4 frame = ringworld_storm_frame;
	vec3 relative = world - frame[3].xyz;
	float tangent = dot(relative, frame[0].xyz);
	float across = dot(relative, frame[1].xyz);
	float inward = dot(relative, frame[2].xyz);
	float ring_radius = frame[2].w;
	float arc = ring_radius > 1.0 ? ring_radius * ring_storm_arc_angle(tangent, ring_radius - inward) : tangent;
	vec2 texel = vec2(frame[0].w + arc, frame[1].w + across) / max(ringworld_storm_map_frame.x, 1.0);
	texel = clamp(texel, vec2(0.5), max(ringworld_storm_map_size.xy - 0.5, vec2(0.5)));
	float potential = textureLod(ringworld_storm_map, vec3(texel / ringworld_storm_map_size.zw, ringworld_storm_map_frame.y), 0.0).z;
	return ring_storm_smooth_unit((potential - 0.18) / 0.82);
}

float ring_wet_hash(vec2 p) {
	vec3 q = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
	q += dot(q, q.yzx + 33.33);
	return fract((q.x + q.y) * q.z);
}

// Dark spots of the first fat drops: one hashed spot per 0.25 m cell, each with its own appearance threshold, so the spot density follows FatDrop.
float ring_wet_fat_drop_spots(vec2 plane_m, float view_distance) {
	float fat = ring_wet_events.x;
	float fade = 1.0 - smoothstep(40.0, 60.0, view_distance);
	if (fat <= 0.01 || fade <= 0.0) return 0.0;
	vec2 cell = floor(plane_m * 4.0);
	vec2 local = fract(plane_m * 4.0);
	float appears = ring_wet_hash(cell + 17.0);
	if (appears >= fat) return 0.0;
	vec2 centre = vec2(ring_wet_hash(cell + 3.1), ring_wet_hash(cell + 7.7)) * 0.6 + 0.2;
	float radius = mix(0.12, 0.32, ring_wet_hash(cell + 11.3));   // 3-8 cm in cell units
	float d = length(local - centre);
	return (1.0 - smoothstep(radius * 0.6, radius, d)) * fade;
}

// Shelter: the S1 precipitation occlusion map, with a normal offset (2.2). Up-facing surfaces sample just above themselves, walls 0.6 m out along their horizontal
// normal (a facade under open sky is exposed while its own roof does not shelter it), undersides are always dry. Outside the map or while it is inactive: exposed.
float ring_wet_exposure(vec3 world, vec3 normal_world, vec3 up_world) {
	float n_up = dot(normal_world, up_world);
	if (n_up < -0.25) return 0.0;
	vec3 probe = world + up_world * 0.05;
	if (n_up <= 0.5) {
		vec3 horizontal = normal_world - up_world * n_up;
		float length_h = length(horizontal);
		probe = world + (length_h > 0.001 ? horizontal / length_h * 0.6 : vec3(0.0));
	}
	return ring_precip_exposure(probe);
}

// The wet map at a world position (film, soak, standing, rain now), blended into the rain-now far fallback beyond its window. No shelter.
vec4 ring_wet_map_sample(vec3 world) {
	vec2 offset = ring_wet_offset(world);
	vec2 map_m = ring_wet_map_frame.xy + offset;
	float extent = ring_wet_map_frame.z * ring_wet_map_frame.w;
	vec2 edge = min(map_m, vec2(extent) - map_m);
	float inside = smoothstep(0.0, max(ring_wet_clock.w, 1.0), min(edge.x, edge.y));
	vec4 sampled = vec4(0.0);
	if (inside > 0.0) sampled = textureLod(ring_wet_map, clamp(map_m / extent, vec2(0.0005), vec2(0.9995)), 0.0);
	if (inside < 1.0) {
		float far_rain = ring_wet_far_rain(world);
		float term = smoothstep(0.0, 0.3, far_rain);
		sampled = mix(vec4(term, term, 0.0, far_rain), sampled, inside);
	}
	return sampled;
}

// Wetness at a surface point (zero when !valid): wet map (or the rain-now far fallback beyond its window) x shelter, plus fat-drop spots on exposed up-facing surfaces.
RingWet ring_wet_sample(vec3 world, vec3 normal_world, vec3 up_world) {
	RingWet w = RingWet(0.0, 0.0, 0.0, 0.0);
	if (ring_wet_params.w < 0.5) return w;
	vec4 sampled = ring_wet_map_sample(world);
	float exposure = ring_wet_exposure(world, normal_world, up_world);
	float n_up = dot(normal_world, up_world);
	if (n_up <= 0.5) {
		vec3 horizontal = normal_world - up_world * n_up;
		float length_h = length(horizontal);
		float facing = length_h > 0.001 ? clamp(dot(horizontal / length_h, -ring_wet_wind.xyz) * 0.5 + 0.5, 0.0, 1.0) : 0.5;
		float windward = mix(1.0, facing, ring_wet_wind.w);
		sampled.x *= 0.35 + 0.65 * windward;
	}
	w.film = sampled.x * exposure;
	w.soak = sampled.y * exposure;
	w.standing = sampled.z * exposure * step(0.5, n_up);
	w.rain = sampled.w * exposure;
	if (n_up > 0.5 && ring_wet_events.x > 0.01 && exposure > 0.0) {
		float spots = ring_wet_fat_drop_spots(ring_wet_plane(world), distance(world, ring_wet_frame[3].xyz));
		w.soak = max(w.soak, spots * 0.8 * exposure);
		w.film = max(w.film, spots * 0.6 * exposure);
	}
	return w;
}

// Vegetation and other receivers without a meaningful normal: treated as up-facing (the canopy is open to the sky unless something solid is above it).
RingWet ring_wet_sample_up(vec3 world, vec3 up_world) {
	return ring_wet_sample(world, up_world, up_world);
}

// Tree vertices: the wet map (or its far fallback) only. Trees are left out of the precipitation occlusion map and stand in the open, so the shelter
// lookup is skipped, and a 0.25 m fat-drop spot lattice sampled per vertex would only be noise on a canopy. Dense tree meshes made the full per-vertex
// sample the largest wet world cost (the shelter and spot part was about 0.35 ms on a forest view at 720p, RTX 4070; docs/storm-revamp-s2-wet-world-design.md section 10).
RingWet ring_wet_sample_vegetation(vec3 world) {
	if (ring_wet_params.w < 0.5) return RingWet(0.0, 0.0, 0.0, 0.0);
	vec4 sampled = ring_wet_map_sample(world);
	return RingWet(sampled.x, sampled.y, 0.0, sampled.w);
}

// Engine SSR (Godot 4.8 HiZ trace) skips only pixels with roughness >= 0.7, so a wet roughness below that makes every such pixel trace. Wet foliage
// (roughness 0.9 x 0.6) traced across the whole canopy for an invisible reflection (about 0.2 ms on a forest view). Receivers whose wet gloss gains
// nothing from SSR keep their output roughness at this floor (or their dry value, if lower); their direct highlight keeps the full wet gloss.
const float RING_WET_SSR_MIN_ROUGHNESS = 0.71;
float ring_wet_ssr_roughness(float wet_roughness, float dry_roughness) {
	return max(wet_roughness, min(dry_roughness, RING_WET_SSR_MIN_ROUGHNESS));
}

// Gloss 0..1 from film: smoothstep(0.25, 0.85, film) x strength (gloss saturates at 1, the strength above 1 only deepens the darkening).
float ring_wet_gloss(RingWet w) {
	return min(smoothstep(0.25, 0.85, w.film) * ring_wet_params.x, 1.0);
}

// Darkening (soak x porosity) and gloss (roughness to the wet roughness, specular up to 0.5). ALBEDO already carries the haze transmittance in every receiver,
// so scaling it is exact; the haze emission is untouched. porosity: soil 1, grass and bark 0.6, concrete 0.5, metal 0.
void ring_wet_apply(RingWet w, float porosity, inout vec3 albedo, inout float roughness, inout float specular) {
	float darken = smoothstep(0.0, 0.5, w.soak) * porosity * ring_wet_params.x;
	albedo *= 1.0 - 0.55 * min(darken, 1.0);
	float gloss = ring_wet_gloss(w);
	roughness = mix(roughness, min(roughness, ring_wet_params.y), gloss);
	specular = mix(specular, max(specular, 0.5), gloss);
	int mode = int(ring_wet_debug_mode + 0.5);
	if (mode == 1) albedo = vec3(w.film);
	else if (mode == 2) albedo = vec3(w.soak);
	else if (mode == 3) albedo = vec3(w.standing);
	else if (mode == 4) albedo = vec3(w.rain);
}

// How flat a surface is (puddles only collect on flat ground).
float ring_wet_flatness(vec3 normal_world, vec3 up_world) {
	return smoothstep(0.92, 0.985, dot(normal_world, up_world));
}

// Puddle mask (x) and rim band (y). level = 0.55 x standing x amount - (1 - (0.55 cavity + 0.45 noise)): puddles fill hollows first (cavity 1 = hollow, 0.5 = unknown) and shrink back
// the same way while drying. (The design formula has no 0.55: with it a standing level above 0.73 floods all flat ground, so the saturated wet ground would be one sheet.) The mask fades
// to nothing by 150 m (the far ground only gets an averaged gloss, see ring_wet_puddle_average).
vec2 ring_wet_puddle(RingWet w, vec2 plane_m, float flatness, float cavity, float view_distance) {
	float amount = ring_wet_params.z;
	if (w.standing <= 0.0 || amount <= 0.0 || flatness <= 0.0) return vec2(0.0);
	float lod = clamp(log2(max(view_distance, 1.0) / 24.0), 0.0, 6.0);
	float noise = textureLod(ring_wet_noise_tex, plane_m / 51.2, lod).r;
	float level = 0.55 * w.standing * amount - (1.0 - (0.55 * cavity + 0.45 * noise));
	float far_fade = 1.0 - smoothstep(100.0, 150.0, view_distance);
	float mask = smoothstep(0.0, 0.06, level) * flatness * far_fade;
	float rim = smoothstep(-0.12, 0.0, level) * (1.0 - smoothstep(0.0, 0.06, level)) * flatness * far_fade;
	return vec2(mask, rim);
}

// Averaged puddle gloss for far ground (no normals, no mask shapes): standing water on flat ground lowers the roughness.
float ring_wet_puddle_average(RingWet w, float flatness, float view_distance) {
	float amount = ring_wet_params.z;
	return clamp(w.standing * amount, 0.0, 1.0) * flatness * smoothstep(60.0, 150.0, view_distance) * 0.6;
}

// Puddle interior: albedo x 0.3, roughness 0.06 (design 0.03: a sun-disc reflection blew out the frame), SPECULAR 0.5, normal = up plus ripples (rain now, fat drops, hail). The rim band darkens and glosses.
void ring_wet_apply_puddle(vec2 puddle, RingWet w, vec2 plane_m, vec3 up_world, float view_distance,
		inout vec3 albedo, inout float roughness, inout float specular, inout vec3 normal_world) {
	if (puddle.x <= 0.0 && puddle.y <= 0.0) return;
	albedo *= (1.0 - 0.7 * puddle.x) * (1.0 - 0.25 * puddle.y);
	roughness = mix(roughness, 0.06, puddle.x);
	roughness = mix(roughness, min(roughness, 0.12), puddle.y);
	specular = mix(specular, max(specular, 0.5), max(puddle.x, puddle.y));
	if (puddle.x > 0.0) {
		vec2 g = ring_rain_ripple_gradient(plane_m, w.rain, ring_wet_events.x, ring_wet_events.y, view_distance);
		vec3 around = ring_wet_frame[0].xyz;
		vec3 across = ring_wet_frame[1].xyz;
		vec3 rippled = normalize(up_world - (around * g.x + across * g.y) * 0.18);
		normal_world = normalize(mix(normal_world, rippled, puddle.x));
	}
}

// Direct wet specular for light() paths without one: Schlick-GGX as Godot's Forward+ (see terrain3d_ring_surface.gdshader). Returns the colour factor to multiply by
// LIGHT_COLOR, the attenuation and the shadow term. Zero when gloss = 0.
vec3 ring_wet_direct_specular(vec3 N, vec3 L, vec3 V, float roughness, float specular, float gloss) {
	float n_dot_l = max(dot(N, L), 0.0);
	if (gloss <= 0.0 || n_dot_l <= 0.0 || roughness <= 0.0) return vec3(0.0);
	vec3 H = normalize(V + L);
	float n_dot_v = max(dot(N, V), 0.0001);
	float n_dot_h = clamp(dot(N, H), 0.0, 1.0);
	float l_dot_h = clamp(dot(L, H), 0.0, 1.0);
	float alpha = roughness * roughness;
	float a = n_dot_h * alpha;
	float k = alpha / max(1.0 - n_dot_h * n_dot_h + a * a, 0.000001);
	float distribution = min(k * k * (1.0 / 3.14159265), 65504.0);
	float visibility = min(0.5 / max(mix(2.0 * n_dot_l * n_dot_v, n_dot_l + n_dot_v, alpha), 0.000001), 65504.0);
	float f0 = 0.16 * specular * specular;
	float f90 = clamp(f0 * (50.0 * 0.33 * 3.0), 0.0, 1.0);
	float m = 1.0 - l_dot_h;
	float fresnel = f0 + (f90 - f0) * m * m * m * m * m;
	return vec3(n_dot_l * distribution * visibility * fresnel * gloss);
}
#endif
