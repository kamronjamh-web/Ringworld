#[compute]
// GenericData uses the append-only shared menu/gameplay layout in CloudsInc.comp (312 floats since the engine jitter controls append; 308 with the enclosed fog append, P6; 304 with the 16-slot ring atmosphere profile, P2b).
#version 450

// GPU-resident cloud-occlusion producer.  This intentionally
// shares Sunshine's general-data layout and noise resources so the projected
// shadow follows the visible volume instead of maintaining a second weather
// simulation.
#include "./CloudsInc.comp"
#include "../../materials/ring_cloud_weather_shared.gdshaderinc"

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// R/G retain two partial optical-depth sums from the same N-sample raymarch.
// Both are written by the same dispatch so a receiver never composes a newly
// projected half with a stale half from a previous cloud, sun, or cascade
// frame.
layout(rg16f, binding = 0) uniform image2D shadow_output;
layout(binding = 1) uniform sampler2D extra_large_noise;
layout(binding = 2) uniform sampler3D large_noise;
layout(binding = 3) uniform sampler3D medium_noise;
layout(binding = 4) uniform sampler3D small_noise;
layout(binding = 5) uniform sampler2D heightmask;
layout(binding = 6) uniform uniformBuffer { GenericData data; } genericData;
// Camera-independent clouds (RingworldTechDemo): up to 64 render weather systems; see RingWeatherSystems.glslinc for the
// layout. Binding 8 is free in this shader (0-7 are used). header0.y = 0 keeps the legacy single-system path.
layout(binding = 8, std140) uniform RingWeatherSystemsBlock {
    vec4 header0;
    vec4 header1;
    vec4 header2;
    vec4 header3;
    RingWeatherSystem systems[64];
} ringWeatherSystems;
// Macro weather map (RingMacroWeatherMap.glsl): the same world-anchored front map the main compute samples; bindings 9/10 are free
// (0-8 are used). Params: see RingWeatherSystems.glslinc. Invalid (b.w = 0) keeps the direct per-system evaluation.
layout(binding = 9, std140) uniform RingMacroMapBlock {
    vec4 a;
    vec4 b;
} ringMacroMap;
layout(binding = 10) uniform sampler2DArray ring_macro_map;
// Storm profile map (RingStormProfileBake.glsl), the same front map the main compute samples; bindings 11/12 are free. Params: see
// RingStormProfileSample.glslinc. Invalid (b.w = 0) keeps the analytic storm shape below.
layout(binding = 11, std140) uniform RingStormMapBlock {
    vec4 a;
    vec4 b;
    vec4 c;
} ringStormMap;
layout(binding = 12) uniform sampler2DArray ring_storm_map;
// S6 cloud layers (RingCloudLayers.glslinc): the live layer table block (the main march's binding 29) and the layer height gradient
// (the main march's binding 30); bindings 13/14 are free (0-12 are used).
layout(binding = 14) uniform sampler2D layer_heightmask;
// Megastorm strip (RingMegastormStripBuild.glsl), the same front strip the main compute samples (its bindings 31/32); 15/16 are free here.
// Params: see RingStormProfileSample.glslinc.
layout(binding = 15, std140) uniform RingMegastormStripBlock {
    vec4 a;
    vec4 b;
} ringMegastormStrip;
layout(binding = 16) uniform sampler2DArray ring_megastorm_strip;
#define RING_WS_MACRO_MAP
// The ray mask also keeps base-lowering systems at their weatherness reach, so the layer hull's floor is the weather base wherever weatherness can be > 0.
#define RING_WS_MASK_LOWERS_BASE 1
#include "./RingWeatherSystems.glslinc"
#include "./RingStormProfileSample.glslinc"
#define RING_CLOUD_LAYERS_BINDING 13
#include "./RingCloudLayers.glslinc"

// center_radius.xyz is the ground datum at the cascade centre; w is its
// half-width. The three axes are world-space unit vectors. Axis .w values hold
// canonical centre-around, centre-across, and ring radius respectively.
// sun_floor.w is cloud floor, and ceiling_steps_extinction stores ceiling,
// vertical samples and Beer-Lambert extinction respectively. The final
// component is the first output row of this dispatch (0 for a full-map refresh;
// non-zero when the refresh is sliced across frames into a staging texture).
// adaptive_march packs the constant-step-spacing and clear-air-skip controls:
// x = target step length in metres (<= 0 selects the legacy fixed sample count
// from ceiling_steps_extinction.y), y/z = min/max adaptive sample counts, and
// w = coarse clear-air probe count (0 disables skipping; only honoured when the
// adaptive path is on).
layout(binding = 7) uniform ShadowParams {
    vec4 center_radius;
    vec4 around_axis;
    vec4 across_axis;
    vec4 up_axis;
    vec4 sun_floor;
    vec4 ceiling_steps_extinction;
    vec4 adaptive_march;
} shadow;

const int MAX_FINE_SAMPLES = 256;
const int MAX_SKIP_PROBES = 64;

float remap(float value, float from_min, float from_max, float to_min, float to_max) {
    return to_min + (value - from_min) * (to_max - to_min) / max(from_max - from_min, 0.0001);
}

vec3 to_cloud_space(vec3 world_position) {
    vec3 delta = world_position - shadow.center_radius.xyz;
    float ring_radius = max(shadow.up_axis.w, 1.0);
    float tangent = dot(delta, shadow.around_axis.xyz);
    float inward = dot(delta, shadow.up_axis.xyz);
    float outward_radius = ring_radius - inward;
    float local_arc = ring_radius * atan(tangent, outward_radius);
    float vertical = ring_radius
        - length(vec2(tangent, outward_radius));
    return vec3(
        shadow.around_axis.w + local_arc,
        vertical,
        shadow.across_axis.w + dot(delta, shadow.across_axis.xyz));
}

float ring_side_wall_mask(float canonical_across) {
    float ring_width = genericData.data.temporal_controls.y;
    if (ring_width <= 1.0) return 1.0;
    float feather = min(1000.0, max(50.0, ring_width * 0.02));
    return smoothstep(0.0, feather, canonical_across)
        * (1.0 - smoothstep(ring_width - feather, ring_width, canonical_across));
}

