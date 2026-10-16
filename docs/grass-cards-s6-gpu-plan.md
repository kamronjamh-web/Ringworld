# Grass cards S6: remove hero blades, GPU card compute, card grouping (implementation plan)

Status: read-only design by the Lead, 2026-10-15. Nothing in the code has been changed.

Baseline: the working tree with S5 uncommitted, as of `25c0c4b` plus the diff. The build is 0 warnings / 0 errors.

Line numbers come from that tree. Re-verify any reference marked [RV] before editing.

Binding inputs:
- `docs/grass-cards-integration-plan.md`, USER DECISIONS (later):
  - hero blades are scrapped (S7 dropped);
  - card placement moves to the GPU compute path (S6);
  - adaptive grouping is re-enabled for Cards.
- Streaming invariants 1-8 from `docs/grass-cards-redesign.md`, especially idle = 0.
- `AGENTS.md` feature convention: new settings go in the grave-key menu and are persisted.

## 0. Key design decisions

1. **The GPU does conservative culling, and the card shader keeps the final say.**
   - The compute card branch accepts an instance when all of these hold:
     - `rank < keep(d')` and `growIn >= minGrowIn`;
     - the low layer passes `LowKeep(d')`;
     - `d' < reach`;
     - it passes a conservative frustum test.
   - Here `d' = max(0, d - margin)`.
   - `keep`, `growIn` and `LowKeep` never increase as `d` grows. So the GPU set is a superset of what the shader would show for any camera within `margin` of the compute camera. That covers deferred batches and frame latency.
   - The vertex shader still recomputes `scale` per vertex with the exact camera distance, as the CPU path does today.
   - Result: the GPU path and the CPU path draw identical pixels. The CPU MultiMesh path stays both the fallback and the visual reference.
2. **Output rows are copied unchanged** (20 floats: transform, COLOR biome weights, CUSTOM). This is the S4 instance contract, so the card shader is not touched by S6.
3. **The tier ladder stays CPU residency.** A tile holds the rank prefix of its tier, as in S5. The GPU applies the continuous per-instance keep on the Bayer rank decoded from CUSTOM.x. There are no new GPU tiers.
4. **The curve lives in a GLSL twin inside the compute shader.** A Godot `.gdshaderinc` cannot be included from RD compute GLSL: it uses `uniform` declarations, and the raw-source compile path in `LoadComputeShader` does not resolve `#include`. Both twins are anchored to `RingGrassCardCurve.cs`:
   - existing `curve` mode for the `.gdshaderinc`;
   - new GPU parity test for the compute twin.
5. **Grouping reuses the existing intermediate-slot machinery.** A Cards group resident uses `IntermediateRenderer` / `GpuIntermediateBatch` / `IntermediateWanted` exactly as Blades groups do, with the card mesh and material. Card leaves keep using the near slot.
6. **The per-tile `grass_terrain_origin_uv` needs no change for groups.**
   - The uv mapping is affine in the canonical offset from the renderer's origin. The group renderer already gets its own `grass_terrain_origin_uv`, gust origin and covectors from the group bounds through `RefreshTilePresentation` (PresentationCache.cs:222-232). That is exact to well under 1 mm over a 128 m group.
   - Linearisation error is about `s³/6R² + Δh·s/R`. With R = 5000 km and s ≤ 90 m that is below 2 mm.
   - No per-instance data and no group-relative baking are needed. A test proves the equivalence.
7. **Cards groups contain only floor-tier tiles.** The effective Cards grouping start is pushed past the last ladder boundary plus hysteresis. A tier change, and the group split it would cause, therefore never happens inside an active group during normal movement.
8. **Cards groups need the GPU path**, which is also the existing rule for Blades groups. The CPU fallback renders ungrouped leaves, as S5 does today.

---

## Part A: remove the hero blades

Goal: delete all hero code, shaders, assets and tests.
- Cards keep full density at the camera. Card hero thinning is removed outright, not left as an override set to 1.
- Card placement must stay byte-identical, and so must everything in Blades.
- In-game Cards look is unchanged, because S5 already forced `HeroThinMinimum = 1`.

### A.0 Pre-step: record a card golden before any edit

The repo has no card-placement golden; only `BladeGolden` exists.
1. Before touching the builder, add a `CardGolden` dictionary to `tools/GrassCardPlacementTest.cs` next to `BladeGolden` (:29).
2. Fill it with `PlacementHash` and `InstanceCount` for:
   - the meadow tile at tier 0 (with low layer), tier 4, tier 8 and tier 13 (`FindMeadowTile` + `BuildCards`);
   - one seam tile.
3. Capture the values by running the current tree once.
4. Assert them in a `TestCardGolden` step that runs after `TestBladeGolden` (:53).

The hero removal must leave this test green.

### A.1 Delete (with their `.uid` files)

- `scripts/Voxel/LOD/RingGrassHeroCellBuilder.cs` (+ `.cs.uid`)
- `scripts/Voxel/LOD/RingGrassHeroTuftMeshFactory.cs` (+ `.cs.uid`)
- `materials/ring_grass_hero.gdshader` (+ `.gdshader.uid`)

### A.2 Strip hero references

