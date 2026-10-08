extends SceneTree

const PROBE := preload("res://scripts/Tools/PerformanceFarViewportGpuProbe.cs")
var fixture: Node
var failed := false
var output := ""

func _initialize() -> void:
    if DisplayServer.get_name() == "headless" or RenderingServer.get_rendering_device() == null:
        fail("requires Forward+ display session"); return
    call_deferred("run")

func run() -> void:
    create_timer(45.0).timeout.connect(func(): fail("watchdog expired"))
    output = "res://.godot/performance_far_gpu_%d_%d" % [OS.get_process_id(), Time.get_ticks_usec()]
    DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output))
    fixture = Node.new(); root.add_child(fixture)
    var probe := PROBE.new()
    var monitor: Node = probe.CreateMonitor(fixture, output)
    monitor.call("_Process", 0.0)
    var missing: Dictionary = probe.Observe(monitor)
    if not check(not missing.outer_ready and not missing.regional_ready, "missing owner did not remain null"): return
    var owner: Node = probe.CreateOwner(fixture)
    monitor.call("_Process", .6) # bounded discovery, same source process frame
    var guarded: Dictionary = probe.Observe(monitor)
    if not check(not guarded.outer_ready and not guarded.regional_ready, "new measurement skipped startup guard"): return
    var observed := await wait_for_timings(probe, monitor)
    if failed: return
    print("PERFORMANCE_FAR_GPU delayed_readings=" + str(observed))
    if not check(probe.RendererMeasurementCallsMatch(owner, true), "real measurement enable commands missing"): return
    var report := read_report(monitor)
    if not check_report(report, true): return
    var bundle := await capture_bundle(probe, monitor)
    if failed or not check_bundle(bundle, true): return
    if not check(String(probe.get("ConcurrentCaptureResult")).begins_with("Export refused:"), "concurrent capture was not refused"): return
    monitor.set("Enabled", false); monitor.call("_Process", 0.0)
    for frame in range(3): await RenderingServer.frame_post_draw
    if not check(probe.RendererMeasurementCallsMatch(owner, false), "monitor off did not issue actual renderer disable commands"): return
    var disabled := read_report(monitor)
    if not check_report(disabled, false): return
    monitor.set("Enabled", true); monitor.call("_Process", 0.0)
    guarded = probe.Observe(monitor)
    if not check(not guarded.outer_ready and not guarded.regional_ready, "re-enable exposed immediate stale readings"): return
    observed = await wait_for_timings(probe, monitor)
    if failed: return
    # Canonical live replacement is detected by production bounded discovery.
    owner.name = "RetiredScaledFarViewport"
    var replacement: Node = probe.CreateOwner(fixture)
    monitor.call("_Process", .6)
    guarded = probe.Observe(monitor)
    if not check(not guarded.outer_ready and not guarded.regional_ready, "replacement did not restart measurement guard"): return
    await RenderingServer.frame_post_draw
    if not check(probe.RendererMeasurementCallsMatch(owner, false), "replacement did not disable old renderer measurement"): return
    owner.free()
    observed = await wait_for_timings(probe, monitor)
    if failed: return
    bundle = await capture_bundle(probe, monitor, replacement)
    if failed or not check_bundle(bundle, false): return
    monitor.call("_Process", .6)
    observed = probe.Observe(monitor)
    if not check(not observed.outer_ready and not observed.regional_ready, "freed owner left readable timings"): return
    if not check_report(read_report(monitor), false): return
    var exit_owner: Node = probe.CreateOwner(fixture)
    monitor.call("_Process", .6)
    observed = await wait_for_timings(probe, monitor)
    if failed: return
    bundle = await capture_bundle(probe, monitor, null, true)
    if failed or not check_bundle(bundle, false): return
    fixture.queue_free()
    for frame in range(3): await process_frame
    print("PERFORMANCE_FAR_VIEWPORT_GPU=PASS outer,regional,late_owner,startup_guard,disabled,reenabled,replacement,freed,json_bins,render_state,bundle_pngs,busy_guard,owner_removed,monitor_exit output=" + output)
    quit(0)

func wait_for_timings(probe: RefCounted, monitor: Node) -> Dictionary:
    # Wait for actual renderer values; two process frames alone do not certify
    # GPU freshness. This checks presence, never durations or performance.
    for frame in range(120):
        await process_frame
        await RenderingServer.frame_post_draw
        monitor.call("_Process", 1.0 / 60.0)
        var observed: Dictionary = probe.Observe(monitor)
        if bool(observed.outer_ready) and bool(observed.regional_ready): return observed
    fail("separate production viewport timings did not become available")
    return {}

