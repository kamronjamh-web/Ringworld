extends SceneTree

# Small isolated Sunshine compositor timing smoke. It uses actual RenderingDevice
# timestamps; no synthetic GPU events are accepted by the C# verifier.
const PROBE := preload("res://scripts/Tools/PerformanceDiagnosticsContractTest.cs")
const SIZE := Vector2i(320, 180)
var monitor: Node
var probe: Node
var viewport_3d: SubViewport
var world: WorldEnvironment
var adapter: Node
var driver: Node
var bridge: Node
var failed := false

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		fail("RenderingDevice required")
		return
	call_deferred("run")

func run() -> void:
	viewport_3d = SubViewport.new(); viewport_3d.own_world_3d = true; viewport_3d.size = SIZE; viewport_3d.render_target_update_mode = SubViewport.UPDATE_DISABLED; root.add_child(viewport_3d)
	monitor = preload("res://scripts/diagnostics/RingPerformanceMonitor.cs").new()
	monitor.set("Enabled", true); monitor.set("DetailedGpuEnabled", true); monitor.set("HistorySeconds", 60); monitor.set("MemoryBudgetMiB", 16)
	monitor.call("InstallSettingsPathForTests", "res://.godot/cloud-profiler-gpu/diagnostics.tres")
	monitor.call("InstallReportDirectoryForTests", "res://.godot/cloud-profiler-gpu/reports")
	probe = PROBE.new(); root.add_child(probe)
	var env := Environment.new(); env.background_mode = Environment.BG_COLOR; env.background_color = Color(.08, .11, .17)
	world = WorldEnvironment.new(); world.environment = env; viewport_3d.add_child(world)
	var camera := Camera3D.new(); camera.current = true; camera.near = 10; camera.far = 10000000; camera.position = Vector3(0, 20000, 0); camera.rotation_degrees = Vector3(-8, 20, 0); viewport_3d.add_child(camera)
	var sun := DirectionalLight3D.new(); sun.light_energy = 2; viewport_3d.add_child(sun); sun.look_at(Vector3(.4, -.75, .5), Vector3.UP)
	var ground := MeshInstance3D.new(); var plane := PlaneMesh.new(); plane.size = Vector2(600000, 600000); ground.mesh = plane; viewport_3d.add_child(ground)
	adapter = preload("res://scripts/ui/MenuFlyoverSunshineLayer.cs").new(); root.add_child(adapter)
	adapter.call("Configure", world, 1337, 5000000.0, 318000, Vector3(-.4,.75,-.5).normalized())
	var settings := preload("res://scripts/ui/MenuFlyoverSettings.cs").new()
	settings.set("CloudsEnabled", true); settings.set("CloudShadowsEnabled", true); settings.set("ShadowGenerationEnabled", true)
	adapter.call("ApplyTuning", settings, Vector3(-.4,.75,-.5).normalized())
	var effect: Object = adapter.get("Effect") as Object
	if effect == null: return fail("Sunshine effect unavailable")
	bridge = probe.call("CreateCloudHistoryBridgeForTests", effect) as Node; viewport_3d.add_child(bridge)
	monitor.name = "PerformanceMonitor"; viewport_3d.add_child(monitor)
	driver = preload("res://addons/SunshineClouds2/SunshineCloudsDriver.gd").new(); driver.name = "SunshineCloudsDriver"; driver.set("update_continuously", false); driver.set("clouds_resource", effect); viewport_3d.add_child(driver)
	await process_frame
	if not bool(probe.call("ConfigureEffectProfiler", effect)): return fail("could not configure Sunshine profiler markers/cascades")
	for frame in 50:
		# Keep near work pending to exercise producer fairness, including a far
		# request already pending from ConfigureEffectProfiler.
		effect.call("request_gpu_cloud_shadow_update", 0)
		camera.rotation_degrees = Vector3(-8, 20 + frame * .5, 0)
		adapter.call("UpdateFrame", 17219375.0 + frame * 2.0, frame / 60.0, false)
		driver.call("_process", 1.0 / 60.0)
		viewport_3d.render_target_update_mode = SubViewport.UPDATE_ONCE
		await RenderingServer.frame_post_draw
		if frame == 4:
			var shadow_status: Dictionary = effect.call("get_gpu_cloud_shadow_status")
			if int(shadow_status.get("far_dispatches", 0)) == 0:
				return fail("pending far cascade starved by recurring near requests")
	for frame in 12: await RenderingServer.frame_post_draw
	var verdict: Dictionary = probe.call("ValidateCloudGpuProfiler", monitor)
	if not bool(verdict.get("pass", false)): return fail("missing actual Sunshine GPU timing rows: " + JSON.stringify(verdict))
	var report := str(monitor.call("CaptureSlowdown"))
	if report.begins_with("Export failed") or report.begins_with("Export refused"): return fail("report export failed: " + report)
	var exported: Dictionary = probe.call("ValidateExport", report)
	if not bool(exported.get("pass", false)): return fail("v2 profiler export invalid: " + JSON.stringify(exported))
	var history: Dictionary = probe.call("ReadCloudHistoryDiagnostics", monitor)
	if int(history.get("count", 0)) <= 0: return fail("cloud history did not capture bridge renderer state: " + JSON.stringify(history))
	print("CLOUD_PROFILER_GPU_SMOKE=PASS report=", report)
	cleanup(0)

func fail(reason: String) -> void:
	if failed: return
	failed = true; push_error("CLOUD_PROFILER_GPU_SMOKE=FAIL " + reason); cleanup(1)

func cleanup(code: int) -> void:
	if adapter != null: adapter.call("Detach")
	if driver != null: driver.queue_free()
	if bridge != null: bridge.queue_free()
	if probe != null: probe.queue_free()
	if monitor != null: monitor.queue_free()
	if viewport_3d != null: viewport_3d.queue_free()
	quit(code)
