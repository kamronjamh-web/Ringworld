extends SceneTree

# Serial, opt-in A-B-A captures. Current preferences and rendered quality stay intact.
var world: Node
var driver: RefCounted
var finished := false
var case_restore: Callable
var clock_restore: Callable
var results: Array = []
var suite_directory := ""
var suite_cases: PackedStringArray
var active_case := ""
var active_role := ""
var render_snapshot_count := 0

func _initialize():
 call_deferred("_run")

func _run():
 if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
  _fail("Forward+ display/RenderingDevice required")
  return
 driver = load("res://tools/FarRingGameplayCapture.cs").new()
 if not driver.ConfigureArguments():
  _fail(driver.ErrorText)
  return
 if driver.CollectionSeconds < 8.0 or driver.CollectionSeconds > 10.0:
  _fail("Matched ablation collection must be 8?10 seconds")
  return
 var cases = driver.AblationCases.split(",", false)
 suite_cases = cases
 var watchdog = driver.WarmupSeconds + cases.size() * 3.0 * (driver.CollectionSeconds + driver.SettleSeconds + 15.0) + 30.0
 create_timer(watchdog, true, false, true).timeout.connect(func(): _fail("bounded serial ablation watchdog expired"))
 var directory = "res://.godot/far-ring-gameplay/%s-%d-%d-%d" % [driver.ArtifactLabel, OS.get_process_id(), Time.get_unix_time_from_system(), Time.get_ticks_usec()]
 suite_directory = directory
 world = load("res://scenes/VoxelRingIntegrationTest.tscn").instantiate()
 var launcher = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
 root.add_child(launcher)
 launcher.call("_on_certified_preset_pressed")
 launcher.call("_configure_game", world, 5000000.0)
 launcher.free()
 if not driver.Prepare(world, directory):
  _fail(driver.ErrorText)
  return
 root.add_child(world)
 await process_frame
 if not driver.SetPose(world):
  _fail(driver.ErrorText)
  return
 Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
 print("FAR_RING_ABLATION=WARMUP seconds=", driver.WarmupSeconds)
 await create_timer(maxf(.001, driver.WarmupSeconds), true, false, true).timeout
 var weather = world.get_node("RingVolumetricClouds")
 var celestial = world.get_node("RingDayNightCycle")
 var weather_paused = weather.CloudSimulationPaused
 var celestial_paused = celestial.Paused
 weather.CloudSimulationPaused = true
 celestial.Paused = true
 clock_restore = func():
  if is_instance_valid(weather): weather.CloudSimulationPaused = weather_paused
  if is_instance_valid(celestial): celestial.Paused = celestial_paused
 for name in cases:
  if finished: return
  if name == "baseline":
   if not await _capture_phase(name, "baseline"): return
   continue
  if not await _capture_phase(name, "A-before"): return
  if not _apply_case(name): return
  if not await _capture_phase(name, "B"): return
  _restore_case()
  if not await _capture_phase(name, "A-after"): return
 _restore_all()
 if not _write_suite_manifest("PASS", ""):
  _fail("Cannot write suite manifest")
  return
 print("FAR_RING_ABLATION=PASS directory=", directory)
 finished = true
 world.queue_free()
 await process_frame
 await process_frame
 quit(0)

