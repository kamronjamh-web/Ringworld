extends SceneTree

# Actual production Sunshine + far-backdrop integration. Pixel reads occur only
# in this fixture; production rain never reads GPU pixels back to the CPU.
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/RainPrecipitationTestDriver.cs")
const ISOLATION := preload("res://scripts/Tools/MinimapWindContractTest.cs")
var world: Node
var driver: RefCounted
var local: Node
var cloud: Resource
var environment: WorldEnvironment
var output := ""
var failed := false

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null: fail("Forward+ required"); return
	run.call_deferred()

func run() -> void:
	create_timer(90).timeout.connect(func(): fail("watchdog expired"))
	output = "res://.godot/rain-cloud-compositing-%d-%d" % [OS.get_process_id(),Time.get_ticks_usec()]
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output))
	world = WORLD.instantiate()
	world.get_node("ScaledFarViewport").set("Enabled",true)
	var isolated: Dictionary = ISOLATION.new().ExecuteFixture("isolation",world,output)
	if not check(bool(isolated.get("pass",false)),"isolation failed"): return
	root.add_child(world)
	driver = DRIVER.new()
	for frame in range(12): await process_frame
	local = world.get_node("LocalWeatherEffects")
	local.set_process(false); local.set("EnableLightning",false)
	world.get_node("Explorer").set_physics_process(false)
	world.get_node("Explorer").set_process(false)
	world.get_node("Explorer").call("SetPresentationView",45.0,55.0)
	# Measure the composed scene, without HUD/menu/map canvas overlays.
	for name in ["LiveTuningDrawer","TerrainMinimap","PresentationHud","LodDebugHud","CelestialDebugHud","LodPauseMenu"]:
		var overlay := world.get_node_or_null(name) as CanvasLayer
		if overlay != null: overlay.visible = false
	world.get_node("RingDayNightCycle").set("Paused",true)
	driver.call("SetDaylightForFixture",world.get_node("RingDayNightCycle"),1.0)
	driver.call("SetShelterForFixture",world.get_node("WeatherAmbience"),false)
	driver.call("SetUnderwaterForFixture",world.get_node("Explorer"),false)
	var bridge := world.get_node("SunshineCloudBridge")
	bridge.set("CloudCoverageBias",1.0); bridge.set("CloudDensityMultiplier",2.0)
	bridge.set("StationaryHistoryWeight",0.0); bridge.set("MovingHistoryWeight",0.0)
	bridge.set("DitherSpeed",0.0); bridge.set("RayStartJitterStrength",0.0)
	bridge.call("ApplyTemporalQualityTuning", false); bridge.call("ApplyCloudAppearanceTuning")
	cloud = world.get_node("SunshineCloudsDriver").get("clouds_resource")
	# Fixture-only COPY_FROM allocation permits alpha-based cloud pixel selection.
	cloud.set("diagnostic_temporal_readback", true)
	cloud.call("refresh_compute")
	environment = world.get_node("WorldEnvironment")
	driver.call("SetHazeControl","VariationStrength",0.0)
	driver.call("SetHazeControl","Tint",Color(.35,.4,.45))
	driver.call("SetIntensity",local,1.0,false,Vector2(8,-3))
	for step in range(220): local.call("_Process",1.0/60.0)
	for name in ["Explorer","RingVolumetricClouds","SunshineCloudBridge","SunshineCloudsDriver","WeatherAmbience","DistantRainCurtains"]:
		var node := world.get_node_or_null(name)
		if node != null: node.set_process(false); node.set_physics_process(false)
	var far_view := world.get_node("ScaledFarViewport").find_child("FarRingViewport",true,false) as SubViewport
	if not check(far_view != null,"production far viewport missing"): return
	for frame in range(8): await process_frame
	freeze_scene(world)
	freeze_exposure()
	# The atmosphere controller owns tonemapping while processing. Override only
	# after freezing it, so the quantitative comparison stays in linear light.
	environment.environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	# Linear exposure commutes with the haze blend and bounds HDR highlights for
	# an 8-bit screenshot comparison without changing the renderer's light cache.
	environment.environment.tonemap_exposure = .025
	environment.environment.glow_enabled = false
	environment.environment.adjustment_enabled = false
	environment.environment.background_energy_multiplier = .15
	environment.environment.ambient_light_energy = .15
	for light in world.find_children("*","Light3D",true,false): light.light_energy = 0.0
	cloud.set("cloud_ambient_color",Color(.25,.25,.25))
	cloud.set("cloud_ambient_tint",Color(.5,.5,.5))
	cloud.set("lights_updated",true)
	var far_image := far_view.get_texture().get_image()
	if not check(far_image != null and not far_image.is_empty(),"production far backdrop readback empty"): return
	far_image.save_png(output + "/far_backdrop.png")
	if OS.get_cmdline_user_args().has("--enclosed-fog"):
		await enclosed_fog_test(bridge)
		return
	if OS.get_cmdline_user_args().has("--diagnose-msaa"):
		await diagnose_msaa()
		return
	# The frozen production monitor does not collect during this fixture.
	# Enable the same real asynchronous marker reader and drain only fixture samples.
	driver.call("ConfigureRainGpuProfilerForFixture",true)
	var dry_baseline: Image
	var dry_baseline_cloud: Image
	var validation_errors: Array[String] = []
	for msaa: int in [Viewport.MSAA_DISABLED,Viewport.MSAA_2X,Viewport.MSAA_4X,Viewport.MSAA_8X]:
		root.msaa_3d = msaa
		driver.call("SetHazeControl","Enabled",false)
		var clear := await capture("clear_msaa%d" % msaa)
		var dry_scopes_before := int(local.call("GetRainHazeDiagnostics").compositor_timestamp_scopes)
		for frame in range(4):
			await process_frame
			driver.call("RequestRainGpuReadbackForFixture")
		var dry_scopes_after := int(local.call("GetRainHazeDiagnostics").compositor_timestamp_scopes)
		if not check(dry_scopes_before == dry_scopes_after,"dry rain emitted a GPU scope"): return
		driver.call("DrainRainGpuSamplesForFixture")
		var backdrop: Texture2D = cloud.call("get_cloud_backdrop_texture",0)
		if not check(backdrop != null,"Sunshine cloud texture unavailable"): return
		var cloud_image := backdrop.get_image()
		if not check(cloud_image != null and not cloud_image.is_empty(),"Sunshine cloud readback empty"): return
		cloud_image.save_png(output + "/cloud_density_msaa%d.png" % msaa)
		if msaa == Viewport.MSAA_DISABLED:
			dry_baseline = clear; dry_baseline_cloud = cloud_image
		else:
			var raw_aa_drift := image_delta(dry_baseline_cloud,cloud_image)
			print("RAIN_CLOUD_RAW_MSAA_PARITY msaa=%d mean_rgba_delta=%.9f" % [msaa,raw_aa_drift])
			if raw_aa_drift >= .00001: validation_errors.append("MSAA%d raw cloud source changed: %.9f" % [msaa,raw_aa_drift])
			var dry_error := cloud_scene_delta(dry_baseline,clear,dry_baseline_cloud,cloud_image)
			print("RAIN_CLOUD_DRY_MSAA_PARITY msaa=%d mean_linear_rgb_error=%.6f" % [msaa,dry_error])
			if dry_error >= .004: validation_errors.append("MSAA%d dry scene parity error %.6f exceeds .004" % [msaa,dry_error])
		driver.call("SetHazeControl","Enabled",true)
		var rainy := await capture("rain_msaa%d" % msaa)
		var rain_gpu: Dictionary = driver.call("DrainRainGpuSamplesForFixture")
		print("RAIN_GPU_TIMESTAMP msaa=%d category=GPU.RainHaze count=%d source_frame=%d maximum_ms=%.6f" % [msaa,rain_gpu.count,rain_gpu.source_frame,rain_gpu.maximum_ms])
		if not check(int(rain_gpu.count) > 0 and int(rain_gpu.source_frame) >= 0,"active rain GPU timestamp category/source frame missing"): return
		var rainy_cloud: Texture2D = cloud.call("get_cloud_backdrop_texture",0)
		var rainy_cloud_image := rainy_cloud.get_image()
		var cloud_drift := image_delta(cloud_image,rainy_cloud_image)
		print("RAIN_CLOUD_SOURCE_PARITY msaa=%d mean_rgba_delta=%.9f" % [msaa,cloud_drift])
		if cloud_drift >= .00001: validation_errors.append("MSAA%d rain-only cloud source drift %.9f" % [msaa,cloud_drift])
		var diagnostics: Dictionary = local.call("GetRainHazeDiagnostics")
		print("RAIN_CLOUD_TONEMAP actual=%d expected=%d" % [environment.environment.tonemap_mode,Environment.TONE_MAPPER_LINEAR])
		if not check(exposure_is_fixed(),"fixture auto exposure remains active"): return
		if not check(environment.environment.tonemap_mode == Environment.TONE_MAPPER_LINEAR,"fixture linear tonemapping was overwritten"): return
		var camera := root.get_camera_3d()
		print("RAIN_CLOUD_EFFECTIVE state=%s camera_far=%s" % [str(diagnostics),str(camera.far if camera != null else -1)])
		if not check(bool(diagnostics.compositor_active) and not bool(diagnostics.fallback_visible),"rain presentation duplicated or compositor unavailable"): return
		var effects := environment.compositor.compositor_effects
		var rain_count := 0
		for effect in effects:
			if effect.get_instance_id() == int(diagnostics.compositor_id): rain_count += 1
		if not check(rain_count == 1 and effects[-1].get_instance_id() == int(diagnostics.compositor_id),"rain must appear exactly once after Sunshine"): return
		var eligible := 0; var changed := 0; var once_error := 0.0; var twice_error := 0.0
		var sample_min := 1.0; var sample_max := 0.0
		var opacity := float(driver.call("HazeOpacity",float(diagnostics.rain_intensity),minf(750.0,camera.far)))
		var linear_tint := Color(.35,.4,.45).srgb_to_linear()
		var tint := Vector3(linear_tint.r,linear_tint.g,linear_tint.b) * environment.environment.tonemap_exposure
		for y in range(8,clear.get_height()/2,8):
			for x in range(8,clear.get_width()-8,8):
				var uv := Vector2(float(x)/clear.get_width(),float(y)/clear.get_height())
				var c := cloud_image.get_pixel(clampi(int(uv.x*cloud_image.get_width()),0,cloud_image.get_width()-1),clampi(int(uv.y*cloud_image.get_height()),0,cloud_image.get_height()-1))
				if c.a < .2: continue
				var before := clear.get_pixel(x,y); var after := rainy.get_pixel(x,y)
				sample_min = minf(sample_min,minf(before.r,minf(before.g,before.b)))
				sample_max = maxf(sample_max,maxf(before.r,maxf(before.g,before.b)))
				var a := Vector3(before.r,before.g,before.b); var b := Vector3(after.r,after.g,after.b)
				# Linear framebuffer -> sRGB Image. Compare in linear light.
				before = before.srgb_to_linear(); after = after.srgb_to_linear()
				a = Vector3(before.r,before.g,before.b); b = Vector3(after.r,after.g,after.b)
				var once := a.lerp(tint,opacity); var twice := once.lerp(tint,opacity)
				eligible += 1
				if a.distance_to(b) > .005: changed += 1
				once_error += once.distance_to(b); twice_error += twice.distance_to(b)
		print("RAIN_CLOUD_ORDER msaa=%d cloud_pixels=%d changed=%d mean_once_error=%.6f mean_twice_error=%.6f far_backdrop=%s" % [msaa,eligible,changed,once_error/maxi(eligible,1),twice_error/maxi(eligible,1),str(world.get_node("ScaledFarViewport").get("Enabled"))])
		print("RAIN_CLOUD_SAMPLE_RANGE min=%.6f max=%.6f" % [sample_min,sample_max])
		print_pixel_mix("far_on",clear,rainy,tint,opacity)
		if eligible < 8 or changed < 8: validation_errors.append("MSAA%d insufficient measured cloudy/rainy pixels" % msaa)
		if sample_max >= .99: validation_errors.append("MSAA%d clipped HDR samples" % msaa)
		if once_error/maxi(eligible,1) >= .004: validation_errors.append("MSAA%d single haze error %.6f exceeds .004" % [msaa,once_error/maxi(eligible,1)])
		if once_error >= twice_error: validation_errors.append("MSAA%d pixels resemble double haze" % msaa)
	if not validation_errors.is_empty():
		world.queue_free()
		for frame in range(12): await process_frame
		fail("; ".join(validation_errors)); return
	# Provider lifecycle can restart tuning; test it only after AA comparisons.
	for msaa: int in [Viewport.MSAA_DISABLED,Viewport.MSAA_2X,Viewport.MSAA_4X,Viewport.MSAA_8X]:
		root.msaa_3d = msaa
		await capture("rain_lifecycle_start_msaa%d" % msaa)
		var diagnostics: Dictionary = local.call("GetRainHazeDiagnostics")
		# Reattachment is the same operation used when providers switch/rebuild.
		bridge.call("SetProviderActive",false)
		local.call("_Process",1.0/60.0)
		var detached := environment.compositor.compositor_effects
		if not check(detached[-1].get_instance_id() == int(diagnostics.compositor_id),"cloud detach removed rain ownership"): return
		await capture("rain_provider_off_msaa%d" % msaa)
		bridge.call("SetProviderActive",true)
		local.call("_Process",1.0/60.0)
		if not check(environment.compositor.compositor_effects[-1].get_instance_id() == int(diagnostics.compositor_id),"provider reattachment moved clouds after rain"): return
		cloud.call("refresh_compute")
		await capture("rain_cloud_rebuild_msaa%d" % msaa)
		var rebuilt: Dictionary = local.call("GetRainHazeDiagnostics")
		var rebuilt_count := 0
		for effect in environment.compositor.compositor_effects:
			if effect.get_instance_id() == int(rebuilt.compositor_id): rebuilt_count += 1
		if not check(rebuilt_count == 1 and bool(rebuilt.compositor_active) and not bool(rebuilt.fallback_visible),"cloud rebuild duplicated haze or activated fallback"): return
		# Reactivation deliberately restarts the bridge. Freeze it again before
		# the next baseline and restore fixture values it may have overwritten.
		freeze_scene(world)
		freeze_exposure()
		environment.environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
		environment.environment.tonemap_exposure = .025
		environment.environment.glow_enabled = false
		environment.environment.adjustment_enabled = false
		environment.environment.background_energy_multiplier = .15
		environment.environment.ambient_light_energy = .15
		for light in world.find_children("*","Light3D",true,false): light.light_energy = 0.0
		cloud.set("cloud_ambient_color",Color(.25,.25,.25))
		cloud.set("cloud_ambient_tint",Color(.5,.5,.5))
		cloud.set("lights_updated",true)
	driver.call("ConfigureRainGpuProfilerForFixture",false)
	world.queue_free()
	for frame in range(12): await process_frame
	print("RAIN_CLOUD_COMPOSITING_GPU=PASS output=" + output)
	quit(0)