// Keep localized physical formations in the shadow field. The visible pass
// adds finer erosion, but sharing these silhouettes prevents a storm tower or
// anvil from becoming brightly lit terrain underneath an opaque cloud.
// One localized storm system, laid out like the legacy single-system uniforms:
// system = (centre around, centre across, major radius, minor radius), shape = (orientation cos, sin, influence,
// shader profile), detail = (pillar radius, anvil radius multiplier, anvil start, physical CloudFormationProfile).
struct StormSystemParams {
    vec4 system;
    vec4 shape;
    vec4 detail;
};

float storm_system_density(StormSystemParams storm_params, vec3 cloud_position, float normalized_height) {
    float influence = clamp(storm_params.shape.z, 0.0, 1.0);
    float profile = storm_params.shape.w;
    int physical_profile = int(round(storm_params.detail.w));
    float major_radius = storm_params.system.z;
    float minor_radius = storm_params.system.w;
    if (influence <= 0.001 || major_radius <= 1.0 || minor_radius <= 1.0) return 0.0;

    float around_delta = cloud_position.x - storm_params.system.x;
    float circumference = genericData.data.cloud_frame_up.w * 6.28318530718;
    if (circumference > 1.0) {
        around_delta = mod(around_delta + circumference * 0.5, circumference)
            - circumference * 0.5;
    }
    float across_delta = cloud_position.z - storm_params.system.y;
    float orientation_cosine = storm_params.shape.x;
    float orientation_sine = storm_params.shape.y;
    vec2 system_position = vec2(
        orientation_cosine * around_delta + orientation_sine * across_delta,
        -orientation_sine * around_delta + orientation_cosine * across_delta);
    vec2 normalized_position = vec2(
        system_position.x / max(major_radius, 1.0),
        system_position.y / max(minor_radius, 1.0));
    float system_mask = 1.0 - smoothstep(0.76, 1.0, length(normalized_position));
    if (system_mask <= 0.001) return 0.0;
    float vertical_envelope = smoothstep(0.015, 0.10, normalized_height)
        * (1.0 - smoothstep(0.90, 0.995, normalized_height));

    if (physical_profile == 4) { // Cirrus
        float streaks = 0.0;
        for (int streak_index = 0; streak_index < 4; ++streak_index) {
            float index_offset = float(streak_index) - 1.5;
            float center = index_offset * 0.31
                + sin(normalized_position.x * 5.5 + float(streak_index) * 1.9) * 0.075;
            float streak = 1.0 - smoothstep(
                0.035, 0.105, abs(normalized_position.y - center));
            streaks = max(streaks, streak
                * (1.0 - smoothstep(0.55, 1.0, abs(normalized_position.x))));
        }
        float thin_band = smoothstep(0.24, 0.42, normalized_height)
            * (1.0 - smoothstep(0.58, 0.78, normalized_height));
        return streaks * thin_band * system_mask * influence * 0.52;
    }
    if (physical_profile == 5) return system_mask * vertical_envelope * influence * 0.38;
    if (physical_profile == 6) { // Cirrocumulus
        vec2 grid = vec2(normalized_position.x * 6.0,
            normalized_position.y * 7.5 + sin(normalized_position.x * 12.0) * 0.55);
        float puff = 1.0 - smoothstep(0.18, 0.47, length(fract(grid + 0.5) - 0.5));
        float height = smoothstep(0.16, 0.38, normalized_height)
            * (1.0 - smoothstep(0.68, 0.90, normalized_height));
        return puff * height * system_mask * influence * 0.62;
    }
    if (physical_profile == 7) {
        float sheet = 0.86
            + sin(normalized_position.x * 7.0 + normalized_position.y * 4.0) * 0.08;
        return system_mask * vertical_envelope * sheet * influence * 0.72;
    }
    if (physical_profile == 8) { // Altocumulus
        vec2 grid = normalized_position * vec2(4.2, 4.8);
        grid.y += sin(normalized_position.x * 8.0) * 0.42;
        float puff = 1.0 - smoothstep(0.20, 0.53, length(fract(grid + 0.5) - 0.5));
        float height = smoothstep(0.06, 0.30, normalized_height)
            * (1.0 - smoothstep(0.72, 0.96, normalized_height));
        return puff * height * system_mask * influence * 0.82;
    }
    if (physical_profile == 9) {
        float layer = smoothstep(0.04, 0.22, normalized_height)
            * (1.0 - smoothstep(0.70, 0.94, normalized_height));
        return system_mask * layer * influence * 0.78;
    }
    if (physical_profile == 1) { // Stratocumulus
        float roll = cos(normalized_position.y * 13.0
            + sin(normalized_position.x * 6.0) * 1.4) * 0.5 + 0.5;
        float masses = smoothstep(0.18, 0.66, roll);
        float height = smoothstep(0.03, 0.24, normalized_height)
            * (1.0 - smoothstep(0.72, 0.97, normalized_height));
        return masses * height * system_mask * influence * 0.90;
    }
    if (physical_profile == 10) {
        float ragged_base = 0.08
            + (sin(normalized_position.x * 9.0)
                + sin(normalized_position.y * 11.0 + 1.7)) * 0.025;
        float deck = smoothstep(ragged_base, ragged_base + 0.12, normalized_height)
            * (1.0 - smoothstep(0.86, 0.995, normalized_height));
        return system_mask * deck * influence;
    }
    if (physical_profile == 0) { // Cumulus
        float angle = atan(normalized_position.y, normalized_position.x);
        float body_radius = mix(0.72, 0.30, clamp(normalized_height, 0.0, 1.0));
        body_radius *= 0.92 + sin(angle * 7.0 + normalized_height * 11.0) * 0.10;
        float body = 1.0 - smoothstep(
            body_radius * 0.72, body_radius, length(normalized_position));
        return body * smoothstep(0.015, 0.10, normalized_height)
            * (1.0 - smoothstep(0.72, 0.99, normalized_height))
            * system_mask * influence * 0.88;
    }

    if (physical_profile != 2 && physical_profile != 3 && profile < 1.5) return 0.0;
    float pillar_radius = max(1000.0, storm_params.detail.x);
    float anvil_radius = pillar_radius * max(1.2, storm_params.detail.y);
    float cell_width = max(pillar_radius * 3.5, anvil_radius * 2.35);
    vec2 warped = system_position;
    warped.x += sin(system_position.y / cell_width * 6.28318530718 + 1.7)
        * pillar_radius * 0.42;
    warped.y += sin(system_position.x / cell_width * 6.28318530718 + 4.1)
        * pillar_radius * 0.36;
    vec2 cell = mod(warped + cell_width * 0.5, cell_width) - cell_width * 0.5;
    float core_distance = length(cell);
    float pillar = 1.0 - smoothstep(pillar_radius * 0.58, pillar_radius, core_distance);
    float pillar_top = profile >= 2.5 ? 0.91 : 0.84;
    float sculpted = pillar * smoothstep(0.015, 0.13, normalized_height)
        * (1.0 - smoothstep(pillar_top, 0.995, normalized_height));
    if (physical_profile == 3 || profile >= 2.5) {
        float anvil = 1.0 - smoothstep(anvil_radius * 0.60, anvil_radius, core_distance);
        float anvil_start = clamp(storm_params.detail.z, 0.5, 0.9);
        float anvil_vertical = smoothstep(
            anvil_start, min(0.96, anvil_start + 0.075), normalized_height)
            * (1.0 - smoothstep(0.955, 1.0, normalized_height));
        sculpted = max(sculpted, anvil * anvil_vertical * 0.94);
    }
    return sculpted * system_mask * influence;
}