func _apply_case(name: String) -> bool:
 _restore_case()
 active_case = name
 active_role = "B"
 match name:
  "vsync-off":
   var previous = DisplayServer.window_get_vsync_mode()
   case_restore = func(): DisplayServer.window_set_vsync_mode(previous)
   DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
  "gpu-markers-off":
   var monitor = world.get_node("RingPerformanceMonitor")
   var previous = monitor.DetailedGpuEnabled
   case_restore = func():
    if is_instance_valid(monitor): monitor.DetailedGpuEnabled = previous
   monitor.DetailedGpuEnabled = false
  "outer-render-off":
   var owner = world.get_node("ScaledFarViewport")
   var viewport = owner.OuterViewportForDiagnostics
   if not is_instance_valid(viewport):
    _fail("Outer viewport unavailable; case cannot measure submission")
    return false
   var previous = viewport.render_target_update_mode
   case_restore = func():
    if is_instance_valid(viewport): viewport.render_target_update_mode = previous
   viewport.render_target_update_mode = SubViewport.UPDATE_DISABLED
  "far-process-off", "grass-process-off", "trees-process-off":
   var node_name = {"far-process-off":"ScaledFarViewport", "grass-process-off":"StandingGrass", "trees-process-off":"BiomeDecorations"}[name]
   var node = world.get_node(node_name)
   var previous = node.is_processing()
   case_restore = func():
    if is_instance_valid(node): node.set_process(previous)
   node.set_process(false)
  "terrain-cloud-shadow-off":
   var bridge = world.get_node("SunshineCloudBridge")
   var previous = bridge.TerrainCloudShadowEnabled
   case_restore = func():
    if is_instance_valid(bridge):
     bridge.TerrainCloudShadowEnabled = previous
     bridge.ApplyCloudAppearanceTuning()
   bridge.TerrainCloudShadowEnabled = false
   bridge.ApplyCloudAppearanceTuning()
  "sunshine-off", "cloud-shadow-off":
   var bridge = world.get_node("SunshineCloudBridge")
   var property = "CloudRenderingEnabled" if name == "sunshine-off" else "GpuShadowGenerationEnabled"
   var previous = bridge.get(property)
   case_restore = func():
    if is_instance_valid(bridge): bridge.set(property, previous)
   bridge.set(property, false)
  "grass-render-off", "trees-render-off":
   var node = world.get_node("StandingGrass" if name == "grass-render-off" else "BiomeDecorations")
   var saved_masks: Array = []
   for geometry in _geometry_nodes(node):
    saved_masks.append({"node":geometry,"layers":geometry.layers})
    geometry.layers = 0
   render_snapshot_count = saved_masks.size()
   case_restore = func():
    for item in saved_masks:
     if is_instance_valid(item.node): item.node.layers = item.layers
   if render_snapshot_count == 0:
    _fail("Render-only case has no live geometry to mask: " + name)
    return false
  _:
   _fail("Unsupported case " + name)
   return false
 return true

func _capture_phase(name: String, role: String) -> bool:
 active_case = name
 active_role = role
 await create_timer(driver.SettleSeconds, true, false, true).timeout
 if finished: return false
 if not driver.SetCaseContext(world, name, role):
  _fail(driver.ErrorText)
  return false
 if not driver.BeginCollection(world):
  _fail(driver.ErrorText)
  return false
 var state = _runtime_state()
 if role == "B" and not _case_applied(name, state):
  _fail("Case mutation was not effective before collection: " + name)
  return false
 print("FAR_RING_ABLATION=COLLECT case=", name, " phase=", role)
 await create_timer(driver.CollectionSeconds, true, false, true).timeout
 if finished: return false
 driver.BeginCapture(world)
 while driver.Status == "CAPTURING" and not finished:
  await process_frame
 if finished: return false
 if driver.Status != "DONE":
  _fail(driver.ErrorText)
  return false
 await RenderingServer.frame_post_draw
 var bundle: String = driver.BundleDirectory
 var error = root.get_texture().get_image().save_png(bundle.path_join("main.png"))
 if error != OK:
  _fail("Main image save failed")
  return false
 var file = FileAccess.open(bundle.path_join("ablation-phase.json"), FileAccess.WRITE)
 if file == null:
  _fail("Phase manifest write failed")
  return false
 var end_state = _runtime_state()
 if role == "B" and not _case_applied(name, end_state):
  file.close()
  _fail("Case mutation was not retained during collection: " + name)
  return false
 if state.cloudClock != end_state.cloudClock or state.celestialClock != end_state.celestialClock:
  file.close()
  _fail("Authoritative clocks changed within matched capture")
  return false
 file.store_string(JSON.stringify({"case":name,"role":role,"startState":state,"endState":end_state}, "  "))
 file.close()
 results.append({"case":name,"role":role,"bundle":bundle})
 _write_suite_manifest("RUNNING", "")
 return true

func _case_applied(name: String, state: Dictionary) -> bool:
 match name:
  "vsync-off": return state.vsync == DisplayServer.VSYNC_DISABLED
  "gpu-markers-off": return state.detailedGpuEnabled == false
  "outer-render-off": return state.outerUpdateMode == SubViewport.UPDATE_DISABLED
  "far-process-off": return not state.farProcess
  "grass-process-off": return not state.grassProcess
  "trees-process-off": return not state.treesProcess
  "sunshine-off": return not state.sunshineEnabled and state.sunshineResourceEnabled == false
  "cloud-shadow-off": return not state.cloudShadowProducerEnabled
  "terrain-cloud-shadow-off":
   return not state.terrainCloudShadowEnabled and state.terrainField.get("settings", {}).get("enabled", true) == false and state.terrainField.get("ready", true) == false
  "grass-render-off": return state.renderSnapshotCount > 0 and state.grassGeometry.total > 0 and state.grassGeometry.layered == 0
  "trees-render-off": return state.renderSnapshotCount > 0 and state.treesGeometry.total > 0 and state.treesGeometry.layered == 0
 return false

