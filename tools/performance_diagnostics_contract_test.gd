extends SceneTree

## Headless-safe behavioural contract for RingPerformanceMonitor.  Settings
## are isolated below res://.godot so this runner cannot modify user:// prefs.
const MONITOR: Script = preload("res://scripts/diagnostics/RingPerformanceMonitor.cs")
const CONTRACT: Script = preload("res://scripts/Tools/PerformanceDiagnosticsContractTest.cs")
const SETTINGS_PATH := "res://.godot/performance_diagnostics/contract-settings.tres"
const REPORT_DIRECTORY := "res://.godot/performance_diagnostics/contract-reports"
const TIMEOUT_SECONDS := 20.0

var _started_usec := 0
var _monitor: Node
var _contract: Node

func _initialize() -> void:
	_started_usec = Time.get_ticks_usec()
	call_deferred("_boot")

func _process(_delta: float) -> bool:
	if float(Time.get_ticks_usec() - _started_usec) / 1000000.0 > TIMEOUT_SECONDS:
		_fail("timeout")
	return false

func _boot() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://.godot/performance_diagnostics"))
	_contract = CONTRACT.new()
	_monitor = MONITOR.new()
	# The monitor test hook is intentionally invoked before entering the tree,
	# preventing its normal user:// path from being read or written.
	_monitor.call("InstallSettingsPathForTests", SETTINGS_PATH)
	_monitor.call("InstallReportDirectoryForTests", ProjectSettings.globalize_path(REPORT_DIRECTORY))
	root.add_child(_contract)
	root.add_child(_monitor)
	await process_frame
	var result: Dictionary = _contract.call("Run", _monitor)
	if not bool(result.get("pass", false)):
		_fail(JSON.stringify(result))
		return
	print("PERFORMANCE_DIAGNOSTICS_CONTRACT=PASS ", JSON.stringify(result))
	_monitor.queue_free()
	_contract.queue_free()
	quit(0)

func _fail(reason: String) -> void:
	push_error("PERFORMANCE_DIAGNOSTICS_CONTRACT=FAIL " + reason)
	if is_instance_valid(_monitor): _monitor.queue_free()
	if is_instance_valid(_contract): _contract.queue_free()
	quit(1)
