shader_type spatial;

render_mode blend_mix, cull_back, diffuse_burley, specular_schlick_ggx;

uniform float time = 0.0;
uniform float coverage = 0.36;

float hash21(vec2 p)
{
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float value_noise(vec2 p)
{
    vec2 cell = floor(p);
    vec2 fraction = fract(p);
    fraction = fraction * fraction * (3.0 - 2.0 * fraction);
    float a = hash21(cell);
    float b = hash21(cell + vec2(1.0, 0.0));
    float c = hash21(cell + vec2(0.0, 1.0));
    float d = hash21(cell + vec2(1.0, 1.0));
    return mix(mix(a, b, fraction.x), mix(c, d, fraction.x), fraction.y);
}

float periodic_noise(vec2 uv)
{
    float around = uv.x * TAU;
    vec2 ring_coordinate = vec2(cos(around), sin(around)) * 5.7;
    vec2 p = ring_coordinate + vec2(uv.y * 3.8, uv.y * 11.3);
    return value_noise(p) * 0.52 + value_noise(p * 2.03) * 0.25
        + value_noise(p * 4.11) * 0.15 + value_noise(p * 8.17) * 0.08;
}

void fragment()
{
    vec2 drift = vec2(time * 0.00002, time * 0.000006);
    float cloud_field = periodic_noise(UV + drift);
    float wisps = periodic_noise(UV * vec2(1.0, 2.5) + drift * 1.6);
    float alpha = smoothstep(1.0 - coverage, 1.0, mix(cloud_field, wisps, 0.28)) * 0.46;
    ALBEDO = vec3(0.62, 0.70, 0.78);
    ROUGHNESS = 0.94;
    ALPHA = alpha;
}
