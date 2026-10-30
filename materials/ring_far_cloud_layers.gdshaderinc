// RingworldTechDemo S6 WO6 (docs/storm-revamp-s6-cloud-layers-design.md section 3.5): the S6 cloud layers beyond the Sunshine handoff.
//
// The far tiers draw the same low / mid volumetric layers and the same cirrus shell as the near Sunshine march, from the same data:
//   - the S6 layer table (the std140 RingCloudLayersBlock of RingCloudLayers.glslinc, as 41 vec4: far_cloud_layers),
//   - the layer height gradient (SunshineClouds.gd _build_layer_gradient: far_cloud_layer_gradient),
//   - the Sunshine macro weather map itself (the RD texture array, front layer: cov, storm, precip, influence; front + 2: low, mid, high
//     coverage, weatherness) where it reaches, so the handoff band reads exactly the texels the near march reads,
//   - beyond the map, the whole-ring layer atlas (RingScaledFarViewport.CloudLayers: RingCloudLayers.EvaluateMap at the far weather
//     atlas samples; high coverage already storm-owned-suppressed) plus the far weather atlas storm channel.
// Storm ownership (only sculpt storms own the storm look, section 3.8) comes from the uploaded sculpt systems (far_sculpt_*), the same
// ellipse falloff as ring_ws_storm_owned_at.
//
// Include after ring_far_cloud_density.gdshaderinc and ring_cloud_weather_shared.gdshaderinc. Plain functions over uniforms; the
// density body (far_layer_density) mirrors ring_layer_density (RingCloudLayerSample.glslinc) without curl, effectors and the thin-layer
// overlap weight: tools/far_cloud_density_parity_gpu_test.gd compares the two.
#ifndef RING_FAR_CLOUD_LAYERS_GDSHADERINC
#define RING_FAR_CLOUD_LAYERS_GDSHADERINC

uniform bool far_layers_enabled = false;
// RingCloudLayersBlock: 0 header0, 1 header1, 2 header2, 3 header3, 4 sun, 5 + 4k + (0 a, 1 b, 2 c, 3 d) layer k, 21 + p genus p, 37 + k noise k.
uniform vec4 far_cloud_layers[41];
uniform sampler2D far_cloud_layer_gradient : filter_linear, repeat_disable;
uniform sampler2DArray far_macro_map : filter_linear, repeat_disable;
// a: canonical around / across of the map's texel-grid corner (m), texel (m), front layer; b: used width / height (texels), circumference, valid.
uniform vec4 far_macro_map_a = vec4(0.0);
uniform vec4 far_macro_map_b = vec4(0.0);
// Whole-ring layer atlas (RGBA8): low, mid, high (x (1 - storm ownership)), weatherness; same UV and advection as weather_atlas.
uniform sampler2D far_layer_atlas : hint_default_black, filter_linear, repeat_enable;
uniform bool far_layer_atlas_enabled = false;
// Camera-centred fine layer atlas (RGBA8, same channels): a window of N x M texels in canonical metres at its snapshot time, advected by
// the wind for `age` seconds (RingScaledFarViewport.CloudLayers). a: window origin around (m), texel around (m), texel across (m),
// enabled; b: N, M, age (s), edge fade width (texels). The window spans the ring's whole width; only the around edges fade into the whole-ring atlas.
uniform sampler2D far_fine_atlas : hint_default_black, filter_linear, repeat_disable;
uniform vec4 far_fine_a = vec4(0.0);
uniform vec4 far_fine_b = vec4(0.0);
uniform vec2 far_fine_wind = vec2(0.0);
// x base coverage (systems header0.z), y heavy-overcast start coverage (header1.x), z coverage bias (header0.w), w downwind angle (header1.w).
uniform vec4 far_ws_header = vec4(0.5, 0.82, 0.0, 0.0);
// Sculpt (storm) systems near the camera: a = centre around / across (m, source space unless static), major / minor radius (m);
// b = orientation cos / sin, static (1) or wind-advected (0), unused.
uniform int far_sculpt_count = 0;
uniform vec4 far_sculpt_a[8];
uniform vec4 far_sculpt_b[8];
// x Sunshine cloud density multiplier, y sharpness, z reference step (m: Sunshine's far maxstep), w debug mask (1 = no cirrus).
uniform vec4 far_layers_march = vec4(1.0, 0.746, 958.0, 0.0);

const float FAR_LAYER_WEATHER_CLOSE = 0.97; // RING_LAYER_WEATHER_CLOSE
const float FAR_LAYER_DECK_DARK_FLOOR = 0.35; // RING_LAYER_DECK_DARK_FLOOR
const float FAR_STORM_SUPPORT = 1.45; // RING_STORM_SUPPORT

