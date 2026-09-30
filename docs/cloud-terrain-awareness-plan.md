# Terrain-aware clouds and mountain intersections

Assessment: 2026-09-29. The user selected shared haze integration and the depth-cutoff milestone for this pass. The final march interval is now clipped and weighted against opaque terrain depth. The broader requirement that clouds remain absent above mountain peaks still requires the mountain-exclusion map below; it is not implemented by depth clipping. Authored layer heights and saved cloud settings were preserved. The original in-game mountain-grain appearance still needs user visual confirmation.

## Findings in this checkout

- `scripts/RingSunshineCloudBridge.cs:87` defines a fixed cloud datum (0 m), an ordinary 2000-6000 m layer, and a separate 500 m storm-front base. The checked-in gameplay overrides currently set the ordinary top to 6050 m. These are repository settings; a running world's saved overrides may differ.
- `ResolveNaturalCloudAltitude` and `ResolveNaturalStormCloudAltitude` use those authored altitudes. The terrain sample in `ResolveSurfaceHeight` is used for renderer visibility/handoff, not a spatial cloud-clearance field. Mountains extending to 8000 m can therefore intersect or rise above the ordinary layer.
- Sunshine already clips against main opaque depth and the scaled far terrain's physical-depth texture. It also has depth-aware reconstruction and temporal filtering. Terrain awareness should extend these systems rather than replace their existing responsibilities.
- The original Sunshine march set `depthBreak` after passing opaque depth but still evaluated density/lighting before breaking. A controlled GPU capture confirmed clouds behind a 300 m occluder even though the cloud band started at 500 m. The corrected path samples the visible terminal interval and scales its density by the visible fraction. Regression captures exercise 500 m and 958 m steps; user-configured march distances can differ.
- `docs/cloud-history-grain-fix.md` documents prior fixes for persistent interior noise and continuous camera motion. Preserve those reprojection, floating-origin, opacity, and depth-rejection contracts; increasing history alone could hide a boundary problem or produce ghosting.
- Near clouds, distant clipmap/shell clouds, and GPU cloud shadows use separate rendering paths. Their density evaluation must share the same mountain-exclusion rule.

## Required visual policy: mountain exclusion

Keep the authored weather-layer altitudes. Mountains and their peaks define regions where cloud density is zero throughout the cloud volume, including the space above a summit. Clouds fade out as they approach a mountain's exclusion boundary and remain absent over its protected footprint. There is no terrain-driven lifting of the cloud base or top.

Use two complementary rules: an altitude-independent mountain-footprint mask prevents clouds over peaks, and a local clearance check prevents remaining clouds from intersecting ordinary terrain. A clearance check alone is insufficient because its density returns above the summit. The exclusion applies to ordinary clouds, localized storms, and distant cloud representations. No automatic fog/storm exception should bypass this requirement.

The mask stays anchored to canonical world coordinates while cloud noise and weather move. It suppresses cloud formation locally; it does not imply a wind simulation, redirect storms around mountains, or permanently remove all weather beyond a mountain. Unblocked valleys can retain their clouds. This is an intentional visual policy, not a simulation of atmospheric uplift.

## 1. Establish and repair the depth boundary

Capture a fixed seeded mountain scene with camera movement, cloud history on/off, and separate raw-density/depth-tested-density outputs. Include opaque terrain in front of clouds, terrain inside a cloud, and real cloud volume between camera and terrain. Use both local Terrain3D and distant terrain depth.

Verify the ray's last interval ends at the nearer opaque depth. Integrate only its remaining visible length, with the sample point inside that interval. Simply breaking at the first overshooting sample can omit legitimate foreground cloud; simply clamping its position while retaining a full-step contribution can over-accumulate cloud.

If needed, refine a small number of terminal intervals rather than reducing the step length across every sky pixel. Keep reconstruction aware of geometric depth differences, not only whether a ray ever encountered geometry. Preserve foreground cloud while rejecting history from newly hidden geometry. Check depth units and pixel-center conventions across the main/far depth paths.

