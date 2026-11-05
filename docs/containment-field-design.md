# Atmospheric containment field (lead design, 2026-10-29)

User request (2026-10-29): an atmospheric containment field emitted from the rim walls, forming a continuous arched canopy across the 318 km habitat. It reaches 40 km above the habitat floor at the centre, follows the circumference and leaves the central hole open. It is invisible in normal play. Close up it shows subtle shimmer and refraction. Extreme storms briefly reveal localized ripples. No permanent glow or grid. Low cost. A POI that runs along the field will follow.

Status: design only. No field code has been written beyond the megastorm seam (`scripts/ContainmentField/ContainmentDisturbance.cs`). The user answered the three open questions on 2026-10-29. A second round of decisions on 2026-11-04 (next section) **supersedes** parts of sections 1.5, 3.3-3.4, 3.7, 2.1, 5, 6.1, 7, 8 (WO12 and ordering) and 9. Where the body text disagrees with the 2026-11-04 section, the 2026-11-04 section wins until the body is rewritten.

## User decisions 2026-11-04

Prompted by the atmosphere-shield projector asset (`ringworld_atmosphere_projector` bundle: two-blade tower, 80 x 180 x 1600 m, origin at the wall-top centre, shield plane at local X = 0, planned 100 m wall).

1. **Rim wall gets real volume, and it comes first, not last.** WO12 moves to the front of the queue, straight after WO1. The field itself stays a thin sheet (no slab thickness).
2. **Depth 128 m** (four 32 m voxel chunks). The wall occupies across [-128, 0] and [W, W + 128]; inner faces do not move. With the projector's 80 m body centred, that leaves 24 m of clear wall top on each side.
3. **Field root: centre line.** `RimWallDescription.Current = new(128, RimFieldRoot.CentreLine)`, so the roots are at across -64 and W + 64 and the profile half-width is `a = (W + 128) / 2` (159,064 m). This matches the projector's X = 0 shield plane. The habitat half of the wall top (64 m) is inside the retained atmosphere; the outer 64 m is vacuum. Walking across the wall top crosses the field. This replaces WO12's OuterEdge recommendation.
4. **Contact: pass through, with a ripple.** `FlightPermeability = Pass`, `ProjectilePermeability = Pass`. New disturbance kind `BodyContact`: the player/camera and registered fast bodies (projectiles, debris) emit a ripple where their per-frame movement segment crosses the field (`TryIntersectWorldSegment`). Initial values: Rmax 2-30 m scaled by speed and size, T 1.5 s, refraction plus a brief sheen boost, no flash light. A soft membrane "whump" sound on crossing. These count against the ripple pool with their own small reservation (2 slots) and a per-source cooldown, so a player standing in the sheet cannot spam it.
5. **Visibility range stays 3 km** (the existing `ContainmentShimmerRangeKm` default). A brief change to 10-50 m was considered and reverted by the user the same day.
6. **Close look: refraction plus a faint sheen.** This adds one term to the near patch (3.4): a faint cool tint (about the ripple tint, 0.55/0.65/1.0) weighted by grazing angle and the same distance weight `w`, at most about 3-4 % over the background. It is still not a glow: it is proportional to what is behind the field (multiplied into the refracted colour, not added), so it is zero against black space and never lights anything. No grid, no fresnel rim line. New menu slider `ContainmentSheenStrength` (0-2, default 1). Strikes and ripples are unchanged.
7. **Access: flight.** Flight does not exist yet, but the field must be built for it. That pulls several "later" items into scope:
   - the 3.7 limitation (clouds overwriting the shimmer when seen from above) needs the post-Sunshine field pass when the camera is above the field;
   - section 2.1's atmosphere lid stops being optional: WO9 builds the clip at the field, not just the audit;
   - verification uses a debug free-fly camera until real flight lands, including views from above the apex and along the arch.
8. **Vacuum side: visual and audio only.** Outside the field (`SignedDistance > 0`), haze stops at the field (from the lid in 7), and the weather and ambience buses get a low-pass plus a gain drop that ramps over a few metres around the crossing. No gameplay penalty.
9. **Respace the cue lines evenly.** Spacing becomes `C / 126` (249,332.8 m on Certified; derive it from the live circumference, not a constant). That gives 126 lines and **126 projector sites per wall, 252 in total**, with no seam gap. Every hull blue line except k = 0 shifts slightly. WO8 owns the shared constant and the hull-shader change. Captures before and after the blue-line move are expected to differ.

### Found while checking the projector: the ellipse curves out of the projector slot

The ellipse leaves the root vertically but bends inward at once. Inward offset from the root at height h above the wall top (Certified, rise 30 km): 0.9 m at 100 m, 22 m at 500 m, 72 m at 900 m, 226 m at 1,600 m. The projector's central slit is only 0.9 m wide and the blades reach 40 m each side, so the field would cut through the habitat-side blade from about 100 m up.

**Decided (user, 2026-11-05): a vertical skirt, 1,600 m.** The profile becomes a vertical segment from the wall top up to the projector crown (`SkirtHeightMeters`, 1,600 m), then the semi-ellipse from there to the 40 km apex (rise 28.4 km). The field rises straight through the tower stack and the arch starts at the crown, so the towers read as the emitters. It is a profile parameter like the roots, the queries stay exact (segment plus ellipse, still 2D), and the skirt runs along the whole wall, not only at projectors. It barely changes the cloud ceiling: the field moves at most about 230 m outward near the rims. Alternatives: a superellipse with a fuller shoulder (the field still leaves the slot, just higher), or accepting the clash and hiding it with the soft intersection (not recommended, because the field would visibly cut the blades).

### Revised work-order order

WO1 (geometry API with the 128 m centre-line wall description and skirt), then **WO12 (rim wall depth)**, then WO8 (anchors and even respacing), then WO2-WO6, then WO9 (atmosphere lid, now required), then contact ripples (new, after WO4), then WO10 audio (vacuum low-pass plus crossing sound), then WO13/WO14. The post-Sunshine pass for views from above goes with WO3 or straight after it. Projector POI import follows WO12 and WO8. All of it starts after the current megastorm work (`docs/megastorm-handoff.md`).

### WO1 status (2026-11-05): geometry core done

- Files: `scripts/ContainmentField/ContainmentFieldProfile.cs` (profile, `RimWallDescription.Current = (128, CentreLine)`, `RimFieldRoot`), `ContainmentFieldGeometry.cs`, `ContainmentFieldPlacement.cs`, `ContainmentFieldPolicy.cs` (flight and projectile permeability = Pass). Test: `tools/ContainmentFieldGeometryTest.cs` + `containment_field_geometry_test.gd`, manifest `containment_field_geometry_test` (headless, gate, tag `containment-field`, marker `CONTAINMENT_FIELD_GEOMETRY`). Passes.
- Profile has a fifth/sixth field `SkirtHeightMeters` (default 1,600). Curve = left skirt, upper semi-ellipse from the crown (`WallTop + Skirt`) to the apex, right skirt. `H(root) = WallTop` exactly; just inside the root H is at least the crown (the skirt is vertical, H is not continuous at the root). Arch length runs skirt, ellipse, skirt: Certified with the Current wall is 334,683.595 m (ellipse half 331,483.595 m), runtime-surface 334,377.306 m.
- Signed distance is negative for the geometric region (between the roots, below the curve, including the wall body below the wall top); `IsContained` additionally excludes the wall body (across outside [0, W] and vertical at or below the wall top). `TryIntersectWorldSegment` rejects sign flips that are not surface zeros, so passing through the wall body below the wall top is not a crossing; walking across the wall top crosses the skirt at the centre line.
- `WorldTransform` basis is X = around tangent, Y = -N, Z = X x Y (the arch tangent reversed): the spec's Z = arch tangent is left-handed. `Vector3d` does not exist; canonical directions are `CanonicalVector`. `MegastormFieldCoupling.CeilingFromUnderside` is not yet replaced; `CloudCeilingFromUnderside` matches the stub bit for bit (asserted). Cloud ceiling reaches 18 km 4.45 km from the inner face (Certified), 4.03 km (runtime-surface), not 6.2 km, because the skirt moves the arch outward. Nothing is wired into the running game.

## User decisions 2026-10-29

1. **Wall height.**
   - The field is anchored to the real rim-wall top: `RingLodGeometrySnapshot.StructuralWallTopGlobalY`. That is 10.0 km on Certified and 10.4 km on runtime-surface worlds.
   - The apex stays at 40 km above sea level.
   - The "6 km" figure in the request is not used anywhere in this design.
2. **Sprites and jets are left exactly as they are.**
   - No clamp under the field; the lore conflict is accepted. Sprite height, timing, chance and look do not change.
   - The old WO7 (sprite clamp) is dropped.
   - The field may still ripple where an **existing** sprite or jet column crosses it. This is a field-side effect only. It reads the sprite derivation and never writes to it.
3. **POI form: wall-top POIs.**
   - User's words: "these ring containment field pois will sit atop the wall. at every vertical blue line on the wall. now this would mean that we will need to give the ring wall some depth. i am thinking 100 meters wide but this will be the last thing we will deal with right before adding the wall pois."
   - The vertical blue lines are the hull shader's longitudinal structural cue. There are 125 per wall, every 250 km, with one 415.9 km seam gap (E19, 6.1).
   - WO8's first helper produces wall-top anchors at those positions.
   - A new final work order, **WO12: rim wall depth (~100 m)**, comes last, just before the wall POIs (section 8). The geometry API carries the wall depth and field root positions as parameters from WO1 on, so nothing earlier assumes a zero-thickness wall (1.4, 1.5).
4. **Megastorm reconciliation** (with `docs/megastorm-design.md`). The disturbance interface consumes the megastorm hooks in its section 6.3:
   - `UpdraftBurst`, `Lightning`, `UpperDischarge`, `QueryContact`;
   - the field installs `FieldUndersideProvider`.

   There is **one** cloud ceiling: `min(18 km, field underside - 300 m)`. Sunshine, the CPU column, the bake clamps and the megastorm wall all flatten against it (2.2, 4.2). The megastorm's default speed is **350 m/s** (user decision), and the contact adapter is sized for that.
5. **"We can also have lightning touch the containment field."**
   - The megastorm adds upward **field strikes** (its 5.5.5), and the field designs the response (3.10): a ripple ring, plus a brief crawl and contact glow lit only by the strike's pulse.
   - Seen beyond 400 km, and across the arch, through a far strike layer.
   - New disturbance kind `FieldStrike`.
6. **"Yes ordinary storms can do this if close to a wall."** Strong natural storm towers within about 10 km of a wall can rarely strike the field. Gating is in 3.10.6; WO14 builds it. Natural storms far from the walls never do.

## Summary of the recommendation

- **Shape.** A semi-ellipse in the ring cross-section, extruded around the whole circumference as a surface of revolution about the ring axis. It rises from the real rim-wall top: 10.0 km on Certified and 10.4 km on the runtime-surface backend (decision 1). It reaches 40 km above sea level (canonical vertical 0) at the centreline.
  - The field leaves the wall top vertically, so it reads as the wall continuing upward.
  - The shape matches a membrane holding an exponentially thinning atmosphere: it curves sharply where pressure is high and is nearly flat at the top.
  - It sits far higher near the rims than a circular arc would. 18 km storm tops only meet it within 5.8 km of a wall, against 22 km for a circle.
- **Exact queries.** Every distance and nearest-point query is exact and 2D, because the nearest point on a surface of revolution always lies in the query point's own meridian plane. All of it is pure C# in canonical coordinates, testable headless.
- **Rendering.** No CompositorEffect. Two kinds of transparent mesh, drawn in the transparent pass:
  - a **near patch**, shown only within the shimmer range (3 km default);
  - up to 16 pooled **ripple patches**, shown only while a disturbance is alive.

  Both are built exactly on the CPU in double precision, like the proxy sectors, and use one unshaded shader with `hint_screen_texture` refraction plus `hint_depth_texture` soft intersection. With nothing in range, nothing is drawn: **zero GPU cost and pixel identity**.
- **Ripples.** A ripple refracts, and adds light only while its source flash is lit (lightning pulse, sprite level). That gives no permanent glow. The ripple profile is analytic and isotropic, so there is no grid.
- **Disturbance interface.** `ContainmentDisturbance(SourceId, Kind, Position, StartTime, Energy, Radius, Duration, FlashCoupling, Occluder)` goes into a deterministic bus on the weather clock. Adapters:
  - poll the existing lightning list;
  - read existing sprites and jets (read-only);
  - check storm tops against the field;
  - pull the megastorm's `MegastormFieldCoupling` queries.
- **Atmosphere.** The current model already holds the air between the wall planes, extended upward, under a 20 km cap. The field only removes a sliver within about 9 km of each rim. v1 needs no lid; WO9 measures this before anything changes.
- **Clouds.** They get one soft ceiling, `min(18 km, H - 300 m)`, shared with the megastorm wall. It only bites within about 6 km of the rims and is exactly 1.0 everywhere else.
- **Field strikes (3.10).**
  - Megastorm upward channels, and rare strong natural towers within about 10 km of a wall, terminate on the field.
  - The field answers with a ripple, plus a ≤ 1 s crawl of filaments and a contact glow, both lit only by the strike's own pulse.
  - It is rendered by the ripple pool within 400 km and by a far strike layer out to the antipode.
- **Wall-top POIs.** They sit at the 125 longitudinal cue lines on each wall. A 100 m wall depth arrives last (WO12), and the field root moves with it through one profile parameter.
- **Menu.** Atmosphere tab, three sliders: shimmer strength, shimmer range, ripple strength. Debug views go through CLI flags and the Diagnostics tab.

---

## 0. Evidence (what the code says today)

