# PDA debug prototype handoff — 2026-11-06

The F10 prototype now supports prepared game context, world-scoped conversation memory, narrow authored answer rules and opt-in Gemma images. **The reported slow Gemma response during gameplay is unresolved.** The user confirmed CUDA selection with **Attach current view** enabled. The isolated CPU measurements below do not explain that incident, and the synthetic CUDA checks do not certify live-game performance.

Gemma 3 4B Q4_K_M and Qwen3 8B Q4_K_M are retained **for testing only**. The legacy `qwen3-0.6b` default remains intentional. There is one selected resident helper; inference and image capture happen on demand. This handoff does not introduce continuous inference, two concurrent LLMs, a new default, or a shipping-model decision.

## Entry points and architecture

| Component | Responsibility |
|---|---|
| [PdaDebugChatPanel](../scripts/Pda/Debug/PdaDebugChatPanel.cs) | F10 UI, explicit reload, CPU/CUDA layer requests, Focused/Baseline selection, accepted replies, memory binding and cancellation |
| [PdaContextFeed](../scripts/Pda/Debug/PdaContextFeed.cs) | Main-thread prepared readings and categorical history; a fresh snapshot for each question |
| [Context types and composer](../scripts/Pda/Debug/PdaContextSnapshot.cs) | Bounded focused selection, typed evidence, snapshot provenance and unverified conversation retrieval |
| [Authored knowledge](../resources/pda/debug_knowledge.json) | Full inspection text plus compact model explanations; current activation comes from live readings |
| [PdaAnswerPolicy](../scripts/Pda/Debug/PdaAnswerPolicy.cs) | Narrow typed current-state/unavailable-reading replies and read-only action refusal; replacement of clear generated false action claims |
| [PdaConversationStore](../scripts/Pda/Debug/PdaConversationStore.cs) | Validated, bounded, atomic per-world transcript storage and identity isolation |
| [PdaCameraCapture](../scripts/Pda/Debug/PdaCameraCapture.cs) | One PNG within 640×360 / 2 MiB, excluding the PDA panel and pairing the image with a fresh snapshot |
| [LocalPdaQaClient](../scripts/Pda/Debug/LocalPdaQaClient.cs) | Serialized asynchronous JSONL, actual backend verification, owned helper/temp-image lifetime and cancellation |
| [PdaQaWorker](../prototypes/pda-voice/PdaQaWorker.cs) / [vision executor](../prototypes/pda-voice/BoundedPdaVisionExecutor.cs) | Resident language weights, lazy matching projector, fresh request contexts, multimodal budget checks and measured response metadata |
| [Model comparison](../prototypes/pda-voice/PdaModelComparison.cs) / [vision smoke](../prototypes/pda-voice/PdaVisionSmoke.cs) | Explicit standalone evaluation, separate from gameplay acceptance |

The gameplay scene wires the feed and panel as siblings in [VoxelRingIntegrationTest](../scenes/VoxelRingIntegrationTest.tscn). Existing automatic PDA announcements keep their authored announcer path. Free-form inference does not run inside its synchronous line-provider callback. This feature currently returns text; speech input and generated audio remain separate work.

In F10, choose a model and placement, set GPU layers if applicable, then **Reload model**. Sixteen layers requests partial offload; 99 requests all available layers. Actual offloaded layers are reported, and other allocations still use system RAM. Keep **Focused** for normal debug questions. **Clear display** retains conversation memory; **Reset memory** removes the current world's transcript. Only Gemma supports **Attach current view**. These remain debug controls.

Memory binds lazily to the active playable saved world, not menu warm previews. It retains the newest 64 accepted turns under `user://pda_debug/conversations/`, with a `.pda_conversation_identity.json` sidecar in the world directory. Existing `world.json` is read without schema changes. Copied/recreated worlds receive separate identities. Old assistant answers are unverified transcript, never current sensor facts. See the [memory/vision guide](pda-debug-memory-and-vision.md) for lifecycle and storage details.

## Fresh-checkout bootstrap

Model weights, project-local engine/SDK archives, helper binaries and `.godot` reports are excluded from Git. Provision the pinned Godot/.NET environment using the repository's [engine setup guide](godot-48-native-texture-streaming.md), then explicitly install the retained test assets and build both helper outputs:

```powershell
.\tools\install_pda_test_models.ps1 -IncludeVisionProjector
.\tools\install_pda_test_models.ps1 -IncludeVisionProjector -VerifyOnly
.\tools\build_pda_worker.ps1
.\tools\build_pda_worker.ps1 -Comparison
.\tools\use_godot_48.ps1 -Build
```

The installer defaults to the two retained test models. `-ModelKeys gemma-3-4b-q4 -IncludeVisionProjector` installs only Gemma and its projector; `-VerifyOnly` performs no downloads. It verifies immutable revisions, sizes and SHA-256 pins from tracked [model metadata](../resources/pda/model_artifacts.json) and [projector metadata](../resources/pda/gemma_vision_projector.json), resumes owned partial files, and refuses unexpected existing final files. It does not install an SDK, build or run inference. Do not use ignored `.godot` metadata as a bootstrap dependency.

Use `-NoRestore` only after the first successful dependency restore. The canonical output is `prototypes/pda-voice/bin/qa-runtime/`; explicit comparison checks use `bin/qa-comparison/`. Both are rebuildable, ignored output. The retained-asset installer does not fetch legacy small-model weights: on a fresh checkout without them, select installed Gemma or Qwen3 8B in F10 and reload. The default remains unchanged rather than silently promoted.

