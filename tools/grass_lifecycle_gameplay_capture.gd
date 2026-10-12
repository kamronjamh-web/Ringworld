extends SceneTree

# Serial real-gameplay diagnostic. Snapshot work is outside telemetry collection.
var world: Node
var grass: Node3D
var driver: RefCounted
var finished := false
var suite_directory := ""
var active_phase := ""
var results: Array = []
var restore: Callable
var elevated_meters := 3000.0
var regenerate_seconds := 45.0
var initial_settings: Dictionary = {}
var baseline_offset_meters := -100.0
var baseline_only := false
var frozen_cloud: float
var frozen_celestial: float

func _initialize():
 call_deferred("_run")

func _run():
 driver = load("res://tools/FarRingGameplayCapture.cs").new()
 if not driver.ConfigureArguments():
  _fail(driver.ErrorText)
  return
 var args := OS.get_cmdline_user_args()
 for i in range(args.size()):
  if args[i] == "--baseline-only": baseline_only = true
  if args[i] in ["--altitude-offset-meters", "--regenerate-seconds", "--baseline-altitude-offset-meters"]:
   if i + 1 >= args.size() or not args[i + 1].is_valid_float():
    _fail("Missing/invalid lifecycle argument " + args[i])
    return
   var value := float(args[i + 1])
   if not is_finite(value):
    _fail("Non-finite lifecycle argument")
    return
   if args[i] == "--altitude-offset-meters": elevated_meters = value
   elif args[i] == "--baseline-altitude-offset-meters": baseline_offset_meters = value
   else: regenerate_seconds = value
 if baseline_offset_meters < -400 or baseline_offset_meters > 0 or elevated_meters < 2000 or elevated_meters > 5000 or regenerate_seconds < 1 or regenerate_seconds > 90:
  _fail("Baseline offset must be -400..0m; ascent 2000..5000m; regeneration timeout 1..90s")
  return
 if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
  _fail("Forward+ display/RenderingDevice required")
  return
 create_timer(driver.WarmupSeconds + 3 * regenerate_seconds + 8 * (driver.CollectionSeconds + driver.SettleSeconds + 20) + 30, true, false, true).timeout.connect(func(): _fail("Lifecycle watchdog expired"))
 suite_directory = "res://.godot/far-ring-gameplay/%s-lifecycle-%d-%d" % [driver.ArtifactLabel, OS.get_process_id(), Time.get_ticks_usec()]
 world = load("res://scenes/VoxelRingIntegrationTest.tscn").instantiate()
 var launcher = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
 root.add_child(launcher)
 launcher.call("_on_certified_preset_pressed")
 launcher.call("_configure_game", world, 5000000.0)
 launcher.free()
 if not driver.Prepare(world, suite_directory):
  _fail(driver.ErrorText)
  return
 root.add_child(world)
 await process_frame
 if not driver.SetPose(world):
  _fail(driver.ErrorText)
  return
 Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
 grass = world.get_node("StandingGrass")
 var enabled = grass.Enabled
 var clumps = grass.UseGrassClumps
 initial_settings = {"enabled":enabled,"useClumps":clumps,"settings":grass.CaptureGrassStreamingDiagnostics().get("settings", {}),"forcedBaselineEnabled":true}
 var weather = world.get_node("RingVolumetricClouds")
 var celestial = world.get_node("RingDayNightCycle")
 var cloud_paused = weather.CloudSimulationPaused
 var celestial_paused = celestial.Paused
 restore = func():
  if is_instance_valid(grass):
   grass.Enabled = enabled
   grass.UseGrassClumps = clumps
  if is_instance_valid(weather): weather.CloudSimulationPaused = cloud_paused
  if is_instance_valid(celestial): celestial.Paused = celestial_paused
 grass.Enabled = true
 if not driver.SetPoseAltitudeOffset(world, baseline_offset_meters):
  _fail(driver.ErrorText)
  return
 _manifest("RUNNING", "")
 await create_timer(maxf(.001, driver.WarmupSeconds), true, false, true).timeout
 if finished: return
 if not await _populate_baseline(): return
 weather.CloudSimulationPaused = true
 celestial.Paused = true
 frozen_cloud = weather.CloudSimulationTimeSeconds
 frozen_celestial = celestial.GetSimulationSeconds()
 if not await _phase("baseline-enabled"): return
 grass.Enabled = false
 if not await _phase("baseline-direct-disabled"): return
 grass.Enabled = true
 if not await _populate_baseline(): return
 if not await _phase("baseline-reenabled"): return
 if baseline_only:
  await _complete()
  return
 if not driver.SetPoseAltitudeOffset(world, baseline_offset_meters + elevated_meters):
  _fail(driver.ErrorText)
  return
 if not await _phase("elevated-before-clump-reset"): return
 grass.UseGrassClumps = not clumps
 await process_frame
 await process_frame
 await create_timer(driver.SettleSeconds, true, false, true).timeout
 if finished: return
 grass.UseGrassClumps = clumps
 await process_frame
 await process_frame
 if not await _phase("elevated-after-clump-reset"): return
 grass.Enabled = false
 if not await _phase("elevated-reset-disabled"): return
 if not driver.SetPoseAltitudeOffset(world, baseline_offset_meters):
  _fail(driver.ErrorText)
  return
 if not await _phase("baseline-reset-disabled"): return
 grass.Enabled = true
 if not await _populate_baseline(): return
 if not await _phase("baseline-final-regenerated"): return
 await _complete()

