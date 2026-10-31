#[vertex]
#version 450
// Rain streaks and splashes (docs/storm-revamp-s1-design.md sections 2-3). Stateless: every instance is one hashed drop of a world-anchored
// lattice, its height comes from a CPU double-precision phase, so there is no state, no snapping pop, and a paused clock gives identical frames.
// Drawn by RingRainStreaksComposite.gd after the Sunshine cloud composite, so clouds can never overdraw near rain.
layout(push_constant, std430) uniform Push { uint mode; uint layer; uint pad0; uint pad1; } pc; // mode 0 streak, 1 splash; layer 0 near, 1 mid
// storm_level reads the storm-owned D (storm-light layer in ringworld_storm_map_frame.w, system-free heavy term; see ring_rain_streaks.glslinc).
#include "res://scripts/render/ring_rain_streaks.glslinc"

layout(location = 0) out vec4 v_color;
layout(location = 1) out vec3 v_uv;
layout(location = 2) out float v_viewz;
layout(location = 3) flat out float v_kind;

void cull() { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); v_color = vec4(0.0); v_uv = vec3(0.0); v_viewz = 0.0; v_kind = 0.0; }
vec3 to_view(vec3 world) { vec3 rel = world - F.cam_pos.xyz; return vec3(dot(rel, F.cam_x.xyz), dot(rel, F.cam_y.xyz), dot(rel, F.cam_z.xyz)); }
vec3 lit_radiance(vec3 world) { return min(streak_radiance(world) + F.lightning.rgb * F.light.w, vec3(4.0)); }

// HOOK for S2 wet world drips (S2 WO6): a third instance class drawn by this pass (push constant mode 2; RingRainStreaksComposite.gd draws counts[4] instances of counts[5] vertices
// after the splashes). Write the vertex here: gl_InstanceIndex is the drip id; use map_surface(), local_intensity(), streak_radiance(), to_view() and F.proj as the streak and splash code does,
// and set v_color (rgb radiance, a alpha), v_uv, v_viewz and v_kind = 3.0 (the fragment stage shades kind 3 with drip_alpha() in the fragment file). Until then it culls.
void drip_vertex() { cull(); }

// ---- S3 hail (docs/storm-revamp-s3-storm-arrival-design.md WO-C2): a drop class of this same lattice ----
const vec2 CORNERS[6] = vec2[6](vec2(-1, 0), vec2(1, 0), vec2(-1, 1), vec2(1, 0), vec2(1, 1), vec2(-1, 1));
// Screen-space capsule quad between two world points (the streak quad construction); false when it lies behind the near plane.
bool capsule_quad(vec3 head, vec3 tail, float width_m, int vi, out float comp, out float vz) {
	vec3 vA = to_view(head), vB = to_view(tail);
	float zn = -0.08;
	comp = 1.0; vz = 0.0;
	if (vA.z > zn && vB.z > zn) return false;
	if (vA.z > zn) vA = mix(vA, vB, (zn - vA.z) / (vB.z - vA.z));
	else if (vB.z > zn) vB = mix(vB, vA, (zn - vB.z) / (vA.z - vB.z));
	vec2 corner = CORNERS[vi % 6];
	bool at_tail = corner.y > 0.5;
	vec4 cA = F.proj * vec4(vA, 1.0), cB = F.proj * vec4(vB, 1.0);
	vec2 ndcA = cA.xy / cA.w, ndcB = cB.xy / cB.w;
	vec2 sd = (ndcB - ndcA) * 0.5 * F.viewport.xy;
	float sdl = length(sd);
	vec2 dir = sdl > 1.0e-4 ? sd / sdl : vec2(0.0, 1.0);
	vec2 perp = vec2(-dir.y, dir.x);
	vec3 v = at_tail ? vB : vA;
	float wtrue = width_m * F.proj[1][1] * 0.5 * F.viewport.y / max(-v.z, 0.1);
	float wpx = max(wtrue, 1.5 * max(1.0, F.viewport.y / 1080.0));   // pellets are never thinner than 1.5 px
	vec2 off_px = perp * corner.x * wpx * 0.5 + (at_tail ? dir : -dir) * wpx * 0.5;
	vec4 c = at_tail ? cB : cA;
	gl_Position = vec4((c.xy / c.w + off_px * 2.0 / F.viewport.xy) * c.w, c.z, c.w);
	comp = clamp(wtrue / wpx, 0.45, 1.0);
	vz = v.z;
	return true;
}

