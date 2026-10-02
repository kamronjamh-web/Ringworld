// Per-tile RGBA8 modifier atlas. RGB is source_color sRGB tint; A is blend weight.
uniform sampler2DArray grass_modifier_atlas : source_color, repeat_disable, filter_linear;
uniform bool grass_modifier_enabled = false;
uniform float grass_modifier_tint_strength = 1.0;
instance uniform int grass_modifier_layer = -1;
instance uniform vec3 grass_modifier_origin_world;
instance uniform vec4 grass_modifier_around_world;
instance uniform vec4 grass_modifier_across_world;

vec4 grass_modifier_tint(vec3 root_world) {
    if (!grass_modifier_enabled || grass_modifier_layer < 0) return vec4(0.0);
    vec3 delta = root_world - grass_modifier_origin_world;
    vec2 uv = vec2(0.5) + vec2(dot(delta, grass_modifier_around_world.xyz) * grass_modifier_around_world.w, dot(delta, grass_modifier_across_world.xyz) * grass_modifier_across_world.w);
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return vec4(0.0);
    vec4 sample_value = texture(grass_modifier_atlas, vec3(uv, float(grass_modifier_layer)));
    sample_value.a *= grass_modifier_tint_strength;
    return sample_value;
}
