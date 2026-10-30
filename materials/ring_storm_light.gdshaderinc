#ifndef RING_STORM_LIGHT
#define RING_STORM_LIGHT
// Storm light (docs/storm-revamp-s0-storm-light-design.md). One per-location storm level D (storm-owned, see ring_storm_owned_level) read from the world-anchored macro
// weather map Sunshine builds (R = coverage, G = storm; the same value as g_ws_storm in RingWeatherSystems.glslinc and the CPU's _localStormFactor).
// Every receiver derives its sun-shadow fade and its ambient darkening from D, so terrain, vegetation, POIs and water darken together and
// no subsystem needs its own switch. Producer: scripts/RingSunshineCloudBridge.StormLight.cs. valid = 0 (map off / no build yet) returns D = 0,
// and D = 0 keeps every consumer bitwise identical to the pre-storm-light output.
//   ringworld_storm_map        RGBA16F array (Sunshine's macro map: front / back 0-1 = cov, storm, precip, influence; 4-5 = storm light: owned, front rain), one texel per ringworld_storm_map_frame.x metres
//   ringworld_storm_map_frame  (texel m, front layer, heavy-overcast start coverage, storm-light layer: front + 4, or 0 when the map has none)
//   ringworld_storm_map_size   (used width, used height, texture width, texture height) in texels
//   ringworld_storm_frame      world -> map texels: columns 0/1 = around/across axis.xyz + camera position in map metres, column 2 = up axis.xyz + ring radius m, column 3 = centre
//   ringworld_storm_light      (Storm darkness strength, shadow-fade start, shadow-fade end, valid)
global uniform sampler2DArray ringworld_storm_map : filter_linear, repeat_disable;
global uniform vec4 ringworld_storm_map_frame;
global uniform vec4 ringworld_storm_map_size;
global uniform mat4 ringworld_storm_frame;
global uniform vec4 ringworld_storm_light;

// Same small-angle series as ring_cloud_shadow_arc_angle (angles stay below 0.02 rad over the map).
float ring_storm_arc_angle(float tangent, float outward) {
	float q = tangent / outward;
	if (outward > 0.0 && abs(q) <= 0.05) {
		float q2 = q * q;
		return q * (1.0 - q2 * (1.0 / 3.0 - q2 * (1.0 / 5.0 - q2 * (1.0 / 7.0))));
	}
	return atan(tangent, outward);
}

float ring_storm_smooth_unit(float value) {
	float t = clamp(value, 0.0, 1.0);
	return t * t * (3.0 - 2.0 * t);
}

// Who darkens the world (2026-10-29; docs/storm-revamp-s0-storm-light-design.md "Storm-owned D"). The macro map's storm-light layer (front + 4, its index in
// ringworld_storm_map_frame.w; RingMacroWeatherMap.glsl) holds x = storm ownership (the S6 section 3.8 weight: 1 within 1.3 ellipse radii of a StormTower /
// Cumulonimbus sculpt system, 0 by 1.45) and y = front rain level (a non-sculpt MajorFront / ExceptionalFront's own precipitation through the
// 0.25 .. 0.65 ramp: heavy frontal rain dark, drizzle grey, a dry front 0).
//   D = max(owned x max(G, heavy), front, heavy x (1 - smoothstep(0, 0.25, influence)))
// Inside a storm (owned = 1) this is exactly the old max(G, heavy). Every other system (dry Cs / As / Ns, banks, clusters) gives 0: its overcast darkens the
// ground through the cloud shadows instead. Plain heavy cluster overcast keeps its term only where no system is present (map influence below 0.25), so the
// heavy coverage a dry deck raises never leaks into D. Layer index 0 (a map without the layer) reads owned = 1, front = 0: the old D.
// CPU twin: RingSunshineCloudBridge.StormOwnedLevel; GLSL twins: ring_rain_streaks.glslinc storm_level, ring_wet_map.glsl.
const float RING_STORM_SYSTEM_FREE_INFLUENCE = 0.25;
float ring_storm_owned_level(float storm, float heavy, float influence, vec2 ownership) {
	return max(max(ownership.x * max(storm, heavy), ownership.y), heavy * (1.0 - ring_storm_smooth_unit(influence / RING_STORM_SYSTEM_FREE_INFLUENCE)));
}

