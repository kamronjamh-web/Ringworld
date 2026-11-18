extends SceneTree

## R3a-2 of docs/work-orders/reflections/R3a-design.md (section 6.2): parity test of materials/ring_cloud_reflection.gdshaderinc.
## Renders a canvas_item shader that includes the file into a float SubViewport with synthetic globals (nearest RGBAF images, known matrices) and
## compares with double-precision GDScript references: the reprojection round trip (march ray -> include uv -> its own texel), the direction to uv
## maths, the four-tap filter, behind/edge validity and the placement refinement. Run windowed Vulkan (not --headless):
##   godot --path . --script res://tools/cloud_reflection_include_gpu_test.gd
## Prints CLOUD_REFLECTION_INCLUDE_GPU_TEST=PASS.

const FAR := 1.0e9
const SHADER_CODE := """shader_type canvas_item;
// Raw float output: no blending (alpha carries data), no lighting.
render_mode blend_disabled, unshaded;
#include "res://materials/ring_cloud_reflection.gdshaderinc"
uniform int mode;
uniform vec2 cloud_size;
uniform mat4 inv_proj_jit;
uniform mat3 cam_basis;
uniform sampler2D table : filter_nearest, repeat_disable;

// A float as two fp16 values (a part exactly representable in fp16 and the remainder) so the half-float target keeps about 21 bits; FAR becomes -1.
// The high part clears the low 13 mantissa bits by hand: the driver folded unpackHalf2x16(packHalf2x16(v)) to v and the target store truncates (first run).
vec2 enc(float v) {
	if (v >= 1.0e9) {
		return vec2(-1.0, 0.0);
	}
	float hi = uintBitsToFloat(floatBitsToUint(v) & 0xFFFFE000u);
	return vec2(hi, v - hi);
}

void fragment() {
	ivec2 px = ivec2(FRAGCOORD.xy);
	if (mode == 0) {
		// The march ray of one cloud texel, as SunshineCloudsHistory.glslinc:59 and :82-96 (ring_cloud_pixel_ray, no resolution jitter):
		// depthUV = (uv + 0.5) / size, ndc = depthUV * 2 - 1, view = inverse(P) * (ndc, 0, 1), dir = basis * normalize(view.xyz / view.w).
		vec2 depth_uv = (vec2(px) + 0.5) / cloud_size;
		vec2 ndc = depth_uv * 2.0 - 1.0;
		vec4 view_pos = inv_proj_jit * vec4(ndc, 0.0, 1.0);
		view_pos.xyz /= view_pos.w;
		vec3 rd = normalize(cam_basis * normalize(view_pos.xyz));
		vec3 u = ring_cloud_reflection_uv(rd);
		vec2 res = u.xy * cloud_size - (vec2(px) + 0.5);
		COLOR = vec4(res, u.z, 1.0);
	} else if (mode == 1) {
		vec3 dir = texelFetch(table, ivec2(px.x, 0), 0).xyz;
		vec2 expected = texelFetch(table, ivec2(px.x, 1), 0).xy;
		vec3 u = ring_cloud_reflection_uv(dir);
		COLOR = vec4(u.xy - expected, u.z, 1.0);
	} else {
		int k = px.x / 4;
		int c = px.x % 4;
		vec4 q0 = texelFetch(table, ivec2(k, 0), 0);
		vec3 origin = texelFetch(table, ivec2(k, 1), 0).xyz;
		vec3 camera = texelFetch(table, ivec2(k, 2), 0).xyz;
		RingCloudSample s;
		if (q0.w < 0.5) {
			s = ring_cloud_reflection_sample_uv(q0.xy, q0.z);
		} else if (q0.w < 1.5) {
			s = ring_cloud_reflection_sample(q0.xyz);
		} else {
			s = ring_cloud_reflection_sample_placed(origin, q0.xyz, camera);
		}
		if (c == 0) {
			COLOR = vec4(s.premul, s.coverage);
		} else if (c == 1) {
			COLOR = vec4(s.valid, s.distance_weight, 0.0, 1.0);
		} else if (c == 2) {
			COLOR = vec4(enc(s.distance), enc(s.opaque_limit));
		} else {
			COLOR = vec4(enc(s.premul.r), enc(s.premul.g));
		}
	}
}
"""

