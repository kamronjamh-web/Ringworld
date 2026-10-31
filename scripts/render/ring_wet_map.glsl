#[compute]
#version 450

// Wet map (docs/storm-revamp-s2-wet-world-design.md 2.1). One thread per texel of the 256 x 256 camera-centred, 64 m texel map. Stateless: every texel is recomputed
// from S3's per-lag rain-history geometry (the snapshot, evaluated with the GLSL port of the S3 proxy), the instantaneous rain from the macro weather map
// (B = precipitation potential) and the closed-form drying of WetnessModel. R film (gloss), G soak (darkening), B standing water, A rain now.
// A display filter (0.5 s exponential against the previous map, shifted by the texel window movement) hides the 2 Hz update lattice; it is not an integrator.

#include "./ring_rain_history.glslinc"

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict writeonly image2D out_map;
layout(set = 0, binding = 1) uniform sampler2D prev_map;
layout(set = 0, binding = 2) uniform sampler2DArray storm_map;
layout(set = 0, binding = 3, std140) uniform Params {
	vec4 origin;       // xy: texel (0,0) centre minus the snapshot observer (m, around/across), zw: texel (0,0) centre in storm-map metres
	vec4 storm;        // x: storm map texel (m), y: front layer, zw: used width/height (texels)
	vec4 storm_tex;    // xy: storm map texture size (texels), z: heavy-overcast start coverage, w: storm map valid (1/0)
	vec4 tuning;       // x: precipitation start, y: base coverage, z: display filter alpha, w: snowing (1/0)
	vec4 env;          // x: day factor, y: wind m/s, z: drying speed, w: force enabled (1/0)
	vec4 force_value;  // film, soak, standing, rain (used when env.w > 0.5)
	vec4 prev;         // xy: previous texel = texel + shift, z: previous valid (1/0), w: storm map storm-light layer (owned, front rain; 0 = none)
	ivec4 counts[3];   // systems per lag (9 lags)
	vec4 lags[3];      // lag seconds (9)
} params;
layout(set = 0, binding = 4, std430) restrict readonly buffer Systems { vec4 v[]; } systems; // per lag, per system: [offset.xy, major, minor], [cos, sin, density, potential]

float lag_seconds(int i) { return params.lags[i / 4][i % 4]; }
int lag_count(int i) { return params.counts[i / 4][i % 4]; }

void main() {
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	if (texel.x >= 256 || texel.y >= 256) return;
	vec4 result;
	if (params.env.w > 0.5) {
		result = params.force_value;
	} else {
		vec2 delta = params.origin.xy + vec2(texel) * 64.0;
		float proxies[RW_LAGS];
		float lag_s[RW_LAGS];
		for (int l = 0; l < RW_LAGS; l++) {
			lag_s[l] = lag_seconds(l);
			float best = 0.0;
			int count = lag_count(l);
			for (int s = 0; s < count; s++) {
				int base = (l * RW_MAX_SYSTEMS + s) * 2;
				vec4 a = systems.v[base];
				vec4 b = systems.v[base + 1];
				float n = rw_normalized_distance(a.xy + delta, b.xy, a.z, a.w);
				best = max(best, rw_rain_proxy(n, b.z, b.w, params.tuning.y));
			}
			proxies[l] = best;
		}
		float recent, time_since;
		rw_reduce_history(proxies, lag_s, params.tuning.x, recent, time_since);
		float rain_now = 0.0;
		float sun = params.env.x;
		if (params.storm_tex.w > 0.5) {
			vec2 map_texel = (params.origin.zw + vec2(texel) * 64.0) / max(params.storm.x, 1.0);
			map_texel = clamp(map_texel, vec2(0.5), max(params.storm.zw - 0.5, vec2(0.5)));
			vec4 m = textureLod(storm_map, vec3(map_texel / params.storm_tex.xy, params.storm.y), 0.0);
			float start = params.tuning.x;
			rain_now = rw_smooth_unit((m.z - start) / max(1.0 - start, 0.01));
			float heavy = rw_smooth_unit((m.x - params.storm_tex.z) / max(0.01, 1.0 - params.storm_tex.z));
			// Storm level D as ring_storm_owned_level (materials/ring_storm_light.gdshaderinc): storm-light layer prev.w (owned, front rain, megastorm E); 0 = none (old D).
			// The drying sun uses the receivers' D = min(D + E, 1) (megastorm WO14); E = 0 leaves it unchanged.
			vec3 o = params.prev.w > 0.5 ? textureLod(storm_map, vec3(map_texel / params.storm_tex.xy, params.prev.w), 0.0).xyz : vec3(1.0, 0.0, 0.0);
			float d = min(max(max(o.x * max(m.y, heavy), o.y), heavy * (1.0 - rw_smooth_unit(m.w / 0.25))) + o.z, 1.0);
			sun = (1.0 - rw_smooth(0.35, 0.85, m.x)) * (1.0 - d) * params.env.x;
		}
		bool snowing = params.tuning.w > 0.5;
		if (snowing) rain_now = 0.0;
		float prior = rw_prior_rain(proxies, lag_s, params.tuning.x, time_since);
		vec4 model = rw_wetness_model(recent, prior, time_since, rain_now, sun, params.env.y, params.env.z, snowing);
		result = vec4(model.xyz, rain_now);
		if (params.prev.z > 0.5) {
			ivec2 p = texel + ivec2(params.prev.xy);
			if (p.x >= 0 && p.y >= 0 && p.x < 256 && p.y < 256) result = mix(texelFetch(prev_map, p, 0), result, params.tuning.z);
		}
	}
	imageStore(out_map, texel, result);
}
