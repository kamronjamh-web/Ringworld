extends RefCounted

static func haze_state(diagnostics: Dictionary) -> Dictionary:
	return {"model": int(diagnostics["haze_model"]), "quality": int(diagnostics["haze_quality"]), "enabled": bool(diagnostics["enabled"])}

static func restore_haze(drawer: Object, state: Dictionary) -> void:
	drawer.call("SetAtmosphereHazeModel", state["model"])
	drawer.call("SetAtmosphereHazeQuality", state["quality"])
	drawer.call("SetAtmosphereEnabled", state["enabled"])

## Capture qualification is separate from completing the requested recording.
static func stationary_invalid(settled: bool, busy_frames: int, frames: int) -> String:
	if not settled:
		return "streaming settle timed out"
	if busy_frames > 0:
		return "streaming resumed during stationary window"
	if frames <= 0:
		return "empty sample window"
	return ""

static func flight_tier(speed: float) -> int:
	if is_equal_approx(speed, 72.0):
		return 1
	if is_equal_approx(speed, 240.0):
		return 2
	return -1

static func flight_invalid(accepted: bool, settled: bool, target: float, actual_median: float) -> String:
	if not accepted or not settled:
		return "flight teleport or settle failed"
	if flight_tier(target) < 0 or not is_finite(actual_median) or absf(actual_median - target) > target * 0.1:
		return "measured flight speed differs from requested tier"
	return ""

static func walk_invalid(accepted: bool, settled: bool, grounded: bool, noclip: bool, travel_speed: float, grounded_fraction: float, blocked_fraction: float) -> String:
	if not accepted or not settled or not grounded or noclip:
		return "walk did not begin grounded with settled terrain"
	if not is_finite(travel_speed) or travel_speed < 1.0 or grounded_fraction < 0.5 or blocked_fraction > 0.5:
		return "walk did not sustain grounded forward travel"
	return ""
