#[vertex]
#version 450
// Containment field ripple patches, post-Sunshine raster pass (RingContainmentFieldComposite.gd; docs/containment-field-design.md 3.5, 3.7, 3.10.2; WO4).
// Used only while the camera is ABOVE the field (outside the contained volume): every cloud is then behind the field, and Sunshine, which composites after the
// transparent pass, would overwrite the spatial ripple patches (materials/ring_containment_ripple.gdshader). Same patches, same attributes and the same per-fragment
// maths as that shader, with haze = 1 (above the field the camera-to-patch path is in vacuum or thin air) and no own-storm occluder (every cloud is behind the field).
// One draw per active ripple slot (push constant = slot): one instance per patch quad, six vertices each.
// Vertex records: 16 slots at a fixed stride of 33 x 33 vertices, 3 vec4 per vertex (index = slot * 1089 + j * (Q + 1) + i), the near post's layout.
// Slot records (80 bytes): anchor minus camera + quads, wave, crawl, frame = (lattice origin xy, metric k, strike level), flags = (near cut 0/1, 0, 0, 0).
layout(set = 0, binding = 0, std140) uniform Frame {
	mat4 proj; mat4 inv_proj;
	vec4 cam_x; vec4 cam_y; vec4 cam_z;
	vec4 viewport;     // xy internal size px, z ripple max offset px at 1080 lines, w debug mode
	vec4 params;       // x glass strength, y lattice strength, z near range N m, w near patch active 0/1
	vec4 far_fade;     // xy (start, end) m of the far fade band
	vec4 to_sun;       // world, xyz; RingContainmentField.UpdateLighting's values, as the near post
	vec4 up;
	vec4 sun;
	vec4 sky;
	vec4 ground;
	vec4 flow;         // x seam flow phase, y seam energy scale
} F;
struct PatchVertex { vec4 a; vec4 b; vec4 c; };   // a: position (anchor-relative) + uv2.x, b: normal + uv.x, c: around tangent + uv.y
struct SlotRecord { vec4 anchor_rel; vec4 wave; vec4 crawl; vec4 frame; vec4 flags; };
layout(set = 0, binding = 2, std430) restrict readonly buffer Vertices { PatchVertex v[]; } S;
layout(set = 0, binding = 4, std430) restrict readonly buffer Slots { SlotRecord s[]; } SL;
layout(push_constant, std430) uniform Push { int slot; int pad0; int pad1; int pad2; } P;

layout(location = 0) out vec3 v_view;
layout(location = 1) out vec3 v_normal;
layout(location = 2) out vec3 v_tangent;
layout(location = 3) out vec3 v_uv;   // xy noise uv (m from the ripple centre), z root distance
layout(location = 4) flat out vec4 v_wave;
layout(location = 5) flat out vec4 v_crawl;
layout(location = 6) flat out vec4 v_frame;
layout(location = 7) flat out float v_near_cut;

const ivec2 CORNERS[6] = ivec2[6](ivec2(0, 0), ivec2(1, 0), ivec2(0, 1), ivec2(1, 0), ivec2(1, 1), ivec2(0, 1));
const int SLOT_STRIDE = 33 * 33;

void main() {
	SlotRecord sr = SL.s[P.slot];
	int quads = max(int(sr.anchor_rel.w + 0.5), 1);
	int q = gl_InstanceIndex;
	ivec2 c = ivec2(q % quads, q / quads) + CORNERS[gl_VertexIndex % 6];
	PatchVertex pv = S.v[P.slot * SLOT_STRIDE + c.y * (quads + 1) + c.x];
	vec3 rel = sr.anchor_rel.xyz + pv.a.xyz;
	vec3 view = vec3(dot(rel, F.cam_x.xyz), dot(rel, F.cam_y.xyz), dot(rel, F.cam_z.xyz));
	vec3 n = pv.b.xyz, t = pv.c.xyz;
	v_view = view;
	v_normal = vec3(dot(n, F.cam_x.xyz), dot(n, F.cam_y.xyz), dot(n, F.cam_z.xyz));
	v_tangent = vec3(dot(t, F.cam_x.xyz), dot(t, F.cam_y.xyz), dot(t, F.cam_z.xyz));
	v_uv = vec3(pv.b.w, pv.c.w, pv.a.w);
	v_wave = sr.wave;
	v_crawl = sr.crawl;
	v_frame = sr.frame;
	v_near_cut = sr.flags.x;
	gl_Position = F.proj * vec4(view, 1.0);
}

