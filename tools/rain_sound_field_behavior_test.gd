extends SceneTree

# Headless: godot --headless --audio-driver Dummy --path . --script res://tools/rain_sound_field_behavior_test.gd [-- --rain-field-certified]
# Windowed (no --headless, GPU lock): additionally runs the occlusion-map provider cases (mesh roof A/B against the fixture, provider switch smoothness).
# Replaces rain_spatial_audio_smoke_test, rain_surface_behavior_test and rain_surface_audio_smoke_test.
# Real controller, generated surfaces and unsaved edits; only weather input and listener position are fixture-controlled.
# Covers shelter classes on constructed geometry (open, porch, sealed room, room with a doorway, cave, deep cave), exposure toward
# an opening, shoreline sector materials, directional panning, thunder muffling, lifecycle silence and the query budgets.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const WEATHER_RUNNER := preload("res://scripts/Weather/WeatherAmbienceDiagnosticRunner.cs")
const SURFACE_RUNNER := preload("res://scripts/Weather/RainSurfaceVerificationRunner.cs")
const FIXTURE := preload("res://scripts/Weather/FixtureRainExposureProvider.cs")
const SETTLE := 3.2
const SHOT_LIBRARIES := ["ShotPlips", "ShotLeafDrips", "ShotTicks", "ShotRoofPlinks", "ShotCaveDrips", "ShotEavesDribbles", "ShotHailHits"]
const KIND_FAT_PLIP := 6
const KIND_HAIL_HIT := 7
const KIND_ROOF_PLINK := 3
const KIND_CAVE_DRIP := 4
const MATERIAL_METAL := 4
const RAIN_HARD := 2
var scene: Node
var ambience: Node
var thunder: Node
var grid: Node
var smooth: Node
var explorer: Node
var bridge: Node
var local: Node
var runner: RefCounted
var sites: Array = []
var fixture: RefCounted
var weather_runner: RefCounted
var capture: AudioEffectCapture
var camera: Camera3D
var start_usec := 0
var done := false
var budget_armed := false
var last_frames := 0
var certified := false

func _initialize() -> void:
	start_usec = Time.get_ticks_usec()
	call_deferred("run")

func _process(_delta: float) -> bool:
	if done: return false
	if float(Time.get_ticks_usec() - start_usec) > 450000000.0: check(false, "real-time deadline")
	if budget_armed and is_instance_valid(ambience):
		var d := diag()
		check(int(d.surface_queries_frame) <= 3 and int(d.surface_density_query_frame) <= 48, "per-frame column/density budget")
	return false

func run() -> void:
	# The layout is referenced by uid; a stale uid cache (another process re-importing) would leave the game without its audio buses.
	if AudioServer.get_bus_index(&"Rain") < 0: AudioServer.set_bus_layout(load("res://default_bus_layout.tres"))
	AudioServer.set_bus_volume_db(0, -80.0)
	scene = SCENE.instantiate()
	certified = "--rain-field-certified" in OS.get_cmdline_user_args()
	if certified:
		var launcher := LAUNCHER.instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", scene, 5000000.0)
		launcher.free()
	root.add_child(scene)
	await frames(5)
	ambience = scene.get_node("WeatherAmbience")
	thunder = scene.get_node("WeatherAudio")
	grid = scene.get_node("ChunkGridManager")
	smooth = scene.get_node("SmoothTerrainGrid")
	explorer = scene.get_node("Explorer")
	bridge = scene.get_node("SunshineCloudBridge")
	local = scene.get_node("LocalWeatherEffects")
	runner = SURFACE_RUNNER.new()
	weather_runner = WEATHER_RUNNER.new()
	fixture = FIXTURE.new()
	var weather := scene.get_node("RingVolumetricClouds")
	weather.set_process(false)
	weather.set("Coverage", -10.0)
	weather.set("WeatherClusteringStrength", 0.0)
	bridge.set_process(false)
	local.set("EnableLightning", false)
	explorer.set_physics_process(false)
	capture = AudioEffectCapture.new()
	capture.buffer_length = 1.0
	AudioServer.add_bus_effect(AudioServer.get_bus_index(&"Rain"), capture)
	var flat := find_site(0)
	if flat.is_empty(): return
	rain(1.0)
	place(flat, float(flat.top) + 1)
	await wait_real(SETTLE)
	budget_armed = true
	if not await open_ground(flat): return
	if not await near_shots(flat): return
	if not await storm_arrival(flat): return
	if not await porch(flat): return
	if not await room(flat): return
	if not await mismatch_open(flat): return
	if not await map_provider(flat): return
	if not await direction(flat): return
	if not await cave(): return
	if not await shoreline(): return
	if not await lifecycle(flat): return
	var result := diag()
	done = true
	scene.queue_free()
	await frames(4)
	print("RAIN_SOUND_FIELD_BEHAVIOR=PASS: open/porch/metal porch/room/doorway/cave/deep cave classes, doorway exposure, shoreline materials, directional pan reversal, thunder muffle, near one-shot rate/cap/spacing/empty library, roof plinks and cave drips, aftermath wetness, dry/snow/pause/disable/underwater/space silence, teleport reseed, budgets ", {"shelter": result.shelter_class, "queries": result.surface_query_total, "shots": result.shot_started_total})
	quit(0)

# A dry site whose heights come from the same column query the audio uses, not from the raw sampler.
func find_site(index: int) -> Dictionary:
	if sites.is_empty(): sites = runner.FindDrySurfaces(grid, 4)
	if not check(sites.size() > index, "no dry generated land site #%d" % index): return {}
	var site: Dictionary = sites[index].duplicate()
	var resolved: Dictionary = ambience.call("GetRainSurfaceDiagnostic", int(site.around), int(site.across))
	if not check(bool(resolved.found), "site column did not resolve"): return {}
	site.top = float(resolved.top)
	site.natural = float(resolved.natural)
	return site