var _failed := false
var _vp: SubViewport
var _rect: ColorRect
var _mat: ShaderMaterial
var _keep: Array = []
var _report: Array[String] = []

func _init() -> void:
	create_timer(240.0).timeout.connect(func(): _fail("timeout"))
	call_deferred("_run")

func _fail(message: String) -> void:
	if _failed: return
	_failed = true
	for line in _report: print("CLOUD_REFLECTION_INCLUDE ", line)
	push_error("CLOUD_REFLECTION_INCLUDE_GPU_TEST=FAIL " + message)
	quit(1)

func _note(line: String) -> void:
	_report.append(line)
	print("CLOUD_REFLECTION_INCLUDE ", line)

# ---------- matrices (doubles in the references, floats on the GPU) ----------

func _proj(aspect: float, jx: float, jy: float) -> Projection:
	var f := 1.0 / tan(deg_to_rad(70.0) * 0.5)
	var n := 0.05
	var fr := 20000.0
	var a := -(fr + n) / (fr - n)
	var b := -2.0 * fr * n / (fr - n)
	return Projection(Vector4(f / aspect, 0, 0, 0), Vector4(0, f, 0, 0), Vector4(jx, jy, a, -1.0), Vector4(0, 0, b, 0))

func _inverse_double(p: Projection) -> Projection:
	# Gauss-Jordan in doubles on the (float) entries; rows m[r][c] = p[c][r].
	var m: Array = []
	for r in 4:
		var row: Array = []
		for c in 4: row.append(float(p[c][r]))
		for c in 4: row.append(1.0 if c == r else 0.0)
		m.append(row)
	for col in 4:
		var piv := col
		for r in range(col + 1, 4):
			if absf(m[r][col]) > absf(m[piv][col]): piv = r
		var tmp: Array = m[col]
		m[col] = m[piv]
		m[piv] = tmp
		var d: float = m[col][col]
		for c in 8: m[col][c] = m[col][c] / d
		for r in 4:
			if r == col: continue
			var fct: float = m[r][col]
			for c in 8: m[r][c] = m[r][c] - fct * m[col][c]
	var cols: Array[Vector4] = []
	for c in 4:
		cols.append(Vector4(m[0][4 + c], m[1][4 + c], m[2][4 + c], m[3][4 + c]))
	return Projection(cols[0], cols[1], cols[2], cols[3])

func _dir_to_clip(p_unjit: Projection, basis: Basis) -> Projection:
	return p_unjit * Projection(Transform3D(basis.inverse(), Vector3.ZERO))

# Double-precision uv of a world direction through the published matrices: clip = P * (R^T d, 0); uv = clip.xy / clip.w * 0.5 + 0.5.
func _ref_uv(p: Projection, basis: Basis, d: Vector3) -> Vector3:
	var vx := float(basis.x.x) * d.x + float(basis.x.y) * d.y + float(basis.x.z) * d.z
	var vy := float(basis.y.x) * d.x + float(basis.y.y) * d.y + float(basis.y.z) * d.z
	var vz := float(basis.z.x) * d.x + float(basis.z.y) * d.y + float(basis.z.z) * d.z
	var cx: float = float(p.x.x) * vx + float(p.y.x) * vy + float(p.z.x) * vz
	var cy: float = float(p.x.y) * vx + float(p.y.y) * vy + float(p.z.y) * vz
	var cw: float = float(p.x.w) * vx + float(p.y.w) * vy + float(p.z.w) * vz
	if cw <= 1e-6: return Vector3(-1, -1, 0)
	return Vector3(cx / cw * 0.5 + 0.5, cy / cw * 0.5 + 0.5, 1.0)

# ---------- rendering ----------