| # | Finding | Where |
|---|---|---|
| E1 | Certified ring: requested radius 5,000 km, width 318 km, sea level 0, terrain ceiling 8,000 m, wall clearance 2,000 m, **wall top 10,000 m**. | `scripts/Voxel/Core/CertifiedRingScale.cs:13-24` |
| E2 | Launcher: Certified build max is 8,000. The runtime-surface backend overrides it to 8,400, so the **wall top is 10,400 m** in runtime-generation worlds. The wall top is `MaxBuildGlobalY + StructuralWallClearanceMeters`. | `scripts/world_size_launcher.gd:29-36, 1565, 1591-1593, 1716`; `scripts/Voxel/Phase2/VoxelChunkGridManager.cs:1600-1604` |
| E3 | Wall Engines (rim-wall machines) assume a 10 km wall (`WallHeightM = 10_000`). Their vents are at 2/4/6 km. "6 km" in the request may come from the vent galleries or the 2-6 km deck. | `scripts/Terrain/Sites/ClimateMachineRecipe.cs:98`; `docs/emitter-grid-climate-machines-spec.md:435` |
| E4 | **Canonical frame.**<br>- `(Around, Vertical, Across)`; vertical is radial inward: `radial = R - (RingVerticalBaseOffset + vertical)`.<br>- World/grid-local **Y is the ring axis (across)**, so ring up is not world Y.<br>- Across runs 0..W, with the walls at 0 and W.<br>- `CenteredAt` gives sector-local, double-precision mesh anchors (<1 mm shared edges, self-test). | `scripts/Voxel/Phase2/RingLodGeometrySnapshot.cs:8-18, 56-68, 75-90, 118-140`; `scripts/Voxel/Core/RingCanonicalPosition.cs:9-31`; `addons/SunshineClouds2/SunshineCloudsCompute.glsl:242-249` (across 0..W) |
| E5 | Rim walls are built in every tier:<br>- near proxy sectors (surface A, hull shader, double-sided);<br>- global LOD hull;<br>- analytic global LOD;<br>- scaled far viewport (zero-thickness planes at across 0 and W, up to `StructuralWallTopMeters`).<br>The hull shader has a teal **top rail** cue on the wall top. | `scripts/Voxel/LOD/RingTerrainProxySector.cs:5247, 6170-6181`; `scripts/Voxel/LOD/RingTerrainGlobalLod.cs:1507, 1603`; `scripts/Voxel/LOD/RingAnalyticGlobalLod.cs:223`; `scripts/Voxel/LOD/RingScaledFarViewport.cs:123, 1875-1876`; `materials/ring_hull_global.gdshader:29-39` |
| E6 | **Atmosphere model.** Air is the interval between the floor shell and an upper shell at `UpperHeightMeters` (20 km), **clipped by the two side-wall planes extended without limit** (`side_negative/side_positive`). Rayleigh scale height is 8 km and aerosol 1.5 km. The sky goes black from 8 to 20 km; stars fade in from 20 to 26 km. | `materials/ring_atmosphere_shared.gdshaderinc:3-53`; `scripts/Voxel/LOD/RingAtmosphereModel.cs:114, 235-236, 334-335`; `scripts/Voxel/LOD/RingGraphicsQualityController.cs:94`; `scripts/day_side_atmosphere.gd:89-102` |
| E7 | Clouds:<br>- the range is 500 m to 18 km (S6), and only Cb towers and anvils reach 14-18 km;<br>- the Sunshine density has a 1 km side-wall feather (`ring_side_wall_mask`);<br>- storm ceilings per system come from `CollectRenderWeatherSystems` (`StormCeilingMeters`). | `docs/storm-revamp-s6-cloud-layers-design.md:1-29, 107-110`; `addons/SunshineClouds2/SunshineCloudsCompute.glsl:242-249, 460-462`; `scripts/RingVolumetricCloudLayer.cs:3855-3890` |
| E8 | Lightning:<br>- `RingLightningEvent` is deterministic. It has an EventId, a SystemId, a start time on the weather clock, ground and cloud positions (the **cloud position is at the cloud base**, not the top), Energy and a pulse envelope (reduced-flash aware).<br>- Consumers **poll** `ActiveLightningEvents`. | `scripts/Lightning/RingLightningEvent.cs:7-41`; `scripts/RingVolumetricCloudLayer.Lightning.cs:34, 381-384`; `scripts/Lightning/RingUpperAtmosphereLightningLayer.cs:99-114` |
| E9 | Red sprites are centred at **cloud top + 55 km** with a height of 45 km. Blue and gigantic jets rise from the cloud top, 40 / 75 km tall. Both are drawn in the far viewport only. **All of them extend above the 40 km apex today.** | `scripts/Lightning/UpperAtmosphereSprite.cs:17-74`; `docs/storm-revamp-plan.md:59-62` |
| E10 | Compositor chain:<br>- Sunshine clouds, rain streaks, rain haze and rainbow are `POST_TRANSPARENT`; rain haze is forced last;<br>- the far backdrop is `POST_SKY`;<br>- the far viewport is a separate 1:1000 World3D composited as background with no depth interplay. | `addons/SunshineClouds2/SunshineClouds.gd:1294-1297`; `scripts/RingLocalWeatherEffects.RainFog.cs:199-214`; `scripts/render/RingFarBackdropComposite.gd:23`; `scripts/Voxel/LOD/RingScaledFarViewport.cs:16-21` |
| E11 | Screen and depth texture patterns:<br>- ocean refraction plus reversed-Z depth linearisation;<br>- water and lens droplets already use `hint_screen_texture`, so the back-buffer copy is already paid whenever water or rain is on screen. | `materials/ring_ocean_proxy.gdshader:64-69, 489-506`; `materials/ring_water_common.gdshaderinc:25-26`; `materials/ring_lens_droplets.gdshader:7` |
| E12 | Haze for any material that includes `ring_atmosphere`: `RingPoiHaze.RegisterMaterial` plus `Tick` feed the camera ray frame, and `ring_haze_sample(camera, target, center, day)` returns `RingHaze.T`. | `scripts/Atmosphere/RingPoiHaze.cs:11-30, 86-150`; `materials/ring_poi_architecture.gdshaderinc:40-50`; `materials/ring_atmosphere.gdshaderinc:544-568` |
| E13 | Render layers: the precipitation occlusion map camera excludes layer 19 (group `no_precipitation_occlusion`). | `scripts/render/RingRenderLayers.cs:12-55` |
| E14 | Main camera far plane is 1.2 Mm. The far field starts at 850 km. | `scripts/world_size_launcher.gd:48-50` |
| E15 | POI pipeline: shared `ring_poi_architecture*.gdshader`, `RingPoiHaze.RegisterTree`, `PoiBaseAmbientOcclusion`, `RingLargePoiShadowExtents`, streamer pools. | `.claude/skills/poi-asset-import/SKILL.md`; `scripts/poi/Emitters/EmitterPoiStreamer.cs:146, 288-289`; `scripts/poi/WeatherLung/WeatherLungStreamer.cs:167-177`; `scripts/Shadows/RingLargePoiShadowExtents.cs:12` |
| E16 | Shader warm-up: shaders listed in `RingWorldLoadingResources` compile on a worker before the world attaches. | `scripts/Loading/RingWorldLoadingResources.cs:55-80`; `scripts/Loading/RingStartupWarmup.cs:1-30` |
| E17 | Live menu: the Atmosphere tab already exists. New subsystems use the table-driven settings trio. | `scripts/ui/RingLiveTuningDrawer.Atmosphere.cs:127`; `scripts/ui/RingLiveTuningDrawer.cs:360`; `.claude/skills/live-tuning-setting/SKILL.md` |
| E18 | Megastorm design: the field pulls `MegastormFieldCoupling`, which provides:<br>- `QueryDisturbances(t0, t1)`, with kinds UpdraftBurst, Lightning and UpperDischarge;<br>- `QueryContact(t)`;<br>- a `FieldUndersideProvider` setter.<br>The megastorm's own ceiling text says `min(18 km, underside - 1 km)` and assumes 6 km rims and a circular arc. Both are superseded by 2.2 here.<br>Default speed is 350 m/s (user decision 2026-10-29; the doc still says 25). | `docs/megastorm-design.md:24-25, 62-66, 303-329, 340, 449` |
| E19 | **The "vertical blue lines" on the rim wall** are the hull shader's longitudinal structural cue:<br>- `periodic_structure_line(around, structural_longitudinal_spacing_meters = 250 000, half_width = 1 200)`, teal `structural_cue_color (0.08, 0.72, 0.82)`, emissive (day 0.16, night 0.85);<br>- `around = atan(rel.z, rel.x) * R`, so a line sits at every `around = k * 250 km` measured from ring angle 0, which is canonical around 0;<br>- `atan` covers [-πR, πR] and the circumference (31 415 936 m) is not a multiple of 250 km. That gives **125 lines per wall** (k = -62..62), spaced 250 km, except **one 415.9 km gap** centred on around = C/2;<br>- the same lines are on both walls (250 in all), visible as bands about 2.4-4.8 km wide;<br>- one shader serves every tier: near proxy walls, global hull, far shell (×CoordinateScale);<br>- the cross grid (by across) is masked off wall faces, and the top rail is horizontal. | `materials/ring_hull_global.gdshader:24-33, 49-77, 127-140`; `scripts/Voxel/LOD/RingTerrainGlobalLod.cs:106-110, 1508-1510`; `scripts/Voxel/LOD/RingScaledFarViewport.cs:192-196, 1284-1290`; `scripts/Voxel/LOD/RingTerrainProxySector.cs:6170` |
| E20 | Wall thickness today:<br>- render tiers draw the rim walls as **zero-thickness planes** at across 0 and W;<br>- the voxel domain has a 32 m structural hull **outboard** of the habitat (across -32..0 and W..W+32). | `RingScaledFarViewport.cs:1875-1876`; `RingTerrainProxySector.cs:5247`; `RingTerrainGlobalLod.cs:292, 1603`; `VoxelChunkGridManager.cs:4729-4740` |

---

## 1. Geometry

### 1.1 Interpretation

- **Follows the circumference.** The field is a surface of revolution about the ring axis. In canonical coordinates it is a height function of `across` only: `vertical = H(across)` for every `around`. In world space every cross-section is the same profile, rotated about the axis.
- **Central hole open.** The field is a curved band (an annular canopy) spanning only across ∈ [0, W] between the two wall tops, at radii between `R - Apex` and `R - WallTop`. Nothing spans the ring's interior:
  - no disc or lid over the hub or sun space;
  - nothing outboard of the walls or under the hull.

  Everything inside radius `R - 40 km` stays open space. Hub beams and eclipse orbs pass through the band; light passes freely (lore).
- **Habitat floor.** This is canonical vertical 0 (sea level, `CertifiedRingScale.SeaLevelMeters`, E1). It is not the terrain floor (-8,192 m) and not local terrain height. The 40 km apex is canonical vertical 40,000.
- **Wall top.** This is the runtime value `RingLodGeometrySnapshot.StructuralWallTopGlobalY`: 10,000 or 10,400 m (E1, E2), never a constant (decision 1).
- **Field roots.** The field leaves the wall top at two root lines, `RootAcross0` and `RootAcrossW`. Today they are 0 and W (zero-thickness walls). WO12 moves them when the wall gains depth (1.5). The profile is defined between the roots, not between 0 and W.

### 1.2 Cross-section: semi-ellipse (chosen)

