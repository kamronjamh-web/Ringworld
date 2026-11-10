extends RefCounted

## Distance-band table shared by the landscape (T0) and vastness (V0) reports (docs/landscape-audit-t0-spec.md 2.5). Test-only, no production code uses it.
## Edges are the union of T0's near decades and V0's bands. Distance is linear view distance in metres (tools/shadow_depth_capture_effect.gd); anything at or beyond
## NO_DISTANCE_M (sky, beyond the far clip) goes to the last band, "no distance". V0 replaces those pixels with its analytic ray distance before calling compute().

const EDGES_M := [0.0, 10.0, 30.0, 100.0, 500.0, 2000.0, 8000.0, 30000.0, 120000.0, 500000.0, 2000000.0, 7000000.0, 1.0e8]
const NAMES := ["0-10 m", "10-30 m", "30-100 m", "0.1-0.5 km", "0.5-2 km", "2-8 km", "8-30 km", "30-120 km", "120-500 km", "500-2,000 km", "2,000-7,000 km", "beyond 7,000 km", "no distance"]
const COLORS := [Color(0.90, 0.10, 0.10), Color(0.95, 0.45, 0.10), Color(0.95, 0.80, 0.10), Color(0.60, 0.85, 0.15), Color(0.15, 0.75, 0.30), Color(0.10, 0.70, 0.65),
	Color(0.10, 0.55, 0.90), Color(0.25, 0.30, 0.95), Color(0.55, 0.25, 0.90), Color(0.80, 0.25, 0.80), Color(0.85, 0.35, 0.50), Color(0.45, 0.45, 0.50), Color(0.12, 0.12, 0.16)]
const NO_DISTANCE_M := 1.0e8
const NO_DISTANCE_BAND := 12

static var _lut := PackedFloat32Array()


static func _srgb_lut() -> PackedFloat32Array:
	if _lut.is_empty():
		_lut.resize(256)
		for i in 256:
			var c := float(i) / 255.0
			_lut[i] = c / 12.92 if c <= 0.04045 else pow((c + 0.055) / 1.055, 2.4)
	return _lut


static func band_of(distance: float) -> int:
	if is_nan(distance) or distance < 0.0 or distance >= NO_DISTANCE_M:
		return NO_DISTANCE_BAND
	for b in range(NAMES.size() - 1):
		if distance < float(EDGES_M[b + 1]):
			return b
	return NO_DISTANCE_BAND


## Resamples a depth-effect result ({size: Vector2i, depth: PackedFloat32Array}) to a w x h distance array (nearest neighbour). Missing depth gives all "no distance".
static func distance_from_depth(depth_data: Dictionary, w: int, h: int) -> PackedFloat32Array:
	var out := PackedFloat32Array()
	out.resize(w * h)
	if depth_data.is_empty():
		out.fill(NO_DISTANCE_M)
		return out
	var depth: PackedFloat32Array = depth_data["depth"]
	var size: Vector2i = depth_data["size"]
	for y in h:
		var row := mini(size.y - 1, int(float(y) / float(h) * size.y)) * size.x
		for x in w:
			out[y * w + x] = depth[row + mini(size.x - 1, int(float(x) / float(w) * size.x))]
	return out