func read_report(monitor: Node) -> Dictionary:
    var path: String = monitor.call("CaptureSlowdown")
    if not FileAccess.file_exists(path): fail("report export failed: " + path); return {}
    var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
    if not parsed is Dictionary: fail("report JSON invalid"); return {}
    return parsed

func capture_bundle(probe: RefCounted, monitor: Node, remove_owner: Node = null, remove_monitor := false) -> Dictionary:
    probe.call("BeginCapture", monitor, remove_owner, remove_monitor)
    for frame in range(120):
        if bool(probe.get("CaptureFinished")):
            var directory := String(probe.get("CaptureResult"))
            var path := directory.path_join("report.json")
            if not FileAccess.file_exists(path): fail("far capture bundle failed: " + directory); return {}
            var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
            if not parsed is Dictionary: fail("far bundle JSON invalid"); return {}
            parsed["fixture_directory"] = directory
            return parsed
        await process_frame
    fail("far bundle export did not finish")
    return {}

func check_bundle(bundle: Dictionary, expect_images: bool) -> bool:
    if failed: return false
    if not check(bundle.has("reportStatus") and bundle.has("imageErrors"), "focused report status/errors missing"): return false
    var measurement: Dictionary = bundle.measurement
    if not check(measurement.summary.outerGpu != null and measurement.summary.regionalGpu != null, "retained separate timing summaries missing"): return false
    if not check(measurement.outer.visible != null and measurement.regional.visible != null, "capture-time visible workload missing"): return false
    if not check(int(measurement.outer.visible.drawCalls) > 0 and int(measurement.regional.visible.drawCalls) > 0, "actual viewport draw counters unavailable"): return false
    if not check(measurement.farRingCpu.has("summary") and measurement.farRingCpu.has("events") and String(measurement.farRingCpu.note).contains("must not be summed"), "separate CPU attribution/caveat missing"): return false
    var inclusive_found := false
    for row: Dictionary in measurement.farRingCpu.summary:
        inclusive_found = inclusive_found or (row.name == "Far ring update (inclusive)" and int(row.milliseconds.sampleCount) > 0)
    if not check(inclusive_found, "actual controller CPU scope absent from focused capture"): return false
    for image: Dictionary in bundle.images:
        if expect_images:
            if not check(image.Status == "saved" and FileAccess.file_exists(String(bundle.fixture_directory).path_join(String(image.File))), "actual viewport PNG missing"): return false
            var pixels := Image.load_from_file(String(bundle.fixture_directory).path_join(String(image.File)))
            if not check(not pixels.is_empty(), "PNG is not decodable"): return false
        elif not check(image.Status != "saved" and image.File == null, "removed owner/monitor produced mismatched snapshot image"): return false
    return true

func check_report(report: Dictionary, active: bool) -> bool:
    if failed: return false
    var far: Dictionary = report.renderState.farViewports
    for kind: String in ["outer", "regional"]:
        var state: Dictionary = far[kind]
        if not check(bool(state.active) == active, kind + " renderState lifecycle mismatch"): return false
        if active:
            if not check(state.cpuMilliseconds != null and state.gpuMilliseconds != null, kind + " renderState omitted separate timings"): return false
        else:
            if not check(state.cpuMilliseconds == null and state.gpuMilliseconds == null, kind + " inactive renderState retained timings"): return false
    if not check(String(far.note).contains("not CPU-frame paired"), "timing caveat missing"): return false
    var bins: Array = report.captureOnlyOneSecondBins
    if not check(not bins.is_empty(), "capture bins unavailable"): return false
    var any_outer := false; var any_regional := false
    for bin: Dictionary in bins:
        if not check(bin.has("outerFarViewportGpuMilliseconds") and bin.has("regionalFarViewportGpuMilliseconds"), "separate bin fields missing"): return false
        any_outer = any_outer or (bin.outerFarViewportGpuMilliseconds != null and int(bin.outerFarViewportGpuMilliseconds.sampleCount) > 0)
        any_regional = any_regional or (bin.regionalFarViewportGpuMilliseconds != null and int(bin.regionalFarViewportGpuMilliseconds.sampleCount) > 0)
    return check(any_outer and any_regional, "bins have no separate measured samples")

func check(condition: bool, reason: String) -> bool:
    if not condition: fail(reason)
    return condition

func fail(reason: String) -> void:
    if failed: return
    failed = true
    if fixture != null and is_instance_valid(fixture): fixture.queue_free()
    push_error("PERFORMANCE_FAR_VIEWPORT_GPU=FAIL " + reason); quit(1)
