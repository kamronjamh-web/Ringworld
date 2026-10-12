extends SceneTree

# Read-only phase snapshots; reversible in-memory mutations; no user preference writes.
var world: Node
var grass: Node3D
var driver: RefCounted
var helper: RefCounted
var invariant_helper: RefCounted
var finished := false
var suite_directory := ""
var active_phase := "startup"
var results: Array = []
var restore: Callable
var frozen_view: Dictionary
var retained: Dictionary
var frozen_cloud: float
var frozen_celestial: float
var initial: Dictionary = {}
var regenerate_seconds := 90.0
var source_hashes: Dictionary = {}
var failure_snapshot: Dictionary = {}

func _initialize():
 call_deferred("_run")

func _run():
 driver = load("res://tools/FarRingGameplayCapture.cs").new()
 helper = load("res://tools/GrassCostGameplayCapture.cs").new()
 invariant_helper = load("res://tools/GrassAdaptiveGroupingTest.cs").new()
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
 for path in ["res://tools/grass_adaptive_grouping_capture.gd", "res://tools/GrassCostGameplayCapture.cs", "res://tools/FarRingGameplayCapture.cs", "res://scripts/Voxel/LOD/RingStandingGrassLayer.cs", "res://scripts/Voxel/LOD/RingGpuVegetationCompute.cs", "res://scripts/Voxel/LOD/RingStandingGrassLayer.OutputResidency.cs", "res://scripts/Voxel/LOD/RingStandingGrassLayer.PresentationCache.cs", "res://tools/GrassAdaptiveGroupingTest.cs", "res://scripts/Voxel/LOD/RingStandingGrassLayer.AdaptiveGroups.cs", "res://scripts/Voxel/LOD/RingStandingGrassLayer.WorkCache.cs"]:
  source_hashes[path] = FileAccess.get_sha256(path)
 create_timer(driver.WarmupSeconds + 2 * regenerate_seconds + 5 * (regenerate_seconds + driver.CollectionSeconds + driver.SettleSeconds + 20) + 40, true, false, true).timeout.connect(func(): _fail("Grass grouping watchdog expired"))
 suite_directory = "res://.godot/far-ring-gameplay/%s-grass-adaptive-grouping-%d-%d" % [driver.ArtifactLabel, OS.get_process_id(), Time.get_ticks_usec()]
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
  "settings":grass.CaptureGrassStreamingDiagnostics().get("settings", {}),
  "cache":grass.CacheGrassPresentation,"grouping":grass.EnableAdaptiveGrassGrouping,"maximumGroup":grass.GrassGroupingMaximumLevel,
  "farGroupStart":grass.GrassGroupingFarStartMeters}
 restore = func():
  if is_instance_valid(grass):
   grass.Enabled = initial.enabled
   grass.visible = initial.visible
   grass.set_process(initial.processing)
   grass.CacheGrassPresentation = initial.cache
   grass.EnableAdaptiveGrassGrouping = initial.grouping
   grass.GrassGroupingMaximumLevel = initial.maximumGroup
   grass.GrassGroupingFarStartMeters = initial.farGroupStart
  if is_instance_valid(weather): weather.CloudSimulationPaused = initial.cloudPaused
  if is_instance_valid(celestial): celestial.Paused = initial.celestialPaused
 grass.Enabled = true
 grass.EnableAdaptiveGrassGrouping = false
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
 for item in [
  {"name":"A-cache-off-groups-off","cache":false,"groups":false,"level":1},
  {"name":"B-cache-on-groups-off","cache":true,"groups":false,"level":1},
  {"name":"C-cache-on-groups-32","cache":true,"groups":true,"level":1},
  {"name":"D-cache-on-groups-64","cache":true,"groups":true,"level":2},
  {"name":"B2-cache-on-groups-off-restored","cache":true,"groups":false,"level":1}]:
  grass.CacheGrassPresentation = item.cache
  grass.EnableAdaptiveGrassGrouping = item.groups
  grass.GrassGroupingMaximumLevel = item.level
  grass.GrassGroupingFarStartMeters = 160 if item.level == 2 and item.groups else initial.farGroupStart
  if not await _phase(item.name): return
 if restore.is_valid(): restore.call()
 if not _manifest("PASS", ""):
  _fail("Cannot write final manifest")
  return
 finished = true
 print("GRASS_ADAPTIVE_GROUPING_CAPTURE=PASS directory=", suite_directory)
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
 var native_batches := 0
 var output_instance_slots := 0
 var visible_instance_slots := 0
 for node in _geometry_nodes():
  var item = {"id":str(node.get_instance_id()),"path":str(grass.get_path_to(node)),"visible":node.visible,
   "visibleInTree":node.is_visible_in_tree(),"layers":node.layers,"shadow":node.cast_shadow}
  if node is MultiMeshInstance3D and node.multimesh != null:
   var mesh: MultiMesh = node.multimesh
   native_batches += 1
   output_instance_slots += mesh.instance_count
   visible_instance_slots += mesh.instance_count if mesh.visible_instance_count < 0 else mesh.visible_instance_count
   item.merge({"multiMeshId":str(mesh.get_instance_id()),"multiMeshRid":str(mesh.get_rid()),
    "meshRid":str(mesh.mesh.get_rid()) if mesh.mesh != null else "none",
    "instances":mesh.instance_count,"visibleInstances":mesh.visible_instance_count})
  geometry.append(item)
 return {"view":helper.CaptureView(world),"enabled":grass.Enabled,"processing":grass.is_processing(),"visible":grass.visible,
  "geometry":geometry,"diagnostics":diagnostic,"residentPlacementHash":placement_hash,
  "nativeBatchCount":native_batches,"outputInstanceSlots":output_instance_slots,"visibleInstanceSlots":visible_instance_slots,
  "visibilityCacheHits":grass.GrassVisibilityCacheHits,"visibilityScans":grass.GrassVisibilityScans,
  "presentationCacheHits":grass.GrassPresentationCacheHits,"presentationScans":grass.GrassPresentationScans,
  "gpuRequestCacheHits":grass.GrassGpuRequestCacheHits,"gpuRequestScans":grass.GrassGpuRequestScans,
  "canonicalRoots":invariant_helper.CaptureRootInvariant(world),
  "cacheEnabled":grass.CacheGrassPresentation,"groupingEnabled":grass.EnableAdaptiveGrassGrouping,
  "maximumGroupLevel":grass.GrassGroupingMaximumLevel,
  "farGroupStartMeters":grass.GrassGroupingFarStartMeters,
  "groupCount":grass.AdaptiveGrassGroupCount,"groupsLevel1":grass.AdaptiveGrassLevel1GroupCount,
  "groupsLevel2":grass.AdaptiveGrassLevel2GroupCount,"groupedSources":grass.AdaptiveGrassGroupedSourceCount,
  "activePresentations":grass.AdaptiveGrassActivePresentationCount,"groupingStatus":grass.AdaptiveGrassGroupingStatus,
  "totalBuilds":grass.TotalGrassBuildCount,"totalUploads":grass.TotalGrassUploadCount,"skippedUnchanged":grass.GpuCullingSkippedUnchangedUpdates,
  "cloudClock":world.get_node("RingVolumetricClouds").CloudSimulationTimeSeconds,
  "cloudPaused":world.get_node("RingVolumetricClouds").CloudSimulationPaused,
  "celestialClock":world.get_node("RingDayNightCycle").GetSimulationSeconds(),
  "celestialPaused":world.get_node("RingDayNightCycle").Paused,
  "detailedGpuEnabled":world.get_node("RingPerformanceMonitor").DetailedGpuEnabled}