// A hail pellet: falls at 30 m/s with little slant, then hops (1-2 analytic hops of 0.2-0.6 m over 0.3-0.5 s) at the map landing height, so roofs and terrain both bounce it
// and nothing draws under a roof (the pellet's landing height is the roof).
void hail_pellet(bool is_near, vec2 cell2, vec3 origin, vec3 around, vec3 across, vec3 up, vec2 slope2, uint h1, float hp, float radius, float vis, vec3 radiance, int vi) {
	if (vis <= 0.001) { cull(); return; }
	vec2 sl2 = slope2 * HAIL_SLANT;
	vec3 travel = normalize(around * sl2.x + across * sl2.y - up);
	float yrel = hail_yrel(is_near, hp);
	float diam = mix(0.006, 0.025, clamp(F.hail.y, 0.0, 1.0)) * mix(0.6, 1.0, u01(hash_u(h1 ^ 0x7e57ab1eu)));
	float hop_h = mix(0.2, 0.6, u01(hash_u(h1 ^ 0x1badb002u)));
	float hop_t = mix(0.3, 0.5, u01(hash_u(h1 ^ 0x0ddba11u)));
	bool two = u01(hash_u(h1 ^ 0x5eed5eedu)) > 0.4;
	float total = two ? hop_t * 1.6 : hop_t;
	float yl;
	bool grounded = hail_landing(cell2, origin, around, across, up, sl2, yl);
	float period = is_near ? HAIL_NEAR_PERIOD : HAIL_MID_PERIOD;
	vec3 head, tail;
	float fade_age = 1.0;
	if (!grounded || yrel >= yl) {
		if (yrel > F.axis_across.w) { cull(); return; }
		vec2 pos2 = cell2 - sl2 * yrel;
		head = origin + around * pos2.x + across * pos2.y + up * yrel;
		tail = head - travel * 0.35;   // about 1/85 s of travel: a short motion trail, not a rain streak
		if (grounded) { float lc = (yrel - yl) / max(dot(-travel, up), 0.2); if (lc < 0.35) tail = head - travel * max(lc, 0.0); }
	} else {
		float age = (yl - yrel) / HAIL_FALL;
		float usable = min(total, (yl + 0.5 * period) / HAIL_FALL);   // the lattice column only reaches half a period below the observer plane
		if (age >= usable) { cull(); return; }
		float u, h, vy;
		if (age < hop_t) { u = age / hop_t; h = hop_h * 4.0 * u * (1.0 - u); vy = hop_h * 4.0 * (1.0 - 2.0 * u) / hop_t; }
		else { u = (age - hop_t) / (0.6 * hop_t); h = 0.35 * hop_h * 4.0 * u * (1.0 - u); vy = 0.35 * hop_h * 4.0 * (1.0 - 2.0 * u) / (0.6 * hop_t); }
		vec2 q = cell2 - sl2 * yl;
		vec3 c = origin + around * q.x + across * q.y + up * (yl + h + 0.5 * diam);
		float half_len = 0.5 * (diam + abs(vy) * 0.012);
		head = c + up * half_len; tail = c - up * half_len;
		fade_age = 1.0 - smoothstep(0.7, 1.0, age / usable);
	}
	float d_cam = length(head - F.cam_pos.xyz);
	float comp, vz;
	if (!capsule_quad(head, tail, diam, vi, comp, vz)) { cull(); return; }
	float cloud_base = F.axis_across.w;
	float fade_cb = 1.0 - smoothstep(cloud_base - 8.0, cloud_base, yrel + 0.35);
	float fade_cam = smoothstep(0.35, 0.7, d_cam);
	float fade_rad = 1.0 - smoothstep(0.7 * radius, radius, d_cam);
	float fade_in = is_near ? 1.0 : smoothstep(0.5 * F.near_cfg.z, F.near_cfg.z + 2.0, d_cam);
	v_color = vec4(min(radiance * 2.4, vec3(4.0)), clamp(F.rain.z * 1.8, 0.0, 1.0) * comp * fade_cam * fade_rad * fade_in * fade_age * vis * fade_cb);
	v_uv = vec3(CORNERS[vi % 6], 0.0);
	v_viewz = vz;
	v_kind = 4.0;
}

