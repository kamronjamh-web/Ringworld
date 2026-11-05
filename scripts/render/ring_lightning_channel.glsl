#[vertex]
#version 450
// Megastorm lightning channels (post-cloud raster pass). One instance per world-space bolt segment (storage buffer, std430 records of 3 vec4), six vertices each:
// a screen-space capsule quad built like capsule_quad in ring_rain_streaks.glsl, never thinner than the pixel minimums so a km-scale bolt stays visible at 5-50 km.
// Drawn by RingLightningChannelComposite.gd after the Sunshine cloud composite, so the bolt is not replaced by cloud that lies behind it.
layout(set = 0, binding = 0, std140) uniform Frame {
	mat4 proj; mat4 inv_proj;
	vec4 cam_x; vec4 cam_y; vec4 cam_z; vec4 cam_pos;
	vec4 viewport;      // x,y: internal size in px; z,w: Sunshine min / max step distance
	vec4 sun_flags;     // x: 1 when the Sunshine textures are bound
	vec4 bolt[4];       // per-slot intensity (pulse): 0..7 bolts, 8..15 scythe cores
	vec4 tip[4];        // per-slot travelling tip fraction (scythe cores); bolt slots: stroke index 0..2
	vec4 radiance;      // x core radiance, y glow radiance, z core width (m), w glow width (m)
	vec4 min_px;        // x, y: multipliers on the distance-scaled minimum core / glow pixel widths
	vec4 colour;        // rgb core colour
	vec4 glow_colour;   // rgb glow colour
} F;
struct Segment { vec4 a; vec4 b; vec4 c; };   // a: A.xyz + width, b: B.xyz + width, c: x intensity, y slot, z/w along fraction of A / B (scythe cores)
layout(set = 0, binding = 2, std430) restrict readonly buffer Segments { Segment seg[]; } S;

layout(location = 0) out float v_u;
layout(location = 1) out vec3 v_view;
layout(location = 2) out vec3 v_px;               // x glow px, y core px, z unclamped core px
layout(location = 3) out float v_intensity;
layout(location = 4) out vec2 v_zone;             // field strikes: x channel height fraction, y class + 1 (0 = not a field strike)

const vec2 CORNERS[6] = vec2[6](vec2(-1, 0), vec2(1, 0), vec2(-1, 1), vec2(1, 0), vec2(1, 1), vec2(-1, 1));
void cull() { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); v_u = 0.0; v_view = vec3(0.0, 0.0, -1.0); v_px = vec3(1.0); v_intensity = 0.0; v_zone = vec2(0.0); }
vec3 to_view(vec3 world) { vec3 rel = world - F.cam_pos.xyz; return vec3(dot(rel, F.cam_x.xyz), dot(rel, F.cam_y.xyz), dot(rel, F.cam_z.xyz)); }