Define:
- half-width `a = (RootAcrossW - RootAcross0) / 2`, which is 159 km today (159.1 km with WO12's outer-edge roots);
- centre `c = (RootAcross0 + RootAcrossW) / 2`, which is W/2 in both cases;
- rise `b = Apex - WallTop` (30.0 km Certified, 29.6 km runtime-surface);
- `x = across - c`.

```
H(across) = WallTop + b * sqrt(1 - (x/a)^2)                      across in [RootAcross0, RootAcrossW]
parametric: across(phi) = c - a*cos(phi),  vertical(phi) = WallTop + b*sin(phi),  phi in [0, pi]
```

Comparison, Certified (WallTop 10 km, Apex 40 km):

| Distance from rim | Ellipse H | Circular arc H (R_c = 436 km) |
|---|---|---|
| 0.5 km | 12.4 km | 10.2 km |
| 2 km | 14.7 km | 10.8 km |
| 6 km | 18.2 km | 12.3 km |
| 10 km | 20.5 km | 13.8 km |
| 30 km | 27.5 km | 20.5 km |
| 80 km | 36.0 km | 32.8 km |
| Where H = 18 km (S6 cloud ceiling) | 5.8 km | 22.2 km |
| Where H = 20 km (atmosphere cap) | 9.1 km | 28.4 km |

Half-arch length is 332.6 km. The radius of curvature is 5.7 km at the rim and 843 km at the apex.

Why an ellipse, not a circular arc or a catenary:

1. **Physics-flavoured lore.** A tension membrane has curvature proportional to the pressure it holds back: Laplace, `κ = Δp / T`. Air pressure falls with an 8 km scale height, so between the wall top (10 km) and the apex (40 km) the load drops by about e^(30/8) ≈ 42x. The ideal canopy therefore bends hard near the walls and is almost flat on top. The ellipse has the same character (curvature ratio about 150x). A circular arc is the constant-pressure answer. A catenary is the self-weight arch answer, which does not apply to a massless field.
2. **"Emitted from the walls".** The tangent is vertical at the wall top, so the field leaves the top rail (E5) as a continuation of the wall face. There is no kink, and no shelf that would invite a visible seam line at grazing angles.
3. **Weather room.** The habitat's volume near the rims stays large. Only Cb tops within 5.8 km of a wall meet the field, which is exactly where "extreme storms reveal ripples" should happen (section 4). A circular arc would decapitate storms in a 22 km band along each wall.
4. **Cheap and exact.** `H` and `dH/dacross` are closed form. Point-to-ellipse distance has a robust, bounded root solve (Eberly's bisection). The circle would be simpler still, but points 1-3 matter more.

No shape exponent is exposed. If art later wants a fuller shoulder, the profile struct can switch to a superellipse with exponent `p` (p = 2 is the ellipse) without changing the API.

### 1.3 Exactness: why the queries are 2D and exact

The field is a surface of revolution about the ring axis. The nearest field point to any point Q therefore lies in Q's own meridian half-plane (same ring angle, so the same `around` scaled to that radius). In that half-plane, `(vertical, across)` are Euclidean metres: vertical is radial distance offset by a constant, and across is axial distance. So:

- `distance(Q, field)` is the exact 2D distance from `(Q.across, Q.vertical)` to the profile curve;
- the nearest point keeps `Q.AroundMeters` (as an arc at the floor datum, which is what canonical around is: `angle = around / R`).

There is no curvature approximation anywhere. The only degenerate point is the ring axis itself, which is not reachable.

### 1.4 Query API (pure C#, no scene dependencies)

New folder `scripts/ContainmentField/`, namespace `RingworldTechDemo.ContainmentField`.

```csharp
/// Profile in canonical metres. Built from the live snapshot plus the rim-wall description; never hard-coded.
/// Habitat = across [0, W] (inner wall faces). Field roots may sit outboard of the inner faces once walls have depth (WO12).
public readonly record struct ContainmentFieldProfile(
    double WidthMeters,            // habitat width W (inner face to inner face)
    double WallTopMeters,
    double ApexMeters,
    double RootAcross0,            // field root on the across-0 wall: 0 today; -RimWallDepth (outer edge) after WO12
    double RootAcrossW)            // W today; W + RimWallDepth after WO12
{
    public double HalfWidth => (RootAcrossW - RootAcross0) * 0.5;
    public double Centre => (RootAcross0 + RootAcrossW) * 0.5;
    public double Rise => ApexMeters - WallTopMeters;
    public bool IsValid => WidthMeters > 1000 && RootAcrossW - RootAcross0 >= WidthMeters && Rise > 100 && double.IsFinite(ApexMeters);
    public static ContainmentFieldProfile FromSnapshot(in RingLodGeometrySnapshot s, double widthMeters, in RimWallDescription wall,
        double apexMeters = 40_000);
}

/// The single description of the rim wall cross-section that every consumer (field, POI anchors, later the wall meshes) reads.
/// v1: DepthMeters = 0 (today's zero-thickness planes). WO12 sets it to ~100 and is the only place that changes.
public readonly record struct RimWallDescription(double DepthMeters, RimFieldRoot Root)
{
    public static readonly RimWallDescription Current = new(0.0, RimFieldRoot.OuterEdge);
    /// Across of the wall top's inner edge, centre and outer edge for side 0 (across 0) or 1 (across W).
    public double InnerEdge(int side, double w) => side == 0 ? 0.0 : w;
    public double OuterEdge(int side, double w) => side == 0 ? -DepthMeters : w + DepthMeters;
    public double CentreLine(int side, double w) => side == 0 ? -DepthMeters * 0.5 : w + DepthMeters * 0.5;
}
public enum RimFieldRoot { InnerEdge, CentreLine, OuterEdge }   // with DepthMeters = 0 all three are identical

public readonly record struct ContainmentFieldPoint(
    double AroundMeters, double Phi, double AcrossMeters, double VerticalMeters,
    double NormalVertical, double NormalAcross,   // unit, points INTO the contained volume (down/inward)
    double ArchLengthMeters);                     // from the across-0 wall top along the arch, 0..ArchLength

public static class ContainmentFieldGeometry
{
    public static double HeightAt(in ContainmentFieldProfile p, double acrossMeters);          // NaN outside [RootAcross0, RootAcrossW]
    public static double SlopeAt(in ContainmentFieldProfile p, double acrossMeters);           // dH/dacross, +-inf at the roots
    /// THE cloud ceiling (2.2): min(18 km, H(across) - 300 m); exactly CloudCeiling (18 km) where H - 300 >= 18 km.
    public static double CloudCeilingAt(in ContainmentFieldProfile p, double acrossMeters);
    public static double CloudCeilingFromUnderside(double undersideMeters);                    // same rule for provider consumers
    public static ContainmentFieldPoint SurfaceAtPhi(in ContainmentFieldProfile p, double around, double phi);
    public static ContainmentFieldPoint SurfaceAtArchLength(in ContainmentFieldProfile p, double around, double s); // arc-length LUT, < 1 cm error
    public static double ArchLength(in ContainmentFieldProfile p);                            // total, about 332.6 km
    /// Exact nearest point (meridian-plane ellipse distance; robust bisection, about 60 iterations, double).
    public static ContainmentFieldPoint Nearest(in ContainmentFieldProfile p, in RingCanonicalPosition q);
    /// Signed Euclidean distance: negative inside the contained volume, positive outside (above the field or beyond the walls' tops).
    public static double SignedDistance(in ContainmentFieldProfile p, in RingCanonicalPosition q);
    /// Vertical clearance at q's across: H(across) - q.vertical (gameplay "how far below the field am I"); NaN outside [0,W].
    public static double ClearanceBelow(in ContainmentFieldProfile p, in RingCanonicalPosition q);
    public static bool IsContained(in ContainmentFieldProfile p, in RingCanonicalPosition q);  // Root0<=across<=RootW && vertical < H(across)
                                                                                               // (and above the wall top where across is over a wall)
    /// World segment test (projectiles, flight later). Subdivides a straight world segment into <= 500 m canonical steps,
    /// brackets the sign change of SignedDistance, then bisects to 1 cm.
    public static bool TryIntersectWorldSegment(in ContainmentFieldProfile p, in RingLodGeometrySnapshot s,
        Vector3 worldA, Vector3 worldB, out ContainmentFieldPoint hit, out double t);
}

/// World placement helpers (POIs, debug gizmos, ripple and near patches).
public static class ContainmentFieldPlacement
{
    /// Canonical tangent frame at a surface point: T_around, T_arch (increasing phi), N (into the contained volume).
    public static (Vector3d around, Vector3d arch, Vector3d normal) CanonicalFrame(in ContainmentFieldPoint pt);
    /// World transform of a surface point (plus an optional offset along N), relative to the current floating origin.
    /// Basis: X = around tangent, Y = -N (outward/up out of the habitat), Z = arch tangent. Computed in double from
    /// RingLodGeometrySnapshot.CanonicalToAbsoluteLocal differences, then GridGlobalTransform.Basis.
    public static Transform3D WorldTransform(in RingLodGeometrySnapshot s, in ContainmentFieldPoint pt, double normalOffsetMeters = 0);
    /// Anchor snapshot for meshes built around a field point: like RingLodGeometrySnapshot.CenteredAt, but anchored at the
    /// point's own vertical (CenteredAt pins vertical 0, which would put 10-40 km into every vertex).
    public static RingLodGeometrySnapshot AnchoredAt(in RingLodGeometrySnapshot s, in ContainmentFieldPoint pt);
    /// Rim root line: where the field leaves the wall top (profile root). side 0 = across-0 wall, side 1 = across-W wall.
    public static ContainmentFieldPoint RimRoot(in ContainmentFieldProfile p, int side, double around);
}

/// Wall-top POI anchors (decision 3, WO8). Positions come from the same spacing the hull shader uses (E19).
public readonly record struct RimWallTopAnchor(int Index, int Side, double AroundMeters, double AcrossMeters, double VerticalMeters,
    int CueLine /* k in around = k * spacing, -62..62 */);
public static class RimWallTopAnchors
{
    /// Every longitudinal cue line on both walls, ordered by side then canonical around in [0, C).
    /// Across = wall.CentreLine(side, W) (equals the inner face while DepthMeters = 0); vertical = wall top.
    public static IReadOnlyList<RimWallTopAnchor> Build(in RingLodGeometrySnapshot s, double widthMeters, in RimWallDescription wall,
        double spacingMeters);
    /// Canonical around of cue line k, matching the shader: k * spacing for k >= 0, C + k * spacing for k < 0, |k| <= floor((C/2)/spacing).
    public static double CueLineAround(int k, double spacingMeters, double circumferenceMeters);
}
```

### 1.5 Wall depth stays a parameter

Nothing in the field or anchor API assumes zero wall thickness:
- the profile is defined between `RootAcross0` and `RootAcrossW`;
- anchors read `RimWallDescription`;
- the cloud ceiling is evaluated only over the habitat [0, W] (inner faces), so it does not care where the roots are.

Today `RimWallDescription.Current` has depth 0, so every root and edge collapses onto the existing planes at 0 and W. WO12 changes that one value, plus the actual wall geometry. Tests in WO1 and WO8 run with depth 0 and depth 100 to prove this.

The runtime owner (`RingContainmentField`, section 3) exposes `CurrentProfile`, plus `CameraDistance` and `CameraNearest` for the current frame. Gameplay and POIs use the static API with that profile.

### 1.6 Precision

- All queries are in double.
- The near patch is anchored at the nearest field point, so vertices stay within ±N km (N ≤ 10 km): float ULP < 1 mm.
- Ripple patches are anchored at their centre, with radius ≤ 30 km: ULP ≤ 2 mm.
- Instance origins come from `CanonicalToWorld` relative to the floating-origin anchor (E4).
- A patch drawn 400 km away has an origin magnitude of 4e5 m (ULP 3 cm), which is far below a pixel at that range.

---

## 2. Lore-consistent physics hooks

### 2.1 "Retains atmosphere": does haze need a lid?

The current air model (E6) is the floor shell up to a 20 km cap, bounded laterally by the wall planes **extended upward without limit**. It already "contains" the air above the wall tops: implicitly, the existing model is a box-shaped field. The real field differs only in two slivers. Each runs along a whole rim: between the ellipse and the extended wall plane, below the 20 km cap, within 9.1 km of the wall (8.6 km runtime-surface). Air there is at 10-20 km, at 8-29 % of sea-level density.

- **Rays from the habitat** cross a sliver only near the walls and above the wall tops. Worst-case extra optical depth, rough estimate: green Rayleigh 1.35e-5/m x 0.15 x 9 km ≈ 0.018, about 4/255 on a bright sky. It adds a faint haze band just above the wall-top line.
- **A camera inside a sliver** (flight only) looking along the rim can accumulate about 0.2.

**Decision for v1:** no lid. Ground and POI views change by at most a few code values, in a region nobody stands in, and the existing model is lore-consistent: air stops at the walls and is negligible above 20 km.

**WO9** quantifies this headless. It ports the interval math to C# and compares box vs field-lid opacity over a ray set. If the habitat-ray difference exceeds 2/255, or once flight lands, the fix is:
- clip the air interval at the field, by marching the profile in `ring_atmosphere_path_interval_shared`;
- or add a conservative chamfer plane per rim.

That is one change in the shared include, used by every consumer. It must not be a per-system toggle.

The 20 km cap is below the 40 km apex everywhere except within 9 km of the rims. That is consistent with the lore: the field's job is the edge seal and the thin upper tail (pressure at 40 km is about 0.7 % of sea level).

### 2.2 Clouds stay inside

18 km Cb tops (S6, E7) would poke through the field within 5.8 km of a wall. Wall Engines park permanent storms on the rims (`docs/emitter-grid-climate-machines-spec.md:13`), so this happens in normal play.

The fix is a soft ceiling: `ceiling(across) = min(CloudCeiling, H(across) - 300 m)`, with a 500 m feather. It is multiplied into the density next to `ring_side_wall_mask`:
- `SunshineCloudsCompute.glsl:242-249, 460-462`;
- the CPU twin `RingCloudLayers` / `CloudLayerColumn`, so shadows and the CPU column agree.

The function returns exactly 1.0 when `min(across, W - across) > 7 km` (one compare, no sqrt), so clouds are bit-identical everywhere else. It follows the field automatically, with no toggle. The far cloud tiers skip it: 6 km at 1:1000 is 6 m, which is sub-pixel.

Visually, tops pressing the field near the walls flatten into an anvil-like shelf. This is the "storm contact" disturbance source (section 4).

**One ceiling for every cloud, megastorm included (decision 4).**
- `ContainmentFieldGeometry.CloudCeilingAt` / `CloudCeilingFromUnderside` (1.4) is the only definition: `min(18 km, H - 300 m)`. The 500 m density feather sits under it.
- The field installs `MegastormFieldCoupling.FieldUndersideProvider = across => H(across)`, the true underside (`docs/megastorm-design.md:316`).
- Every consumer turns that underside into a ceiling through `CloudCeilingFromUnderside`, with no private offset:
  - the megastorm's ceiling and its bake clamp (its section 2.2 and WO2);
  - `RingStormProfileMap` bake clamps;
  - the Sunshine density;
  - the CPU column.

  The megastorm doc's `underside - 1 km` (`:66`) is superseded and should be edited to call the shared rule.
- With no field (provider null) the ceiling is 18 km, which is identity with today.
- The megastorm doc's contact width (outer ~30 km per side) assumed 6 km rims and a circular arc. With the real 10 / 10.4 km wall top and the ellipse, `H - 300 m < 18 km` only within **6.2 km** of each root (5.8 km runtime-surface). That is how wide the flattened shelf and the contact band are.
- The megastorm's cirrus-shell note (9.5 km above the field near a rim) does not apply: 9.5 km is below the 10 km wall top. The shared ceiling would cover it anyway.

### 2.3 "Transparent to light and thermal radiation": no lighting change

The field never touches lighting. Its meshes are set to:
- `cast_shadow = OFF`, `gi_mode = DISABLED`;
- unshaded;
- no depth write;
- the `no_precipitation_occlusion` group (E13), so the occlusion camera never sees them.

The cloud sun visibility, terrain height shadow, macro shadows, cloud shadow projector, eclipse and sky shaders do not know about the field and need no change. Thermal radiation has no simulation; it is lore only.

### 2.4 Things that cross the field today

- **Red sprites, blue jets, gigantic jets (E9).** All extend above the 40 km apex, beyond the retained atmosphere. **Decision 2: they stay exactly as they are.** The lore conflict is accepted, and sprite and jet geometry, timing and chance are untouched.
  - Field side only: when an existing discharge column (base and top from `UpperAtmosphereLightning.TryDerive`, read-only) spans the field height at its across, the field shows a ripple at that crossing point. The flash comes from `Level(sprite, t)`.
  - The ripple is drawn in the main camera only, within 400 km. Sprites themselves stay in the far viewport, so the two never need to match pixel-for-pixel.
- **Eclipse hub beams and orbs, emitter beams to the hub.** They pass through, as the field transmits light. A future optional source: an orb transit pulses a small ripple (section 4, Kind `Scripted`).
- **Emitter-grid chords.** At 4-6 km mid-span they never reach the field (`docs/emitter-grid-climate-machines-spec.md:25`).

---

## 3. Rendering

### 3.1 States and cost

| State | Condition | Draws | GPU |
|---|---|---|---|
| Idle | camera distance to field > shimmer range x 1.1, no live ripple, or both strengths 0 | none (nodes `Visible = false`) | 0, pixel identity |
| Near | camera within shimmer range | near patch: 1 draw, 48x48 quads | ≤ 0.20 ms at 1080p on the 4070 (≤ 0.45 ms A1000), including the screen copy if water or rain has not already paid for it |
| Ripples | 1..16 live disturbances within 400 km | 1 draw per live ripple patch, 32x32 quads | ≤ 0.15 ms worst case (16 overlapping) |
| Cloud ceiling (always) | Sunshine density | n/a | ≤ +0.03 ms GPU.Clouds (A-B-A) |

CPU per frame:
- one `Nearest` call (about 2 µs);
- adapter polling over at most a few dozen live lightning events;
- per live ripple, one transform and four instance uniforms;
- total ≤ 0.05 ms.

A patch rebuild costs ≤ 0.5 ms on the main thread. The near patch rebuilds at most about 1/s while moving, and only when the nearest point moves more than N/4. A ripple patch builds once per spawn.

### 3.2 Why meshes plus a screen texture, not a CompositorEffect

- **Depth comes free.** The transparent pass depth-tests against the opaque scene: wall tops, the future POI, terrain. Reversed-Z, the ring-scale far plane and MSAA are handled by the engine. A full-screen compute pass would need its own analytic ray-ellipsoid-of-revolution intersection per pixel, depth linearisation and a colour copy, and would cost something every frame it is enabled.
- **Cost scales with covered pixels.** It is exactly 0 when nothing is visible. That meets "concentrate effects near the player or active disturbances" directly.
- **Ordering is right for the common case (below the field).** Opaque scene plus sky plus far backdrop (`POST_SKY`), then the field (transparent pass), then Sunshine clouds (`POST_TRANSPARENT`, E10), then rain streaks and rain haze. Clouds in front of the field, which for any viewer below it means all of them, correctly draw over the field and are not refracted. Rain haze dims the field like everything else.
- **The screen copy is shared.** Water and lens droplets already use `hint_screen_texture` (E11), so the copy is often already paid.

### 3.3 Near patch

- Owner node: `RingContainmentField` (`scripts/ContainmentField/RingContainmentField.cs`). It is a sibling of the weather nodes in `scenes/Main.tscn` and `scenes/VoxelRingIntegrationTest.tscn`, and binds `../ChunkGridManager`, the camera, `../VolumetricCloudLayer` (or whatever the weather node path is) and the settings.
- Each frame:
  - `camCanon = grid.WorldToCanonicalRingPosition64(camera.GlobalPosition)` (`VoxelChunkGridManager.cs:1504`);
  - `nearest = Nearest(profile, camCanon)`;
  - `d = |SignedDistance|`.
- Visible iff `d < N * 1.1`, where N is the shimmer range (default 3 km).
- Mesh: a grid in `(around, arch length)` centred on `nearest`, spanning ±N in both directions, with arch length clamped to [0, ArchLength] so the field ends at the wall tops.
  - Built with `SurfaceAtArchLength`, relative to `AnchoredAt(nearest)`.
  - The instance transform origin is `snapshot.CanonicalToWorld(nearest)`, recomputed each frame, so floating-origin shifts move it.
  - `custom_aabb` is set to the patch bounds.
- Vertex attributes:
  - `UV` = patch-local (Δaround m, Δarch m), the noise domain, so the noise is isotropic in surface metres;
  - `UV2.x` = arch length from the nearer rim root (m), for the root ramp;
  - `NORMAL` = field normal.
- Rebuild when `nearest` moves more than N/4 along the surface, or when the profile changes (world reload).
- Activation never pops: per-pixel weight is 0 for fragments at distance ≥ N (3.4), so the patch appears and disappears on fully transparent pixels.
- On the ground the camera is ≥ 10 km from the field (the wall top is 10 km). The patch never draws in normal exploration. It appears on the wall top, near the future POI, on mountain tops right at the rim if N is raised, and in flight.

### 3.4 Shader `materials/ring_containment_field.gdshader` (shared by both patch kinds)

```
render_mode unshaded, cull_disabled, depth_draw_never, blend_premul_alpha, shadows_disabled, fog_disabled;  // normal MODELVIEW transform
// premultiplied: out = ALBEDO + dst * (1 - ALPHA). Refraction writes ALBEDO = refracted * a, ALPHA = a; strike light (3.10) adds
// to ALBEDO without touching ALPHA, so it is purely additive and exactly 0 when no flash is lit.
uniform sampler2D cf_screen : hint_screen_texture, filter_linear, repeat_disable;
uniform sampler2D cf_depth  : hint_depth_texture, filter_nearest, repeat_disable;
#include "res://materials/ring_atmosphere.gdshaderinc"   // ring_haze_sample, ray frame from RingPoiHaze
uniform float cf_time;                 // weather clock (CloudSimulationTimeSeconds) -> paused clocks freeze the field
uniform float cf_shimmer_strength;     // menu, 0..2 (0 -> patch hidden on CPU anyway)
uniform float cf_shimmer_range_m;      // menu
uniform float cf_ripple_strength;      // menu
uniform float cf_max_offset_px = 3.0;  // constant cap, not a tunable
uniform int   cf_debug_mode = 0;       // CLI/diagnostics only
instance uniform vec4 cf_ripple_a;     // (age s, duration s, max radius m, energy 0..1)  ripple patches only; near patch = 0
instance uniform vec4 cf_ripple_b;     // (flash level 0..1, flash coupling, occluder top m, occluder radius m)
instance uniform vec4 cf_ripple_c;     // occluder centre, camera-relative world xyz; w = kind
instance uniform vec4 cf_strike;       // FieldStrike only (3.10): (crawl radius m, crawl age s, shape seed, rim-bias sign/strength); 0 = none
instance uniform vec4 cf_occluder_band;// band occluder for the megastorm wall: (trail around m, lead around m, top m, enabled) relative to anchor
```

Per fragment:

1. **Weight.**
   - Near patch: `w = shimmer_strength * (1 - smoothstep(0.35 N, N, dist_cam_frag))`.
   - Ripple patch: `w = ripple_strength * A(τ) * ring(s, τ)` (3.5).
2. **Soft intersection.** Read raw depth and linearise exactly as `ring_ocean_proxy.gdshader:64-69` (reversed-Z; raw ≤ 0 is sky, treated as infinite). Multiply by `smoothstep(0, 30 m, scene_view_depth - frag_view_depth)`. This kills hard seams where the field meets the wall top, the POI or terrain.
3. **Root ramp.** `smoothstep(0, 250 m, UV2.x)`, so the field grows out of the top rail instead of starting on a line.
4. **Grazing.** `smoothstep(0.03, 0.15, |dot(N, V)|)`, so an edge-on sheet near the rim never produces a line. The offset is also clamped to `cf_max_offset_px`.
5. **Haze.** `w *= ring_haze_visibility(ring_haze_sample(cam, frag, centre, day))`, the same regular haze every surface uses (E12). Distant ripples low on the horizon vanish into the air.
6. **Shimmer normal.**
   - Two octaves of gradient noise in surface metres (wavelengths about 180 m and 45 m), advected slowly (≤ 0.3 m/s).
   - The temporal frequency is kept low so TAA/FSR history stays stable.
   - Isotropic, with no axis-aligned terms: no grid.
7. **Refraction.** `uv' = SCREEN_UV + (normal_view.xy * w) * px_to_uv`, with magnitude ≤ `cf_max_offset_px`. With `a = clamp(w * 4.0, 0, 1)`, the output is `ALBEDO = texture(cf_screen, uv').rgb * a`, `ALPHA = a` (premultiplied).
8. **Reveal light (ripples only).** `ALBEDO += T * (tint * A * g^2 * flash * coupling * reveal_gain)`, where `tint ≈ (0.55, 0.65, 1.0)`, `g` is the ring slope and `T` is the haze transmittance from step 5. The term is 0 whenever `flash` is 0, so there is **no emission without a live, lit event**. The near patch has no light term at all.
9. **Strike light (FieldStrike only).** Contact glow plus crawl filaments (3.10), added to `ALBEDO` the same way.
10. **Identity.** `discard` when `w < 1/1024` and the light terms are below 1/4096. Below that, premultiplied blending of zero is exact anyway.

There is no fresnel brightening, no rim line and no base colour. "Shimmer" is only the distortion of whatever is behind the field: the wall, the arch, clouds, stars. Against a uniform sky it is invisible, which is the point.

### 3.5 Ripples

- **Patch.** A disc of radius `1.15 Rmax` in surface metres around the projected disturbance point. It is built once at spawn with `SurfaceAtArchLength` (exact across the rim bend) and anchored like the near patch. Pool: 16 `MeshInstance3D`, one shared `ShaderMaterial`, instance uniforms per ripple.
- **Analytic ring.** `s` is the surface distance from the centre (`length(UV)`) and `τ` the age.

  ```
  ρ(τ) = Rmax * (1 - exp(-3τ/T))                     front radius, decelerating
  A(τ) = E * smoothstep(0, 0.08 s, τ) * (1 - τ/T)^2   envelope, exactly 0 at τ >= T
  f(s) = cos(2π (s - ρ) / λ) * exp(-((s - ρ)/ω)^2)    λ = 0.18 Rmax, ω = 0.12 Rmax (about 1.5 visible crests)
  g    = ∂f/∂s  (analytic)                            slope -> refraction along the radial surface direction
  ```

  The ring is centrally symmetric with a Gaussian edge, so the patch boundary is always at zero amplitude.
- **Own-storm occlusion (far tier).** Clouds beyond the 36-48 km Sunshine handoff are in the sky backdrop. They are drawn before the field, so a ripple behind its own storm tower would wrongly draw over it. Each ripple therefore carries its source storm's footprint (`cf_ripple_b.zw`, `cf_ripple_c`). The shader finds where the view ray crosses the occluder-top altitude between the camera and the fragment. If that crossing is inside the footprint, it attenuates by 0.85. This is one analytic intersection, and it handles the dominant case.
- **Kinds.** Initial values, tuned in WO4:

  | Kind | Rmax | T | Flash source |
  |---|---|---|---|
  | Lightning (coupled, near rims) | 3-8 km (∝ E) | 2.5 s | parent `RingLightningEvent.PulseAtAge`, reduced-flash aware |
  | Sprite / jet crossing (existing discharges, read-only) | 8-20 km | 4 s | `UpperAtmosphereLightning.Level` |
  | Storm contact (tops pressing the field) | 4-10 km | 3 s | none: refraction only (visible against cloud tops and stars) |
  | Updraft burst (megastorm overshooting top) | the burst radius, 4-12 km | 3 s per pulse, repeated while the burst lives (120-600 s) | none |
  | Megastorm contact band | 6-15 km (the band is only about 6 km wide) | 4 s | megastorm flash-field pulse where one coincides; else none |
  | **FieldStrike, channel** (3.10) | 6-15 km (∝ E) | 3 s; the crawl and glow last ≤ 1 s | the strike's own pulse |
  | **FieldStrike, contact arc** (3.10) | 1-2 km | 0.8 s; the crawl only, ≤ 1.5 km | the arc's own pulse |
  | Scripted / impact / future | caller-given | caller-given | caller-given |

- **Budget.** Superseded by the strike-aware budget in 3.10.4. That section keeps the 16-patch pool and the 6/s global cap and adds per-class reservations, restrike merging and a separate far list.
- **Photosensitivity.** The flash term uses the source's own pulse, which already honours `LightningVisualPolicy.ReducedFlashes` and its summed cap (E8). No new flash source is introduced.

### 3.6 Depth, reversed-Z, precision

- The engine does the depth test.
- The soft-intersection read uses the ocean linearisation (E11), with raw depth ≤ 0 treated as sky. It is correct under reversed-Z: ndc z = raw depth through `INV_PROJECTION_MATRIX`.
- Patch vertices are anchor-relative, so there are no large magnitudes in vertex data (1.6).

### 3.7 Draw order with the other passes

| Pass | Relation to the field | Correct? |
|---|---|---|
| Opaque world, sky, far backdrop (`POST_SKY`), far viewport | Behind the field, so refracted | Yes (the arch, wall, stars) |
| Water (transparent, also uses the screen copy) | Sorted by distance; both read the same pre-transparent copy | Yes from below. Water never sees the field; acceptable |
| Sunshine clouds (`POST_TRANSPARENT`) | Composited over the field; the field writes no depth, so clouds march to the opaque depth | Yes for viewers below the field. From above (flight later), clouds behind the field overwrite its shimmer. **Accepted v1 limitation**; when flight lands, the fix is a POST_TRANSPARENT field pass ordered after Sunshine |
| Far-tier clouds (in backdrop) | Drawn before the field | Mostly yes. The own-storm occlusion term (3.5) covers ripples behind their storm |
| Rain streaks, rain haze (last), rainbow | After the field | Yes: rain and rain haze dim it |
| SSR / water reflections | The field is absent (transparent) | Accepted |
| TAA / FSR | Transparents write no motion vectors | Low temporal frequency; WO3 checks for smear with FSR on and off |

### 3.8 View cases

- **From the ground (10-40 km below).** Nothing (Idle) unless a ripple is live. Lightning ripples happen only near the rims (coupling, section 4); sprite and jet ripples anywhere. They are seen above the storm from outside it and hazed by the regular air. Under the storm the deck hides them, which is correct.
- **Wall top and the future POI (0-3 km from the field).** The near shimmer distorts the wall-top rail, the arch and clouds beyond. A root ramp; no line.
- **Grazing near the rim walls.** The field there is a vertical sheet over the wall. Looking along the circumference from the wall top, the grazing fade plus the pixel cap keep it to a faint wobble of the horizon and arch. Acceptance requires no visible line in a 10 s flicker capture.
- **High altitude (35-40 km) and above (flight later).** Same patch, double-sided. Above the field, see the cloud ordering limitation in 3.7.
- **Far / arch scale.**
  - Refraction and ripples: nothing. At 1:1000, 40 km is 40 m.
  - **Field strikes are the one exception.** Their contact glow and crawl draw as emissive billboards in the far viewport (3.10.3) beyond 350-400 km, so strikes are readable across the arch at night. Nothing else of the field is ever drawn far.

### 3.9 Perf acceptance numbers

Measured with the `perf-capture` skill, A-B-A, on the 4070. A1000 numbers are informational: goldens and GPU ms are machine-specific.

| Pose | Gate |
|---|---|
| Idle (ground, field on vs `--containment-field=off`) | Δ within the noise floor; draw count identical |
| Near (wall top, N = 3 km) | ≤ 0.20 ms GPU, ≤ 0.05 ms CPU |
| 16 forced overlapping ripples (night, 60 km) | ≤ 0.15 ms GPU |
| Cloud ceiling (standard 15 / 40 km poses) | GPU.Clouds Δ ≤ 0.03 ms |
| Busy megastorm at night, 100 km ahead of the wall, pool full with strikes in their flash phase (3.10.5) | ≤ 0.25 ms GPU for all field patches; far strike layer ≤ 0.02 ms GPU; field CPU ≤ 0.08 ms |

### 3.10 Field strikes: lightning that touches the field (user request and decisions 2026-10-29)

**Lore.** *The field takes the lightning.* Charge that would otherwise bleed off the tops of the tallest storms lands on the canopy. The field spreads it across its skin, sheds it into the rim walls' emitter rail and is quiet again within a second. That is why a strike crawls outward and dies, and why the field never holds a glow.

**Sources** (4.2):
- **Megastorm field strikes** (`docs/megastorm-design.md` 5.5.5). Upward channels from the wall's 15-18 km tops end 50 m below the field, anywhere across the width (channels up to about 22 km long at the centre). About 0.3/s for the whole wall. Its near-rim **contact arcs** publish no separate disturbance; they drive the `FlashLevel` of `MegastormContact` ripples (megastorm doc 5.5.5).
- **Natural near-wall field strikes** (decision 6, 3.10.6). They are rare and confined to within about 10 km of a wall.

Both publish `ContainmentDisturbanceKind.FieldStrike` with:
- `Position` = the contact point;
- `Energy`;
- `FlashLevel` = the strike's own pulse;
- `SourceId` = the lightning EventId.

The megastorm draws the channels. The field draws only what happens **on** the field.

#### 3.10.1 The response, evaluated

| Idea | Verdict | Why |
|---|---|---|
| Ripple ring from the contact point | **Yes** | It reuses 3.5 unchanged. Rmax 6-15 km ∝ E, T 3 s. It is the after-image that outlives the flash, and it is pure refraction, so it gives no glow once the flash ends |
| "Crawl": filaments spreading across the surface from the contact | **Yes, the signature** | It shows the canopy as a surface, which a bolt alone cannot. It lives only while the flash is lit (≤ 1 s) |
| Faint glow at the contact point | **Yes, on the field only** | A small core plus halo at the contact, flash-gated |
| The contact glow lighting the cloud tops below | **No new light** | The channel's own `RingLightningEvent` flash already lights the cloud through the existing flash consumers (Sunshine in-cloud flash, far cloud flashes, the megastorm's dome-top glow). Near the rims, where natural strikes happen, the gap is 0.3-3 km, so that flash sits right under the contact. At the centre the gap is about 22 km, and light from the contact would add almost nothing to tops that far below. A Light3D or a cloud-lighting input per strike would cost shadow and cloud work for an invisible gain |

#### 3.10.2 Shader: crawl and glow on the ripple patch

A strike uses one ripple-pool patch, radius `1.15 * max(Rmax, Rc)`. The ring (3.5) and the strike light share the patch. `cf_strike = (Rc, crawl age, seed, rim bias)`, and the light is added premultiplied (3.4 steps 9-10).

All terms are evaluated in patch-local surface polar coordinates `(s, θ)` about the contact.

- **Crawl radius.** `Rc = 2 + 4E` km. The front advances as `r_f(t) = Rc (1 - exp(-t / 0.12 s))`, so the crawl spreads over about 0.3 s. Reduced flashes uses 0.24 s, half the speed.
- **Branches.** 5-9 primary branches from `hash(seed)`:
  - angles `θ_i = 2π(i + u_i)/n`;
  - an angular wiggle `0.35 * noise(s / 600 m + seed_i) / max(s / 1 km, 0.5)`;
  - one fork per branch at 30-60 % of its length, ±20-35°;
  - length `L_i = (0.6-1.0) Rc`.

  Shapes are fixed per strike and only the front and intensity animate, which keeps TAA stable.
- **Rim bias.** The branch angles lean up to 25° toward the arch direction of the nearer root, and those branches are 1.4x longer. Charge "drains to the walls". Each strike's bias is seeded, so there are no straight lines and no lattice: no grid.
- **Filament profile.** `I = exp(-(δ / w)^2) * (25 m / w)`, with δ the lateral distance to the branch.
  - `w = max(25 m, 1.25 * pixel footprint)`, with the footprint from `fwidth` of the surface coordinates.
  - The width is pixel-aware and energy-preserving, so a crawl 40 km up neither sparkles (sub-pixel aliasing) nor vanishes.
- **Intensity.** `E * flash(t) * smoothstep(r_f, r_f - 200 m, s) * (1 - s / L_i)^1.5`.
- **Contact glow.** `E * flash(t) * (exp(-(s / r_core)^2) + 0.25 exp(-(s / r_halo)^2))`, with `r_core` 300-600 m and `r_halo` 2-3 km.
- **Colour.** Violet-white, `(0.75, 0.80, 1.0)`, matching the megastorm's terminal spray. Multiplied by the haze transmittance `T` and by `cf_ripple_strength`. The menu slider's tooltip names strikes; there is no new slider.
- **No permanent glow.**
  - Every light term carries `flash(t)`, the source pulse (`PulseAtAge` maths). That pulse is reduced-flash aware and inside the summed flash cap (E8).
  - The crawl and glow are gone by the end of the pulse (≤ 1 s, or ≤ 1.9 s for megastorm re-pulses). Only refraction remains until T, and then nothing.
  - The field never gets a light term without a lit pulse.
- **Contact-band ripples** (`MegastormContact`) use the same crawl code with `Rc = 0.5-1.5 km` while their arc-driven `FlashLevel` is above 0. That is how the megastorm's frequent contact arcs read on the field without a patch each.
- **Band occluder.** The megastorm wall is a band, not a disc, so own-storm occlusion (3.5) gains a band form, `cf_occluder_band`.
  - Where the view ray crosses the wall-top altitude inside `[trail, lead]` around the wall, the strike is attenuated by 0.85.
  - This matters for far-tier clouds only; Sunshine already composites over the field.
  - Strikes seen from ahead of the wall mostly pass above the wall top and are not attenuated.

#### 3.10.3 Visibility: ground, distance and across the arch

- **The near patch is irrelevant here.** It only exists within the shimmer range (≤ 10 km), and a ground viewer is 18-40 km from the field.
- **Strikes within 400 km use the ripple pool**, which already renders anywhere within 400 km:
  - patches are anchored at the contact (1.6 precision: ULP ≤ 3 cm at 400 km);
  - they are depth-tested against the opaque world;
  - they are hazed by the regular air, with Sunshine clouds composited over them.

  From the ground at 30-200 km a strike is a flat, spreading flare in empty sky above the storm. At 300-400 km the arch's rise puts it 5-10° above the horizon, heavily hazed.
- **Beyond 350-400 km: a far strike layer.** New `RingContainmentFarStrikeLayer`, in the scaled far viewport.
  - It follows the `RingUpperAtmosphereLightningLayer` pattern: a MultiMesh in the 1:1000 world, ignore-culling, per-instance air dimming through `UpperAtmosphereLightning.PathTransmittance`.
  - It draws **emissive quads lying in the field's tangent plane** at the contact, so the flare reads as a mark on the canopy, not a sprite. Size is `2 Rc`, with a minimum angular size of 0.3° (matching the megastorm's far channel set).
  - Content is the same crawl and glow functions from a shared `materials/ring_containment_strike.gdshaderinc`, at reduced branch detail, additive. No refraction.
  - Main-view patches fade `smoothstep(400, 350 km)` and far quads fade `smoothstep(350, 400 km)` by camera distance, so there is no pop at the handoff.
  - Strikes are visible out to the antipode at night as short violet flares where megastorm channels stop. This answers the megastorm doc's request (5.5.5 "the reveal").
  - Ripples are never drawn far (refraction is invisible there).