| File | Change |
|---|---|
| `RingGrassCardCurve.cs` | Remove the `HeroThinMinimum` / `HeroThinStartMeters` / `HeroThinEndMeters` record parameters (:12) and `HeroThin()` (:77-82). In `Scale()` (:91) drop the `* HeroThin(...)` factor. Remove the hero clause from the doc comment. |
| `ring_grass_card_curve.gdshaderinc` | Remove `uniform vec3 grass_card_hero_thin` (:15) and `grass_card_hero_thin_factor` (:43-46). In `grass_card_curve_scale` drop the factor (:54). |
| `ring_grass_cards.gdshader` | Delete line 94 (`scale *= grass_card_hero_thin_factor(rank, dist);`). Nothing else changes. |
| `RingGrassCardMaterialConfig.cs` | Remove `HeroShaderPath`, the `Hero*` properties (:54-58), `CreateHeroMaterial`, `ApplyHero` and the `grass_card_hero_thin` line in `ApplyCurve` (:141). Rewrite the comments "card and hero" as "card". `InstanceUniformCount` stays 9. |
| `RingGrassCardAssets.cs` | Remove `HeroMesh`, `HeroArrayMesh` and the `RingGrassHeroTuftMeshFactory.BuildDefault()` call (:84, :88). Update the doc comment. Do **not** bump `GeneratorVersion`: the atlas is unchanged and the cache key only hashes atlas parameters. |
| `RingGrassCardPlacement.cs` | Remove `HeroUnitHeight`, `Hero`, `HeroDensity` and `HeroHeightScale`. Change the enum to `{ Card = 0, Low = 1 }` and add the comment `// 2 was the retired hero kind; never reuse it (CUSTOM.x encodes kind*4)`. Clean up the comments. |
| `RingGrassStyleProfile.cs` | Remove the `HeroThinMinimum = 1f` override and its comment (:63-68). The result is `RingGrassCardCurveParameters.Default with { ReachMeters = reach }`. Neither fingerprint hashes hero fields, so `PlacementFingerprint` and `CoverageFingerprint` are unchanged. |
| `RingGrassTileBuilder.cs` | See the bullets after this table. |
| `ring_grass_card_color.gdshaderinc`, `ring_grass_card_common.gdshaderinc` | Comment-only: drop "hero". |

`RingGrassTileBuilder.cs` changes:
- Remove `HeroLatticeSalt` (:66).
- :139: drop `&& !cards.Hero`.
- `CardShape`: delete the hero branch (:186-190), so `cardWidth = .45f + nominal * 1.1f` is unconditional for non-Low kinds.
- :315: clump radius becomes `Mathf.Clamp(bladeWidth * .5f, .3f, .7f)`.
- :326: becomes `(int)cardKind * 4 + variant + cardRank * .999f`.
- :337: becomes `if (cards!.Lean > 0)`.
- `RunCardLattice`: the `if (kind != Hero)` block (:371) runs unconditionally.
- :389: becomes the two plain `RunCardLattice` calls.
- Return statement (:450-452): `sourceFraction`, `tierKeys`, `isCardPlacement: true`.
- Fix comments (:170, :210, :314).

Optional: leave `RingGrassMeshData.Custom0Rg` in place (it is generic, and removing it is churn).

### A.3 Tests

**`tools/GrassCardAssetsTest.cs`**
- Delete the hero mesh contract (:125-143).
- Drop `HeroArrayMesh` from the cache-identity check (:156).
- Delete the spike hero parity (:213-224).
- The parity print line (:225) loses `heroMesh` and `heroRootMaxErr...`.
- Atlas and card-mesh spike parity stay as they are.

**`tools/GrassCardPlacementTest.cs`**
- `TestCurve`: remove `keepHero` and `thin` (:182-183), so the expected scale is `expected` alone. Delete the three `heroThin` checks (:194-196).
- Delete `TestHero` (:523-551) and its call (:63).
- Delete the hero block of the site-mask test (:585-587).
- Delete the hero timing in `ReportCostAndCounts` (:611-612). Keep the cards and blades timings.

**`tools/GrassCardShaderFixture.cs`**
- Remove `HeroMaterial`, `HeroInstances` and `SetHeroThinning`.
- Remove the hero branch of `BuildField` (:267-292). The signature becomes `BuildField(camX, camZ, eye, reach, cards, seed)`, or keep the `cards` bool.
- Remove the `hero` parameter of `Emit`; kind 2 is gone.
- Remove the `ApplyHero` calls and the hero follower registration (:332).
- `CompareCoveredMeans` may stay as a generic helper.

**`tools/grass_card_color_match_gpu_test.gd`**
- Remove the `"hero_card"` pose flags (:29-30), `_hero_vs_card` (:407-424) and its call (:392-396).
- Remove every `SetHeroThinning` call.
- Remove the hero shader from `_audit_shaders` (:241) and `HeroMaterial` from `_audit_followers` and `_set_all`.
- `_rebuild_field` becomes cards-only.
- Measure states become `[["hidden", false], ["cards", true]]` with a cards-only count string.
- Update the header comments.

Unrelated "hero" hits, verified (word fragments of `WeatherOverride` / `OwnerKey` / `weatherOk`):
- `RingCloudProviderController`, `RingLocalWeatherEffects`, `RingSunshineCloudBridge`;
- `MinimapWindContractTest`, `UndergroundModeRoundTripSmoke`, `WeatherAmbienceDiagnosticRunner`;
- `RainPrecipitationTestDriver`, `SharedWindCoreTests`, **`GrassQualityValidation`** (`SetExternalWeatherOverride`, nothing to do);
- a prototype `MANIFEST.json` preview name.

