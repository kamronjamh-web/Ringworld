#[compute]
#version 460

// 3x3 gameplay query. It repeats the same filtered displacement and
// horizontal inversion used by the near-water vertex path. Output[0] is only
// the vertical delta above the caller's normal ocean base height.
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;
layout(set = 0, binding = 0) uniform sampler2DArray displacement_map;
layout(std430, set = 0, binding = 1) writeonly buffer Result { vec4 value[9]; } result;
layout(std430, set = 0, binding = 2) readonly buffer Shoreline { vec4 mapping; vec4 controls; } shoreline;
layout(set = 0, binding = 3) uniform sampler2D shoreline_field;
layout(push_constant) uniform Push { vec4 layer[4]; vec4 lods; vec4 params; uvec4 mode; } p;
float geometry_fade(vec2 horizontal) {
    // A non-positive end is the raw-displacement diagnostic mode.
    if (p.params.z <= 0.0) return 1.0;
    float distance_to_body = length(vec3(horizontal.x, p.params.w, horizontal.y));
    float begin = p.params.z * 0.65;
    return 1.0 - smoothstep(begin, p.params.z, distance_to_body);
}
vec2 shoreline_factors(vec2 source) {
    if (shoreline.controls.w < 0.5) return vec2(1.0);
    if (shoreline.controls.w < 1.5) return vec2(0.0);
    vec2 uv = shoreline.mapping.xy + source * vec2(shoreline.mapping.z);
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThanEqual(uv, vec2(1.0)))) return vec2(0.0);
    vec3 field = texture(shoreline_field, uv).rgb;
    if (field.b < 0.999) return vec2(0.0);
    if (field.r <= 0.0) return vec2(0.0);
    float vertical = smoothstep(max(0.0, shoreline.controls.z), max(shoreline.controls.z + 0.01, shoreline.mapping.w), field.r) * smoothstep(0.0, max(0.01, shoreline.controls.x), field.g);
    return vec2(vertical, vertical * smoothstep(0.0, max(0.01, shoreline.controls.y), field.g));
}
void main() {
    ivec2 cell = ivec2(gl_GlobalInvocationID.xy);
    int index = cell.y * 3 + cell.x;
    vec2 horizontal = vec2(cell) - 1.0;
    vec2 source = horizontal;
    for (int i = 0; i < 8; ++i) {
        vec2 chop = vec2(0.0);
        for (int layer = 0; layer < 4; ++layer) if ((p.mode.x & (1u << layer)) != 0u) chop += textureLod(displacement_map, vec3(fract(p.layer[layer].xy + source / p.layer[layer].zw), float(layer)), p.lods[layer]).xz;
        source = horizontal - chop * (p.params.x * p.params.y * geometry_fade(source) * shoreline_factors(source).y);
        // The controller's cache certificate covers this supported source
        // bound. Extreme waves fall back conservatively instead of reusing a
        // cache whose shoreline footprint cannot be certified.
        if (length(source) > 190.0) { result.value[index] = vec4(0.0, 1000000.0, 0.0, 0.0); return; }
    }
    vec3 sum_d = vec3(0.0); for (int layer = 0; layer < 4; ++layer) if ((p.mode.x & (1u << layer)) != 0u) sum_d += textureLod(displacement_map, vec3(fract(p.layer[layer].xy + source / p.layer[layer].zw), float(layer)), p.lods[layer]).xyz;
    float fade = geometry_fade(source);
    vec2 shore = shoreline_factors(source);
    vec2 residual = source + sum_d.xz * (p.params.x * p.params.y * fade * shore.y) - horizontal;
    result.value[index] = vec4(sum_d.y * p.params.x * fade * shore.x, length(residual), 0.0, 0.0);
}