func _setup_viewport() -> void:
	_vp = SubViewport.new()
	_vp.size = Vector2i(16, 4)
	_vp.use_hdr_2d = true
	_vp.transparent_bg = true  # keep the alpha channel: it carries data (an opaque viewport writes alpha 1)
	_vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_vp)
	_rect = ColorRect.new()
	_rect.size = Vector2(16, 4)
	_vp.add_child(_rect)
	var shader := Shader.new()
	shader.code = SHADER_CODE
	_mat = ShaderMaterial.new()
	_mat.shader = shader
	_rect.material = _mat

func _render(size: Vector2i) -> Image:
	_vp.size = size
	_rect.size = Vector2(size)
	_mat.set_shader_parameter("cloud_size", Vector2(size))
	for i in 4: await process_frame
	return _vp.get_texture().get_image()

func _rgbaf(w: int, h: int) -> Image:
	return Image.create(w, h, false, Image.FORMAT_RGBAF)

func _set_globals(color_img: Image, data_img: Image, d2c: Projection, params: Vector4, params2: Vector4) -> void:
	var ct := ImageTexture.create_from_image(color_img)
	var dt := ImageTexture.create_from_image(data_img)
	_keep.append(ct)
	_keep.append(dt)
	RenderingServer.global_shader_parameter_set("ringworld_cloud_reflection_color", ct)
	RenderingServer.global_shader_parameter_set("ringworld_cloud_reflection_data", dt)
	RenderingServer.global_shader_parameter_set("ringworld_cloud_reflection_dir_to_clip", d2c)
	RenderingServer.global_shader_parameter_set("ringworld_cloud_reflection_params", params)
	RenderingServer.global_shader_parameter_set("ringworld_cloud_reflection_params2", params2)

func _set_table(img: Image) -> void:
	var t := ImageTexture.create_from_image(img)
	_keep.append(t)
	_mat.set_shader_parameter("table", t)

func _uniform_image(w: int, h: int, col: Color) -> Image:
	var img := _rgbaf(w, h)
	img.fill(col)
	return img

# ---------- query decoding ----------

func _dec(a: float, b: float) -> float:
	return FAR if a < 0.0 else a + b

# Result of query k from a mode-2 image: dictionary of the six outputs.
func _query_result(img: Image, k: int) -> Dictionary:
	var c0 := img.get_pixel(4 * k, 0)
	var c1 := img.get_pixel(4 * k + 1, 0)
	var c2 := img.get_pixel(4 * k + 2, 0)
	var c3 := img.get_pixel(4 * k + 3, 0)
	return {
		"premul": Vector3(c0.r, c0.g, c0.b), "coverage": c0.a, "valid": c1.r, "distance_weight": c1.g,
		"distance": _dec(c2.r, c2.g), "opaque": _dec(c2.b, c2.a),
		"premul_r": _dec(c3.r, c3.g), "premul_g": _dec(c3.b, c3.a),
	}

func _query_table(queries: Array) -> Image:
	# queries: [{kind, v: Vector3 (uv.xy + in_front, or a direction), origin, camera}]
	var img := _rgbaf(queries.size(), 3)
	for k in queries.size():
		var q: Dictionary = queries[k]
		var v: Vector3 = q.v
		img.set_pixel(k, 0, Color(v.x, v.y, v.z, float(q.kind)))
		var o: Vector3 = q.get("origin", Vector3.ZERO)
		var cam: Vector3 = q.get("camera", Vector3.ZERO)
		img.set_pixel(k, 1, Color(o.x, o.y, o.z, 0))
		img.set_pixel(k, 2, Color(cam.x, cam.y, cam.z, 0))
	return img

# ---------- the filter reference (design 3.2, doubles) ----------

func _finite(v: float) -> bool:
	return not is_nan(v) and not is_inf(v)