## Per-band table. `image` is the RGBA8 screenshot (w x h), `distance` the per-pixel metres (w x h, see distance_from_depth), `stride` the sampling step.
## Each row: {name, pixels (estimated at full resolution), share, mean_luma (linear, Rec.709), mean_saturation (HSV of the sRGB values), local_contrast (mean |L - 3x3 box mean|
## at full resolution), luma_p10, luma_p90}. Empty bands have pixels 0 and NAN statistics.
static func compute(image: Image, distance: PackedFloat32Array, w: int, h: int, stride: int) -> Array:
	if image.get_format() != Image.FORMAT_RGBA8:
		image = image.duplicate()
		image.convert(Image.FORMAT_RGBA8)
	var lut := _srgb_lut()
	var data := image.get_data()
	var luma := PackedFloat32Array()
	luma.resize(w * h)
	for i in w * h:
		var o := i * 4
		luma[i] = 0.2126 * lut[data[o]] + 0.7152 * lut[data[o + 1]] + 0.0722 * lut[data[o + 2]]
	var count := PackedInt32Array(); count.resize(NAMES.size())
	var sum_luma := PackedFloat64Array(); sum_luma.resize(NAMES.size())
	var sum_sat := PackedFloat64Array(); sum_sat.resize(NAMES.size())
	var sum_contrast := PackedFloat64Array(); sum_contrast.resize(NAMES.size())
	var samples: Array = []
	for b in NAMES.size():
		samples.append(PackedFloat32Array())
	var total := 0
	var step := maxi(1, stride)
	for y in range(1, h - 1, step):
		for x in range(1, w - 1, step):
			var i := y * w + x
			var b := band_of(distance[i])
			var l := luma[i]
			var box := (luma[i - w - 1] + luma[i - w] + luma[i - w + 1] + luma[i - 1] + l + luma[i + 1] + luma[i + w - 1] + luma[i + w] + luma[i + w + 1]) / 9.0
			var o := i * 4
			var hi := maxi(data[o], maxi(data[o + 1], data[o + 2]))
			var lo := mini(data[o], mini(data[o + 1], data[o + 2]))
			count[b] += 1
			sum_luma[b] += l
			sum_sat[b] += float(hi - lo) / float(hi) if hi > 0 else 0.0
			sum_contrast[b] += absf(l - box)
			samples[b].append(l)
			total += 1
	var table: Array = []
	for b in NAMES.size():
		var n := count[b]
		var row := {"name": NAMES[b], "pixels": n * step * step, "share": float(n) / float(maxi(1, total))}
		if n == 0:
			row.merge({"mean_luma": NAN, "mean_saturation": NAN, "local_contrast": NAN, "luma_p10": NAN, "luma_p90": NAN})
		else:
			var sorted: PackedFloat32Array = samples[b]
			sorted.sort()
			row.merge({"mean_luma": sum_luma[b] / n, "mean_saturation": sum_sat[b] / n, "local_contrast": sum_contrast[b] / n,
				"luma_p10": sorted[int(float(n - 1) * 0.10)], "luma_p90": sorted[int(float(n - 1) * 0.90)]})
		table.append(row)
	return table


## False-colour band map (RGB8, w x h), one fixed colour per band.
static func band_image(distance: PackedFloat32Array, w: int, h: int) -> Image:
	var bytes := PackedByteArray()
	bytes.resize(w * h * 3)
	for i in w * h:
		var c: Color = COLORS[band_of(distance[i])]
		bytes[i * 3] = int(c.r * 255.0)
		bytes[i * 3 + 1] = int(c.g * 255.0)
		bytes[i * 3 + 2] = int(c.b * 255.0)
	return Image.create_from_data(w, h, false, Image.FORMAT_RGB8, bytes)


## Mean-luma step between neighbouring non-empty distance bands ("no distance" excluded): [{from, to, step}], step = later minus nearer. The V0 tier metric.
static func adjacent_steps(table: Array) -> Array:
	var steps: Array = []
	var previous := {}
	for b in NO_DISTANCE_BAND:
		var row: Dictionary = table[b]
		if int(row["pixels"]) == 0:
			continue
		if not previous.is_empty():
			steps.append({"from": previous["name"], "to": row["name"], "step": float(row["mean_luma"]) - float(previous["mean_luma"])})
		previous = row
	return steps


## Number of non-empty bands, "no distance" excluded.
static func non_empty_bands(table: Array) -> int:
	var n := 0
	for b in NO_DISTANCE_BAND:
		if int(table[b]["pixels"]) > 0:
			n += 1
	return n