func _check(state: Dictionary) -> String:
 if state.view != frozen_view: return "Camera/canonical pose/altitude changed"
 if not state.cloudPaused or not state.celestialPaused or state.cloudClock != frozen_cloud or state.celestialClock != frozen_celestial:
  return "Authoritative clocks changed"
 var diagnostic: Dictionary = state.diagnostics
 if not state.processing or not state.enabled or not state.visible: return "Grass manager/output disabled"
 if diagnostic.pending_requests != 0 or diagnostic.active_jobs != 0 or diagnostic.pending_uploads != 0:
  return "Grass build/upload queues are not empty"
 if state.canonicalRoots != retained.canonicalRoots: return "Canonical leaf root identity/rank/wind/world pose changed"
 if state.residentPlacementHash != retained.residentPlacementHash: return "Leaf density or membership changed"
 for key in ["totalBuilds"]:
  if state[key] != retained[key]: return "Grouping/cache rebuilt or uploaded leaf sources: " + key
 if diagnostic.resident_roots != retained.diagnostics.resident_roots: return "Canonical root count changed"
 var expected_settings: Dictionary = retained.diagnostics.settings.duplicate(true)
 var actual_settings: Dictionary = diagnostic.settings.duplicate(true)
 for key in ["cache_grass_presentation", "adaptive_grass_grouping", "grass_grouping_maximum_level", "grass_grouping_far_start_meters"]:
  expected_settings.erase(key)
  actual_settings.erase(key)
 if actual_settings != expected_settings: return "Unrelated grass density/coverage/quality settings changed"
 if active_phase.begins_with("C-") and state.groupsLevel1 <= 0: return "32 m grouping phase has no actual level 1 groups"
 if active_phase.begins_with("D-") and state.groupsLevel2 <= 0: return "64 m grouping phase has no actual level 2 groups"
 return ""