void main() {
	Segment sg = S.seg[gl_InstanceIndex];
	int bi = clamp(int(sg.c.y), 0, 15);
	float intensity = F.bolt[bi >> 2][bi & 3] * sg.c.x;
	if (!(intensity > 0.0)) { cull(); return; }
	vec3 pa = sg.a.xyz, pb = sg.b.xyz;
	float wA = max(sg.a.w, 0.05), wB = max(sg.b.w, 0.05);
	float alongA = sg.c.z, alongB = sg.c.w, tip = 1.0;
	bool field = false;
	float zoneA = 0.0, zoneB = 0.0, zoneClass = 0.0;
	if (bi < 8) {
		float stroke = F.tip[bi >> 2][bi & 3];
		field = stroke >= 9.5;
		if (field) {
			// Field strike (tip = 10 + leader progress): c.z is the height fraction of the segment start, floor(c.w) the class (0 trunk, 1 fan branch, 2 spray) and
			// its fraction the end height of a trunk piece. The leader reveals what starts below its tip and cuts the trunk piece it is inside.
			float st = stroke - 10.0;
			float stroke_index = floor(st * 0.5);
			float prog = st - 2.0 * stroke_index;   // tip = 10 + progress + 2 x return stroke index
			zoneClass = floor(alongB + 1.0e-4);
			float endf = alongB - zoneClass;
			if (alongA > prog) { cull(); return; }
			zoneA = alongA; zoneB = alongA;
			if (zoneClass < 0.5) {
				zoneB = endf;
				if (endf > prog) { float t = (prog - alongA) / max(endf - alongA, 1e-6); pb = mix(pa, pb, t); wB = mix(wA, wB, t); zoneB = prog; }
			}
			intensity *= 0.3;   // field strikes are tens of km long: a lower radiance than a ground bolt keeps the channel blue instead of white
			// Branch-level flicker: each fan branch re-rolls on every return stroke.
			if (zoneClass > 0.5 && zoneClass < 1.5 && fract(sin(alongA * 91.7 + stroke_index * 17.3) * 43758.5453) <= 0.4) intensity *= 0.12;
			// The growing head is brighter than the channel behind it while the leader rises.
			if (prog < 0.999 && zoneClass < 0.5) intensity *= 1.0 + 0.8 * smoothstep(0.85, 1.0, zoneB / max(prog, 1e-3));
		} else {
			// Branch / run segments (c.w > 0.5) flicker per stroke: c.z is the per-branch hash, tip[] the stroke index.
			if (alongB > 0.5 && fract(sin(alongA * 91.7 + stroke * 17.3) * 43758.5453) <= 0.35) intensity *= 0.15;
		}
	}
	if (bi >= 8) {
		// Scythe core: the whole line is uploaded once; the travelling tip cuts it here.
		tip = F.tip[bi >> 2][bi & 3];
		if (alongA >= tip) { cull(); return; }
		if (alongB > tip) { float t = (tip - alongA) / max(alongB - alongA, 1e-6); pb = mix(pa, pb, t); wB = mix(wA, wB, t); alongB = tip; }
	}
	vec3 vA = to_view(pa), vB = to_view(pb);
	float zn = -0.08;
	if (vA.z > zn && vB.z > zn) { cull(); return; }
	if (vA.z > zn) { float t = (zn - vA.z) / (vB.z - vA.z); vA = mix(vA, vB, t); wA = mix(wA, wB, t); }
	else if (vB.z > zn) { float t = (zn - vB.z) / (vA.z - vB.z); vB = mix(vB, vA, t); wB = mix(wB, wA, t); }
	vec2 corner = CORNERS[gl_VertexIndex % 6];
	bool at_b = corner.y > 0.5;
	vec4 cA = F.proj * vec4(vA, 1.0), cB = F.proj * vec4(vB, 1.0);
	vec2 ndcA = cA.xy / cA.w, ndcB = cB.xy / cB.w;
	vec2 sd = (ndcB - ndcA) * 0.5 * F.viewport.xy;
	float sdl = length(sd);
	vec2 dir = sdl > 1.0e-4 ? sd / sdl : vec2(0.0, 1.0);
	vec2 perp = vec2(-dir.y, dir.x);
	vec3 v = at_b ? vB : vA;
	float wrel = at_b ? wB : wA;
	float scale = max(1.0, F.viewport.y / 1080.0);
	float factor = F.proj[1][1] * 0.5 * F.viewport.y / max(-v.z, 0.1);
	float dview = max(-v.z, 0.1);
	float min_core = mix(1.4, 0.85, smoothstep(1500.0, 20000.0, dview)) * scale * F.min_px.x;
	float min_glow = mix(9.0, 3.0, smoothstep(1000.0, 25000.0, dview)) * scale * F.min_px.y;
	float glow_px = max(F.radiance.w * mix(0.6, 1.0, clamp(wrel, 0.0, 1.0)) * factor, min_glow * (field ? 0.55 : 1.0));
	float core_true = F.radiance.z * wrel * factor;
	float core_px = max(core_true, min_core);
	vec2 off_px = perp * corner.x * glow_px * 0.5 + (at_b ? dir : -dir) * glow_px * 0.5;
	vec4 c = at_b ? cB : cA;
	gl_Position = vec4((c.xy / c.w + off_px * 2.0 / F.viewport.xy) * c.w, c.z, c.w);
	v_u = corner.x;
	v_view = v;
	v_px = vec3(glow_px, core_px, core_true);
	v_intensity = bi >= 8 ? intensity * ((at_b ? alongB : alongA) >= 0.85 * tip ? 1.0 : 0.6) : intensity;
	v_zone = field ? vec2(at_b ? zoneB : zoneA, zoneClass + 1.0) : vec2(0.0);
}