- **Day.** Strikes are faint against a bright sky, and ripples barely show. That is correct for "revealed briefly". Acceptance is judged at night and dusk.

#### 3.10.4 Pool budget, with field strikes

Pool: 16 patches. Global spawn cap: 6/s.

| Class | Spawn cap | Live share | Notes |
|---|---|---|---|
| FieldStrike (all sources, within 400 km) | 2/s | **4 reserved** (and may use free shared slots) | Restrike merge (below) |
| Natural FieldStrike | ≤ 1/s of the 2/s | inside the 4 | per system ≤ 1 per 20 s (3.10.6) |
| Megastorm contact band | 4/s megastorm total (with bursts) | ≤ 6 live | crawl from arc flashes, no patches of their own |
| Megastorm updraft bursts | (in the 4/s) | ≤ 3 live | |
| Lightning, sprite/jet crossings, storm contact, other | rest of 6/s | shared | |

- **Restrike merge.** A new FieldStrike within the crawl radius of a live strike patch re-triggers that patch: energy max, a new crawl seed, the flash = max of the two pulses. It does not take a new slot. This is also the "same channel re-strikes" look.
- **Other merges.** As 3.5: same kind within 0.25 s and closer than Rmax.
- **Eviction.**
  - Never evict a patch whose flash is above 0, unless the newcomer is a FieldStrike with higher E.
  - Otherwise evict the lowest `E * (1 - τ/T) / (1 + dist_cam / 100 km)`, so ripple tails go first.