func open_ground(site: Dictionary) -> bool:
	var d := diag()
	if not check(d.shelter_class == "Open" and bool(d.field_seeded) and not bool(ambience.get("IsRainSheltered")), "open ground not Open/seeded: " + str(d.shelter_class) + " site=" + str(site) + " resolved=" + str(ambience.call("GetRainSurfaceDiagnostic", int(site.around), int(site.across)))): return false
	# Rolling generated terrain may legitimately wall off a hillside, so ask for open air on average, not in every direction.
	var open_sectors := 0
	var mean_open := 0.0
	for sector in 4:
		mean_open += float(d.sector_exposure[sector]) * .25
		if float(d.sector_exposure[sector]) > .6: open_sectors += 1
	if not check(mean_open > .5 and open_sectors >= 2, "open-ground exposure " + str(d.sector_exposure)): return false
	if not check(float(d.cutoff_hz) > 17000.0 and float(d.thunder_db) > -0.5 and thunder.get("ShelterMuffleAmount") < .02, "open ground filters"): return false
	var open_peak := await peak()
	if not check(open_peak > .00001, "open ground produced no PCM, peak %f drained %d" % [open_peak, last_frames]): return false
	if not check(layer_sum(d, "bed") > .2, "bed silent in open rain"): return false
	var field_total := layer_sum(d, "field")
	if not check(field_total > .5 and layer_gain(d, "roof", 0) < .001 and layer_gain(d, "roof", 1) < .001, "field/roof split in the open: " + str([field_total, layer_gain(d, "roof", 0)])): return false
	return true

# Near one-shot pool: rate tracks intensity, the cap and spacing hold, and an empty library is silent.
func near_shots(site: Dictionary) -> bool:
	print("RAIN_FIELD_PHASE near_shots t=", float(Time.get_ticks_usec() - start_usec) / 1000000.0)
	var d := diag()
	for kind in 8:
		if not check(int(d.shot_library_counts[kind]) > 0, "shot library %d is empty: %s" % [kind, str(d.shot_library_counts)]): return false
	rain(1.0)
	await wait_real(3.0)
	ambience.call("ResetShotStats")
	await wait_real(12.0)
	d = diag()
	var count_high := int(d.shot_started_total)
	if not check(count_high >= 8, "full rain started only %d one-shots in 12 s (rates %s)" % [count_high, str(d.shot_rates)]): return false
	if not check(int(d.shot_max_active) <= 6 and int(d.shot_voice_count) == 6, "shot voice cap exceeded: %d" % int(d.shot_max_active)): return false
	if not check(float(d.shot_min_spacing) >= .06 - 1e-3, "shot spacing %f" % float(d.shot_min_spacing)): return false
	rain(0.35)
	await wait_real(4.0)
	ambience.call("ResetShotStats")
	await wait_real(12.0)
	d = diag()
	var count_low := int(d.shot_started_total)
	var ratio := float(count_low) / float(count_high)
	if not check(count_low < count_high and ratio >= .1 and ratio <= .75, "shot rate did not track intensity: low %d high %d" % [count_low, count_high]): return false
	print("RAIN_FIELD_SHOTS ", {"high": count_high, "low": count_low, "ratio": snappedf(ratio, .01), "kinds": d.shot_kind_totals, "min_spacing": d.shot_min_spacing})
	# Empty library: silent, nothing active.
	rain(1.0)
	await wait_real(SETTLE)
	var saved := {}
	var empty: Array[AudioStream] = []
	for prop in SHOT_LIBRARIES:
		saved[prop] = ambience.get(prop)
		ambience.set(prop, empty)
	await wait_real(1.0)
	ambience.call("ResetShotStats")
	await wait_real(4.0)
	d = diag()
	var silent := int(d.shot_started_total) == 0 and int(d.shot_active_count) == 0
	for prop in SHOT_LIBRARIES: ambience.set(prop, saved[prop])
	if not check(silent, "empty shot library still played: started %d active %d" % [int(d.shot_started_total), int(d.shot_active_count)]): return false
	await wait_real(1.0)
	return true