func _ref_filter(col: Array, dat: Array, f: Vector2, params: Vector4, params2: Vector4) -> Dictionary:
	var out := {"premul": Vector3.ZERO, "coverage": 0.0, "distance": FAR, "distance_weight": 0.0, "opaque": FAR, "valid": 0.0}
	if params.x <= 0.0: return out
	var premul := Vector3.ZERO
	var cov := 0.0
	var cw := 0.0
	var ci := 0.0
	var best_score := -1.0
	var best_w := -1.0
	var best_o := -1.0
	for k in 4:
		var ox := k % 2
		var oy := k / 2
		var w := (1.0 - f.x if ox == 0 else f.x) * (1.0 - f.y if oy == 0 else f.y)
		var c: Color = col[k]
		var d: Color = dat[k]
		var bad := not _finite(c.a) or not _finite(c.r) or not _finite(c.g) or not _finite(c.b)
		var b: float = d.b
		var bf := _finite(b)
		var b_fog := maxf(b, 0.0) if bf else FAR
		var fog := 1.0 - params2.y * (1.0 - exp(-params2.x * b_fog))
		var a := 0.0 if bad else clampf(c.a, 0.0, 1.0) * fog
		if not bad: premul += w * a * Vector3(c.r, c.g, c.b)
		cov += w * a
		if a >= 0.1:
			var inv := 1.0 / b if (bf and b > 0.0 and b < 65000.0) else 0.0
			cw += w * a
			ci += w * a * inv
		var score := w * a
		if score > best_score or (score == best_score and w > best_w):
			best_score = score
			best_w = w
			best_o = d.a
	out.premul = premul
	out.coverage = clampf(cov, 0.0, 1.0)
	if cw >= 0.05:
		out.distance_weight = cw
		out.distance = minf(cw / ci, FAR) if ci > 0.0 else FAR
	out.opaque = minf(exp(best_o * log(2.0)) - 1.0, FAR) if (_finite(best_o) and best_o >= 0.0) else FAR
	out.valid = 1.0
	return out

func _rel_ok(got: float, want: float, rel: float) -> bool:
	if want >= FAR or got >= FAR: return want >= FAR and got >= FAR
	return absf(got - want) <= rel * maxf(absf(want), 1.0)

func _compare_result(name: String, got: Dictionary, want: Dictionary) -> bool:
	var ok := true
	var gp: Vector3 = got.premul
	var wp: Vector3 = want.premul
	var dp := (gp - wp).abs()
	if maxf(dp.x, maxf(dp.y, dp.z)) > 1e-3: ok = false
	if absf(got.coverage - want.coverage) > 1e-3: ok = false
	if absf(got.valid - want.valid) > 1e-3: ok = false
	if absf(got.distance_weight - want.distance_weight) > 1e-3: ok = false
	if not _rel_ok(got.distance, want.distance, 1e-3): ok = false
	if not _rel_ok(got.opaque, want.opaque, 1e-3): ok = false
	if not ok:
		_note("MISMATCH %s: got %s want %s" % [name, str(got), str(want)])
	return ok

func _run() -> void:
	if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
		_fail("needs a display and RenderingDevice (run windowed Vulkan, not --headless)")
		return
	_setup_viewport()
	var ok := true
	ok = (await _case_reprojection()) and ok
	if _failed: return
	ok = (await _case_cpu_reference()) and ok
	if _failed: return
	ok = (await _case_behind_edges()) and ok
	if _failed: return
	ok = (await _case_filter()) and ok
	if _failed: return
	ok = (await _case_placement()) and ok
	if _failed: return
	if ok:
		for line in _report: print("CLOUD_REFLECTION_INCLUDE ", line)
		print("CLOUD_REFLECTION_INCLUDE_GPU_TEST=PASS")
		quit(0)
	else:
		_fail("see MISMATCH / BOUND lines above")

