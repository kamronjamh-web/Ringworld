#[compute]
// GenericData uses the append-only shared menu/gameplay layout in CloudsInc.comp.
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

// center_radius.xyz is the ground datum at the cascade centre; w is its
// half-width. The three axes are world-space unit vectors. Axis .w values hold
// canonical centre-around, centre-across, and ring radius respectively.
// sun_floor.w is cloud floor, and ceiling_steps_extinction stores ceiling,
// vertical samples and Beer-Lambert extinction respectively. The final
// component is the first output row of this dispatch (0 for a full-map refresh;
// non-zero when the refresh is sliced across frames into a staging texture).
layout(binding = 7) uniform ShadowParams {
    vec4 center_radius;
    vec4 around_axis;
    vec4 across_axis;
    vec4 up_axis;
    vec4 sun_floor;
    vec4 ceiling_steps_extinction;
} shadow;

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
float localized_storm_density(vec3 cloud_position, float normalized_height) {
    float influence = clamp(genericData.data.storm_shape.z, 0.0, 1.0);
    float profile = genericData.data.storm_shape.w;
    int physical_profile = int(round(genericData.data.storm_detail.w));
    float major_radius = genericData.data.storm_system.z;
    float minor_radius = genericData.data.storm_system.w;
    if (influence <= 0.001 || major_radius <= 1.0 || minor_radius <= 1.0) return 0.0;

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
    float pillar_radius = max(1000.0, genericData.data.storm_detail.x);
    float anvil_radius = pillar_radius * max(1.2, genericData.data.storm_detail.y);
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
        float anvil_start = clamp(genericData.data.storm_detail.z, 0.5, 0.9);
        float anvil_vertical = smoothstep(
            anvil_start, min(0.96, anvil_start + 0.075), normalized_height)
            * (1.0 - smoothstep(0.955, 1.0, normalized_height));
        sculpted = max(sculpted, anvil * anvil_vertical * 0.94);
    }
    return sculpted * system_mask * influence;
}

float sample_sunshine_density(vec3 world_position) {
	// A disabled density field is guaranteed empty. Test this before any noise
	// fetches, without broadening the condition to low positive densities.
	if (genericData.data.cloud_density <= 0.0) return 0.0;
    vec3 cloud_position = to_cloud_space(world_position);
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

    vec4 gradient = texture(heightmask, vec2(clamp(height, 0.0, 1.0), 0.5));
    float edge_fade = min(smoothstep(0.0, 0.1, height), smoothstep(1.0, 0.9, height));
    float extra = texture(extra_large_noise,
        (cloud_position.xz - genericData.data.extralargenoiseposition.xz) /
        max(genericData.data.extralargenoisescale, 1.0)).a * gradient.b;
    float large = texture(large_noise,
        (cloud_position - genericData.data.largenoiseposition) /
        max(genericData.data.large_noise_scale, 1.0)).r * extra;
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
    float storm_silhouette = localized_storm_density(cloud_position, storm_height)
        * storm_altitude_mask;
    float weatherStorm = max(storm_silhouette, genericData.data.cloud_handoff.z);
    float weatherCoverage = ring_cloud_weather_resolved_coverage(
        genericData.data.cloud_weather_frame.w, weatherStorm, cloud_position,
        genericData.data.cloud_weather_params, genericData.data.cloud_weather_frame);
    float legacyCoverage = genericData.data.cloud_coverage * 1.01;
    float weatherBackgroundThreshold = genericData.data.cloud_weather_params.y > 0.0
        ? mix(0.70, 0.90, weatherCoverage) : legacyCoverage;
    large = smoothstep(1.0 - weatherBackgroundThreshold,
        1.1 - weatherBackgroundThreshold, large * gradient.r);
    float weather_gate = ring_cloud_weather_clear_gate(weatherCoverage, weatherStorm,
        genericData.data.cloud_weather_params);
    float background_coverage = large * edge_fade;
    if ((background_coverage <= 0.0 || weather_gate <= 0.0)
        && storm_silhouette <= 0.0) return 0.0;

    // Storm-only samples need the medium channel for the existing storm
    // modulation, but not the small erosion texture used only by background.
    vec4 medium = texture(medium_noise,
        (cloud_position - genericData.data.mediumnoiseposition) /
        max(genericData.data.medium_noise_scale, 1.0));
    float storm_density = storm_silhouette * mix(0.62, 1.0, medium.r);
    float background_density = 0.0;
    if (background_coverage > 0.0) {
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
        clamp(density * genericData.data.cloud_density, 0.0, 1.0),
        sharpness);
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
    float floor_distance = shadow.sun_floor.w / sun_up;
    float ceiling_distance = shadow.ceiling_steps_extinction.x / sun_up;
    int samples = max(int(shadow.ceiling_steps_extinction.y), 1);
    float step_length = max(ceiling_distance - floor_distance, 0.0) / float(samples);
    float even_optical_depth = 0.0;
    float odd_optical_depth = 0.0;
    for (int i = 0; i < samples; ++i) {
        // Partition the existing N full-ray samples across R/G in this one
        // dispatch. The old producer refreshed one 2N interleave per frame,
        // so receivers multiplied a current result by a stale projected
        // result. This preserves the per-dispatch density-sampling budget
        // while keeping both partial optical depths coherent.
        vec3 sample_position = ground + sun_direction * (floor_distance
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
    float odd_occlusion = 1.0 - clamp(exp(-odd_optical_depth * extinction), 0.0, 1.0);
    imageStore(shadow_output, pixel, vec4(even_occlusion, odd_occlusion, 0.0, 0.0));
}
