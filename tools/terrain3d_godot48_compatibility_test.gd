extends SceneTree
## Protect addon-specific asset names and region copies from inherited Resource method collisions.
func _initialize() -> void:
	var fixture := "res://.godot/terrain3d-godot48-compatibility"
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture)) != OK:
		fail("cannot prepare isolated serialization fixture")
		return
	for class_name_text: String in ["Terrain3DMeshAsset", "Terrain3DTextureAsset"]:
		var asset: Resource = ClassDB.instantiate(class_name_text)
		if not asset.has_method("set_asset_name") or not asset.has_method("get_asset_name"):
			fail("missing collision-free asset methods: " + class_name_text)
			return
		asset.resource_name = "ResourceMetadataName"
		asset.call("set_asset_name", "AuthoredAssetName")
		if asset.call("get_asset_name") != "AuthoredAssetName" or asset.get("name") != "AuthoredAssetName" or asset.resource_name != "ResourceMetadataName":
			fail("asset name and inherited resource metadata are not independent")
			return
		asset.set("name", "SerializedAssetName")
		if asset.call("get_asset_name") != "SerializedAssetName":
			fail("serialized name property no longer binds custom setter/getter")
			return
		var save_path := fixture.path_join(class_name_text + ".tres")
		if ResourceSaver.save(asset, save_path) != OK:
			fail("could not save addon asset")
			return
		var restored := ResourceLoader.load(save_path, "", ResourceLoader.CACHE_MODE_IGNORE)
		if restored == null or restored.call("get_asset_name") != "SerializedAssetName" or restored.resource_name != "ResourceMetadataName":
			fail("saved asset-name property or Resource metadata did not round-trip")
			return
	var region: Resource = ClassDB.instantiate("Terrain3DRegion")
	region.call("set_region_size", 64)
	region.call("set_location", Vector2i(3, 4))
	var height := Image.create(64, 64, false, Image.FORMAT_RF)
	height.fill(Color(17.25, 0, 0, 1))
	region.call("set_height_map", height)
	region.call("sanitize_maps")
	for deep: bool in [false, true]:
		if not region.has_method("duplicate_region"):
			fail("missing collision-free region duplication")
			return
		var copy: Resource = region.call("duplicate_region", deep)
		if copy == null or copy.get_class() != "Terrain3DRegion" or copy.call("get_location") != Vector2i(3, 4) or copy.call("get_region_size") != 64:
			fail("region copy lost runtime location or size")
			return
		var copy_height: Image = copy.call("get_height_map")
		if not is_equal_approx(copy_height.get_pixel(0, 0).r, 17.25) or (copy_height == height) == deep:
			fail("region copy changed deep/shallow height ownership")
			return
	print("TERRAIN3D_GODOT48_COMPATIBILITY=PASS")
	quit(0)

func fail(message: String) -> void:
	push_error("TERRAIN3D_GODOT48_COMPATIBILITY=FAIL " + message)
	quit(1)
