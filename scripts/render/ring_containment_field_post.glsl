#[vertex]
#version 450
// Containment field near patch, post-Sunshine raster pass (RingContainmentFieldComposite.gd; docs/containment-field-design.md 3.7, decision 7 of 2026-11-04).
// Used only while the camera is ABOVE the field (outside the contained volume): every cloud is then behind the field, and Sunshine, which composites after
// the transparent pass, would overwrite the shimmer. Same patch, same attributes and the same per-fragment maths as materials/ring_containment_field.gdshader
// (materials/ring_containment_field_core.gdshaderinc), drawn onto the resolved colour after Sunshine, sampling a copy of it.
// One instance per patch quad, six vertices each; vertex records in a storage buffer (3 vec4 per vertex, index = j * (Q + 1) + i).
layout(set = 0, binding = 0, std140) uniform Frame {
	mat4 proj; mat4 inv_proj;
	vec4 cam_x; vec4 cam_y; vec4 cam_z;
	vec4 anchor_rel;   // xyz: patch anchor minus camera position (world), w: quads per side
	vec4 viewport;     // xy internal size px, z max offset px, w debug mode
	vec4 params;       // x shimmer strength, y sheen strength, z range m, w haze visibility
	vec4 noise;        // per-octave noise origins (m)
	vec4 lattice;      // x lattice strength, yz lattice origin (m)
} F;
struct PatchVertex { vec4 a; vec4 b; vec4 c; };   // a: position (anchor-relative) + uv2.x, b: normal + uv.x, c: around tangent + uv.y
layout(set = 0, binding = 2, std430) restrict readonly buffer Vertices { PatchVertex v[]; } S;

layout(location = 0) out vec3 v_view;
layout(location = 1) out vec3 v_normal;
layout(location = 2) out vec3 v_tangent;
layout(location = 3) out vec3 v_uv;   // xy noise uv, z root distance

const ivec2 CORNERS[6] = ivec2[6](ivec2(0, 0), ivec2(1, 0), ivec2(0, 1), ivec2(1, 0), ivec2(1, 1), ivec2(0, 1));

void main() {
	int quads = max(int(F.anchor_rel.w + 0.5), 1);
	int q = gl_InstanceIndex;
	ivec2 c = ivec2(q % quads, q / quads) + CORNERS[gl_VertexIndex % 6];
	PatchVertex pv = S.v[c.y * (quads + 1) + c.x];
	vec3 rel = F.anchor_rel.xyz + pv.a.xyz;
	vec3 view = vec3(dot(rel, F.cam_x.xyz), dot(rel, F.cam_y.xyz), dot(rel, F.cam_z.xyz));
	vec3 n = pv.b.xyz, t = pv.c.xyz;
	v_view = view;
	v_normal = vec3(dot(n, F.cam_x.xyz), dot(n, F.cam_y.xyz), dot(n, F.cam_z.xyz));
	v_tangent = vec3(dot(t, F.cam_x.xyz), dot(t, F.cam_y.xyz), dot(t, F.cam_z.xyz));
	v_uv = vec3(pv.b.w, pv.c.w, pv.a.w);
	gl_Position = F.proj * vec4(view, 1.0);
}

#[fragment]
#version 450
layout(set = 0, binding = 0, std140) uniform Frame {
	mat4 proj; mat4 inv_proj;
	vec4 cam_x; vec4 cam_y; vec4 cam_z;
	vec4 anchor_rel;
	vec4 viewport;
	vec4 params;
	vec4 noise;
	vec4 lattice;
} F;
layout(set = 0, binding = 1) uniform sampler2D scene_depth;
layout(set = 0, binding = 3) uniform sampler2D scene_copy;   // the resolved colour after Sunshine, copied by ring_containment_field_copy.glsl
layout(location = 0) in vec3 v_view;
layout(location = 1) in vec3 v_normal;
layout(location = 2) in vec3 v_tangent;
layout(location = 3) in vec3 v_uv;
layout(location = 0) out vec4 out_color;

#include "../../materials/ring_containment_field_core.gdshaderinc"

float view_depth_at(vec2 uv) {
	ivec2 p = clamp(ivec2(uv * F.viewport.xy), ivec2(0), ivec2(F.viewport.xy) - 1);
	float raw = texelFetch(scene_depth, p, 0).r;
	if (raw <= 0.0) return 1.0e30;
	vec4 sv = F.inv_proj * vec4(uv * 2.0 - 1.0, raw, 1.0);
	return sv.w != 0.0 ? -sv.z / sv.w : 1.0e30;
}

void main() {
	float dist = length(v_view);
	vec3 to_camera = -v_view / max(dist, 0.0001);
	vec3 n = normalize(v_normal);
	float ndv = abs(dot(n, to_camera));
	float frag_depth = -v_view.z;
	vec2 screen_uv = gl_FragCoord.xy / F.viewport.xy;
	float shimmer = F.params.x, sheen_strength = F.params.y, range_m = F.params.z;
	float base = cf_distance_weight(dist, range_m) * cf_grazing_fade(ndv) * cf_root_ramp(v_uv.z);
	base *= cf_soft_intersection(view_depth_at(screen_uv) - frag_depth);
	base *= F.params.w;
	float w_refraction = shimmer * base;
	float w_sheen = sheen_strength * base;
	float w_lattice = F.lattice.x * base;
	float a = cf_alpha(w_refraction, w_sheen, w_lattice);
	vec2 lattice_p = v_uv.xy + F.lattice.yz;
	vec2 lattice_fw = fwidth(lattice_p);
	if (a < 1.0 / 1024.0) discard;
	float lattice = cf_lattice_mask(lattice_p, max(lattice_fw.x, lattice_fw.y));
	int debug_mode = int(F.viewport.w + 0.5);
	if (debug_mode > 0) {
		out_color = vec4(cf_debug_colour(debug_mode, a, dist, range_m, lattice * w_lattice) * a, a);
		return;
	}
	vec3 ta = normalize(v_tangent);
	vec3 tb = normalize(cross(n, ta));
	float max_px = F.viewport.z * max(1.0, F.viewport.y / 1080.0);
	vec2 offset = cf_offset_px(cf_shimmer_slope(v_uv.xy, F.noise), ta, tb, min(w_refraction, 1.0), max_px);
	vec2 uv = screen_uv + offset / F.viewport.xy;
	if (view_depth_at(uv) < frag_depth) uv = screen_uv;
	vec3 refracted = textureLod(scene_copy, uv, 0.0).rgb;
	out_color = vec4(refracted * cf_sheen_gain(w_sheen * cf_sheen_angle(ndv)) * cf_lattice_gain(w_lattice, lattice, 0.0) * a, a);
}
