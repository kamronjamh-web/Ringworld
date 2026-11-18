# R3a design: publish the cloud data for reflections

Lead design (R3a-0), 2026-11-17. Part of `docs/reflections-plan.md` section 10. Read 10.2, 10.3 and 10.5 first. The water side is `docs/work-orders/reflections/R1-2-R1-5-design.md` (section 4 there); section 7 below lists where the two designs meet.

Game lines cite the working tree of 2026-11-17. `SunshineClouds.gd` and `RingSunshineCloudBridge.cs` had no uncommitted changes on that date. Short names: "gd" = `addons/SunshineClouds2/SunshineClouds.gd`, "march" = `SunshineCloudsCompute.glsl`, "fill" = `SunshineCloudsInterleaveFill.glsl`, "post" = `SunshineCloudsPostCompute.comp` (the body that `SunshineCloudsPostCompute.glsl` includes), "hist" = `SunshineCloudsHistory.glslinc`, "bridge" = `scripts/RingSunshineCloudBridge.cs`. **Every value marked "est." is an estimate, not a measurement.** Section 10 lists what was read and what was not.

## 0. Summary

- **Publish the two images the post pass reads**, view 0: the colour image (`accumulation_textures[1]`) and the data image (`accumulation_textures[0]`). After every callback they hold the reconstructed result: interleave fill, temporal history, transient light and haze are all in. The post pass only reads them. They are not the display result: the post pass blurs, depth-occludes and composites into a separate blit image. Reflections need them separable, so they read these images, not the display.
- **Colour is straight (not premultiplied).** `rgb` = cloud radiance with haze to the cloud distance already applied; `a` = opacity. The display composites `mix(screen, rgb, a)`. Premultiplied colour is `rgb · a`.
- **Form.** Two bridge-owned `Texture2DRD` globals whose RD RID is swapped on the render thread (the contact-shadow pattern), one `mat4` that maps a world direction to the cloud image's clip space, and two `vec4` parameters. All are written from one render-thread callable per frame, from a mutex-guarded snapshot the callback records. The callback itself issues no new GPU command.
- **Valid** is a weight in [0, 1]. It drops to 0 only when the data is missing or belongs to another view: before the first dispatch, after retirement, clouds off or suspended, stale snapshot, another view, a rebase jump (teleport), the menu or diagnostic modes. It ramps back up over 0.25 s (est.). History resets, resizes and interleave changes do **not** invalidate: after any callback the image is complete (section 2.3).
- **Readers** sample by world direction. Colour is filtered premultiplied. Distance is a coverage-weighted *harmonic* mean over the bilinear taps: taps below 0.1 coverage (est.) are left out, and "far" counts as 1/D = 0. One refinement places the hit at the cloud's distance.
- **Cost.** GPU: nothing measurable for the publication. CPU: one snapshot and one render-thread callable per frame. Reader: 8 `texelFetch` per call, two calls with the refinement.
- **Byte identity holds by construction.** The publication runs after every command of the callback has been recorded. It writes no state that the march, the fill, the post or the display reads, and nothing reads the globals at model 0. The test `cloud_publish_identity_gpu_test` proves it with an A-B-A run.
- **Main view only (2.6).** The callback's snapshot step filters itself, using the main view's target size and camera, which the bridge hands over each frame. This changes nothing about which views the clouds render in. A non-main callback updates nothing, but it marks the content as overwritten, so valid goes to 0 until the next main-view march.
- **Capture hold (2.5, order B7b of the test-speed lane).** The snapshot has two parts:
  - *content*: RIDs, camera, matrix, serial. It is refreshed only by a main-view callback that recorded a complete march (and fill);
  - *liveness*: refreshed by every main-view callback.

  During a hold the globals therefore keep the last complete images, and valid stays 1.
- **No cloud shader changes.** Two findings would need one. Both are settled without a shader change (section 9).

## 1. What the cloud pass computes, and where it is between the march and the screen

### 1.1 Images (gd:2296-2329)

Per view, `accumulation_textures` holds seven `R32G32B32A32_SFLOAT` textures of `new_size = internal size / 2^resolution_scale` (gd:2200-2211, 2310-2312; integer division):

| Index | March binding | Role |
|---|---|---|
| `view·7 + 0` | 0 `output_data_image` | **data**: what the post pass and readers use |
| `view·7 + 1` | 1 `output_color_image` | **colour**: already published as the backdrop texture (gd:2319-2321) |
| `+2 .. +5` | 2-5 | colour and data history, ping-pong A/B, private |
| `+6` | post binding 2 | Sunshine's own "reflections" image (section 1.6) |

The bridge sets `resolution_scale` 0 to 2: Native, Half, Quarter (bridge:1709, 1729).

**Format mismatch (finding).** The textures are RGBA32F (gd:2310), but the march and the fill declare both output images `rgba16f` (march:48-49, fill:34-35). The post pass reads them through a sampler (post:6-7, 347-348), and a sampler uses the view format. Whether a store goes through fp16 rounding is up to the driver. The reader rules of section 3 hold either way; the test of section 4 records which one happens on the 4070. See question 1.

### 1.2 Order of one callback (gd:2752-2858), per view

1. Pre-pass: depth downsample (2780-2784).
2. March: full resolution, or one pixel per n x n block when interleave is active (2791-2814). It resolves its pixels against history and writes colour and data at those pixels (march:2312-2397, 2399-2483, 2559, 2566).
3. Interleave fill when active: every other pixel (2817-2827). It writes the same two images for those pixels (fill:257-288). After step 3 every pixel of both images is this frame's reconstructed value.
4. Post: **reads** colour and data through a linear sampler (bindings 0 and 1 are `UNIFORM_TYPE_SAMPLER_WITH_TEXTURE`, gd:2571-2583). It writes the composited screen into `blit_screen_images` (post:497) and the "reflections" image (post:504-509).
5. Display: a raster draw that copies the blit image into the colour attachment (gd:2841-2846; `SunshineCloudsDisplay.rast`:15-18).
6. Publication of the backdrop wrapper (gd:2857-2858). **This is the publication point for R3a too.** Nothing writes the two images again until the next callback's step 2.

### 1.3 The colour image after step 3

- `rgb` is **straight**. The march sets the colour as radiance, with the opacity in `a` (march:2219; no multiply by `a`). The display composites `color.rgb = mix(color.rgb, currentAccumilation.rgb, density)` (post:490), with `density` from `a`. The far backdrop does the same (`materials/ring_far_backdrop_cloud_composite.gdshader`:19-21). Premultiplied = `rgb · a`.
- `rgb` **has the haze to the cloud already applied**: `L·T + S` with the physical model (`RingAtmosphereCompute.glslinc`:24-30), the megastorm haze path factors when the strip is on (march:2546-2557, fill:273-281). The haze distance is `clamp(data.b, 0, opaque depth)` (march:2547, fill:274). The image follows the live profile; the private history stays profile-free (march:2544-2545).
- `rgb` **includes transient light** (lightning point lights isolated from history, transient segment lights), added after the history store (march:2541-2543, fill:269-272).
- **HDR range:** linear scene radiance, the same units as the colour buffer before tonemapping (it is mixed straight into it at post:490). No upper clamp. The floor is the ambient (march:2251). NaN and Inf are replaced before the haze (march:2246-2248), not after.
- `a` = opacity, clamped to [0, 1] (march:2219), then history-blended. The post clamps it again (post:410).
- **Diagnostics overwrite it.** With the history-weight diagnostic on, the colour image holds weights, not colour (march:2536-2539, fill:265-267). The raw and depth density mattes skip the haze (march:2546). Publication is invalid in all three (2.4).