// Legacy single-system entry point: the uniforms describe the camera's system.
float localized_storm_density(vec3 cloud_position, float normalized_height) {
    StormSystemParams storm_params = StormSystemParams(
        genericData.data.storm_system, genericData.data.storm_shape, genericData.data.storm_detail);
    return storm_system_density(storm_params, cloud_position, normalized_height);
}

// Camera-independent clouds: per-system sculpt density over the masked sculpt systems. Natural systems are evaluated
// in source space (the sample is shifted by the wind frame offsets), manual systems in world canonical space. A system
// without a storm band of its own sculpts inside the background band. Mirrors ring_ws_sculpt_density in the main compute.
float ring_ws_sculpt_density(vec3 canonical, float background_floor, float background_ceiling) {
    float influence = clamp(ringWeatherSystems.header1.y, 0.0, 1.0);
    if (influence <= 0.001) return 0.0;
    vec3 detail = genericData.data.storm_detail.xyz;
    vec2 shift = vec2(genericData.data.cloud_weather_frame.y, genericData.data.cloud_weather_frame.z);
    float result = 0.0;
    // Storm towers and cumulonimbus come from the baked profile map (one fetch per sample, like the visible march's primary
    // samples); other sculpt profiles, or every profile while the map is invalid, run the analytic loop below.
    bool storm_map = ring_storm_map_valid();
    float other = 0.0;
    g_storm_map_density = 0.0;
    // Megastorm records (profile 12) take their column from the strip (docs/megastorm-design.md 3.2); no analytic fallback.
    if (storm_map || ring_megastorm_strip_valid()) {
        ring_storm_update_primary(canonical, g_ws_sculpt_mask);
        result = ring_storm_map_density(canonical.y);
    }
    for (int word = 0; word < 2; word++) {
        uint bits = word == 0 ? g_ws_sculpt_mask.x : g_ws_sculpt_mask.y;
        while (bits != 0u) {
            int index = word * 32 + findLSB(bits);
            bits &= bits - 1u;
            if (storm_map && ring_storm_is_storm_profile(index)) continue;
            if (ring_storm_is_strip_profile(index)) continue;
            vec4 c = ringWeatherSystems.systems[index].c;
            float storm_floor = c.x;
            float storm_ceiling = c.y;
            if (storm_ceiling <= storm_floor + 1.0) {
                storm_floor = background_floor;
                storm_ceiling = background_ceiling;
            }
            float storm_height = remap(canonical.y, storm_floor, storm_ceiling, 0.0, 1.0);
            float altitude_mask = min(
                smoothstep(0.0, 0.06, storm_height),
                smoothstep(1.0, 0.94, storm_height));
            if (altitude_mask == 0.0) continue;
            vec4 b = ringWeatherSystems.systems[index].b;
            StormSystemParams systemParams = StormSystemParams(
                ringWeatherSystems.systems[index].a,
                vec4(b.x, b.y, influence, ring_ws_shader_profile(int(round(c.z)))),
                vec4(detail.x, detail.y, detail.z, c.z));
            vec3 position = canonical;
            if ((ring_ws_flags(index) & RING_WS_FLAG_STATIC) == 0u) position.xz += shift;
            float system_density = storm_system_density(systemParams, position, storm_height) * altitude_mask
                * ringWeatherSystems.systems[index].d.z;
            other = max(other, system_density);
            result = max(result, system_density);
        }
    }
    g_storm_other = other;
    return result;
}

// ---- S6 cloud layers (the shadow twin of RingCloudLayerSample.glslinc) ----------------------------------------------------------
// On for a camera-independent march with a published layer table and a valid macro map (debug mask bit 64 clears it: the single legacy
// background band). The low / mid layers use the main march's density function without the view-only terms (curl warp, effectors,
// thin-layer overlap, ambient samples), scaled by the table's per-layer ShadowWeight. Cirrus (the high shell layer) adds no volumetric
// shadow here; main() adds its closed-form term (shadow weight * high coverage at the sun ray's crossing of the shell altitude) as
// cirrus_occlusion, the same term ring_layers_sun_above gives the layers below it in the main compute.
bool g_layers_on = false;
vec4 g_layer_low = vec4(0.0);
float g_layer_weather = 0.0; // the column's weatherness (map.w), for the deck closure (RingCloudLayerSample.glslinc ring_layer_closure)
vec4 g_layer_mid = vec4(0.0);

