extends SceneTree

# Read-only phase snapshots; reversible in-memory mutations; no user preference writes.
var world: Node
var grass: Node3D
var driver: RefCounted
var helper: RefCounted
var finished := false
var suite_directory := ""
var active_phase := "startup"
var results: Array = []
var restore: Callable
var saved_layers: Array = []
var frozen_view: Dictionary
var retained: Dictionary
var frozen_cloud: float
var frozen_celestial: float
var initial: Dictionary = {}
var regenerate_seconds := 90.0
var source_hashes: Dictionary = {}

func _initialize():
 call_deferred("_run")

func _run():
 driver = load("res://tools/FarRingGameplayCapture.cs").new()
 helper = load("res://tools/GrassCostGameplayCapture.cs").new()
 if not driver.ConfigureArguments():
  _fail(driver.ErrorText)
  return
 # Explicit arguments make the measurement policy reproducible; smoke may shorten it.
 var args := OS.get_cmdline_user_args()
 for i in range(args.size()):
  if args[i] == "--regenerate-seconds":
   if i + 1 >= args.size() or not args[i + 1].is_valid_float():
    _fail("Invalid regeneration timeout")
    return
   regenerate_seconds = float(args[i + 1])
 if not is_finite(regenerate_seconds) or regenerate_seconds < 1 or regenerate_seconds > 90:
  _fail("Regeneration timeout must be 1..90 seconds")
  return
 if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
  _fail("Forward+ display/RenderingDevice required")
  return
 for path in ["res://tools/grass_cost_gameplay_capture.gd", "res://tools/GrassCostGameplayCapture.cs", "res://tools/FarRingGameplayCapture.cs", "res://scripts/Voxel/LOD/RingStandingGrassLayer.cs", "res://scripts/Voxel/LOD/RingGpuVegetationCompute.cs", "res://scripts/Voxel/LOD/RingStandingGrassLayer.OutputResidency.cs", "res://scripts/Voxel/LOD/RingStandingGrassLayer.PresentationCache.cs"]:
  source_hashes[path] = FileAccess.get_sha256(path)
 create_timer(driver.WarmupSeconds + 2 * regenerate_seconds + 7 * (driver.CollectionSeconds + driver.SettleSeconds + 20) + 40, true, false, true).timeout.connect(func(): _fail("Grass cost watchdog expired"))
 suite_directory = "res://.godot/far-ring-gameplay/%s-grass-cost-%d-%d" % [driver.ArtifactLabel, OS.get_process_id(), Time.get_ticks_usec()]
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
 if not driver.SetPose(world) or not driver.SetPoseAltitudeOffset(world, -340):
  _fail(driver.ErrorText)
  return
 Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
 grass = world.get_node("StandingGrass")
 var weather = world.get_node("RingVolumetricClouds")
 var celestial = world.get_node("RingDayNightCycle")
 initial = {"enabled":grass.Enabled,"processing":grass.is_processing(),"visible":grass.visible,
  "cloudPaused":weather.CloudSimulationPaused,"celestialPaused":celestial.Paused,
  "settings":grass.CaptureGrassStreamingDiagnostics().get("settings", {})}
 restore = func():
  _restore_layers()
  if is_instance_valid(grass):
   grass.Enabled = initial.enabled
   grass.visible = initial.visible
   grass.set_process(initial.processing)
  if is_instance_valid(weather): weather.CloudSimulationPaused = initial.cloudPaused
  if is_instance_valid(celestial): celestial.Paused = initial.celestialPaused
 grass.Enabled = true
 grass.set_process(true)
 if not _manifest("RUNNING", ""):
  _fail("Cannot write running manifest")
  return
 await create_timer(maxf(.001, driver.WarmupSeconds), true, false, true).timeout
 if finished or not await _regenerate(): return
 weather.CloudSimulationPaused = true
 celestial.Paused = true
 frozen_cloud = weather.CloudSimulationTimeSeconds
 frozen_celestial = celestial.GetSimulationSeconds()
 frozen_view = helper.CaptureView(world)
 retained = _snapshot()
 if retained.geometry.size() == 0:
  _fail("Ground baseline has no native grass outputs")
  return
 if not await _phase("A-normal-enabled", false, false): return
 grass.set_process(false)
 if not await _phase("B-manager-paused-visible", true, false): return
 grass.set_process(true)
 if not await _phase("A2-normal-restored", false, false): return
 grass.set_process(false)
 # Save masks only after manager pause so no newly created renderer is missed.
 for node in _geometry_nodes():
  saved_layers.append({"node":node,"layers":node.layers})
  node.layers = 0
 if not await _phase("C-manager-paused-layers-zero", true, true): return
 _restore_layers()
 if not await _phase("D-manager-paused-visible-restored", true, false): return
 grass.Enabled = false
 grass.set_process(true)
 await create_timer(driver.SettleSeconds, true, false, true).timeout
 if finished: return
 grass.set_process(false)
 if not await _phase("E-resources-released", false, false): return
 grass.Enabled = true
 grass.set_process(true)
 if not await _regenerate(): return
 if not await _phase("A3-regenerated-normal", false, false): return
 if restore.is_valid(): restore.call()
 if not _manifest("PASS", ""):
  _fail("Cannot write final manifest")
  return
 finished = true
 print("GRASS_COST=PASS directory=", suite_directory)
 world.queue_free()
 await process_frame
 await process_frame
 quit(0)