func freeze_exposure() -> void:
	var camera := root.get_camera_3d()
	for attributes in [environment.camera_attributes,camera.attributes if camera != null else null]:
		if attributes == null: continue
		attributes.auto_exposure_enabled = false
		attributes.exposure_multiplier = 1.0

func exposure_is_fixed() -> bool:
	var camera := root.get_camera_3d()
	var fixed := true
	for attributes in [environment.camera_attributes,camera.attributes if camera != null else null]:
		if attributes == null: continue
		print("RAIN_CLOUD_EXPOSURE attributes_id=%d auto=%s multiplier=%.6f" % [attributes.get_instance_id(),str(attributes.auto_exposure_enabled),attributes.exposure_multiplier])
		fixed = fixed and not attributes.auto_exposure_enabled and is_equal_approx(attributes.exposure_multiplier,1.0)
	return fixed

func diagnose_msaa() -> void:
	driver.call("SetHazeControl","Enabled",false)
	var raw_baseline: Image
	var depth_baseline: Image
	for msaa: int in [Viewport.MSAA_DISABLED,Viewport.MSAA_2X,Viewport.MSAA_4X,Viewport.MSAA_8X]:
		root.msaa_3d = msaa
		cloud.set("enabled",true)
		cloud.set("diagnostic_raw_density_matte",false)
		cloud.set("diagnostic_depth_density_matte",false)
		var normal := await capture("diagnose_normal_msaa%d" % msaa)
		cloud.set("diagnostic_raw_density_matte",true)
		var raw := await capture("diagnose_raw_msaa%d" % msaa)
		cloud.set("diagnostic_raw_density_matte",false)
		cloud.set("diagnostic_depth_density_matte",true)
		var depth := await capture("diagnose_depth_msaa%d" % msaa)
		cloud.set("diagnostic_depth_density_matte",false)
		cloud.set("enabled",false)
		var background := await capture("diagnose_background_msaa%d" % msaa)
		cloud.set("enabled",true)
		if not check(not normal.is_empty() and not raw.is_empty() and not depth.is_empty() and not background.is_empty(),"diagnostic image missing"): return
		var raw_delta := 0.0; var depth_delta := 0.0
		if msaa == Viewport.MSAA_DISABLED:
			raw_baseline = raw; depth_baseline = depth
		else:
			raw_delta = image_delta(raw_baseline,raw); depth_delta = image_delta(depth_baseline,depth)
		var point := Vector2i(normal.get_width()/2,normal.get_height()/4)
		print("RAIN_MSAA_DIAGNOSTIC mode=%d point=%s normal=%s raw=%s depth=%s background=%s raw_cross_aa=%.9f depth_cross_aa=%.9f cloud_id=%d effect_count=%d rain=%s" % [msaa,str(point),str(normal.get_pixelv(point)),str(raw.get_pixelv(point)),str(depth.get_pixelv(point)),str(background.get_pixelv(point)),raw_delta,depth_delta,cloud.get_instance_id(),environment.compositor.compositor_effects.size(),str(local.call("GetRainHazeDiagnostics"))])
	world.queue_free()
	for frame in range(12): await process_frame
	print("RAIN_CLOUD_MSAA_DIAGNOSTICS=PASS strict_suite_not_run=true output=" + output)
	quit(0)

