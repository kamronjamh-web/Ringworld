extends SceneTree

# Isolated production Sunshine test. No RingSunshineCloudBridge exists here, so
# callback uploads cannot overwrite the atmosphere profile under test.
# 16-slot profile (P2b): slots 0-9 Legacy, 10-15 physical haze. Physical is the default; pass `-- --model=legacy` for the Legacy slots.
const SIZE := Vector2i(640, 360)
const RADIUS := 5000000.0
const WIDTH := 318000.0
var viewport_3d: SubViewport
var adapter: Node
var settings: Resource
var effect: Object
var failed := false
var physical := false

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required")
		return
	var watchdog := create_timer(120.0)
	watchdog.timeout.connect(func() -> void: _fail("120-second watchdog"))
	call_deferred("_run")

func _run() -> void:
	physical = not ("--model=legacy" in OS.get_cmdline_user_args())
	_build(); _configure_frozen_field()
	effect.set("diagnostic_temporal_readback", true)
	effect.set("ring_atmosphere_profile", _disabled())
	var disabled := await _capture("disabled")
	_require(_alpha_mean(disabled) > .0001, "guaranteed field produced no alpha")
	effect.set("ring_atmosphere_profile", _profile(1.0, .45))
	var enabled := await _capture("enabled")
	_require(_rgb_delta(disabled, enabled) > .00002, "enabled haze did not change RGB")
	_require(_alpha_delta(disabled, enabled) < .00001, "haze changed alpha")
	effect.set("ring_atmosphere_profile", _profile(1.0, 0.0))
	var density_zero := await _capture("density_zero")
	_require(_rgb_delta(disabled, density_zero) < .00001, "density-zero changed RGB")
	effect.set("ring_atmosphere_profile", _disabled())
	_require(_rgb_delta(disabled, await _capture("restored")) < .00001, "disabled profile changed RGB")
	effect.set("diagnostic_raw_density_matte", true)
	var raw_before := await _capture("raw_before")
	effect.set("ring_atmosphere_profile", _profile(1.0, .45))
	var raw_after := await _capture("raw_after")
	effect.set("diagnostic_raw_density_matte", false)
	_require(_rgb_delta(raw_before, raw_after) < .00001, "raw diagnostic was haze-composited")
	if failed: return
	print("CLOUD_ATMOSPHERE_GPU=PASS model=", "physical" if physical else "legacy", " rgb=", _rgb_delta(disabled, enabled))
	quit(0)

func _build() -> void:
	viewport_3d = SubViewport.new(); viewport_3d.own_world_3d = true; viewport_3d.size = SIZE; viewport_3d.render_target_update_mode = SubViewport.UPDATE_ALWAYS; root.add_child(viewport_3d)
	var world := WorldEnvironment.new(); world.environment = Environment.new(); world.environment.background_mode = Environment.BG_COLOR; viewport_3d.add_child(world)
	var camera := Camera3D.new(); camera.current = true; camera.position = Vector3(0,20112,0); camera.rotation_degrees = Vector3(-8,0,0); viewport_3d.add_child(camera)
	var light := DirectionalLight3D.new(); viewport_3d.add_child(light); light.look_at(Vector3(.4,-.75,.5),Vector3.UP)
	adapter = (load("res://tools/support/SunshineTestRig.cs") as Script).new(); root.add_child(adapter)
	adapter.call("Configure",world,1337,RADIUS,WIDTH,Vector3(-.4,.75,-.5).normalized())
	effect = adapter.get("Effect") as Object; settings = (load("res://tools/support/SunshineTestRigSettings.cs") as Script).new()

func _configure_frozen_field() -> void:
	var v := {"CloudsEnabled":true,"CloudShadowsEnabled":false,"ShadowGenerationEnabled":false,"AltitudeKm":20.0,"PitchDegrees":8.0,"FieldOfView":65.0,"CloudFloorKm":2.0,"CloudCeilingKm":8.0,"CloudCoverage":.71,"CloudDensity":3.0,"CloudAmbientStrength":2.0,"CloudSharpness":1.0,"CloudResolution":1,"CloudViewSteps":256,"CloudMinStepMeters":10.0,"CloudMaxStepMeters":1600.0,"CloudLodBias":.1,"CloudLightingSteps":5,"CloudRayStartJitterStrength":0.0,"CloudBlurPower":0.0,"CloudDriftMps":0.0,"CloudDitherSpeed":0.0,"CloudStationaryHistoryWeight":0.0,"CloudMovingHistoryWeight":0.0}
	for key: String in v: settings.set(key,v[key])
	adapter.call("ApplyTuning",settings,Vector3(-.4,.75,-.5).normalized())

func _disabled() -> Array[Vector4]:
	var zeros: Array[Vector4] = []
	zeros.resize(16)
	for index in 16: zeros[index] = Vector4.ZERO
	return zeros
func _profile(enabled:float,density:float)->Array[Vector4]:
	var p: Array[Vector4] = [Vector4(enabled,RADIUS,2500,2500),Vector4(20000,60000,300000,.8),Vector4(.16*density,.06*density,.45*density,.26),Vector4(1,1000,1,1000),Vector4(0,12000,.35,.22),Vector4(.34,.46,.60,.25),Vector4(.026,.035,.055,1),Vector4(0,1,0,100),Vector4(0,0,1,WIDTH*.5),Vector4(0,1,0,WIDTH*.5)]
	p.append_array(_disabled().slice(10))
	if physical:
		# Physical slots; density scales every extinction so density 0 is the identity. Values mirror ring_haze_params_default.
		var k := density / .45
		p[10] = Vector4(1, 1, 5.802e-6 * k, 13.558e-6 * k)
		p[11] = Vector4(33.1e-6 * k, 8000, 4.175e-6 * k, .9)
		p[12] = Vector4(1500, 4.0e-6 * k, .95, 400)
		p[13] = Vector4(.6, 8, 1, 12.566)
		p[14] = Vector4(12.566, 12.566, .1855, .203)
		p[15] = Vector4(.2275, .102, .12, .156)
	return p
func _capture(label:String)->Image:
	for frame in 12: adapter.call("UpdateFrame",17219375.0,10.0,true); await process_frame
	await RenderingServer.frame_post_draw
	var texture:=effect.call("get_cloud_backdrop_texture",0) as Texture2D
	if texture==null: _fail("backdrop missing "+label); return Image.new()
	return texture.get_image()
func _rgb_delta(a:Image,b:Image)->float:
	var total:=0.0
	for y in a.get_height(): for x in a.get_width(): var l:=a.get_pixel(x,y); var r:=b.get_pixel(x,y); total+=absf(l.r-r.r)+absf(l.g-r.g)+absf(l.b-r.b)
	return total/max(1,a.get_width()*a.get_height())
func _alpha_delta(a:Image,b:Image)->float:
	var total:=0.0
	for y in a.get_height(): for x in a.get_width(): total+=absf(a.get_pixel(x,y).a-b.get_pixel(x,y).a)
	return total/max(1,a.get_width()*a.get_height())
func _alpha_mean(a:Image)->float:
	var total:=0.0
	for y in a.get_height(): for x in a.get_width(): total+=a.get_pixel(x,y).a
	return total/max(1,a.get_width()*a.get_height())
func _require(ok:bool,message:String)->void: if not ok: _fail(message)
func _fail(message:String)->void: if not failed: failed=true; push_error("CLOUD_ATMOSPHERE_GPU: "+message); quit(1)