func _regenerate() -> bool:
 var deadline := Time.get_ticks_usec() + int(regenerate_seconds * 1000000)
 while not finished and Time.get_ticks_usec() < deadline:
  if grass.ResidentGrassInstanceCount > 0 and grass.IsGrassStreamingDiagnosticsSettled: return true
  await create_timer(.25, true, false, true).timeout
 if not finished: _fail("Populated grass did not settle within bounded regeneration timeout")
 return false

func _geometry_nodes() -> Array:
 var geometry: Array = []
 var pending: Array = [grass]
 while not pending.is_empty():
  var node: Node = pending.pop_back()
  if not is_instance_valid(node) or node.is_queued_for_deletion(): continue
  pending.append_array(node.get_children(true))
  if node is GeometryInstance3D: geometry.append(node)
 geometry.sort_custom(func(a, b): return a.get_instance_id() < b.get_instance_id())
 return geometry

func _snapshot() -> Dictionary:
 var diagnostic: Dictionary = grass.CaptureGrassStreamingDiagnostics()
 var residents: Array = grass.CaptureGrassStreamingDiagnosticResidents()
 var placement_hash := JSON.stringify(residents).sha256_text()
 var geometry: Array = []
 for node in _geometry_nodes():
  var item = {"id":str(node.get_instance_id()),"path":str(grass.get_path_to(node)),"visible":node.visible,
   "visibleInTree":node.is_visible_in_tree(),"layers":node.layers,"shadow":node.cast_shadow}
  if node is MultiMeshInstance3D and node.multimesh != null:
   var mesh: MultiMesh = node.multimesh
   item.merge({"multiMeshId":str(mesh.get_instance_id()),"multiMeshRid":str(mesh.get_rid()),
    "meshRid":str(mesh.mesh.get_rid()) if mesh.mesh != null else "none",
    "instances":mesh.instance_count,"visibleInstances":mesh.visible_instance_count})
  geometry.append(item)
 return {"view":helper.CaptureView(world),"enabled":grass.Enabled,"processing":grass.is_processing(),"visible":grass.visible,
  "geometry":geometry,"diagnostics":diagnostic,"residentPlacementHash":placement_hash,
  "totalBuilds":grass.TotalGrassBuildCount,"totalUploads":grass.TotalGrassUploadCount,"skippedUnchanged":grass.GpuCullingSkippedUnchangedUpdates,
  "cloudClock":world.get_node("RingVolumetricClouds").CloudSimulationTimeSeconds,
  "cloudPaused":world.get_node("RingVolumetricClouds").CloudSimulationPaused,
  "celestialClock":world.get_node("RingDayNightCycle").GetSimulationSeconds(),
  "celestialPaused":world.get_node("RingDayNightCycle").Paused,
  "detailedGpuEnabled":world.get_node("RingPerformanceMonitor").DetailedGpuEnabled}