func _complete():
 if restore.is_valid(): restore.call()
 if not _manifest("PASS", ""):
  _fail("Cannot write final manifest")
  return
 finished = true
 print("GRASS_LIFECYCLE=PASS directory=", suite_directory)
 world.queue_free()
 await process_frame
 await process_frame
 quit(0)

func _populate_baseline() -> bool:
 await process_frame
 await process_frame
 if grass.StreamingSuspended:
  _fail("Baseline altitude is suspended; choose a lower explicit baseline offset")
  return false
 return await _regenerate()

func _regenerate() -> bool:
 var deadline := Time.get_ticks_usec() + int(regenerate_seconds * 1000000)
 while not finished and Time.get_ticks_usec() < deadline:
  if grass.ResidentGrassInstanceCount > 0 and grass.IsGrassStreamingDiagnosticsSettled: return true
  await create_timer(.25, true, false, true).timeout
 if not finished: _fail("Grass regeneration did not settle with populated roots within bounded timeout")
 return false

func _phase(name: String) -> bool:
 active_phase = name
 if not _manifest("RUNNING", ""):
  _fail("Cannot write running suite manifest")
  return false
 await create_timer(driver.SettleSeconds, true, false, true).timeout
 if finished: return false
 if not driver.SetCaseContext(world, "grass-lifecycle", name):
  _fail(driver.ErrorText)
  return false
 var begin = _snapshot()
 if not _clocks_match(begin):
  _fail("Authoritative clocks drifted before " + name)
  return false
 if not driver.BeginCollection(world):
  _fail(driver.ErrorText)
  return false
 print("GRASS_LIFECYCLE=COLLECT phase=", name)
 await create_timer(driver.CollectionSeconds, true, false, true).timeout
 if finished: return false
 driver.BeginCapture(world)
 while driver.Status == "CAPTURING" and not finished: await process_frame
 if finished: return false
 if driver.Status != "DONE":
  _fail(driver.ErrorText)
  return false
 var end = _snapshot()
 if not _clocks_match(end):
  _fail("Authoritative clocks drifted during " + name)
  return false
 await RenderingServer.frame_post_draw
 if finished: return false
 if root.get_texture().get_image().save_png(driver.BundleDirectory.path_join("main.png")) != OK:
  _fail("Main gameplay image save failed")
  return false
 var result = {"phase":name,"status":"PASS","bundle":driver.BundleDirectory,"begin":begin,"end":end}
 results.append(result)
 var file = FileAccess.open(driver.BundleDirectory.path_join("grass-lifecycle-phase.json"), FileAccess.WRITE)
 if file == null:
  _fail("Cannot write phase manifest")
  return false
 file.store_string(JSON.stringify(result, "  "))
 file.close()
 if not _manifest("RUNNING", ""):
  _fail("Cannot write completed phase manifest")
  return false
 return true

func _clocks_match(state: Dictionary) -> bool:
 return state.cloudPaused and state.celestialPaused and state.cloudClock == frozen_cloud and state.celestialClock == frozen_celestial

