"""Run inside Blender: blender --background --python tools/author_meadow_grass.py.

Creates the editable source and exports mesh data read back from Blender meshes.
-- --export-existing reexports edits from the loaded .blend without regenerating.
"""
import bpy
import json
import math
import sys
from pathlib import Path
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "resources" / "grass" / "meadow"
VARIANTS = ("compact", "spreading", "leaning")
TIERS = ("near", "near_reduced", "intermediate", "far")


def to_blender(v):
    return Vector((v[0], -v[2], v[1]))


def to_godot(v):
    return (v[0], v[2], -v[1])


def blade_specs(variant):
    # Uneven phyllotaxis and radial spacing avoid a regular four-source stamp.
    heights = (1.0, .79, .92, .68, .87, .75, .96, .82)
    radii = (1.32, .48, 1.91, 1.06, 2.12, .84, 1.63, 1.28)
    result = []
    for i in range(8):
        angle = i * 2.39996323 + variant * .53
        radius = radii[(i + variant * 2) % 8] * (.84 if variant == 0 else 1.0)
        root = Vector((math.cos(angle) * radius, 0, math.sin(angle) * radius))
        side_angle = angle + (.35, -.48, .83, -.22)[i % 4]
        side = Vector((math.cos(side_angle), 0, math.sin(side_angle)))
        outward = Vector((-side.z, 0, side.x))
        lean = (.025 + .012 * (i % 4)) * (-1 if i % 3 == 0 else 1)
        if variant == 2:
            lean += .035
        result.append((root, side, outward, heights[i], .25 + .025 * (i % 4), lean))
    return result


def make_mesh(variant_name, variant, tier):
    selection = {"near": range(8), "near_reduced": range(8), "intermediate": (0, 1, 2, 4, 6, 7), "far": (0, 2, 4, 6)}[tier]
    segments = 2 if tier == "near" else 1
    width_scale = {"near": 1.0, "near_reduced": 1.0, "intermediate": 1.24, "far": 1.64}[tier]
    vertices, faces, uvs, metadata, normals = [], [], [], [], []
    for blade in selection:
        root, side, outward, height, half_width, lean = blade_specs(variant)[blade]
        start = len(vertices)
        for section in range(segments + 1):
            t = section / segments
            # Broad base, narrow terminal tip; restrained rest arc, shader owns bending.
            width = half_width * width_scale * (1 - .96 * t ** 1.5)
            center = root + Vector((0, height * t, 0)) + outward * (lean * t * t)
            center += side * (.018 * math.sin(t * math.pi))
            tangent = Vector((0, height, 0)) + outward * (2 * lean * t)
            tangent += side * (.018 * math.pi * math.cos(t * math.pi))
            face_normal = side.cross(tangent).normalized()
            for edge in (-1, 1):
                vertices.append(to_blender(center + side * (edge * width)))
                normals.append(to_blender((face_normal + side * (edge * .28)).normalized()))
                uvs.append(((edge + 1) / 2, t))
                metadata.append((root.x, root.z, side.x, side.z))
        for section in range(segments):
            a = start + section * 2
            # Blender counterclockwise; export reverses for Godot clockwise.
            faces.extend(((a, a + 3, a + 2), (a, a + 1, a + 3)))
    mesh = bpy.data.meshes.new(f"{variant_name}_{tier}")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    uv_layer = mesh.uv_layers.new(name="BladeUV")
    for loop in mesh.loops:
        uv_layer.data[loop.index].uv = uvs[loop.vertex_index]
    for component, name in enumerate(("root_x", "root_z", "rest_side_x", "rest_side_z")):
        attr = mesh.attributes.new(name, "FLOAT", "POINT")
        for i, item in enumerate(metadata):
            attr.data[i].value = item[component]
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    mesh.normals_split_custom_set_from_vertices(normals)
    obj = bpy.data.objects.new(mesh.name, mesh)
    bpy.context.collection.objects.link(obj)
    obj["tier"] = tier
    obj["variant"] = variant_name
    obj["bladeCount"] = len(selection)
    obj["export_contract"] = "Godot Y-up; UV.y blade fraction; CUSTOM0 root xz / rest side xz"
    return obj