# Storm arrival pools and layers (S3 WO-C3): the fixture override stands in for the observer's arrival state.
func storm_arrival(site: Dictionary) -> bool:
	print("RAIN_FIELD_PHASE storm_arrival t=", float(Time.get_ticks_usec() - start_usec) / 1000000.0)
	var d := diag()
	if not check(bool(d.gust_roar_stream) and bool(d.hail_stream) and bool(d.skitter_stream), "storm layer streams missing: " + str([d.gust_roar_stream, d.hail_stream, d.skitter_stream])): return false
	if not check(int(d.storm_player_count) == 12 and layer_range(d, "gust")[1] - layer_range(d, "gust")[0] == 4, "storm voice layout"): return false
	# Fat drops in light rain: fat plips, no hail.
	rain(0.2)
	ambience.call("SetArrivalOverrideForFixture", 0.0, 1.0, 0.0, 0.0)
	await wait_real(4.0)
	ambience.call("ResetShotStats")
	await wait_real(12.0)
	d = diag()
	if not check(int(d.shot_kind_totals[KIND_FAT_PLIP]) > 0 and int(d.shot_kind_totals[KIND_HAIL_HIT]) == 0, "fat drops in light rain: " + str(d.shot_kind_totals)): return false
	# Heavy rain covers the fat drops (rain-tier ducking).
	rain(1.0)
	await wait_real(5.0)
	ambience.call("ResetShotStats")
	await wait_real(8.0)
	d = diag()
	if not check(int(d.shot_kind_totals[KIND_FAT_PLIP]) == 0, "fat plips under heavy rain: " + str(d.shot_kind_totals)): return false
	if not check(float(d.wind_tier_duck) < .8, "heavy rain did not duck the wind: %f" % float(d.wind_tier_duck)): return false
	# Hail: hits, the hail bed, the rain bed ducked.
	var bed_before := layer_sum(d, "bed")
	ambience.call("SetArrivalOverrideForFixture", 0.0, 0.0, 1.0, 0.0)
	await wait_real(5.0)
	ambience.call("ResetShotStats")
	await wait_real(10.0)
	d = diag()
	if not check(int(d.shot_kind_totals[KIND_HAIL_HIT]) >= 8, "too few hail hits in 10 s: " + str(d.shot_kind_totals)): return false
	if not check(layer_sum(d, "hail") > .3, "hail bed silent in the open: %f" % layer_sum(d, "hail")): return false
	if not check(float(d.hail_rain_duck) < .65 and layer_sum(d, "bed") < bed_before * .8, "hail did not duck the rain bed: before %f now %f" % [bed_before, layer_sum(d, "bed")]): return false
	if not check(float(d.shot_min_spacing) >= .06 - 1e-3 and int(d.shot_max_active) <= 6, "hail shots broke the pool caps"): return false
	# Gust front: the roar swells in the sector facing the storm (across-positive here) and the skitter needs leaves, not rain.
	ambience.call("SetArrivalOverrideForFixture", 1.0, 0.0, 0.0, PI * 0.5)
	await wait_real(5.0)
	d = diag()
	var gust_toward := layer_gain(d, "gust", 1)
	if not check(gust_toward > .05 and gust_toward > layer_gain(d, "gust", 0) and gust_toward > layer_gain(d, "gust", 3), "gust roar not loudest toward the storm: " + str([layer_gain(d, "gust", 0), gust_toward, layer_gain(d, "gust", 2), layer_gain(d, "gust", 3)])): return false
	if not check(layer_sum(d, "hail") < .02, "hail bed lingered after the hail ended"): return false
	# Clearing the override silences the storm layers again.
	ambience.call("ClearArrivalOverrideForFixture")
	await wait_real(6.0)
	d = diag()
	if not check(layer_sum(d, "gust") < .01 and layer_sum(d, "hail") < .01 and layer_sum(d, "skitter") < .01, "storm layers did not fall silent: " + str([layer_sum(d, "gust"), layer_sum(d, "hail"), layer_sum(d, "skitter")])): return false
	rain(1.0)
	await wait_real(2.0)
	return true

func layer_range(d: Dictionary, layer_name: String) -> Array:
	return [int(d.voice_layout[layer_name][0]), int(d.voice_layout[layer_name][1])]

func layer_gain(d: Dictionary, layer_name: String, index: int) -> float:
	return float(d.voice_gains[int(d.voice_layout[layer_name][0]) + index])

func layer_sum(d: Dictionary, layer_name: String) -> float:
	var bounds := layer_range(d, layer_name)
	var total := 0.0
	for index in range(bounds[0], bounds[1]): total += float(d.voice_gains[index])
	return total

# Voxel edits are dormant in the surface-runtime world, so constructed geometry comes from the synthetic provider the node accepts
# through its IRainExposureProvider seam. Ground, listener and every other system stay real.
func use_fixture(site: Dictionary) -> void:
	fixture.Clear()
	# The explorer settles onto the collision ground, so take the ground from where the camera really is (eye height 1.54 m).
	fixture.GroundTop = float(diag().listener_vertical) - 1.54
	ambience.call("SetExposureProviderForFixture", fixture)

func use_terrain() -> void:
	ambience.call("SetExposureProviderForFixture", null)

func porch(site: Dictionary) -> bool:
	ambience.call("ResetShotStats")
	use_fixture(site)
	var ground: float = float(diag().listener_vertical) - 1.54
	# A 5 x 5 roof 5 m above the ground, no walls.
	fixture.AddBox(float(site.around) - 2, ground + 5.0, float(site.across) - 2, float(site.around) + 3, ground + 6.0, float(site.across) + 3, true)
	await wait_real(SETTLE)
	var d := diag()
	if not check(d.shelter_class == "Roof" and bool(ambience.get("IsRainSheltered")), "porch class " + str(d.shelter_class) + " exposure " + str(d.sector_exposure)): return false
	if not check(float(d.cutoff_hz) < 7000.0 and float(d.cutoff_hz) > 5000.0 and float(d.field_cutoff_hz) > float(d.cutoff_hz), "porch filters bed %f field %f" % [d.cutoff_hz, d.field_cutoff_hz]): return false
	var mean := 0.0
	for e in d.sector_exposure: mean += float(e) * .25
	if not check(mean > .6, "porch sides should stay open, mean exposure %f" % mean): return false
	if not check(layer_gain(d, "roof", 0) > .05 and layer_gain(d, "roof", 1) > .05, "no roof patter under a porch"): return false
	if not check(await peak() > .000005, "porch PCM missing"): return false
	# Roof plinks fall overhead for as long as the porch is the class.
	await wait_real(3.0)
	d = diag()
	if not check(int(d.shot_kind_totals[KIND_ROOF_PLINK]) > 0, "no roof plinks under a porch: " + str(d.shot_kind_totals)): return false
	# Constructed surfaces as sheet metal: a low shed 5-9 m out on the +around side (top below the eyes, so it is no wall)
	# catches the 7 m columns of that sector at full exposure. Metal fractions appear there and its Metal field voice carries gain.
	fixture.set("ConstructedMaterial", MATERIAL_METAL)
	fixture.AddBox(float(site.around) + 5, ground, float(site.across) - 3, float(site.around) + 9, ground + 1.0, float(site.across) + 4, true)
	await wait_real(SETTLE)
	d = diag()
	var metal_fraction := 0.0
	for sector in 4: metal_fraction = maxf(metal_fraction, float(d.sector_material[sector][MATERIAL_METAL]))
	var metal_gain := 0.0
	for sector in 4: metal_gain += layer_gain(d, "field", sector * 5 + MATERIAL_METAL)
	fixture.set("ConstructedMaterial", RAIN_HARD)
	if not check(metal_fraction > .02 and metal_gain > .0005, "metal porch: metal fraction %f field metal gain %f materials %s" % [metal_fraction, metal_gain, str(d.sector_material)]): return false
	fixture.Clear()
	fixture.GroundTop = ground
	await wait_real(SETTLE)
	return check(diag().shelter_class == "Open" and layer_gain(diag(), "roof", 0) < .001, "porch removal did not restore open air: " + str(diag().shelter_class))