// The macro map's layer value (low, mid, high coverage, weatherness) at a canonical position (one bilinear fetch); outside the map
// there are none.
vec4 ring_layers_map_at(vec3 canonical) {
    vec4 map = vec4(0.0);
    float circumference = ringMacroMap.b.z;
    float texel = max(ringMacroMap.a.z, 1.0);
    float dx = canonical.x - ringMacroMap.a.x;
    if (circumference > 1.0) dx -= circumference * floor(dx / circumference);
    vec2 t = vec2(dx, canonical.z - ringMacroMap.a.y) / texel;
    vec2 size = ringMacroMap.b.xy;
    if (t.x >= 0.5 && t.y >= 0.5 && t.x <= size.x - 0.5 && t.y <= size.y - 0.5) {
        map = textureLod(ring_macro_map, vec3(t / vec2(textureSize(ring_macro_map, 0).xy), ringMacroMap.a.w + 2.0), 0.0);
    }
    return map;
}

// Resolves the sample's layers from the front macro map (ring_layers_map_at).
void ring_layers_update(vec3 canonical) {
    vec4 map = ring_layers_map_at(canonical);
    g_layer_low = ring_cloud_layer_resolve(0, map);
    g_layer_mid = ring_cloud_layer_resolve(1, map);
    g_layer_weather = map.w;
}

// Shadow density of one layer (0 low, 1 mid) at a sample; col is the layer's resolved (coverage, base, top, stratiformity).
float ring_layer_shadow_density(int layer, vec4 col, vec3 canonical, float weather_storm) {
    float thickness = col.z - col.y;
    if (thickness < 1.0 || canonical.y < col.y || canonical.y > col.z) return 0.0;
    vec4 layer_a = ringCloudLayers.layers[layer * 4];
    vec4 layer_c = ringCloudLayers.layers[layer * 4 + 2];
    float shadow_weight = ringCloudLayers.layers[layer * 4 + 3].z;
    if (shadow_weight <= 0.0) return 0.0;
    vec3 offset = ringCloudLayers.noise[layer].xyz;
    float stratiformity = col.w;
    // The weather gate is pure ALU on the sample's position: a cleared gate skips every fetch below.
    vec4 weather_params = genericData.data.cloud_weather_params;
    float patch_factor = 1.0;
    if (weather_params.y > 0.0) {
        float field = ring_cloud_weather_noise(canonical + offset, weather_params, genericData.data.cloud_weather_frame);
        patch_factor = mix(1.0, mix(0.05, 2.0, smoothstep(0.20, 0.80, field)), clamp(weather_params.x, 0.0, 1.0));
    }
    // Deck closure (twin of ring_layer_closure / ring_layer_density, RingCloudLayerSample.glslinc): a floor on the patch factor and on
    // the threshold. weather_storm is this sample's owned storm level (or the storm silhouette).
    float closure = max(smoothstep(0.25, 0.85, g_layer_weather), smoothstep(0.75, 0.98, col.x) * smoothstep(0.5, 0.9, col.w))
        * (1.0 - g_ws_storm_owned) * (1.0 - smoothstep(0.35, 0.80, weather_storm));
    patch_factor = max(patch_factor, closure);
    float resolved = clamp(max(clamp(col.x + ringWeatherSystems.header0.w, 0.0, 1.0) * patch_factor, weather_storm), 0.0, 1.0);
    float gate = ring_cloud_weather_clear_gate(resolved, weather_storm, weather_params);
    if (gate <= 0.0) return 0.0;
    float threshold = max(mix(mix(0.70, 0.90, resolved), 1.05, smoothstep(0.35, 0.80, weather_storm)), 0.97 * closure * clamp(col.x, 0.0, 1.0));

    float height = (canonical.y - col.y) / thickness;
    float edge_fade = min(smoothstep(0.0, 0.1, height), smoothstep(1.0, 0.9, height));
    if (edge_fade <= 0.0) return 0.0;
    float gradient_v = (2.0 * layer_a.w + 0.5 + stratiformity) / 8.0;
    vec4 gradient = texture(layer_heightmask, vec2(height, gradient_v));

    float extra = texture(extra_large_noise,
        (canonical.xz + offset.xz - genericData.data.extralargenoiseposition.xz) /
        max(genericData.data.extralargenoisescale, 1.0)).a * gradient.b;
    vec3 position = canonical;
    vec2 wind_direction = genericData.data.WindDirection;
    float wind_swept = 1.0 - clamp(height / genericData.data.windSweptRange, 0.0, 1.0);
    position += vec3(wind_direction.x, 0.0, wind_direction.y) * genericData.data.windSweptPower * wind_swept * wind_swept;

    vec3 large_position = (position + offset - genericData.data.largenoiseposition) / max(genericData.data.large_noise_scale, 1.0);
    // Stratiform stretch: constant-factor lookup cross-faded by stratiformity (RingCloudLayerSample.glslinc ring_layer_density; a
    // stratiformity-dependent divisor of the absolute noise coordinate streaks the noise along stratiformity gradients).
    float large_scale = extra * gradient.r;
    float large_shape = 0.0;
    if (stratiformity < 1.0)
        large_shape = smoothstep(1.0 - threshold, 1.1 - threshold, texture(large_noise, large_position).r * large_scale);
    if (stratiformity > 0.0) {
        vec3 stretched_position = vec3(large_position.x / layer_c.x, large_position.y, large_position.z / layer_c.x);
        large_shape = mix(large_shape, smoothstep(1.0 - threshold, 1.1 - threshold, texture(large_noise, stretched_position).r * large_scale),
            stratiformity);
    }
    if (large_shape <= 0.0) return 0.0;
    float medium_shape = 1.0 - texture(medium_noise,
        (position + offset - genericData.data.mediumnoiseposition) / max(genericData.data.medium_noise_scale, 1.0)).b;
    float small_raw = texture(small_noise,
        (canonical + offset - genericData.data.smallnoiseposition) / max(genericData.data.small_noise_scale, 1.0)).r;
    float small_shape = small_raw * gradient.g * mix(layer_c.y, layer_c.z, stratiformity)
        * pow(1.0 - medium_shape, genericData.data.small_noise_strength);
    float shape = clamp((medium_shape - (1.0 - large_shape)) / large_shape, 0.0, 1.0);
    shape = clamp((shape - small_shape) / (1.0 - small_shape), 0.0, 1.0);
    return clamp(shape * edge_fade, 0.0, 1.0) * gate * shadow_weight;
}

