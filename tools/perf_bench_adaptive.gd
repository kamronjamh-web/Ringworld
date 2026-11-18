extends RefCounted
## Stop rule of the adaptive stand windows of tools/perf_bench.gd (docs/perf-bench.md "Adaptive windows"). Pure functions, no SceneTree, so a headless test can call them
## (tools/perf_bench_adaptive_rule_test.gd). Times are ms per frame.

const MIN_ELAPSED_S := 4.0
const MIN_FRAMES := 300
const BLOCK_S := 2.0
const TOL_FLOOR_MS := 0.05
const TOL_REL := 0.005


## wall and gpu: per-frame ms so far. block_starts: the frame index at each 2 s block boundary, starting with 0 and ending with the boundary being checked now
## (so block i is [block_starts[i], block_starts[i + 1]) and every block but the last entry's successor is complete). elapsed_s: window time so far.
## Settled only when elapsed_s >= 4 and frames >= 300 and, for wall and gpu, the 95 % CI half-width of the median and the drift between the first and the
## last complete 2 s block are both within tol = max(0.05 ms, 0.5 % of the median).
static func check(wall: PackedFloat64Array, gpu: PackedFloat64Array, block_starts: PackedInt32Array, elapsed_s: float) -> Dictionary:
	var n := wall.size()
	var res := {"settled": false, "n": n, "blocks": maxi(0, block_starts.size() - 1), "wall": _stat(wall, block_starts), "gpu": _stat(gpu, block_starts)}
	if elapsed_s < MIN_ELAPSED_S or n < MIN_FRAMES or block_starts.size() < 3:
		return res
	res["settled"] = _ok(res["wall"]) and _ok(res["gpu"])
	return res


static func _ok(s: Dictionary) -> bool:
	return float(s["half"]) <= float(s["tol"]) and float(s["drift"]) <= float(s["tol"])


static func _stat(v: PackedFloat64Array, block_starts: PackedInt32Array) -> Dictionary:
	var n := v.size()
	if n == 0:
		return {"median": 0.0, "half": 1.0e9, "drift": 1.0e9, "tol": TOL_FLOOR_MS}
	var s := v.duplicate()
	s.sort()
	var med := _median_sorted(s)
	var tol := maxf(TOL_FLOOR_MS, TOL_REL * med)
	var spread := 1.96 * sqrt(float(n))
	var lo := clampi(int(floor((float(n) - spread) / 2.0)), 1, n)
	var hi := clampi(int(ceil((float(n) + spread) / 2.0)), 1, n)
	var half := (s[hi - 1] - s[lo - 1]) / 2.0
	var drift := 1.0e9
	var k := block_starts.size()
	if k >= 3:
		var first := v.slice(block_starts[0], block_starts[1])
		var last := v.slice(block_starts[k - 2], block_starts[k - 1])
		if not first.is_empty() and not last.is_empty():
			first.sort()
			last.sort()
			drift = absf(_median_sorted(first) - _median_sorted(last))
	return {"median": med, "half": half, "drift": drift, "tol": tol}


static func _median_sorted(s: PackedFloat64Array) -> float:
	var k := s.size()
	if k == 0:
		return 0.0
	if k % 2 == 1:
		return s[k / 2]
	return (s[k / 2 - 1] + s[k / 2]) / 2.0
