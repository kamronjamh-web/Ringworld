# Session handoff 2026-11-09: vastness plan and the Fable agent

## Where things stand

- **Fable agent added.** `.claude/agents/claude-fable.md` runs Fable at high effort for world building, creative direction and project planning. Its model is pinned to `claude-fable-5-2`; the user updates the pin when a new model ships. The role is in the AGENTS.md table and CLAUDE.md. Ask the user before spawning it (rule in AGENTS.md).
- **Open problem: the agent spawns Fable 5.1, not 5.2.** The first run used the `fable` alias. The second used the pinned `claude-fable-5-2` ID. Both came up as 5.1, and both runs were stopped before writing anything.
  - Ruled out: no project settings set a model. User `settings.json` has only `"model": "opus"`, for the main session. The env var check (for example `CLAUDE_CODE_SUBAGENT_MODEL`) was blocked by the permission check, so it is still unverified.
  - Suspected: this Claude Code build does not know 5.2 (its built-in model list stops at `claude-fable-5-1`) and resolves an unknown ID to the family default. The other possibility is that agent definitions are cached at session start.
  - **First step in the new session:** spawn `claude-fable` with the brief below and check the model ID on its first line. If it is 5.1 again, stop it and tell the user that updating Claude Code is the likely fix. Do not accept a 5.1 result. Fallback: the user switches the main session with `/model claude-fable-5-2` (that worked earlier) and the main session writes the plan.
- **No plan doc exists yet.** `docs/vastness-plan.md` has not been written.
- **Memory corrected:** Physical haze has been the default since 2026-10-23, not Legacy.

## Background: the vastness brainstorm

The user asked for visual tricks that convey vastness and picked four areas to plan: 1, 2, 4 and 5 below. Item 3 (signs of life on the far arch: settlement lights, flocks, craft) is out of scope for now.

1. **Light shafts and shadowed air.** Deferred in `docs/atmosphere-haze-revamp-plan.md` (P9 notes the plug-in point: per-path sun visibility replaces `ringworld_air_sun_visibility`). Two things wait on this step: per-target terrain cloud shadow on haze, and the far-arch remote shell using camera-dimmed E_sun. Valley mist pooling is also deferred to it.
2. **Aerial perspective tuning.** Aerosol and ForwardScattering are both 0 in the defaults, which removes the glow around the sun. Terrain albedo scale 0.15 makes far terrain dark and the haze milky.
4. **Scale ladder.** Objects of known size in every distance band: POIs, the emitter grid, projector towers, the rim sidewalls.
5. **Large shadows and slow motion.** Terrain height shadows (off by default, user review pending), far cloud shadows on the arch, terminator crispness, side-lit rain curtains, an eclipse shadow sweep.

## Fable brief (reuse verbatim)

> First line of your final reply: state your exact model ID (for example `claude-fable-5-2`), so I can confirm which model ran.
>
> Goal: produce a plan for making Ringworld (Godot 4.8 .NET, a 5,000 km radius / 318 km wide ring habitat) feel vast. Write it to `docs/vastness-plan.md` and return a short summary: your recommended order, the first phase in detail, and the open questions for the user.
>
> Scope is four areas the user picked from a brainstorm:
>
> 1. Light shafts and shadowed air. The haze revamp deferred froxel/light-shaft volumetrics. Read the Handoff, P9 and P10 sections and the Decisions list of `docs/atmosphere-haze-revamp-plan.md`. The plug-in point: per-path sun visibility replaces the camera-column `ringworld_air_sun_visibility` in the in-scatter term. Two items wait on this step: per-target terrain cloud shadow on haze, and the far-arch remote shell using camera-dimmed E_sun (a storm at the camera dims the sunlit far arch). Valley mist pooling is also deferred to it. Candidate idea, which you should challenge: a cheap low-res view-ray march of the existing cloud shadow field (and later the terrain height-shadow clipmap, plus the analytic rim-wall plane) as path visibility, with no froxel volume.
> 2. Aerial perspective tuning. Physical haze has been the default since 2026-10-23 with the user's values: Aerosol 0, ForwardScattering 0, SunWarmth 0, HazeBrightness 0.52, VisibilityFullKm 400, Density 0.5. Terrain albedo scale is 0.15, so far terrain is very dark and haze looks milky at altitude (decision 1 in the haze plan). The user rejected ringshine and an arch veil and accepted the physical far-arch result; do not propose those again. Ideas so far: a little aerosol with forward scattering for a sun glow, an inversion-layer haze band so ridges stack in tiers, desaturation before bluing with distance, and possibly the albedo/sun-energy retune (option C).
> 3. Scale ladder. Vastness needs objects of known size at every distance band. Existing pieces: POIs (`assets/poi/`, `docs/emitter-poi-streaming-spec.md`), emitter sites and the relay grid with a far layer (`docs/emitter-grid-climate-machines-spec.md`), containment field projector towers (`docs/containment-field-design.md`), the rim sidewalls, trees and grass. Propose how to find empty distance bands (a fly-out capture audit) and what to place in them; this is partly world building, so say what these structures are in-world.
> 4. Large shadows and slow motion. Terrain height shadows (`docs/shadow-revamp-plan.md`, all off by default pending user review, with an open planet near-map perf regression), far cloud shadows on the arch (`scripts/Voxel/LOD/RingScaledFarViewport.FarWeatherShadow.cs` and friends), terminator crispness on the arch, side-lit distant rain curtains (`scripts/RingDistantRainCurtains.cs`), and an eclipse shadow sweep (`scripts/Celestial/Eclipse/`).
>
> Constraints from AGENTS.md: new tunables go in the grave-key live tuning menu, but only genuine tunables (haze as a whole, never per-subsystem toggles such as "POI haze"); POIs must receive regular haze, shadows and lighting automatically. Perf is judged on an RTX A1000 laptop (authoritative) as well as an RTX 4070 desktop; the far viewport renders at 1/1000 scale, and the main viewport root must stay LDR 2D. Haze object, cloud and sky results must keep agreeing (`haze_consistency_gpu_test` contracts).
>
> Already ruled out: ringshine, arch veil, and settlement/night lights on the far arch (out of scope for now).
>
> For each area, the plan should give the recommendation and the main alternative you rejected, the systems it touches, a rough cost estimate and a perf budget, the verification (tests, ffmpeg captures via `tools/media/ffmpeg_tools.ps1`), and the user decisions it needs. Then give phases in a recommended order that `claude-lead` and `implementer` can take over. Mark what is cheap slider work and what is new engineering. Do not edit code, shaders, scenes or settings; only write the plan doc.

## After the plan is written

The primary agent reviews `docs/vastness-plan.md` and reports to the user: the model ID that ran, the recommended order, the first phase in detail, and the open questions.

## Uncommitted changes from this session

`AGENTS.md`, `CLAUDE.md`, `.claude/agents/claude-fable.md` and this handoff. Nothing has been committed. The branch is `gpu-offload`, which also carries unrelated uncommitted user changes.