- **Far list.** FieldStrikes beyond 350 km go to a separate far list: 32 entries, each living as long as its pulse (≤ 2 s), drawn by the far layer. They never use pool slots. Overflow drops the oldest.
- **Busy-megastorm steady state** (within 400 km, at 350 m/s):

  | Source | Live patches |
  |---|---|
  | contact | 6 |
  | bursts | 2-3 |
  | field strikes | 1-2 (megastorm 0.3/s wall-wide gives ≤ 0.3/s near) |
  | coupled lightning | 1-2 |
  | sprite/jet crossings | about 1 |
  | **Total** | **11-14, within 16** |

  The far list holds about 0.6 live entries on average (0.3/s x about 2 s).

#### 3.10.5 Cost

- **Crawl evaluation.** At most 9 branches plus 9 forks per fragment. It runs only on patches with `cf_strike.x > 0` and flash above 0, an instance-uniform branch, so it is coherent and costs 0 for ripple tails and every other kind. It is about 2-3x the plain ripple per fragment, but only for about 1 s per strike.
- **Busy megastorm gate.** ≤ 0.25 ms GPU (4070, 1080p) for all field patches, including strikes in their flash phase.
- **Far strike layer.** ≤ 32 billboards, ≤ 0.02 ms GPU. CPU ≤ 0.03 ms for `PathTransmittance` (96 samples x ≤ 32 instances).
- **CPU.** The adapters are O(active events). Natural field-strike gating is one hash plus one `HeightAt` per natural lightning event.

#### 3.10.6 Ordinary storms striking the field (decision 6: "yes ordinary storms can do this if close to a wall")

Natural field strikes are made by the **natural lightning scheduler** (`RingVolumetricCloudLayer.Lightning.cs`, `CreateLightningEvent` `:381-384`). Each one is an ordinary natural `RingLightningEvent` that a deterministic roll turns into an upward field strike, using the megastorm's `FieldStrike` flag on `RingLightningEvent`. No second scheduler or clock exists.

The field side reads the event through `LightningDisturbanceAdapter`, which publishes `FieldStrike` instead of `Lightning` for flagged events. The channel is drawn by the megastorm's source-agnostic `RingFieldStrikeLayer`, which draws every flagged event in `ActiveLightningEvents` whatever its kind; natural channels of 0.3-3 km get a scaled-down fan (megastorm doc 5.5.5 and WO13). Natural flagged events also use the per-event `DurationSeconds` and the field-strike pulse envelope (4.2).

**Gating.** All of these must hold, evaluated at the event's across with the live field provider:

1. **A field exists.** The field provider is installed; with none (provider null) there are no strikes. Natural storms read a field-owned `ContainmentFieldProvider.Underside`, the same function the field installs as the megastorm's `FieldUndersideProvider`.
2. **Storm type and strength.**
   - The parent render system is a storm tower: Cb or StormTower physical profile, or `Sculpts`.
   - It is not the megastorm: the megastorm has its own stream (kind 6 excluded).
   - It is strong: `SystemKind >= ExceptionalFront (4)`, **or** `Precipitation >= 0.6` with a mature lifecycle.
3. **Tall top near the field.** `top = StormCeilingMeters >= 15 km`. The gap `g = H(across) - top` (with tops already capped at `H - 300 m`, 2.2) must satisfy `0.3 km <= g <= 3 km`.
4. **Close to a wall.** `d_w = min(across - RootAcross0, RootAcrossW - across) <= 12 km` (hard cutoff).
5. **Roll.**

   ```
   p = 0.04 * strength * exp(-(g - 0.3 km) / 0.75 km) * (1 - smoothstep(6 km, 12 km, d_w))
   strength = clamp(Precipitation, 0, 1) * (SystemKind >= 4 ? 1.0 : 0.6)
   strike  <=> Unit(EventId, salt 0x5F1E) < p
   ```

   - p is 0.04 at the best spot and falls by e for every 0.75 km of gap.
   - The field rises fast away from the wall (ellipse, 1.2), so g grows quickly too. In practice strikes are confined to about 4-10 km from a wall.
   - At the habitat centre g ≈ 22 km and d_w = 159 km, so p = 0 exactly.
6. **Cooldown.** At most one natural field strike per system per 20 s weather-clock bucket: the first event in the bucket that passes the roll. This is deterministic because events are a pure function of (system, time).

**Rate and look.**
- A mature near-wall Cb with about 0.1-0.3 natural strikes/s gives roughly one field strike every 2-8 minutes, against about 1/3 s along a megastorm wall. That keeps it special.
- Wall Engine parked storms (`docs/emitter-grid-climate-machines-spec.md:13`) are the usual hosts, which fits the lore that the machines feed charge into the wall.
- "Lightning frequency" scales the parent events, so it scales field strikes too. There is no new setting.
- The channel is 0.3-3 km long. Rc is 2-4 km (E 0.45-1.0 from the event). The contact glow and crawl are as in 3.10.2.

**Determinism and caps.** The roll is a pure hash of EventId, and the cooldown is computed from the schedule. Paused clocks and rewinds reproduce the same strikes. They go through the same bus, pool and caps (3.10.4: ≤ 1/s natural, inside the FieldStrike share).

---

## 4. Disturbance interface and sources

### 4.1 Contract

```csharp
public enum ContainmentDisturbanceKind { Lightning = 1, UpperAtmosphere = 2, StormContact = 3, MegastormContact = 4, Impact = 5, Scripted = 6, UpdraftBurst = 7,
    FieldStrike = 8 }   // lightning that terminates on the field (3.10): Position = contact point, SourceId = lightning EventId

/// One transient disturbance. Position is the SOURCE point; the bus projects it onto the field (Nearest).
/// Time is the weather clock (RingVolumetricCloudLayer.CloudSimulationTimeSeconds) so paused clocks and scrubbing are deterministic.
public readonly record struct ContainmentDisturbance(
    long SourceId,                       // dedupe key: lightning EventId, sprite EventId ^ salt, hash(systemId, time bucket), ...
    ContainmentDisturbanceKind Kind,
    RingCanonicalPosition Position,
    double StartTimeSeconds,
    float Energy,                        // 0..1 after coupling
    float RadiusMeters,                  // Rmax on the surface
    float DurationSeconds,               // T
    float FlashCoupling,                 // 0..1: share of the source's flash that lights the ripple (0 = refraction only)
    Func<double, float>? FlashLevel = null,          // flash 0..1 at weather time t; null = 0
    RingCanonicalPosition? OccluderCenter = null,    // source storm footprint for own-storm occlusion
    float OccluderRadiusMeters = 0, float OccluderTopMeters = 0);

public interface IContainmentDisturbanceSink { bool Publish(in ContainmentDisturbance d); }   // false = rejected (dedupe/budget/out of range)
```

`ContainmentDisturbanceBus` is pure C#. It holds the pool, budget, merge and eviction (3.5). Any system publishes with `RingContainmentField.Disturbances.Publish(...)` on the main thread during `_Process`. Results are deterministic for a given input sequence and clock.

### 4.2 Sources (adapters in `scripts/ContainmentField/Sources/`)

- **Lightning.** `LightningDisturbanceAdapter` polls `ActiveLightningEvents` each frame and dedupes by EventId.
  - Coupling uses the parent system's top, not `CloudPosition` (that is the cloud base, E8): `top = StormCeilingMeters` of the system from `CollectRenderWeatherSystems` (`RingVolumetricCloudLayer.cs:3890`), with `CloudTopMeters` as fallback.
  - `gap = H(across) - top`, `E = e.Energy * exp(-max(gap, 0) / 2 km)`, and the event publishes only if E ≥ 0.1.
  - Result: only near-rim storms couple, which is "extreme storm activity".
  - FlashLevel is the event's own pulse over the event's own `DurationSeconds`. That is `RingLightningEvent.PulseAtAge(t - start)` for ordinary events (0.72 s). Flagged field strikes use the field-strike envelope with re-pulses, reduced-flash aware, over 0.8-1.9 s. Both are added in megastorm WO13, and pruning follows `DurationSeconds` (`RingLightningEvent.cs:19`, `RingVolumetricCloudLayer.Lightning.cs:146`).
  - **Field strikes.** An event carrying the `FieldStrike` flag publishes `FieldStrike` instead of `Lightning`, with no gap attenuation: the strike *is* contact. That covers natural near-wall strikes (3.10.6) and megastorm strikes if they appear in `ActiveLightningEvents`.
    - Position is the event's contact point. Agreed convention: for flagged events `GroundPosition` = the field contact point and `CloudPosition` = the channel base (tower or dome top).
    - E is the event Energy. SourceId is the EventId, so a megastorm-published duplicate is dropped by the bus.
- **Sprites and jets (read-only, decision 2).** `UpperAtmosphereDisturbanceAdapter` calls `UpperAtmosphereLightning.TryDerive` with the same inputs and chance roll as `RingUpperAtmosphereLightningLayer` (`:99-114`), so a ripple appears exactly when a discharge does. It never modifies the sprite.
  - The column spans `[base, base + Height]` (base = cloud top for jets, cloud top + 55 km for sprites).
  - If that interval contains `H(across)`, it publishes at `(around, H(across), across)`, with SourceId = `EventId ^ salt`. Red sprites (base about 61 km, above the apex) never cross, so only jets do.
  - FlashLevel is `Level(sprite, t)`; Energy is the sprite energy. It works at any across.
- **Storm contact.** `StormContactAdapter`:
  - For each render system with `top ≥ H(across) - 500 m` anywhere in its footprint, it computes the contact point: the across nearest the rim where top meets H.
  - At a deterministic cadence of 0.7-1.5 s (hash of SystemId and the time bucket) it publishes a refraction-only ripple there. E scales with penetration.
  - SourceId = hash(SystemId, bucket), so a paused clock gives identical frames.
  - It skips kind-6 (megastorm) records, so the wall is never published twice (megastorm doc 6.3).
- **Megastorm (reconciled with `docs/megastorm-design.md` sections 5.5.5 and 6.3).**
  - **Direction and attenuation.** The field pulls everything except field strikes.
    - Field strikes reach the field **only** through `LightningDisturbanceAdapter`. The megastorm adds every strike within reach to `ActiveLightningEvents` with the `FieldStrike` flag, and has dropped its direct push. SourceId = EventId; the bus still dedupes by SourceId.
    - Contact arcs publish nothing of their own; they set the `MegastormContact` FlashLevel, which drives the small contact crawl (3.10.2).
    - `MegastormDisturbanceAdapter` calls `MegastormFieldCoupling.QueryDisturbances(tPrev, t)` and `QueryContact(t)` each frame, and maps the results onto `ContainmentDisturbance`.
    - The field owns the gap attenuation: `gap = H(across) - TopAltitudeMeters`, `E = Strength * exp(-max(gap, 0) / 2 km)`, publish if E ≥ 0.1.
  - **Mapping:**

    | Megastorm hook | Field kind | Rule |
    |---|---|---|
    | `UpdraftBurst` (radius 4-12 km, 120-600 s, top 18 km or ceiling, drifting) | `UpdraftBurst` | While alive, re-publish a 3 s pulse every 0.7-1.5 s (hash of Id and bucket) at the drifted position `around + Drift * (t - start)`. SourceId = hash(Id, bucket). Couples only near the rims: at the centre the gap is about 22 km, giving E ≈ 0 |
    | `Lightning` (top 5 % of flash-field pulses, near strikes E > 0.8) | `Lightning` | Same as natural lightning. Coupling uses the megastorm top, not the strike's cloud position. FlashLevel is the megastorm pulse (`PulseAtAge` maths, reduced-flash aware) |
    | `UpperDischarge` (sprite and jet columns; ids match the sprite layer) | `UpperAtmosphere` | Same read-only crossing rule as natural sprites. **SourceId is shared with `UpperAtmosphereDisturbanceAdapter`**, so the bus dedupes and one discharge never ripples twice |
    | `QueryContact` (`Active`, Lead/Trail around, `AcrossInnerMeters`, `Pressure`, `Direction`, `Speed`) | `MegastormContact` | Only when `Active`. Per side and per 0.5 s bucket, publish one ripple at a hashed around in [Trail, Lead] within 400 km of the camera, at the across where the shared ceiling meets the wall top (inside `|across - W/2| > AcrossInnerMeters`). E = Pressure. At most 4/s megastorm total (3.5) |
  - **Provider.** The field installs `FieldUndersideProvider` (2.2), so the megastorm's ceiling, contact band and the ripples all derive from the same H.
  - **Speed 350 m/s (user decision).**
    - Ripples are fixed to the field and the wall moves 1.4 km during a 4 s contact ripple, so contact ripples leave a short wake behind the leading edge. That is intended.
    - The contact band passes a fixed observer quickly, so the adapter keys its 0.5 s buckets to absolute time, not to wall position: the cadence does not depend on speed.
    - Burst drift uses `DriftAroundMetersPerSecond` as published, so no field-side speed constant exists.
  - **Megastorm WO8** (`MegastormFieldEventsTest`) and this doc's WO2 share one fixture: the same Ids give the same ripples.
  - **Bursts** use kind `UpdraftBurst` (= 7) on both sides (megastorm doc 6.3, aligned 2026-10-29).
  - **Shared field-strike pieces** (agreed with the megastorm author; natural strikes, 3.10.6, need them):
    - the `FieldStrike` flag on `RingLightningEvent`, set by the natural scheduler too;
    - contact point convention: `GroundPosition` = contact, `CloudPosition` = channel base;
    - per-event `DurationSeconds` (default 0.72 s, so unflagged events and the fingerprint are unchanged) and pruning by it, plus a field-strike pulse envelope with re-pulses;
    - `RingFieldStrikeLayer`, which draws **every** flagged event, whatever its system kind;
    - `ContainmentFieldProvider.Underside` as the one field-height source that the natural scheduler reads and that is also installed as `FieldUndersideProvider`.

    The canopy flare beyond 400 km is the field's far strike layer (3.10.3), so the megastorm needs no canopy-flare drawing of its own.
- **Future.** Projectile or vehicle impacts (`Impact`, from `TryIntersectWorldSegment` hits) and eclipse orb transits (`Scripted`) use the same call.

### 4.3 Data flow

```
weather clock t ─┐
sources ─(poll / Publish)─> ContainmentDisturbanceBus (pool 16, budget, merge, evict)
                                   │ spawn: project to field, build ripple patch mesh (CPU, double, once)
                                   │ every frame: age, A(τ)>0?, FlashLevel(t), camera-relative occluder
                                   v
RingContainmentField ── MeshInstance3D.SetInstanceShaderParameter(cf_ripple_a/b/c) + transform ──> GPU (no SSBO)
```

The "GPU ripple buffer" is 16 pooled instances x 3 instance-uniform vec4s. That is within Godot's 16-instance-uniform limit per shader.

---

## 5. Gameplay, collision, audio

- **Now: flags and queries only.** `ContainmentFieldPolicy` (static):
  - `RetainsAtmosphere = true`, `TransmitsLight = true`, `TransmitsThermal = true`;
  - `FlightPermeability = Pass`, `ProjectilePermeability = Pass` (enum `Pass | Resist | Solid`).

  There is no collider and no gameplay effect in v1. Flight (later) decides between `Resist` (drag or a pushback impulse from `SignedDistance`) and `Solid` (a `TryIntersectWorldSegment` sweep). The query API already supports both.
- **Audio (optional, WO10).** A very low hum within 500 m of the field, gain `smoothstep(500, 50, d)`, through the existing weather ambience bus. It uses the existing ambience volume, so no new slider. Ripples are silent: thunder already covers the lightning.

---

## 6. POI along the field: what the POI author gets

### 6.1 Wall-top POIs at the blue lines (decision 3)

- **Positions.** One POI per longitudinal cue line per wall (E19), from `RimWallTopAnchors.Build` (1.4).
  - Canonical around is `k * 250 km` for k = 0..62, and `C + k * 250 km` for k = -62..-1.
  - That gives **125 per wall, 250 in total**. Neighbours are 250 km apart, except one 415.9 km gap between k = 62 (15,500 km) and k = -62 (15,915.9 km), centred on the antipode of around 0.
  - Across is the wall-top centre line: today the inner face plane; after WO12 the middle of the 100 m top. Vertical is the wall top.