# P6: cave/underwater Godot fog fades Sunshine clouds at cloud depth (bridge SetEnclosedFog -> GenericData.ring_enclosed_fog ->
# SunshineCloudsPostCompute). Fade fraction f = |scene - scene_with_fog| / |scene - scene_without_clouds| over cloud pixels.
func enclosed_fog_test(bridge: Node) -> void:
	root.msaa_3d = Viewport.MSAA_DISABLED
	driver.call("SetHazeControl","Enabled",false)
	var default_image := await capture("enclosed_fog_default")
	var backdrop: Texture2D = cloud.call("get_cloud_backdrop_texture",0)
	if not check(backdrop != null,"Sunshine cloud texture unavailable"): return
	var cloud_image := backdrop.get_image()
	cloud.set("enabled",false)
	var background := await capture("enclosed_fog_no_clouds")
	cloud.set("enabled",true)
	var density := 0.003
	bridge.call("SetEnclosedFog",density,1.0,0.0)
	var zero_weight := await capture("enclosed_fog_weight0")
	bridge.call("SetEnclosedFog",density,0.0,1.0)
	var zero_affect := await capture("enclosed_fog_sky_affect0")
	bridge.call("SetEnclosedFog",density,1.0,0.5)
	var half := await capture("enclosed_fog_weight_half")
	bridge.call("SetEnclosedFog",density,1.0,1.0)
	var full := await capture("enclosed_fog_weight1")
	bridge.call("SetEnclosedFog",0.0,0.0,0.0)
	var restored := await capture("enclosed_fog_restored")
	var reference := cloud_fade_fraction(default_image,background,default_image,cloud_image)
	var f_zero_weight := cloud_fade_fraction(default_image,background,zero_weight,cloud_image)
	var f_zero_affect := cloud_fade_fraction(default_image,background,zero_affect,cloud_image)
	var f_half := cloud_fade_fraction(default_image,background,half,cloud_image)
	var f_full := cloud_fade_fraction(default_image,background,full,cloud_image)
	var f_restored := cloud_fade_fraction(default_image,background,restored,cloud_image)
	print("RAIN_CLOUD_ENCLOSED_FOG pixels=%d clouds_vs_background=%.6f weight0=%.6f sky_affect0=%.6f half=%.6f full=%.6f restored=%.6f" % [reference.count,reference.contrast,f_zero_weight.fade,f_zero_affect.fade,f_half.fade,f_full.fade,f_restored.fade])
	var errors: Array[String] = []
	if reference.count < 8 or reference.contrast < .0005: errors.append("too few/low-contrast cloud pixels (%d, %.6f)" % [reference.count,reference.contrast])
	if f_zero_weight.fade >= .01: errors.append("weight 0 changed clouds: %.6f" % f_zero_weight.fade)
	if f_zero_affect.fade >= .01: errors.append("sky affect 0 changed clouds: %.6f" % f_zero_affect.fade)
	if f_restored.fade >= .01: errors.append("clearing the fog did not restore clouds: %.6f" % f_restored.fade)
	if f_full.fade < .5: errors.append("full-weight fog faded clouds only %.6f (< .5)" % f_full.fade)
	if f_half.fade < .1 or f_half.fade >= f_full.fade: errors.append("half-weight fade %.6f not between 0.1 and full %.6f" % [f_half.fade,f_full.fade])
	world.queue_free()
	for frame in range(12): await process_frame
	if not errors.is_empty(): fail("; ".join(errors)); return
	print("RAIN_CLOUD_ENCLOSED_FOG=PASS output=" + output)
	quit(0)