func room(site: Dictionary) -> bool:
	use_fixture(site)
	var ground: float = float(diag().listener_vertical) - 1.54
	fixture.AddRoom(int(site.around), int(site.across), ground - 1.0, ground + 5.0, false)
	await wait_real(SETTLE + 1.0)
	var d := diag()
	if not check(d.shelter_class == "Indoors", "sealed room class " + str(d.shelter_class) + " exposure " + str(d.sector_exposure)): return false
	var maximum := 0.0
	for e in d.sector_exposure: maximum = maxf(maximum, float(e))
	if not check(maximum < .3, "sealed room leaked rain exposure %f" % maximum): return false
	if not check(float(d.cutoff_hz) < 2500.0 and float(d.thunder_db) < -5.0 and float(d.thunder_cutoff_hz) < 3000.0, "room filters/thunder: " + str([d.cutoff_hz, d.thunder_db, d.thunder_cutoff_hz])): return false
	if not check(float(thunder.get("ShelterMuffleAmount")) > .25 and float(thunder.get("ShelterCutoffHz")) < 3000.0, "thunder was not muffled indoors"): return false
	if not check(layer_gain(d, "roof", 0) > .03, "no roof drum indoors"): return false
	# Same room with a three-wide doorway on the +around wall.
	fixture.Clear()
	fixture.GroundTop = ground
	fixture.AddRoom(int(site.around), int(site.across), ground - 1.0, ground + 5.0, true)
	await wait_real(SETTLE + 1.0)
	d = diag()
	var doorway := float(d.sector_exposure[0])
	var others := maxf(float(d.sector_exposure[1]), maxf(float(d.sector_exposure[2]), float(d.sector_exposure[3])))
	if not check(d.shelter_class in ["Roof", "Indoors"] and doorway > .6 and doorway > others * 2.5,
		"doorway sector not dominant: class %s exposure %s" % [d.shelter_class, str(d.sector_exposure)]): return false
	print("RAIN_FIELD_DOORWAY ", {"class": d.shelter_class, "exposure": d.sector_exposure, "bed_cutoff": d.cutoff_hz})
	use_terrain()
	await wait_real(SETTLE)
	return check(diag().shelter_class == "Open", "terrain provider did not restore open air: " + str(diag().shelter_class))

# Generated sites put the terrain's natural surface about 2 m above the collision ground the player stands on (found with the surface diagnostic: natural minus
# camera-1.54 is 1.9-2.1 m at every dry site). That must never read as a cave: open ground stays Open on whichever provider is active (the map when windowed).
func mismatch_open(flat: Dictionary) -> bool:
	print("RAIN_FIELD_PHASE mismatch_open t=", float(Time.get_ticks_usec() - start_usec) / 1000000.0)
	use_terrain()
	var gaps := []
	for index in [1, 2, 3]:
		var site := find_site(index)
		if site.is_empty(): return false
		place(site, float(site.top) + 1)
		await wait_real(SETTLE)
		var d := diag()
		gaps.append(snappedf(float(site.natural) - (float(d.listener_vertical) - 1.54), .01))
		if not check(d.shelter_class == "Open" and not bool(ambience.get("IsRainSheltered")) and not bool(d.deep_shelter), "open ground at mismatch site #%d read as %s (natural over ground %s, source %s)" % [index, d.shelter_class, str(gaps), d.cover_source]): return false
		if not check(float(d.cutoff_hz) > 17000.0 and float(d.thunder_db) > -0.5, "mismatch site #%d muffled: %s" % [index, str([d.cutoff_hz, d.thunder_db])]): return false
	print("RAIN_FIELD_MISMATCH_OPEN ", {"natural_over_ground_m": gaps, "source": diag().cover_source})
	place(flat, float(flat.top) + 1)
	await wait_real(SETTLE)
	return true

