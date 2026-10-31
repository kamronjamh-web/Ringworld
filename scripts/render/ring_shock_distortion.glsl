#[compute]
#version 450
// Megastorm shock distortion (RingShockDistortionComposite.gd, docs/megastorm-design.md 5.5.1). Refraction only: no brightening, so a dark wall stays dark.
layout(local_size_x = 8, local_size_y = 8) in;
layout(rgba16f, binding = 0) uniform image2D scene_color;
layout(binding = 1) uniform sampler2D scene_depth;
layout(rgba16f, binding = 2) uniform image2D scene_tmp;   // own storage texture: pass 0 writes the refracted frame here, pass 1 copies it back (the engine colour texture cannot be copy-read)
layout(push_constant, std430) uniform Push { uint pass_index; uint pad0; uint pad1; uint pad2; } pc;
layout(std140, binding = 3) uniform Frame {
	mat4 inv_proj;
	mat4 cam;           // camera world transform
	vec4 band_n;        // xyz: unit direction of travel (world), w: front offset along it from the origin (m)
	vec4 origin;        // xyz: origin (world), w: amplitude (px at the internal resolution)
	vec4 band;          // x: lens half width (m), y: wake width (m)
} frame;

const float FAR_METERS = 6000.0;

void main() {
	ivec2 p = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = imageSize(scene_color);
	if (any(greaterThanEqual(p, size))) return;
	if (pc.pass_index == 1u) { imageStore(scene_color, p, imageLoad(scene_tmp, p)); return; }
	vec4 here = imageLoad(scene_color, p);
	vec2 uv = (vec2(p) + 0.5) / vec2(size);
	float depth = texelFetch(scene_depth, p, 0).r;
	vec4 ray = frame.inv_proj * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
	vec3 dir = normalize(ray.xyz / ray.w);
	float dist = FAR_METERS;
	if (depth > 0.0) {
		vec4 sv = frame.inv_proj * vec4(uv * 2.0 - 1.0, depth, 1.0);
		if (sv.w > 0.0) dist = min(length(sv.xyz / sv.w), FAR_METERS);
	}
	vec3 world = (frame.cam * vec4(dir * dist, 1.0)).xyz;
	float s = frame.band_n.w - dot(world - frame.origin.xyz, frame.band_n.xyz);   // metres behind the front (negative = still ahead)
	float w = max(frame.band.x, 1.0);
	float core = exp(-(s / w) * (s / w));
	float tail = exp(-(s / frame.band.y) * (s / frame.band.y));
	float lens = core * sin(s / w * 2.4) + 0.3 * tail * sin(s * 0.09);
	float amp = frame.origin.w * lens;
	if (abs(amp) < 0.02) { imageStore(scene_tmp, p, here); return; }
	// Shift along the projected direction of travel (view space x right, y up; image y runs down).
	vec3 n_view = transpose(mat3(frame.cam)) * frame.band_n.xyz;
	vec2 sdir = n_view.xy;
	float len = length(sdir);
	sdir = len > 1.0e-4 ? sdir / len : vec2(0.0, 1.0);
	// Manual bilinear read of the colour image at the shifted position.
	vec2 q = clamp(vec2(p) + amp * vec2(sdir.x, -sdir.y), vec2(0.0), vec2(size) - 1.001);
	ivec2 q0 = ivec2(floor(q));
	vec2 f = q - vec2(q0);
	vec3 c = mix(mix(imageLoad(scene_color, q0).rgb, imageLoad(scene_color, q0 + ivec2(1, 0)).rgb, f.x),
		mix(imageLoad(scene_color, q0 + ivec2(0, 1)).rgb, imageLoad(scene_color, q0 + ivec2(1, 1)).rgb, f.x), f.y);
	imageStore(scene_tmp, p, vec4(c, here.a));
}
