extends SceneTree
# Exercises the actual bounded producer shader, GPU textures and retirement path.
var backend: RefCounted
var elapsed := 0.0
var failed := false
var generation := 0
var results := []
func _initialize() -> void:
    if RenderingServer.get_rendering_device()==null: _fail("Forward+ required"); return
    backend=load("res://scripts/render/RingTerrainCloudSunVisibilityGpu.gd").new()
    call_deferred("_run")
func _process(delta: float) -> bool:
    elapsed+=delta
    if elapsed>120.0: _fail("watchdog exceeded")
    return false
func _run() -> void:
    var theta := TAU/16.0
    var outward := Vector3(cos(theta),0,sin(theta))
    for kind in ["clear","near_blocked","opposite_arc","walls","budget_unknown","regional_ridge"]:
        generation+=1
        var heights := PackedFloat32Array(); heights.resize(16*4); heights.fill(0.0)
        var source := _hierarchy(heights,16,4,TAU*50000.0/16.0)
        var regional := PackedFloat32Array(); regional.resize(16*16); regional.fill(1500.0 if kind=="regional_ridge" else 0.0)
        var sun := (-outward+Vector3(0,1,0)).normalized()
        if kind=="near_blocked" or kind=="regional_ridge": sun=outward
        if kind=="opposite_arc" or kind=="budget_unknown": sun=-outward
        if kind=="walls": sun=Vector3.UP
        var request := {"generation":generation,"terrain_key":str(generation),"height":source.values,"levels":source.levels,"height_size":Vector2i(16,4),
            "regional_height":regional,"regional_height_size":16,"regional_maximum":1500.0 if kind=="regional_ridge" else 0.0,"regional_minimum":1500.0 if kind=="regional_ridge" else 0.0,
            "circumference":TAU*50000.0,"width":10000.0,"radius":50000.0,"vertical_offset":0.0,"sun":sun,
            "global_size":Vector2i(8,4),"regional_size":4,"slices":3,"altitudes":Vector2(500,2000),
            "regional_center":Vector2(theta*50000.0,5000.0),"regional_extent":32000.0,"visits":1 if kind=="budget_unknown" else 128,
            "maximum_height":1500.0 if kind=="regional_ridge" else 0.0,"minimum_height":0.0,
            "walls":Vector2(0,1000) if kind=="walls" else Vector2(1,0)}
        backend.begin_generation(request)
        var complete := {}
        while complete.is_empty():
            backend.advance(4096)
            await RenderingServer.frame_post_draw
            complete=backend.take_completed()
            if failed:return
        if complete.has("error"): _fail(str(complete.error));return
        var texture:=Texture2DRD.new();texture.texture_rd_rid=complete.global
        var image:=texture.get_image()
        if image==null or image.is_empty():_fail("GPU image missing");return
        # Interior first around cell, middle across row, lowest altitude slice.
        var pixel:=image.get_pixel(1,3)
        if kind != "regional_ridge":
            var reference := _flat_reference(outward*49500.0+Vector3(0,6250,0),sun,Vector2(0,1000) if kind=="walls" else Vector2(1,0))
            if kind=="clear" and reference: _fail("clear CPU geometry control is blocked");return
            if kind!="clear" and not reference: _fail("blocked CPU geometry control is clear");return
        if kind=="budget_unknown":
            if pixel.r<.999 or pixel.g>.001:_fail("budget exhaustion fabricated visibility "+str(pixel));return
        elif kind=="clear":
            if pixel.r<.999 or pixel.g<.999:_fail("clear ray not proven clear "+str(pixel));return
        elif pixel.r>.001 or pixel.g<.999:
            _fail("%s blocker not proven occluded: %s" % [kind,pixel]);return
        # Periodic padded cells must agree with their matching interior cells.
        for y in image.get_height():
            if image.get_pixel(0,y)!=image.get_pixel(8,y) or image.get_pixel(9,y)!=image.get_pixel(1,y):
                _fail("periodic atlas padding mismatch");return
        results.append({"case":kind,"visibility":pixel.r,"confidence":pixel.g})
        backend.acknowledge_publication(generation)
        await process_frame
    backend.release()
    print("TERRAIN_CLOUD_SUN_VISIBILITY_GPU_TEST=PASS ",JSON.stringify(results))
    quit(0)
func _flat_reference(origin: Vector3,sun: Vector3,walls: Vector2)->bool:
    # Independent analytic flat-cylinder intersection, without the GPU subdivision path.
    if absf(sun.y)>1e-8 and walls.y>=walls.x:
        for across in [0.0,10000.0]:
            var t: float=(across-origin.y)/sun.y
            if t>0.0:
                var point:=origin+sun*t
                var h:=50000.0-sqrt(float(point.x)*point.x+float(point.z)*point.z)
                if h>=walls.x and h<=walls.y:return true
    var a:=float(sun.x)*sun.x+float(sun.z)*sun.z
    if a<1e-12:return false
    var b:=float(origin.x)*sun.x+float(origin.z)*sun.z
    var c:=float(origin.x)*origin.x+float(origin.z)*origin.z-50000.0*50000.0
    var disc:=b*b-a*c
    if disc<0.0:return false
    var t:=(-b+sqrt(disc))/a
    var across:=origin.y+sun.y*t
    return t>0.0 and across>=0.0 and across<=10000.0
func _hierarchy(base: PackedFloat32Array,nx: int,ny: int,cell_size: float)->Dictionary:
    var values:=PackedFloat32Array();var levels:=PackedFloat32Array();var current:=base
    while true:
        levels.append_array([float(nx),float(ny),float(values.size()),cell_size]);values.append_array(current)
        if nx==1 and ny==1:break
        var px: int=(nx+1)/2;var py: int=(ny+1)/2
        var parents:=PackedFloat32Array();parents.resize(px*py)
        for y in py:
            for x in px:
                var maximum: float=-INF
                for oy in 2:
                    for ox in 2:maximum=maxf(maximum,current[mini(y*2+oy,ny-1)*nx+mini(x*2+ox,nx-1)])
                parents[y*px+x]=maximum
        current=parents;nx=px;ny=py;cell_size*=2.0
    return {"values":values,"levels":levels}
func _fail(message: String)->void:
    if failed:return
    failed=true
    if backend!=null:backend.release()
    push_error("TERRAIN_CLOUD_SUN_VISIBILITY_GPU_TEST=FAIL "+message)
    quit(1)