float far_layers_smooth(float x) { float t = clamp(x, 0.0, 1.0); return t * t * (3.0 - 2.0 * t); }

// ring_cloud_layer_resolve: (coverage, base, top, stratiformity) of layer 0 low, 1 mid, 2 high from map = (low, mid, high, weatherness).
vec4 far_layers_resolve(int layer, vec4 map) {
	vec4 a = far_cloud_layers[5 + layer * 4];
	vec4 b = far_cloud_layers[6 + layer * 4];
	float weatherness = map.w;
	float coverage = layer == 0 ? map.x : (layer == 1 ? map.y : map.z);
	float base_meters = mix(a.x, a.y, weatherness);
	float stratiformity = layer == 0
		? clamp(far_layers_smooth((coverage - 0.4) / 0.45) * 0.6 + weatherness * 0.8, 0.0, 1.0)
		: far_layers_smooth((coverage - 0.4) / 0.4);
	float convective = mix(b.x, b.y, coverage);
	float stratiform = mix(b.z, b.w, coverage);
	float top_meters = base_meters + convective + (stratiform - convective) * stratiformity;
	top_meters = max(base_meters, min(min(top_meters, a.z), far_cloud_layers[0].x));
	return vec4(coverage, base_meters, top_meters, stratiformity);
}

// Storm ownership at canonical (around, across) metres: 1 within 1.3 ellipse radii of an uploaded sculpt system, 0 by 1.45
// (ring_ws_storm_owned_at). `shift` is the wind source shift (cloud_weather_frame.yz).
float far_layers_storm_owned(vec2 xz, vec2 shift, float circumference) {
	float owned = 0.0;
	for (int i = 0; i < 8; i++) {
		if (i >= far_sculpt_count) break;
		vec4 a = far_sculpt_a[i];
		vec4 b = far_sculpt_b[i];
		vec2 offset = b.z > 0.5 ? vec2(0.0) : shift;
		float da = xz.x + offset.x - a.x;
		if (circumference > 1.0) da -= circumference * floor(da / circumference + 0.5);
		float dc = xz.y + offset.y - a.y;
		vec2 q = vec2((b.x * da + b.y * dc) / max(a.z, 1.0), (-b.y * da + b.x * dc) / max(a.w, 1.0));
		owned = max(owned, 1.0 - smoothstep(1.69, FAR_STORM_SUPPORT * FAR_STORM_SUPPORT, dot(q, q)));
	}
	return owned;
}

// The column at canonical (around, across) metres: layers = (low, mid, high, weatherness) and storm = (owned storm level, ownership,
// the weather-shared "storm" input of the layer density, 0). Inside the Sunshine macro map: its front texels (exact near parity),
// faded over the last 6 texels to the whole-ring atlas (atlas_uv: the advected far weather atlas UV, atlas_storm its storm channel).
void far_layers_column(vec2 xz, vec2 atlas_uv, float atlas_storm, vec2 shift, float circumference, out vec4 layers, out vec4 storm) {
	vec4 atlas_layers = far_layer_atlas_enabled ? textureLod(far_layer_atlas, atlas_uv, 0.0) : vec4(0.0);
	if (far_fine_a.w > 0.5) {
		// Where the content now at xz sat at the fine atlas's snapshot; the window position wraps around the ring about its centre.
		vec2 src = xz - far_fine_wind * far_fine_b.z;
		float half_span = 0.5 * far_fine_b.x * far_fine_a.y;
		float dc = src.x - far_fine_a.x - half_span;
		if (circumference > 1.0) dc -= circumference * floor(dc / circumference + 0.5);
		float tx = (dc + half_span) / far_fine_a.y;
		float edge_x = min(tx, far_fine_b.x - tx);
		float w_fine = smoothstep(0.0, max(far_fine_b.w, 1.0), edge_x);
		if (w_fine > 0.0) {
			vec2 fine_uv = vec2(tx / far_fine_b.x, clamp(src.y / (far_fine_a.z * far_fine_b.y), 0.0, 1.0));
			atlas_layers = mix(atlas_layers, textureLod(far_fine_atlas, fine_uv, 0.0), w_fine);
		}
	}
	// Beyond the map the atlas's high coverage is already suppressed under storms; its storm level is the far weather storm channel.
	layers = atlas_layers;
	storm = vec4(atlas_storm, smoothstep(0.35, 0.80, atlas_storm), atlas_storm, 0.0);
	if (far_macro_map_b.w > 0.5) {
		float texel = max(far_macro_map_a.z, 1.0);
		float dx = xz.x - far_macro_map_a.x;
		if (circumference > 1.0) dx -= circumference * floor(dx / circumference);
		vec2 t = vec2(dx, xz.y - far_macro_map_a.y) / texel;
		vec2 size = far_macro_map_b.xy;
		float edge = min(min(t.x - 0.5, size.x - 0.5 - t.x), min(t.y - 0.5, size.y - 0.5 - t.y));
		// The across edges of the map are the ring's own edges (the map spans the whole width): only the around edges fade.
		float around_edge = min(t.x - 0.5, size.x - 0.5 - t.x);
		if (edge >= 0.0) {
			vec2 uv = t / vec2(textureSize(far_macro_map, 0).xy);
			vec4 macro = textureLod(far_macro_map, vec3(uv, far_macro_map_a.w), 0.0);
			vec4 map_layers = textureLod(far_macro_map, vec3(uv, far_macro_map_a.w + 2.0), 0.0);
			float owned = far_layers_storm_owned(xz, shift, circumference);
			// ring_ws_eval_owned_storm_level: the storm level where a storm owns the column, else the heavy cluster overcast term.
			float heavy_start = far_ws_header.y;
			float heavy = far_layers_smooth((macro.x - heavy_start) / max(0.01, 1.0 - heavy_start));
			float owned_level = max(macro.y * owned, heavy * (1.0 - clamp(macro.w, 0.0, 1.0)));
			map_layers.z *= 1.0 - owned;
			float w = (far_layer_atlas_enabled || far_fine_a.w > 0.5) ? smoothstep(0.0, 6.0, around_edge) : 1.0;
			layers = mix(atlas_layers, map_layers, w);
			storm = mix(storm, vec4(owned_level, owned, owned_level, 0.0), w);
		}
	}
}