Leave the spike untouched: `scenes/grass_cards_spike`, `materials/grass_cards_spike/*`, `scripts/grass_cards_spike/*`, `tools/grass_cards_spike_*.gd`. It builds on its own.

### A.4 Outputs that change, and how to verify them

| Output | Expected |
|---|---|
| Blades fingerprints (`GrassCardStyleTest` `BladesDefaultFingerprint`/`BladesMaxedFingerprint`), `BladeGolden` 12/12 | **unchanged** (gate) |
| New `CardGolden` (A.0) | **unchanged** (gate) |
| Atlas hash / coverage, card mesh spike parity | unchanged |
| `PlacementFingerprint` / `CoverageFingerprint` | unchanged (no hero fields hashed) |
| Colour match `color` mode | `day_near`/`day_down` change, because 0-7.5 m is now full-density cards instead of thinned cards plus hero. Re-run; the gates stay as they are (≤3 day, ≤4 dusk/night/haze, ≥8 blocks), 9/9 PASS, no `GRASS_S4_HERO_CARD` lines. Record the new numbers in the handoff. |
| Colour match `curve` mode | C# and GLSL both lose the factor; must stay PASS (worst ~1e-6). |
| `measure` mode | Cards-only numbers. Informational; note the machine (the 4070 numbers are not comparable with the A1000). |

Final grep gate: `grep -rniE "hero" scripts/Voxel materials/ring_* tools/GrassCard* tools/grass_card* tools/shaders` returns nothing.

---

## Part B: S6, compute card mode

### B.1 Backend (`RingGpuVegetationCompute`, GLSL); does not touch layer files

**New file `scripts/Voxel/LOD/RingGrassCardComputeParameters.cs`.** It must be `public`, because it appears in the public `BatchFrameParameters`.

```csharp
public readonly record struct RingGrassCardComputeParameters(
    float ReachMeters, float NearDensity, float BaseDensity, float FloorDensity, float FalloffExponent,
    float FalloffStartMeters, float NearBoostStartMeters, float NearBoostEndMeters,
    float LowLayerFullMeters, float LowLayerEndMeters, float MinimumGrowIn,
    float DistanceMarginMeters, float GuardWidthFactor, float GuardHeightFactor)
{
    // Card horizontal half-extent per unit width scale = 0.46 * 1.6 * 2.5 <= BoundsHorizontalExtent (2.0), applied to the
    // Frobenius radius sqrt(|X|^2+|Z|^2) >= |X|. Height: compensation 2.5 x (1 + wind lateral .58 + tilt .08) + 1 (interaction).
    public const float DefaultGuardWidthFactor = RingGrassCardPlacement.BoundsHorizontalExtent;
    public const float DefaultGuardHeightFactor = RingGrassCardPlacement.BoundsHeightScale * 1.66f + 1f;
    internal static RingGrassCardComputeParameters From(in RingGrassCardCurveParameters c, float marginMeters);
    /// C# reference of the GLSL card branch (tests only). Same operation order as the GLSL.
    public static bool ReferenceAccepts(float customX, float cameraDistance, in RingGrassCardComputeParameters p);
    /// Tests: true when float noise could flip the decision (see B.4).
    public static bool ReferenceAmbiguous(float customX, float cameraDistance, in RingGrassCardComputeParameters p);
}
```

- `From` sanitises values: finite, margin clamped to [0, 8].
- `MinimumGrowIn` comes from `RingGrassCardCurve.MinimumGrowIn`.
- The reference decodes the rank exactly as the shader does:
  ```
  slot = floor(x)
  rank = (x - slot) / 0.999f
  low  = floor(slot * .25f) > .5f
  ```
- It then evaluates `d' = max(0, d - margin)`, `d' < reach`, and finally `RingGrassCardCurve.Keep`/`GrowIn` or `LowKeep`/`GrowIn` with the same parameters. Reuse the curve methods directly; do not copy them.

**`RingGpuVegetationCompute.cs`**
- `ParameterBytes` 256 → **512** (:14).
- Add `private const int ParameterWords = ParameterBytes / 4;` and use it for `ParameterWords = new float[64]` (:44).
- `BatchFrameParameters` (:19-25): append `RingGrassCardComputeParameters? GrassCard = null`. The default keeps every tree and blade call site source-compatible.
- `ParameterData` (:769-805): after the existing writes, `if (p.Grass && p.GrassCard is { } c)` write:

| word | value |
|---|---|
| p[62] | 1 (card mode) |
| p[63] | reserved, 0 |
| p[64] | reach |
| p[65] | near density |
| p[66] | base density |
| p[67] | floor density |
| p[68] | falloff exponent |
| p[69] | falloff start |
| p[70] | near boost start |
| p[71] | near boost end |
| p[72] | low full |
| p[73] | low end |
| p[74] | min grow-in |
| p[75] | distance margin |
| p[76] | guard width factor |
| p[77] | guard height factor |
| p[78..127] | 0 |

  Words 0..61 keep their exact current meaning and values. `Array.Clear(data)` already zeroes the new words for trees.
- Refactor `ParameterData` into `internal static void WriteParameterWords(FrameRequest, BatchFrameParameters, bool treeCustomData, bool treeDitherData, float prototypeRadius, float[] data)` plus the byte copy, so the layout test can call it without RD. **First** add the wrapper with no layout change and capture the golden (B.4 test 1); then change the layout.
- `FrameEquals` (:914-921): add `|| !Nullable.Equals(x.GrassCard, y.GrassCard)`.
- Nothing else changes: dispatch, budgets, grouped phases, mirrors and retirement. Card batches are `Grass = true`, stride 20.

