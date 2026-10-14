# Emitter POI Streaming, Far Stand-ins, Tooling and Milestone 1 Plan

Status: design spec, read-only investigation, 2026-10-14. No project files edited.
Companion to `docs/emitter-site-terrain-spec.md` (called "site spec" below; §/D numbers refer to it).
Milestone 1 (M1) backend: `surface_runtime_generation_v1` (Terrain3D heightmap). Reactors are not shown. The bore gets a liner and a plug (D8).

---

## 0. Findings that shape the design (evidence)

| # | Finding | Evidence |
|---|---|---|
| E1 | **Rebase is a canonical anchor, not a root shift**, in the gameplay scene. `FloatingOrigin` is disabled in `VoxelRingIntegrationTest.tscn:109-113`. The explorer recentres a 32,768 m collision sector and calls `SetFloatingOriginCanonicalAnchor` (`RingVoxelExplorerController.cs:16, 2837-2858`). The grid raises `CanonicalFloatingOriginAnchorChanged(previous, current, anchor)` **synchronously** (`VoxelChunkGridManager.cs:292-334`). The root-shift path (`floating_origin.gd:13-52`) is only used by `Main.tscn`. | cited |
| E2 | `RingLodGeometrySnapshot.CanonicalToWorld` subtracts the anchor in double before narrowing to float (`RingLodGeometrySnapshot.cs:25-69`). World positions are therefore anchor-relative: at most about 23 km for anything near the player. Ring axis = grid-local **Y = across**. Radial plane = grid XZ. The player's "up" is the inward radial, **not world +Y** (`RingVoxelExplorerController.SurfaceTerrain.cs:41-43`: `across = Vector3.Up`). | cited |
| E3 | **Handedness:** (tangent, inward) together with +across forms a left-handed frame. Terrain3D therefore uses a right-handed basis `(tangent, inward, gridBasis*Down)` with `AcrossDirection = -1` (`RingTerrain3DGameplay.cs:373-396`). Any rigid asset must use local **Z = −across**. Site spec §1.3 step 6 states `θ = 67.5° − β` for `AcrossDirection=+1`. In the game world the correct value is **`θ = 67.5° + β`**. | derivation §A.2 |
| E4 | `EntryApproach` sits at asset (88.78, 0, 214.34), which is atan2 = 67.5°. This confirms the asset entry bearing and gives an exact orientation test. `FarProxy` has `visibility_range_begin 11500` with a 1000 m margin, fade Self, and **no end**. Spire detail ends at 12500 (1000 margin), facility/roof at 6000 (500). `PlaceholderBeam` is hidden. | `BeamEmitterPOI.tscn:161-165, 921-926, 960-977` |
| E5 | Distant landmark precedent: `RingScaleAnchorLayer` draws 1.6 km towers to 180 km with three MultiMeshes, worker builds, `CanonicalToWorld` per instance, a lit shader with ring atmosphere, no shadows and ignore-culling. | `RingScaleAnchorLayer.cs:17-120, 199-215, 270-302`; `materials/ring_scale_anchor.gdshader` |
| E6 | Rebase precedents: proxy sectors reposition sector-local meshes in the handler (`RingTerrainProxySector.cs:1142-1172`). Trees translate the layer by a pure delta (`RingBiomeDecorationLayer.GpuRebase.cs:12-48`). Terrain3D rebuilds its world frame from the new snapshot (`RingTerrain3DGameplay.cs:363-371`). | cited |
| E7 | **Distant rendering split (Certified):** the main camera's far plane is `min(1.2 Mm, 2.5R)` = 1.2 Mm (`world_size_launcher.gd:48-50, 1510`). Main-camera global LOD fades 500→850 km (`:1607-1610`). `RingScaledFarViewport` is a separate World3D at 1:1000 (`:1714` asserts 0.001), composited as a **canvas background with no depth interplay** (`RingScaledFarViewport.cs:16-21, 2236-2237`). Its terrain is cut out near the camera: 625 km at ground level, down to a 16 km minimum above 20 km altitude (`:144-165, 2615-2641`). | cited |
| E8 | Terrain3D owns only a disk of 84-128 m around the player (`GameplaySurfaceTerrainDefaults.cs:19-20`; `RingTerrain3DGameplay.cs:23-28`). Proxy terrain is drawn **as an underlay everywhere**, with near discard off (`ring_terrain_proxy.gdshader:5-10, 121-126`). **A Terrain3D control-map hole alone would therefore show the proxy underlay as a lid at ground level.** | cited |
| E9 | Gameplay height queries ignore holes. `SampleCollisionHeight` returns the field height (`RingTerrain3DGameplay.Streaming.cs:248-258`). Window activation lifts the capsule to that height (`:51-52`, `Startup.cs:252`). The Terrain3D underground probe treats everything below the field as solid (`Terrain3DUndergroundProbeSource.cs:20-31`). As a result, a player on a −200 m plug would be **lifted back to P** on the next window swap, and the probe sees rays starting in rock (`UndergroundEnclosureProbe.cs:116-124`): openness 0, so it **enters underground mode in an open pit**. | cited |
| E10 | Threaded load helper `RingThreadedResourceClaims` (`scripts/Loading/RingThreadedResourceDrain.cs:8-58`). Loading gate: structure owners expose `StartupComplete`, an optional `GetLoadingStatus`, and `UseStagedStartup` (`RingWorldLoadingBridge.cs:62-75, 132-157`). | cited |
| E11 | Underground participants: flags enum plus adapters, "hold, never free" (`UndergroundParticipant.cs:7-58`). FarTerrain-group adapters live in `UndergroundParticipants.cs:176-222`. `ScaleAnchors` is intentionally not a participant (`underground-mode-spec.md:134`). | cited |
| E12 | Settings pattern: a `[GlobalClass] Resource` plus a `*Defaults` class with Load/Save(atomic temp+move)/Clamp/Reset and a path override (`GameplaySurfaceTerrainDefaults.cs:8-203`). A drawer section debounces saves and refreshes a live label (`RingLiveTuningDrawer.Underground.cs:24-58`). Isolated test install (`tools/atmosphere_visual_test.gd:117-121`). Settings smoke (`tools/SurfaceTerrainSettingsSmoke.cs`). | cited |
| E13 | Teleport precedent: `TryTeleportToNearestDesignedCave` ends with a reusable "move, noclip, recentre, re-focus" tail (`RingVoxelExplorerController.cs:578-593`). Windowed capture precedent: `tools/atmosphere_visual_test.gd` (game creation with the runtime backend, readiness wait, matched poses, PNG + JSON report). | cited |
| E14 | Physics: the explorer is a `CharacterBody3D` using `MoveAndSlide` in Terrain3D mode (`RingVoxelExplorerController.SurfaceTerrain.cs:24-62`). POI `StaticBody3D` collision works as-is. Physics interpolation is off (no setting in `project.godot`). | cited |
| E15 | Ring scale: C = 31,415,936 m, R ≈ 5,000,000 m (`CertifiedRingScale.cs:27-30`), width 318 km. Sites are 314 km apart along and 53 km across. The nearest site is always ≤ ~160 km away. Typically ~30 sites lie within 850 km. | site spec F14 |