func cloud_fade_fraction(base: Image, background: Image, test: Image, cloud_image: Image) -> Dictionary:
	var fade := 0.0; var contrast := 0.0; var count := 0
	for y in range(8,base.get_height()/2,8):
		for x in range(8,base.get_width()-8,8):
			var c := cloud_image.get_pixel(clampi(int(float(x)/base.get_width()*cloud_image.get_width()),0,cloud_image.get_width()-1),clampi(int(float(y)/base.get_height()*cloud_image.get_height()),0,cloud_image.get_height()-1))
			if c.a < .2: continue
			var p := base.get_pixel(x,y).srgb_to_linear(); var q := background.get_pixel(x,y).srgb_to_linear(); var r := test.get_pixel(x,y).srgb_to_linear()
			var span := Vector3(p.r,p.g,p.b).distance_to(Vector3(q.r,q.g,q.b))
			if span < .0005: continue
			fade += clampf(Vector3(p.r,p.g,p.b).distance_to(Vector3(r.r,r.g,r.b)) / span,0.0,2.0); contrast += span; count += 1
	return {"count":count,"fade":fade/maxi(count,1),"contrast":contrast/maxi(count,1)}

func cloud_scene_delta(a: Image, b: Image, cloud_a: Image, cloud_b: Image) -> float:
	var error := 0.0; var count := 0
	for y in range(8,a.get_height()/2,8):
		for x in range(8,a.get_width()-8,8):
			var uv := Vector2(float(x)/a.get_width(),float(y)/a.get_height())
			var ca := cloud_a.get_pixel(clampi(int(uv.x*cloud_a.get_width()),0,cloud_a.get_width()-1),clampi(int(uv.y*cloud_a.get_height()),0,cloud_a.get_height()-1))
			var cb := cloud_b.get_pixel(clampi(int(uv.x*cloud_b.get_width()),0,cloud_b.get_width()-1),clampi(int(uv.y*cloud_b.get_height()),0,cloud_b.get_height()-1))
			if ca.a < .2 or cb.a < .2: continue
			var p := a.get_pixel(x,y).srgb_to_linear(); var q := b.get_pixel(x,y).srgb_to_linear()
			error += Vector3(p.r,p.g,p.b).distance_to(Vector3(q.r,q.g,q.b)); count += 1
	return error/maxi(count,1) if count >= 8 else INF