// ring_layer_closure with the column's weatherness, the layer's coverage / stratiformity and the owned storm level / ownership.
float far_layers_closure(vec4 col, float weatherness, float owned, float owned_level) {
	float weather = smoothstep(0.25, 0.85, weatherness);
	float sheet = smoothstep(0.75, 0.98, col.x) * smoothstep(0.5, 0.9, col.w);
	return max(weather, sheet) * (1.0 - owned) * (1.0 - smoothstep(0.35, 0.80, owned_level));
}

// ring_layer_density (no curl, effectors or thin-layer overlap) for layer 0 / 1 at canonical metres `p` (x around, y altitude, z across).
// `storm` is the weather storm input (the owned storm level), `closure` the layer's closure. lowpass > 0 blends the small erosion to its
// mean (samples spaced wider than the erosion noise would alias it). Returns the raw 0..1 density (before Sunshine's density multiplier).
float far_layer_density(int layer, vec4 col, vec3 p, float storm, float closure, float lowpass) {
	float thickness = col.z - col.y;
	if (thickness < 1.0 || p.y < col.y || p.y > col.z) return 0.0;
	vec4 layer_a = far_cloud_layers[5 + layer * 4];
	vec4 layer_c = far_cloud_layers[7 + layer * 4];
	vec3 offset = far_cloud_layers[37 + layer].xyz;
	float stratiformity = col.w;
	float patch_factor = 1.0;
	if (cloud_weather_params.y > 0.0) {
		float field = ring_cloud_weather_noise(p + offset, cloud_weather_params, cloud_weather_frame);
		patch_factor = mix(1.0, mix(0.05, 2.0, smoothstep(0.20, 0.80, field)), clamp(cloud_weather_params.x, 0.0, 1.0));
	}
	// The near floors the patch by the closure and packs the closure to 1/31 steps for the threshold (ring_layer_density); match it.
	patch_factor = max(patch_factor, closure);
	float closure_q = floor(clamp(closure, 0.0, 1.0) * 31.0 + 0.5) / 31.0;
	float resolved = clamp(max(clamp(col.x + far_ws_header.z, 0.0, 1.0) * patch_factor, storm), 0.0, 1.0);
	float gate = ring_cloud_weather_clear_gate(resolved, storm, cloud_weather_params);
	if (gate <= 0.0) return 0.0;
	float threshold = max(mix(mix(0.70, 0.90, resolved), 1.05, smoothstep(0.35, 0.80, storm)),
		FAR_LAYER_WEATHER_CLOSE * closure_q * clamp(col.x, 0.0, 1.0));
	float height = (p.y - col.y) / thickness;
	float edge_fade = min(smoothstep(0.0, 0.1, height), smoothstep(1.0, 0.9, height));
	if (edge_fade <= 0.0) return 0.0;
	float gradient_v = (2.0 * layer_a.w + 0.5 + stratiformity) / 8.0;
	vec4 gradient = textureLod(far_cloud_layer_gradient, vec2(height, gradient_v), 0.0);
	float extra_large = texture(far_density_extra_large_noise_patterns,
		(p.xz + offset.xz - far_density_extra_large_position.xz) / max(far_density_extra_large_scale, 1.0)).a;
	float extra_large_shape = extra_large * gradient.b;
	vec3 position = p;
	float wind_swept = 1.0 - clamp(height / max(far_density_wind_swept_range, 0.0001), 0.0, 1.0);
	position += vec3(far_density_wind_direction.x, 0.0, far_density_wind_direction.z) * far_density_wind_swept_strength * wind_swept * wind_swept;
	vec3 large_position = (position + offset - far_density_large_position) / max(far_density_large_scale, 1.0);
	// Stratiform stretch: constant-factor lookup cross-faded by stratiformity after the threshold (ring_layer_density twin). A
	// stratiformity-dependent divisor of the absolute noise coordinate streaks the noise along stratiformity gradients.
	float large_scale = extra_large_shape * gradient.r;
	float large_shape = 0.0;
	if (stratiformity < 1.0)
		large_shape = smoothstep(1.0 - threshold, 1.1 - threshold, texture(far_density_large_scale_noise, large_position).r * large_scale);
	if (stratiformity > 0.0) {
		vec3 stretched_position = vec3(large_position.x / layer_c.x, large_position.y, large_position.z / layer_c.x);
		large_shape = mix(large_shape, smoothstep(1.0 - threshold, 1.1 - threshold, texture(far_density_large_scale_noise, stretched_position).r * large_scale), stratiformity);
	}
	if (large_shape <= 0.0) return 0.0;
	float medium_shape = 1.0 - texture(far_density_medium_scale_noise, (position + offset - far_density_medium_position) / max(far_density_medium_scale, 1.0)).b;
	float erosion = gradient.g * mix(layer_c.y, layer_c.z, stratiformity) * pow(max(1.0 - medium_shape, 0.0), max(far_density_clouds_detail_power, 0.0));
	float small_raw = lowpass >= 1.0 ? 0.5
		: mix(texture(far_density_small_scale_noise, (p + offset - far_density_detail_position) / max(far_density_small_scale, 1.0)).r, 0.5, lowpass);
	float small_shape = small_raw * erosion;
	float shape = clamp((medium_shape - (1.0 - large_shape)) / large_shape, 0.0, 1.0);
	shape = clamp((shape - small_shape) / max(1.0 - small_shape, 0.0001), 0.0, 1.0);
	return clamp(shape * edge_fade, 0.0, 1.0) * gate;
}