func _snapshot() -> Dictionary:
 var geometry: Array = []
 var pending: Array = [grass]
 var visible := 0
 var layered := 0
 var shadowed := 0
 var instances := 0
 var visible_instances := 0
 while not pending.is_empty():
  var node: Node = pending.pop_back()
  if not is_instance_valid(node) or node.is_queued_for_deletion(): continue
  pending.append_array(node.get_children(true))
  if not node is GeometryInstance3D: continue
  var item = {"path":str(grass.get_path_to(node)),"visible":node.visible,"visibleInTree":node.is_visible_in_tree(),"layers":node.layers,"castShadow":node.cast_shadow}
  if node.is_visible_in_tree(): visible += 1
  if node.layers != 0: layered += 1
  if node.cast_shadow != GeometryInstance3D.SHADOW_CASTING_SETTING_OFF: shadowed += 1
  if node is MultiMeshInstance3D and node.multimesh != null:
   var multi: MultiMesh = node.multimesh
   item["nativeInstanceCount"] = multi.instance_count
   item["nativeVisibleInstanceCount"] = multi.visible_instance_count
   instances += multi.instance_count
   visible_instances += multi.instance_count if multi.visible_instance_count < 0 else multi.visible_instance_count
  geometry.append(item)
 var weather = world.get_node("RingVolumetricClouds")
 var celestial = world.get_node("RingDayNightCycle")
 return {"enabled":grass.Enabled,"useClumps":grass.UseGrassClumps,"processing":grass.is_processing(),"visible":grass.visible,
  "geometryTotal":geometry.size(),"geometryVisible":visible,"geometryLayered":layered,"geometryShadowed":shadowed,
  "nativeInstances":instances,"nativeVisibleInstances":visible_instances,"geometry":geometry,
  "streamingSuspended":grass.StreamingSuspended,
  "altitudeAboveTerrain":world.get_node("Explorer").CurrentAltitudeAboveTerrainMeters,
  "computedAltitudeAboveTerrain":driver.ComputedAltitudeAboveTerrainMeters,
  "cachedAltitudeAboveTerrain":driver.CachedAltitudeAboveTerrainMeters,
  "maximumViewerAltitudeAboveTerrainMeters":grass.MaximumViewerAltitudeAboveTerrainMeters,
  "grass":grass.CaptureGrassStreamingDiagnostics(),"gpuStatus":grass.GpuCullingStatus,
  "gpuSkippedUnchangedUpdates":grass.GpuCullingSkippedUnchangedUpdates,
  "detailedGpuEnabled":world.get_node("RingPerformanceMonitor").DetailedGpuEnabled,
  "cloudClock":weather.CloudSimulationTimeSeconds,"cloudPaused":weather.CloudSimulationPaused,
  "celestialClock":celestial.GetSimulationSeconds(),"celestialPaused":celestial.Paused}

func _manifest(status: String, reason: String) -> bool:
 if suite_directory.is_empty(): return false
 DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(suite_directory))
 var file = FileAccess.open(suite_directory.path_join("grass-lifecycle-suite.json"), FileAccess.WRITE)
 if file == null: return false
 file.store_string(JSON.stringify({"schemaVersion":1,"status":status,"reason":reason,"activePhase":active_phase,"results":results,
  "initialSettings":initial_settings,"baselineAltitudeOffsetMeters":baseline_offset_meters,
  "baselineCanonicalHeightMeters":397.3149763485417 + baseline_offset_meters,
  "baselineOnly":baseline_only,"expectedPhaseCount":3 if baseline_only else 8,
  "altitudeOffsetMeters":elevated_meters,"regenerationTimeoutSeconds":regenerate_seconds,
  "comparison":"Baseline-only is enabled/disabled/reenabled at an identical explicitly reported pose; enabled endpoints expose drift. Full lifecycle compares baseline-direct-disabled with baseline-reset-disabled at that same pose. Elevated phases document cleanup only; changed-view timing is not causal evidence.",
  "nativeCountLimit":"MultiMesh counts are native allocation/visibility counts; indirect GPU output is not a GPU readback. Unexposed compute retirement queue counts unavailable.",
  "altitudePolicy":"Physics held stationary; cached altitude refreshed from canonical terrain query after each tool relocation.",
  "clockPolicy":"Normal warmup/regeneration followed by frozen authoritative cloud and celestial clocks; checked at every phase boundary."}, "  "))
 file.close()
 return true

func _fail(reason):
 if finished: return
 finished = true
 printerr("GRASS_LIFECYCLE=FAILED ", reason)
 _manifest("FAILED", str(reason))
 if restore.is_valid(): restore.call()
 if is_instance_valid(world):
  if world.is_inside_tree(): world.queue_free()
  else: world.free()
 await process_frame
 quit(1)
