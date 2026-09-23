shader_type spatial;

render_mode blend_add, cull_back, unshaded, depth_draw_never;

uniform float time = 0.0;
uniform vec3 sun_direction = vec3(0.30, 0.55, 0.75);

varying vec3 world_normal;

void vertex()
{
    world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment()
{
    float across_edge = pow(abs(UV.y * 2.0 - 1.0), 5.0);
    float rim = pow(1.0 - max(0.0, dot(normalize(NORMAL), normalize(VIEW))), 3.0);
    float variation = 0.85 + 0.15 * sin(UV.x * TAU * 3.0 + time * 0.015);
    float daylight = 0.25 + 0.75 * max(dot(normalize(world_normal), normalize(sun_direction)), 0.0);

    ALBEDO = vec3(0.08, 0.25, 0.52) * variation * daylight;
    ALPHA = across_edge * rim * 0.20;
}