// Background shadow density: the max over the layers whose altitude range contains the sample.
float ring_layers_shadow_background(vec3 canonical, float weather_storm) {
    ring_layers_update(canonical);
    float density = 0.0;
    if (canonical.y >= g_layer_low.y && canonical.y <= g_layer_low.z) {
        density = ring_layer_shadow_density(0, g_layer_low, canonical, weather_storm);
    }
    if (canonical.y >= g_layer_mid.y && canonical.y <= g_layer_mid.z) {
        density = max(density, ring_layer_shadow_density(1, g_layer_mid, canonical, weather_storm));
    }
    return density;
}

float sample_sunshine_density(vec3 world_position) {
	// A disabled density field is guaranteed empty. Test this before any noise
	// fetches, without broadening the condition to low positive densities.
	if (genericData.data.cloud_density <= 0.0) return 0.0;
    vec3 cloud_position = to_cloud_space(world_position);
    // Camera-independent clouds: macro weather (coverage, storm level, density scale) is evaluated here, per sample.
    bool camera_independent = ring_ws_enabled();
    float density_scale = genericData.data.cloud_density;
    if (camera_independent) {
        float unused_coverage;
        float unused_lighting;
        // One bilinear map fetch (the map is the same function sampled per texel); the direct per-system loop is the fallback.
        if (ring_ws_map_valid()) {
            ring_ws_update_macro_map(cloud_position, unused_coverage, density_scale, unused_lighting);
        } else {
            ring_ws_update_macro(cloud_position, unused_coverage, density_scale, unused_lighting);
        }
    }
    // This is intentionally evaluated once per cloud sample: the sun ray can
    // cross a ring edge, so using the ground texel's mask would alter edge
    // shadow semantics.
    float side_wall_mask = ring_side_wall_mask(cloud_position.z);
    if (side_wall_mask <= 0.0) return 0.0;

    float floor_height = genericData.data.background_altitude.x;
    float ceiling_height = genericData.data.background_altitude.y;
    if (ceiling_height <= floor_height + 1.0) {
        floor_height = genericData.data.cloud_floor;
        ceiling_height = genericData.data.cloud_ceiling;
    }
    float height = remap(cloud_position.y, floor_height, ceiling_height, 0.0, 1.0);

    float edge_fade = min(smoothstep(0.0, 0.1, height), smoothstep(1.0, 0.9, height));
    vec4 gradient = vec4(0.0);
    float large = 0.0;
    if (!g_layers_on) {
        gradient = texture(heightmask, vec2(clamp(height, 0.0, 1.0), 0.5));
        float extra = texture(extra_large_noise,
            (cloud_position.xz - genericData.data.extralargenoiseposition.xz) /
            max(genericData.data.extralargenoisescale, 1.0)).a * gradient.b;
        large = texture(large_noise,
            (cloud_position - genericData.data.largenoiseposition) /
            max(genericData.data.large_noise_scale, 1.0)).r * extra;
    }
    // This is the exact pre-medium background coverage. smoothstep returns
    // literal zero in clear cells, so those cells can skip the expensive 3D
    // detail textures without introducing a visibility threshold.
    float storm_floor = genericData.data.storm_altitude.x;
    float storm_ceiling = genericData.data.storm_altitude.y;
    if (storm_ceiling <= storm_floor + 1.0) {
        storm_floor = floor_height;
        storm_ceiling = ceiling_height;
    }
    float storm_height = remap(
        cloud_position.y, storm_floor, storm_ceiling, 0.0, 1.0);
    float storm_altitude_mask = min(
        smoothstep(0.0, 0.06, storm_height),
        smoothstep(1.0, 0.94, storm_height));
    float storm_silhouette;
    if (camera_independent) {
        storm_silhouette = ring_ws_sculpt_density(cloud_position, floor_height, ceiling_height);
    } else {
        storm_silhouette = localized_storm_density(cloud_position, storm_height)
            * storm_altitude_mask;
    }
    float weatherStorm = max(storm_silhouette, genericData.data.cloud_handoff.z);
    vec4 weather_frame = genericData.data.cloud_weather_frame;
    if (camera_independent) {
        weatherStorm = max(storm_silhouette, ring_ws_storm_level());
        weather_frame.w = ring_ws_frame_coverage();
    }
    float layered = 0.0;
    float weather_gate = 1.0;
    float background_coverage = 0.0;
    if (g_layers_on) {
        // The altitude-dispatched low / mid layers are the whole background; the storm sculpt keeps its own density below.
        // Only a storm-owned column takes the storm lift (S6 3.8), exactly as the main compute's sampleScene.
        g_ws_storm_owned = ring_ws_storm_owned_at(cloud_position, g_ws_sculpt_mask);
        layered = ring_layers_shadow_background(cloud_position, max(storm_silhouette, ring_ws_eval_owned_storm_level()));
        if (layered <= 0.0 && storm_silhouette <= 0.0) return 0.0;
    } else {
        float weatherCoverage = ring_cloud_weather_resolved_coverage(
            weather_frame.w, weatherStorm, cloud_position,
            genericData.data.cloud_weather_params, weather_frame);
        float legacyCoverage = genericData.data.cloud_coverage * 1.01;
        float weatherBackgroundThreshold = genericData.data.cloud_weather_params.y > 0.0
            ? mix(mix(0.70, 0.90, weatherCoverage), 1.05, smoothstep(0.35, 0.80, weatherStorm)) : legacyCoverage;
        large = smoothstep(1.0 - weatherBackgroundThreshold,
            1.1 - weatherBackgroundThreshold, large * gradient.r);
        weather_gate = ring_cloud_weather_clear_gate(weatherCoverage, weatherStorm,
            genericData.data.cloud_weather_params);
        background_coverage = large * edge_fade;
        if ((background_coverage <= 0.0 || weather_gate <= 0.0)
            && storm_silhouette <= 0.0) return 0.0;
    }

    // Storm-only samples need the medium channel for the existing storm
    // modulation, but not the small erosion texture used only by background.
    float storm_density = 0.0;
    vec4 medium = vec4(0.0);
    if (storm_silhouette > 0.0 || !g_layers_on) {
        // Megastorm strip columns erode with the moving-frame noise of the visible march (docs/megastorm-design.md 3.2).
        vec3 medium_coord = cloud_position - genericData.data.mediumnoiseposition;
        if (g_storm_strip) medium_coord.x -= genericData.data.ring_megastorm_noise.y;
        medium = texture(medium_noise, medium_coord / max(genericData.data.medium_noise_scale, 1.0));
        storm_density = storm_silhouette * mix(0.62, 1.0, medium.r);
        if (camera_independent && g_storm_map_density > 0.0) {
            // Baked storm columns: the same profile-driven erosion as the visible march, medium noise only.
            storm_density = max(g_storm_other * mix(0.62, 1.0, medium.r), ring_storm_eroded(1.0 - medium.b, 0.0));
        }
    }
    float background_density = layered;
    if (!g_layers_on && background_coverage > 0.0) {
        float medium_shape = 1.0 - medium.b;
        float small = texture(small_noise,
            (cloud_position - genericData.data.smallnoiseposition) /
            max(genericData.data.small_noise_scale, 1.0)).r;
        small *= gradient.g * pow(max(1.0 - medium_shape, 0.0), genericData.data.small_noise_strength);
        float shape = clamp(remap(medium_shape, 1.0 - large, 1.0, 0.0, 1.0), 0.0, 1.0);
        shape = clamp(remap(shape, small, 1.0, 0.0, 1.0), 0.0, 1.0);
        background_density = clamp(shape * edge_fade, 0.0, 1.0) * weather_gate;
    }
    float density = max(background_density, storm_density)
        * side_wall_mask;
    float sharpness = clamp(
        1.0 - genericData.data.cloud_sharpness, 0.001, 1.0) * 2.0;
    return pow(
        clamp(density * density_scale, 0.0, 1.0),
        sharpness);
}