func _phase(name: String) -> bool:
 active_phase = name
 if not _manifest("RUNNING", ""):
  _fail("Cannot write phase-start manifest")
  return false
 await create_timer(driver.SettleSeconds, true, false, true).timeout
 if finished: return false
 if not await _settle_groups(): return false
 if not driver.SetCaseContext(world, "grass-adaptive-grouping", name):
  _fail(driver.ErrorText)
  return false
 var begin = _snapshot()
 var reason = _check(begin)
 if not reason.is_empty():
  _fail(name + ": " + reason)
  return false
 if not driver.BeginCollection(world):
  _fail(driver.ErrorText)
  return false
 print("GRASS_ADAPTIVE_GROUPING_CAPTURE=COLLECT phase=", name)
 await create_timer(driver.CollectionSeconds, true, false, true).timeout
 if finished: return false
 var end = _snapshot()
 driver.BeginCapture(world)
 while driver.Status == "CAPTURING" and not finished: await process_frame
 if finished: return false
 if driver.Status != "DONE":
  _fail(driver.ErrorText)
  return false
 reason = _check(end)
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
 for key in ["visibilityCacheHits", "visibilityScans", "presentationCacheHits", "presentationScans", "gpuRequestCacheHits", "gpuRequestScans"]:
  deltas[key] = end[key] - begin[key]
 await RenderingServer.frame_post_draw
 if finished: return false
 if root.get_texture().get_image().save_png(driver.BundleDirectory.path_join("main.png")) != OK:
  _fail("Main image save failed")
  return false
 var result = {"phase":name,"status":"PASS","bundle":driver.BundleDirectory,"begin":begin,"end":end,"counterDeltas":deltas,
  "counterInterval":"Boundary snapshots bracket collection only; post-collection report/image capture excluded from counter deltas."}
 results.append(result)
 var file = FileAccess.open(driver.BundleDirectory.path_join("grass-adaptive-grouping-phase.json"), FileAccess.WRITE)
 if file == null:
  _fail("Cannot write phase result")
  return false
 file.store_string(JSON.stringify(result, "  "))
 file.close()
 if not _manifest("RUNNING", ""):
  _fail("Cannot persist completed phase")
  return false
 return true

func _settle_groups() -> bool:
 var deadline := Time.get_ticks_usec() + int(regenerate_seconds * 1000000)
 while not finished and Time.get_ticks_usec() < deadline:
  if grass.IsGrassStreamingDiagnosticsSettled and grass.IsAdaptiveGrassGroupingSettled: return true
  await create_timer(.25, true, false, true).timeout
 if not finished: _fail("Grouping handoff did not settle within bounded timeout")
 return false

func _manifest(status: String, reason: String) -> bool:
 if suite_directory.is_empty(): return false
 DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(suite_directory))
 var file = FileAccess.open(suite_directory.path_join("grass-adaptive-grouping-suite.json"), FileAccess.WRITE)
 if file == null: return false
 file.store_string(JSON.stringify({"schemaVersion":1,"status":status,"reason":reason,"activePhase":active_phase,"results":results,
  "sourceHashes":source_hashes,"initial":initial,"failureSnapshot":failure_snapshot,"baselineAltitudeOffsetMeters":-340,"expectedPhaseCount":5,
  "policy":"Fixed captured ground pose (source altitude offset -340 m, approximately 12.39 m above terrain), camera and authoritative celestial/cloud clocks. All phases retain the same canonical leaf sources, density, coverage and quality. Normal material/wind processing remains enabled. Isolated .godot preferences only.",
  "comparison":"A cache off/groups off; B cache on/groups off; C cache on/groups up to 32 m; D cache on/groups up to 64 m with far grouping threshold 160 m; B2 restores B for drift. Canonical root source/rank/wind bytes and canonical world pose hashes must match. Native IDs, output slot totals and batch IDs may change. Output uploads during grouping handoff are allowed; settled collection builds/uploads must remain zero. Every phase records native geometry, instance slots, group/cache diagnostics, production reports and boundary counter deltas.",
  "limitations":"Stationary matched capture only; movement/turning/cache invalidation contracts run in grass_adaptive_grouping_test.gd. Canonical world hashes describe leaf source poses, while production grouping contracts verify group re-expression. Full-scene timing and draw/native counters do not identify shader-only cost or certify cross-run FPS. No density/appearance adjustments or GPU readback in capture."}, "  "))
 file.close()
 return true

func _fail(reason):
 if finished: return
 finished = true
 printerr("GRASS_ADAPTIVE_GROUPING_CAPTURE=FAILED ", reason)
 if is_instance_valid(world) and is_instance_valid(grass) and grass.is_inside_tree() and is_instance_valid(helper) and is_instance_valid(invariant_helper):
  failure_snapshot = _snapshot()
 _manifest("FAILED", str(reason))
 if restore.is_valid(): restore.call()
 if is_instance_valid(world):
  if world.is_inside_tree(): world.queue_free()
  else: world.free()
 await process_frame
 quit(1)