- **Single source for the spacing.** The shader default, `RingTerrainGlobalLod.StructuralLongitudinalSpacingMeters` and `RingScaledFarViewport.StructuralLongitudinalSpacingMeters` are three copies of 250,000 today. WO8 moves the value to one C# constant, for example `RingStructuralCues.LongitudinalSpacingMeters`. Both owners and the anchor builder read it, and the shader default stays equal (a test asserts all four agree). That way a POI can never drift off its line.
- **Seam gap.** The gap exists because C / 250 km = 125.66 is not an integer. If the user wants perfectly even POIs, the alternative is spacing = C / 126 = 249,333 m (126 lines, a uniform 0.3 % shift of every line except the first). That is a visual change to the hull cue and is not done unless asked. The anchor builder takes the spacing as input, so either choice works.
- **Angle zero.** The shader measures around from world +X about the ring centre. That equals canonical around 0 only while the grid basis is identity, which is true for every current scene. WO8 asserts it: it projects anchor k = 0 through `CanonicalToWorld` and checks that `atan(z, x)` is 0.
- **Streaming.** 250 sites, at least 250 km apart, so at most one or two are ever within POI range. Use the `EmitterPoiStreamer` slot pattern (2 slots) plus a MultiMesh stand-in tier. Within about 2 km of a site the field near patch is active, so the POI is the main place players see the shimmer.
- **Field around the POI.** The field root runs along the wall top through every site (1.5, WO12). The POI is opaque and depth-tested; the field's 30 m soft intersection and the root ramp hide the seam where the root meets POI geometry.

### 6.2 General placement and streaming

- **Placement.**
  - `ContainmentFieldPlacement.WorldTransform(snapshot, SurfaceAtArchLength(profile, around, s), offset)` places a node on the field, oriented to it.
  - `RimRoot(side, around)` places along the wall-top emission line (the field is vertical there).
  - `AnchoredAt(pt)` gives a mesh-building anchor with no large coordinates.
  - All of it is in canonical metres, so a POI that "goes along the field" is a function of `around` (circumferential) and/or `s` (across the arch).
- **Streaming.** A circumferential POI is effectively 31,400 km long. Split it into around-segments, about 2-4 km each. Follow the streamer pattern:
  - threaded load and instantiate once (`EmitterPoiStreamer.cs`);
  - a slot pool with dwell and hysteresis;
  - `RingPoiHaze.RegisterTree` and `PoiBaseAmbientOcclusion.Apply` on instantiate (`EmitterPoiStreamer.cs:288-289`), plus `RingPoiHaze.Tick` (`:146`);
  - `RingLargePoiShadowExtents` while large detail is mounted;
  - a MultiMesh stand-in tier like `RingScaleAnchorLayer` for the distance;
  - `AutoBind` / `LoadSavedDefaults` for fixtures.
- **Materials.** Only `ring_poi_architecture.gdshader` or `_double_sided`. The POI then gets regular haze, cloud shadows and lighting with no toggles (AGENTS.md, skill section 3).
- **Things to verify for a POI at 10-40 km**, fixed in the shared systems, not per POI:
  - cloud shadows: receivers above the cloud tops must be unshadowed (`RingCloudShadowProjector` height projection);
  - haze at altitude;
  - `PoiBaseAmbientOcclusion` base height, which is the wall top for rim-rooted parts.
- **How the field renders around POI geometry.**
  - The POI is opaque and drawn first.
  - The field refracts only what is behind it, and depth-tests against the POI. Parts in front of the field are untouched.
  - The 30 m soft intersection removes seams where the POI pierces the field.
  - The root ramp hides the line at the wall top.
  - The field never tints or lights the POI.
- **Precipitation.** The occlusion camera starts at the precipitation plane (`RingPrecipitationOcclusion.cs:213, 266-269`), so POI parts above the clouds do not shelter the ground. Verify this in the POI's own acceptance.
- **Do not** add POI-specific field behaviour (for example "field glow near POI"). If the POI is the field's emitter, it publishes `Scripted` disturbances through the bus.

---

## 7. Live tuning menu and debug tooling

Section "Containment field", Atmosphere tab (`RingLiveTuningDrawer.Atmosphere.cs:127`). It uses the table-driven trio (skill section 1):

- `GameplayContainmentFieldSettings` / `GameplayContainmentFieldDefaults` (`user://gameplay_containment_field_defaults.tres`);
- `RingLiveTuningDrawer.ContainmentField.cs`.

| Control name | Label | Range | Default | Effect |
|---|---|---|---|---|
| `ContainmentShimmerStrength` | Shimmer strength | 0-2 | 1.0 | near refraction amplitude; 0 hides the near patch |
| `ContainmentShimmerRangeKm` | Shimmer range | 0.5-10 km | 3.0 | distance N where shimmer starts |
| `ContainmentRippleStrength` | Ripple strength | 0-2 | 1.0 | ripple refraction and reveal; 0 hides ripple patches (the bus still runs, so A/B is deterministic) |

No enable toggle, no per-system toggles, and no quality-preset column (the cost is too small to matter). Shape, apex and pool size are not tunables.