#[fragment]
#version 450
layout(set = 0, binding = 0, std140) uniform Frame {
	mat4 proj; mat4 inv_proj;
	vec4 cam_x; vec4 cam_y; vec4 cam_z; vec4 cam_pos;
	vec4 viewport;
	vec4 sun_flags;
	vec4 bolt[4];
	vec4 tip[4];
	vec4 radiance;
	vec4 min_px;
	vec4 colour;
	vec4 glow_colour;
} F;
layout(set = 0, binding = 1) uniform sampler2D scene_depth;
layout(set = 0, binding = 3) uniform sampler2D sun_data;    // r first cloud distance, g travelled distance, b cloud depth
layout(set = 0, binding = 4) uniform sampler2D sun_color;   // a cloud density
layout(set = 0, binding = 5) uniform sampler2D far_depth;   // r: far-view opaque distance in metres (0 = none), Sunshine's far_opaque_depth_texture
layout(location = 0) in float v_u;
layout(location = 1) in vec3 v_view;
layout(location = 2) in vec3 v_px;
layout(location = 3) in float v_intensity;
layout(location = 4) in vec2 v_zone;
layout(location = 0) out vec4 out_color;

void main() {
	float d = texelFetch(scene_depth, ivec2(gl_FragCoord.xy), 0).r;
	if (d > 0.0 && v_zone.y < 0.5) {   // field strikes are high in the sky: no terrain depth test (cloud in front still dims them through Sunshine's data below)
		vec2 ndc = gl_FragCoord.xy / F.viewport.xy * 2.0 - 1.0;
		vec4 sv = F.inv_proj * vec4(ndc, d, 1.0);
		if (sv.z / sv.w > v_view.z + 1.0) discard;   // terrain in front (1 m bias suits km-scale bolts)
	}
	float glow_px = v_px.x, core_px = v_px.y, core_true_px = v_px.z;
	float dpx = abs(v_u) * glow_px * 0.5;
	float core = exp(-0.5 * pow(dpx / max(core_px * 0.5, 0.35), 2.0)) * clamp(core_true_px / core_px, 0.25, 1.0);
	float g = max(glow_px * 0.18, 0.5);
	float glow = (1.0 / (1.0 + (dpx / g) * (dpx / g))) * (1.0 - smoothstep(0.7, 1.0, abs(v_u)));

	// Cloud lying in front of the bolt, by Sunshine's own rule for an opaque object at Euclidean distance L
	// (addons/SunshineClouds2/SunshineCloudsPostCompute.comp lines ~425-436).
	float L = length(v_view);
	float far_d = texture(far_depth, gl_FragCoord.xy / F.viewport.xy).r;
	if (v_zone.y < 0.5 && far_d > 0.0 && far_d < L - 1.0) discard;   // terrain or trees drawn by the scaled far view in front of the bolt (not for field strikes: their channels are high in the sky)
	vec2 uv = gl_FragCoord.xy / F.viewport.xy;
	vec4 data = texture(sun_data, uv);
	float dens = clamp(texture(sun_color, uv).a, 0.0, 1.0);
	float minstep = F.viewport.z, maxstep = F.viewport.w;
	float lerpv = 0.0;
	if (data.r > L) lerpv = 1.0;
	else if (data.g > L) {
		float ftb = mix(1.0 - clamp((data.g - data.r) / minstep, 0.0, 1.0), 1.0, clamp(data.r / maxstep, 0.0, 1.0));
		lerpv = clamp((data.g - L) / max(data.g - data.r, 1e-3), 0.0, 1.0) * ftb;
	}
	float front = F.sun_flags.x > 0.5 ? dens * (1.0 - lerpv) * smoothstep(minstep, minstep * 2.0, L) : 0.0;
	float vis = 1.0 - front;
	float dist_fade = mix(1.0, 0.4, smoothstep(5000.0, 60000.0, L));
	vec3 core_colour = F.colour.rgb, glow_colour = F.glow_colour.rgb;
	if (v_zone.y > 0.5) {
		// Field strike: white-blue leader (lower 30%), deep blue upper trunk and fan, violet-white terminal spray. Air dims it slowly (it is km above the haze).
		float cls = v_zone.y - 1.0;
		float up = smoothstep(0.22, 0.38, v_zone.x);
		core_colour = mix(vec3(0.92, 0.96, 1.0), vec3(0.45, 0.62, 1.0), up);
		glow_colour = mix(vec3(0.55, 0.70, 1.0), vec3(0.18, 0.30, 1.0), up);
		if (cls > 1.5) { core_colour = vec3(0.90, 0.88, 1.0); glow_colour = vec3(0.72, 0.40, 1.0); }
		dist_fade = mix(1.0, 0.55, smoothstep(20000.0, 200000.0, L));
	}
	vec3 rgb = v_intensity * (core_colour * core * F.radiance.x + glow_colour * glow * F.radiance.y) * vis * dist_fade;
	if (any(isnan(rgb)) || any(isinf(rgb))) rgb = vec3(0.0);
	out_color = vec4(clamp(rgb, vec3(0.0), vec3(60000.0)), 0.0);
}
