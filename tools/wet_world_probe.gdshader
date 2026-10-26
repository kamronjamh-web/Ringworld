shader_type canvas_item;
render_mode unshaded;
// Probe of materials/ring_wetness.gdshaderinc and ring_rain_ripples.gdshaderinc for tools/wet_world_gpu_test.gd. One pixel per probe point (index = y * 64 + x of the 64-wide viewport).
// mode 0: ring_wet_sample at (world, normal, up) -> (film, soak, standing, rain)
// mode 1: ring_wet_puddle with standing = params.x, cavity = params.y, view distance = params.z, plane = world.xz -> (mask, rim, flatness, 0)
// mode 2: ring_wet_fat_drop_spots at plane = world.xz, view distance = params.z -> (spot, 0, 0, 0)
// mode 3: ring_rain_ripple_gradient at plane = world.xz with rain = params.x, fat = params.y, view distance = params.z, hail = params.w -> (gradient.xy, length, 0)
#include "res://materials/ring_wetness.gdshaderinc"

uniform int probe_mode = 0;
uniform vec3 probe_world[512];
uniform vec3 probe_normal[512];
uniform vec3 probe_up = vec3(0.0, 1.0, 0.0);
uniform vec4 probe_params = vec4(0.0);
uniform bool probe_alpha_pass = false; // write the fourth result channel as grey (canvas alpha cannot carry data)

void fragment() {
	int index = int(FRAGCOORD.y) * 64 + int(FRAGCOORD.x);
	vec3 world = probe_world[index];
	vec3 normal = probe_normal[index];
	vec4 result = vec4(0.0);
	if (probe_mode == 0) {
		RingWet w = ring_wet_sample(world, normal, probe_up);
		result = vec4(w.film, w.soak, w.standing, w.rain);
	} else if (probe_mode == 1) {
		RingWet w = RingWet(1.0, 1.0, probe_params.x, 1.0);
		float flatness = ring_wet_flatness(normal, probe_up);
		vec2 puddle = ring_wet_puddle(w, world.xz, flatness, probe_params.y, probe_params.z);
		result = vec4(puddle, flatness, 0.0);
	} else if (probe_mode == 2) {
		result = vec4(ring_wet_fat_drop_spots(world.xz, probe_params.z), 0.0, 0.0, 0.0);
	} else {
		vec2 g = ring_rain_ripple_gradient(world.xz, probe_params.x, probe_params.y, probe_params.w, probe_params.z);
		result = vec4(g, length(g), 0.0);
	}
	COLOR = probe_alpha_pass ? vec4(vec3(result.a), 1.0) : vec4(result.rgb, 1.0);
}