Debug tooling, not the menu:
- CLI `--containment-field=off` (A/B and perf);
- `--containment-debug=1|2|3` (patch tint / distance heat / ripple discs);
- Diagnostics tab:
  - readout: camera distance to the field, clearance below, near patch on/off, live ripples, spawns/s;
  - button "Spawn containment ripple" (above the camera's nearest field point, E = 1, flash 1 for 0.3 s);
  - button "Spawn field strike" (a FieldStrike at a chosen across or above the camera, E = 1, a standard lightning pulse, a random seed). It also writes the far list when the point is beyond 350 km.

---

## 8. Work orders

Every GPU step follows the `godot-test-run` skill:
- one reused profile, `appdata_containment`;
- the GPU queue and Godot-kill rules from memory;
- captures under `.godot/captures/containment_*`;
- `tools/prune_test_output.ps1 -Apply` at the end.

ffmpeg checks use `tools/media/ffmpeg_tools.ps1` (`sheet`, `compare`, `flicker`, `mp4`).

**WO1: Geometry core (pure C#)**
- Files:
  - `scripts/ContainmentField/ContainmentFieldProfile.cs`, `ContainmentFieldGeometry.cs`, `ContainmentFieldPlacement.cs`, `ContainmentFieldPolicy.cs`;
  - `tools/ContainmentFieldGeometryTest.cs` plus a `.gd` wrapper (`test-scaffold`).
- Tests, headless:
  - `H(0) = H(W) = WallTop`, `H(W/2) = Apex`; symmetric; monotonic per half;
  - `SurfaceAtArchLength` round-trip < 1 cm, total arch length 332.6 km ± 0.1 % (Certified);
  - `Nearest`: the residual is parallel to the normal; distance matches brute-force sampling (2e5 samples) within 1 cm, for points inside, above, beyond the walls, under the wall tops and at the centreline;
  - `SignedDistance` sign, `IsContained`, `ClearanceBelow`;
  - invariance in `around` (including wrap at the circumference);
  - `WorldTransform` / `AnchoredAt` vs `CanonicalToWorld` at 3 anchors, < 1 mm;
  - `TryIntersectWorldSegment`: hits and misses, plus a grazing segment near the rim;
  - `FromSnapshot` for Certified (10,000) and runtime-surface (10,400) snapshots;
  - **wall depth as a parameter.** Every geometry test also runs with `RimWallDescription(100, OuterEdge)`, `(100, CentreLine)` and `(100, InnerEdge)`:
    - roots at -100 / W+100, -50 / W+50 and 0 / W respectively;
    - `H(root) = WallTop`, `H(c) = Apex`;
    - with depth 0, all three modes are byte-identical to each other;
  - `CloudCeilingAt`: exactly 18,000 where `H - 300 ≥ 18 km`, exactly `H - 300` otherwise; `CloudCeilingFromUnderside(H(x)) == CloudCeilingAt(x)`.
- Done when the test passes. No scene changes.

**WO2: Disturbance bus and adapters (pure C# plus thin adapters)**
- Files:
  - `ContainmentDisturbance.cs`, `ContainmentDisturbanceBus.cs`, `Sources/LightningDisturbanceAdapter.cs`, `Sources/UpperAtmosphereDisturbanceAdapter.cs`, `Sources/StormContactAdapter.cs`;
  - `Sources/MegastormDisturbanceAdapter.cs`, plus installing `FieldUndersideProvider`. This part lands after megastorm WO8 (`MegastormFieldCoupling`) exists; until then the adapter compiles against nothing and is not created;
  - `tools/ContainmentDisturbanceTest.cs`.
- Tests:
  - dedupe by SourceId;
  - pool cap 16, eviction order, 6/s rate limit, merge rule;
  - coupling threshold: a centre-habitat 18 km top gives no ripple; a 1 km-from-rim 18 km top does;
  - determinism: same event sequence gives the same pool; paused clock gives the same state;
  - `A(τ)` is exactly 0 at τ ≥ T;
  - reduced-flash pulse passthrough;
  - out-of-range (> 400 km) rejected;
  - **sprites are read-only (decision 2).**
    - With the adapter on vs off, the `UpperAtmosphereSprite` lists from `TryDerive` are identical over a 600 s event stream.
    - Jets whose column spans H publish at `(around, H, across)`.
    - Red sprites (base above the apex) never publish.
    - The same EventId arriving via the natural layer and via megastorm `UpperDischarge` yields one ripple;
  - **megastorm (after megastorm WO8):**
    - shared fixture with `MegastormFieldEventsTest`, giving the same ids and the same ripples;
    - bursts re-pulse while alive and drift at `DriftAroundMetersPerSecond`;
    - a centre-habitat burst (gap about 22 km) gives no ripple;
    - contact publishes only while `Active`, within the band, and at most 4/s;
    - cadence is identical at 25 and 350 m/s for a fixed observer window;
    - with the provider installed, the megastorm ceiling equals `CloudCeilingAt` at 64 across samples;
  - **field strikes:**
    - a flagged event publishes `FieldStrike` (never `Lightning`), with no gap attenuation;
    - a flagged event's FlashLevel follows its own `DurationSeconds` envelope (a 1.9 s megastorm strike stays lit through its re-pulses; a 0.72 s default is unchanged);
    - the same EventId seen on consecutive frames gives one patch;
    - reservations: 4 FieldStrike slots stay free under a saturating contact load, contact ≤ 6 live, bursts ≤ 3;
    - caps: FieldStrike 2/s, natural ≤ 1/s;
    - restrike merge re-triggers the live patch instead of taking a slot;
    - never evict a lit patch for a dimmer one;
    - strikes beyond 350 km go to the 32-entry far list, never to the pool;
    - a scripted busy-megastorm minute gives ≤ 16 live patches throughout and ≥ 1 strike rendered per strike published within 400 km.

**WO3: Near patch rendering**
- Files:
  - `RingContainmentField.cs` (node, near patch, mesh builder);
  - `materials/ring_containment_field.gdshader`;
  - scene nodes in `scenes/Main.tscn` and `scenes/VoxelRingIntegrationTest.tscn`;
  - the shader added to `RingWorldLoadingResources.cs:55-80` (warm-up);
  - `RingPoiHaze.RegisterMaterial` for the ray frame;
  - the `no_precipitation_occlusion` group;
  - CLI flags `--containment-field`, `--containment-debug`.
- GPU acceptance (`tools/containment_field_gpu_test.gd` plus a capture route):
  - **Identity.** Ground centre and ground 2 km from the rim, noon and night, on vs off: byte-exact on the capturing machine. The draw count is unchanged.
  - **Wall top grazing.** Camera at the wall top + 50 m, across 200 m, looking along the circumference, noon and dusk. Visible shimmer on the arch and rail, no line. A 10 s 30 fps capture through `flicker`: no single-frame spike above the static-scene baseline.
  - **Approach flight.** Vertical path at across 3 km, recorded as the camera climbs from a field clearance of 6,000 m to 300 m. `sheet` shows a smooth onset; `flicker` shows no pop when the patch toggles at N x 1.1.
  - **High altitude.** At 35 km and 39.5 km at the centre, and from above at 42 km: double-sided and correct. Note the cloud-order limitation.
  - FSR on vs off: no smear trail in `sheet`.
  - Paused clock: two frames identical.
- Perf: the Idle and Near rows of 3.9.

**WO4: Ripples**
- Ripple pool in `RingContainmentField`, patch build per spawn, instance uniforms, flash reveal, own-storm occlusion.
- Diagnostics "Spawn containment ripple" button and readout.
- GPU acceptance:
  - forced ripple, day and night, from 60 km and 150 km. `compare` on vs off: the heatmap differs only inside the ripple disc, and the frame after `T` is byte-identical to off;
  - storm-triggered: a forced near-rim storm with lightning (reuse the `storm_light_capture` / `storm_anvil_capture` forcing), night, from 80 km outside the storm. Ripples appear with flashes, with no residual glow between flashes (`flicker` and `sheet`);
  - under the storm: no ripple visible through the deck;
  - 16 overlapping ripples meet the perf row.
- **Field strikes** (3.10.2):
  - crawl and glow in the shared `ring_containment_strike.gdshaderinc`;
  - premultiplied blending;
  - `cf_strike` and `cf_occluder_band`;
  - the contact-band crawl from arc flashes;
  - the "Spawn field strike" button.
- Field-strike acceptance:
  - **Ground, night.** Forced strikes at across 8 km (field about 19.5 km) and at the centre (40 km), seen from the ground 50 km and 150 km away. `sheet` (30 fps) shows the crawl spreading from the contact over about 0.3 s and gone by 1 s, then a ripple. At t ≥ T the frame is byte-identical to field off. `flicker` shows one spike group per strike and returns to the static baseline (no residual glow).
  - **Reduced flashes.** Same strikes: the per-frame luma change stays within the reduced lightning envelope (no frame above the reduced pulse's step), and the crawl is slower and dimmer.
  - **Distance aliasing.** A strike at 40 km seen from 200 km: no per-frame sparkle inside the crawl's life (`flicker` stays smooth through the envelope).
  - **Busy megastorm** (`RING_MEGASTORM`, night, 100 km ahead of the wall, 60 s): the Diagnostics readout stays ≤ 16 live and ≤ the caps. The perf row (≤ 0.25 ms) holds.
  - **Occlusion.** Behind the wall from 100 km, strikes above the wall top stay visible, and ripples below the wall-top sight line are attenuated (heatmap).
  - **Day.** The strike is faint; it is recorded, not gated.

**WO5: Live menu**
- The settings trio, the drawer section and the Diagnostics wiring.
- `MinimapWindContractTest.InstallIsolatedPreferences` entry.
- `tools/ContainmentFieldSettingsSmoke.cs`, covering all 10 skill cases.
- A menu wiring test (`atmosphere_menu_smoke_test.gd` style). It also asserts that no `ContainmentField*Enabled` toggle exists.

**WO6: Cloud ceiling under the field**
- The `ring_containment_ceiling(across, altitude)` helper is multiplied into the density next to `ring_side_wall_mask` (`SunshineCloudsCompute.glsl:242-249, 460-462`). It is the GLSL twin of `ContainmentFieldGeometry.CloudCeilingAt`, the single ceiling (2.2).
  - Profile values go in GenericData: wall top, apex, `RootAcross0`, `RootAcrossW`. Pass the roots, not W, so WO12 needs no shader change. The WO author picks free slots and documents them.
  - Mirror it in the CPU column (`scripts/clouds/RingCloudLayers.cs`).
  - Find every 18 km ceiling consumer listed in S6 section 2.4.
  - The megastorm bake clamp (its WO2) consumes the same rule through `FieldUndersideProvider` plus `CloudCeilingFromUnderside`. Whichever of the two WOs lands second adds the parity check.
- Tests:
  - `CloudLayerColumnTest` gains near-rim cases;
  - `cloud_layers_gpu_test` asserts nothing above `H - 300` within 7 km of a rim;
  - centre-habitat identity: `far_cloud_density_parity` and `sunshine_shader_compile` pass;
  - byte-identical at the standard 15 / 40 km poses (far from the rims);
  - perf A-B-A as in 3.9.

**WO7: dropped (decision 2).** Sprites and jets are not clamped or changed. The field-side jet-crossing ripple lives in WO2 and WO4.

**WO8: Wall-top POI anchors and POI support**
- **First deliverable: `RimWallTopAnchors`** (1.4, 6.1). It returns one wall-top anchor per longitudinal cue line per wall: 125 per wall, 250 total, in canonical around order.
  - Move the 250,000 m spacing to one C# constant (for example `RingStructuralCues.LongitudinalSpacingMeters`). `RingTerrainGlobalLod.cs:106-110` and `RingScaledFarViewport.cs:192-196` read it; the shader default (`ring_hull_global.gdshader:27`) stays equal.
  - Across comes from `RimWallDescription.CentreLine`. Vertical is the snapshot wall top.
- Tests, headless (`tools/RimWallTopAnchorsTest.cs`):
  - count is 125 per side; spacing is 250 km except one 415,936 m gap at around 15,500-15,915.936 km (Certified);
  - each anchor's world position, projected through `CanonicalToWorld`, gives `atan(z, x) * R ≡ k * 250 km` (the shader's own line formula) within 1 m. This proves angle 0 is canonical around 0;
  - the four spacing copies agree;
  - with depth 0 the anchors sit on the inner-face planes; with `RimWallDescription(100, ...)` they sit at across -50 / W+50.
- GPU check: one capture at a wall-top anchor at noon and night. The anchor gizmo is centred on the teal vertical band.
- Then the general helpers: `ContainmentFieldPlacement` examples in a fixture (one dummy wall-top POI at an anchor, using `ring_poi_architecture`).
  - `poi_haze_registry_test` and `poi_haze_gpu_test` poses at the wall top.
  - Verify that cloud shadows and haze are correct for 10 km receivers on the wall top. Any fix goes into the shared includes.
  - Write the author notes (6.1, 6.2) into `assets/poi/README.md`.
- Streaming for the 250 sites (slot pool plus stand-ins) is built with the POIs themselves, after WO12. It is not part of WO8.

**WO9: Atmosphere lid audit (headless, measurement only)**
- A C# port of `ring_atmosphere_path_interval_shared` / `first_air_paths` (`materials/ring_atmosphere_shared.gdshaderinc:3-53`) with a field-lid variant. Report the maximum opacity difference over:
  - ground rays toward both rims at elevations 0-30° (habitat);
  - wall-top rays;
  - in-sliver rays (flight).
- Implement the lid in the shared include only if the habitat delta exceeds 2/255 (then rerun `poi_haze_gpu_test` and the haze goldens), or when flight is scheduled.

**WO10 (optional): field hum**
- Ambience voice and `WeatherAmbienceMix` gain. Headless gain-curve test.

**WO11: reserved** (megastorm coupling is folded into WO2).

**WO13: Far strike layer (3.10.3)**
- `scripts/ContainmentField/RingContainmentFarStrikeLayer.cs` in the scaled far viewport, following the `RingUpperAtmosphereLightningLayer` pattern: MultiMesh, ignore-culling, `AddScaledWorldChild`, per-instance `PathTransmittance`.
- `materials/ring_containment_far_strike.gdshader`, including `ring_containment_strike.gdshaderinc`. Quads in the field tangent plane, minimum 0.3°, additive.
- Fed by the bus's far list. Crossfades with main-view patches over 350-400 km. Added to `RingWorldLoadingResources`.
- Acceptance:
  - forced strikes at 500 km, 2,000 km and on the opposite arch, at night: each visible as a short violet flare on the canopy, gone with its pulse (`sheet`, `flicker`);
  - a scripted pull-back of the camera from 300 to 450 km during a strike shows no pop at the handoff;
  - with no strikes, the far viewport is byte-identical to field off;
  - ≤ 0.02 ms GPU.

**WO14: Natural near-wall field strikes (3.10.6; after WO2 and megastorm WO13)**
- `RingVolumetricCloudLayer.Lightning.cs`:
  - the deterministic roll in natural event creation (`CreateLightningEvent` `:381-384`): gating 1-6, salt `0x5F1E`, 20 s per-system cooldown;
  - it sets the shared `FieldStrike` flag, `GroundPosition` = contact point, `CloudPosition` = channel base, and `DurationSeconds` with the field-strike envelope (all from megastorm WO13);
  - it reads `ContainmentFieldProvider.Underside`.
- The channel is drawn by `RingFieldStrikeLayer` (megastorm WO13, source-agnostic; its acceptance already includes a synthetic flagged kind-3 event with a 1 km channel).
- Tests, headless (`tools/NaturalFieldStrikeTest.cs`), with a scripted Cb (top 18 km, Precipitation 0.9, kind 4), 3,600 s of weather clock per case:
  - **zero field strikes from natural storms far from the walls:** storms at d_w = 20, 40, 80 and 159 km give exactly 0 flagged events. A storm at d_w = 13 km (past the 12 km cutoff) also gives 0;
  - a storm at d_w = 7 km gives at least 1 strike and at most 1 per 20 s;
  - a non-Cb or weak system (Precipitation 0.3, kind 3) at 7 km gives 0;
  - a null provider gives 0;
  - the megastorm (kind 6) never takes the natural path;
  - determinism: two runs, and a run with a mid-way pause, give identical EventIds and flags;
  - "Lightning frequency" x0.5 roughly halves the count;
  - every published natural strike goes through the WO2 caps (≤ 1/s).
- GPU check: a forced near-wall Cb at night, seen from 60 km. An occasional strike, crawl and ripple near the wall, nothing elsewhere.

**WO12: Rim wall depth (~100 m), the last WO, immediately before the wall POIs**

Today every render tier draws the rim walls as zero-thickness planes at across 0 and W. Only the voxel domain has a 32 m outboard hull (E20).

- **Direction.** Thicken **outward**: the wall occupies across [-D, 0] and [W, W+D], with D ≈ 100 m. The inner faces stay where they are, so the habitat, terrain, clouds, atmosphere and Wall Engines are untouched.
- **Field root: recommend the outer edge.** Set `RimWallDescription.Current = new(D, RimFieldRoot.OuterEdge)`, giving roots at -D and W+D.
  - The whole 100 m wall top, and every wall-top POI on it, is then inside the retained atmosphere. POIs get regular haze and lighting with no vacuum special case.
  - The field rises vertically just outboard of the POIs instead of cutting through them.
  - Side effect: H at the inner face becomes about 1.06 km above the wall top (the vertical tangent moved 100 m out). That is harmless.
  - Alternatives:
    - `CentreLine` makes the field visibly leave every POI's crown. Use it only if the POIs are meant to be emitters with the field through them; the soft intersection then works on every POI.
    - `InnerEdge` puts the wall top and POIs in vacuum. Not recommended.
- **What it touches:**
  1. **Wall geometry, every tier.** Each wall gets an inner face, an outer face at -D / W+D and a top cap at the wall top. Lower hull and underside closures extend by D.
     - near proxy rim walls: surface A, double-sided, plus the UV2 wall-floor contract and its smoke check (`RingTerrainProxySector.cs:2117, 5247, 6170-6181`);
     - global LOD hull (`RingTerrainGlobalLod.cs:292, 1507, 1603`);
     - analytic global LOD (`RingAnalyticGlobalLod.cs:223`);
     - far shell `AddWall` (`RingScaledFarViewport.cs:1875-1876`). At 1:1000, D is 0.1 m, so the far shell may keep a single plane; decide by capture.
  2. **Hull shader** (`ring_hull_global.gdshader:58-77, 127-130`).
     - The top cap has a radial normal, so `wall_face` is 0 there. The across-based cross grid would then paint the cap, because across ≈ 0 lies on a grid line, so mask the cross grid on the cap as well.
     - The longitudinal blue lines (around-based) continue over the cap automatically. Check that they stay aligned with the anchors.
     - The top rail cue stays at the wall-top altitude.
  3. **Collision.**
     - The voxel structural hull is `ChunkSizeZ` (32 m) outboard (`VoxelChunkGridManager.cs:4729-4740`). D must be chunk-aligned (96 or 128 m), or the hull thickness must be decoupled from the chunk size. Pick 96 m or 128 m unless the user insists on exactly 100.
     - The wall top must become walkable collision.
     - Verify the Terrain3D runtime-surface backend's across bounds and explorer clamping (`EnforceRingAcrossBounds`).
     - Check that saved voxel edits in hull chunks still load.
  4. **Terrain/wall junction.** None, because the inner face does not move. Re-check the proxy `inner_face_floor_enabled` discard and the Wall Engine forecourt quays (`ClimateMachineRegistry`, across 0 / W) only for regressions.
  5. **Shadows.** Macro wall shadows and the height-shadow and cloud-sun wall inputs (`RingTerrainGlobalLod.cs:502`; `RingTerrainHeightShadow.cs:578-579`; `RingTerrainCloudSunVisibility.cs:260`) take the cap into account, or a 100 m cap is shown to be below their resolution.
  6. **Field and anchors.** Set the one `RimWallDescription.Current` value.
     - The field profile, near patch, ripples, cloud ceiling (GenericData roots, WO6) and wall-top anchors (WO8) follow with no other code change.
     - Rerun the WO1 and WO8 depth-100 tests against the live value.
     - Rerun the WO3 wall-top grazing capture: the root ramp now starts at the outer edge.
  7. **Atmosphere side planes.** Leave them at the inner faces. The 100 m strip over the wall top is negligible air, and WO9's audit covers it if the lid is ever built.
- **Acceptance:**
  - wall-top captures at a cue line, noon and night, from the habitat floor, the wall top and 50 km away: no z-fighting at the cap, no grid on the cap, blue line continuous over the cap;
  - collision walk on the cap;
  - far-shell `compare` before and after (sub-pixel or accepted);
  - perf: proxy and global hull draw and triangle counts within +1 %.

Order and dependencies:
- WO1, then WO2 (the megastorm part waits for megastorm WO8), then WO3, then WO4, then WO5.
- WO6 is independent after WO1.
- WO8 waits for WO1 and WO3.
- WO9 is independent after WO1.
- WO10 is optional.
- WO13 follows WO4.
- WO14 follows WO2 and the megastorm's WO13 (the shared flag, `DurationSeconds`, the pulse envelope and `RingFieldStrikeLayer`).
- **WO12 is last.** It runs right before the wall-top POIs are built, after the POI assets exist.

Per-batch gate: full GPU gate suite once (memory "Lean test cadence").

---

## 9. Questions

The three original questions (wall height, sprites and jets, POI form) were answered by the user on 2026-10-29. See the "User decisions" section at the top.

2026-11-04: questions 1-3 below are answered (respace evenly to C/126; root on the centre line; depth 128 m). See the 2026-11-04 decisions at the top. The vertical skirt (1,600 m) was confirmed by the user on 2026-11-05.

Smaller decisions remain. Each has a default, so none blocks WO1-WO6.

1. **Cue seam gap.** Keep 250 km spacing with its one 415.9 km gap (default: no visual change), or respace to C/126 = 249,333 m for 126 evenly spaced lines and POIs? Needed before WO8 ships anchors.
2. **Field root on the 100 m top.** Outer edge (recommended), centre line, or inner edge? It is one constant, and it is needed only at WO12.
3. **Exact depth.** 96 m or 128 m to match the 32 m voxel chunk lattice (recommended), or exactly 100 m with a decoupled hull thickness? Needed only at WO12.

Flight solidity stays a policy flag (default `Pass`) until flight exists.
