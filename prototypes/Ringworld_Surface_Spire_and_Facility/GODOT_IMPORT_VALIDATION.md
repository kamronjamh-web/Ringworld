# Godot import and geometry verification

Three exports were clean-imported and instantiated in **Godot 4.6.3**. No import warnings or errors were reported.

| Export | Mesh instances | Material surfaces | Triangles | Godot AABB position (m) | Godot AABB size (m) |
|---|---:|---:|---:|---|---|
| Surface_Beam_Spire_Godot.glb | 60 | 108 | 5,712 | (−308, 0, −308) | (616, 1800, 616) |
| Surface_Base_Facility_Godot.glb | 877 | 1,112 | 31,868 | (−196.9833, −1, −196.9833) | (393.9666, 31, 412.1544) |
| Surface_Spire_and_Facility_Godot.glb | 937 | 1,220 | 37,580 | (−308, −1, −308) | (616, 1801, 616) |

The combined spire is still 1,800 m above the surface datum. Its AABB includes a 1 m sub-grade facility slab. Main rooms and corridor are at Y0 in Godot; gallery floor is Y16; facility roof maximum is Y30. The shared root is the surface ground center, with scale 1.

The many separate surfaces preserve editable components and materials. **Production batching/merging, instancing, LODs and visibility management are still needed.** Successful import and a modest triangle count do not establish runtime performance.

## Independent geometric audit

- Exactly eight primary arms; feet at radius 250 m and 45° intervals.
- All 37,580 combined exported triangles checked against the continuous vertical beam cylinder.
- Minimum projected surface radius: 75.039949 m.
- Faces/edges inside reserved radius 75 m: none.
- All 1,385 samples on 10 route segments/loops passed floor, 1.85 m standing-headroom, and 0.35 m body-radius clearance tests.
- Route samples cover the entrance, whole ground ring, all three rooms, lower landing, sloped ramp, upper landing and whole raised gallery.
- Original standalone spire source and GLB were not overwritten by the facility builder. Their SHA256 hashes are recorded in the facility manifest.

The clearance audit evaluates actual triangle faces, rather than assuming that clear vertices imply clear faces. Route audit is geometric only. It does not simulate a player controller or test collision, navigation, accessibility, animation, game lighting, beam VFX, streaming or target-device performance.

Presentation terrain, shaft liner, people, rocks, beam preview, cameras and lights are absent from every GLB.