# Case 1: per cloud texel the march ray maps back to its own texel.
func _case_reprojection() -> bool:
	var ok := true
	var yawed := Basis.from_euler(Vector3(deg_to_rad(-22.0), deg_to_rad(137.0), deg_to_rad(6.0)))
	var cams := [
		{"name": "identity", "basis": Basis.IDENTITY, "jitter": Vector2.ZERO},
		{"name": "yawed", "basis": yawed, "jitter": Vector2.ZERO},
		{"name": "jittered", "basis": yawed, "jitter": Vector2(3e-4, -2e-4)},
	]
	_mat.set_shader_parameter("mode", 0)
	for size: Vector2i in [Vector2i(800, 450), Vector2i(801, 451)]:
		var aspect := float(size.x) / float(size.y)
		for cam in cams:
			var jit: Vector2 = cam.jitter
			var p_unjit := _proj(aspect, 0.0, 0.0)
			var p_jit := _proj(aspect, jit.x, jit.y)
			_set_globals(_uniform_image(1, 1, Color(0, 0, 0, 0)), _uniform_image(1, 1, Color(0, 0, 0, -8)), _dir_to_clip(p_unjit, cam.basis), Vector4(1, 1.0 / size.x, 1.0 / size.y, 0), Vector4(0, 0, 0, 1))
			_mat.set_shader_parameter("inv_proj_jit", _inverse_double(p_jit))
			_mat.set_shader_parameter("cam_basis", cam.basis)
			var img := await _render(size)
			var max_x := 0.0
			var max_y := 0.0
			var min_x := 1e9
			var min_y := 1e9
			var worst_front := 1.0
			var sum_x := 0.0
			var sum_y := 0.0
			for y in size.y:
				for x in size.x:
					var c := img.get_pixel(x, y)
					max_x = maxf(max_x, absf(c.r))
					max_y = maxf(max_y, absf(c.g))
					sum_x += c.r
					sum_y += c.g
					worst_front = minf(worst_front, c.b)
			var mean_x := sum_x / float(size.x * size.y)
			var mean_y := sum_y / float(size.x * size.y)
			# The jitter shifts the ndc of every ray by -jitter, i.e. -jitter * size / 2 texels.
			var bound_x := absf(jit.x) * size.x * 0.5 + 1e-3
			var bound_y := absf(jit.y) * size.y * 0.5 + 1e-3
			_note("reproject %s %dx%d: max |res| = (%.6f, %.6f) texel, mean (%.6f, %.6f), bound (%.6f, %.6f), in_front min %.0f" % [cam.name, size.x, size.y, max_x, max_y, mean_x, mean_y, bound_x, bound_y, worst_front])
			if max_x > bound_x or max_y > bound_y or worst_front < 1.0:
				_note("BOUND reproject %s %dx%d exceeds its bound" % [cam.name, size.x, size.y])
				ok = false
	return ok

# Case 1b: GDScript double-precision reference for 16 directions.
func _case_cpu_reference() -> bool:
	var basis := Basis.from_euler(Vector3(deg_to_rad(15.0), deg_to_rad(-70.0), deg_to_rad(-9.0)))
	var p := _proj(16.0 / 9.0, 0.0, 0.0)
	var vs := [Vector2(0, 0), Vector2(1.2, 0.7), Vector2(-1.2, 0.7), Vector2(1.2, -0.7), Vector2(-1.2, -0.7), Vector2(0.3, 0.1), Vector2(-0.6, 0.45), Vector2(0.9, -0.2),
			Vector2(-0.1, -0.6), Vector2(2.4, 0.3), Vector2(-2.0, -1.5), Vector2(0.05, 1.5), Vector2(1.7, 1.1), Vector2(-0.35, -0.05), Vector2(0.77, 0.52), Vector2(-1.0, 0.0)]
	var img := _rgbaf(16, 2)
	var expected: Array = []
	for i in 16:
		var d: Vector3 = (basis * Vector3(vs[i].x, vs[i].y, -1.0)).normalized()
		var e := _ref_uv(p, basis, d)
		expected.append(e)
		img.set_pixel(i, 0, Color(d.x, d.y, d.z, 0))
		img.set_pixel(i, 1, Color(e.x, e.y, e.z, 0))
	_set_globals(_uniform_image(1, 1, Color(0, 0, 0, 0)), _uniform_image(1, 1, Color(0, 0, 0, -8)), _dir_to_clip(p, basis), Vector4(1, 0, 0, 0), Vector4(0, 0, 0, 1))
	_set_table(img)
	_mat.set_shader_parameter("mode", 1)
	var out := await _render(Vector2i(16, 4))
	var worst := 0.0
	for i in 16:
		var c := out.get_pixel(i, 0)
		worst = maxf(worst, maxf(absf(c.r), absf(c.g)))
		if c.b < 1.0: worst = 1e9
	_note("cpu reference 16 directions: max |uv gpu - uv double| = %.7f (tolerance 1e-4)" % worst)
	if worst > 1e-4:
		_note("BOUND cpu reference exceeds 1e-4")
		return false
	return true