// The impact puff of a pellet: a short-lived camera-facing white puff at the landing point (roofs, rock and soil alike; the occlusion map carries no material).
void hail_puff(bool is_near, vec2 cell2, vec3 origin, vec3 around, vec3 across, vec3 up, vec2 slope2, uint h1, float hp, float radius, float vis, vec3 radiance, int vi) {
	if (vi >= 6 || vis <= 0.001 || u01(hash_u(h1 ^ 0x6a09e667u)) > 0.55) { cull(); return; }
	vec2 sl2 = slope2 * HAIL_SLANT;
	float yrel = hail_yrel(is_near, hp);
	float yl;
	if (!hail_landing(cell2, origin, around, across, up, sl2, yl)) { cull(); return; }
	float age = (yl - yrel) / HAIL_FALL;
	if (age < 0.0 || age > HAIL_PUFF_LIFE) { cull(); return; }
	float u = age / HAIL_PUFF_LIFE;
	vec2 q = cell2 - sl2 * yl;
	vec3 center = origin + around * q.x + across * q.y + up * (yl + 0.03);
	float d_cam = length(center - F.cam_pos.xyz);
	if (d_cam > radius) { cull(); return; }
	float r = (0.035 + 0.11 * sqrt(u)) * (0.7 + 0.6 * clamp(F.hail.y, 0.0, 1.0));
	vec2 cn = CORNERS[vi % 6];
	vec2 cc = vec2(cn.x, cn.y * 2.0 - 1.0);
	vec3 pos = center + (F.cam_x.xyz * cc.x + F.cam_y.xyz * cc.y) * r;
	vec3 v = to_view(pos);
	if (v.z > -0.08) { cull(); return; }
	gl_Position = F.proj * vec4(v, 1.0);
	float fade_cam = smoothstep(0.35, 0.7, length(v));
	float fade_rad = 1.0 - smoothstep(0.7 * radius, radius, d_cam);
	v_color = vec4(min(radiance * 1.8, vec3(4.0)), clamp(F.rain.z * 1.6, 0.0, 1.0) * (1.0 - u) * (1.0 - u) * 0.6 * fade_cam * fade_rad * vis);
	v_uv = vec3(cc, 0.0);
	v_viewz = v.z;
	v_kind = 5.0;
}

// ---- Megastorm bullet rain (docs/megastorm-design.md 5.5.2, WO11) ----
// Wind-aligned lattice: above a streak slope of 3 the vertical drop columns would leave the near footprint (a drop 20 m up sits 150 m upwind), so the lattice lives on the
// plane perpendicular to the drops' travel instead and a drop's phase runs along that travel. The cell and period sizes are the vertical lattice's, so the density per volume
// is unchanged. Sheltering: the top-down occlusion map cannot shade the lee of a wall from near-horizontal rain, so six taps upwind along the travel test the ray against
// the map surface (a 10 m wall shades about 80 m behind it at slope 7.5).
const float LEE_TAPS[6] = float[6](6.0, 16.0, 32.0, 56.0, 90.0, 140.0);
bool lee_blocked(vec3 head, vec3 travel, float up_comp, float yh, vec3 origin) {
	for (int i = 0; i < 6; i++) {
		float d = LEE_TAPS[i];
		float ys;
		if (map_surface(head - travel * d, origin, ys) && yh + up_comp * d < ys - 0.2) return true;
	}
	return false;
}

