# PDA debug memory, focused context and current-view images

This is an experimental F10 debug feature. Gemma 3 4B Q4_K_M and Qwen3 8B Q4_K_M remain test candidates; adding them to the debug picker does not choose a final model or change the existing small-model default. One selected helper stays resident. Inference happens for a submitted question, not continuously during play.

The [2026-11-06 handoff](pda-handoff-2026-11-06.md) preserves the compact 11-check validation snapshot and the unresolved live CUDA-plus-image performance issue. Local `.godot` logs, reports and captures are ignored evidence, not assets supplied by a fresh checkout.

## Trying it

For a fresh checkout, first provision the repository's pinned Godot/.NET environment using the [engine setup guide](godot-48-native-texture-streaming.md). Model weights, helper executables and `.godot` evidence are not committed. Install the retained assets from tracked pins and build with dependency restore enabled:

```powershell
.\tools\install_pda_test_models.ps1 -IncludeVisionProjector
.\tools\install_pda_test_models.ps1 -IncludeVisionProjector -VerifyOnly
.\tools\build_pda_worker.ps1
.\tools\build_pda_worker.ps1 -Comparison
.\tools\use_godot_48.ps1 -Build
```

The installer defaults to `qwen3-8b-q4` and `gemma-3-4b-q4`; `-ModelKeys gemma-3-4b-q4 -IncludeVisionProjector` selects Gemma alone. `-VerifyOnly` performs no downloads. Tracked [model pins](../resources/pda/model_artifacts.json) and [projector pins](../resources/pda/gemma_vision_projector.json) provide immutable revisions, sizes and SHA-256 hashes. Existing mismatched final files are preserved and refused. The installer does not build, start inference or install CUDA. Use `build_pda_worker.ps1 -NoRestore` only after the first successful restore.

The retained-asset installer does not install the older small-model weights. If they are absent on a fresh checkout, select an installed retained model and reload; the intentional `qwen3-0.6b` default is not silently changed. `qa-runtime` is the canonical helper output; `qa-comparison` is a separate explicit evaluation build.