### 1.4 The data image after step 3

| Channel | Meaning | Source |
|---|---|---|
| `.r` | distance to the first non-empty sample, combined as `min(history-mixed, this frame)`. 0 whenever this frame's ray found nothing (`initialdistanceSample` starts at 0, march:1544, 1807-1808, 2500; fill:286). **Not usable** as a cloud entry distance. | |
| `.g` | march end distance (layer exit, opaque depth or step budget), history-mixed | march:2389, 2475 |
| `.b` | transmittance-weighted distance in metres, history-mixed | resolve march:2257; mix 2390, 2476; fill:249 |
| `.a` | `log2(1 + opaque linear depth)`, or -8 for sky. Written after the history store, not accumulated | march:2565, fill:287 |

What `.b` holds:
- A covered ray: the weighted mean, clamped to the march end (`SunshineCloudsDepth.glslinc`:12-16).
- An empty ray: `min(traveledDistance, highestDensityDistance)`. The latter starts at `stepCount · maxstep` (march:1336-1338). On High that is of the order of 70 km (est.: 128 x 560 m, or the handoff end when it is longer, hist:39-47).
- A sky ray that leaves the layer early stores the exit distance (march:2041-2046), which can be a few kilometres.

So **`.b` alone cannot say "no cloud"**: an empty ray stores an ordinary-looking finite number. Coverage must decide (3.2).

Also, the history mixes `.b` without regard to coverage (march:2390). At a cloud edge it averages a cloud distance with an empty-ray distance. This is the second reason for the coverage threshold.

### 1.5 What the post pass does to them before the screen (not in the images)

- Sampling at `uv + jitter_uv` (post:341). The images are treated as unjittered (gd:3030-3031, 3459-3465).
- Reconstruction blur: bicubic, depth-aware, mixed 0.8 at native with `blur_power` 0.1. The power falls with `.b` (post:357-379; the resource has `blur_power = 0.1`, `resources/clouds/sunshine_ring_local.tres`:59). At Half and Quarter, the radial blur.
- Occlusion by the opaque depth with `.r` and `.g` (post:431-437); near fade `smoothstep(minstep, 2·minstep, depth)` (post:438).
- Enclosed fog and altitude fade: `density *= 1 − y·z·(1 − exp(−x·min(b, depth)))` with `ring_enclosed_fog` (post:442-445). The bridge packs the altitude crossfade into the same vector (bridge:781-793).
- Legacy atmosphere on the screen colour. Inactive: `atmospheric_density = 0` (`sunshine_ring_local.tres`:23).

**Which one reflections read: the raw colour and data images.** Reasons:
1. A reflection composites the cloud over a different colour (a screen-texture hit, a backdrop pixel), so it needs colour, coverage and distance separately. The display result is already merged with the screen.
2. The display's occlusion is for the camera's own depth at that pixel. A reflection hit has its own depth.
3. The blur is a sub-texel smoothing at native. Reflection roughness dominates it.

The readers apply the two global terms of the post themselves: the enclosed and altitude fade (published, 2.2) and the near fade on finite hits (helper, 6.2).

### 1.6 Sunshine's own "reflections" image: do not use

`accumulation_textures[view·7 + 6]` receives the final composited screen colour, with `.a` = march end `.g` (post:504-509). It is published only when `reflections_globalshaderparam` is set (gd:2592-2595). It is empty in the game resource (not set in `sunshine_ring_local.tres`).
- It is written by every full-resolution thread into one reduced-resolution texel, which is a race.
- Its distance is the march end, not the cloud distance.
- It is set as a global from inside the callback, which the contact-shadow notes call a draw-error hazard (`scripts/render/RingContactShadows.gd`:77-78).

R3a leaves it alone. R3-1 builds its own copy.

## 2. The publication

### 2.1 Where and what

At gd:2857, after the per-view loop has recorded every command for view 0, and only when `view_count == 1`, the callback records a snapshot under a mutex:

| Key | Value |
|---|---|
| `serial` | `render_callback_serial` |
| `generation` | a counter incremented in the rebuild branch (gd:2231-2253) |
| `color`, `data` | `accumulation_textures[1]`, `accumulation_textures[0]` |
| `raster`, `internal`, `target` | `new_size`, `size`, `buffers.get_target_size()` |
| `divisor` | `resscale` |
| `camera` | `cameraTR` (gd:2695) |
| `projection` | `viewProj` with `z.x = z.y = 0`: the unjittered projection. Engine jitter lives only there (gd:3030-3041) |
| `frame` | the gameplay frame dictionary this callback used (`current_gameplay_frame` in `update_matrices`, gd:3053), for rebase mapping |
| `jump_limit` | `maxf(4000, 2·max_step_distance)` (gd:3269) |
| `menu` | `menu_temporal_sampling` |
| `diagnostic` | raw matte, depth matte or the history-weight diagnostic on |
| `enclosed`, `min_step` | `ring_enclosed_fog`, `min_step_distance` |
| `frames_drawn` | `Engine.get_frames_drawn()` |

The snapshot has two parts, kept separately under the mutex.
- **Content** is every key above except `frames_drawn`, `menu` and `diagnostic`. It is replaced only when this callback is the main view (2.6) **and** it recorded a complete reconstruction of view 0:
  - the march dispatch, plus the fill dispatch when `interleave_active_block > 0`, or a full march otherwise;
  - in code, a local flag `complete_this_callback`, set where those dispatches are recorded (gd:2809-2827).
- **Liveness** is `frames_drawn`, `menu`, `diagnostic` and a `content_overwritten` flag. It is replaced by every main-view callback.

A main-view callback that recorded no complete reconstruction leaves the content as it was. That is the capture hold (2.5). `serial` (published in `params.w`) is the content serial, so it identifies the images, not the last callback.

The snapshot is cleared (under the same mutex) wherever the backdrop publication is cleared:
- `invalidate_cloud_backdrop_texture_publication` (gd:880-882), which `refresh_compute` calls (gd:781);
- `_retire_current_generation` (gd:1468-1469);
- `clear_compute` (gd:1619-1620).

So a snapshot never names a retired texture.

**Why the unjittered projection, and why not the exact march matrix.** A texel's ray is marched through the inverse of the jittered callback projection (hist:59-91). The history reprojects with the current jitter applied to the previous projection, so the jitter cancels and the accumulated texel is centred (gd:3030-3031). The unjittered matrix therefore describes the accumulated image. The error is at most half a full-resolution pixel under FSR2 or TAA, and zero at native without TAA (what ships). Resolution jitter, when on, is ±0.25 of a cloud texel (hist:10-20).

### 2.2 Globals (declared in `project.godot` `[shader_globals]`, format as gd's neighbours, e.g. `project.godot`:123-124, 182-185)

| Global | Type | Content |
|---|---|---|
| `ringworld_cloud_reflection_color` | `sampler2D` | colour image (straight rgb, a) |
| `ringworld_cloud_reflection_data` | `sampler2D` | data image |
| `ringworld_cloud_reflection_dir_to_clip` | `mat4` | `P_unjit · Rᵀ`: world direction (w = 0) to the cloud frame's clip space. R = the march camera's basis, mapped into the reader's presentation frame. No translation. |
| `ringworld_cloud_reflection_params` | `vec4` | x valid weight [0, 1]; y 1/W; z 1/H (cloud image texels); w callback serial mod 2^24 |
| `ringworld_cloud_reflection_params2` | `vec4` | x enclosed-fog density (1/m); y enclosed weight (`ring_enclosed_fog.y · .z`, which carries the altitude fade too); z `min_step_distance` (m); w resolution divisor (1, 2, 4) |