# Case 2: behind the camera and at the border.
func _case_behind_edges() -> bool:
	var p := _proj(16.0 / 9.0, 0.0, 0.0)
	_set_globals(_uniform_image(64, 64, Color(0.2, 0.4, 0.6, 1.0)), _uniform_image(64, 64, Color(0, 0, 1000.0, -8.0)), _dir_to_clip(p, Basis.IDENTITY), Vector4(0.8, 1.0 / 64.0, 1.0 / 64.0, 0), Vector4(0, 0, 0, 1))
	var qs := [
		{"kind": 1, "v": Vector3(0, 0, 1)},
		{"kind": 0, "v": Vector3(0.001, 0.5, 1)},
		{"kind": 0, "v": Vector3(0.5, 0.5, 1)},
		{"kind": 0, "v": Vector3(-0.1, 0.5, 1)},
		{"kind": 0, "v": Vector3(0.5, 0.5, 0)},
		{"kind": 1, "v": Vector3(0, 0, -1)},
	]
	_set_table(_query_table(qs))
	_mat.set_shader_parameter("mode", 2)
	var img := await _render(Vector2i(4 * qs.size(), 4))
	var r: Array = []
	for k in qs.size(): r.append(_query_result(img, k))
	_note("behind valid %.4f; uv 0.001 valid %.4f (< 0.1); uv 0.5 valid %.4f (params.x 0.8); outside valid %.4f; not in front valid %.4f; forward valid %.4f" % [r[0].valid, r[1].valid, r[2].valid, r[3].valid, r[4].valid, r[5].valid])
	var ok: bool = r[0].valid == 0.0 and r[1].valid < 0.1 and r[1].valid > 0.0 and absf(r[2].valid - 0.8) < 1e-3 and r[3].valid == 0.0 and r[4].valid == 0.0 and absf(r[5].valid - 0.8) < 1e-3
	if not ok: _note("BOUND behind/edges")
	return ok