void aligned_streak(bool is_near, int vi) {
	vec4 cfg = is_near ? F.near_cfg : F.mid_cfg;
	vec4 cfg2 = is_near ? F.near_cfg2 : F.mid_cfg2;
	float cell = cfg.x; int n = int(cfg.y); float radius = cfg.z;
	int cells = n * n;
	int inst = gl_InstanceIndex;
	int slot = inst / cells;
	int k = inst - slot * cells;
	int di = k % n - n / 2; int dj = k / n - n / 2;
	int ci = int(is_near ? F.mega_u.w : F.mega_cells.x) + di;
	int cj = int(is_near ? F.mega_v.w : F.mega_cells.y) + dj;
	uint salt = uint(cfg2.y) + uint(slot) * 0x51ed27u;
	uint h0 = hash_cell(ci, cj, salt);
	uint h1 = hash_u(h0 ^ 0x68bc21ebu);
	float jx = u01(h0), jy = u01(h1);
	float hp = u01(hash_u(h1 + 0x02e5be93u));
	float keep = u01(hash_u(h1 ^ 0x1b873593u));
	float period = is_near ? F.near_cfg2.w : F.mid_origin.w;
	vec4 corner_phase = is_near ? F.mega_near_o : F.mega_mid_o;
	vec3 u = F.mega_u.xyz, v = F.mega_v.xyz, travel = F.mega_t.xyz;
	vec3 origin = F.near_origin.xyz;
	vec3 up = F.axis_up.xyz;
	vec2 cell2 = vec2(float(di) + jx, float(dj) + jy) * cell;
	float srel = fract((hp * period - corner_phase.w) / period + 0.5) * period - 0.5 * period;
	vec3 base_w = corner_phase.xyz + u * cell2.x + v * cell2.y;
	vec3 head = base_w - travel * srel;   // srel is measured against the travel (like yrel against down)
	float up_comp = dot(-travel, up);
	vec3 radiance = lit_radiance(base_w);
	float vis = smoothstep(0.0015, 0.03, dot(radiance, vec3(0.2126, 0.7152, 0.0722)));
	float local_i = tier_intensity(local_intensity(base_w));
	float dens = rain_density(local_i) * F.rain.x * (is_near ? 1.0 : rain_mid_weight(local_i));
	float thresh = slot == 0 ? min(dens, 1.0) : max(dens - 1.0, 0.0);
	if (keep >= thresh || vis <= 0.001) { cull(); return; }
	float drop_var = mix(0.6, 1.0, u01(hash_u(h1 ^ 0x2c1b3c6du)));
	// Length is speed / 60 s clamped to 2.5 m (a drop at 140 m/s covers 2.3 m in a 60 Hz frame).
	float len = min(2.5, F.mega_t.w * (1.0 / 60.0) * mix(0.85, 1.15, drop_var));
	float yh = dot(head - origin, up);
	float ys;
	if (map_surface(head, origin, ys) && yh < ys) {
		float lc = (ys - yh) / max(up_comp, 0.02);
		if (lc >= len) { cull(); return; }
		head -= travel * lc; len -= lc; yh = ys;
	}
	float cloud_base = F.axis_across.w;
	if (yh > cloud_base) { cull(); return; }
	if (lee_blocked(head, travel, up_comp, yh, origin)) { cull(); return; }
	vec3 tail = head - travel * len;
	float comp, vz;
	float width = F.rain2.x * mix(0.8, 1.4, drop_var);
	if (!capsule_quad(head, tail, width, vi, comp, vz)) { cull(); return; }
	float d_cam = length(head - F.cam_pos.xyz);
	float fade_cb = 1.0 - smoothstep(cloud_base - 8.0, cloud_base, yh + len * up_comp);
	float fade_cam = smoothstep(0.35, 0.7, d_cam);
	float fade_rad = 1.0 - smoothstep(0.7 * radius, radius, d_cam);
	float fade_in = is_near ? 1.0 : smoothstep(0.5 * F.near_cfg.z, F.near_cfg.z + 2.0, d_cam);
	float alpha = F.rain.z * rain_brightness_scale(local_i) * drop_var * comp * fade_cam * fade_rad * fade_in * fade_cb * vis;
	v_color = vec4(radiance, alpha);
	v_uv = vec3(CORNERS[vi % 6], 0.0);
	v_viewz = vz;
	v_kind = 0.0;
}