# Occlusion-map provider (windowed Vulkan only: the map renders on the GPU). The real map sees a real mesh roof the terrain knows nothing about; the same roof on the
# fixture provider must give the same class. Then the source is flipped back and forth under the roof (map: Roof, terrain: Open) to prove the switch is smooth.
func map_provider(flat: Dictionary) -> bool:
	if RenderingServer.get_rendering_device() == null:
		print("RAIN_FIELD_MAP=SKIP headless: the occlusion map renders on the GPU, run windowed")
		return true
	print("RAIN_FIELD_PHASE map_provider t=", float(Time.get_ticks_usec() - start_usec) / 1000000.0)
	use_terrain()
	ambience.set("PreferOcclusionMap", true)
	place(flat, float(flat.top) + 1)
	var waited := 0.0
	while diag().cover_source != "map" and waited < 20.0:
		await wait_real(.25)
		waited += .25
	if not check(diag().cover_source == "map", "occlusion map never became the exposure source (waited %f s)" % waited): return false
	await wait_real(SETTLE)
	var d := diag()
	if not check(d.shelter_class == "Open" and bool(d.field_seeded), "map: open ground class " + str(d.shelter_class) + " exposure " + str(d.sector_exposure)): return false
	var mean_open := 0.0
	for e in d.sector_exposure: mean_open += float(e) * .25
	if not check(mean_open > .5, "map: open ground exposure " + str(d.sector_exposure)): return false
	var ground: float = float(d.listener_vertical) - 1.54
	var a := float(flat.around)
	var c := float(flat.across)
	# A 5 x 5 mesh roof 5 m over the ground, oriented along the ring axes at the site.
	var here := world(a + .5, ground, c + .5)
	var around := (world(a + 4.5, ground, c + .5) - here).normalized()
	var across := (world(a + .5, ground, c + 4.5) - here).normalized()
	var up := (world(a + .5, ground + 4.0, c + .5) - here).normalized()
	var basis := Basis(around, up, across)
	if basis.determinant() < 0.0: basis = Basis(around, up, -across)
	var roof := MeshInstance3D.new()
	var box := BoxMesh.new()
	box.size = Vector3(5, 1, 5)
	roof.mesh = box
	roof.transform = Transform3D(basis, world(a + .5, ground + 5.5, c + .5))
	scene.add_child(roof)
	await wait_real(SETTLE + 1.5)
	d = diag()
	var map_class: String = str(d.shelter_class)
	if not check(d.cover_source == "map" and map_class == "Roof" and bool(ambience.get("IsRainSheltered")), "map: roof class %s source %s exposure %s" % [map_class, d.cover_source, str(d.sector_exposure)]): return false
	var mean := 0.0
	for e in d.sector_exposure: mean += float(e) * .25
	if not check(mean > .5 and layer_gain(d, "roof", 0) > .05, "map: roof sides should stay open, mean %f roof gain %f" % [mean, layer_gain(d, "roof", 0)]): return false
	var map_cutoff := float(d.cutoff_hz)
	# Same roof on the fixture provider.
	use_fixture(flat)
	fixture.AddBox(a - 2, ground + 5.0, c - 2, a + 3, ground + 6.0, c + 3, true)
	await wait_real(SETTLE)
	d = diag()
	if not check(d.cover_source == "fixture" and d.shelter_class == map_class, "A/B: fixture class %s (source %s) differs from map class %s" % [d.shelter_class, d.cover_source, map_class]): return false
	if not check(absf(float(d.cutoff_hz) - map_cutoff) < 200.0, "A/B: bed cutoff map %f fixture %f" % [map_cutoff, d.cutoff_hz]): return false
	print("RAIN_FIELD_MAP_AB ", {"map": map_class, "fixture": d.shelter_class, "cutoff": [map_cutoff, d.cutoff_hz]})
	fixture.Clear()
	use_terrain()
	await wait_real(SETTLE)
	if not check(diag().cover_source == "map" and diag().shelter_class == "Roof", "back on the map the roof was lost: " + str([diag().cover_source, diag().shelter_class])): return false
	# The terrain provider alone cannot see the mesh roof: Open, which is exactly the jump the switch must smooth.
	ambience.set("PreferOcclusionMap", false)
	await wait_real(SETTLE)
	if not check(diag().cover_source == "terrain" and diag().shelter_class == "Open", "terrain provider on a mesh roof: " + str([diag().cover_source, diag().shelter_class])): return false
	ambience.set("PreferOcclusionMap", true)
	await wait_real(SETTLE)
	if not check(diag().cover_source == "map" and diag().shelter_class == "Roof", "map did not retake the roof: " + str([diag().cover_source, diag().shelter_class])): return false
	# Smooth switch: flip the source every 2 s for 12 s and hold every gain and the cutoff to the slew caps.
	var gains: Array = diag().voice_gains
	var clock := float(diag().clock)
	var cutoff := float(diag().cutoff_hz)
	var classes := {}
	var sources := {}
	var flips := 0
	var worst_rate := 0.0
	var worst_octaves := 0.0
	var begin := Time.get_ticks_usec()
	var next_flip := begin + 1500000
	while Time.get_ticks_usec() - begin < 15000000:
		await process_frame
		if Time.get_ticks_usec() >= next_flip and flips < 6:
			ambience.set("PreferOcclusionMap", flips % 2 == 1)
			flips += 1
			next_flip += 2000000
		var now := diag()
		classes[str(now.shelter_class)] = true
		sources[str(now.cover_source)] = true
		var dt := float(now.clock) - clock
		if dt > 1e-4:
			for index in gains.size():
				var step := absf(float(now.voice_gains[index]) - float(gains[index]))
				worst_rate = maxf(worst_rate, step / dt)
				if not check(step <= .8 * dt + 1e-4, "provider switch: voice %d gain step %f in %f s" % [index, step, dt]): return false
			var octaves := absf(log(float(now.cutoff_hz) / cutoff) / log(2.0))
			worst_octaves = maxf(worst_octaves, octaves / dt)
			if not check(octaves <= 2.5 * dt + 1e-3, "provider switch: cutoff moved %f octaves in %f s" % [octaves, dt]): return false
		clock = float(now.clock)
		gains = now.voice_gains
		cutoff = float(now.cutoff_hz)
	if not check(classes.has("Roof") and classes.has("Open") and sources.has("map") and sources.has("terrain"), "provider switch never exercised both sources/classes: %s %s" % [str(classes), str(sources)]): return false
	print("RAIN_FIELD_MAP_SWITCH ", {"classes": classes.keys(), "sources": sources.keys(), "worst_gain_rate": snappedf(worst_rate, .001), "worst_octaves_per_s": snappedf(worst_octaves, .001)})
	ambience.set("PreferOcclusionMap", true)
	roof.queue_free()
	await wait_real(SETTLE + 1.5)
	# Roof removed: the map (refreshed within a second) restores open air; every mismatch site stays Open on the map as well.
	if not check(diag().cover_source == "map" and diag().shelter_class == "Open", "map roof removal: " + str([diag().cover_source, diag().shelter_class, diag().sector_exposure])): return false
	for index in [1, 2, 3]:
		var site := find_site(index)
		if site.is_empty(): return false
		place(site, float(site.top) + 1)
		await wait_real(SETTLE)
		var s := diag()
		if not check(s.cover_source == "map" and s.shelter_class == "Open" and float(s.cutoff_hz) > 17000.0, "map: mismatch site #%d class %s source %s cutoff %f" % [index, s.shelter_class, s.cover_source, s.cutoff_hz]): return false
	print("RAIN_FIELD_MAP=PASS")
	place(flat, float(flat.top) + 1)
	await wait_real(SETTLE)
	return true