# Case 3: filter patterns.
func _case_filter() -> bool:
	var ok := true
	var nan := NAN
	var inf := INF
	var rgb := Color(0.2, 0.4, 0.6)
	var fractions := [0.0, 0.25, 0.5]
	var p1 := Vector4(1, 0.5, 0.5, 0)
	var p2 := Vector4(0, 0, 0, 1)
	# Each case: name, col[4] (TL, TR, BL, BR), dat[4], params, params2, optional explicit expectations {fraction index: {field: value}}.
	var left_a1 := Color(0.2, 0.4, 0.6, 1.0)
	var none := Color(0.2, 0.4, 0.6, 0.0)
	var cases := [
		{"name": "near beside far-empty", "col": [left_a1, none, left_a1, none], "dat": [Color(0, 0, 2000, 0), Color(0, 0, 70000, 0), Color(0, 0, 2000, 0), Color(0, 0, 70000, 0)], "p": p1, "p2": p2,
			"expect": {"distance": [2000.0, 2000.0, 2000.0]}},
		{"name": "2000 beside 8000", "col": [left_a1, left_a1, left_a1, left_a1], "dat": [Color(0, 0, 2000, 0), Color(0, 0, 8000, 0), Color(0, 0, 2000, 0), Color(0, 0, 8000, 0)], "p": p1, "p2": p2,
			"expect": {"distance": [2000.0, null, 3200.0]}},
		{"name": "far values 1e5 NaN inf", "col": [left_a1, left_a1, left_a1, left_a1], "dat": [Color(0, 0, 1e5, 0), Color(0, 0, nan, 0), Color(0, 0, inf, 0), Color(0, 0, 1e5, 0)], "p": p1, "p2": p2,
			"expect": {"distance": [FAR, FAR, FAR], "distance_weight": [1.0, 1.0, 1.0]}},
		{"name": "all a 0.05", "col": [Color(0.2, 0.4, 0.6, 0.05), Color(0.2, 0.4, 0.6, 0.05), Color(0.2, 0.4, 0.6, 0.05), Color(0.2, 0.4, 0.6, 0.05)], "dat": [Color(0, 0, 2000, 0), Color(0, 0, 2000, 0), Color(0, 0, 2000, 0), Color(0, 0, 2000, 0)], "p": p1, "p2": p2,
			"expect": {"distance_weight": [0.0, 0.0, 0.0], "distance": [FAR, FAR, FAR], "coverage": [0.05, 0.05, 0.05]}},
		{"name": "red beside blue-empty", "col": [Color(1, 0, 0, 1), Color(0, 0, 1, 0), Color(1, 0, 0, 1), Color(0, 0, 1, 0)], "dat": [Color(0, 0, 2000, 0), Color(0, 0, 2000, 0), Color(0, 0, 2000, 0), Color(0, 0, 2000, 0)], "p": p1, "p2": p2,
			"expect": {"coverage": [1.0, 0.75, 0.5], "premul_r": [1.0, 0.75, 0.5]}},
		{"name": "NaN colour tap ignored", "col": [left_a1, Color(nan, nan, nan, 1.0), left_a1, Color(nan, 0.1, 0.2, 1.0)], "dat": [Color(0, 0, 2000, 0), Color(0, 0, 2000, 0), Color(0, 0, 2000, 0), Color(0, 0, 2000, 0)], "p": p1, "p2": p2,
			"expect": {"coverage": [1.0, 0.75, 0.5]}},
		{"name": "enclosed weight 0.5", "col": [left_a1, left_a1, left_a1, left_a1], "dat": [Color(0, 0, 2000, 0), Color(0, 0, 2000, 0), Color(0, 0, 2000, 0), Color(0, 0, 2000, 0)], "p": p1, "p2": Vector4(1, 0.5, 0, 1),
			"expect": {"coverage": [0.5, 0.5, 0.5]}},
		{"name": "params.x 0", "col": [left_a1, left_a1, left_a1, left_a1], "dat": [Color(0, 0, 2000, 3), Color(0, 0, 2000, 3), Color(0, 0, 2000, 3), Color(0, 0, 2000, 3)], "p": Vector4(0, 0.5, 0.5, 0), "p2": p2,
			"expect": {"coverage": [0.0, 0.0, 0.0], "distance": [FAR, FAR, FAR], "opaque": [FAR, FAR, FAR], "valid": [0.0, 0.0, 0.0]}},
		{"name": "fallback data", "col": [left_a1, left_a1, left_a1, left_a1], "dat": [Color(0, 0, 0, -8), Color(0, 0, 0, -8), Color(0, 0, 0, -8), Color(0, 0, 0, -8)], "p": p1, "p2": p2,
			"expect": {"opaque": [FAR, FAR, FAR]}},
		{"name": "opaque limit of the heaviest tap", "col": [left_a1, Color(0.2, 0.4, 0.6, 0.5), left_a1, Color(0.2, 0.4, 0.6, 0.5)], "dat": [Color(0, 0, 500, log(101.0) / log(2.0)), Color(0, 0, 500, log(1001.0) / log(2.0)), Color(0, 0, 500, log(101.0) / log(2.0)), Color(0, 0, 500, log(1001.0) / log(2.0))], "p": p1, "p2": p2,
			"expect": {"opaque": [100.0, 100.0, 100.0]}},
	]
	_mat.set_shader_parameter("mode", 2)
	for cs in cases:
		var col_img := _rgbaf(2, 2)
		var dat_img := _rgbaf(2, 2)
		for k in 4:
			col_img.set_pixel(k % 2, k / 2, cs.col[k])
			dat_img.set_pixel(k % 2, k / 2, cs.dat[k])
		_set_globals(col_img, dat_img, Projection.IDENTITY, cs.p, cs.p2)
		var qs: Array = []
		for f in fractions:
			qs.append({"kind": 0, "v": Vector3((f + 0.5) / 2.0, 0.25, 1.0)})
		_set_table(_query_table(qs))
		var img := await _render(Vector2i(4 * qs.size(), 4))
		var case_ok := true
		var parts: Array[String] = []
		for i in fractions.size():
			var got := _query_result(img, i)
			var want := _ref_filter(cs.col, cs.dat, Vector2(fractions[i], 0.0), cs.p, cs.p2)
			if not _compare_result("%s @%.2f" % [cs.name, fractions[i]], got, want): case_ok = false
			for field in cs.expect:
				var want_v = cs.expect[field][i]
				if want_v == null: continue
				var g: float = got[field]
				if not _rel_ok(g, float(want_v), 1e-3) and absf(g - float(want_v)) > 1e-3:
					_note("MISMATCH %s @%.2f explicit %s: got %s want %s" % [cs.name, fractions[i], field, str(g), str(want_v)])
					case_ok = false
			parts.append("f%.2f dist %s w %.3f cov %.3f op %s" % [fractions[i], "FAR" if got.distance >= FAR else "%.2f" % got.distance, got.distance_weight, got.coverage, "FAR" if got.opaque >= FAR else "%.2f" % got.opaque])
		_note("filter %s: %s -> %s" % [cs.name, " | ".join(parts), "ok" if case_ok else "FAIL"])
		ok = ok and case_ok
	return ok

