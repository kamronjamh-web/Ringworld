extends SceneTree

const ContractTest := preload("res://scripts/Tools/ProxySeamGeometryContractTest.cs")

# CPU-only regression launcher for proxy sector precision, parent-triangle
# interpolation, clipped-water morph selection, and skirt metadata contracts.
func _init() -> void:
	var passed: bool = ContractTest.new().RunContract()
	if passed: print("PROXY_SEAM_GEOMETRY_CONTRACT=PASS")
	else: push_error("PROXY_SEAM_GEOMETRY_CONTRACT=FAIL")
	quit(0 if passed else 1)