A depth fade can soften the remaining contact, but should not erase an entire ray's foreground cloud. NVIDIA's [soft-particle discussion](https://developer.nvidia.com/gpugems/gpugems3/part-iv-image-effects/chapter-23-high-speed-screen-particles) supports depth-aware intersection fading and describes low-resolution depth edge artifacts. Applying it to this integrated volume requires interval-aware treatment, not copying a particle alpha formula onto the final accumulated cloud.

## 2. Publish cached terrain context

**Deferred for this pass by user choice.** The depth correction does not remove cloud density above a mountain that is not occluding the view ray.

Expose an immutable, revisioned terrain snapshot in canonical around/across coordinates. Use the existing height field and terrain LOD summaries as data sources; fill missing coverage asynchronously with independent samplers. Never run the expensive procedural generator for each cloud pixel or rebuild a full height atlas every frame.

Maintain two concepts, which may be stored in separate fields or texture channels:

1. **Mountain exclusion footprint:** a world-anchored mask or distance field identifying mountain slopes, ridges, and peaks that must remain cloud-free at every cloud altitude.
2. **Local terrain clearance:** a more conservative terrain height used to suppress cloud density below/near the surface, including relevant edit updates.

Construct the footprint using the terrain generator's mountain/relief information together with actual height maxima. Elevation relative to the authored cloud base can identify unsafe highlands, but elevation alone must not define the entire mountain footprint or miss peaks outside the cloud band. The mountain classification threshold and horizontal padding are configurable visual choices to validate in the prototype. Retain narrow ridges/isolated peaks, expand their protected footprint with a horizontal buffer, and feather outward from that protected area. Do not blur away the fully excluded center of a narrow summit. Add an exclusion fallback where terrain is too close to a layer's base, so high plateaus do not acquire low ground-hugging clouds.

The existing `RingTerrainGlobalLod.TryGetTerrainHeightRange` is a useful starting point. However, inspection of `RingTerrainMinMaxPyramid.BuildBaseline` shows each baseline range is derived from four corners and a center sample. It is not a proven bound on every unsampled v3 peak. Do not claim strict no-intersection guarantees from that summary alone. Near terrain can use max reduction of existing authoritative samples; coarser regions require validated error margins, refinement, or a conservative fallback. Ordinary averaging/mipmapping can lose sharp peaks.

Use R32F/RF height storage. A 256-by-256 single-channel float field contains 256 KiB of base-level data; two such fields contain 512 KiB before mip levels, double buffering, and overhead. An exclusion mask stores coverage rather than elevation and may use a suitable separate format after filtering requirements are established. This arithmetic does not select a sufficient geographic resolution. Coverage must include the near renderer's tens-of-kilometers range, the far handoff, and shadow sampling footprints; the roughly 768 m gameplay window alone is insufficient.

Update newly exposed atlas strips on aligned recentering and dirty areas on edits. Publish complete revisions through the existing main/render-thread resource conventions, and retire old GPU resources only after their consumers can no longer use them. Preserve the previous valid map while preparing its replacement. Provide explicit missing-data behavior and blend coverage boundaries without allowing the blend to undermine the required terrain exclusion.

Over open water evaluate clearance against at least the water surface rather than the deep seabed. Map addressing must wrap the ring seam and remain stable under floating-origin shifts.

## 3. Suppress cloud generation around and above mountains

Conceptually, for each cloud-density sample:

```text
base = authored_base
top  = authored_top

// 0 within the protected mountain footprint; smoothly reaches 1 outside it.
mountain_allowance = smoothstep(0, edge_feather,
                               signed_distance_from_exclusion_boundary(x,z))

height_above_ground = canonical_altitude - local_terrain(x,z)
terrain_allowance = smoothstep(minimum_clearance,
                               minimum_clearance + clearance_fade,
                               height_above_ground)

if mountain_allowance == 0 or terrain_allowance == 0:
    return 0

density = authored_weather_density * mountain_allowance * terrain_allowance
```

The signed distance is nonpositive inside the horizontally buffered mountain footprint and positive outside. The mountain allowance depends only on canonical around/across coordinates, so moving the sample upward never re-enables cloud over that footprint. A 5000 m peak in a 2000-6000 m cloud layer has no cloud over its protected footprint, including the 5000-6000 m air above its summit. An 8000 m peak must also be excluded even though it rises above the entire ordinary layer. Both are explicit acceptance cases.

Reject excluded samples before evaluating expensive cloud noise and lighting. This resembles the early conservative-density check described in [Epic's volumetric cloud documentation](https://dev.epicgames.com/documentation/unreal-engine/volumetric-cloud-component-in-unreal-engine). A shared cached 2D mask is simpler than moving and bounding spatially varying cloud layers. Net performance must still be measured because it adds texture reads in visible-cloud and shadow paths.

Suggested prototype ranges, to be tuned by captures:

- Horizontal padding beyond classified mountains: 250-1000 m, initially 500 m.
- Outward edge feather beyond the protected footprint: 250-1000 m, initially 500 m.
- Local minimum clearance over remaining terrain: 100-500 m, initially 200 m.
- Local exclusion fade width: 100-300 m, initially 200 m.

These values are artistic starting points, not measured defaults. A 200 m fade is narrower than the current minimum 500 m ray step, so fade width, terminal refinement, and temporal convergence must be evaluated together. Exclusion-mask filtering must preserve exact zero within the protected footprint at every rendering distance.

## 4. Keep all cloud consumers consistent

- Apply the same mountain-footprint and local-clearance rules to Sunshine primary density and its density-based lighting, GPU near/far cloud shadows, and the far cloud clipmap/shell. Suppressed cloud must not continue casting a cloud shadow. A cloud outside the protected footprint may still cast a physically valid oblique shadow onto a mountain.
- Keep the existing authored traversal envelope and curved-shell entry/exit behavior. An excluded sample is empty space, not a reason to stop the entire ray: the ray can encounter valid clouds beyond that footprint. Actual opaque mountain depth still terminates visibility, and opposite-side shell rejection remains intact.
- Preserve authored storm identity, wind, and weather timing. Apply the exclusion after combining cloud-density sources, or to every source before combination, so additive localized storms cannot refill the protected region.
- Keep shadow projection altitudes at the authored layer values and synchronize mask revisions between their producers and visible clouds.
- Invalidate or locally reject stale temporal history when the density field changes materially. A normal atlas recenter with identical canonical values must not reset the whole history.
- Preserve altitude handoff and existing cloud limits; terrain awareness does not introduce higher cloud layers.

## Settings and diagnostics

Extend the existing grave-key Clouds tab and `GameplayCloudDefaults` persistence for mountain exclusion, mountain classification threshold, horizontal padding, edge feather, and local clearance/fade. Preserve an off mode for diagnostic A/B captures. Diagnostics should show the protected footprint, source terrain heights, authored base/top, atlas coverage/revision, and final exclusion allowance.

## Implementation order and acceptance

1. Add a controlled mountain-intersection fixture and fix any confirmed depth-boundary error first. This is the smallest potentially useful change and needs no new terrain generation.
2. Add the shared mountain footprint and local terrain-clearance context across near, far, and shadow consumers, with no terrain-driven altitude lift.
3. Validate that every cloud source, including localized storms, stays absent over protected mountain columns. Any future mountain-fog exception requires a separate user-selected feature; it is outside this plan.

Required comparisons: sea-level plains, high plateau, 5000 m peak below cloud top, narrow 8000 m peak above cloud top, adjacent unblocked valley, above-summit camera, inside-cloud camera, cloud in front of terrain, terrain in front of cloud, moving/turning camera, edited terrain, ring seam, floating-origin rebase, atlas recenter, near/far handoff, storms crossing the footprint, and cloud shadows. Reuse the existing cloud-history, foliage-edge, inside-cloud seam, shader-source, and tuning-persistence tests. Assert zero final density within protected columns across all supported cloud altitudes, including after filtered atlas and LOD transitions; screenshots alone cannot establish that invariant.

Measure frame-time distribution, ray samples, GPU cloud/shadow timings, CPU atlas preparation, uploads, and memory separately. Begin on the i7-13700H laptop; retain the i9-14900KF / RTX 4070 1080p 60 FPS check as a separate unmeasured target. Do not fix grain merely by globally increasing samples or blur without measuring the tradeoff.

- [x] Current architecture and likely altitude mismatch inspected.
- [x] User requirement incorporated: clouds stop at mountains and remain absent above protected peaks; regional lifting removed.
- [x] Concrete terrain-depth boundary behavior identified for a reproduction test.
- [x] Terrain-facing stochastic history loss reproduced and corrected with stored opaque-depth validation; visual acceptance in the user's scene remains pending.
- [x] Depth-boundary bug reproduced with controlled opaque geometry and physical far-depth input.
- [x] Final interval clipped, sampled inside the visible portion, and fractionally weighted.
- [x] Main/far depth regression covers a surface before the cloud layer and a surface inside it, with separate lit history-on/off captures.
- [x] Uniform-density regression verifies a half-step extension adds 0.1000 opacity at both 500 m and 958 m step lengths, preserving foreground volume.
- [x] Clouds and tree LODs connected to the existing shared atmosphere profile and live controls.
- [ ] Shared mountain-exclusion and terrain-clearance context implemented (milestones 2 onward).
- [ ] User visual acceptance and target desktop performance measured.

### Depth-cutoff evidence

`tools/cloud_mountain_depth_gpu_test.gd` uses production Sunshine shaders with controlled noise and an opaque proxy, plus a physical-metre far-depth texture. Its 12 cases include four fractional-integration checks. Before the fix, a 300 m occluder admitted cloud samples at roughly 603–1219 m in the measured cases. After the fix, all sampled near-occluder rays contain zero cloud and terminate at 300 m (within the planar proxy's perspective tolerance). The 1700 m occluder retains foreground clouds. The half-step alpha increment is approximately 0.100022 for the perspective main-depth proxy and 0.100000 for physical far depth.

Local captures and JSON are under `.godot/cloud-mountain-depth/`; `baseline-report.json` preserves the original march result and `report.json` records the corrected result. These controlled tests establish the depth bug and its fix, not a guarantee of mountain-footprint exclusion or a measured game-frame-rate improvement.

### Terrain-facing temporal history evidence

The gameplay temporal path previously used `depthBreak` to validate history. A jittered ray could stop at full cloud opacity in one frame and at the terrain behind it in the next. That change could reduce or reject history even though the opaque scene had not changed. This is separate from cloud altitude limits and from shadow vertical samples.

The private history metadata now stores the current opaque ray depth, or an explicit sky sentinel, without temporal averaging. Each of the four reprojected color taps is checked against its own depth metadata; accepted weights are renormalized for both color and cloud-distance metadata. Valid gameplay history keeps the configured weight. Near a terrain cutoff, validation uses depth agreement with a small screen-footprint tolerance. For clouds ending well before both surfaces, an endpoint check with camera translation and a full march-step margin allows history across grazing ground slopes. The endpoint is a filtered estimate, so this remains a temporal reconstruction heuristic, not exact volumetric reprojection.

The public output retains the original depth-break flag for depth-aware reconstruction. Menu history, terminal interval integration, haze-after-history ordering, saved settings, and the 64-sample shadow cap are preserved. The fix reuses existing textures; it adds depth validation reads and arithmetic, but no additional ray-march samples or texture allocation.

`tools/cloud_terrain_history_gpu_test.gd` compares production shaders against a saved pre-fix shader compiled in memory. Its ten cases cover stationary and translating/turning views at 1,200 m and 60,000 m through main and physical far depth, followed by newly appearing 300 m occluders. A controlled dither sequence changes the march termination without changing terrain or density. The fixed path retains 0.95 history in all eight stable-surface cases. Accumulated opacity variance falls by approximately 65–79% against the pre-fix shader. On the first frame of a new occluder, retained history and cloud alpha are both zero; the old shader retained 0.95 history and approximately 0.424 cloud alpha in this fixture.

Evidence: `.godot/cloud-terrain-history/baseline-report.json` and `report.json`. These are controlled correctness comparisons, not a frame-rate measurement or a claim that every source of cloud grain is eliminated. The six-route motion regression also passes at its established 0.85 history setting, including 15/60/120-degree-per-second turns, translation, and a 128 m floating-origin rebase. The previously documented fast-turn response limit at 0.95 remains separate.
