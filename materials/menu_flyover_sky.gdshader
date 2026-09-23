shader_type sky;

uniform vec3 camera_origin_km = vec3(0.0, 20.0, 0.0);
uniform float Rkm = 5000.0;
uniform float ring_width_km = 318.0;
uniform vec3 sun_direction = vec3(-0.4, 0.75, -0.5);

float sky_hash(vec2 p)
{
    return fract(sin(dot(p, vec2(41.73, 289.21))) * 143758.5453);
}

void sky()
{
    vec3 ray = normalize(EYEDIR);
    vec3 space = mix(vec3(0.002, 0.004, 0.010), vec3(0.025, 0.045, 0.085), clamp(ray.y * .5 + .5, 0.0, 1.0));
    // Sparse, fixed stars: one cell lookup, restricted to the outward sky.
    vec2 star_cell = floor(ray.xy * 430.0 + ray.z * vec2(137.0, 271.0));
    float star = step(0.9975, sky_hash(star_cell)) * smoothstep(-0.12, 0.45, ray.y);
    space += vec3(0.20, 0.25, 0.36) * star;
    COLOR = space;
    // Actual seeded far-terrain geometry now occludes the stars. Do not paint
    // a fog-colored substitute over the distant ring.
}