// Windward spray (mode 3): flecks at random screen pixels whose depth-buffer normal faces into the wind and is not ground; each lives a few tenths of a second, thrown
// off the face and drooping. Culled unless the sampled surface qualifies, so the count is a ceiling, not a cost per visible fleck.
layout(set = 0, binding = 1) uniform sampler2D vtx_depth;
void spray_vertex(int vi) {
	if (F.mega.z <= 0.001) { cull(); return; }
	int id = gl_InstanceIndex;
	uint h0 = hash_u(uint(id) * 0x9e3779b9u + 0x1234567u);
	float cycle = mix(0.2, 0.45, u01(hash_u(h0 ^ 0x68bc21ebu)));
	float t = F.mega.y / cycle + u01(hash_u(h0 + 0x02e5be93u));
	float age = fract(t);
	uint epoch = uint(floor(t));
	uint h1 = hash_u(h0 ^ (epoch * 0x85ebca6bu + 0x9e3779b9u));
	uint h2 = hash_u(h1 + 0x7f4a7c15u);
	if (u01(hash_u(h2 ^ 0x3c6ef372u)) > F.mega.z) { cull(); return; }
	ivec2 px = ivec2(vec2(u01(h1), u01(h2)) * (F.viewport.xy - 1.0));
	float d = texelFetch(vtx_depth, px, 0).r;
	if (d <= 0.0) { cull(); return; }
	ivec2 sz = ivec2(F.viewport.xy);
	ivec2 pa = clamp(px + ivec2(3, 0), ivec2(0), sz - 1), pb = clamp(px + ivec2(0, 3), ivec2(0), sz - 1);
	float da = texelFetch(vtx_depth, pa, 0).r, db = texelFetch(vtx_depth, pb, 0).r;
	if (da <= 0.0 || db <= 0.0) { cull(); return; }
	vec2 uv0 = (vec2(px) + 0.5) / F.viewport.xy, uva = (vec2(pa) + 0.5) / F.viewport.xy, uvb = (vec2(pb) + 0.5) / F.viewport.xy;
	vec4 p0 = F.inv_proj * vec4(uv0 * 2.0 - 1.0, d, 1.0), p1 = F.inv_proj * vec4(uva * 2.0 - 1.0, da, 1.0), p2 = F.inv_proj * vec4(uvb * 2.0 - 1.0, db, 1.0);
	vec3 a = p0.xyz / p0.w, b = p1.xyz / p1.w, c = p2.xyz / p2.w;
	float dist = length(a);
	if (dist > 70.0 || dist < 0.6) { cull(); return; }
	vec3 nv = normalize(cross(b - a, c - a));
	if (nv.z < 0.0) nv = -nv;   // face the camera
	vec3 nw = F.cam_x.xyz * nv.x + F.cam_y.xyz * nv.y + F.cam_z.xyz * nv.z;
	vec3 travel = F.mega_t.xyz;
	float facing = dot(nw, -travel);
	if (facing < 0.5 || dot(nw, F.axis_up.xyz) > 0.55) { cull(); return; }
	vec3 world = F.cam_pos.xyz + F.cam_x.xyz * a.x + F.cam_y.xyz * a.y + F.cam_z.xyz * a.z;
	vec3 radiance = lit_radiance(world);
	float vis = smoothstep(0.0015, 0.03, dot(radiance, vec3(0.2126, 0.7152, 0.0722)));
	if (vis <= 0.001) { cull(); return; }
	vec3 side = normalize(cross(nw, F.axis_up.xyz));
	float sx = u01(hash_u(h2 ^ 0x1b873593u)) * 2.0 - 1.0;
	vec3 centre = world + nw * (0.05 + 0.9 * age) + side * sx * 0.5 * age + F.axis_up.xyz * (0.25 * age - 0.8 * age * age);
	float r = (0.035 + 0.04 * u01(hash_u(h2 ^ 0x2c1b3c6du))) * (1.0 + 1.5 * age);
	vec2 cn = CORNERS[vi % 6];
	vec2 cc = vec2(cn.x, cn.y * 2.0 - 1.0);
	vec3 pos = centre + (F.cam_x.xyz * cc.x + F.cam_y.xyz * cc.y) * r;
	vec3 vp = to_view(pos);
	if (vp.z > -0.08) { cull(); return; }
	gl_Position = F.proj * vec4(vp, 1.0);
	float fade_cam = smoothstep(0.35, 0.7, length(vp));
	float fade_far = 1.0 - smoothstep(40.0, 70.0, dist);
	float fresh = (1.0 - age) * (1.0 - age);
	v_color = vec4(min(radiance * 2.5 + vec3(0.02), vec3(4.0)), clamp(0.8 * fresh * fade_cam * fade_far * vis * smoothstep(0.5, 0.9, facing), 0.0, 1.0));
	v_uv = vec3(cc, 0.0);
	v_viewz = vp.z;
	v_kind = 5.0;
}