func _runtime_state() -> Dictionary:
 var far = world.get_node("ScaledFarViewport")
 var outer = far.OuterViewportForDiagnostics
 var weather = world.get_node("RingVolumetricClouds")
 var resource = world.get_node("SunshineCloudsDriver").get("clouds_resource")
 return {"vsync":DisplayServer.window_get_vsync_mode(), "maxFps":Engine.max_fps,
  "detailedGpuEnabled":world.get_node("RingPerformanceMonitor").DetailedGpuEnabled,
  "cloudClock":weather.CloudSimulationTimeSeconds,"cloudPaused":weather.CloudSimulationPaused,
  "celestialClock":world.get_node("RingDayNightCycle").GetSimulationSeconds(),
  "outerUpdateMode":outer.render_target_update_mode if is_instance_valid(outer) else null,
  "farProcess":far.is_processing(),"grassProcess":world.get_node("StandingGrass").is_processing(),
  "treesProcess":world.get_node("BiomeDecorations").is_processing(),
  "grassVisible":world.get_node("StandingGrass").visible,"treesVisible":world.get_node("BiomeDecorations").visible,
  "renderSnapshotCount":render_snapshot_count,
  "grassGeometry":_geometry_counts(world.get_node("StandingGrass")),
  "treesGeometry":_geometry_counts(world.get_node("BiomeDecorations")),
  "sunshineEnabled":world.get_node("SunshineCloudBridge").CloudRenderingEnabled,
  "sunshineResourceEnabled":resource.get("enabled") if is_instance_valid(resource) else null,
  "terrainCloudShadowEnabled":world.get_node("SunshineCloudBridge").TerrainCloudShadowEnabled,
  "terrainField":world.get_node("SunshineCloudBridge").GetTerrainCloudSunVisibilityDiagnostics(),
  "terrainTraceBudget":world.get_node("SunshineCloudBridge").TerrainCloudShadowTraceVisitBudget,
  "terrainTexelsPerFrame":world.get_node("SunshineCloudBridge").TerrainCloudShadowTexelsPerFrame,
  "cloudShadowProducerEnabled":world.get_node("SunshineCloudBridge").GpuShadowGenerationEnabled}

func _geometry_nodes(owner: Node) -> Array:
 var result: Array = []
 var pending: Array = [owner]
 while not pending.is_empty():
  var node: Node = pending.pop_back()
  if not is_instance_valid(node) or node.is_queued_for_deletion(): continue
  if node is GeometryInstance3D: result.append(node)
  pending.append_array(node.get_children(true))
 return result

func _geometry_counts(owner: Node) -> Dictionary:
 var geometry = _geometry_nodes(owner)
 var layered := 0
 for node in geometry:
  if node.layers != 0: layered += 1
 return {"total":geometry.size(),"layered":layered}

func _write_suite_manifest(status: String, reason: String) -> bool:
 if suite_directory.is_empty(): return false
 DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(suite_directory))
 var file = FileAccess.open(suite_directory.path_join("ablation-suite.json"), FileAccess.WRITE)
 if file == null: return false
 file.store_string(JSON.stringify({"schemaVersion":1,"status":status,"reason":reason,"activeCase":active_case,"activeRole":active_role,"cases":suite_cases,"results":results,"externalGpuProcessPresence":"Recorded externally by primary; runner does not enumerate or change other applications.","clockPolicy":"CloudSimulationPaused and celestial Paused enabled only after normal warmup; exact same authoritative clocks throughout all cases; restored on exit.","comparisonNote":"Serial matched A-B-A. No nested CPU/GPU totals added; disabled viewport timings unavailable rather than zero. Last outer texture preserved for render-off case."}, "  "))
 file.close()
 return true

func _restore_case():
 if case_restore.is_valid(): case_restore.call()
 case_restore = Callable()
 render_snapshot_count = 0

func _restore_all():
 _restore_case()
 if clock_restore.is_valid(): clock_restore.call()
 clock_restore = Callable()

func _fail(reason):
 if finished: return
 finished = true
 printerr("FAR_RING_ABLATION=FAIL ", reason)
 _write_suite_manifest("FAILED", str(reason))
 _restore_all()
 if is_instance_valid(world):
  if world.is_inside_tree(): world.queue_free()
  else: world.free()
 await process_frame
 quit(1)