---

## A. EmitterPoiStreamer

### A.1 Components and files (new code under `scripts/poi/Emitters/`, namespace `RingworldTechDemo.Poi`)

Note: on Windows, `scripts/Poi` and `scripts/poi` are the same folder. Use the existing lowercase `scripts/poi`.

| File | Kind | Responsibility |
|---|---|---|
| `EmitterSitePlacement.cs` | pure | `readonly record struct EmitterSitePlacement(int Index, double AroundMeters, double AcrossMeters, int PadHeightMeters, double BearingRadians, EmitterSiteKind Kind, EmitterSiteFlags Flags)`. `interface IEmitterSitePlacementSource { int Count; ulong Identity; double CircumferenceMeters; EmitterSitePlacement GetPlacement(int i); int FindNearestIndex(double around, double across); }`. `EmitterSiteRegistryPlacementSource` adapts the P1 `EmitterSiteRegistry` (this is the site spec's P4 `GetPlacement`). |
| `EmitterSiteFrameMath.cs` | pure (Godot math types only) | Site transform, yaw, asset-local→canonical, wrapped distances (§A.2). |
| `EmitterPoiStreamerCore.cs` | pure | Per-site state machine, hysteresis, pool assignment, collision gating (§A.4). No Godot nodes, so it is unit-testable. |
| `EmitterPoiStreamer.cs` | `Node3D` "EmitterSites" | Binds grid/explorer/camera, threaded load, pool slots, rebase, presented-flag handoff to stand-ins, aperture publishing, loading-bridge readiness, diagnostics. |
| `EmitterStandInLayer.cs` | `MultiMeshInstance3D` child "StandIns" | Part B. |
| `RingSurfaceApertures.cs` | static registry | Pushes aperture uniforms to registered terrain/ocean materials (§A.7). |
| `EmitterSiteDebugRings.cs` | `MeshInstance3D` child "DebugRings" | Part C. |

Scene: add a `EmitterSites` Node3D (script `EmitterPoiStreamer`) to `scenes/VoxelRingIntegrationTest.tscn` at the scene root, next to `ScaleAnchors` (`:640`). It disables itself (`Enabled=false`, `StartupComplete=true`) when the sampler exposes no registry (v1-v4 worlds, gen-16 in M1, legacy). Registry source: `grid.TerrainSampler.EmitterSites` (to be added in P3, §M1-2). It is null unless the world recipe is `ringworld_surface_sites_v5`.

### A.2 Canonical → curved local frame → world (reusing existing conventions)

Inputs: `RingLodGeometrySnapshot g = grid.CaptureLodGeometrySnapshot()` and placement `(A, X, P, β)`.

```
φ          = wrap(A, C) / g.RingRadiusMeters                      // same as CanonicalToAbsoluteLocal
B          = g.GridGlobalTransform.Basis (orthonormalized)
tangent    = (B * (−sin φ, 0,  cos φ)).Normalized()                // +around
inward     = (B * (−cos φ, 0, −sin φ)).Normalized()                // local up
zAxis      = (B * Vector3.Down).Normalized()                        // = −across (right-handed, E3)
frame      = new Basis(tangent, inward, zAxis)                      // identical to RingTerrain3DGameplay.BuildWorldFrame:383-396
θ          = DegToRad(67.5) + β                                     // AcrossDirection = −1 world frame
yaw        = new Basis(Vector3.Up, θ)
origin     = g.CanonicalToWorld(A, P + PadClearance, X)             // double subtraction, anchor-relative
siteWorld  = new Transform3D(frame * yaw, origin)
siteLocal  = layer.GlobalTransform.AffineInverse() * siteWorld      // RingScaleAnchorLayer.cs:225 convention
```

- **Yaw derivation:** `Basis(Up, θ)` maps the XZ angle α to α − θ. The asset entry is at α = 67.5°. A bearing β in (around, across) becomes the frame-local angle −β, because Z = −across. Therefore θ = 67.5° + β. **Do not consume a registry "yaw" computed for the +1 frame.** Ask P1 to store β explicitly (see M1 dependencies).
- **PadClearance:** default 0.05 m, settings range 0..0.5. The test scene needed −0.05 m ground to avoid floor z-fighting (`assets/poi/README.md:82`). Curvature sagitta at R350 is r²/2R = 1.2 cm (the ring is concave, so terrain rises toward the pad edge), which leaves ≥ 3.8 cm clearance. The facility slab bottom (−1 m) stays buried.
- **Precision:** a loaded POI is ≤ 16 km from the camera and the camera is ≤ ~23 km from the anchor, so the origin magnitude is < 40 km and the float ULP is ≈ 4 mm. That matches the Terrain3D window and all near systems. Stand-ins at 850 km have ULP 6 cm, which is irrelevant at 1-2 px.
- **Teleport targets (asset-local → canonical):** `q = yaw * assetLocal`. Then `around = A + q.X`, `across = X − q.Z`, `vertical = P + PadClearance + q.Y`. This uses the flat-tangent approximation; the error at 230 m is 5 mm and over 20 km it is ≈ 40 m vertical, so use it only for near markers.
- **Agreement with Terrain3D:** pad points mapped by the snapshot and by `TerrainWorldFrame * frame.MapPosition(a, x, P)` must agree within 1 cm (test D.1-c). `RingSurfaceFrameTest.VerifyLodGeometryAgreement` (`tools/RingSurfaceFrameTest.cs:16`) is the existing precedent.

### A.3 Pool, threaded loading, startup

- **Concurrency bound:** sites are ≥ 45 km apart and the unload radius is 16 km, so at most **one** site can be within range (16+16 < 45). Pool size defaults to **2**: one active plus one spare for teleport or overlap. The core asserts `active ≤ poolSize`. If the core ever asks for more, it keeps the nearest.
- **Slot layout:** `EmitterSiteSlot{n}` (Node3D) contains a `BeamEmitterPOI` instance and, on the heightmap backend only, a `BoreLinerPlug` instance (§A.6). Slots stay **in the tree permanently**. Idle means `Visible=false` and `Collision.ProcessMode=Disabled`. Assigning a slot only sets a transform and visibility, so no instantiate or `add_child` happens at runtime and there are no hitches after startup. `FarProxy.MaterialOverride` is set to the shared "single" stand-in material (§B.3), which gives a pixel-identical handoff.
- **Load:** in `_Ready`, call `RingThreadedResourceClaims.Request("res://assets/poi/beam_emitter/BeamEmitterPOI.tscn")` plus `BeamEmitter_FarProxy.res` and `BoreLinerPlug.tscn`. Poll in `_Process`. When loaded, instantiate **one slot per frame**. Keep the `PackedScene` referenced for the session (meshes are shared, so the cost is paid once). Dispose the claims in `_ExitTree` (the drain handles in-flight IO).
- **Pipeline warm-up:** optional, behind the loading screen. Place slot 0 visibly 50 m in front of the camera for 2 frames, then hide it. Keep it only if the first-visible frame spike in D.2 exceeds 8 ms. Godot's ubershaders may make it unnecessary; measure before relying on it.
- **Loading gate:** add `EmitterPoiStreamer` to the structure-owner list and readiness switch (`RingWorldLoadingBridge.cs:66-69, 138-146`). Provide `bool StartupComplete`, `bool UseStagedStartup`, and `GetLoadingStatus()` returning `{error}` on a load failure.

### A.4 Load/unload hysteresis and collision (core state machine)

Per frame, compute the camera canonical position as `grid.WorldToCanonicalRingPosition64(camera.GetCameraTransform().Origin)`. This is the eye, not the feet, matching `RingScaledFarViewport.cs:2253`.

Candidates: `FindNearestIndex` plus the 3×3 row/col neighbourhood (≤ 9 sites, O(1)). Distance metric:
`d = sqrt(du² + dw² + dv²)` with `du = signedWrap(A − camA)`, `dw = X − camX`, `dv = (P + 900) − camV`. The 900 m term is the spire mid-height, which approximates the AABB-centre metric that visibility ranges use.

| Transition | Rule (defaults; settings in §C) |
|---|---|
| Unloaded → Active | `d < LoadRadius` (14,000 m) **and** pool ready. Assign a slot, set the transform, set `Visible=true`, and set the stand-in presented flag (§B.4) in the same frame. |
| Active → Unloaded | `d > LoadRadius + Hysteresis` (16,000 m) **and** active ≥ `MinDwell` (1.0 s). Hide the slot, clear the presented flag, release the slot. |
| Collision on/off | Ground distance `dg = sqrt(du² + dw² + (P − camV)²)`. On when `< CollisionRadius` (1,500 m), off when `> CollisionRadius + 500`. Toggle via `Collision.ProcessMode`. Shapes are shared resources, so toggling does not rebuild the BVH. |

The load radius must be ≥ 12,500 + 1,000 (spire detail end + margin). The setter clamps it to that.

**Fast travel:** no speed gate is needed because assignment is cheap. If a slot is not ready (startup only), the stand-in keeps drawing the site at any distance, so it degrades gracefully to the 352-tri proxy.

### A.5 Rebase and interaction with Terrain3D, the 768 m window, and the 84 m handoff

- Subscribe to `CanonicalFloatingOriginAnchorChanged`. In the handler, which runs synchronously inside the explorer's physics tick before `ApplyWorldTransform`:
  1. Recompute every active slot's `GlobalTransform` from the **current** snapshot. Body and player then move in the same tick.
  2. `StandIns.Rebuild(current)`: 600 transforms, synchronous (§B.5).
  3. Re-publish apertures (§A.7).

  Also detect root shifts (`Main.tscn` path) by a `grid.GlobalPosition` change, as `RingScaleAnchorLayer.cs:129` does. Never translate the POI by a delta: a delta accumulates cancelling large transforms (E6 trees caveat).
- **The Terrain3D window is independent.** POIs never read the Terrain3D frame. Both derive from the same snapshot, so its 2 m frame re-anchoring and staged window swaps (`RingTerrain3DGameplay.Streaming.cs:35-53`) need no coupling. Test D.1-c guards the agreement.
- **The 768 m window and the 84-128 m disk do not cover the pad** (700 m across plus apron). Beyond the disk, proxy and global terrain draw the pad from the same sampler, so after P3 it is flat at P. Risk: a proxy underlay offset or lattice interpolation near R350-410. Check with a capture: from 300 m and from 2 km, floors must neither float visibly nor z-fight.
- **Hole:** because of E8, the control-map hole (P4/M1-7) is insufficient on its own. Proxy and global terrain (and ocean, on islands) must also discard inside the bore. See §A.7.

### A.6 Bore liner and plug (heightmap backend, D8)

New tool `tools/poi/build_bore_liner.gd`. It follows the conventions of `build_poi_scenes.gd`, overwrites its outputs, and writes `assets/poi/beam_emitter/BoreLinerPlug.tscn` plus `merged/meshes/BoreLiner*.res` and `collision/BoreLiner*.res`. Parameters live in the tool header.

| Part | Geometry | Notes |
|---|---|---|
| Liner | Open cylinder, R = 76 m (`bore_top_radius_m`), y ∈ [−200, −0.5], 64 radial × 8 vertical segments, normals inward, `cull_disabled` | The top at −0.5 sits inside the facility slab (bottom −1), so there is no crack under the throat (throat R75-94, z0..14; site spec F16). It also does not rise above the rim floor (`GroundFloors` reaches R75), so it never blocks walking. |
| Plug | Disk R76 at y = −200, with a thin emissive teal ring at R60-70 (reuse `Reactor_Teal_Inlay.tres`) | Material reuse: `Obsidian_Alloy.tres` for the liner. |
| Collision | `ConcavePolygonShape3D` from the liner faces (backface collision on) plus a convex plug slab | The liner wall stops players from leaving the bore into the void between R76 and the jagged terrain hole edge (78.6-81.4 m). |
| Guard (U3) | Optional invisible collision collar R74.5-75.5, 1.2 m tall at the rim | Default per the user decision. |

Shadows are on, so the pit self-shadows. The liner and plug are not used on the gen-16 SDF backend, where the carve supplies the bore. Attach them only when `sampler.UsesRuntimeSurfaceGeneration`.

### A.7 Surface apertures (required for the hole to be visible; E8, E9)

- **Record:** `SurfaceAperture { RingCanonicalPosition Center (A, P, X); float Radius (hole 80); float FloorDepth (200); int SiteIndex }`. This is the site spec §4.6 aperture record, provided by `EmitterSiteRegistry.GetSurfaceApertures(window)` (M1-7).
- **Render:** new include `materials/ring_surface_aperture.gdshaderinc`, with `uniform vec4 surface_aperture_center_radius[2]` and `uniform vec4 surface_aperture_up[2]` in world space. `ring_surface_aperture_discard(world_position)` discards when the perpendicular distance to the site axis is `< radius` **and** `|height along axis| < 500 m` (the axis line also hits the antipodal ring). Include it in `ring_terrain_proxy.gdshader` (fragment, before line 121), `ring_terrain_global.gdshader`, and, after verifying they draw under island pads, `ring_ocean_proxy.gdshader` and `ring_ocean_global.gdshader`. Materials register through `RingSurfaceApertures.Register(material)` at creation, mirroring the `RingCloudShadowProjector.Register` usage at `RingScaleAnchorLayer.cs:178`.
- **Publishing:** only for **active** slots, where the liner exists. Otherwise the discard would show the void. Publish on assign, release, and rebase.
- **Gameplay queries (M1-8):**
  1. `RingTerrain3DGameplay.SampleCollisionHeight` returns `P − FloorDepth` (the plug top) inside an aperture. That fixes the lift at `Streaming.cs:51-52` and `Startup.cs:252`, and the spawn snap at `RingVoxelExplorerController.cs:2458-2460`, because the lift is then never above the plug.
  2. `Terrain3DUndergroundProbeSource.TryGetSurfaceHeight/TryGetDensity` report air inside the aperture above the plug and use the plug top as the surface. Depth is then ≤ 0 in the pit, so it never latches underground.
- **Far:** when no POI is loaded (d > 16 km), the bore reads as flat ground. A dark "bore disk" in the stand-in mesh is optional P9 polish.

### A.8 Underground mode

- **POIs: no participant in M1.** They are near content, and the future reactor will be reached through them. On the M1 Terrain3D backend underground mode cannot latch naturally (no caves; the aperture fix in §A.7). It can only be forced by the debug override.
- **Stand-ins:** the new `EmitterStandInParticipant` (group **FarTerrain**) is added to `UndergroundParticipantFactory.CreateDefaults` (`UndergroundParticipants.cs:11-19`). Suspend freezes per-frame updates; rebase rebuilds still apply. Hide sets `Visible=false`. `RetainedInstanceIds` returns the layer and its MultiMesh, as the round-trip smoke requires (`underground-mode-spec.md:196`).

### A.9 Save/load

There is no per-site state. Placement is a pure function of the frozen recipe (site spec §5) and the streamer state is derived. Only the preferences in §C persist (user://). `RingWorldSaveManager` needs no change. Debug teleports are not saved, apart from the player position that the normal save already stores.

---

## B. Far stand-ins

### B.1 Tiers

| Tier | Range | Owner | Draws |
|---|---|---|---|
| T0 detail | 0-12.5 km (facility 6 km) | POI slot, engine visibility ranges | 126 surfaces, shadows on |
| T1 near proxy | 11.5 km → slot unload (≤ 16 km) | POI slot `FarProxy` using the **shared stand-in material** | 352 tris, engine crossfade with T0 (bands already matched in the asset) |
| T2 far proxy | any distance with no active slot → `StandInMaxDistance` (default 700 km, fade over the last 100 km, clamped ≤ `GlobalTerrainLod3.FarFadeEndMeters` = 850 km) | `EmitterStandInLayer` MultiMesh (main camera) | Same `FarProxy` mesh and material, per-instance flags |
| T3 sub-pixel | Coverage fade in T2 when the projected height is < `MinPixels` (1.5 px) | T2 shader dither | Nothing beyond |
| T4 beacons / beams (hook) | > 850 km, the whole arch | Future layer in `RingScaledFarViewport` world (§B.7) | Not in M1 (U1) |

**What draws the spire between detail and far stand-ins:** T1, the POI's own `FarProxy` from 11.5 km, while the slot is active. The T2 instance for that site is hidden in the same frame (presented flag). Mesh, material and transform are identical (same `EmitterSiteFrameMath` function, layer at identity), so the T1↔T2 swap at 14-16 km is invisible, and the T0↔T1 crossfade is the engine's.

**Pixel check (1080p, 80° FOV, ≈ 643 px/rad):** 1800 m spire at 100 km ≈ 11.6 px. At 600 km ≈ 1.9 px. At 850 km ≈ 1.4 px. At the far side (10,000 km) ≈ 0.1 px. Arm widths drop below 1 px past about 100-150 km, so expect thin-line shimmer, softened by TAA (U5).

### B.2 Where it renders: main camera

Main camera, not `RingScaledFarViewport`, for M1:
1. The scaled viewport is a background canvas with no depth against the main scene (E7). Spires inside its cutout radius would bleed through main-camera terrain.
2. The main camera holds depth-correct terrain to 850 km (global LOD), and the spire is < 1.5 px beyond that.
3. The far plane is 1.2 Mm (E7), with no clipping inside 850 km.

The far tier ends at `min(StandInMaxDistance, global FarFadeEndMeters, camera.Far)`.

### B.3 Shader `materials/ring_emitter_standin.gdshader`

Derived from `ring_scale_anchor.gdshader` (lit, `cull_back`, `depth_draw_opaque`, includes `ring_atmosphere.gdshaderinc` and `ring_cloud_shadow.gdshaderinc`; day factor from `ring_center` and `sun_direction`).

- `uniform bool use_instance_custom`: true on the MultiMesh material, false on the "single" copy used as the POI `FarProxy` override. Otherwise identical uniforms (create with `Duplicate()`; set both on every update).
- **vertex:** `inst_mid = (MODEL_MATRIX * vec4(0, 900, 0, 1)).xyz`, `d = distance(inst_mid, CAMERA_POSITION_WORLD)`. If `use_instance_custom && INSTANCE_CUSTOM.r > 0.5` (presented by a POI) or `d > max_distance`, set `VERTEX = vec3(0)` (degenerate, rasterises nothing).
  - Varyings: `fade = 1 − smoothstep(max_distance − fade_meters, max_distance, d)`, `coverage = clamp((1800.0 / d) * (PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y * 0.5) / min_pixels, 0, 1)`.
- **fragment:** screen-space interleaved-gradient-noise dither `discard` when `noise > fade * coverage` (opaque pipeline, like the engine fade). Lighting and atmosphere follow the anchor shader. `EMISSION` comes from the atmosphere only (no beacon in M1).
- Configure through `RingAtmosphereModel.ConfigureMaterial(mat, R)` and `RingCloudShadowProjector.Register(mat)`. Set the per-frame `camera_world_position`, `ring_center` (`grid.GetCanonicalRingCenterWorld()`) and `sun_direction` as `RingScaleAnchorLayer.UpdateLightingUniforms` does (`:315-330`).
- **Verify (M1-4):** that the engine visibility-range fade (`fade_mode` Self on `FarProxy`) still applies when `material_override` is a ShaderMaterial. If not, the fallback is to keep the asset's own material on `FarProxy` and accept a shading swap at 14-16 km, or move the T0↔T1 crossfade into this shader.

### B.4 Layer `EmitterStandInLayer`

`MultiMesh{TransformFormat=3D, UseCustomData=true, InstanceCount=registry.Count}`. Mesh: `BeamEmitter_FarProxy.res`. Settings: `CastShadow=Off`, `IgnoreOcclusionCulling`, `RenderingServer.InstanceSetIgnoreCulling(true)` (precedent `RingScaleAnchorLayer.cs:205-215`), and `GIMode` disabled.

API:
- `Rebuild(snapshot)`
- `SetPresented(int site, bool)`: writes `INSTANCE_CUSTOM.r`
- `SetUndergroundHold(bool)` / `SetUndergroundHidden(bool)`
- `Configure(maxDistance, fade, minPixels, enabled)`
- `VisibleEstimate` (CPU count of d < max) for diagnostics

### B.5 Curvature and precision

- Each instance uses its **own** φ basis (§A.2). At 850 km along the ring, a site sits about 72 km "above" the viewer's tangent plane (d²/2R) and leans 9.7°. Spires on the far arch point at the axis; they are out of main-camera range and only reachable through the T4 hook.
- Rebuild all 600 transforms **synchronously** on rebase (600 × `CanonicalToWorld` in double, expected < 0.3 ms; record `LastRebuildMs` and the budget assert in D.1). Do not use the pure-delta trick: after long travel the large cancelling layer offset loses precision for near instances.
- **Float:** anchor-relative world positions ≤ 850 km have a 6 cm ULP. The vertex error is likewise sub-pixel.

### B.6 Lighting, atmosphere, fog, underground

- Stand-ins: ring atmosphere and day factor (night-side and eclipse spires darken correctly, unlike the global DirectionalLight), plus cloud-shadow AO, no shadow casting.
- POI detail meshes (`StandardMaterial3D`) receive only Environment fog (density 4e-6, `day_side_atmosphere.gd:154-158`), while terrain and trees use `sample_ring_atmosphere`. **Risk:** the POI looks too crisp at 6-12 km next to hazed terrain. The D.2 capture pairs (6 km, 11 km) decide it (U4). Fallback: the merge tool emits ShaderMaterials with the atmosphere include.
- Underground: §A.8.

### B.7 Eclipse-beam hooks (no rendering in M1)

- `EmitterSiteFrameMath.GetBeamGeometry(placement) → (originCanonical = (A, P, X), crownCanonical = (A, P + 1743, X), hubCanonical)`. The marker offsets come from `BeamEmitterPOI.tscn` `Markers` (`BeamOrigin`, `BeamCrownFocus`) and are read once from the loaded scene, not hard-coded. A world-space variant uses the snapshot.
- `IEmitterBeamSource` (`Count`, `GetBeam(i)`, `Identity`) is consumed later by a beam layer, which would draw main camera ≤ 850 km plus scaled far viewport beyond. Hook needed on the far viewport: a public `RingScaledFarViewport.AddScaledWorldChild(Node3D)` and making `ScaledPoint(around, vertical, across)` public. In scaled space, positions are absolute and static (no rebase); at 1:1000 the ring is ~5,000 units and the ULP ≈ 0.5 m real.
- **Convergence convention (U2):** a beam along the local radial passes through the ring axis at its own across coordinate (six distinct axis points per row). Converging on one hub point (across = W/2) needs a tilt of up to atan(132.5 km / 5,000 km) = **1.52°**, which is ~46 m off-axis at the crown (1743 m) and may clip the crown aperture.

---

## C. Grave-key tooling (M1 subset of site spec P8)

### C.1 Persisted settings (`scripts/ui/GameplayEmitterSiteSettings.cs` + `GameplayEmitterSiteDefaults.cs`, `user://gameplay_emitter_site_defaults.tres`)

Same structure as `GameplaySurfaceTerrainDefaults` (E12): `Initialize`, `Set*` returning a changed flag, `Save` (atomic), `Reset`, `Clamp*`, path override.

| Field | Default | Range |
|---|---|---|
| `StandInsEnabled` | true | bool |
| `StandInMaxDistanceKm` | 700 | 50..850 (also clamped at runtime to the global LOD fade end) |
| `StandInFadeKm` | 100 | 10..300 |
| `StandInMinPixels` | 1.5 | 0.5..4 |
| `PoiLoadRadiusMeters` | 14,000 | 13,500..30,000 |
| `PoiHysteresisMeters` | 2,000 | 500..6,000 |
| `PoiCollisionRadiusMeters` | 1,500 | 500..5,000 |
| `PadClearanceMeters` | 0.05 | 0..0.5 |
| `ShowSiteRings` | false | bool |
| `BoreGuardCollar` | per U3 | bool |

World-shape values are not here; they are frozen in the recipe (D12).

### C.2 Drawer section

New file `scripts/ui/RingLiveTuningDrawer.EmitterSites.cs`, built inside the Surface terrain gameplay tab (`RingLiveTuningDrawer.SurfaceTerrain.cs:116`, invoked from `RingLiveTuningDrawer.cs:357`). Hide or disable it when `EmitterSites` is disabled.

- **Live status** (0.25 s): nearest site `#i` (row, col), kind (Land/Coast/Island), flags, distance and bearing, P, β, R_inf, slot state (unloaded/active, collision on/off), pool size/used, T2 visible estimate, last rebase ms, registry identity. Source: `EmitterPoiStreamer.DescribeStatus()`.
- **Toggles and sliders:** the fields in C.1, with debounced save and retry (pattern at `RingLiveTuningDrawer.Underground.cs:35-51`).
- **Actions** (not saved):
  - "Teleport to nearest site"
  - SpinBox `0..Count−1` + "Teleport to site N"
  - Prev/Next
  - Viewpoint OptionButton: Entry (ground), Gallery, 20 km, High altitude (150 km)
  - "Teleport to nearest island" and "Teleport to nearest mountain" (largest registry `maxCut`). These serve the capture route.
- **Explorer API:** extract the cave teleport tail (`RingVoxelExplorerController.cs:578-593`) into `public bool TryTeleportToCanonical(in RingCanonicalPosition target, float yawDegrees, float pitchDegrees, bool noclip, out string feedback)`. The cave teleport then calls it, with behaviour unchanged.
- **Test hook:** add `InstallEmitterSiteDefaults(path)` to the drawer, matching the isolated-preference installers used by the capture tools.

### C.3 Site-rings debug (`EmitterSiteDebugRings`)

For the ≤ 3 nearest sites: line loops (`ImmediateMesh`, 128 segments, unshaded, `no_depth_test`, colour-coded) at r = 80 (hole), 350 (pad), 410 (apron), 2,000 (island enclosure) and R_inf. Each vertex is draped at sampled terrain height + 2 m.
- Rebuild on a worker (sampler `CreateIndependentCopy`, as `RingScaleAnchorLayer.cs:236`) when the nearest set changes.
- On rebase, re-project the cached canonical points synchronously.
- A minimap overlay is deferred to P8 proper.

---

## D. Milestone 1 tests

### D.1 Headless smokes (pattern: `tools/X.cs` + `tools/x.gd` runner, or a Node smoke with `Result`)

| Smoke | Asserts |
|---|---|
| a. `EmitterSitePlacementSmoke` (pure, registry from P1 fixture + synthetic geometry snapshots, Certified R) | For all 600 sites: `WorldToCanonical(origin) ≈ (A, P + clr, X)` < 1 cm; `basis.Y · radialInward` > 1 − 1e-9; **`EntryApproach` bearing == β ± 0.05°** (measured in (around, across)); `det(basis) = +1`; seam sites (A ≈ 0 and C − 2) are continuous; MultiMesh instance transform == slot transform (< 0.1 mm). |
| b. Rebase invariance | Anchor sequences, including a seam wrap (`SetFloatingOriginCanonicalAnchor` semantics from `VoxelChunkGridManager.cs:304-320`) and random 32 km recentres. For `BeamOrigin`, `BeamCrownFocus` and `EntryApproach`: canonical before = after (< 1 cm); slot and stand-in transforms are recomputed in the same call; `LastRebuildMs` < 1 ms (Release). |
| c. Terrain3D agreement | For pad points r ∈ {0, 200, 350} at 8 azimuths: snapshot world vs `BuildWorldFrame(frame) * frame.MapPosition(...)` < 1 cm, for frames anchored at the site and 400 m away. |
| d. `EmitterPoiStreamerCoreSmoke` (pure) | A radial oscillation 13-17 km (period 2 s, 120 s) gives exactly 1 assign and ≤ 1 release per crossing pair, no thrash under `MinDwell`; collision gating at 1.5/2.0 km; passing site A then B reuses the same slot ids; `active ≤ pool`; a teleport between distant sites in one frame releases then assigns (no third slot). |
| e. `EmitterSiteSettingsSmoke` | Defaults, round-trip, clamps, reset, invalid path (copy of `SurfaceTerrainSettingsSmoke`). |
| f. `EmitterSiteTraverseSmoke` (scene-level, headless, stub geometry/placement sources if Terrain3D needs a GPU) | Teleport through 60 sites (10 rows × 6 columns), then a 2 km/s noclip flight over 3 rows, with forced anchor recentres. Afterwards: slot count constant, `Performance.ObjectOrphanNodeCount` unchanged, `ObjectCount` growth < 0.5 %, MultiMesh instance count == `Count`, claims drained, no errors. |
| g. Aperture smoke (after M1-8) | `SampleCollisionHeight` at the bore centre == P − 200; at r = 90 == P; the probe never latches underground at the plug (run the underground round-trip smoke with the player on the plug); proxy material receives two aperture uniforms only while a slot is active. |
| h. Underground round-trip | The existing smoke plus the new participant: force underground → stand-ins hidden at deep hide and retained, slot unaffected; surface → restored. |

### D.2 Windowed capture route `tools/emitter_site_capture_test.gd`

Modelled on `tools/atmosphere_visual_test.gd`: create the game with the runtime backend and a **v5 recipe**, isolated preferences, wait-ready, then `explorer.process_mode = DISABLED` for posed shots, and write PNGs plus `report.json`.

Pick sites through the registry: the first Land site with `maxCut < 20`, the first Island site, and the Land site with the largest `maxCut` (mountain).

Shots per site, at noon (plus one dusk shot for the island):
1. **Entry, ground:** camera at `EntryApproach` + 1.7 m, looking at the spire, pitch +25°.
2. **Gallery:** Y16 at R160, looking into the bore. Check the liner, plug, no proxy lid, and no ocean disk (island).
3. **Handoff sweep:** distance 6, 11, 12, 13, 14, 15, 16.5 km along −β at P + 50 m. Frame diffs flag pops > threshold around 11.5-12.5 and 14-16 km.
4. **20 km:** ground level and P + 1 km.
5. **High altitude:** 150 km above, 60 km offset, looking at the site. Expect a 3-12 px spire, atmosphere-hazed.
6. **Orbit:** 400 km altitude, looking along the ring. Several stand-ins show 1-2 px; the cutoff is respected; no spires bleed through terrain.

The report records slot states, stand-in estimates, the frame-time p95 per shot, and the first-visible-slot frame spike. The human review checklist (z-fight, float, pop, haze match U4, shimmer U5) is printed with the PASS line.

---

## E. Milestone 1 phase order (implementer-sized, each independently testable)

| Step | Deliverable | Depends on | Tests |
|---|---|---|---|
| M1-0 | P0 + P1 (in progress): registry, adapters, goldens | — | spec §8.1-2 |
| M1-1 | **P2** `EmitterSiteSurfaceOperator` + mask (pure) | P1 recipe | spec §8.3, 8.4 synthetic, 8.11 |
| M1-2 | **P3** gen-17 v5 recipe, model hook, diagnostics, runtime hash, launcher/preview create v5; expose `RingTerrainSampler.EmitterSites` (null unless v5) | M1-1 | spec §8.1, 8.3, 8.4, 8.10 |
| M1-3 | **A1** placement source adapter (`GetPlacement`, site spec P4 API) + `EmitterSiteFrameMath` + beam hook API | **P1 only** (can run in parallel with M1-1/2, using the registry on the v4 natural source) | D.1-a, D.1-c |
| M1-4 | **B** stand-in shader + layer + `EmitterSites` scene node (stand-ins only) + rebase rebuild + underground participant | M1-3 (visual correctness needs M1-2) | D.1-b (stand-in part), D.1-h, verify ShaderMaterial fade (B.3) |
| M1-5 | **A2** streamer core + node: threaded load, pool, hysteresis, collision gating, presented handoff, loading-bridge readiness, diagnostics | M1-3, M1-4 | D.1-b, D.1-d, D.1-f |
| M1-6 | **P4a** `TerrainSample` mask, biome override, grass/tree gates | M1-2 | spec §8.9 |
| M1-7 | **P4b** Terrain3D control-map hole + collision hole mask + `GetSurfaceApertures` | M1-2 | spec §8.5 (heightmap) |
| M1-8 | **A3** `build_bore_liner.gd` + `BoreLinerPlug.tscn`, slot wiring (heightmap only), aperture shader include in proxy/global/ocean, `RingSurfaceApertures`, aperture-aware `SampleCollisionHeight` and Terrain3D probe | M1-5, M1-7 | D.1-g, D.2 shot 2 |
| M1-9 | **C1** settings resource/defaults + smoke, applied to streamer/stand-ins | M1-5 | D.1-e |
| M1-10 | **C2** drawer section, `TryTeleportToCanonical` refactor (cave teleport regression), debug rings | M1-9 | drawer smoke (opens, values round-trip), manual |
| M1-11 | **D2** capture route + human review + perf note | all | D.2 |

M1-3 can start as soon as P1 lands. M1-4/M1-5 can be developed against unshaped v4 terrain (the POI floats or sinks) while M1-1/2 proceed. The critical path is P1 → P2 → P3 → P4b → M1-8.

**Asks for the P1 implementer** (to the primary agent, before P1 freezes the record):
1. Store the **bearing β** (radians, in (around, across)) explicitly, plus `maxCut`. Do not store only a +1-frame yaw (E3).
2. Expose a `FindNearestIndex(around, across)` row/col lookup **without** the R_inf test. `TryGetInfluencingSite` returns false outside R_inf, but the streamer needs the nearest site at 16+ km.
3. Expose `Count` and `Identity`.

---

## F. Decisions needing the user

- **U1 Whole-arch visibility:** in M1, spires vanish beyond ~700-850 km (≤ 1.5 px). Should distant sites appear as lights (crown beacons) across the arch? Recommended: no for M1; arrives with the eclipse beams (T4 hook).
- **U2 Beam convergence:** do beams follow the local radial (parallel to the spire axis, meeting the ring axis at each site's across) or converge on one hub point (tilt ≤ 1.52°, ~46 m off-axis at the crown)? This affects future beam and asset work.
- **U3 Bore safety (M1):** add an invisible guard collar at the throat rim, or allow a 200 m fall onto the plug? Recommended: collar on until a lift exists. Also confirm plug depth (−200 m) and an emissive look.
- **U4 POI atmosphere:** accept `StandardMaterial3D` (Environment fog only) for POI detail in M1 and convert only if captures show a haze mismatch? Recommended: yes, decide after D.2.
- **U5 Far shimmer:** accept FarProxy-only far stand-ins (thin sub-pixel arms, TAA-softened) in M1, with a silhouette impostor as later polish? Recommended: yes.

## G. Technical verifications (not user decisions)

1. The engine visibility-range fade applies with a ShaderMaterial override (B.3).
2. The visibility-range distance metric (AABB centre vs origin) matches the 900 m mid-height assumption well enough; checked by the D.2 sweep.
3. Whether ocean proxy/global surfaces draw under an island pad (whether the aperture include is needed there).
4. Whether proxy/global underlay offsets near R350-410 make the floors visibly float.
5. The first-visible pipeline spike, which decides the warm-up.

---

## Appendix: key numbers

- Spire 1800 m; crown focus Y1743; bore R75 (liner R76); hole 80; throat R75-94 z0..14; slab −1; pad R350; apron to R410; FarProxy 352 tris; detail end 12.5 km; facility end 6 km.
- Load 14 km / unload 16 km / collision 1.5-2 km; pool 2; max simultaneous sites in range: 1.
- Far tier: main camera to min(700 km default, 850 km global LOD fade end, 1.2 Mm camera far). Scaled far viewport 1:1000; its terrain cutout is 16-625 km.
- R ≈ 5,000 km; at 850 km a site rises 72 km and leans 9.7°; hub-convergence tilt ≤ 1.52°.

---

## USER DECISIONS (2026-10-14) — binding

- Islands: keep the many-islands outcome (321/600 in gen-17); ocean sites stay on the grid.
- U2 Eclipse beams converge exactly on the hub (per-beam tilt up to ~1.52°).
- U3 Bore edge: allow the fall (no guard collar); the plug at −200 m is the landing.
- U1 No arch lights beyond ~850 km in milestone 1.
- U4/U5 defaults accepted: standard materials on POI detail meshes; some far stand-in shimmer acceptable in milestone 1.

## MAIN SPIRE DECISIONS (2026-10-14) — binding

- Assets: prototypes/Ringworld_Main_Spire_4km_Outer_Ring (16-arm spire, 1,642.78 m footprint, 4,000 m tall, 300 m beam bore = R150 clear, outer feet at R730, entry through outer arm 07 at 292.5°) and prototypes/Ringworld_Main_Reactor (six-module master reactor, ~5.0 x 5.6 x 2.8 km architecture, nominal cavern 6 km x 4 km, centre ~4,000 m below surface, roof ~-1,922 m, 300 m clear exhaust).
- Exactly one main site. It sits on the ring centre line (across = W/2 = 159 km) and REPLACES ALL SIX regular sites in its row. Totals: 99 rows x 6 = 594 regular + 1 main = 595 sites. Main beam is vertical to the hub (zero tilt).
- Main row selection: seed-driven, preferring good land; if no row offers acceptable land, the main site still goes on the centre line and gets an (enlarged) island.
- Event vision: at eclipse ignition every spire fires and converges on the hub to light the artificial sun; the main spire then sustains it for the whole eclipse, with the ~594 regular spires periodically sending pulses to the false sun to keep it fuelled.