void main() {
	if (pc.mode == 3u) { spray_vertex(gl_VertexIndex); return; }
	if (pc.mode == 0u && F.mega.x > 0.5) { aligned_streak(pc.layer == 0u, gl_VertexIndex); return; }
	bool is_near = pc.layer == 0u;
	vec4 cfg = is_near ? F.near_cfg : F.mid_cfg;
	vec4 cfg2 = is_near ? F.near_cfg2 : F.mid_cfg2;
	vec3 origin = is_near ? F.near_origin.xyz : F.mid_origin.xyz;
	float cell = cfg.x; int n = int(cfg.y); float radius = cfg.z;
	int cells = n * n;
	int inst = gl_InstanceIndex;
	int slot = inst / cells;
	int k = inst - slot * cells;
	int di = k % n - n / 2; int dj = k / n - n / 2;
	int ci = int(cfg.w) + di; int cj = int(cfg2.x) + dj;
	uint salt = uint(cfg2.y) + uint(slot) * 0x51ed27u;
	uint h0 = hash_cell(ci, cj, salt);
	uint h1 = hash_u(h0 ^ 0x68bc21ebu);
	float jx = u01(h0), jy = u01(h1);
	float hp = u01(hash_u(h1 + 0x02e5be93u));
	float keep = u01(hash_u(h1 ^ 0x1b873593u));
	vec3 around = F.axis_around.xyz, across = F.axis_across.xyz, up = F.axis_up.xyz;
	vec2 cell2 = vec2(float(di) + jx, float(dj) + jy) * cell;
	vec3 base_w = origin + around * cell2.x + across * cell2.y;
	float period = is_near ? F.near_cfg2.w : F.mid_origin.w;
	float phase = is_near ? F.near_origin.w : F.axis_up.w;
	float fall = F.axis_around.w;
	// World-anchored drop height relative to the observer plane; the CPU supplies (time * fall + observer height) modulo the period in double precision.
	float yrel = fract((hp * period - phase) / period + 0.5) * period - 0.5 * period;
	// Shared wind: mean wind times the travelling gust field at the drop column. Slope = horizontal metres per metre of fall.
	vec2 slope2 = F.wind.xy * gust_at(base_w) / fall;
	float sl = length(slope2);
	if (sl > F.rain2.y) { slope2 *= F.rain2.y / sl; sl = F.rain2.y; }
	vec3 travel = normalize(around * slope2.x + across * slope2.y - up);
	float up_comp = 1.0 / sqrt(1.0 + sl * sl);
	vec3 radiance = lit_radiance(base_w);
	// Only true darkness (night, deep storm shade) hides a streak: the ramp saturates at a very low luma so a storm-dimmed streak still reads against the dark scene.
	float vis = smoothstep(0.0015, 0.03, dot(radiance, vec3(0.2126, 0.7152, 0.0722)));
	float drop_var = mix(0.6, 1.0, u01(hash_u(h1 ^ 0x2c1b3c6du)));
	int vi = gl_VertexIndex;
	const vec2 corners[6] = vec2[6](vec2(-1, 0), vec2(1, 0), vec2(-1, 1), vec2(1, 0), vec2(1, 1), vec2(-1, 1));

	if (pc.mode == 0u) {
		float local_i = tier_intensity(local_intensity(base_w));
		if (is_hail(h1, local_i)) { hail_pellet(is_near, cell2, origin, around, across, up, slope2, h1, hp, radius, vis, radiance, vi); return; }
		float dens = rain_density(local_i) * F.rain.x * (is_near ? 1.0 : rain_mid_weight(local_i));
		// S3 fat drops: a sparse share of drops stays drawn (big and long) even below the rain start threshold.
		float fat_fr = fat_fraction();
		bool fat = slot == 0 && keep < fat_fr;
		float thresh = slot == 0 ? min(max(dens, fat_fr), 1.0) : max(dens - 1.0, 0.0);
		if (keep >= thresh || vis <= 0.001) { cull(); return; }
		vec2 pos2 = cell2 - slope2 * yrel;
		vec3 head = origin + around * pos2.x + across * pos2.y + up * yrel;
		float len = F.rain.y * rain_length_scale(local_i) * mix(1.0, 0.85, clamp(F.storm.z, 0.0, 1.0)) * (fat ? 1.6 : 1.0);
		// Land on whatever the occlusion map says is below: terrain, roofs, POIs. Under a roof the drop is already below the roof surface and vanishes.
		float ys;
		if (map_surface(head, origin, ys) && yrel < ys) {
			float lc = (ys - yrel) / up_comp;
			if (lc >= len) { cull(); return; }
			head -= travel * lc; len -= lc; yrel = ys;
		}
		float cloud_base = F.axis_across.w;
		float tail_y = yrel + len * up_comp;
		if (yrel > cloud_base) { cull(); return; }
		float fade_cb = 1.0 - smoothstep(cloud_base - 8.0, cloud_base, tail_y);
		vec3 tail = head - travel * len;
		vec3 vA = to_view(head), vB = to_view(tail);
		float zn = -0.08;
		if (vA.z > zn && vB.z > zn) { cull(); return; }
		if (vA.z > zn) vA = mix(vA, vB, (zn - vA.z) / (vB.z - vA.z));
		else if (vB.z > zn) vB = mix(vB, vA, (zn - vB.z) / (vA.z - vB.z));
		vec2 corner = corners[vi % 6];
		bool at_tail = corner.y > 0.5;
		vec4 cA = F.proj * vec4(vA, 1.0), cB = F.proj * vec4(vB, 1.0);
		vec2 ndcA = cA.xy / cA.w, ndcB = cB.xy / cB.w;
		vec2 sd = (ndcB - ndcA) * 0.5 * F.viewport.xy;
		float sdl = length(sd);
		vec2 dir = sdl > 1.0e-4 ? sd / sdl : vec2(0.0, 1.0);
		vec2 perp = vec2(-dir.y, dir.x);
		vec3 v = at_tail ? vB : vA;
		float wtrue = F.rain2.x * mix(0.8, 1.4, drop_var) * (fat ? 1.8 : 1.0) * F.proj[1][1] * 0.5 * F.viewport.y / max(-v.z, 0.1);
		float wpx = max(wtrue, F.rain2.z * max(1.0, F.viewport.y / 1080.0));
		vec2 off_px = perp * corner.x * wpx * 0.5 + (at_tail ? dir : -dir) * wpx * 0.5;
		vec4 c = at_tail ? cB : cA;
		gl_Position = vec4((c.xy / c.w + off_px * 2.0 / F.viewport.xy) * c.w, c.z, c.w);
		float comp = clamp(wtrue / wpx, 0.45, 1.0);
		float d_cam = length(head - F.cam_pos.xyz);
		float fade_cam = smoothstep(0.35, 0.7, length(v));
		float fade_rad = 1.0 - smoothstep(0.7 * radius, radius, d_cam);
		float fade_in = is_near ? 1.0 : smoothstep(0.5 * F.near_cfg.z, F.near_cfg.z + 2.0, d_cam);
		float alpha = F.rain.z * rain_brightness_scale(local_i) * drop_var * comp * fade_cam * fade_rad * fade_in * fade_cb * vis * (fat ? 1.25 : 1.0);
		v_color = vec4(radiance, alpha);
		v_uv = vec3(corner.x, corner.y, 0.0);
		v_viewz = v.z;
		v_kind = 0.0;
	} else if (pc.mode == 1u) {
		// Splash: tied to this drop's id, landing at the analytic time at the map surface height.
		float splash_i = tier_intensity(local_intensity(base_w));
		float splash_radius = is_near ? radius : F.mid_cfg2.w;
		if (is_hail(h1, splash_i)) { hail_puff(is_near, cell2, origin, around, across, up, slope2, h1, hp, splash_radius, vis, radiance, vi); return; }
		float splash_dens = rain_density(splash_i) * F.rain.x * (is_near ? 1.0 : rain_mid_weight(splash_i));
		float splash_cap = min(splash_dens, 1.0);
		float fat_fr = fat_fraction();
		bool fat = slot == 0 && keep < fat_fr;
		if (slot == 0) splash_cap = max(splash_cap, fat_fr);
		bool splash_pick = fat ? F.splash.w > 0.001 : u01(hash_u(h1 ^ 0x7f4a7c15u)) < F.splash.w;
		if (keep >= splash_cap || !splash_pick || vis <= 0.001) { cull(); return; }
		float fat_scale = fat ? 2.2 : 1.0;
		if (length(base_w - F.cam_pos.xyz) > splash_radius + 2.0) { cull(); return; }
		float yl = 0.0, ys = 0.0;
		bool ok = true;
		for (int it = 0; it < 3; it++) {
			vec2 q = cell2 - slope2 * yl;
			ok = map_surface(origin + around * q.x + across * q.y + up * yl, origin, ys);
			if (!ok) break;
			yl = ys;
		}
		if (!ok) { cull(); return; }
		float age = (yl - yrel) / fall;
		float life = F.splash.x;
		if (age < 0.0 || age > life) { cull(); return; }
		float u = age / life;
		vec2 q2 = cell2 - slope2 * yl;
		vec3 center = origin + around * q2.x + across * q2.y + up * yl;
		float d_cam = length(center - F.cam_pos.xyz);
		if (d_cam > splash_radius) { cull(); return; }
		vec3 n = up;
		{
			float e = 0.5, hx0, hx1, hy0, hy1;
			vec3 mx = F.map_across.xyz, my = F.map_around.xyz;
			if (map_surface(center + mx * e, origin, hx1) && map_surface(center - mx * e, origin, hx0) && map_surface(center + my * e, origin, hy1) && map_surface(center - my * e, origin, hy0)) {
				vec3 g = normalize(up - mx * ((hx1 - hx0) / (2.0 * e)) - my * ((hy1 - hy0) / (2.0 * e)));
				if (dot(g, up) > 0.5) n = g;
			}
		}
		float r = mix(F.splash.y, F.splash.z, sqrt(u)) * fat_scale;
		float strength = (1.0 - u) * (1.0 - u);
		vec3 my = F.map_around.xyz;
		vec3 t1 = normalize(my - n * dot(my, n));
		vec3 t2 = cross(n, t1);
		vec3 pos;
		float kind;
		vec2 uv;
		if (vi < 6) {
			// Ring: an expanding flat ring on the surface the map reports (roofs too), tilted to the map gradient.
			vec2 corner = corners[vi % 6];
			float hs = r * 1.35 + 0.01;
			vec2 cc = vec2(corner.x, corner.y * 2.0 - 1.0);
			pos = center + n * 0.015 + (t1 * cc.x + t2 * cc.y) * hs;
			kind = 1.0; uv = cc;
		} else {
			// Crown: a 12-segment translucent wall around the impact point. Both its rim and its top edge are continuous (shared vertices), the top edge zig-zags between tall and
			// short vertices so six peaks stand out like a milk crown, the wall leans outward and fades to the tip. The crown rises fast and collapses (sin of u^0.55).
			int s = (vi - 6) / 6;
			int k = (vi - 6) - s * 6;
			vec2 cn = corners[k];
			int idx = (cn.x < 0.0 ? s : s + 1) % 12;
			float rise = sin(3.14159 * pow(u, 0.55));
			float peak = ((idx & 1) == 0) ? 1.0 : 0.38;
			float jitter = 0.8 + 0.2 * u01(hash_u(h1 + uint(idx) * 0x9e3779b9u));
			float hgt = 0.11 * peak * jitter * rise * (F.splash.z / 0.14) * fat_scale;
			float ang = 6.2831853 * (cn.x < 0.0 ? float(s) : float(s + 1)) / 12.0;
			float rc = 0.02 + r * 0.8;
			bool top = cn.y > 0.5;
			pos = center + (t1 * cos(ang) + t2 * sin(ang)) * (top ? rc * 1.3 : rc) + n * (top ? hgt + 0.006 : 0.004);
			kind = 2.0; uv = vec2(cn.x, cn.y);
			strength *= 0.9;
		}
		vec3 v = to_view(pos);
		if (v.z > -0.08) { cull(); return; }
		gl_Position = F.proj * vec4(v, 1.0);
		float fade_cam = smoothstep(0.35, 0.7, length(v));
		float fade_rad = 1.0 - smoothstep(0.7 * splash_radius, splash_radius, d_cam);
		v_color = vec4(radiance, F.rain.z * rain_brightness_scale(splash_i) * 0.7 * strength * fade_cam * fade_rad * vis);
		v_uv = vec3(uv, 0.0);
		v_viewz = v.z;
		v_kind = kind;
	} else {
		drip_vertex();
	}
}