**`materials/vegetation_gpu/ring_gpu_vegetation_indirect.glsl`**, `accepted()` (:31-87):
- Insert the card branch right after the `if (!grass) {...}` block (after :49), before the blade frustum and LOD code.
- `card_smooth` must use the explicit formula, not `smoothstep`, so the op order matches C#.

```glsl
float card_smooth(float a, float b, float x) { float t = clamp((x - a) / (b - a), 0.0, 1.0); return t * t * (3.0 - 2.0 * t); }
float card_density(float d) {
    float far_d = clamp(p[66] * pow(p[69] / max(d, p[69]), p[68]), p[67], p[66]);
    if (p[65] <= p[66]) return far_d;
    return far_d + (p[65] - far_d) * (1.0 - card_smooth(p[70], p[71], d));
}
bool card_keeps(float rank, float d, bool low) {
    float keep = low ? 1.0 - card_smooth(p[72], p[73], d) : min(1.0, card_density(d) / max(0.01, p[65]));
    if (!(rank < keep)) return false;
    return card_smooth(0.0, 1.0, (keep - rank) / max(0.02, 0.2 * keep)) >= p[74];
}
// inside accepted(), grass path:
if (p[62] > .5) {
    float custom_x = source[base + 16u];
    float kind_slot = floor(custom_x);
    float rank = (custom_x - kind_slot) / 0.999;
    bool low = floor(kind_slot * 0.25) > 0.5;
    float d = max(0.0, distance_to_camera - p[75]);
    if (!(d < p[64])) return false;                       // fade is 0 at the reach
    if (!card_keeps(rank, d, low)) return false;
    vec3 wx = basis * vec3(source[base], source[base + 4u], source[base + 8u]);
    vec3 wy = basis * vec3(source[base + 1u], source[base + 5u], source[base + 9u]);
    vec3 wz = basis * vec3(source[base + 2u], source[base + 6u], source[base + 10u]);
    float guard = p[9] + p[76] * sqrt(dot(wx, wx) + dot(wz, wz)) + p[77] * length(wy);
    if (isnan(guard) || isinf(guard) || guard < 0.0) return true;
    for (int i = 0; i < int(p[13]); ++i) {
        uint a = 28u + uint(i) * 4u;
        if (dot(vec3(p[a], p[a + 1u], p[a + 2u]), world) - p[a + 3u] > guard) return false;
    }
    return true;
}
```

- Metadata is ignored in card mode, so leaves register `Metadata = null` and groups may keep theirs.
- Tree code reads only p ≤ 61, and the card branch is unreachable for trees (p[10] = 0, p[62] = 0).
- After editing the `.glsl`, run the editor or `--import` once so the imported `RDShaderFile` used by exports matches. Dev runs compile the raw file.

### B.2 Layer integration (Cards uses the existing GPU registration path)

**`RingStandingGrassLayer.CardStyle.cs`**
- Add `[Export(PropertyHint.Range, "0,8,0.25")] public float GrassCardComputeMarginMeters { get; set; } = 2f;`.
- Add `internal RingGrassCardComputeParameters CardComputeFrame()`, cached on `(ReferenceEquals(_style), sanitized margin)`. It returns `RingGrassCardComputeParameters.From(_style.Curve, margin)`.

**`RingStandingGrassLayer.cs`**
- `RegisterGpuBatches` (:1359-1360) [RV]: replace `|| _mesh == null || _style.IsCards` with `|| (_style.IsCards ? _cardMesh == null : _mesh == null)`.
  - Near registration is unchanged: `NearMeshFor` returns `_cardMesh`, Candidates is `Data.Buffer`, Metadata is null, Capacity is `Data.InstanceCount`, and the AABB is `Data.LocalAabb`.
  - Card leaves never have an intermediate renderer.
- `RequestGpuCulling` (:1389-1402): **delete** the S5 Cards early-return and dispose block, so Cards flows through the normal path.
  - With `!UseGpuCulling` (:1403-1411) set `GpuCullingStatus = _style.IsCards ? "Cards: CPU MultiMesh" : "CPU fallback"`.
  - At :1496 prefix the status with `"Cards: "` in Cards.
- `AddGpuFrame` (:1499-1505): in Cards emit
  `new BatchFrameParameters(id, CapturePresentationWorld(state, renderer), true, pass, 0f, _style.ReachMeters, 1f, 1f, 1f, EnableLod: false, 1f, false, frustum, GrassFrustumPaddingMeters, VisibilityRevision: visibilityRevision, GrassCard: CardComputeFrame())`.
  - The neutral LOD fields keep blade-key edits from changing card frames.
  - The Blades branch is byte-identical to today.
- `SetGpuCulled` (:1687-1703): make it an instance method. In Cards, keep the renderer and state bookkeeping but **skip** writing `grass_gpu_culled`, which the card shader does not declare. Blades is unchanged.
- No change to `Install`, `RestoreCpuGrassPresentation`, `Remove` or `RetireGrassOutput`.
  - A tier upgrade or downgrade on a GPU tile already does RemoveBatch → ReplaceCpuMultiMesh (CPU draw, same pixels) → re-register.
  - Toggling `UseGpuCulling` off restores CPU MultiMeshes with the card mesh through `NearMeshFor`.