Why direction only:
- The clouds are kilometres away. Camera translation over one frame changes their direction by `v·dt/D`: 0.25 mrad at 1,000 m/s and 2 km, a fraction of a pixel.
- A translation-free matrix also avoids large world coordinates in a float mat4.

**Rebase.** The world can be re-centred, with a rotation, between the march frame and the reader frame. `get_cloud_reflection_publication` maps the snapshot camera into the reader's frame with the existing static `gameplay_frame_remap` (gd:732-772), the same function the history uses (gd:3268-3272). The bridge passes the frame it handed to `set_gameplay_cloud_frame` this `_Process` (bridge:1338). If the remap has a contract but is not valid (a jump above `jump_limit`, i.e. a teleport), the publication is invalid for that frame.

### 2.3 Valid

Valid weight is 0 when any of these holds:
1. No snapshot: before the first dispatch of a generation, or after `refresh_compute`, retirement or `clear_compute` (2.1).
2. The bridge has the compositor off: `_lastRenderOn == false`. This covers clouds off, quality Off, the speed and altitude cutoffs and underground suspension (bridge:2943-2962).
3. Stale: `Engine.GetFramesDrawn() − frames_drawn (liveness) > 2`. This is a safety net for a disabled or skipped main-view callback. A capture hold does not trip it, because held callbacks still refresh the liveness part.
4. Another view has overwritten the images, or may overwrite them before the readers run (2.6):
   - `content_overwritten` is set;
   - or a non-main callback was seen within the last 2 frames;
   - or XR or `view_count != 1`.
5. `menu` or `diagnostic` in the snapshot.
6. Rebase jump (2.2).
7. `_scaledFarViewport.ExactSunshineBackdropActive`. The far backdrop then already composites Sunshine's clouds into the background (`RingScaledFarViewport.cs`:2948-2964), and consumption 2 would double them. It is off today: `CloudTransmittanceMaskEnabled` defaults to false (`RingScaledFarViewport.cs`:213) and nothing sets it.
8. The test hook `CloudReflectionPublicationEnabled` is false.

The following do **not** set valid to 0. Plan 10.5 listed some of them; this design changes that on purpose:
- **History reset, teleport within the jump limit, hard continuity reset.** The image of that callback is a fresh, complete march: `_resolve_interleave_block` falls back to a full march for `first_run` and for zero history (gd:2123-2128), and the history blend is replaced by this frame's value (march:2360-2367). The screen shows the same noisier frame, so the reflection matches it. Invalidating would make the cloud layer of the reflection blink out for a frame, which is the flash the plan wants to avoid.
- **Resize, resolution change, interleave turned on.** These rebuild the generation (gd:2231). The new images are dispatched and the snapshot is replaced in the same callback, and the reader binds the new RIDs together with the new matrix (2.4). So the publication has no gap and no mismatch.
- **Interleave turned off.** No rebuild is needed; the image stays complete.

**Ramp.** When the conditions pass, the weight rises linearly over 0.25 s (est., a constant, not a menu row). When they fail, it drops to 0 at once. The on-screen clouds also vanish at once in every case of the list above, so the reflection never shows clouds the screen does not.

### 2.4 Threading, binding and retirement

Pattern: `scripts/Shadows/RingContactShadows.cs`:72-77, 168-179. One persistent `Texture2DRD` per sampler global. The global's value object never changes; only its `TextureRdRid` is swapped, on the render thread. Replacing a texture from inside a compositor callback is not allowed (`RingContactShadows.gd`:77-78).

Per frame, at the end of the bridge's `_Process`:
1. Main thread:
   - hand the main view's target size and camera transform to the effect with `set_cloud_reflection_main_view(target_size, camera)` (mutex-guarded, as `publish_frame` in `RingContactShadows.gd`:56-60);
   - record `_lastRenderOn`, XR, `ExactSunshineBackdropActive`, the publication toggle and this frame's gameplay frame arguments, under a lock;
   - enqueue one **cached** `Callable` with `RenderingServer.CallOnRenderThread`, so no allocation happens per frame.
2. Render thread (or the main thread in the single-threaded model, where `CallOnRenderThread` runs at once):
   - call `get_cloud_reflection_publication(frame...)`;
   - apply 2.3;
   - if valid: bind the snapshot's `color` and `data` RIDs where they differ from the bound ones; set `dir_to_clip`, `params` and `params2`, each only when its value changed;
   - otherwise: set `params.x = 0`. If the publication returned no RIDs (unpublished), bind the fallback RIDs.

The callable runs after frame N's draw has been submitted and before frame N+1's draw, whatever the thread model: commands execute in order, and in the single-threaded model `_Process` N+1 follows draw N. The model is not set in `project.godot`, so it is the engine default, which was not verified for 4.8-dev7. Either way, the snapshot is callback N and frame N+1's readers read the images callback N wrote. Matrix and RIDs come from one snapshot, so they always match.

**Fallback RIDs.** `get_cloud_reflection_fallback_rids()` creates two 1 x 1 RGBA32F textures once per RenderingDevice, on the render thread: colour `(0, 0, 0, 0)` and data `(0, 0, 0, −8)`. They live outside the generation lifecycle and are released only in `_notification(PREDELETE)` (gd:1329-1406, add to `rids`). A `Texture2DRD` is never pointed at an invalid RID: on that path the RS texture is freed and the RID the global holds dangles.

**Retirement.** A retired generation's RIDs are freed only after the bridge acknowledges (gd:1425-1499; bridge:635-648, which first calls `ReleaseExternalSunshineTextureReceivers`, bridge:651-658). Add to `ReleaseExternalSunshineTextureReceivers`: enqueue a render-thread callable that binds the fallback RIDs and sets the weight to 0, **unless** the bound colour RID is already the current snapshot's. This avoids a needless one-frame drop when the per-frame callable has already rebound the new generation. Because it is enqueued before the acknowledgement's callable, order is guaranteed. `_ExitTree` does the same and publishes zeros.

### 2.5 Capture hold (B7b, test-speed lane)

B7b adds a debug-only `capture_hold` to `SunshineClouds.gd` and the bridge: while held, the cloud march is skipped so stills are stable. It was not in the tree when this design was written. The design follows the described behaviour; R3a-1 re-checks it against the committed code.

Behaviour of the publication during a hold:
- **No content refresh.** Held callbacks record no march (and so no complete reconstruction), so the content part of the snapshot stays as the last complete callback left it (2.1):
  - the colour and data RIDs;
  - the camera, the `dir_to_clip` matrix and the gameplay frame;
  - the content serial;
  - the generation, which advances only on a rebuild.
- **Liveness keeps going.** Held callbacks are main-view callbacks, so they refresh liveness; check 3 of 2.3 does not trip, and **valid stays 1**.
- **The matrix stays right if the camera moves.** It is the camera the held images were marched with. It is remapped to the reader's presentation frame every frame (2.2), so directions keep landing on the right texels of the held images.
- **Leaving the hold.** The content is replaced only by a callback that recorded a complete reconstruction. Publication happens after all of that callback's commands, so a half-written image is never published. If B7b resumes with an interleaved callback, the fill completes the image in the same callback (1.2), and that callback counts as complete only when the fill was dispatched.
- **A rebuild during a hold** (resize, resolution change) retires the generation and clears the snapshot (2.1). New images that have never been marched are never published, so valid is 0 until the first complete march after the hold. This is the "data missing" case.

