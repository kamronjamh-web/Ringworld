extends SceneTree

# Repeats the same 216m ocean route with isolated preferences and no save slot.
# Whole-frame timings include terrain and weather; they are not FFT GPU timings.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const PROBE := preload("res://scripts/Tools/WaterRenderingProbe.cs")
var game: Node
var probe: RefCounted
var ocean: Dictionary
var fft: Node
var samples: Array[float] = []
var phase := 0
var frame := 0
var total_frames := 0
var previous_usec := 0
var report: Array[Dictionary] = []
var started := false
var label := "current"

func _initialize() -> void:
	call_deferred("boot")

func boot() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("WATER_TRAVERSAL=FAIL Forward+ required"); quit(1); return
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--label="): label = arg.trim_prefix("--label=").validate_filename()
	Engine.max_fps = 60
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	game = SCENE.instantiate()
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	game.get_node("LiveTuningDrawer").call("InstallWaterDefaults", "res://artifacts/water_traversal_isolated.tres")
	var launcher: Node = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", game, 5000000.0)
	launcher.free()
	root.add_child(game)
	for i in range(10): await process_frame
	probe = PROBE.new()
	ocean = probe.call("FindOcean", game)
	if not bool(ocean.get("valid", false)):
		push_error("WATER_TRAVERSAL=FAIL no ocean"); quit(1); return
	var explorer := game.get_node("Explorer")
	explorer.set_physics_process(false)
	explorer.set_process_unhandled_input(false)
	fft = game.get_node("WaterFftController")
	fft.set("Resolution", 128); fft.set("CascadeCount", 2)
	fft.set("UpdatesPerSecond", 24); fft.set("GeometryHeightScale", 1.0)
	fft.set("GeometryDistanceMeters", 240.0)
	fft.set("FollowWeatherWind", false); fft.set("WindSpeedMetersPerSecond", 6.0)
	begin_phase()
	started = true

func begin_phase() -> void:
	fft.set("Enabled", phase == 1)
	probe.call("PlaceCamera", game, ocean)
	frame = 0; samples.clear(); previous_usec = 0

func _process(_delta: float) -> bool:
	if not started: return false
	total_frames += 1
	if total_frames > 2100:
		push_error("WATER_TRAVERSAL=FAIL timeout"); quit(1); return true
	frame += 1
	if frame <= 150: return false
	if phase == 1 and not bool(fft.get("Ready")):
		push_error("WATER_TRAVERSAL=FAIL FFT never ready: %s enabled=%s cascades=%s" % [fft.get("Status"), fft.get("Enabled"), fft.get("ActiveCascadeCount")]); quit(1); return true
	var now := Time.get_ticks_usec()
	if previous_usec != 0: samples.append((now - previous_usec) / 1000.0)
	previous_usec = now
	var position := ocean.duplicate()
	position["around"] = float(ocean.around) + minf(samples.size(), 360) * 0.6
	probe.call("PlaceCamera", game, position)
	if samples.size() < 360: return false
	var sorted := samples.duplicate(); sorted.sort()
	var sum := 0.0
	var hitches := 0
	for value in samples:
		sum += value
		if value > 33.333: hitches += 1
	var row := {"phase": ["off_before", "on", "off_after"][phase], "mean_ms": sum / samples.size(), "p95_ms": sorted[int(sorted.size() * .95)], "p99_ms": sorted[int(sorted.size() * .99)], "max_ms": sorted[-1], "over_33ms": hitches, "frames": samples.size()}
	report.append(row)
	print("WATER_TRAVERSAL_WINDOW ", row)
	phase += 1
	if phase < 3:
		begin_phase(); return false
	var path := "res://artifacts/water_traversal_%s.json" % label
	var output := FileAccess.open(path, FileAccess.WRITE)
	if output == null:
		push_error("WATER_TRAVERSAL=FAIL report write"); quit(1); return true
	output.store_string(JSON.stringify({"adapter": RenderingServer.get_video_adapter_name(), "label": label, "route_m": 216, "windows": report}, "\t"))
	output.close()
	print("WATER_TRAVERSAL=PASS ", path)
	game.queue_free(); quit(0); return true