// ring_layers_column_light: the ambient factor of a deck sample from the vertical transmittance of the layer cloud above it.
float far_layers_column_light(float altitude, vec4 low, vec4 mid) {
	float tau = 0.0;
	if (altitude <= low.z && low.z > low.y + 1.0)
		tau += clamp(low.x, 0.0, 1.0) * far_cloud_layers[7].w * (low.z - max(altitude, low.y));
	if (altitude <= mid.z && mid.z > mid.y + 1.0)
		tau += clamp(mid.x, 0.0, 1.0) * far_cloud_layers[11].w * (mid.z - max(altitude, mid.y));
	return mix(FAR_LAYER_DECK_DARK_FLOOR, 1.0, exp(-tau));
}

// Direct-sun transmittance of a layer sample through the cloud above it: its own layer's remainder and the layers above (coverage-
// weighted Beer), and the cirrus shell's weak closed-form shadow (ring_layers_sun_above without the march's reach: the far has no march).
float far_layers_sun_transmittance(float altitude, float sun_up, vec4 low, vec4 mid, float high_cov) {
	float s = max(sun_up, 0.05);
	float t = 1.0;
	if (far_cloud_layers[16].w > 0.5 && altitude < far_cloud_layers[13].x && far_layers_march.w < 0.5)
		t = 1.0 - far_cloud_layers[16].z * clamp(high_cov, 0.0, 1.0);
	if (altitude <= low.z && low.z > low.y + 1.0)
		t *= 1.0 - clamp(low.x, 0.0, 1.0) * (1.0 - exp(-far_cloud_layers[7].w * (low.z - max(altitude, low.y)) / s));
	if (altitude <= mid.z && mid.z > mid.y + 1.0)
		t *= 1.0 - clamp(mid.x, 0.0, 1.0) * (1.0 - exp(-far_cloud_layers[11].w * (mid.z - max(altitude, mid.y)) / s));
	return t;
}

#endif