func _check(state: Dictionary, preserve: bool, masked: bool) -> String:
 if state.view != frozen_view: return "Camera/canonical pose/altitude changed"
 if not state.cloudPaused or not state.celestialPaused or state.cloudClock != frozen_cloud or state.celestialClock != frozen_celestial:
  return "Authoritative clocks changed"
 var diagnostic: Dictionary = state.diagnostics
 var released := active_phase == "E-resources-released"
 var paused := active_phase.begins_with("B-") or active_phase.begins_with("C-") or active_phase.begins_with("D-") or released
 if state.processing == paused: return "Grass effective processing state does not match phase"
 if state.enabled == released: return "Grass Enabled state does not match phase"
 if state.visible == released: return "Grass parent visibility does not match phase"
 if diagnostic.streaming_suspended != released: return "Grass suspension state does not match phase"
 if diagnostic.pending_requests != 0 or diagnostic.active_jobs != 0 or diagnostic.pending_uploads != 0:
  return "Grass build/upload queues are not empty"
 if active_phase == "E-resources-released":
  if diagnostic.resident_roots != 0 or diagnostic.resident_tiles != 0 or diagnostic.gpu_registered_batches != 0 or diagnostic.gpu_buffer_bytes != 0 or not state.geometry.is_empty():
   return "Suspension did not release all grass roots/native outputs/buffers"
 if preserve:
  for key in ["totalBuilds", "totalUploads", "residentPlacementHash"]:
   if state[key] != retained[key]: return "Retained build/upload/placement state changed: " + key
  if state.processing: return "Grass manager was not paused"
  for key in ["resident_roots", "resident_tiles", "gpu_registered_batches", "gpu_buffer_bytes", "gpu_shared_source_bytes", "gpu_output_bytes"]:
   if diagnostic[key] != retained.diagnostics[key]: return "Retained grass resource count changed: " + key
  if diagnostic.settings != retained.diagnostics.settings: return "Grass settings changed"
  if state.geometry.size() != retained.geometry.size(): return "Retained native node count changed"
  for i in range(state.geometry.size()):
   var actual: Dictionary = state.geometry[i]
   var expected: Dictionary = retained.geometry[i]
   for key in expected:
    if key == "layers":
     if actual[key] != (0 if masked else expected[key]): return "Grass layer mutation/restoration mismatch"
    elif actual[key] != expected[key]: return "Retained native object/resource/visibility changed: " + key
 return ""