func direction(site: Dictionary) -> bool:
	print("RAIN_FIELD_PHASE direction t=", float(Time.get_ticks_usec() - start_usec) / 1000000.0)
	# One real field voice on the production bus, everything else routed to a quiet sink: stereo must follow the camera, not the speaker.
	AudioServer.add_bus(1)
	AudioServer.set_bus_name(1, &"FieldTestSink")
	AudioServer.set_bus_volume_db(1, -80.0)
	var rig := ambience.get_node("RainFieldRig")
	var voice: AudioStreamPlayer3D = null
	for child in rig.get_children():
		if str(child.name).begins_with("FieldAroundPos") and child.playing and child.volume_db > -70.0 and (voice == null or child.volume_db > voice.volume_db):
			voice = child
	if not check(voice != null, "no playing +around field voice to measure"): return false
	for child in rig.get_children():
		if child != voice: child.bus = &"FieldTestSink"
	for child in [ambience.get_node("BedLight"), ambience.get_node("BedHeavy")]: child.bus = &"FieldTestSink"
	for child in ambience.get_node("RainShots").get_children(): child.bus = &"FieldTestSink"
	camera = Camera3D.new()
	scene.add_child(camera)
	camera.make_current()
	camera.global_position = world(float(site.around) + .5, float(site.top) + 2.5, float(site.across) + .5)
	var up := (world(float(site.around), float(site.top) + 12.5, float(site.across)) - camera.global_position).normalized()
	await wait_real(.5)
	var towards := (voice.global_position - camera.global_position).normalized()
	var forward := up.cross(towards).normalized()
	var before_position := voice.global_position
	camera.look_at(camera.global_position + forward, up)
	await frames(3)
	var one := await measure()
	camera.look_at(camera.global_position - forward, up)
	await frames(3)
	var other := await measure()
	if not check(voice.global_position.distance_to(before_position) < 3.0, "turning the camera moved a ring-fixed speaker"): return false
	if not check((float(one.right) > float(one.left) * 1.3 and float(other.left) > float(other.right) * 1.3) or (float(one.left) > float(one.right) * 1.3 and float(other.right) > float(other.left) * 1.3),
		"stereo did not reverse with the camera: " + str([one, other])): return false
	print("RAIN_FIELD_PAN ", {"first": one, "second": other})
	# Restore routing and the placed pose for the following cases.
	for child in rig.get_children(): child.bus = &"RainField" if (str(child.name).begins_with("Field") or str(child.name).begins_with("Roof")) else (&"Wind" if str(child.name).begins_with("Wind") else &"DistantStorm")
	for child in ambience.get_node("RainShots").get_children(): child.bus = &"RainField"
	ambience.get_node("BedLight").bus = &"RainBed"
	ambience.get_node("BedHeavy").bus = &"RainBed"
	camera.queue_free()
	return true

func cave() -> bool:
	print("RAIN_FIELD_PHASE cave t=", float(Time.get_ticks_usec() - start_usec) / 1000000.0)
	# Natural cover on the synthetic provider: a rock slab 12 m over the listener, then 30 m. Runs in every world size.
	var site := find_site(1)
	if site.is_empty(): return false
	place(site, float(site.top) + 1)
	await wait_real(1.0)
	use_fixture(site)
	var ground: float = float(diag().listener_vertical) - 1.54
	var a := float(site.around)
	var c := float(site.across)
	fixture.AddBox(a - 6, ground + 4.0, c - 6, a + 7, ground + 12.0, c + 7, false)
	await wait_real(SETTLE + 1.0)
	var d := diag()
	if not check(d.shelter_class == "Cave" and not bool(d.deep_shelter), "12 m natural cover class " + str(d.shelter_class)): return false
	if not check(float(d.cutoff_hz) < 1600.0 and float(d.thunder_db) < -7.0 and layer_gain(d, "roof", 0) < .001, "cave filters/roof: " + str([d.cutoff_hz, d.thunder_db, layer_gain(d, "roof", 0)])): return false
	# Drips fall into the cave for as long as it is the class.
	ambience.call("ResetShotStats")
	await wait_real(8.0)
	d = diag()
	if not check(d.shelter_class == "Cave" and int(d.shot_kind_totals[KIND_CAVE_DRIP]) > 0, "no cave drips over the dwell: " + str([d.shelter_class, d.shot_kind_totals])): return false
	fixture.Clear()
	fixture.GroundTop = ground
	fixture.AddBox(a - 6, ground + 4.0, c - 6, a + 7, ground + 40.0, c + 7, false)
	await wait_real(SETTLE + 4.0)
	d = diag()
	if not check(d.shelter_class == "DeepCave" and bool(d.deep_shelter), "deep natural cover class " + str(d.shelter_class)): return false
	if not check(await peak() <= .000001 and float(d.thunder_db) < -12.0 and float(d.thunder_cutoff_hz) < 1000.0, "deep cave retained rain or thunder level " + str([d.thunder_db, d.thunder_cutoff_hz])): return false
	use_terrain()
	# Real carved terrain: editing a Certified-size world stalls the main thread for minutes, so these run in the default world only.
	if certified:
		print("RAIN_FIELD_CAVE_TERRAIN=SKIP certified-size terrain edits stall the main thread")
		return true
	var carved := find_site(2)
	if carved.is_empty(): return false
	var cave_y := floori(float(carved.natural)) - 12
	runner.Stamp(smooth, int(carved.around), int(carved.across), cave_y, 5.0, false)
	place(carved, cave_y)
	await wait_real(SETTLE + 1.0)
	d = diag()
	if not check(runner.IsAir(smooth, int(carved.around), int(carved.across), cave_y), "cave not carved"): return false
	if not check(d.shelter_class == "Cave" and not bool(d.deep_shelter), "carved 12 m cover class " + str(d.shelter_class)): return false
	var deep := find_site(3)
	if deep.is_empty(): return false
	var deep_y := floori(float(deep.natural)) - 38
	runner.Stamp(smooth, int(deep.around), int(deep.across), deep_y, 5.0, false)
	place(deep, deep_y)
	await wait_real(SETTLE + 4.0)
	d = diag()
	if not check(d.shelter_class == "DeepCave" and bool(d.deep_shelter), "carved 38 m cover class " + str(d.shelter_class)): return false
	return await peak() <= .000001