**`RingStandingGrassLayer.WorkCache.cs`**
- Add `RingGrassCardComputeParameters? CardCompute` to `GrassPresentationSettings`, captured as `_style.IsCards ? CardComputeFrame() : null`.
- This way a margin or curve change invalidates the cached `_gpuFrames`. Blades keys stay identical because the value is null.

**Settings** (AGENTS.md feature convention):
- `GameplayGrassDefaults`: add `GrassCardComputeMarginMeters` to `Keys` and to `Limits` as `(0, 8, false)`.
- `RingGraphicsQualityController` profile default: `2f`, next to the S5 card defaults (:519-522).
- Drawer `RingLiveTuningDrawer.Grass.cs`: add a slider "Cards GPU distance margin (m)" in the "GPU culling" group (:31).
- `grass_live_tuning_smoke_test.gd`: set/persist/reload the key.

### B.3 What the CPU path keeps

- Nothing in the CPU MultiMesh path changes.
- It is used when `UseGpuCulling` is off, the RD is unavailable or failed, there is no camera (headless tests), or before the backend is ready.
- It draws every resident instance and the shader culls per vertex, which is the reference image.

### B.4 Tests for Part B

**1. `tools/GpuVegetationParameterLayoutTest.cs` + `tools/gpu_vegetation_parameter_layout_test.gd`** (new, headless, pure C#)
- Calls `WriteParameterWords` for fixed inputs:
  - tree: plain, `TreeCustomData`, `TreeDitherData`, protected plus canonical camera, distance-protected with fades;
  - blade grass: with 6 frustum planes, with LOD on and off;
  - card grass.
- Words 0..63 of tree and blade frames must equal a golden hex dump captured **before** the layout change (step order in B.1).
- Words 64..127 must be 0 for non-card frames.
- The card frame must have p[62] = 1 and p[64..77] equal to its fields.
- Gate: PASS. This is the ParameterBytes compatibility guard for Risk 4.

**2. `tools/GpuGrassCardCullingTest.cs` + `tools/gpu_grass_card_culling_test.gd`** (new, windowed, RD). Backend-level parity, GPU-placed vs CPU reference.

Inputs:
- (a) a synthetic batch of 8192 rows: roots on a 0-460 m polar grid around the camera; ranks stratified including exact values at curve boundaries (keep(d) ± 1e-3); kinds 0 and 1; random yaw, width and height within the builder ranges; plus 32 rows with NaN rank.
- (b) a real tile from `RingGrassTileBuilder` (Gen17 seed 1337 meadow tile; tier 0 with low layer, and tier 8), at camera distances 0, 30, 120 and 300 m.

Each case:
- Register with `Capacity = count`, `LocalToWorld` a non-trivial rotation plus translation, frustum on and off, margin 0 and 2.
- `RequestFrame` with `GrassCard` set.
- `RequestReadbackForTests`.

Checks:
- **Count:** `commands[1]` equals the number of output rows read.
- **Set parity:**
  - Map each output row back to its source index by exact 20-float equality. Compaction preserves order, so the rows are bit-identical copies.
  - Compare with `ReferenceAccepts`, using the frustum guard reproduced in C#.
  - Mismatches are allowed only where `ReferenceAmbiguous` is true:
    - |rank − keep(d')| ≤ 1e-5;
    - |growIn − minGrowIn| ≤ 1e-4;
    - |d' − reach| ≤ 1e-3 m;
    - any plane with |signedDistance − guard| ≤ 1e-3·max(1, guard).
  - NaN-root rows are excluded. NaN-rank rows must be rejected on both sides.
  - Gates: zero non-ambiguous mismatches, and ambiguous rows ≤ 0.1%.
- **Superset vs the shader:** pure C#, no GPU. For 64 random displacements δ with |δ| ≤ margin, every instance the shader would show at camera c+δ must be in the reference set at c. The shader rule is `RingGrassCardCurve.Accepts` or `LowScale > 0`, plus `Fade > 0`. Gate: 0 violations.
- **Skip-unchanged:**
  - An identical frame gives `SkippedUnchangedUpdates` +1 and `CompletedUpdates` +0.
  - Changing only reach, only margin, or only base density gives `CompletedUpdates` +1 each.
  - Removing `GrassCard` (null) gives a dispatch, and the blade branch output equals the pre-change blade expectation from the existing `gpu_grass_frustum_culling_test`.

**3. Extend `tools/GrassCardLayerGpuSmoke.cs`** (windowed, reach 150, as today):
- After settle: `GpuCullingStatus` starts with `"Cards: GPU"` and `GpuCullingBatchCount > 0`.
- Readback one dense tile through `RequestGpuGrassReadbackForTests`. Its accepted set must equal `ReferenceAccepts` applied to `TryGetGpuGrassSourceForTests` rows, with the layer camera and the tile world from `CapturePresentationAuditsForTests` (same tolerance rule).
- **Image parity, GPU vs CPU:**
  1. Fixed camera, wind globals off. First capture the same state twice to measure baseline noise; it should be 0.
  2. Capture with `UseGpuCulling = true`.
  3. Toggle `UseGpuCulling = false`, settle, capture.
  4. Gate: mean |Δ| ≤ 0.5/255 per channel, and ≤ 0.2% of pixels with |Δ| > 8/255 (above baseline).
  5. Toggle back on: batches re-register, and the status returns to GPU.
- **Idle 10 s, GPU mode:** each of these must stay at 0 delta:
  - `TotalGrassBuildCount`, `TotalGrassUploadCount`;
  - `GpuCullingBatchCount` changes;
  - backend `CompletedUpdates`, `DispatchCount`, `ParameterUploads`;
  - `PresentationParameterWrites`;
  - card material parameter sets.
- The frustum retire/re-upload and underground-hold phases stay as they are.

**4. Regression runs.** Serial, windowed, one Godot process at a time.
- Trees: `tree_compute_optimization_regression`, `tree_lod_compute_regression`, `gpu_vegetation_driver_test`, `gpu_vegetation_snapshot_contract_test`, `gpu_tree_integration_test`, `gpu_tree_seam_integration_test`, `tree_lod_dither_gpu_test`, `imported_tree_gpu_regression`, `tree_streaming_contract_test`, `tree_gpu_frame_cache_smoke`, `tree_gpu_rebase_smoke`.
- Blade GPU grass: `gpu_grass_frustum_culling_test`, `gpu_grass_grouped_phases_test`, `gpu_grass_integration_test`, `gpu_grass_registration_test`, `grass_gpu_geometry_test`, `grass_lifecycle_test`, `grass_output_residency_test`, `grass_adaptive_grouping_test`.
- Cards: `grass_card_style_test`, `grass_card_placement_test`, `grass_streaming_hysteresis_test`, `grass_card_color_match_gpu_test` (color and curve).
- All must PASS. For `grass_live_tuning_smoke_test` the gate is "no new failure": the clipped `SectionAdaptivegrassgroups` failure also happens at HEAD.

---

## Part C: adaptive grouping for Cards

### C.1 Effective Cards grouping distances

Add to `RingGrassStyleProfile`, computed in `Build`:
- `LastTierStartMeters` = the largest finite `TierStartMeters[t]`. All tiles beyond it, up to the reach, hold that same last tier. With defaults (reach 400, H = lookahead = 8) that is about 178 m, the floor tier 13.

Add a layer helper in `AdaptiveGroups.cs`:

```csharp
// Cards: never group a tile whose tier can still change. The split threshold (start - hysteresis) stays beyond the last
// ladder boundary + downgrade hysteresis + 4 m camera/focus slack.
internal float EffectiveGrassGroupingStartMeters => _style.IsCards
    ? Math.Max(GrassGroupingStartMeters, _style.LastTierStartMeters + _style.HysteresisMeters + 4f + Math.Max(4f, GrassGroupingHysteresisMeters))
    : GrassGroupingStartMeters;
internal float EffectiveGrassGroupingFarStartMeters => Math.Max(GrassGroupingFarStartMeters, EffectiveGrassGroupingStartMeters);
```

- Defaults: 2×2 starts about 206 m (admitted at about 222 m), 4×4 starts at 256 m (admitted at 272 m).
- With reach 400 that is roughly 375 far tiles in about 40 renderers.
- The group leaf cap (`MaximumGrassSourcesPerGroup` 16384) is far above the about 2.5k cards in a floor-tier 4×4 group.

### C.2 Code changes (Blades behaviour must stay identical; every branch is gated on `_style.IsCards`)

**`RingStandingGrassLayer.AdaptiveGroups.cs`**

`UpdateAdaptiveGroups` (:106-233):
- `AdaptiveSettings`:
  - `Enabled = EnableAdaptiveGrassGrouping`: delete the S5 `&& !_style.IsCards` and its comment (:109-110).
  - `Start`/`Far` = the `Effective*` values above.
  - Cards only: `NearLod = 0`, `Transition = 0`, `DistanceLod = true`. `FarLod = EffectiveGrassDrawDistance()`, which is already the reach in Cards.
- The `enabled` expression is unchanged. It still requires `settings.Gpu`, so the CPU fallback stays ungrouped.
- Build gate (:189): `_mesh != null && _intermediateMaterial != null` becomes `(_style.IsCards ? _cardMesh != null && _cardMaterial != null : _mesh != null && _intermediateMaterial != null)`.

`InstallAdaptiveGroup` (:270-283):
- The material is `_style.IsCards ? _cardMaterial! : _intermediateMaterial!`. `IntermediateMeshFor` already returns `_cardMesh`.
- In Cards skip `ConfigureLodPass`; `SetGpuCulled` is already neutral in Cards (B.2).
- Registration is unchanged: Capacity is `InstanceCount`, and `GroupMetadata` may be passed (it is ignored).

`RestoreAdaptiveFineOutputs` (:368-383), Cards branch:
- For each source leaf set `leaf.NearWanted = group.Resident.IntermediateWanted` and enqueue an upload (`_uploadQueued.Add` → `_uploads.Enqueue`). Do not create intermediate renderers.
- Then `ClearAdaptiveGroups()` and `RefreshAdaptivePresentations()` as today.
- This path only runs on GPU failure or `UseGpuCulling` off. A short re-upload gap there is accepted.

Add `private bool AdaptiveNearVisible(Resident r) => !_style.IsCards || r.Group != null || !_adaptiveLeafOwners.TryGetValue(r.Data.Key, out var o) || !o.Active;`.
- While a Cards group is active and splitting, its leaves re-warm their near outputs. Those outputs must stay hidden until the group retires. Otherwise density doubles for a few frames.
- Blades always returns true.

**`RingStandingGrassLayer.OutputResidency.cs`**, `PrepareGrassPresentation` (:13-110):
- :86 becomes `if (IsAdaptiveGroup(resident)) { if (cards) intermediate = near; near = false; }`. A Cards group's single output lives in the intermediate slot.
- :98 becomes `bool needsIntermediate = (distanceLod || (cards && IsAdaptiveGroup(resident))) && resident.IntermediateRenderer == null && (...)`.

**`RingStandingGrassLayer.cs`**
- `RequestGpuCulling` visibility loop (:1491-1492): the near visibility also requires `&& AdaptiveNearVisible(resident)`.
- `Transforms` CPU branch (:893): `SetRendererVisible(near, resident.NearWanted && AdaptiveNearVisible(resident))`.

**Unchanged** (works as is for the intermediate-slot design):
- `CompleteAdaptiveHandoffs` (activation retires both leaf outputs; the split readiness checks leaf near batches).
- `InstallAdaptiveOutputs`, `RetireAdaptiveGroup`, the ownership audit, and `RingGrassGrouping.Build`/`Compatible`.
  - `Compatible` already rejects mixed tiers, tint maps and partial seam tiles.
  - `Build` preserves CUSTOM (rank) and COLOR, and rebases transforms exactly.

**Drawer status** (`RingLiveTuningDrawer.Grass.cs:154-178`):
- In Cards, `firstStart`/`secondStart` read `layer.EffectiveGrassGroupingStartMeters` and `...FarStartMeters` plus the margin, and `farLimit` is the reach.
- Blades text is unchanged, so the live-tuning smoke string check still matches.
- Optional: in Cards, make `RefreshGrassGeometrySelection` a no-op. Today it splits all groups when blade-only geometry toggles change.

### C.3 How the per-tile uv survives grouping (answer)

- The group renderer is a separate `GeometryInstance3D`.
- `RefreshTilePresentation` writes its own `grass_gust_origin_world` (the group tile origin), its covectors (from the group `AnchorHeight` and `Bounds.AroundCenter`), and `grass_terrain_origin_uv = CardTerrainOriginUv(group.Bounds, ...)`.
- In the shader, `uv(p) = origin_uv.xy + dot(p - o, cov) * 0.2` is affine. For any instance, the group-based uv equals the leaf-based uv modulo an integer, apart from the curvature and height linearisation term (below 2 mm on the ground at 90 m, R = 5000 km).
- The macro-shadow `zw` is likewise linear.
- The ring-seam unwrap is safe:
  - a group never straddles the wrap, because `FromLeaf` uses floor division and `Compatible` rejects partial cells;
  - group and leaves unwrap against the same `_cardFocusAroundMeters`.

### C.4 Interaction with tiers, hysteresis and idle 0

- **Tiers:** with the C.1 start, a leaf inside an active group cannot change tier during normal movement. If a user setting forces it, `GroupSourcesPresent` detects the replaced `Data` and splits, so it stays correct, only with churn.
- **Hysteresis:** groups are admitted at start + hysteresis and split at start − hysteresis (existing logic), so oscillating by less than H cannot flap.
- **Idle 0:** `UpdateAdaptiveGroups` returns early on an unchanged planner state (:121-122), and compute frames are skip-unchanged. A settled stationary camera does 0 group installs, 0 retirements, 0 dispatches and 0 parameter writes.

### C.5 Tests for Part C

1. **uv equivalence** (headless; add to `tools/GrassCardStyleTest.cs`):
   - Build 2×2 and 4×4 groups from real card leaves with `RingGrassGrouping.Build`, including one group next to the wrap, with the focus across around = 0.
   - For each grouped instance compute the shader uv with the group uniforms, and with the leaf uniforms on the leaf's untransformed row. The uniforms are `CardTerrainOriginUv`, `CalculateGrassGroundCovectors` and `CreateTileWorldTransform` origin.
   - Gate: |Δuv − round(Δuv)| ≤ 1e-3 (5 mm), and macro-uv |Δ| ≤ 1e-6.
   - Also check that CUSTOM and COLOR are bit-identical, and that the world transforms match the existing tolerance (`RunGrassAdaptiveGroupingTests` pattern).
2. **New `tools/GrassCardGroupingGpuTest.cs` + `tools/grass_card_grouping_gpu_test.gd`** (windowed, Cards, reach 400, camera at eye height looking along the ground):
   - **Groups form:** groups form beyond `EffectiveGrassGroupingStartMeters`, and none closer.
   - **Ownership:** `AssertAdaptiveGrassOwnershipForTests()` passes.
   - **Uniform:** each group renderer's `grass_terrain_origin_uv` equals `CardTerrainOriginUv(group bounds, ...)`.
   - **Image parity:** grouping on (settled) vs off, same pose, same thresholds as B.4-3.
   - **Idle 10 s settled:** zero deltas in group installs/retires, builds, uploads, dispatches and `PresentationParameterWrites`.
   - **Walk:** 300 m outward then back, in 2 m steps, checking every step:
     - every wanted leaf is drawn exactly once (either its visible near output, or an active owner group), with no gaps and no doubles; add an internal audit helper that returns the per-leaf coverage owner;
     - group splits caused by a leaf data change = 0;
     - each group splits at most once per approach.
   - **Oscillation:** ±(grouping hysteresis/2) for 60 s gives 0 group transitions.
3. **Regression:** `grass_adaptive_grouping_test` and `grass_adaptive_grouping_capture` (Blades) unchanged; streaming hysteresis tests PASS.

---

## Phasing

| Step | Content | Depends | Can run in parallel with | Gate |
|---|---|---|---|---|
| A | A.0 card golden, then hero removal (A.1-A.3) | – | B1, only in a separate worktree | build clean; placement (Blade+Card golden), assets, style, curve, color 9/9, layer smoke; grep clean |
| B1 | Compute backend: layout wrapper + golden first, then ParameterBytes 512, `GrassCard` frame, GLSL branch, `RingGrassCardComputeParameters`, tests B.4-1/2 | – (reads curve methods A does not change) | A (worktree) | layout test, card culling parity, tree + blade GPU suite |
| B2 | Layer: RegisterGpuBatches, RequestGpuCulling, AddGpuFrame, SetGpuCulled, WorkCache key, margin setting/drawer/persistence, B.4-3 | B1 (and A merged) | – | layer smoke incl. GPU/CPU image parity and GPU idle 0; live-tuning smoke no new failure |
| C | Grouping for Cards (C.1-C.2), drawer status, C.5 tests | B2 | – | uv equivalence, grouping GPU test, Blades grouping unchanged |
| Final | Full suite, serial, one Godot process at a time | all | – | all gates below |

Practical notes:
- With one shared working tree, run A, then B1, then B2, then C in order: concurrent builds fail on each other's half edits. Use `isolation: worktree` if you want A to run alongside B1.
- GPU tests always run one at a time.
- Recommendation: commit S5 before starting A, so each step's review diff is clean. This is the user's or primary's call.

## Gates (the whole set must pass at the end)

- Build: 0 warnings / 0 errors.
- **Blades unchanged:**
  - `BladeGolden` 12/12;
  - `BladesDefaultFingerprint`/`BladesMaxedFingerprint`;
  - blade GPU grass suite;
  - `grass_adaptive_grouping_test`;
  - the parameter layout test (words 0..63 unchanged).
- **Trees unchanged:** the tree suite in B.4-4.
- **Cards correctness:**
  - `CardGolden` unchanged after A;
  - GPU parity (0 non-ambiguous mismatches, ambiguous ≤ 0.1%);
  - superset property: 0 violations;
  - GPU vs CPU image parity;
  - grouping on/off image parity;
  - uv equivalence ≤ 1e-3.
- **Idle 0:** `grass_streaming_hysteresis_test`, plus the GPU-mode and grouped idle windows (0 builds, uploads, registrations, dispatches, parameter uploads, presentation writes, group transitions).
- **No seam:**
  - the S5 seam/unwrap checks in `grass_card_style_test`;
  - uv equivalence including the wrap-adjacent group;
  - the colour-match `border_*` poses.
- **Colour match:** `grass_card_color_match_gpu_test` color 9/9 PASS (no hero lines) and curve PASS.
- **Underground hold:** the existing phase in the layer smoke.
- **Live tuning:** the new key persists; no new failure beyond the pre-existing clipped-section one.
- Also report: Cards GPU `GpuCullingBytes` vs `ResidentGrassInstanceBytes`. Full-capacity outputs roughly double instance VRAM; see Risks.

## Risks

1. **VRAM:** output capacity = `InstanceCount` per card batch doubles the instance memory on the GPU (source + output, about 80 B each). Capacity cannot shrink much (≤ 30%): tier-0 tiles can accept everything, and frustum rotation needs headroom. Measure resident GPU bytes at spawn on the A1000 (Risk 1 of the main plan). The fallback is a tighter capacity of `ceil(InstanceCount * keep(d_min - margin) / f_t)`, deferred.
2. **Stale culls under the dispatch budget:** `MaximumGpuCullingBatchesPerFrame` = 96 against about 200 visible card batches gives 2-3 frames of latency.
   - Distance is covered by the margin (2 m covers about 40 m/s at 3 frames and 60 fps).
   - Fast camera rotation can show edge holes for a frame or two, the same as Blades; `GrassFrustumPaddingMeters` mitigates it.
   - Grouping reduces the batch count.
3. **Float divergence between GPU `pow` and `MathF.Pow`:** handled by the ambiguity band. The shader is the final arbiter, so a divergence can only cost a few extra vertex invocations, never a visible difference, as long as margin > 0.
4. **ParameterBytes / tree regressions (main plan Risk 4):** guarded by the golden layout test and the full tree suite. Allocation size grows by 256 B per batch, which is negligible.
5. **Imported `.glsl` resource:** an export uses the imported `RDShaderFile`. Re-import after the change, or exports run the old shader.
6. **Grouping start interacts with user settings:** if a user lowers the grouping start, the C.1 clamp keeps the Cards start beyond the ladder end. The drawer status must show the effective value, or the slider will seem to do nothing below about 206 m.
7. **Split gap or double draw at group transitions in Cards:** the near slot was never grouped before. `AdaptiveNearVisible` plus the coverage-owner audit in the walk test is the guard.
8. **Colour-match numbers shift after A:** the near band is now full-density cards. A pose could cross its target, most likely `day_near`/`day_down` near 2.4. If that happens, do not loosen the gate; escalate to the primary.

## Open questions for the user

None are blocking. Two choices are worth confirming:
1. **Margin setting:** the GPU distance margin is exposed as `GrassCardComputeMarginMeters` (default 2 m) in the grave-key "GPU culling" group. The alternative is a fixed internal constant with no menu entry.
2. **Grouping start:** the Cards grouping start is clamped to about 206 m or more, beyond the density ladder, so tiers never change inside a group. The 2-by-2 slider then acts as a lower bound in Cards. The alternative is to let groups start closer and accept split and regroup churn at tier boundaries.