// Storm level D (0 clear .. 1 violent) at a map position in metres (same axes as ringworld_storm_frame columns 0/1 w). Positions outside the map clamp to its edge.
float ring_storm_level_at_map(vec2 map_metres) {
#ifdef RING_STORM_NO_MAP
	// Compute-shader test harness (tools/haze_consistency_gpu_test.gd): no sampler exists, the test supplies a synthetic level.
	return ring_storm_test_level(map_metres);
#else
	vec2 texel = map_metres / max(ringworld_storm_map_frame.x, 1.0);
	texel = clamp(texel, vec2(0.5), max(ringworld_storm_map_size.xy - 0.5, vec2(0.5)));
	vec2 uv = texel / ringworld_storm_map_size.zw;
	vec4 storm_sample = textureLod(ringworld_storm_map, vec3(uv, ringworld_storm_map_frame.y), 0.0);
	vec2 ownership = ringworld_storm_map_frame.w > 0.5 ? textureLod(ringworld_storm_map, vec3(uv, ringworld_storm_map_frame.w), 0.0).xy : vec2(1.0, 0.0);
	float heavy_start = ringworld_storm_map_frame.z;
	float heavy = ring_storm_smooth_unit((storm_sample.x - heavy_start) / max(0.01, 1.0 - heavy_start));
	return ring_storm_owned_level(storm_sample.y, heavy, storm_sample.w, ownership);
#endif
}

// Storm level D (0 clear .. 1 violent) at a world position. Positions outside the map clamp to its edge.
float ring_storm_level(vec3 world) {
	if (ringworld_storm_light.w < 0.5) return 0.0;
	mat4 frame = ringworld_storm_frame;
	vec3 relative = world - frame[3].xyz;
	float tangent = dot(relative, frame[0].xyz);
	float across = dot(relative, frame[1].xyz);
	float inward = dot(relative, frame[2].xyz);
	float ring_radius = frame[2].w;
	float arc = ring_radius > 1.0 ? ring_radius * ring_storm_arc_angle(tangent, ring_radius - inward) : tangent;
	return ring_storm_level_at_map(vec2(frame[0].w + arc, frame[1].w + across));
}

// Same for ring_terrain_global / ring_ocean_global, which also render in the scaled far viewport (RingScaledFarViewport). That space is not the main
// world: the ring axis is +Y, the ring centre sits at ring_centre (the origin, or the regional tier's rebased centre), canonical around runs with
// atan(z, x) and across with +Y, and positions are metres x coordinate_scale (1 / 1000). The storm frame describes the main world, so the receiver
// is placed relative to the far camera instead (same pattern as ring_far_weather_cloud_occlusion): its around / across offset from the camera in
// metres is added to the camera's map position (frame[0].w, frame[1].w), which is the same canonical point in both spaces. Offsets are taken from
// world - camera (both small in the regional tier) so the big ring-centre distance never enters the subtraction. Scale 1 (the main-world LODs) is
// ring_storm_level itself.
float ring_storm_level_far(vec3 world, vec3 camera_world, vec3 ring_centre, float coordinate_scale) {
	if (coordinate_scale == 1.0) return ring_storm_level(world);
	if (ringworld_storm_light.w < 0.5) return 0.0;
	vec2 camera_radial = (camera_world - ring_centre).xz;
	float camera_radius = length(camera_radial);
	if (camera_radius <= 0.0) return 0.0;
	vec2 outward = camera_radial / camera_radius; // away from the ring axis: down, into the floor
	vec3 offset_m = (world - camera_world) / max(coordinate_scale, 0.000001);
	float tangent = dot(offset_m.xz, vec2(-outward.y, outward.x)); // d(cos a, sin a) / da: +around
	float outward_m = dot(offset_m.xz, outward);
	mat4 frame = ringworld_storm_frame;
	float ring_radius = frame[2].w;
	// The angle is taken at the receiver's true distance from the axis (camera distance + outward offset; the camera may be kilometres up) and
	// turned into ground arc at the ring radius, as ring_storm_level does.
	float arc = ring_radius > 1.0 ? ring_radius * ring_storm_arc_angle(tangent, camera_radius / max(coordinate_scale, 0.000001) + outward_m) : tangent;
	vec2 map_metres = vec2(frame[0].w + arc, frame[1].w + offset_m.y);
	// Most of the far viewport lies beyond the map (it covers the Sunshine handoff reach around the camera). The edge clamp would
	// stretch the edge texels into bands thousands of kilometres long, so the far receivers fade to D = 0 over two texels outside it instead.
	float texel_m = max(ringworld_storm_map_frame.x, 1.0);
	vec2 outside = max(max(-map_metres, map_metres - ringworld_storm_map_size.xy * texel_m), vec2(0.0));
	float inside = 1.0 - ring_storm_smooth_unit(max(outside.x, outside.y) / (2.0 * texel_m));
	return inside > 0.0 ? ring_storm_level_at_map(map_metres) * inside : 0.0;
}