#[fragment]
#version 450
layout(set = 0, binding = 0, std140) uniform Frame {
	mat4 proj; mat4 inv_proj;
	vec4 cam_x; vec4 cam_y; vec4 cam_z;
	vec4 viewport;
	vec4 params;
	vec4 far_fade;
	vec4 to_sun;
	vec4 up;
	vec4 sun;
	vec4 sky;
	vec4 ground;
	vec4 flow;
} F;
layout(set = 0, binding = 1) uniform sampler2D scene_depth;
layout(set = 0, binding = 3) uniform sampler2D scene_copy;   // the resolved colour after Sunshine, copied by ring_containment_field_copy.glsl
layout(location = 0) in vec3 v_view;
layout(location = 1) in vec3 v_normal;
layout(location = 2) in vec3 v_tangent;
layout(location = 3) in vec3 v_uv;
layout(location = 4) flat in vec4 v_wave;
layout(location = 5) flat in vec4 v_crawl;
layout(location = 6) flat in vec4 v_frame;
layout(location = 7) flat in float v_near_cut;
layout(location = 0) out vec4 out_color;

#include "../../materials/ring_containment_strike.gdshaderinc"
#include "../../materials/ring_containment_field_core.gdshaderinc"

float view_depth_at(vec2 uv) {
	ivec2 p = clamp(ivec2(uv * F.viewport.xy), ivec2(0), ivec2(F.viewport.xy) - 1);
	float raw = texelFetch(scene_depth, p, 0).r;
	if (raw <= 0.0) return 1.0e30;
	vec4 sv = F.inv_proj * vec4(uv * 2.0 - 1.0, raw, 1.0);
	return sv.w != 0.0 ? -sv.z / sv.w : 1.0e30;
}

vec3 to_view(vec3 world_dir) { return vec3(dot(world_dir, F.cam_x.xyz), dot(world_dir, F.cam_y.xyz), dot(world_dir, F.cam_z.xyz)); }

void main() {
	float dist = length(v_view);
	vec3 to_camera = -v_view / max(dist, 0.0001);
	vec3 n = normalize(v_normal);
	vec3 ta = normalize(v_tangent);
	vec3 tb = normalize(cross(ta, n));   // the increasing-arch tangent
	float ndv = abs(dot(n, to_camera));
	float frag_depth = -v_view.z;
	vec2 screen_uv = gl_FragCoord.xy / F.viewport.xy;
	float fades = cf_grazing_fade(ndv) * cf_root_ramp(v_uv.z);
	fades *= cf_soft_intersection(view_depth_at(screen_uv) - frag_depth);
	fades *= 1.0 - smoothstep(F.far_fade.x, F.far_fade.y, dist);
	if (v_near_cut > 0.5 && F.params.w > 0.5) fades *= 1.0 - cf_distance_weight(dist, F.params.z);   // the near patch draws that part
	if (fades < 1.0 / 32768.0) discard;

	vec2 d = v_uv.xy * vec2(v_frame.z, 1.0);
	vec2 fwd = fwidth(d);
	float pixel_m = max(fwd.x, fwd.y);
	vec2 slope = vec2(0.0);
	float weight = 0.0;
	float boost = 0.0;
	vec3 light = vec3(0.0);
	cf_ripple_eval(d, pixel_m, v_wave, v_crawl, v_frame.w, slope, weight, boost, light);
	float w = weight * fades;
	light *= fades;
	if (w < 1.0 / 1024.0 && max(light.x, max(light.y, light.z)) < 1.0 / 4096.0) discard;
	float a = clamp(4.0 * w, 0.0, 1.0);

	int debug_mode = int(F.viewport.w + 0.5);
	if (debug_mode > 0) {
		out_color = vec4(vec3(1.0, 0.0, 1.0) * a + light, a);
		return;
	}
	vec2 lattice_p = v_uv.xy + v_frame.xy;
	vec2 lattice_fw = fwidth(lattice_p);
	float lattice_pixel = max(lattice_fw.x, lattice_fw.y);
	vec4 seam = cf_seam(lattice_p, lattice_pixel);
	float scale = max(1.0, F.viewport.y / 1080.0);
	vec2 offset = cf_ripple_offset_px(slope, ta, tb, fades, F.viewport.z * scale)
		+ cf_seam_offset_px(seam, ta, tb, F.params.y * w, scale);
	vec2 uv = screen_uv + offset / F.viewport.xy;
	// Never pull colour from something in front of the field (no halo around the wall top or a POI).
	if (view_depth_at(uv) < frag_depth) uv = screen_uv;
	vec3 refracted = textureLod(scene_copy, uv, 0.0).rgb;
	vec3 shade = cf_glass_shade(refracted, n, ta, tb, to_camera, normalize(to_view(F.to_sun.xyz)), normalize(to_view(F.up.xyz)), F.sun.xyz, F.sky.xyz, F.ground.xyz,
		F.params.x * w, F.params.y * w, seam, cf_seam_energy_filtered(lattice_p, lattice_pixel, F.flow.x), F.flow.y, boost * fades, 1.0);
	out_color = vec4(shade * a + light, a);
}