R3a-1 must check, once B7b is committed:
1. Where B7b skips the march. `complete_this_callback` must be false on held callbacks and true on the first unheld one.
2. That B7b does not write the colour or data images while held (for example by clearing them or by running the fill alone). If it does, the content part must be invalidated on entry to the hold instead. That case is an escalation, because the coordinator's requirement is valid = 1 during a hold.
3. That the post pass and the display still read the held images, so the screen and the reflection agree.

### 2.6 Main view only

The clouds still render in whichever views run the effect today. **Only the publication step filters.**
- At the snapshot step (gd:2857), the callback compares itself with the main view the bridge handed over (2.4 step 1). It is the main view when all of these hold:
  - `view_count == 1`;
  - `buffers.get_target_size()` equals the main target size;
  - its camera is within 8 m and a basis-z dot of 0.999 of the main camera.

  This is the same loose fingerprint as `scripts/render/RingContactShadows.gd`:172-176, loose because the render thread may lag a frame. The callback's own camera is compared, not the content camera, so a hold with a moving camera stays the main view.
- **A non-main callback updates no content and no liveness.** Its march and fill have just overwritten the shared images with another view's clouds. So it only:
  - sets `content_overwritten = true`;
  - records `last_other_view_frame = Engine.get_frames_drawn()`.

  The next main-view callback with a complete reconstruction clears the flag.
- **What the globals hold when another view's callback runs last in a frame:**
  - the RIDs stay bound (they are alive and owned by the current generation);
  - `dir_to_clip` stays the last main-view matrix;
  - `params.x = 0`, from check 4 of 2.3;
  - nothing is unbound or rebuilt.

  A view that renders before the main view inside the same frame would overwrite the images after the bridge's callable has run. That is why check 4 also stays invalid for 2 frames after any non-main callback.
- **Size changes.** If the other view has a different size, Sunshine itself rebuilds the generation (gd:2231). Retirement then clears the snapshot and the fallback is bound (2.4). That case is the existing hazard of question 4, not something the publication causes.

## 3. Reader rules

### 3.1 Direction to uv (exact by construction)

`clip = dir_to_clip · vec4(dir_world, 0)`. The direction is in front when `clip.w > 0`. Then `uv = (clip.xy / clip.w) · 0.5 + 0.5` and texel = `uv · size − 0.5`.

This is the exact inverse of the march's `depthUV = (uv + 0.5) / size`, `ndc = depthUV · 2 − 1`, `dir = basis · normalize(P⁻¹ · ndc)` (hist:59-96). No y flip is needed, because the same `P` is used both ways. A perspective `P` has its translation column only in z, so `w = 0` gives exact xy and w. Image row 0 is texel row 0 in `texelFetch`.

### 3.2 Coverage, colour and distance at reduced resolution (one call, four taps)