func _phase(name: String, preserve: bool, masked: bool) -> bool:
 active_phase = name
 if not _manifest("RUNNING", ""):
  _fail("Cannot write phase-start manifest")
  return false
 await create_timer(driver.SettleSeconds, true, false, true).timeout
 if finished: return false
 if not driver.SetCaseContext(world, "grass-cost", name):
  _fail(driver.ErrorText)
  return false
 var begin = _snapshot()
 var reason = _check(begin, preserve, masked)
 if not reason.is_empty():
  _fail(name + ": " + reason)
  return false
 if not driver.BeginCollection(world):
  _fail(driver.ErrorText)
  return false
 print("GRASS_COST=COLLECT phase=", name)
 await create_timer(driver.CollectionSeconds, true, false, true).timeout
 if finished: return false
 var end = _snapshot()
 driver.BeginCapture(world)
 while driver.Status == "CAPTURING" and not finished: await process_frame
 if finished: return false
 if driver.Status != "DONE":
  _fail(driver.ErrorText)
  return false
 reason = _check(end, preserve, masked)
 if not reason.is_empty():
  _fail(name + ": " + reason)
  return false
 var deltas: Dictionary = {"gpu_completed_updates":end.diagnostics.gpu_completed_updates - begin.diagnostics.gpu_completed_updates,
  "gpu_skipped_unchanged_updates":end.skippedUnchanged - begin.skippedUnchanged,
  "active_jobs":end.diagnostics.active_jobs - begin.diagnostics.active_jobs,
  "pending_requests":end.diagnostics.pending_requests - begin.diagnostics.pending_requests,
  "pending_uploads":end.diagnostics.pending_uploads - begin.diagnostics.pending_uploads,
  "total_builds":end.totalBuilds - begin.totalBuilds,"total_uploads":end.totalUploads - begin.totalUploads}
 if deltas.total_builds != 0 or deltas.total_uploads != 0:
  _fail(name + ": settled collection performed grass builds/uploads")
  return false
 for key in ["gpu_dispatch_count", "gpu_barrier_count", "gpu_parameter_uploads"]:
  deltas[key] = end.diagnostics[key] - begin.diagnostics[key] if begin.diagnostics.has(key) and end.diagnostics.has(key) else null
 await RenderingServer.frame_post_draw
 if finished: return false
 if root.get_texture().get_image().save_png(driver.BundleDirectory.path_join("main.png")) != OK:
  _fail("Main image save failed")
  return false
 var result = {"phase":name,"status":"PASS","bundle":driver.BundleDirectory,"begin":begin,"end":end,"counterDeltas":deltas,
  "counterInterval":"Boundary snapshots bracket collection only; post-collection report/image capture excluded from counter deltas."}
 results.append(result)
 var file = FileAccess.open(driver.BundleDirectory.path_join("grass-cost-phase.json"), FileAccess.WRITE)
 if file == null:
  _fail("Cannot write phase result")
  return false
 file.store_string(JSON.stringify(result, "  "))
 file.close()
 if not _manifest("RUNNING", ""):
  _fail("Cannot persist completed phase")
  return false
 return true

func _restore_layers():
 for item in saved_layers:
  if is_instance_valid(item.node): item.node.layers = item.layers
 saved_layers.clear()

func _manifest(status: String, reason: String) -> bool:
 if suite_directory.is_empty(): return false
 DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(suite_directory))
 var file = FileAccess.open(suite_directory.path_join("grass-cost-suite.json"), FileAccess.WRITE)
 if file == null: return false
 file.store_string(JSON.stringify({"schemaVersion":1,"status":status,"reason":reason,"activePhase":active_phase,"results":results,
  "sourceHashes":source_hashes,"initial":initial,"baselineAltitudeOffsetMeters":-340,"expectedPhaseCount":7,
  "policy":"Canonical position/camera/altitude and authoritative celestial/cloud clocks fixed. Shared wind publication/render paths retain normal processing; manager pause also stops new compute submissions and grass material/interaction updates.",
  "comparison":"A/A2 brackets B for manager plus compute request updates with draw resources retained. B/D brackets C for draw exclusion with manager paused. C/E compares no-draw retained versus released resources; phase order and scene drift limit native-cost attribution. A3 detects restoration/drift. Layer zero excludes main-scene grass; far viewports use separate worlds.",
  "limitations":"No GPU readback or shader/mesh-quality/density changes. Last-request dispatched/deferred values are not per-frame dispatch counts. Unavailable dispatch/barrier/parameter delta fields are null. Counter intervals exclude post-collection capture; queue deltas are pending-work deltas, not total jobs started. No precise native function or shader-only attribution from draw exclusion."}, "  "))
 file.close()
 return true

func _fail(reason):
 if finished: return
 finished = true
 printerr("GRASS_COST=FAILED ", reason)
 _manifest("FAILED", str(reason))
 if restore.is_valid(): restore.call()
 if is_instance_valid(world):
  if world.is_inside_tree(): world.queue_free()
  else: world.free()
 await process_frame
 quit(1)