CUDA requires a compatible driver and real CUDA 12 runtime DLLs. The client accepts a CUDA 12 toolkit through `CUDA_PATH` with its `version.json` and `bin/cublas64_12.dll`, `cublasLt64_12.dll`, and `cudart64_12.dll`. Its existing fallback searches ignored prototype Torch environments; those environments are **not** supplied by a clone or NuGet restore. Provide the CUDA 12 runtime explicitly on another machine. The feature does not install it or change global environment variables. A clean-machine bootstrap has not been certified by the feature checks below.

## Validation checkpoint

The feature-validation checkpoint contains **11 distinct focused PASS verdicts**, aggregated across the following runs. Later portability verification is recorded separately below. This is targeted validation, not a gate/full suite or live-game FPS acceptance. Earlier store failures remain in local audit output; the later store/UI run supplies their corrected verdicts.

| Check | Checkpoint PASS run | Seconds |
|---|---|---:|
| `pda_context_test` | `20261106-160203` | 4.9 |
| `pda_conversation_store_test` | `20261106-161312` | 6.0 |
| `pda_debug_ui_test` | `20261106-161312` | 1.4 |
| `pda_answer_policy_test` | `20261106-160203` | 1.1 |
| `pda_model_catalog_test` | `20261106-160203` | 1.1 |
| `pda_camera_capture_test` | `20261106-160606` | 3.7 |
| `pda_focused_gemma` | `20261106-160203` | 26.8 |
| `pda_focused_qwen3` | `20261106-160203` | 63.9 |
| `pda_vision_gemma_gpu` | `20261106-155649` | 39.5 |
| `pda_vision_gemma_cpu` | `20261106-160606` | 113.8 |
| `pda_debug_ui_capture` | `20261106-161516` | 5.1 |

The local source for this table was `.godot/pda_memory/validation.json`, SHA-256 `2e5f9b99d9fd398c3235a72fc2ba2c1aa08df7e8d48f3cab51cfd532df79319e`. That ignored file and its raw logs/captures are not packaged with this document. The table preserves the compact verdict snapshot; rerun the commands in the [feature guide](pda-debug-memory-and-vision.md) to generate local evidence. The primary reviewed the rendered panel capture. Helper builds were clean and the game build, manifest validation and scoped whitespace checks passed at this checkpoint.

### Post-portability verification

After the tracked-artifact/bootstrap packaging changes, the primary reran `pda_vision_gemma_gpu`: **1/1 PASS**, run `20261106-165342`, test time **34.3 s**, runner wall time **36.2 s**. This confirms the packaged source path's GPU vision protocol/backend/lifecycle check on the existing machine. It is separate from the comparable performance samples below and does not establish clean-machine installation or live-game FPS acceptance.

### Measurements and their limits

Hardware was an RTX A1000 6GB Laptop GPU and i7-13700H, with four CPU inference threads. Focused synthetic text cases made seven model calls and three policy-only replies per retained model. Median model-call wall time was 2.06 s for Gemma and 4.53 s for Qwen, with 16 language layers on CUDA; policy times were excluded. Both recovered the older Lantern nickname, and the bounded current-rain path rejected stale historical precipitation.

| Synthetic Gemma vision mode | Image-answer wall time | Sampled peak device VRAM | Peak helper working set |
|---|---|---:|---:|
| CUDA, 16/35 language layers | First 7.29 s including lazy projector initialization; later 3.41–3.91 s | 3225 MiB / 3.15 GiB | 3.28 GiB |
| System RAM/CPU | First 50.08 s; second 46.85 s | 0 MiB in that fixture | 4.48 GiB |

These are simple red/blue image fixtures, not a rendered-world route. Sampled device VRAM is device-wide and can miss peaks. The 3.15 GiB figure does not establish spare VRAM while terrain/clouds are rendering. Correct dominant-color descriptions and transport/backend PASS do not certify complex scene understanding, leak freedom or frame performance.

Known quality gaps remain: Qwen added an unsupported eclipse/hub cause in an explanation; Gemma combined zero hazard values using incorrect units. Both are outside the narrow authored guard cases. Native prefill/decode counters are unavailable in the pinned wrapper; `n/a`/null is not zero work. Cancellation during synchronous native image prefill was not measured by these bounded smoke runs; the owned-helper watchdog remains the fallback.

## Unresolved live CUDA + image slowdown: next work

The user's slow case is **Gemma 4B, CUDA selected, current-view attachment on, during gameplay**. It remains unresolved. GPU capacity/contention is plausible given simultaneous world rendering and projector allocations, but has not been demonstrated. Do not substitute the isolated CPU timing as a diagnosis, and do not treat a requested CUDA setting as complete evidence of actual language/projector placement.

Use one selected resident Gemma helper and the same world/save, camera pose, question, game settings, weather/lighting conditions and memory/context settings. Alternate current-view **off/on**, separating the first projector load from warm requests. Choose a question that actually reaches the model; policy-only replies must be reported separately. Record:

1. Requested mode/layers and actual `BackendInfo` device/offloaded layers; also `PdaQaReply.ProjectorBackend` / `ProjectorDevice` from image replies. Projector placement is reported on replies but is not currently displayed in the panel's backend line.
2. End-to-end wall time, capture time, prompt/image context size, first-text latency, queue/readiness and total generation time. Retain prefill/decode as unavailable unless a supported measurement is added; do not derive fake prefill timing from TTFT.
3. Helper working set/private bytes, system committed RAM, device dedicated/shared memory and free GPU headroom, alongside game frame times using a matched route/workload.
4. Supplied context and final answer for separate factual review, including whether an image was actually used.

Keep latency, factual quality and gameplay frame performance as separate verdicts. Complete this bounded A/B before changing the default, adding continuous inference, running two LLMs together or selecting a larger model. No live-world speed or FPS target is marked complete by this handoff.
