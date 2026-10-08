extends SceneTree

# Direct GPU contract for the portable policy helpers. Production compositor
# integration is additionally covered by terrain/motion/lightning fixtures.
var viewport: SubViewport
var failed := false
func _init() -> void:
	call_deferred("run")
	create_timer(30).timeout.connect(func(): fail("watchdog expired"))
func run() -> void:
	if RenderingServer.get_rendering_device() == null: return fail("Forward+ GPU required")
	viewport = SubViewport.new()
	viewport.size = Vector2i(19,1)
	viewport.use_hdr_2d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var shader := Shader.new()
	shader.code = '''shader_type canvas_item;
#include "res://addons/SunshineClouds2/RingCloudHistory.gdshaderinc"
void fragment() {
 int c = int(floor(UV.x * 19.0));
 float result = 0.0;
 if (c == 0) result = float(ring_cloud_history_opaque_matches(-1.0,-1.0,2.0));
 if (c == 1) result = float(ring_cloud_history_opaque_matches(-1.0,100.0,2.0));
 if (c == 2) result = float(ring_cloud_history_opaque_matches(100.0,101.0,2.0));
 if (c == 3) result = float(ring_cloud_history_opaque_matches(100.0,120.0,2.0));
 if (c == 4) result = ring_cloud_history_compatible_weight(vec4(1.0,1.0,1.0,.5),vec4(1.0,1.0,1.0,.5),1000.0,1000.0,100.0,.95,true);
 if (c == 5) result = ring_cloud_history_compatible_weight(vec4(1.0,1.0,1.0,.8),vec4(1.0,1.0,1.0,.1),1000.0,1000.0,100.0,.95,true);
 if (c == 6) result = ring_cloud_history_compatible_weight(vec4(1.0,1.0,1.0,.5),vec4(1.0,1.0,1.0,.5),1000.0,5000.0,100.0,.95,true);
 if (c == 7) result = ring_cloud_history_compatible_weight(vec4(1.0,1.0,1.0,.8),vec4(1.0,1.0,1.0,.1),1000.0,5000.0,100.0,.95,false);
 if (c == 8) result = float(ring_cloud_history_terminal_jitter(true,1.0,.5,1053.0,1200.0,1053.0,862.0,1200.0,1200.0,.1,958.0,2.0));
 if (c == 9) result = float(ring_cloud_history_terminal_jitter(false,1.0,.5,1053.0,300.0,1053.0,300.0,1200.0,300.0,.1,958.0,2.0));
 if (c == 10) result = float(ring_cloud_history_terminal_jitter(true,0.0,.5,1053.0,1200.0,1053.0,862.0,1200.0,1200.0,.1,958.0,2.0));
 if (c == 11) result = float(ring_cloud_history_terminal_jitter(true,1.0,.5,4000.0,6000.0,2000.0,5000.0,6000.0,6000.0,.1,958.0,2.0));
 if (c == 12) result = float(ring_cloud_history_terminal_jitter(true,1.0,3.0,1053.0,1200.0,1053.0,862.0,1200.0,1200.0,.1,958.0,2.0));
 if (c == 13) result = float(ring_cloud_history_terminal_jitter(true,1.0,.5,52650.0,60000.0,52650.0,43100.0,60000.0,60000.0,.1,47900.0,2.0));
 if (c == 14) result = float(ring_cloud_history_sky_jitter(true,true,1.0,.5,.1,958.0));
 if (c == 15) result = float(ring_cloud_history_sky_jitter(false,true,1.0,.5,.1,958.0));
 if (c == 16) result = float(ring_cloud_history_sky_jitter(true,false,1.0,.5,.1,958.0));
 if (c == 17) result = float(ring_cloud_history_sky_jitter(true,true,1.0,3.0,.1,958.0));
 if (c == 18) result = float(ring_cloud_history_sky_jitter(true,true,1.0,.5,100.0,958.0));
 COLOR = vec4(result,result,result,1.0);
}'''
	var material := ShaderMaterial.new()
	material.shader = shader
	var rect := ColorRect.new()
	rect.size = Vector2(19,1)
	rect.material = material
	viewport.add_child(rect)
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	var expected := [1.0,0.0,1.0,0.0,.95,0.0,0.0,.95,1.0,0.0,0.0,0.0,0.0,1.0,1.0,0.0,0.0,0.0,0.0]
	for i in 19:
		if absf(image.get_pixel(i,0).r - expected[i]) > .002: return fail("policy pixel %s differs" % i)
	viewport.queue_free()
	await process_frame
	if not failed:
		print("CLOUD_HISTORY_POLICY_GPU=PASS coherent_requested_weight=.95")
		quit(0)
func fail(message: String) -> void:
	if failed: return
	failed = true
	push_error("CLOUD_HISTORY_POLICY_GPU=FAIL " + message)
	if is_instance_valid(viewport): viewport.queue_free()
	quit(1)