Bilinear taps k with weights wₖ, clamped to the image. For each tap:
- `aₖ = clamp(colour.a, 0, 1) · fog(bₖ)`, with `fog(b) = 1 − params2.y · (1 − exp(−params2.x · b))` (the post's term, post:442-445; for a sky direction, `min(b, depth) = b`);
- a tap with non-finite colour or alpha counts as `aₖ = 0`.

From the taps:
- **Premultiplied colour** `= Σ wₖ aₖ rgbₖ`. Filtering premultiplied avoids the dark or bright fringes that straight-colour bilinear gives. The post filters straight, at a sub-texel level that reflections cannot see.
- **Coverage** `= Σ wₖ aₖ`.
- **Distance.**
  - A tap carries a distance only if `aₖ ≥ 0.1` (est., constant `RING_CLOUD_MIN_TAP_COVERAGE`).
  - Its inverse distance is `invₖ = 1 / bₖ` when `bₖ` is finite, `> 0` and `< 65,000`. Otherwise it is 0, which means far.
  - `W = Σ wₖ aₖ` over carrying taps; `I = Σ wₖ aₖ invₖ`.
  - `distance = W ≥ 0.05 (est.) ? (I > 0 ? W / I : FAR) : FAR`, and `distance_weight = W` (0 below the threshold). `FAR = 1e9` m, a sentinel; the code never computes `inf`.

  Why harmonic: placement error and parallax go as 1/D, so averaging 1/D averages what is seen. Infinity enters as 0 without special cases. A near cloud next to "far" gives `D_near / coverage share`, never an arithmetic mean of 2 km and 70 km.
- **Opaque limit** (optional output): the `.a` of the heaviest tap, `≥ 0 ? exp2(.a) − 1 : FAR`. Along that camera direction, cloud data exists only in front of that distance (the march stops at the opaque depth, march:2071-2074).

### 3.3 Half-float limits and "treat as infinity"

- If stores round to fp16 (1.1), `.b` saturates to `inf` above 65,504 m and has about 1/1,024 relative precision: 2 m at 2 km, 32 m at 32-65 km. If they do not, values above 65 km are finite. **Either way:** non-finite, `≤ 0` or `≥ 65,000` means far.
- **Decided (coordinator, 2026-11-17).** No cloud shader change. The reader keeps these half-float-safe rules, so it is right whether a driver stores 16 or 32 bits. R3a-1 only measures the effective precision (4.2, diagnostic line).
- The cloud handoff end on High is of the order of the march length (hist:42-45). Beyond about 65 km the parallax of a 2 m eye height, or of a 300 m camera, is under 5 mrad, and placement does not matter there.
- Empty rays store finite numbers (1.4), so "no distance" comes **only** from the coverage threshold, never from the value.

### 3.4 Temporal lag

The cloud pass is post-transparent (plan 2.1, step 9). An opaque or transparent reader in frame N+1 sees callback N, already mapped to frame N+1's presentation frame (2.2):
- Wind motion: one frame, negligible.
- Transient lightning glow: one frame late in the reflection (3.6).
- A post-transparent reader in the same frame (R3-1's capture) can use the current frame, with no lag (7.3).

### 3.5 Night

- With the sun below the horizon, the march skips each directional light's transmittance march (march:1816-1824). The colour is then the ambient term: the ring's night ambient with a 0.35 floor (march:2115-2128), the eclipse floor of 0.3 (bridge `SetEclipseDarkness`), plus point and segment lights.
- The primary density march is unchanged, so **coverage and distance stay valid**.
- Readers composite by coverage, never by brightness. A dark cloud correctly hides the stars and the planet in the backdrop behind it.

### 3.6 Megastorm and transient light

- **Megastorm:** the wall, its haze path factors and the lightning-flash term of the haze are all inside the colour (march:2504-2557, fill:273-281). Reflections get the wall exactly as the screen shows it.
- **Transient light:** in-cloud lightning glow (history-isolated point lights, transient segment lights) is added after the history, and it **is** in the published colour (march:2541-2543, fill:269-272).
  - **Recommendation: keep it.** A lit cloud's reflection is physically right.
  - Consequence: the reflection lags the sky by one frame during a flash. Check this in the R3a-3 flicker movie.
  - Removing it from the reflection only would need the transient separated in the march, which is a shader change (question 2).
  - Bolt channels (`RingLightningChannelComposite.gd`), the screen flash and the rain haze are separate effects and are not in the data.

## 4. The byte-compare proof

### 4.1 Why publication cannot change the cloud image (by construction)

1. **No GPU work in the callback.** The snapshot is CPU-only. It is taken after every compute and draw command of the callback has been recorded (gd:2857), so it cannot change any of them.
2. **No shared state.** It writes only new variables: the snapshot, its mutex, the generation counter, the fallback RIDs. None of them is read by `update_matrices`, the rebuild branch, the uniform sets or the dispatch sizes.
3. **No change to the images' creation.** A `Texture2DRD` needs `SAMPLING`, which the images already have (the backdrop wrapper relies on it).
4. **Wrappers do not touch the RD texture.** A `Texture2DRD` makes an RS proxy for an RD texture; it neither copies nor transitions it unless a material samples it.
5. **No reader exists at model 0, and the march reads no Godot globals.** R3a-1 adds no material that reads the globals, and no cloud pass reads Godot globals.
6. **The fallback textures are separate RD resources,** created on the bridge's callable.

### 4.2 `cloud_publish_identity_gpu_test` (R3a-1)

- File `tools/cloud_publish_identity_gpu_test.gd`. Windowed Vulkan. It extends `res://tools/atmosphere_visual_test.gd` the way `tools/compositor_temporal_stability_test.gd` does:
  - certified gameplay scene, explorer frozen;
  - local wind, wind field, rain and Sunshine cloud wind off;
  - native, no FSR2 and no TAA, so no engine jitter;
  - day clock paused at a daylight pose, so the lighting march runs. Use the probe's day pose, not the perf night pose.
- Hold still on the effect:
  - `dither_speed = 0`;
  - `resolution_jitter_enabled = false`;
  - `current_time` fixed;
  - noise positions unchanged (wind off);
  - `diagnostic_temporal_readback = true` for every run, so every texture carries `CAN_COPY_FROM` (gd:2313-2314). It applies to both modes, so it does not bias the comparison.
- Cases:
  - **C1** Native, interleave off;
  - **C2** Half, `cloud_interleave = 1` (2x2), which exercises the fill path.
- Per case, warm until static:
  - `macro_map_valid` and not `macro_map_build_active`;
  - the storm map valid;
  - `get_gpu_cloud_shadow_status()` with nothing pending;
  - at least 60 frames.
- Then three runs, **off, on, off**. Each run:
  1. set `CloudReflectionPublicationEnabled`;
  2. call `refresh_compute` on the effect, which rebuilds and sets `first_run` (gd:774-783, 1675-1676);
  3. before the rebuild callback, set `render_callback_serial = 1000` and `resolution_jitter_counter = 0`. The dither phase depends on the serial (gd:3256); the interleave phase resets on `first_run` (gd:2123-2125);
  4. draw K = 12 frames;
  5. read back:
     - colour: `get_cloud_backdrop_texture(0).get_image()`;
     - data: a test-local `Texture2DRD` on `accumulation_textures[0]`, then `get_image()`;
     - final frame: `get_viewport().get_texture().get_image()`.
- Expected: bytes(off) == bytes(on) for all three images, and bytes(off) == bytes(off2) (the control).
  - **If the control differs, the test fails as INCONCLUSIVE** and prints which image and the first differing pixel. A remaining source of nondeterminism has to be found and held; tolerances must not be loosened.
  - Known wall-clock inputs to check first: the cloud-shadow crossfade age (gd:377-378) and macro-map build timing (gd:3770).
- Positive checks in the "on" run:
  - bridge status valid weight > 0;
  - snapshot serial = 1012;
  - `RenderingServer.global_shader_parameter_get("ringworld_cloud_reflection_color")` is the bridge's wrapper, with `TextureRdRid` equal to `accumulation_textures[1]`.
- Negative check in the "off" runs: status reason `disabled`, and the globals never written (the params stay at the declared zero).
- **C3, lightning sequence** (Native, interleave off, and again with C2's settings). From the same reset as above, run two "on" sequences of K = 12:
  - *reference*: no light;
  - *flash*: one transient segment light injected at effect level, `set_segment_lights` with `color.a < 0.5` and `lightning_history_isolation_enabled = true`, so it takes the in-cloud glow path (march:1940), on callbacks 4 and 5 only. Place it in front of the camera inside the cloud layer.

  Read back colour and data after every callback. Expected:
  - (a) the off/on/off identity also holds for the flash sequence;
  - (b) the published colour at callbacks 4-5 differs from the reference, so the glow is published;
  - (c) **from callback 6 on, the published colour and data are byte-equal to the reference.** Transient light never enters the history (march:2541-2543, fill:269-272), so a flash cannot persist;
  - (d) valid weight and content serial behave exactly as in the reference.

  If (c) fails, escalate. It would mean lightning leaks into history, which is not a publication problem.
- **C4, capture hold** (only once B7b is committed; C1 and C2 settings). After 8 callbacks, engage `capture_hold` for 6 callbacks and turn the camera 5 degrees during it, then release. Expected during the hold:
  - content serial, generation, bound RIDs, the colour and data bytes and `dir_to_clip` unchanged from callback 8. The scene's gameplay frame is static, so the remap is the identity;
  - valid weight unchanged (1 after the ramp);
  - liveness `frames_drawn` advancing.

  Expected after release: the content serial advances at the first unheld callback, and the effect's counter `incomplete_content_publications` (a diagnostic: content replaced by a callback without `complete_this_callback`) is 0 throughout.
- **C5, another view** (C1 settings, no byte comparison). A SubViewport of the main size with its own Camera3D, turned 90 degrees and sharing the world, renders for 3 frames and is then removed. Expected:
  - status reason `other_view` and `params.x = 0` from the first frame it renders until 2 frames after the last;
  - content serial not advanced by its callbacks;
  - then the ramp back to 1.
- Diagnostic line, not an acceptance check:
  - `CLOUD_PUBLISH_DATA_RANGE max_b=... over_65504=... fp16_exact_fraction=...`, where the last is the share of `.b` values exactly representable in fp16. It records the effective precision of `.b` on this machine (question 1). The reader's rules do not depend on the result.
- Marker `CLOUD_PUBLISH_IDENTITY_GPU_TEST`. Manifest:
  - copy `mode`, `tier` and `timeout_s` from the `compositor_temporal_stability_test` entry;
  - tags `clouds`, `sunshine-compute`, `reflections`;
  - `expected_s` null until first run.

## 5. Cost

| Item | Cost | Basis |
|---|---|---|
| Callback snapshot (render thread) | one Dictionary of about 18 entries under a mutex: 5 to 15 µs CPU | est. |
| Bridge callable | one GDScript call returning a Dictionary, one remap (a few Transform3D products), at most 5 global writes only when changed: 20 to 50 µs CPU | est. |
| GPU, publication | global uniform buffer update of under 100 bytes when the camera turns; no dispatch, no copy: under 0.001 ms | est.; to be confirmed as "`GPU.Clouds` unchanged within noise" in the matrix (plan 10.5 verification) |
| Reader, one call | 8 `texelFetch` of RGBA32F (4 colour, 4 data), about 30 ALU | count from 6.1 |
| Reader, placed (with refinement) | 2 calls: 16 fetches | |
| Water, 40 % of 1600 x 900 reflecting, placed | 0.02 to 0.04 ms on the 4070 | est.; the cloud image is half or full resolution, so neighbouring pixels share taps |

Within plan 10.3's 0.03 ms for the publication, and the reader is part of R3a-3's measurement. Optimisation if the reader measures high: `textureGather` of colour alpha and data blue for the first call (2 instructions instead of 8). Godot's shading language lists it, but nothing in this repo uses it yet, so the parity test is where it would be proven.

## 6. The include: `materials/ring_cloud_reflection.gdshaderinc` (R3a-2)

### 6.1 Declarations and functions

```glsl
#ifndef RING_CLOUD_REFLECTION_INC
#define RING_CLOUD_REFLECTION_INC
global uniform sampler2D ringworld_cloud_reflection_color : filter_nearest, repeat_disable;
global uniform sampler2D ringworld_cloud_reflection_data : filter_nearest, repeat_disable;
global uniform mat4 ringworld_cloud_reflection_dir_to_clip;
global uniform vec4 ringworld_cloud_reflection_params;   // x valid weight, y 1/W, z 1/H, w serial
global uniform vec4 ringworld_cloud_reflection_params2;  // x enclosed density, y enclosed weight, z min step m, w divisor

const float RING_CLOUD_FAR = 1.0e9;                  // metres; "infinitely far"
const float RING_CLOUD_FAR_LIMIT = 65000.0;          // .b at or above this, or non-finite, is far
const float RING_CLOUD_MIN_TAP_COVERAGE = 0.1;       // est.
const float RING_CLOUD_MIN_DISTANCE_WEIGHT = 0.05;   // est.
const float RING_CLOUD_EDGE = 0.02;                  // est.; uv fraction faded at the image border

struct RingCloudSample {
	vec3 premul;            // premultiplied cloud colour (already hazed), scene-linear
	float coverage;         // [0, 1], enclosed / altitude fade included
	float distance;         // metres from the march camera; RING_CLOUD_FAR when far or unknown
	float distance_weight;  // coverage that carried a distance; 0 = no distance
	float opaque_limit;     // the camera's opaque depth along this direction (metres) or RING_CLOUD_FAR
	float valid;            // params.x * in front * inside * edge fade; 0 => use nothing above
};

// Cloud image uv of a world direction; z = 1 when in front of the march camera.
vec3 ring_cloud_reflection_uv(vec3 dir_world);
// Pure: no built-ins, compiles in canvas_item (for the parity test) and spatial shaders.
RingCloudSample ring_cloud_reflection_sample_uv(vec2 uv, float in_front);
RingCloudSample ring_cloud_reflection_sample(vec3 dir_world);
// One refinement: places the cloud at its distance from the reflecting point.
// origin = reflecting point (world), camera = CAMERA_POSITION_WORLD (passed in, keeps the function pure).
RingCloudSample ring_cloud_reflection_sample_placed(vec3 origin, vec3 dir_world, vec3 camera);
// The post pass's near fade (post:438) for a finite hit at hit_distance metres from the camera.
float ring_cloud_reflection_near_fade(float hit_distance);
// out = src * (1 - coverage * valid) + premul * valid
vec3 ring_cloud_reflection_composite(vec3 src, RingCloudSample s);
#endif
```

Maths, exactly as sections 3.1 and 3.2:
- `ring_cloud_reflection_uv`: as in 3.1. Return `vec3(-1, -1, 0)` when `clip.w ≤ 1e-6`.
- `ring_cloud_reflection_sample_uv`:
  - `size = textureSize(colour, 0)`; `t = uv · size − 0.5`; `i0 = floor(t)`; `f = t − i0`.
  - Four `texelFetch` from each image, at `clamp(i0 + (0,0), (1,0), (0,1), (1,1), 0, size − 1)`.
  - Weights `(1−f.x)(1−f.y)`, `f.x(1−f.y)`, `(1−f.x)f.y`, `f.x·f.y`. Then 3.2.
  - `valid = params.x · in_front · step(0, uv) · step(uv, 1) · smoothstep(0, RING_CLOUD_EDGE, min(min(uv.x, 1 − uv.x), min(uv.y, 1 − uv.y)))`.
  - When `valid == 0`, return zeros with `distance = opaque_limit = RING_CLOUD_FAR`.
- `ring_cloud_reflection_sample_placed`:
  - `s1 = sample(dir)`;
  - if `s1.valid > 0`, `s1.distance_weight > 0` and `s1.distance < RING_CLOUD_FAR`: `d2 = normalize(origin + dir · s1.distance − camera)` and return `sample(d2)`;
  - otherwise return `s1`.
  - For a far cloud `d2 = dir`. For a cloud at D seen from a reflecting point h above the camera ray, the correction is of order `|origin − camera| / D`, the parallax term of plan 4.1.
- `ring_cloud_reflection_near_fade` = `smoothstep(params2.z, 2·params2.z, hit_distance)`.

Only main-view materials may include it. The far and regional far viewports, the minimap and `ring_ocean_global.gdshader` (which the far viewport shares) must not: their world and projection differ, and valid cannot tell. The contract tests of R3a-3 assert that only the files listed there include it.

### 6.2 Parity test `cloud_reflection_include_gpu_test` (R3a-2)

- File `tools/cloud_reflection_include_gpu_test.gd`. Windowed Vulkan, in the style of `tools/cloud_depth_weighting_gpu_test.gd`, with no gameplay scene.
- It renders a `canvas_item` shader that includes the file into a SubViewport with `use_hdr_2d = true`, and reads back float images.
- The globals are set to synthetic data: `ImageTexture` RGBAF, nearest, and known matrices.
- Cases:
  1. **Reprojection round trip.** Cameras:
     - identity;
     - a yawed and pitched basis;
     - a jittered projection with `z.xy = (3e-4, −2e-4)`, of which the include gets the unjittered copy.
     Sizes 800 x 450 and 801 x 451 (odd, as the post guards, post:323-330).

     Per output pixel = cloud texel (i, j), the shader rebuilds the march ray exactly as hist:82-96 (copy the formula into the test shader with a comment citing those lines), maps it back with `ring_cloud_reflection_uv`, and writes the residual `uv · size − (i + 0.5, j + 0.5)`. Residuals are small, so fp16 output keeps the precision.

     Acceptance:
     - |residual| ≤ 1e-3 texel with the unjittered matrix;
     - with the jittered P marched but the unjittered P published, |residual| ≤ the jitter in texels + 1e-3.

     Also a GDScript double-precision reference for 16 directions (CPU inverse of the same matrices), compared with tolerance 1e-4 in uv.
  2. **Behind and edges.** A direction behind the camera gives valid 0. uv 0.001 from the border gives valid < 0.1, and uv 0.5 gives `params.x`.
  3. **Filter.** Each case is a 2 x 2 tap pattern sampled at fractions 0, 0.25 and 0.5. The GDScript reference implements 3.2 in doubles; tolerance 1e-3 relative.

     | Taps (a, b) | Expected |
     |---|---|
     | (1, 2,000) beside (0, 70,000) | distance 2,000 at every fraction |
     | (1, 2,000) beside (1, 8,000) at 0.5 | 3,200 (harmonic) |
     | (1, 1e5), (1, NaN), (1, inf) | FAR, distance_weight 1 |
     | all a = 0.05 | distance_weight 0, distance FAR, coverage 0.05 |
     | colour (1, 0, 0, a 1) beside (0, 0, 1, a 0) at 0.5 | premul (0.5, 0, 0), coverage 0.5 |
     | one tap with NaN colour | ignored |
     | `params2 = (1, 0.5, ·, ·)` | coverage x 0.5 |
     | `params.x = 0` | all outputs zero |
     | fallback data (0, 0, 0, −8) | opaque_limit FAR |
  4. **Placement.** With origin = camera, `sample_placed` equals `sample`. With origin 300 m below the camera and a uniform cloud at 3,000 m, the second direction differs by the expected angle (GDScript reference).
- Marker `CLOUD_REFLECTION_INCLUDE_GPU_TEST`. Manifest:
  - copy the `cloud_depth_weighting_gpu_test` entry (`gpu-local`, `extended`, timeout 300);
  - tags `clouds`, `reflections`.

## 7. Consumption

All three consumption points run only at `ringworld_reflection_model = 1` (plan 10.2).

### 7.1 Compositing over a colour source without clouds (plan consumption 2)

`hit = src · (1 − s.coverage · s.valid) + s.premul · s.valid`.

This is the display's own `mix(src, rgb, a)` (post:490), with `rgb · a` premultiplied and the enclosed and altitude fade inside `coverage`. No extra haze: the cloud colour carries the haze to the cloud along the camera ray, and `src` carries its own. This agrees with R1-2-R1-5 section 2.3.

Which direction to sample:
- **Hit at infinity (backdrop pixel):** `ring_cloud_reflection_sample_placed(water_world, refl_dir, CAMERA_POSITION_WORLD)`. For a far cloud it is the reflected direction, as R1-2-R1-5 section 4 says; for a cloud a few kilometres away it adds the parallax correction.
- **Finite hit at camera distance D:** `ring_cloud_reflection_sample(normalize(hit_world − CAMERA_POSITION_WORLD))`, with coverage and premul scaled by `ring_cloud_reflection_near_fade(D)`. The march already stopped at the opaque depth along that camera ray (march:2071-2074), so no other depth test is needed. This agrees with R1-2-R1-5 section 4.
- **Misses (sky term):** nothing. Off-screen directions stay with the radiance map (R2d).

### 7.2 Hit placement by the cloud distance (plan consumption 3)

On a hit at infinity, use `s.distance` as the hit distance when `s.distance_weight ≥ 0.5` (est.; R1-2-R1-5 uses the same value) and `s.distance < RING_CLOUD_FAR`. Otherwise the hit stays at infinity. R1-5's height guard then uses `parallax = 2h / D_cloud`.

### 7.3 The far mirror's distance channel (R3-1, plan consumption 1)

The far-mirror capture runs post-transparent. If it is ordered after the Sunshine effect, it can read the **current** frame's images: no lag, and the screen-to-cloud mapping is the identity, `uv_cloud = uv_screen + jitter_uv` as the post pass does (post:341). It is an RD compute pass, so it cannot use the gdshaderinc:
- it takes the RIDs on the render thread from `get_cloud_reflection_publication`;
- it ports 3.2 to GLSL, with a comment pointing at the include, and a parity case added to `cloud_reflection_include_gpu_test`.

Distance channel per pixel, consistent with the colour blend `bg · (1 − cov) + cloud`:
- `inv = (1 − cov) · inv_bg + cov · inv_cloud`, with `inv_bg = 1 / opaque distance` (0 for backdrop) and `inv_cloud` = 0 when far;
- `D = inv > 0 ? 1 / inv : FAR`.

Same harmonic rule as 3.2, so a lake far from the camera places a cloud reflection by parallax just as it places terrain.

### 7.4 Where this design and R1-2-R1-5 section 4 meet

| R1-2-R1-5 section 4 says | This design | Action |
|---|---|---|
| "if the colour is not premultiplied, use `cloud_rgb · coverage`" | it is not; the include returns `premul` already | none |
| infinity hit: sample by `dir_world` | same function family; the placed variant equals it for far clouds and corrects near ones | R3a-3 uses `_placed` |
| invalid on "first frame, resize, teleport, history reset, cloud pass off" | valid is a weight; it does not drop on resize or history reset (2.3) | `keep = mix(storm_ssr_keep, 1.0, s.valid)` works unchanged |
| "Lightning … not in the cloud data" | in-cloud transient glow **is** in it (3.6); bolts and screen flash are not | tell the R1-2-R1-5 lead; no interface change |
| threshold 0.5 for the hit distance; 65,000 m rule | same | none |

## 8. Work orders

Runner: `tools/run_tests.ps1`. Run `-DryRun` first and report the selection. The explicit `-Tests` lists below define the expected selection. Do not run `-Changed` on this branch: it reselects the whole branch.

### R3a-1. Publication (implementer; after B7b is committed)

**Depends on** test-speed order B7b (`capture_hold` in `SunshineClouds.gd` and `RingSunshineCloudBridge*.cs`). Start from the committed B7b code, and re-read its hold path before writing `complete_this_callback` (2.5, the three checks). The line numbers in this design predate B7b: re-anchor them by symbol.

**Objective.** Publish the colour and data images, the direction-to-clip matrix and the parameters (section 2) from the main view only, keep them during a capture hold, and do not change a byte of the cloud image.

**Files and symbols.**
- `addons/SunshineClouds2/SunshineClouds.gd`:
  - new members:
    - `cloud_reflection_publication_enabled := true`;
    - `_cloud_reflection_mutex`;
    - `_cloud_reflection_content := {}` and `_cloud_reflection_liveness := {}`;
    - `cloud_reflection_generation := 0`;
    - `cloud_reflection_fallback_textures : Array[RID] = []`;
    - `_last_callback_gameplay_frame := {}`;
    - the main-view key (`_cloud_reflection_main_target`, `_cloud_reflection_main_camera`);
    - `last_other_view_frame := -1000`;
    - the diagnostic counter `incomplete_content_publications := 0`;
  - new `set_cloud_reflection_main_view(target: Vector2i, camera: Transform3D)`;
  - `_render_callback`:
    - the rebuild branch (increment the generation);
    - a local `complete_this_callback`, set where the march and fill are recorded;
    - after gd:2858: the main-view test and the content and liveness update (2.1, 2.6);
  - `update_matrices`: store `current_gameplay_frame` into `_last_callback_gameplay_frame`, one line after gd:3053;
  - `invalidate_cloud_backdrop_texture_publication`, `_retire_current_generation`, `clear_compute` (clear the snapshot);
  - `_notification` (release the fallback RIDs);
  - new `get_cloud_reflection_publication(frame: Transform3D, around: float, across: float, radius: float, has_frame: bool) -> Dictionary`;
  - new `get_cloud_reflection_fallback_rids() -> Array`.
- New `scripts/RingSunshineCloudBridge.CloudReflection.cs` (partial of the bridge): the two `Texture2DRD`s, the cached callable, the 2.3 checks, the ramp, status diagnostics (`CloudReflectionStatus`: weight, reason, serial, generation, raster, divisor), the test hook `[Export] bool CloudReflectionPublicationEnabled`.
- `scripts/RingSunshineCloudBridge.cs`:
  - `_Process` end: call `QueueCloudReflectionPublication(delta)`;
  - `ReleaseExternalSunshineTextureReceivers`: the conditional fallback bind (2.4);
  - `_ExitTree`: zeros and fallback;
  - near bridge:1338: keep the last frame arguments.
- `project.godot` `[shader_globals]`: the five globals of 2.2. Append only. The file has other lanes' uncommitted edits: do not reformat.
- New `tools/cloud_publish_identity_gpu_test.gd` and its manifest entry (4.2).

**Required behaviour.** Sections 2.1 to 2.6 and 4.2, cases C1-C5. The bridge calls `set_cloud_reflection_main_view` every `_Process`. The `get_cloud_reflection_publication` result keys:
- `valid`, `reason`, `serial`, `generation`;
- `color`, `data`;
- `raster`, `target`, `divisor`;
- `dir_to_clip` (Projection), `camera_origin` (mapped);
- `enclosed`, `min_step`, `frames_drawn`;
- `menu`, `diagnostic`;
- `content_overwritten`, `last_other_view_frame`.

`dir_to_clip = projection_unjittered * Projection(Transform3D(mapped_camera.basis.orthonormalized().inverse(), Vector3.ZERO))`.

**Do not change.**
- Any `.glsl`, `.glslinc` or `.comp` file. No reimport is needed, because none changes.
- Any existing RD resource, usage bit, uniform set, dispatch, or the order of commands in `_render_callback`.
- `reflections_globalshaderparam` and the image at `view·7 + 6`.
- The existing backdrop publication and its consumers.
- No new menu row, and no material reads the new globals in this order.
- Which views the clouds render in, and B7b's hold behaviour. The publication only observes both.

**Validation.**
1. `dotnet build` clean.
2. `tools/run_tests.ps1 -Tests cloud_publish_identity_gpu_test,cloud_depth_weighting_gpu_test,compositor_temporal_stability_test -Label r3a1 -DryRun`. Expect exactly those three. Then run without `-DryRun`.
3. `tools/run_tests.ps1 -Since HEAD -Label r3a1 -DryRun` and report its selection. Run it if it adds cloud tests beyond step 2 (for example `cloud_history_*`, `rain_cloud_compositing_gpu_test`, `cloud_atmosphere_gpu_test`, which use the backdrop texture).
4. Report the `results.md` summary line, every non-pass row, and the `CLOUD_PUBLISH_DATA_RANGE` line.
5. Run `tools/testing/validate_manifest.ps1` (via the runner) and `tools/prune_test_output.ps1 -Apply`.

**Escalate** (stop and report, do not work around) if:
- the off-on-off control differs (nondeterminism) after holding the two wall-clock inputs of 4.2;
- off differs from on;
- binding a `Texture2DRD` to `accumulation_textures[0]` fails (format or usage);
- `GlobalShaderParameterSet` from the render-thread callable raises errors;
- a second view's callback is seen to overwrite the images in normal play (status reason `other_view` for more than a frame at a time);
- B7b writes the colour or data images while held, or resumes with a callback that does not complete the image (2.5, check 2);
- C3 (c) fails, which would mean the lightning glow persists through history.

### R3a-2. Include and parity test (implementer, after R3a-1; independent of B7b)

The include and its test touch neither `SunshineClouds.gd` nor the bridge. R3a-2 needs only R3a-1's `project.godot` globals. It can also start before R3a-1 if those five declarations are added first as a tiny separate step.

**Objective.** `materials/ring_cloud_reflection.gdshaderinc` exactly as section 6.1, and `tools/cloud_reflection_include_gpu_test.gd` with its manifest entry, as section 6.2.

**Do not change.** Any existing shader. No shader includes the new file in this order.

**Validation.** `tools/run_tests.ps1 -Tests cloud_reflection_include_gpu_test -Label r3a2 -DryRun`, then run it, and report the summary line and the residual maxima per case.

**Escalate** if:
- `global uniform mat4`, the `struct` return or `texelFetch` on a global sampler does not compile in `canvas_item` on 4.8-dev7;
- any residual exceeds its bound;
- the 801 x 451 case shows a half-texel offset (it would mean the march's texel convention differs from hist:59).

### R3a-3. Consumption (implementer, after R3a-2, R1-5a and B7b; part b after R3-1)

Depends on B7b as well, because its stills and flicker movies are captured with the cloud march held stable where a still needs it. A hold must not change what the water shows (2.5).

**R3a-3a, water.**
- Files: `materials/ring_ocean_proxy.gdshader` (model 1 branch only) and `tools/water_reflection_model_contract_test.gd` (R1-2-R1-5's new test).
- Changes, per section 7.1 and 7.2 and R1-2-R1-5 section 4:
  - include the file;
  - composite on infinity hits with `_placed` and on finite hits with the camera-ray sample and near fade;
  - set the hit distance from the cloud when `distance_weight ≥ 0.5`;
  - `keep = mix(storm_ssr_keep, 1.0, s.valid)`;
  - debug mode 3 shows the cloud distance.
- Contract assertions:
  - the proxy includes `ring_cloud_reflection.gdshaderinc` and calls `ring_cloud_reflection_sample_placed(` inside the model 1 path;
  - `ring_ocean_global.gdshader` and every far-viewport shader do **not** include it;
  - the model 0 path is textually unchanged.
- Verification, with model 0 against model 1 in the same run:
  - stills of a lake under broken cloud by day, at night and under a storm; a megastorm frame;
  - a 10 s flicker movie turning at the lake and during a lightning storm (`tools/media/ffmpeg_tools.ps1 flicker`; look for a one-frame lag flash, section 3.6);
  - the matrix for `GPU.Clouds` and the water pass, against plan 10.3.
- Escalate if: the stills show doubled clouds (check `ExactSunshineBackdropActive`); a seam appears at the cloud-image border; or the cost is above 0.05 ms at the lake pose.

**R3a-3b, far mirror.** In R3-1's order: the distance channel of 7.3 and a GLSL port of 3.2 with a parity case in `cloud_reflection_include_gpu_test`. Written when R3-1 is designed.

## 9. Questions for the primary agent

Questions 1 to 3 were answered by the coordinator on 2026-11-17.

1. **Storage precision. Decided:** no cloud shader change. Memory is RGBA32F (gd:2310), but the march and the fill declare `rgba16f` (march:48-49, fill:34-35). R3a-1 measures the effective precision of `.b`, and the reader keeps the half-float-safe rules (3.3), so it is right on any driver. The mismatch may still matter outside reflections (validation layers, other GPUs).
2. **Lightning glow. Decided:** it stays in what reflections read, one frame late. A flash that persists is not acceptable, and R3a-1's case C3 proves it does not persist.
3. **Validity semantics. Decided:** as designed in 2.3. A reflection one frame stale after a teleport is acceptable.
4. **A shared-world view running the cloud effect** (minimap, capture SubViewport) would overwrite the images, and with a different size trigger a full rebuild every alternate frame (gd:2231). The callback has no main-view filter of its own. Rendering stays as it is. The publication filters itself (2.6) and reports `other_view`. Is any such view known to render with the main WorldEnvironment's compositor?

## 10. Verified and estimated

**Read and verified in code (line references above):**
- the image formats, indices and sizes;
- the order of passes in the callback;
- that the post pass only samples the colour and data images;
- what each channel holds and where it is written;
- straight colour, the haze and transient light in `rgb`;
- the night skip;
- the empty-ray value of `.b`;
- the jitter handling;
- the generation, retirement and acknowledgement mechanics;
- the full-march fallback on `first_run` and zero history;
- the contact-shadow binding pattern;
- the far backdrop's exact-Sunshine path and that it is off;
- the rebase remap;
- the existing "reflections" image and its race.

**Not verified:**
- B7b's `capture_hold`: designed against its described behaviour, because it was not in the tree. R3a-1 re-checks it (2.5);
- whether stores round to fp16 on the 4070 (question 1; the test measures it);
- the engine's default thread model on 4.8-dev7 (the design holds for both);
- that `global uniform mat4`, struct returns and `textureGather` compile in 4.8-dev7 gdshaders (R3a-2 proves the first two; the third is optional);
- the handoff end on High (the order of 65-70 km is inferred from `max_step_count` and `max_step_distance`).

**Estimates:**
- the thresholds: 0.1 tap coverage, 0.05 distance weight, 0.5 hit distance weight, 0.02 edge, 0.25 s ramp;
- every cost figure in section 5;
- the parallax magnitudes in 2.2 and 3.3.

## Addendum: state of B7b when this design was handed over (plan author, 2026-11-17)

Sections 2.5 (capture hold), 2.6 (main view only), the test cases C3 to C5 and the decisions in section 9 were written before B7b was in the tree. Since then:
- B7b is committed as 78b8425. It adds the debug-only `capture_hold` to `addons/SunshineClouds2/SunshineClouds.gd` only; **the bridge (`scripts/RingSunshineCloudBridge*.cs`) was not touched**, although 2.5 and section 8 name it.
- R3a-1 therefore starts by reading the committed `capture_hold` code and checking 2.5 against it, as section 10 already asks.
- Both Sunshine files are free of other lanes.