CUDA needs actual CUDA 12 runtime DLLs and a compatible driver. The client accepts a toolkit via `CUDA_PATH` when its version metadata and CUDA12 `cublas`, `cublasLt` and `cudart` DLLs are present. Its ignored local Torch-environment fallback is not part of Git or NuGet bootstrap. Provision a real compatible runtime on another machine; no untracked environment is assumed portable. See the [handoff bootstrap](pda-handoff-2026-11-06.md#fresh-checkout-bootstrap) for exact DLL names and limitations.

Open F10 in a playable world. Select a local model and CPU/system RAM or CUDA offload. A GPU layer request of 16 selects the tested split; 99 requests all model layers. Press **Reload model** to apply model and placement changes. Status shows the actual language-model backend and layer count.

The context selector defaults to **Focused**; **Baseline** retains the older broader context composition for comparison. Both panel modes apply the deterministic answer policy. The context inspector shows the exact supplied context and selection diagnostics.

**Use conversation memory** enables the bounded transcript and retrieval. **Clear display** clears the visible transcript while retaining conversation memory. **Reset memory** cancels an active question and removes the current world's stored transcript, retaining its world identity. Storage failures are nonfatal: questions still work, and the status explains the memory limitation.

For Gemma, enable **Attach current view (Gemma)** to attach one fresh image to the next model question. There is no periodic image capture or background vision inference. A deterministic policy answer does not need an image or an inference call. Qwen's retained model is text-only.

## Context and answer policy

The existing main-thread feed continues to sample local providers and captures a fresh snapshot when a question is submitted. Focused composition selects readings by the question and relevant causal systems. Lighting questions retain eclipse, hub, weather, storm and enclosure evidence needed to distinguish causes, while unrelated metrics and clocks are withheld. Authored general rules stay separate from current observations.

The focused path does not inject old sensor changes or dialogue into recognized current-state questions. Past sensor changes are eligible for explicit history/change questions. Explicit conversation recall uses bounded lexical retrieval across the newest 64 stored turns; unresolved follow-ups can use recent dialogue. This is simple retrieval, not an embedding index or vector database. Retrieved answers are labelled unverified conversation data and are never promoted to current game facts. Dialogue is JSON-escaped before inclusion.

The authored character identity remains in the worker instructions. This pass does not infer permanent personality or relationship traits from generated answers.

`PdaAnswerPolicy` supplies code-owned replies for bounded intents:

- Explicit supported control requests receive an authored read-only refusal before inference.
- Recognized questions about unavailable oxygen, health or inventory receive an unavailable-reading response; mixed or causal questions remain model work.
- Recognized current rain/snow, artificial-hub and eclipse questions use typed evidence from the freshly captured providers. Uninitialized values do not become zero, and underground suspension prevents a cached precipitation drive from becoming an outside-weather observation.
- Clear generated first-person action confirmations, including the reproduced false activation-sequence claim, are replaced before display or storage.

These rules are deliberately narrow. They are not a general semantic validator, and free-form model explanations can still be wrong. Raw rejected claims do not enter accepted conversation memory.

## Persistence and world identity

Normal gameplay stores transcripts under `user://pda_debug/conversations/<world-guid>.json`. A lazy `.pda_conversation_identity.json` sidecar inside the active save directory supplies the GUID. Its owner fingerprint uses the normalized absolute world directory, slot and creation time. Godot's equivalent integral JSON number formats, including `1.0` and exponents, are normalized for this purpose. Existing `world.json` data is read, not rewritten.

Deleting/recreating a slot removes its sidecar and establishes a new namespace. A copied identity with a different owner receives a new GUID. Renaming a world or changing playtime/generation settings does not change the memory identity. Each captured question freezes its world/scope/serial so a late answer cannot appear in or be saved to a different world.

The store retains the newest 64 accepted question/answer turns with model, answer kind, timestamps and snapshot provenance. Documents, fields and schemas are bounded and validated; writes are serialized and atomic within one running game process. Corrupt or unknown-schema files are preserved and reported. Simultaneous game processes writing the same transcript are outside this first prototype's concurrency contract.

Binding is lazy and gated to visible interaction in the active playable world. Menu warm previews do not create conversation records. Headless automatic persistence is disabled; tests must supply explicit isolated world and memory roots under `.godot/`. Direct scenes without an active saved world use session-only conversation memory.

## Images and native lifetime

The matching Gemma projector is `_comparison/mmproj-model-f16.gguf`, pinned in [tracked metadata](../resources/pda/gemma_vision_projector.json) to ggml-org revision `d0976223747697cb51e056d85c532013931fe52e`, 851,251,104 bytes, SHA-256 `8c0fb064b019a6972856aaae2c7e4792858af3ca4561be2dbf649123ba6c40cb`. It is loaded lazily and retained for the selected helper's lifetime. The text and vision paths use pinned LLamaSharp 0.27.0/native packages.

Capture hides the PDA panel for a rendered frame, reads the current viewport, restores the panel/focus and produces a PNG within 640×360 while preserving aspect ratio. Other game HUD elements may remain. A fresh game snapshot is paired with the captured image's timestamp and version. Appearance is explicitly labelled as an observation, not authority for hidden state, lore, injury or system causes.

The client owns a temporary PNG under `.godot/pda_images/<session-guid>/<request-guid>.png`. Both caller and helper validate bounds, PNG metadata and path containment. The helper reads bounded bytes through a locked file and uses exactly one trusted media marker in the user turn. Temporary files are removed after a terminal response or confirmed owned-process exit.

Language and projector weights stay resident; each image question gets a fresh context/executor. Image token positions are counted before evaluation against the 4096-position context and reserved answer budget. Language-layer placement and the projector's actual CPU/CUDA backend are reported separately. `PdaQaReply.ProjectorBackend` and `ProjectorDevice` provide the latter; the panel's backend line currently shows language placement, not projector placement. Contexts, pending chunks and media handles are disposed per question, preventing the previous image from becoming an implicit input to a later text question.

Native image loading/encoding can be synchronous. Cancellation retains the existing eight-second watchdog and may terminate/reload only the owned helper if native work does not stop cooperatively. This is not a guarantee of instantaneous cancellation. Vision memory and latency differ from the text-only benchmark; game FPS and world-rendering headroom require separate measurements.

## Focused validation

Use the repository runner with an isolated reusable task profile; avoid branch-wide `-Changed` for this work order:

```powershell
.\tools\run_tests.ps1 -Tests pda_context_test,pda_conversation_store_test,pda_answer_policy_test,pda_debug_ui_test,pda_model_catalog_test,pda_camera_capture_test -Label pda_memory -Cache reuse
```

Build the isolated evaluation helper and run retained-model checks explicitly:

```powershell
.\tools\build_pda_worker.ps1 -Comparison -NoRestore
.\tools\run_tests.ps1 -Tests pda_focused_gemma,pda_focused_qwen3,pda_vision_gemma_gpu -Label pda_memory -Cache reuse -NoBuild -Parallel 1
.\tools\run_tests.ps1 -Tests pda_vision_gemma_cpu -Label pda_memory -Cache reuse -NoBuild -Parallel 1
```

The optional `pda_debug_ui_capture` manual GPU tool renders the worldless fixture to `.godot/pda_memory/ui_capture/panel.png`. It refuses an existing image; retain or rename previous evidence before another capture. The underlying `pda_debug_ui_test.gd` also accepts `--capture-dir res://.godot/<task>` for another contained destination.

The runner owns the GPU queue. Focused comparisons preserve the original ten textual fixtures, add explicit typed evidence in `resources/pda/model_comparison_evidence.json`, and separately report model calls versus policy-only responses. They also retain unscored older-nickname/current-rain memory probes. Original baseline composition is still available; comparing the old raw runs with focused-plus-policy results does not isolate context selection as the sole cause of any improvement.

Vision smoke uses deterministic red/blue panels, image→text→different-image sequences, actual backend/provenance/budget checks and owned-helper lifecycle checks. GPU runs repeat seven requests; CPU runs use three with longer explicit deadlines. Transport PASS does not grade model explanations. Read the saved raw answers and inputs for semantic review. These synthetic fixtures and the worldless camera test do not certify real-world scene understanding, absence of memory leaks, or game-frame performance.

## Unresolved live performance

The user confirmed **Gemma4B with CUDA selected and Attach current view on** during the slow gameplay case. Its cause remains unresolved. Isolated CUDA image answers took 7.29 s on first projector use and 3.41–3.91 s warm, with a sampled 3.15 GiB peak device VRAM; CPU image answers took roughly 47–50 s. The CPU result is a separate baseline, not a diagnosis of the confirmed CUDA case. The GPU fixture did not render the live world, and capacity/contention is plausible rather than proven.

Next, alternate image off/on using the same saved world, camera, question, context/memory settings and rendering workload. Capture actual language layers and projector backend, request/capture wall time, TTFT, queue/readiness, system RAM commitment, GPU headroom and game frame times. Native prefill/decode counters remain unavailable; synchronous native image-prefill cancellation was not measured by the bounded smoke. Keep one resident selected helper and separate policy-only timings. The [handoff](pda-handoff-2026-11-06.md#unresolved-live-cuda--image-slowdown-next-work) gives the full measurement checklist; no default or concurrency change is justified by the current synthetic results.