#[fragment]
#version 450
layout(set = 0, binding = 1) uniform sampler2D scene_depth;
#include "res://scripts/render/ring_rain_streaks.glslinc"
layout(location = 0) in vec4 v_color;
layout(location = 1) in vec3 v_uv;
layout(location = 2) in float v_viewz;
layout(location = 3) flat in float v_kind;
layout(location = 0) out vec4 out_color;

void main() {
	float d = texelFetch(scene_depth, ivec2(gl_FragCoord.xy), 0).r;
	if (d > 0.0) {
		vec2 ndc = gl_FragCoord.xy / F.viewport.xy * 2.0 - 1.0;
		vec4 sv = F.inv_proj * vec4(ndc, d, 1.0);
		if (sv.z / sv.w > v_viewz + 0.05) discard;
	}
	float a;
	if (v_kind < 0.5) {
		a = (1.0 - smoothstep(0.35, 1.0, abs(v_uv.x))) * pow(clamp(1.0 - v_uv.y, 0.0, 1.0), 1.25);
	} else if (v_kind < 1.5) {
		float r = length(v_uv.xy);
		a = smoothstep(0.55, 0.73, r) * (1.0 - smoothstep(0.73, 0.92, r)) + 0.2 * (1.0 - smoothstep(0.0, 0.7, r));
	} else if (v_kind < 2.5) {
		// Crown wall: v_uv.y runs 0 (rim) to 1 (top edge); solid at the rim, fading to the tip, soft at the segment seams.
		a = pow(clamp(1.0 - v_uv.y, 0.0, 1.0), 0.6) * 0.75;
	} else {
		if (v_kind < 3.5) {
			// kind 3: S2 drips (see drip_vertex in the vertex stage). Soft round placeholder until that class lands.
			a = clamp(1.0 - length(v_uv.xy), 0.0, 1.0);
		} else if (v_kind < 4.5) {
			// kind 4: hail pellet: a bright round head with a short fading motion trail.
			a = (1.0 - smoothstep(0.45, 1.0, abs(v_uv.x))) * (1.0 - smoothstep(0.05, 1.0, v_uv.y));
			out_color = vec4(v_color.rgb * mix(1.0, 1.5, 1.0 - smoothstep(0.0, 0.4, v_uv.y)), clamp(v_color.a * a, 0.0, 1.0));
			return;
		} else {
			// kind 5: hail impact puff, a soft white round.
			a = pow(clamp(1.0 - length(v_uv.xy), 0.0, 1.0), 1.5);
		}
	}
	out_color = vec4(v_color.rgb, clamp(v_color.a * a, 0.0, 1.0));
}
