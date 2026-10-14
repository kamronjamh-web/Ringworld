# Beam Emitter Sites: Deterministic Placement and Planned Terrain

Status: design spec, read-only investigation, 2026-10-14. Nothing in the repo was edited.
Scope: 600 surface beam-emitter POIs (spire + facility) plus one reactor cavern each, on the Certified ring, for both terrain backends.

---

## 0. Findings that shape the design (with evidence)

| # | Finding | Evidence |
|---|---|---|
| F1 | **There are two height authorities.** Gen-16 (smooth SDF backend) computes height in `RingTerrainSampler` from its own noise. Gen-17 (`surface_runtime_generation_v1`) delegates to `TerrainGenerationHeightModel` (C# authority; GDScript `TerrainHeightModel` is a thin wrapper). The terrain is completely different between backends, so a site list that depends on terrain cannot have identical positions in both. | `RingTerrainSampler.cs:21-23,164-168`; `RingTerrainSampler.SurfaceGeneration.cs:81-120,144-174`; `terrain_height_model.gd:1-24` |
| F2 | **Nearly every consumer reads height through `RingTerrainSampler`.** This covers far LOD (global LOD, proxy sectors, scaled far viewport, min-max pyramid), water/shoreline, grass, trees, minimap, clouds/weather, underground probes, and Terrain3D streaming. No shader reimplements terrain noise. If shaping lives in the two height functions, the consumers "just work". | grep of `GetTerrainSample/GetPresentationSurfaceHeight/...` (about 60 files, all via sampler); no terrain noise in any `*.gdshader` |
| F3 | Gen-16 height is assembled in **three public paths** that each repeat the pipeline continental → biome relief → hydrology: `GetSurfaceHeight`, `GetSmoothSurfaceHeight`, and `GetTerrainSampleLocked`. Hydrology then **overrides** height for lakes and rivers. | `RingTerrainSampler.cs:391-442, 637-677, 1138-1194, 1196-1320` |
| F4 | Precedents for deterministic, site-shaped terrain in gen-16 are `ApplyExceptionalSummit` (a radial lerp toward a target) and `ApplyEngineeredMacroTerrain` (an inland sea plus terraces). Hash identity is append-only, gated by a flag that preserves the exact legacy sequence when disabled. | `RingTerrainSampler.cs:1405-1466, 1506-1534, 2044-2045` |
| F5 | Gen-17 recipes evolve by **new `generator_id`** (v1→v4), **without** bumping the int generator version 17. The recipe is frozen per world in the save document. Old recipes keep byte-identical evaluation order. | `TerrainGenerationParameters.cs:13-16, 31-37`; `TerrainGenerationHeightModel.cs:118,151,157`; `RingWorldSaveManager.cs:73-87, 574-590` |
| F6 | Save/journal compatibility is **fail-closed**. A save loads only if the generator version is equal and the hash is current or explicitly accepted. Smooth journals accept a short list of legacy revisions. Surface edit snapshots require exact hash equality. Accepted-legacy migration exists only for identity changes the codebase judged safe. | `RingWorldDesignedCaveContract.cs:71-136`; `SmoothTerrainContracts.cs:136-162`; `SmoothTerrainEditJournalStore.cs:23-26`; `RingSurfaceHeightField.cs:139-148`; `RingWorldSaveManager.cs:493-499, 945-962` |
| F7 | Every disk cache is keyed by `TerrainDefinitionHash`: global LOD summary, min-max pyramid, vegetation, proxy projection, and Terrain3D gameplay cache. A hash change invalidates them automatically. | `RingTerrainGlobalLod.cs:648`; `RingTerrainMinMaxPyramid.cs:113,184`; `RingBiomeDecorationLayer.TreeStreaming.cs:581`; `RingTerrainProxySector.cs:1457-1463`; `RingTerrain3DGameplay.cs:402` |
| F8 | Smooth density is `y - (surface + 0.5)`, max-unioned with designed caves, then roof, hull, and sidewall terms. The **uniform-region fast path** classifies boxes by recomputing exactly that planar SDF from surface heights. Any new carve term must either be bounded in that proof or make it decline. | `SmoothTerrainContracts.cs:201-253, 277-380`; `RingTerrainSampler.cs:517-575` |
| F9 | The cave interval proof `TryBoundTerrainTopology` is a Lipschitz/centre-slack bound over max-union contributors. "Proven empty" relies on max-union semantics, where air only grows. Max-union (air) terms keep that proof valid. A min() "solid fill" term would break it. | `CaveTopologyDensityComposer.cs:343-441`; `SmoothTerrainContracts.cs:297-329` |
| F10 | Designed-cave placement depth is measured from **absolute Y=0**, not the local surface. `Permits()` filters shapes at composition time. `PermitsConnectivity()` checks **endpoints only**, so a corridor whose endpoints lie outside an exclusion zone can still pass through it. | `CaveFeaturePlacementPolicy.cs:66-110, 131-136`; `CaveTopologyDensityComposer.cs:167,197,1038,1075` |
| F11 | The Terrain3D ring shader already honours the control-map **hole bit 2** (`control >> 2 & 1`). The streaming path preserves existing control maps on import. Curved collision is custom: triangles from a 193² window with no hole support. | `materials/terrain3d_ring_surface.gdshader:160-161`; `RingTerrain3DSurface.PreparedWindow.cs:290-300`; `RingTerrain3DCollisionGeometry.cs:38-72` |
| F12 | Grass eligibility uses biome texture weights plus an optional `RingGrassExclusionSnapshot`. Alpine/desert weights still allow sparse grass (`sparseWeight*.08`). Trees spawn only on `Biome == Forest/Grassland`. `GetBiomeTextureWeights(in TerrainSample)` has **no coordinates**. | `RingGrassTileBuilder.cs:127-136,184`; `RingBiomeDecorationLayer.cs:810,848`; `RingTerrainSampler.cs:765-798` |
| F13 | Gen-16 Certified settings are much shallower than the domain limits: `BedrockY=-2000`, `DeepOceanFloorY` default -1800, rare peak around 1848 m. The surface clamp floor is `max(BedrockY+1, DeepOceanFloorY)`. Gen-17 spans -8000..+8000, with basin 4500 plus trench 3000 by default. | `world_size_launcher.gd:26,1809-1830`; `VoxelTerrainSettings.cs:69-70`; `RingTerrainSampler.cs:1411-1413`; `TerrainGenerationParameters.cs:77` |
| F14 | Circumference is `981,748 × 32 = 31,415,936 m`, so the row pitch is `C/100 = 314,159.36 m` (not an integer). Canonical frame: local +X = +around, +Z = +across × `AcrossDirection`, +Y up. | `CertifiedRingScale.cs:26-29`; `RingSurfaceFrame.cs:48-57` |
| F15 | **Reactor geometry does not match the "3 km across" cave note at the architecture levels.** Wall anchor stelae sit at R1245 (r≤108, spanning R1137-1353, z -360..+530). Wall terraces span R1170-1252 at z -180/+80/+330, within ±0.57 rad of the anchor azimuths. Buttress roots end at about R1260. The arrival tongue ends at R1430, z≈+69..81. The exhaust roof interface ring is r75-115 at z1021-1039. So the **wall must be about R1240 in the three anchor sectors**, bulging only in the bays between them. | `build_alien.py:79,83,93,107,132,138,156`; `modules/module_manifest.json` |
| F16 | The surface throat is an annulus R75→R94, z0..14. Nothing exists below ground. Facility rooms extend to about R194. The spire AABB is 616 m (R308). The slab bottom is -1 m. | `build_surface_spire.py:115-116`; spire `IMPORT_NOTES.md:20-38` |
| F17 | **Spire entry and reactor arrival nearly coincide.** Blender 292.5° maps to Godot XZ direction (0.383, 0.924), which is 67.5° from +X toward +Z. Reactor arrival is Godot +Z (90°). They are 22.5° apart, so one site yaw orients both. Reactor anchor azimuths in Godot XZ are 270°, 150°, and 30°. The bays are 90° (arrival), 210°, and 330°. | spire `IMPORT_NOTES.md:42,48`; reactor `IMPORT_NOTES.md:44`; `build_alien.py:79` |
| F18 | Measured natural sample cost is about 3-6 µs per sample: 16,384-sample grids take about 50-100 ms on the dev laptop (Debug/optimised mix). | `docs/terrain-generation-cpu-optimization.md:89-110` |

---

## 1. Site registry

### 1.1 Recipe (world-definition data, frozen per world)

`EmitterSiteRecipe` (pure C#, no Godot types, immutable). Every field is hashed.

| Field | Default | Notes |
|---|---|---|
| `algorithm_version` | 1 | Bump on any behaviour change. It is mixed into the terrain hash. |
| `rows`, `columns` | 100, 6 | |
| `column_offsets_m` | 26 500 + 53 000·j | Exactly the user's grid. |
| `row_phase_m` | `hash(seed,"emitter-phase") mod pitch`, snapped to 2 m | D11: alternatively 0. |
| `nudge_radius_m` | 4 000 | Gives a minimum pairwise distance of ≥ 45 km across and ≥ 306 km along. |
| `pad_radius_m` / `apron_width_m` | 350 / 60 | Pad is R ≥ R308 spire + 42 m. |
| `max_pad_cut_m` / `max_pad_fill_m` | 150 / 150 | Measured within the pad disc. |
| `skirt_max_radius_m` / `skirt_fade_m` | 3 000 / 600 | |
| `cut_terrace_riser_m`, `cut_terrace_bench_m`, `cut_riser_slope` | 30, 20, tan 60° | Average bowl-wall slope is about 39°. |
| `fill_slope` | tan 33° | |
| `soft_clamp_m` | 24 | |
| `enclosure_radius_m` | 2 000 | Cavern R1500 + 500 m cover. |
| `enclosure_cover_m` | 200 | Minimum rock above the cavern roof. |
| `island_pad_min_m` | +24 | |
| `island_coast_m`, `beach_width_m`, `shelf_depth_m`, `shelf_width_m`, `flank_slope` | +4, 250, 20, 300, tan 30° | |
| `dry_margin_m` | +2 | |
| `road_half_width_m`, `road_shoulder_m`, `road_grade`, `road_max_length_m`, `bank_slope` | 10, 10, 0.08, 2 500, tan 35° | |
| `hole_radius_m` | 80 | Edge hidden under the R75-94 throat. |
| `shaft_radius_m` / `bore_top_radius_m` | 85 / 76 | |
| `cavern_*` | §3.3 | |

Gen-17 stores these as new recipe JSON fields (§5). Gen-16 stores them in a new `EmitterSiteSettings` resource plus `EmitterSiteContractVersion` (§5).

### 1.2 Inputs and purity

`EmitterSiteRegistry Build(EmitterSiteRecipe recipe, INaturalHeightSource source, int threads)`

- `INaturalHeightSource` exposes `double NaturalHeight(long around, int across)` (the **unshaped** dry presentation height), `double SeaLevel`, `double MinPossibleHeight`, `ulong Identity`, and `INaturalHeightSource CreateWorkerCopy()`.
  - Gen-17 adapter: `TerrainGenerationHeightModel.SampleDiagnostics(...).Height` evaluated *without* the site operator, through a private `SampleNatural` entry.
  - Gen-16 adapter: `continental + biome relief`. This is the value at `RingTerrainSampler.cs:406/431/651`, **before** hydrology and shaping, plus the 0.5 presentation offset.
- Each site is evaluated independently on a worker-owned source copy. Results are written by site index, so the output is independent of thread count and order. Models are already documented as worker-confined (`TerrainGenerationHeightModel.cs:7,24-25`).
- The registry is immutable after `Build`. It is shared by reference across `RingTerrainSampler.CreateIndependentCopy()` and worker models, following the same pattern as `sharedRegionFeatures` at `RingTerrainSampler.cs:245,287-293`. It is cached statically in a `ConcurrentDictionary<(ulong naturalIdentity, ulong recipeHash), Lazy<EmitterSiteRegistry>>`. An optional disk cache under `user://generated/emitter_sites_{hash}.bin` is a later optimisation.

### 1.3 Algorithm (per site i = row·6 + col)

1. **Grid point.** `A_g = snap2(row_phase + row·C/100)` wrapped to [0,C). `X_g = column_offsets[col]`. `snap2` rounds to an even metre, the 2 m lattice.
2. **Classify the grid point.** Sample a 25-point stencil: the centre, 12 points at R1000, and 12 at R2000. `wetFrac` is the fraction below `Sea + dry_margin`.
   - `wetFrac ≥ 0.5` gives **OCEAN**: no nudge (user wants islands), so `site = grid point`, `needsIsland = true`.
   - Otherwise the site is **LAND/COAST** and goes to step 3.
3. **Nudge search (LAND/COAST only).** Candidates are the grid point plus rings k=1..4 at k·1000 m with 8 directions each (33 total). The direction set is rotated by `hash(seed,i)·45°` and every point is snapped to the 2 m lattice. Reject any candidate that:
   - leaves `across ∈ [R_inf_max + 1 km, W − R_inf_max − 1 km]`; or
   - (gen-16) lies within `ExceptionalSummitRadius + skirt_max` of the exceptional summit (`RingTerrainSampler.cs:180-184`).
   Score each candidate using a pad stencil `S_pad` (centre, 8 points at R200, 8 at R400; 17 samples) and the enclosure stencil from step 2:
   ```
   σ      = stddev(h over S_pad)
   span   = max − min over S_pad
   wet    = fraction of enclosure stencil below Sea+dry_margin
   cover  = max(0, (P_c − 2800) − min(h over enclosure stencil))   // valley breach risk
   dist   = |c − g| / nudge_radius
   score  = σ/20 + span/100 + 2·wet + cover/200 + 0.35·dist
   ```
   Choose the minimum score; ties go to the lowest candidate index. **Hysteresis:** keep the grid point unless the best score is below `score(g) − 0.15`. If the chosen candidate has `wet > 0`, set `needsIsland = true`. A coastal site becomes a headland, because the raise is max-only.
4. **Pad height P** (integer metres, presentation space):
   ```
   m  = median(h over S_pad of chosen site)
   P0 = round(m)
   P1 = max(P0, ceil(max(S_pad)) − max_pad_cut)       // cap cut
   P2 = min(P1, floor(min(S_pad)) + max_pad_fill)      // cap fill (cut wins on conflict → flag CutFillConflict, keep P1)
   P  = needsIsland ? max(P2, Sea + island_pad_min) : max(P2, Sea + 8)
   ```
   D1 covers alternative policies.
5. **Enclosure check (LAND).** `needsEnclosureRaise = min(h over enclosure stencil and an extra ring at R1500) < P − 2800 + 8`. This is normally false. When it is true, the cover raise in §2.2 is enabled for this site.
6. **Yaw / approach bearing β.**
   - LAND: evaluate 16 bearings. For each, sample h at s ∈ {450, 700, 1000, 1500, 2000} m and compute `cost = Σ|h − clamp(h, P − g·(s−350), P + g·(s−350))|`, the earthwork beyond the grade cone. Choose the minimum, with ties to the lower index.
   - ISLAND: `β = hash(seed,i)·360°`, quantised to 22.5°.
   - Godot yaw of the POI root: `θ = 67.5° − β` (rotation about local +Y; see F17), valid for `AcrossDirection = +1`. Negate β for frames with `AcrossDirection = −1`.
7. **Road profile table** (LAND, and ISLAND down to the shore).
   - Sample the *shaped-without-road* height (§2, steps 1-3) along the centreline every 16 m for s ∈ [350, 350+L_max]: 157 samples.
   - Forward pass: `T_0 = P`, then `T_k = clamp(h_k, T_{k−1} − g·16, T_{k−1} + g·16)`.
   - `L_end` is the first s where `|h_k − T_k| < 0.5` for 3 consecutive samples. Otherwise use `L_max` and set flag `RoadUnmerged`.
   - Store `float[]` (≤157 entries).
8. **Influence radius.** `R_inf = max(skirt_max, 350 + L_end + halfWidth + shoulder + 200, R_E)`.
   - `R_E = 0` if neither the island nor the enclosure raise is enabled.
   - Otherwise `R_E = R_shelf + (Sea − shelf_depth − MinPossibleHeight + soft_k)/flank_slope`, which is conservative.
   - At 30°: gen-17 gives about 16.4 km and gen-16 about 5.6 km.
   - **Assert** `R_inf + nudge_radius < min(53 km/2, pitch/2)` and `R_inf + nudge_radius + 1 km ≤ X_site` (wall).

Per-site record (about 64 B plus the road table): `Around, Across, P, β, yaw, flags {Ocean, Island, EnclosureRaise, RoadUnmerged, CutFillConflict}, R_inf, R_E, L_end, roadTable offset, siteHash`.

The registry identity is FNV over all records plus `algorithm_version`.

**Cross-backend note (D13).** The rules are shared. The positions agree only on the pure grid points (ocean sites and un-nudged sites). If identical positions on both backends matter, use a terrain-independent nudge (hash jitter only).

### 1.4 O(1) lookup (any thread, no allocation, no locks)

```
bool TryGetInfluencingSite(long around, double across, out SiteRef s, out double du, out double dw):
  a   = wrap(around, C)                          // long fmod
  row = floor((a − row_phase)/pitch + 0.5) mod rows
  col = clamp(round((across − 26 500)/53 000), 0, 5)
  s   = sites[row*6+col]
  du  = signedWrap(a − s.Around, C)              // RingSurfaceFrame.SignedAroundDeltaMeters semantics
  dw  = across − s.Across
  return du*du + dw*dw < s.R_inf²
```

This is correct because `R_inf + nudge < pitch/2` on both axes, so the nearest grid cell is the only possible influencer. A brute-force test enforces it (§8). The cost is about 5-10 ns, one array read, with no hashing. The row wrap is handled by `mod rows`, and the seam by `signedWrap`.

---

## 2. Surface shaping operator (height field)

Pure static `EmitterSiteSurfaceOperator.Shape(in Site s, double du, double dw, double h0, out SiteSurfaceMask mask) → double`. It is applied **inside the generator**, before any consumer reads height (§2.7). All constants come from the recipe. Notation:

```
S(e0,e1,x) = smoothstep
smin(a,b,k): h=max(k−|a−b|,0)/k; return min(a,b) − h·h·k/4     (C1, 1-Lipschitz)
smax(a,b,k) = −smin(−a,−b,k)
r = sqrt(du²+dw²);  ρ = max(0, r − (R_pad + W_apron))
```

### 2.1 Order

`h0` (natural) → **(1) island/enclosure raise** → **(2) cone clamp + terraces** → **(3) skirt fade** → **(4) approach road** → **(5) pad flatten**. Each stage is continuous, and stage (5) makes the pad exact.

### 2.2 Island / enclosure raise (max-only, never lowers terrain)

The island profile `E(r)` is a monotone cubic (Fritsch-Carlson PCHIP) through these knots, linear beyond the last:

| r | E |
|---|---|
| R_pad+W_apron (410) | P |
| R_enc (2 000) | Sea + island_coast (+4) |
| R_shore = R_enc + beach (2 250) | Sea − 0.5 |
| R_shelf = R_shore + shelf_w (2 550) | Sea − shelf_depth (−20) |
| > R_shelf | Sea − 20 − flank_slope·(r − R_shelf) |

For r ≤ 410, `E = P`. Then `h1 = needsIsland ? smax(h0, E(r), 8) : h0`.

PCHIP avoids the flat joints that chained smoothsteps produce, so the result reads as a natural dome, beach, shelf, and seamount. D2 offers an "engineered plateau with sea cliffs" variant: knots `(R_enc, P)`, `(R_enc+40, Sea−0.5)`.

**Cover raise (land sites with `EnclosureRaise` only):** `Ec(r) = P − 2800` for r ≤ R_enc, then `P − 2800 − flank_slope·(r−R_enc)`. `h1 = smax(h1, Ec, 8)`.

**Guarantee:** for every column with r ≤ R_enc, `surface ≥ max(Sea + dry_margin, P − 2800)`. So there is no water above the cavern, and at least 200 m of rock covers the cavern roof (P − 3000). Below the surface, the SDF is solid down to the terrain bottom (`SmoothTerrainContracts.cs:206,220-231`). This means the island is solid rock to −7680 with no extra fill term. That keeps max-union semantics and the interval proof intact (F9).

### 2.3 Cone clamp (mountain carve / valley fill) with terraces

```
period = bench + riser/riser_slope;   β = bench/period;  u = ρ/period;  n = floor(u);  f = u − n
Terr(ρ) = riser · (n + S(β, 1, f))                      // continuous, monotone staircase
U(ρ) = P + Terr(ρ)                                      // cut bound (terraced bowl wall)
L(ρ) = P − fill_slope·ρ                                 // fill bound (embankment)
k(ρ) = soft_clamp · S(0, 40, ρ)                          // zero at apron start → exact edge
h2 = smax(smin(h1, U(ρ), k), L(ρ), k)
```

The result:
- In mountains it forms a terraced bowl with roughly 39° average walls and 30 m risers.
- In valleys and on downslopes it forms embankments at 33°.
- Where natural terrain lies within the cone, it is untouched, so ridges beyond the rim are preserved.
- Rims are C1 because of the smooth clamp.

**Max cut depth:** the pad disc is capped by P's choice (≤ 150 m). The bowl wall can grow taller toward `skirt_max`. Registry diagnostics record `maxCut = max(h1 − h2)` from a 64-sample ring scan, and the nudge score penalises it (D4).

### 2.4 Skirt fade

`h3 = lerp(h2, h1, S(skirt_max − skirt_fade, skirt_max, r))`. Normally h2 equals h1 here because the cone has met natural terrain. If not, the registry flag `ConeUnmet` is set and the nudge avoids it. This fade bounds the operator radius.

### 2.5 Approach road (registry-precomputed profile, O(1) query)

```
e = (cos β, sin β) in (around, across)
s_par  = du·e.x + dw·e.y;   s_perp = |−du·e.y + dw·e.x|
if 350 ≤ s_par ≤ 350 + L_end + fade:
  T    = lerpTable(roadTable, (s_par − 350)/16)
  bank = bank_slope · max(0, s_perp − halfWidth)
  hr   = clamp(h3, T − bank, T + bank)                       // flat deck, cut/fill banks
  w    = S(350, 390, s_par) · (1 − S(L_end, L_end + 120, s_par − 350))
  h4   = lerp(h3, hr, w)
else h4 = h3
```

This gives a graded ramp of at most 8% from the pad edge along the 292.5° facility entry (which spans R230→R156 inside the pad), cut through bowl terraces or embanked across valleys. Island roads end at the shore (D5 covers a jetty).

### 2.6 Pad flatten (exact)

```
h5 = r ≤ R_pad ? P : lerp(P, h4, S(R_pad, R_pad + W_apron, r))
```

On the 2 m lattice, every vertex with r ≤ 350 equals P exactly as a float (an integer). The facility slab bottom (−1 m) is buried 1 m.

Gen-16 uses presentation offset 0.5 (`RingTerrainSampler.cs:168,454`). The operator therefore targets `P − 0.5` in the smooth height, so that the SDF iso-surface and the presentation surface equal P. `GetSurfaceHeight` floors to P−1 (legacy blocks).

### 2.7 Where it plugs in

- **Gen-17:** at the end of `TerrainGenerationHeightModel.SampleDiagnostics` (`TerrainGenerationHeightModel.cs:173-174`), only when `GeneratorId == "ringworld_surface_sites_v5"`. Apply `Height = Shape(...)`, clamped to [−8000, 8000]. Add `SiteWeight, SiteHardscape, SiteRock, SiteSand, SiteIndex` to `Diagnostics`, with defaults so v1-v4 goldens stay byte-identical. All gen-17 consumers, plus the preview, inherit the change, because `RingTerrainSampler.SurfaceGeneration.cs:44-174` only calls `SampleDiagnostics`/`CalculateHeight`.
- **Gen-16:** add one private `ShapeDryHeight(around, across, terrain.Height)` and call it right after `GetBiomeTerrainHeightLocked` in all three paths (`RingTerrainSampler.cs:406, 431, 651`). Leave `SampleDrySurfaceHeightLocked` (`:1097`, river/watershed internals) and lake-centre sampling unshaped, but **suppress hydrology** inside sites:
  - In `BuildLakeCandidateLocked` (`:1259-1320`), reject a candidate if its disc (radius + 500 m) intersects any site's `R_inf`. This needs a registry range query, which is still O(1) for a 3×3 neighbourhood of cells.
  - In `ResolveHydrologyLocked` (`:1162-1191`), skip rivers when `TryGetInfluencingSite` is true. The ocean rule `dry < SeaLevel` (`:1145`) then sees the shaped (island) height automatically.
- **Climate** stays computed from unshaped fields, so biome boundaries stay continuous. The site mask (§2.8) overrides surface materials and vegetation.

### 2.8 Material / biome / vegetation masks

`SiteSurfaceMask` is computed in the same `Shape` call:

```
hard = max(1 − S(R_pad, R_pad + W_apron, r),  roadW · (1 − S(halfW, halfW + shoulder, s_perp)))
rock = S(2, 10, h1 − h5) · (1 − hard)                              // cut faces and terraces
sand = needsIsland ? (1 − S(Sea + 3, Sea + 8, h5)) · S(R_enc − 150, R_enc, r) : 0   // beach band
```

- Carry the mask on `TerrainSample` as an **optional trailing member**: `float SiteHardscape = 0, float SiteRock = 0, float SiteSand = 0`. Existing positional constructors stay source-compatible (`TerrainSample.cs:3-13`).
- `GetBiomeTextureWeights(in sample)` (`RingTerrainSampler.cs:765`) blends toward `Alpine = hard + rock` and `Desert = sand`. That gives rock on the pad, plaza, and cut faces, and sand on beaches.
- Set `Biome = Alpine` when `hard ≥ 0.5`. Trees then never spawn, because they are gated to Forest/Grassland (`RingBiomeDecorationLayer.cs:810,848`).
- Add an explicit grass gate in `RingGrassTileBuilder.cs:133-136`: `chance *= 1 − max(hard, rock)`. Hard-reject when `hard > 0.05`; this removes the residual `sparseWeight*.08`. Also apply the gate in the GPU grass preparation path if it bypasses the CPU builder (verify `RingGrassGpuGenerationPrototype`).
- A dedicated plaza texture needs a Terrain3D control-map texture slot (D6). The four-channel biome map has no spare channel.

---

## 3. Underground shaping (SDF), smooth backend now, heightmap after the cave port

### 3.1 Principles

- **Air-only, max-union** carve: `density = max(natural, SiteCarve)`. Rock enclosure comes from the surface guarantee (§2.2) and the cave exclusion (§3.5), **not** from a min() fill term. This preserves F9.
- Every primitive is **1-Lipschitz** (or uses a documented L) in canonical (around, vertical, across) metres. Curvature error is ≤ 1.2 m at R1500 and −4000 m depth; it is absorbed by ≥ 5 m clearances.
- Local site frame: `x = du`, `z = dw` (Godot XZ ↔ around/across for `AcrossDirection=+1`), `y = vertical − P`. Rotate (x,z) by −θ into asset space.

### 3.2 Primitives (air positive)

| Part | SDF (air>0) | Extents |
|---|---|---|
| Bore (under the throat) | `min(R_bt − ρ, y − (−20), 16 − y)` with R_bt = 76 | y ∈ [−20, +16] |
| Shaft | `min(R_sh − ρ, y − y_sb, −20 + 1 − y)` with R_sh = 85 | y_sb = −4000 + 990 (overlaps the cavern by 40 m; the exhaust top at −2961 sits in air) |
| Cavern core | rounded cylinder: `R_w=1240`, half-height 1000, edge radius 600, centre y=−4000. `q=(ρ−(R_w−600), |y+4000|−400)`, `d = 600 − (|max(q,0)| + min(max(q.x,q.y),0))` | Wall is at R1240 for y_local ∈ [−400,+400]. The roof apex at −3000 embeds the exhaust roof ring (z1021-1039). The floor is at −5000. |
| Bays (3) | spheres at Godot azimuths 90° (arrival), 210°, 330°, centre radius 1050, y=−4000, radii 420 (arrival) / 500 | `cavern = smax(core, bays, k=120)` |
| Arrival tunnel stub | capsule radius 14 from the arrival bay wall (about R1470, y_local −3920) along asset +Z for 300 m, intersected with floor half-space `y − y_floor` | ends in a "connection socket" (D7) |

`SiteCarve = max(bore, shaft, cavern, tunnel)` applies only if the lookup hits and `ρ ≤ 1 700`, `y ∈ [−5 100, +20]` (an AABB pre-reject).

The anchor sectors (Godot 270°/150°/30° ± 33°) keep the wall at R1240. Anchors (R1137-1353) and terraces (outer edge R1252) are therefore embedded by 12 m or more. The arrival tongue (to R1430) lands in the 90° bay. **The Blender reference cave is guidance only.** The acceptance criterion is the embed test (§8.6), not visual parity.

Optional organic wall displacement (D7b): `A·Σ sin`-products with amplitude A ≤ 15 m and wavelength ≥ 600 m, giving L ≤ 1.16. Use it in the bays only.

### 3.3 Integration points

- `RingTerrainSampler.SampleNaturalTerrainDensityWithSurface` (`RingTerrainSampler.cs:517-553`): after computing `density`, apply `if (sites && TryGetInfluencingSite(...)) density = max(density, SiteCarve)`. This sits before the existing early return at `:542-543`, which must not skip it. It covers `SmoothTerrainContracts.SampleWithKnownSurface:206` and `RingTerrainSdfSnapshot.cs:72`.
- Material: carved walls classify as Rock below the soil depth, which is the existing rule at `SmoothTerrainContracts.cs:246-251`.

### 3.4 Interval-proof contract

Add `EmitterSiteRegistry.TryBoundCarve(aroundStart, verticalStart, acrossStart, extent, out lower, out upper)`. It reuses the composer's convention (`CaveTopologyDensityComposer.cs:343-441`):
- quick AABB reject gives `upper = −∞`;
- otherwise `centre ± L·halfDiagonal`, with L = 1 (or 1.16 with displacement);
- the margin is 1e-3.

In `TryClassifyUniformRegularRegion` (`SmoothTerrainContracts.cs:277-330`):

1. Compute the site bound **before** the natural classifier.
2. `siteLower ≥ margin` and above the hull/structural bottom gives **Empty**. This is valid because max-union means air ≥ siteLower. The 3 km × 2 km cavern interior then skips sampling at coarse tiers.
3. `siteUpper > −margin` returns false (decline; full sampling).
4. Otherwise fall through to the existing cave and natural branches unchanged.

The ordering with caves is fine: if either the caves or the site proves Empty, the box is Empty, and otherwise the box declines.

`NaturalFeatureBandsCanAffect` (`RingTerrainSampler.cs:561-575`) has no horizontal coordinates. Do **not** overload it; keep the site check separate as above.

### 3.5 Designed-cave interaction

- Add a site exclusion to `CaveFeaturePlacementPolicy`, bumping `Version` to 2 only when sites are enabled and mixing the registry identity into `Identity` (`CaveFeaturePlacementPolicy.cs:13,57-59`).
  - Protected volume per site: a vertical cylinder `ρ ≤ 1 500 + 400` covering `y_abs ∈ [P − 5 400, P + 50]`, unioned with the shaft cylinder `ρ ≤ R_sh + 200`. The 400 m clearance is at least `MaximumInfluenceHaloMeters` plus wall noise; check this against `CaveFeatureFamilyGenerator.MaximumInfluenceMeters`.
  - `Permits(shape)`: reject if the descriptor envelope intersects any protected volume. This is a conservative AABB vs cylinder test, with the registry's 3×3-cell query.
  - `PermitsConnectivity(feature, r)`: **also test the segment**, by distance from the segment to the site axis segment, compared against `R_prot + r`. Endpoint-only checks (F10) would let corridors tunnel through the reactor.
- Caves use absolute-Y depth bands (F10, `:95-110`), so the protected volume must also be absolute (`P + local`).
- Deliberate connections are a separate option (D7). The "connection socket" at the arrival stub end could become a forced chamber anchor in a later cave-POI phase, consistent with roadmap item 6 in `docs/underground-mode-spec.md:30`.

### 3.6 Underground-mode notes

- The cavern is about 2 km tall and needs coarse radius ≥ 2. The existing P4 profile already provides it (`underground-mode-spec.md:7`).
- The bore gives sky access through 150 m. The enclosure probe should still latch underground at reactor depth; verify with `UndergroundModeSmoke`.

---

## 4. Heightmap backend (gen-17)

1. **Heights:** these are finite and equal P everywhere inside the hole, so RF bounds and the `float.IsFinite` checks in collision (`RingTerrain3DCollisionGeometry.cs:42-44`) stay valid.
2. **Render hole:** for every 2 m cell whose centre has `ρ < hole_radius (80)`, set control-map bit 2 (the shader discards at `terrain3d_ring_surface.gdshader:160-161`).
   - Because the cell diagonal is 2.83 m, this clears every cell that intersects R75. The jagged edge (78.6-81.4) is hidden under the throat annulus (R75-94, F16).
   - Implementation: the worker builds a control image per region **only** when a site hole intersects that region. Otherwise it keeps the existing control-map preservation (`RingTerrain3DSurface.PreparedWindow.cs:296-300`) and the direct path.
   - Verify the bit layout against pinned Terrain3D commit `0077405b` (`DECODE_HOLE` in `addons/terrain_3d/extras/shaders/lightweight.gdshader:149`).
3. **Collision hole:** add `bool[]? holeMask` (192×192 cells) to `PrepareGameplay` (`RingTerrain3DCollisionGeometry.cs:31-36`), and skip quads with `holeMask[cell]`. The mask comes from the registry in the same worker job, so render and collision share one revision.
4. **Edits:** `RingSurfaceHeightField.ApplyBrush` may change heights inside the hole, but the hole remains because it is generator-owned. Pad and bore edit protection is D9.
5. **Before the cave port:** there is no SDF, so the hole would show the void. The POI supplies a **bore liner mesh** (R75-85 cylinder, dark/emissive) down to the reactor depth, or a sealed plug at about −200 m, plus liner collision (D8). This belongs to POI scene ownership, not terrain.
6. **Cave port interplay:** model the hole as a generic **surface aperture** record `{center, radius, owner=Site, sdfVolumeId}`. The roadmap item 5 heightmap→voxel handoff (`underground-mode-spec.md:29`) can then consume a single `GetSurfaceApertures(window)` API for site bores, natural cave mouths, and player digs. Under apertures, Terrain3D shows holes, and near-field SDF chunks own geometry, using the same `SiteCarve` code as §3. The carve library therefore lives in a backend-neutral folder (`scripts/Terrain/Sites/`), not in the Phase3 sampler.

---

## 5. Versioning and saves (recommended safe path)

**Rule: existing worlds never change terrain. Sites apply only to newly created worlds, opted in through a frozen world definition.**

| Backend | Identity change | Old worlds |
|---|---|---|
| Gen-17 | Add recipe `generator_id = "ringworld_surface_sites_v5"`, which carries the v4 fields plus the site fields (`TerrainGenerationParameters.cs`: new `SitesFields` set, `ValidateShape`, `Canonical()`, and an `EmitterSitesDefault()` default). `RuntimeSurfaceGeneratorVersion` stays 17, following the v1→v4 precedent (F5). `CalculateRuntimeTerrainDefinitionHash` (`RingTerrainSampler.SurfaceGeneration.cs:225-237`) appends `EmitterSiteRegistry.AlgorithmVersion` and the registry identity, only for v5. New worlds created through the launcher or preview use v5. | Their frozen recipe stays v4, giving identical hash and terrain. `RingWorldSaveManager.cs:585-590` keeps enforcing recipe equality. |
| Gen-16 smooth | Add `VoxelTerrainSettings.EmitterSiteContractVersion` (int, default 0) and `EmitterSiteSettings`. In `CalculateTerrainDefinitionHash` (`RingTerrainSampler.cs:2036-2165`), use **append-only** mixing: `if (version != 0) { mix(version); mix(params…); mix(AlgorithmVersion); }`, the same pattern as `:2044-2045`. A disabled world produces the bit-identical legacy hash. Keep `GeneratorVersion = 16`; the hash is the discriminator (cache file names already include it). | The slot metadata gains `emitter_sites_version`. Missing means 0. `world_size_launcher.gd:1484-1497` sets the settings field from the slot before `RefreshRuntimeTerrainDefinition`. New slots get 1. A world created with sites cannot load with them disabled, because the hash mismatch already rejects it read-only (`RingWorldSaveManager.cs:475-482`). |
| Designed caves | Placement `Version` 2 and identity include sites, only when enabled. | The cave contract is unchanged for old worlds (`RingWorldDesignedCaveContract.cs:121-131`). |
| Journals / edits | Do **not** add the pre-site revision to `AcceptedLegacyRevisions` (`SmoothTerrainContracts.cs:157-159`). Replaying old edits on a changed baseline is unsafe. | Preserved read-only. |

**Optional P10, "Add emitters to this world":** an explicit user action. It is allowed only if every journal command AABB (smooth) or edit point (surface), and the saved player position, lie outside every site's `R_inf` disc plus 256 m. In that case the pre-site hash is accepted once and the save is rewritten. This mirrors the "accepted legacy hash will be rewritten" path, with a spatial proof.

---

## 6. Consumer audit

| Consumer | Path | Needs |
|---|---|---|
| Near smooth terrain, collision, edits | `RingTerrainSmoothBaseline` → sampler (`SmoothTerrainContracts.cs:179-206`) | Works via §2.7 and §3.3, plus the §3.4 proof change. |
| Terrain3D gameplay and pilot | `GetPresentationSurfaceHeight(AndBiomeWeights)` (`RingTerrain3DSurface.cs:607`, `.SurfaceGeneration.cs:20-42`) | Works, plus the §4 hole and collision mask. |
| Far LOD: global LOD, proxy sectors, scaled far viewport tiles, analytic LOD | `GetTerrainSample` / `GetPresentationTerrainSample` (`RingTerrainGlobalLod.cs:893`, `RingTerrainProxySector.cs:564`, `RingScaledFarViewport.SurfaceTiles.cs:485`) | Works. Caches invalidate via the hash (F7). |
| Min-max pyramid (shadows, cloud-sun) | point samples on a 512 m lattice (`RingTerrainMinMaxPyramid.cs:522-528`) | Under-samples the 700 m pad and bowl. Optional: fold analytic site bounds `[P − maxFill, max(P, bowlRim)]` into base cells that intersect a site. |
| Ocean, shoreline, FFT demand | `TerrainSample.WaterKind`; shoreline field from proxy heights (`RingWaterModel.Ocean.cs:312-317`) | Works. Islands get foam and shoreline automatically. |
| Lakes, rivers (gen-16) | hydrology overrides (`RingTerrainSampler.cs:1145-1191`) | **Suppress** inside sites (§2.7). |
| Grass | biome weights plus exclusions (`RingGrassTileBuilder.cs:127-184`) | **Add mask gate** (§2.8). Grass would otherwise poke through the facility floor at P. |
| Trees | `Biome` gate (`RingBiomeDecorationLayer.cs:810,848`; `TreeStreaming.cs:1071,1156`) | Works once `Biome = Alpine` on hardscape. Also reject trees within R_pad + 80. |
| Minimap, weather, rain, presentation HUD | sampler | Works. Optional site markers. |
| Region classification | `TerrainSample.Region` stays `Ocean` on islands (continentalness unchanged) | Audit the consumers of `Region`. Low risk. |
| Spire far visibility (1.8 km) | not terrain | Follow-up: register the spire impostor with the far viewport or scale anchors. Out of scope. |
| Legacy blocky voxel backend | `GetBaselineBlock` | Force sites disabled there, as gen-17 forbids caves (`.SurfaceGeneration.cs:105-114`). |

---

## 7. Performance

- **Lookup:** about 5-10 ns per sample. Points outside every `R_inf` exit there, which covers about 95% of ring area for gen-17 islands and over 99.7% for land-only sites.
- **Inside influence:** the operator costs about 40-80 ns (one sqrt, a few smoothsteps, a floor, a PCHIP segment, a table lerp). There are **no extra natural samples at runtime**. Against about 3-6 µs per natural sample, the overhead is below 2% inside and about 0.2% overall.
- **SDF carve:** an AABB reject, then 5 primitives at about 30 ns. The interval proof's Empty shortcut *reduces* work inside the cavern.
- **Registry build:** about 600 × 700 natural samples, roughly 420k, which is 1.3-2.5 s single-threaded on the dev laptop. Run it `Parallel.For` over sites with worker-owned models, under 0.4 s on 8 threads. It runs once per definition in the loading phase, before the first height query, through the static cache. A disk cache is optional.
- **Targets:** a 128×128 tile changes by ≤ 3% outside sites and ≤ 10% inside. The startup cache (147k samples) changes by ≤ 3% on average.

---

## 8. Validation plan (new tools/tests; extend existing runners)

1. **Determinism:** build the registry with 1, 3, and 8 threads and compare byte-identical records. Keep a golden fixture per backend (seed 1337, and a seed > 2³²) covering positions, P, β, and flags. Invariants: 600 unique sites; pairwise wrapped distance ≥ 45 km; `|nudge| ≤ 4 km`; across margins; `R_inf + nudge < pitch/2`; ocean sites unmoved.
2. **Lookup:** 2M random points plus seam, wall, and corner points. The O(1) result must equal a brute-force nearest-within-`R_inf` search.
3. **Pad flatness:** every lattice vertex with r ≤ 350 has height exactly P (gen-17) or P−0.5 smooth with presentation P (gen-16). Apron slope ≤ 0.5. Every site passes.
4. **Island enclosure:** for every column with r ≤ R_enc: `WaterKind == None` and `surface ≥ max(Sea+2, P−2800)`. Smooth backend: density < 0 on a shell at `ρ ∈ [1 700, 1 900]` × y-span, and along the roof cover band.
5. **Bore clearance:** smooth backend: density > 0 for `ρ ∈ {0, 37.5, 74.9}`, y from P+14 down to P−3000 at 1 m steps, sampled at 64 azimuths. Heightmap: the hole mask covers every cell that intersects ρ ≤ 75, and no collision triangle has a centroid with ρ < 78.
6. **Reactor embed:** the architecture embed points must be rock, and the interior points air. Embed points are the anchor outer faces, terrace outer edge R1252 at 3 levels × 3 sectors, buttress roots R1260, the tongue end R1430, and the roof ring r 85-115 at z1021-1039. Interior points are the causeway route samples and the terrace inner edges. Use asset-space points rotated by yaw.
7. **Cave exclusion:** across all sites, the composer region over the protected cylinder has `HasTerrainContributors == false`. Add a corridor-crossing regression in `CaveFeaturePlacementDiagnostics`.
8. **Interval proof:** extend `CaveUniformProofDiagnostics` with random boxes near and inside sites at every tier. Each classification must equal the brute-force result. Boxes inside the cavern must be proven Empty, and no box may be misclassified.
9. **Far-LOD agreement:** the min-max pyramid, proxy sector, and far tile heights at site centres must equal P within each tier's quantisation. Grass and tree tiles over the pad must contain 0 instances.
10. **Save:** the pre-change v4 recipe and the gen-16 `EmitterSiteContractVersion=0` world hashes must equal recorded goldens. A sites world must save and load round-trip. A sites world loaded with sites off is rejected read-only, and old journals are not replayed. The v1-v4 CPU parity fixtures (`tools/terrain_generation_cpu_parity_test.gd`, `RuntimeTerrainCpuPass2Test.cs`) stay green.
11. **Seam:** with a phase that places a row at around ≈ 0, check height continuity across the wrap with finite differences.
12. **Perf:** a lookup microbenchmark, a tile delta, and registry build time cold versus cached.

---

## 9. Phasing (each phase implementer-sized and testable)

| Phase | Deliverable | Tests |
|---|---|---|
| P0 | Record golden hashes for current v4 and gen-16 definitions; add the fixtures. No behaviour change. | 10 (baseline) |
| P1 | `scripts/Terrain/Sites/`: `EmitterSiteRecipe`, `EmitterSiteRegistry` (grid, nudge, P, β, road table, flags, O(1) lookup), `INaturalHeightSource` adapters. Not wired into terrain. | 1, 2 |
| P2 | `EmitterSiteSurfaceOperator` (pure) plus `SiteSurfaceMask`, unit-tested on synthetic h0 fields: flat, 30° slope, ridge, ocean −4500, trench −7500. Includes C0/C1 finite-difference checks. | 3, 4 (synthetic), 11 |
| P3 | Gen-17 v5 recipe: parameters, `terrain_generation_recipe.gd`, model hook, diagnostics, runtime hash. Preview sliders in `RingLiveTuningDrawer.GenerationPreview.cs:42-57`. | 1, 3, 4, 10 |
| P4 | Surface consumers: `TerrainSample` mask, biome override, grass/tree gates, Terrain3D hole control map, collision mask. POI placement API `GetPlacement(i) → (A, X, P, θ)` for the POI scene owner. | 5 (heightmap), 9 |
| P5 | Gen-16: settings contract, append-only hash, `ShapeDryHeight` in 3 paths, lake/river suppression, slot metadata and launcher plumbing. | 3, 4, 10 |
| P6 | SDF carve library, density hook, `TryBoundCarve`, uniform-proof integration. | 5, 6, 8 |
| P7 | Cave placement exclusion (Version 2) including the corridor segment test. | 7 |
| P8 | Grave-key (per AGENTS.md): read-only "Emitter sites" diagnostics in the Surface Terrain / Underground drawers (nearest site, P, flags, distance, cut/fill stats); a debug overlay of influence rings on the minimap; a dev "teleport to site N"; recipe controls in the generation preview, frozen per world. Preferences persist via the existing `Gameplay*Settings` pattern; world-shape values only via recipe or slot. | settings smoke |
| P9 | Polish: min-max pyramid analytic bounds, human review at ground, flight, and orbit, the perf report. | 9, 12 |
| P10 (opt) | Spatially proven opt-in migration for existing worlds. | 10 |

---

## 10. Decisions for the user

- **D1** Pad height policy. Recommended: median of the footprint, cut ≤ 150 / fill ≤ 150, min +8 on land, min +24 on islands, integer metres. Alternative: fixed elevation tiers.
- **D2** Island look and size. Recommended: natural dome, 250 m beach, 2.0 km dry radius, 30° seamount flank. Alternative: engineered plateau with sea cliffs; a gentler flank widens islands up to about 20 km.
- **D3** Nudge. Recommended: 4 km, flatness-preferring, ocean sites stay put (island). Alternative: also allow ocean sites to nudge toward coast within N km.
- **D4** Mountain carve style. Recommended: terraced bowl (30 m risers, about 39° walls), skirt ≤ 3 km. Alternative: smooth cone.
- **D5** Approach road. Recommended: yes, 20 m wide, 8% grade, ≤ 2.5 km; on islands it ends at the beach. Alternative: a jetty.
- **D6** Pad material. Recommended: existing rock (alpine) channel. Alternative: a new plaza texture via the control-map texture slot.
- **D7** Reactor access. Recommended: 300 m stub plus a future shaft lift. Alternatives: a forced link to designed caves, or a surface ramp tunnel (impractically long). **D7b** cavern wall displacement noise.
- **D8** Heightmap backend before the cave port. Recommended: bore liner mesh plus a sealed plug. Alternative: an open void.
- **D9** Edit protection on the pad, bore, and reactor shell. Can players dig into the reactor?
- **D10** Existing worlds. Recommended: stay site-free. Alternative: the P10 opt-in migration.
- **D11** Grid phase: seed-derived (recommended) or around = 0.
- **D12** Tuning exposure. Recommended: world-shape parameters in the preview recipe (frozen per world), with read-only in-world diagnostics. There would be no live mutation of a running world's geography.
- **D13** Must site positions match across backends? If yes, use a hash-only nudge, independent of terrain.
- **D14** Reactor cavern sizing. The architecture forces a wall at about R1240 in the anchor sectors (F15), not R1500. Confirm the bay layout, or ask the asset author to move the anchors outward.

## Appendix: key constants derived from assets

- Spire: pad must cover R ≥ 308 (AABB). Facility to R194. Throat R75-94 × 0..14 m. Entry at 292.5° (Blender), which is Godot XZ 67.5°.
- Reactor (chamber-centre frame, Godot): anchors at 270°/150°/30°, R1137-1353, y −360..+530. Terraces R1170-1252 at y −180/+80/+330, ±32.7°. Buttress roots about R1260, y +90..+275. Arrival tongue at +Z, R1333-1430, y +69..+81. Exhaust to +1039, roof ring r75-115 at y +1021..+1039. Heart y −298..+299.
- Local datum: chamber centre = P − 4000. Exhaust top = P − 2961. Cavern roof ≈ P − 3000. Floor ≈ P − 5000.

---

## USER DECISIONS (2026-10-14) — binding

- D2 Island look: natural dome (beach, ~2 km dry radius, 30° seamount flank).
- D4 Mountain carve: terraced bowl (30 m risers).
- D5 Approach road: NONE. Pad only; terrain meets the structure directly.
- D9 Edit protection: fully editable. Players may dig anywhere, including the pad, bore and reactor shell (no protected-volume rules).

Defaults accepted unless the user overrides:
- D1 pad height = footprint median, cut/fill capped at 150 m, minimum +8 m land / +24 m island.
- D3 nudge up to 4 km on land; ocean sites stay in place and get an island.
- D6 pad uses the existing rock channel (no new plaza texture yet).
- D7 reactor access = stub tunnel + lift; no designed-cave link yet; no wall displacement noise.
- D8 heightmap backend before the cave port = bore liner + plug.
- D10 existing worlds stay site-free; no migration.
- D11 grid phase seed-derived.
- D12 world-shape values in the generation recipe (frozen per world) + read-only in-world diagnostics + teleport-to-site tool.
- D13 positions may differ per backend (terrain-aware nudge).
- D14 accept the ~R1240 cavern wall.
