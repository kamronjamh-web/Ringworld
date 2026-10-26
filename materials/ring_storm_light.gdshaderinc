#ifndef RING_STORM_LIGHT
#define RING_STORM_LIGHT
// Storm light (docs/storm-revamp-s0-storm-light-design.md). One per-location storm level D = max(G, heavy(R)) read from the world-anchored macro
// weather map Sunshine builds (R = coverage, G = storm; the same value as g_ws_storm in RingWeatherSystems.glslinc and the CPU's _localStormFactor).
// Every receiver derives its sun-shadow fade and its ambient darkening from D, so terrain, vegetation, POIs and water darken together and
// no subsystem needs its own switch. Producer: scripts/RingSunshineCloudBridge.StormLight.cs. valid = 0 (map off / no build yet) returns D = 0,
// and D = 0 keeps every consumer bitwise identical to the pre-storm-light output.
//   ringworld_storm_map        RGBA16F 2-layer array, one texel per ringworld_storm_map_frame.x metres
//   ringworld_storm_map_frame  (texel m, front layer, heavy-overcast start coverage, 0)
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

// Storm level D (0 clear .. 1 violent) at a map position in metres (same axes as ringworld_storm_frame columns 0/1 w). Positions outside the map clamp to its edge.
float ring_storm_level_at_map(vec2 map_metres) {
#ifdef RING_STORM_NO_MAP
	// Compute-shader test harness (tools/haze_consistency_gpu_test.gd): no sampler exists, the test supplies a synthetic level.
	return ring_storm_test_level(map_metres);
#else
	vec2 texel = map_metres / max(ringworld_storm_map_frame.x, 1.0);
	texel = clamp(texel, vec2(0.5), max(ringworld_storm_map_size.xy - 0.5, vec2(0.5)));
	vec4 storm_sample = textureLod(ringworld_storm_map, vec3(texel / ringworld_storm_map_size.zw, ringworld_storm_map_frame.y), 0.0);
	float heavy_start = ringworld_storm_map_frame.z;
	float heavy = ring_storm_smooth_unit((storm_sample.x - heavy_start) / max(0.01, 1.0 - heavy_start));
	return max(storm_sample.y, heavy);
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

// Same for a receiver in the scaled far viewport (ring_scale_anchor, ring_terrain_global, ring_ocean_global): its world positions are physical metres
// times ring_atmosphere_coordinate_scale (1 / 1000 there), so the physical position is world / scale. Without this the far ground and water read the
// map at the wrong place (D about 0) and kept full sun and sun glints under a closed storm. Scale 1 (every near receiver) is ring_storm_level itself.
float ring_storm_level_scaled(vec3 world, float coordinate_scale) {
	return ring_storm_level(coordinate_scale == 1.0 ? world : world / max(coordinate_scale, 0.000001));
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