// Conservative "may contain cloud" test for the clear-air skip. It evaluates
// only the analytic coverage terms that sample_sunshine_density computes before
// its early-out (side-wall mask, storm system extent, weather gate) and never
// touches a noise texture. `slack` is the largest distance, in metres, between
// this probe and any point it must vouch for (half the probe spacing); every
// term is widened by its Lipschitz bound over that distance so a feature
// narrower than the probe spacing cannot be missed. Large/extra-large noise
// textures are deliberately NOT probed: their feature size (~scale / 10, a few
// km) cannot be bounded the way the analytic weather cells can. Unknown terms
// therefore resolve to "possible".
bool sunshine_cloud_possible(vec3 world_position, float slack) {
    vec3 cloud_position = to_cloud_space(world_position);
    float ring_width = genericData.data.temporal_controls.y;
    // The side-wall mask is exactly zero at or beyond either ring edge.
    if (ring_width > 1.0
        && (cloud_position.z + slack <= 0.0 || cloud_position.z - slack >= ring_width)) {
        return false;
    }

    // Storm system extent: localized_storm_density is zero outside the unit
    // ellipse (system_mask), whatever the profile or altitude. One metre of
    // travel moves the normalized radius by at most 1 / min(major, minor).
    bool storm_possible = false;
    float frame_coverage = genericData.data.cloud_weather_frame.w;
    float storm_level = genericData.data.cloud_handoff.z;
    bool camera_independent = ring_ws_enabled();
    if (camera_independent) {
        // Camera-independent clouds: possible anywhere a masked system's support circle (plus slack) reaches. Away from
        // every system the macro weather has no system terms, so bound its coverage by the largest cluster term:
        // cov <= clamp(base + 0.5 * strength + (0 - 0.28) * 0.46, ...). Coverage and storm level are monotonic in cov.
        // The density path reads the macro map: a bilinear value blends the four texel centres within sqrt(2) texels of the
        // sample, and the map was built from a snapshot at most one build period older than the uniforms. Widening the
        // support test by 1.5 texels plus 250 m of wind drift keeps "no system reaches this probe" true for every blended
        // texel, so the pure base + cluster bound below still dominates the map value (it never exceeds the function's
        // supremum there). Outside the map the density path evaluates exactly that no-system function.
        float map_slack = ring_ws_map_valid() ? 1.5 * ringMacroMap.a.z + 250.0 : 0.0;
        if (ring_ws_support_covers(cloud_position, slack + map_slack, g_ws_mask)) return true;
        float bound = clamp(ringWeatherSystems.header0.z
            + 0.5 * abs(ringWeatherSystems.header3.y) - 0.28 * 0.46, 0.015, 0.985);
        float heavy_start = ringWeatherSystems.header1.x;
        frame_coverage = clamp(bound + ringWeatherSystems.header0.w, 0.0, 1.0);
        storm_level = ring_ws_smooth_unit((bound - heavy_start) / max(0.01, 1.0 - heavy_start));
    }
    float influence = clamp(genericData.data.storm_shape.z, 0.0, 1.0);
    float major_radius = genericData.data.storm_system.z;
    float minor_radius = genericData.data.storm_system.w;
    if (!camera_independent && influence > 0.001 && major_radius > 1.0 && minor_radius > 1.0) {
        float around_delta = cloud_position.x - genericData.data.storm_system.x;
        float circumference = genericData.data.cloud_frame_up.w * 6.28318530718;
        if (circumference > 1.0) {
            around_delta = mod(around_delta + circumference * 0.5, circumference)
                - circumference * 0.5;
        }
        float across_delta = cloud_position.z - genericData.data.storm_system.y;
        float orientation_cosine = genericData.data.storm_shape.x;
        float orientation_sine = genericData.data.storm_shape.y;
        vec2 system_position = vec2(
            orientation_cosine * around_delta + orientation_sine * across_delta,
            -orientation_sine * around_delta + orientation_cosine * across_delta);
        float normalized_radius = length(vec2(
            system_position.x / max(major_radius, 1.0),
            system_position.y / max(minor_radius, 1.0)));
        storm_possible = normalized_radius - slack / min(major_radius, minor_radius) < 1.0;
    }

    vec4 weather_params = genericData.data.cloud_weather_params;
    if (weather_params.y <= 0.0) return true; // No weather gate: any cell may hold cloud.
    float weather_storm = max(storm_possible ? 1.0 : 0.0, storm_level);
    // The weather field is bilinear (smoothstep-interpolated) value noise whose
    // finest octave has cells of params.y / 2 and mixes in at 0.28; its gradient
    // is below 2.72 field units per params.y metres, so 3.0 adds a 10% margin.
    // Coverage and the clear gate are monotonic in the field, so evaluating them
    // at field + slack gives an upper bound over the whole slack radius.
    float field = ring_cloud_weather_noise(cloud_position, weather_params,
        genericData.data.cloud_weather_frame);
    // S6: the mid layer evaluates the patch field at its own noise offset; the larger field bounds both layers.
    if (g_layers_on) {
        field = max(field, ring_cloud_weather_noise(cloud_position + ringCloudLayers.noise[1].xyz, weather_params,
            genericData.data.cloud_weather_frame));
    }
    field = min(field + 3.0 * slack / max(weather_params.y, 1.0), 1.0);
    // Mirrors ring_cloud_weather_resolved_coverage; keep the two in sync.
    float shaped = mix(0.05, 2.0, smoothstep(0.20, 0.80, field));
    float coverage = clamp(max(frame_coverage
        * mix(1.0, shaped, clamp(weather_params.x, 0.0, 1.0)), weather_storm), 0.0, 1.0);
    return ring_cloud_weather_clear_gate(coverage, weather_storm, weather_params) > 0.0
        || storm_possible;
}