# Case 4: placement.
func _case_placement() -> bool:
	var basis := Basis.from_euler(Vector3(deg_to_rad(-12.0), deg_to_rad(48.0), deg_to_rad(0.0)))
	var p := _proj(16.0 / 9.0, 0.0, 0.0)
	var w := 64
	var h := 36
	var col := _rgbaf(w, h)
	for y in h:
		for x in w:
			col.set_pixel(x, y, Color((x + 0.5) / w, (y + 0.5) / h, 0.0, 1.0))
	_set_globals(col, _uniform_image(w, h, Color(0, 0, 3000.0, -8.0)), _dir_to_clip(p, basis), Vector4(1, 1.0 / w, 1.0 / h, 0), Vector4(0, 0, 0, 1))
	var camera := Vector3(1234.0, 56.0, -789.0)
	var dir: Vector3 = (basis * Vector3(0.3, 0.1, -1.0)).normalized()
	var below := camera + Vector3(0, -300.0, 0)
	var qs := [
		{"kind": 1, "v": dir},
		{"kind": 2, "v": dir, "origin": camera, "camera": camera},
		{"kind": 2, "v": dir, "origin": below, "camera": camera},
	]
	_set_table(_query_table(qs))
	_mat.set_shader_parameter("mode", 2)
	var img := await _render(Vector2i(4 * qs.size(), 4))
	var base := _query_result(img, 0)
	var same := _query_result(img, 1)
	var moved := _query_result(img, 2)
	var d2: Vector3 = (below + dir * 3000.0 - camera).normalized()
	var e0 := _ref_uv(p, basis, dir)
	var e2 := _ref_uv(p, basis, d2)
	var ok := true
	var same_err := maxf(absf(same.premul_r - base.premul_r), absf(same.premul_g - base.premul_g))
	var base_err := maxf(absf(base.premul_r - e0.x), absf(base.premul_g - e0.y))
	var moved_err := maxf(absf(moved.premul_r - e2.x), absf(moved.premul_g - e2.y))
	var shift := Vector2(moved.premul_r - base.premul_r, moved.premul_g - base.premul_g).length()
	var shift_ref := Vector2(e2.x - e0.x, e2.y - e0.y).length()
	_note("placement: origin = camera differs from sample by %s; sample uv error %s; placed uv error %s; uv shift gpu %.5f ref %.5f; distance %.1f" % [String.num_scientific(same_err), String.num_scientific(base_err), String.num_scientific(moved_err), shift, shift_ref, base.distance])
	if same_err > 1e-6 or base_err > 1e-4 or moved_err > 1e-4 or absf(base.distance - 3000.0) > 3.0 or shift < 0.01:
		_note("BOUND placement")
		ok = false
	return ok