func image_delta(a: Image, b: Image) -> float:
	if a.get_size() != b.get_size(): return INF
	var delta := 0.0
	for y in range(a.get_height()):
		for x in range(a.get_width()):
			var p := a.get_pixel(x,y); var q := b.get_pixel(x,y)
			delta += absf(p.r-q.r)+absf(p.g-q.g)+absf(p.b-q.b)+absf(p.a-q.a)
	return delta/maxi(a.get_width()*a.get_height()*4,1)

func print_pixel_mix(label: String, before: Image, after: Image, tint: Vector3, opacity: float) -> void:
	var error := 0.0; var count := 0
	for y in range(8,before.get_height()/2,8):
		for x in range(8,before.get_width()-8,8):
			var a_color := before.get_pixel(x,y).srgb_to_linear()
			var b_color := after.get_pixel(x,y).srgb_to_linear()
			var a := Vector3(a_color.r,a_color.g,a_color.b)
			var b := Vector3(b_color.r,b_color.g,b_color.b)
			error += a.lerp(tint,opacity).distance_to(b); count += 1
	var point := Vector2i(before.get_width()/2,before.get_height()/4)
	var a_color := before.get_pixelv(point).srgb_to_linear()
	var b_color := after.get_pixelv(point).srgb_to_linear()
	var a := Vector3(a_color.r,a_color.g,a_color.b)
	var b := Vector3(b_color.r,b_color.g,b_color.b)
	var delta := tint-a
	var inferred := (b-a).dot(delta)/maxf(delta.length_squared(),.000001)
	print("RAIN_CLOUD_MIX %s mean_once_error=%.6f point=%s before=%s after=%s expected=%s opacity=%.6f inferred=%.6f" % [label,error/maxi(count,1),str(point),str(a),str(b),str(a.lerp(tint,opacity)),opacity,inferred])

func freeze_scene(node: Node) -> void:
	node.set_process(false); node.set_physics_process(false)
	for child in node.get_children(): freeze_scene(child)

func capture(label: String) -> Image:
	local.call("_Process",1.0/60.0)
	for frame in range(8):
		await process_frame
		driver.call("RequestRainGpuReadbackForFixture")
	# Warm-up readiness must be reflected in an immutable active frame.
	local.call("_Process",1.0/60.0)
	for frame in range(6):
		await process_frame
		driver.call("RequestRainGpuReadbackForFixture")
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	image.save_png(output + "/" + label + ".png")
	return image
func check(ok: bool, message: String) -> bool:
	if not ok: fail(message)
	return ok
func fail(message: String) -> void:
	if failed: return
	failed = true; push_error("RAIN_CLOUD_COMPOSITING_GPU=FAIL " + message); quit(1)
