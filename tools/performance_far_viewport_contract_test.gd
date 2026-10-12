extends SceneTree

func _initialize() -> void:
    call_deferred("run")

func run() -> void:
    var probe := preload("res://scripts/Tools/PerformanceFarViewportContractTest.cs").new()
    var result: Dictionary = probe.ExecuteFixture(root)
    if not bool(result.get("pass", false)):
        push_error("PERFORMANCE_FAR_VIEWPORT_CONTRACT=FAIL " + str(result)); quit(1); return
    var output := "res://.godot/performance_far_capture_contract_%d_%d" % [OS.get_process_id(), Time.get_ticks_usec()]
    DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output))
    # Unrelated slowdown exports do not consume the far-bundle cap.
    for index in range(25):
        var legacy := FileAccess.open(output.path_join("ring-performance-%d.json" % index), FileAccess.WRITE)
        legacy.store_string("{}"); legacy.close()
    var parent := Node.new(); root.add_child(parent)
    var adapter := preload("res://scripts/Tools/PerformanceFarViewportGpuProbe.cs").new()
    var monitor: Node = adapter.CreateMonitor(parent, output)
    monitor.call("_Process", 0.0)
    adapter.call("BeginCapture", monitor, null, false)
    if not bool(adapter.get("CaptureFinished")):
        push_error("PERFORMANCE_FAR_VIEWPORT_CONTRACT=FAIL missing/headless capture awaited a draw"); quit(1); return
    var directory := String(adapter.get("CaptureResult"))
    var path := directory.path_join("report.json")
    if not FileAccess.file_exists(path):
        push_error("PERFORMANCE_FAR_VIEWPORT_CONTRACT=FAIL focused JSON missing " + directory); quit(1); return
    var report: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(path))
    if report.measurement.outer.available or report.measurement.regional.available or report.imageErrors.size() != 2:
        push_error("PERFORMANCE_FAR_VIEWPORT_CONTRACT=FAIL missing viewports fabricated workload/images"); quit(1); return
    for index in range(19): DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output.path_join("ring-far-viewports-cap_%d" % index)))
    adapter.call("BeginCapture", monitor, null, false)
    if not String(adapter.get("CaptureResult")).begins_with("Export refused:"):
        push_error("PERFORMANCE_FAR_VIEWPORT_CONTRACT=FAIL matching bundle cap not enforced"); quit(1); return
    parent.free()
    print("PERFORMANCE_FAR_VIEWPORT_CONTRACT=PASS " + str(result))
    quit(0)