func shoreline() -> bool:
	print("RAIN_FIELD_PHASE shoreline t=", float(Time.get_ticks_usec() - start_usec) / 1000000.0)
	var water: Dictionary = runner.FindSurface(grid, true)
	if not check(bool(water.found), "no generated water"): return false
	# Walk along the around axis from the lake until the material changes to land.
	var shore := {}
	var origin: Dictionary = ambience.call("GetRainSurfaceDiagnostic", int(water.around), int(water.across))
	if not check(origin.material == "Water", "water control column is not water"): return false
	for axis in ["around", "across"]:
		for direction in [1, -1]:
			# Coarse scan (a column query is costly in a Certified world), then bisect the water/land edge to about a metre.
			var samples := []
			for step in range(0, 24000, 128):
				var column := {"around": int(water.around), "across": int(water.across)}
				column[axis] = int(column[axis]) + direction * step
				var sample: Dictionary = ambience.call("GetRainSurfaceDiagnostic", int(column.around), int(column.across))
				samples.append({"step": step, "material": sample.material if bool(sample.found) else "None", "top": float(sample.top)})
			for i in range(1, samples.size() - 2):
				if samples[i - 1].material != "Water" or samples[i].material in ["Water", "None"]: continue
				var solid := true
				for k in range(0, 2):
					if samples[i + k].material in ["Water", "None"]: solid = false
				if not solid: continue
				var lo := int(samples[i - 1].step)
				var hi := int(samples[i].step)
				while hi - lo > 1:
					var mid := (lo + hi) / 2
					var probe := {"around": int(water.around), "across": int(water.across)}
					probe[axis] = int(probe[axis]) + direction * mid
					var result: Dictionary = ambience.call("GetRainSurfaceDiagnostic", int(probe.around), int(probe.across))
					if bool(result.found) and result.material == "Water": lo = mid
					else: hi = mid
				var land := {"around": int(water.around), "across": int(water.across)}
				land[axis] = int(land[axis]) + direction * hi
				var edge: Dictionary = ambience.call("GetRainSurfaceDiagnostic", int(land.around), int(land.across))
				# A shore, not a cliff: the land at the edge sits within a few metres of the water.
				if not bool(edge.found) or absf(float(edge.top) - float(water.top)) > 4.0: continue
				shore = {"land": land, "direction": direction, "axis": axis, "top": float(edge.top)}
				break
			if not shore.is_empty(): break
		if not shore.is_empty(): break
	if not check(not shore.is_empty(), "no shoreline within 24 km of generated water"): return false
	# The scan walked from water to land along `direction`; stand 5 m inland, so the water lies the other way.
	var stand: Dictionary = shore.land.duplicate()
	stand[shore.axis] = int(stand[shore.axis]) + int(shore.direction) * 5
	place(stand, float(shore.top) + 1)
	await wait_real(SETTLE + 1.0)
	var d := diag()
	# Sector ids: 0 +around, 1 +across, 2 -around, 3 -across. Water lies opposite to the scan direction.
	var positive := 0 if shore.axis == "around" else 1
	var toward_sector := positive + 2 if int(shore.direction) > 0 else positive
	var away_sector := positive if int(shore.direction) > 0 else positive + 2
	var toward_water := float(d.sector_material[toward_sector][3])
	var away_water := float(d.sector_material[away_sector][3])
	if not check(toward_water > .15 and away_water < .05, "shoreline water fraction toward %f away %f (exposure %s)" % [toward_water, away_water, str(d.sector_exposure)]): return false
	print("RAIN_FIELD_SHORE ", {"toward": d.sector_material[toward_sector], "away": d.sector_material[away_sector], "exposure": d.sector_exposure})
	return await peak() > .000005