def export_mesh(obj):
    mesh = obj.data
    mesh.calc_loop_triangles()
    data = dict(schemaVersion=1, name=obj.name, tier=obj["tier"], variant=obj["variant"],
                bladeCount=obj["bladeCount"], positions=[], normals=[], uv=[], custom0=[], indices=[])
    attrs = [mesh.attributes[name] for name in ("root_x", "root_z", "rest_side_x", "rest_side_z")]
    tuples = {}
    # Export full corner attribute tuples; edits to split normals/UVs stay lossless.
    for triangle in mesh.loop_triangles:
        for loop_index in reversed(triangle.loops):
            vertex_index = mesh.loops[loop_index].vertex_index
            p = to_godot(mesh.vertices[vertex_index].co)
            n = to_godot(mesh.corner_normals[loop_index].vector)
            uv = tuple(mesh.uv_layers.active.data[loop_index].uv)
            custom = tuple(attr.data[vertex_index].value for attr in attrs)
            key = tuple(round(float(v), 7) for values in (p, n, uv, custom) for v in values)
            if key not in tuples:
                tuples[key] = len(tuples)
                for name, values in (("positions", p), ("normals", n), ("uv", uv), ("custom0", custom)):
                    data[name].extend(round(float(v), 7) for v in values)
            data["indices"].append(tuples[key])
    validate(data)
    path = OUT / f"{obj['variant']}_{obj['tier']}.json"
    path.write_text(json.dumps(data, separators=(",", ":")) + "\n", encoding="utf-8")
    return dict(name=data["name"], vertices=len(data["positions"]) // 3,
                triangles=len(data["indices"]) // 3, bladeCount=data["bladeCount"],
                boundsMin=[min(data["positions"][i::3]) for i in range(3)],
                boundsMax=[max(data["positions"][i::3]) for i in range(3)])


def validate(data):
    count = len(data["positions"]) // 3
    assert count > 0
    for key, stride in (("positions", 3), ("normals", 3), ("uv", 2), ("custom0", 4)):
        assert len(data[key]) == count * stride, key
        assert all(math.isfinite(v) for v in data[key]), key
    assert min(data["positions"][1::3]) == 0
    assert abs(max(data["positions"][1::3]) - 1) < 1e-6
    assert len(data["indices"]) % 3 == 0
    assert len(data["indices"]) // 3 <= (32 if data["tier"] == "near" else 16)
    assert all(0 <= index < count for index in data["indices"])
    for i in range(count):
        root_x, root_z, side_x, side_z = data["custom0"][i * 4:i * 4 + 4]
        assert math.hypot(root_x, root_z) <= 2.5
        assert abs(math.hypot(side_x, side_z) - 1) < 1e-5
        assert 0 <= data["uv"][i * 2 + 1] <= 1
        assert abs(sum(v*v for v in data["normals"][i*3:i*3+3]) - 1) < 1e-5
    for offset in range(0, len(data["indices"]), 3):
        ids = data["indices"][offset:offset+3]
        p = [Vector(data["positions"][i*3:i*3+3]) for i in ids]
        normal = sum((Vector(data["normals"][i*3:i*3+3]) for i in ids), Vector())
        assert (p[1]-p[0]).cross(p[2]-p[0]).length > 1e-7
        assert (p[1]-p[0]).cross(p[2]-p[0]).dot(normal) < 0, "Godot winding"


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    if "--export-existing" not in sys.argv:
        bpy.ops.object.select_all(action="SELECT")
        bpy.ops.object.delete(use_global=False)
        for variant, name in enumerate(VARIANTS):
            for tier in TIERS:
                make_mesh(name, variant, tier)
        # Hide lower tiers in viewport; all objects keep identity transforms for export.
        for obj in bpy.data.objects:
            obj.hide_set(obj["tier"] != "near" or obj["variant"] != "compact")
        bpy.context.scene["asset_provenance"] = "Original Ringworld meadow grass; tools/author_meadow_grass.py"
        bpy.context.scene["normalized_height"] = "Runtime transforms set 10-20 cm; source max height is 1"
    report = [export_mesh(obj) for obj in bpy.data.objects if obj.type == "MESH" and "tier" in obj]
    assert len(report) == 12
    bpy.context.preferences.filepaths.save_version = 0
    (OUT / "source").mkdir(exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(OUT / "source" / "meadow_grass.blend"))
    (OUT / "validation.json").write_text(json.dumps(dict(blenderVersion=bpy.app.version_string,
        generator="tools/author_meadow_grass.py", meshes=report), indent=2) + "\n", encoding="utf-8")
    print("MEADOW_ASSET_VALIDATION " + json.dumps(report))


if __name__ == "__main__":
    main()