// Storm level at a metre offset from the camera (map frame: the camera sits at frame[0].w / frame[1].w). Used by the view-ray path march, which has no
// world position for the camera in the sky shader. The flat offset is exact enough over the 40 km march (the ring radius is millions of metres).
float ring_storm_level_offset(vec3 offset_m) {
	if (ringworld_storm_light.w < 0.5) return 0.0;
	mat4 frame = ringworld_storm_frame;
	return ring_storm_level_at_map(vec2(frame[0].w + dot(offset_m, frame[0].xyz), frame[1].w + dot(offset_m, frame[1].xyz)));
}

// Path storm level: four taps over the first min(length, 40 km) of the view ray (direction in world axes, length in metres), averaged. The haze (objects)
// and the sky both darken their in-scatter by this one value, so a storm in the distance dims the air in front of it and the sky above it alike.
const float RING_STORM_PATH_MAX_METERS = 40000.0;
float ring_storm_path_level(vec3 direction, float length_m) {
	if (ringworld_storm_light.w < 0.5) return 0.0;
	float reach = min(length_m, RING_STORM_PATH_MAX_METERS);
	float sum = ring_storm_level_offset(direction * (reach * 0.2));
	sum += ring_storm_level_offset(direction * (reach * 0.45));
	sum += ring_storm_level_offset(direction * (reach * 0.7));
	sum += ring_storm_level_offset(direction * (reach * 0.95));
	return sum * 0.25;
}

// F: 0 below the fade start, 1 at the fade end. Sun visibility is scaled by 1 - F and the close (shadow-map / contact) terms are
// replaced by 1 so no crisp tree or object shadow survives under a storm.
float ring_storm_shadow_fade(float level) {
	if (level <= 0.0) return 0.0;
	return smoothstep(ringworld_storm_light.y, ringworld_storm_light.z, level);
}

// Direct sun SPECULAR under a storm: a closed overcast has no sun disc to mirror, so a highlight must not survive on the shadowless residual
// (1 - strength) that the diffuse sun keeps. Glossy wet ground (puddle roughness 0.12) turned that residual into a bright sun glint under a
// fully closed storm. Like the crisp shadows, the highlight goes with F alone (ring_construction_surface does the same): 1 below the fade
// start (bitwise unchanged), 0 at the fade end.
// The keep is (1 - F)^16 on the same F(D) ramp: a wet GGX glint (roughness 0.06-0.18) is tens of times the white point, more so under the storm's
// raised exposure. A linear fade left a saturated, full-size sun disc in puddles at mid-fade; the fourth power still left a large soft glow at the
// trailing edge (D 0.47, F 0.27, keep 0.28). User decision 2026-10-29 "Dimmer": only a faint sheen there (keep 0.006), a dimmed but visible glint
// as the storm clears (D 0.42: 0.17), the full glint once D is below the fade start (exactly 1), none in the core (0).
float ring_storm_specular_keep(float level) {
	float keep = 1.0 - ring_storm_shadow_fade(level);
	float keep2 = keep * keep;
	float keep4 = keep2 * keep2;
	float keep8 = keep4 * keep4;
	return keep8 * keep8;
}

// Ambient multiplier A(D) = 1 - 0.55 * strength * D. Receivers take min(1 - k * cloud occlusion, A) (never the product) so the terms do not compound.
// The ground keeps a readable floor: a storm day is dark mostly because the sun is gone, so the ambient only dims to 0.53 at the defaults.
const float RING_STORM_AMBIENT_DEPTH = 0.55;
float ring_storm_ambient(float level) {
	return 1.0 - RING_STORM_AMBIENT_DEPTH * ringworld_storm_light.x * level;
}

// Sky / haze radiance multiplier under a storm: an overcast sky is far darker than the ground it lights (ground = albedo x sky), so the sky and the air
// in-scatter dim harder than the receivers' ambient. 0.13 at the defaults.
const float RING_STORM_SKY_DEPTH = 0.97;
float ring_storm_sky(float level) {
	return 1.0 - RING_STORM_SKY_DEPTH * ringworld_storm_light.x * level;
}
#endif