void main() {
    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
    pixel.y += int(shadow.ceiling_steps_extinction.w + 0.5);
    ivec2 size = imageSize(shadow_output);
    if (any(greaterThanEqual(pixel, size))) return;

    vec2 uv = (vec2(pixel) + 0.5) / vec2(size);
    vec2 local = (uv * 2.0 - 1.0) * shadow.center_radius.w;
    float ring_radius = max(shadow.up_axis.w, 1.0);
    float angle = local.x / ring_radius;
    float sine = sin(angle);
    float cosine = cos(angle);
    vec3 local_up = normalize(
        shadow.up_axis.xyz * cosine - shadow.around_axis.xyz * sine);
    vec3 ground = shadow.center_radius.xyz
        + shadow.around_axis.xyz * (ring_radius * sine)
        + shadow.up_axis.xyz * (ring_radius * (1.0 - cosine))
        + shadow.across_axis.xyz * local.y;
    vec3 sun_direction = normalize(shadow.sun_floor.xyz);
    float sun_up = dot(sun_direction, local_up);
    if (sun_up <= 0.0001) { imageStore(shadow_output, pixel, vec4(0.0)); return; }
    float band_floor = shadow.sun_floor.w;
    float band_ceiling = shadow.ceiling_steps_extinction.x;
    // S6 volumetric low / mid layers (see ring_layers_shadow_background); debug mask bit 64 returns to the single legacy background band.
    g_layers_on = ring_ws_enabled() && ringCloudLayers.header0.x > 1.0 && (ring_ws_debug() & 64) == 0 && ring_ws_map_valid();
    if (g_layers_on) {
        // The envelope reaches at least the layers' hull (weather base to top cap).
        vec2 layer_hull = ring_cloud_layers_hull(true);
        band_floor = min(band_floor, layer_hull.x);
        band_ceiling = max(band_ceiling, layer_hull.y);
    }
    // Closed-form weak cirrus shadow (design 3.6): shadow weight * the high coverage where the sun ray crosses the shell altitude; the
    // same term as ring_layers_sun_above in the main compute. Receivers combine R/G as a transmittance product.
    float cirrus_occlusion = 0.0;
    if (g_layers_on && ringCloudLayers.layers[11].w > 0.5 && (ring_ws_debug() & 128) == 0) {
        vec3 cirrus_point = ground + sun_direction * (ringCloudLayers.layers[8].x / sun_up);
        cirrus_occlusion = ringCloudLayers.layers[11].z * clamp(ring_layers_map_at(to_cloud_space(cirrus_point)).z, 0.0, 1.0);
    }
    if (ring_ws_enabled()) {
        // Camera-independent clouds: mask the systems whose support reaches this pixel's sun ray across the whole
        // envelope, then march the background band unioned with the storm bands of the masked sculpt systems. The margin
        // also covers the clear-air probes' slack (at most an eighth of the weather scale, half the envelope march).
        float envelope_floor_distance = band_floor / sun_up;
        float envelope_ceiling_distance = band_ceiling / sun_up;
        float weather_scale = genericData.data.cloud_weather_params.y;
        float margin = 2000.0 + min(0.5 * max(envelope_ceiling_distance - envelope_floor_distance, 0.0),
            weather_scale > 0.0 ? 0.125 * weather_scale : 0.0)
            + (ring_ws_map_valid() ? 1.5 * ringMacroMap.a.z + 250.0 : 0.0); // the map slack of sunshine_cloud_possible
        vec3 mask_p0 = to_cloud_space(ground + sun_direction * envelope_floor_distance);
        vec3 mask_p1 = to_cloud_space(ground + sun_direction * (0.5 * (envelope_floor_distance + envelope_ceiling_distance)));
        vec3 mask_p2 = to_cloud_space(ground + sun_direction * envelope_ceiling_distance);
        ring_ws_set_mask(ring_ws_mask(mask_p0, mask_p1, mask_p2, margin, false));
        // Sculpt density is exactly zero outside a system's ellipse, so only sculpt systems whose ellipse reaches this sun ray
        // may widen the band or add density (the support circle above is larger than the ellipse).
        g_ws_sculpt_mask = ring_ws_sculpt_ellipse_subset(g_ws_sculpt_mask, mask_p0, mask_p1, mask_p2, margin);
        vec2 background_band = vec2(genericData.data.background_altitude.x, genericData.data.background_altitude.y);
        if (background_band.y <= background_band.x + 1.0) background_band = vec2(band_floor, band_ceiling);
        if (g_layers_on) {
            // The base band is the layer hull; its floor is the weather base only when a base-lowering system reaches this sun ray
            // (the mask margin covers the map's bilinear bleed), otherwise the fair base. The sculpt bands widen it as before.
            bool lowers_base = false;
            for (int word = 0; word < 2; word++) {
                uint bits = word == 0 ? g_ws_mask.x : g_ws_mask.y;
                while (bits != 0u) {
                    int index = word * 32 + findLSB(bits);
                    bits &= bits - 1u;
                    if ((ring_ws_flags(index) & RING_WS_FLAG_LOWERS_BASE) != 0u) lowers_base = true;
                }
            }
            background_band = ring_cloud_layers_hull(lowers_base);
        }
        vec2 band = ring_ws_band(background_band, g_ws_sculpt_mask);
        band_floor = band.x;
        band_ceiling = band.y;
        // A real storm owns its anvil (S6 3.8): no cirrus shell term inside a sculpt storm's support (as in the main compute).
        if (cirrus_occlusion > 0.0) {
            cirrus_occlusion *= 1.0 - ring_ws_storm_owned_at(
                to_cloud_space(ground + sun_direction * (ringCloudLayers.layers[8].x / sun_up)), g_ws_sculpt_mask);
        }
    }
    float floor_distance = band_floor / sun_up;
    float ceiling_distance = band_ceiling / sun_up;
    float march_start = floor_distance;
    float march_length = max(ceiling_distance - floor_distance, 0.0);
    bool adaptive = shadow.adaptive_march.x > 0.0;
    int probe_count = adaptive ? clamp(int(shadow.adaptive_march.w + 0.5), 0, MAX_SKIP_PROBES) : 0;
    if (probe_count > 0 && march_length > 0.0) {
        if (genericData.data.cloud_density <= 0.0) { imageStore(shadow_output, pixel, vec4(0.0)); return; }
        // The weather field's finest octave has cells of cloud_weather_params.y / 2,
        // so probes must be no more than a quarter of that scale apart (half a
        // feature). Raise the probe count to honour it, or skip nothing when even
        // MAX_SKIP_PROBES cannot. (The slack in sunshine_cloud_possible keeps any
        // coarser spacing safe too; this only keeps the trim effective.)
        float weather_scale = genericData.data.cloud_weather_params.y;
        int required = weather_scale > 0.0
            ? int(ceil(march_length / (0.25 * weather_scale))) : 0;
        if (required > MAX_SKIP_PROBES) {
            probe_count = 0;
        } else {
            probe_count = max(probe_count, required);
        }
    } else {
        probe_count = 0;
    }
    if (probe_count > 0) {
        float segment = march_length / float(probe_count);
        int first_possible = -1;
        int last_possible = -1;
        for (int k = 0; k < probe_count; ++k) {
            vec3 probe_position = ground + sun_direction * (march_start + (float(k) + 0.5) * segment);
            if (sunshine_cloud_possible(probe_position, 0.5 * segment)) {
                if (first_possible < 0) first_possible = k;
                last_possible = k;
            }
        }
        if (first_possible < 0) { imageStore(shadow_output, pixel, vec4(cirrus_occlusion, 0.0, 0.0, 0.0)); return; }
        // Widen by one coarse segment on each side before the fine march.
        int lo = max(first_possible - 1, 0);
        int hi = min(last_possible + 1, probe_count - 1);
        march_length = float(hi - lo + 1) * segment;
        march_start += float(lo) * segment;
    }
    int samples = max(int(shadow.ceiling_steps_extinction.y), 1);
    if (adaptive) {
        int min_samples = clamp(int(shadow.adaptive_march.y + 0.5), 1, MAX_FINE_SAMPLES);
        int max_samples = clamp(int(shadow.adaptive_march.z + 0.5), min_samples, MAX_FINE_SAMPLES);
        samples = clamp(int(ceil(march_length / shadow.adaptive_march.x)), min_samples, max_samples);
    }
    float step_length = march_length / float(samples);
    float even_optical_depth = 0.0;
    float odd_optical_depth = 0.0;
    for (int i = 0; i < samples; ++i) {
        // Partition the existing N full-ray samples across R/G in this one
        // dispatch. The old producer refreshed one 2N interleave per frame,
        // so receivers multiplied a current result by a stale projected
        // result. This preserves the per-dispatch density-sampling budget
        // while keeping both partial optical depths coherent.
        vec3 sample_position = ground + sun_direction * (march_start
            + (float(i) + 0.5) * step_length);
        float contribution = sample_sunshine_density(sample_position) * step_length;
        if ((i & 1) == 0) {
            even_optical_depth += contribution;
        } else {
            odd_optical_depth += contribution;
        }
    }
    // Bridge tuning is expressed per kilometre; sample geometry is in metres.
    float extinction = 0.001 * max(shadow.ceiling_steps_extinction.z, 0.0);
    float even_occlusion = 1.0 - clamp(exp(-even_optical_depth * extinction), 0.0, 1.0);
    even_occlusion = 1.0 - (1.0 - even_occlusion) * (1.0 - cirrus_occlusion);
    float odd_occlusion = 1.0 - clamp(exp(-odd_optical_depth * extinction), 0.0, 1.0);
    imageStore(shadow_output, pixel, vec4(even_occlusion, odd_occlusion, 0.0, 0.0));
}