func lifecycle(site: Dictionary) -> bool:
	print("RAIN_FIELD_PHASE lifecycle t=", float(Time.get_ticks_usec() - start_usec) / 1000000.0)
	place(site, float(site.top) + 1)
	await wait_real(SETTLE)
	var before := int(diag().exposure_probe_total)
	# Teleport re-seeds the field instead of dragging old exposure across 3 km.
	var invalidations := int(diag().surface_invalidations)
	var moved := site.duplicate()
	moved.around = int(site.around) + 3000
	place(moved, float(site.top) + 1)
	await frames(3)
	if not check(int(diag().surface_invalidations) > invalidations, "teleport did not re-seed the exposure field"): return false
	await wait_real(SETTLE)
	if not check(bool(diag().field_seeded), "exposure field did not re-seed after teleport"): return false
	place(site, float(site.top) + 1)
	await wait_real(SETTLE)
	# Pause freezes probing and streams.
	var voice: AudioStreamPlayer = ambience.get_node("BedHeavy")
	var queries := int(diag().surface_query_total)
	paused = true
	await frames(2)
	var position := voice.get_playback_position()
	await wait_real(.25)
	if not check(voice.stream_paused and absf(voice.get_playback_position() - position) < .04 and int(diag().surface_query_total) == queries and int(diag().surface_queries_frame) == 0, "pause/query freeze"): return false
	paused = false
	await wait_real(.2)
	if not check(not voice.stream_paused, "resume"): return false
	# Dry weather: true silence from the loops. The surfaces stay wet and drip for a while (tau 90 s), so wetness decays slowly,
	# and exposure/shelter probing keeps running until wetness drops under 0.05 (the dry no-query contract is covered by the core diagnostics).
	rain(0.0)
	await wait_real(3.0)
	var wet_a := float(diag().wetness)
	await wait_real(2.0)
	var wet_b := float(diag().wetness)
	if not check(wet_a > .05 and wet_b < wet_a, "wetness after rain did not decay slowly: %f then %f" % [wet_a, wet_b]): return false
	# The aftermath drips are shots by design; silence them to hear whether anything else leaked.
	var drip_saved := {}
	var no_clips: Array[AudioStream] = []
	for prop in SHOT_LIBRARIES:
		drip_saved[prop] = ambience.get(prop)
		ambience.set(prop, no_clips)
	await wait_real(5.0)
	queries = int(diag().surface_query_total)
	await wait_real(.4)
	if not check(float(diag().wetness) > .05 and int(diag().surface_query_total) > queries and await peak() <= .000001, "post-rain wetness stopped probing or the loops leaked PCM"): return false
	rain(1.0, true)
	await wait_real(.5)
	queries = int(diag().surface_query_total)
	await wait_real(2.0)
	if not check(int(diag().surface_query_total) == queries and await peak() <= .000001, "snow queried terrain or played rain"): return false
	for prop in SHOT_LIBRARIES: ambience.set(prop, drip_saved[prop])
	rain(1.0)
	await wait_real(SETTLE)
	if not check(await peak() > .00001, "rain did not return after snow"): return false
	ambience.set("Enabled", false)
	await frames(3)
	if not check(int(diag().playing_count) == 0 and float(thunder.get("ShelterMuffleAmount")) == 0.0, "disable left voices or a thunder muffle"): return false
	ambience.set("Enabled", true)
	await wait_real(.7)
	var water: Dictionary = runner.FindSurface(grid, true)
	place(water, float(water.top) - 4.0)
	await frames(3)
	if not check(bool(explorer.get("IsCameraUnderwater")) and int(diag().playing_count) == 0 and not bool(diag().can_hear), "underwater was not silent"): return false
	place(site, float(site.top) + 1)
	await wait_real(SETTLE)
	place(site, 30000.0)
	await frames(3)
	if not check(not bool(diag().can_hear) and int(diag().playing_count) == 0, "space silence"): return false
	place(site, float(site.top) + 1)
	await wait_real(SETTLE)
	if not check(int(diag().playing_count) > 0, "reentry did not resume"): return false
	# Missing rain assets: silence and no phantom duck (the one-shot libraries are rain assets too).
	for prop in SHOT_LIBRARIES: ambience.set(prop, no_clips)
	for child in ambience.get_node("RainFieldRig").get_children():
		if str(child.name).begins_with("Field") or str(child.name).begins_with("Roof"): child.stream = null
	ambience.get_node("BedLight").stream = null
	ambience.get_node("BedHeavy").stream = null
	await wait_real(7.0)
	return check(await peak() <= .000001 and is_equal_approx(float(diag().duck), 1.0), "missing rain streams played or ducked")

func measure() -> Dictionary:
	await wait_real(.1)
	capture.clear_buffer()
	await wait_real(.4)
	var energy := Vector2.ZERO
	var count := 0
	while capture.get_frames_available() > 0:
		for frame in capture.get_buffer(mini(capture.get_frames_available(), 2048)):
			if not check(is_finite(frame.x) and is_finite(frame.y), "nonfinite PCM"): return {}
			energy.x += frame.x * frame.x
			energy.y += frame.y * frame.y
			count += 1
	if not check(count > 1024, "capture supplied no PCM"): return {}
	return {"left": sqrt(energy.x / count), "right": sqrt(energy.y / count)}

# Collects 0.2 s of mixed audio. Frames come from the audio thread, which a busy main thread (chunk generation at a fresh site) can delay,
# so wait on the sample count rather than on wall time.
func peak() -> float:
	capture.clear_buffer()
	var wanted := int(AudioServer.get_mix_rate() * .2)
	var waited := 0.0
	while capture.get_frames_available() < wanted and waited < 6.0:
		await wait_real(.05)
		waited += .05
	var result := 0.0
	last_frames = 0
	while capture.get_frames_available() > 0:
		for sample in capture.get_buffer(mini(capture.get_frames_available(), 2048)):
			if not check(is_finite(sample.x) and is_finite(sample.y), "nonfinite PCM"): return 0
			result = maxf(result, maxf(absf(sample.x), absf(sample.y)))
			last_frames += 1
	return result

func world(around: float, height: float, across: float) -> Vector3:
	return grid.call("CanonicalRingPositionToWorld", Vector3(around, height, across))

func diag() -> Dictionary:
	return ambience.call("GetWeatherAmbienceDiagnostics")

func rain(amount: float, snow := false) -> void:
	weather_runner.ConfigureLocalWeather(local, amount, Vector2.ZERO, snow)

func place(column: Dictionary, height: float) -> void:
	bridge.call("SetBenchmarkViewerPosition", float(column.around) + .5, height, float(column.across) + .5)
	explorer.call("ClearBenchmarkOverride")
	explorer.call("_PhysicsProcess", 0.0)
	explorer.set_physics_process(false)

func frames(count: int) -> void:
	for _i in count: await process_frame

func wait_real(seconds: float) -> void:
	await create_timer(seconds, true, false, true).timeout

func check(condition: bool, message: String) -> bool:
	if condition: return true
	done = true
	paused = false
	push_error("RAIN_SOUND_FIELD_BEHAVIOR=FAIL: " + message + (" " + str(diag()) if is_instance_valid(ambience) else ""))
	quit(1)
	return false
