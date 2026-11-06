# PDA debug questions and prepared context

The debug PDA lets a developer ask questions about the running game using local inference. Its information comes from a prepared feed of game readings, a small authored knowledge catalog, and recent changes. Existing automatic PDA announcements continue through their existing path.

## Using the panel

- In a debug game build, press **F10** to open or close the PDA panel. **Ctrl+F10** remains the precipitation debug shortcut.
- The local model preloads when the interactive debug game scene starts and remains resident while you play. Type a question and submit it with Enter or **Ask**.
- Select a model and **System RAM (CPU)** or **VRAM (CUDA)**, then press **Reload model** to apply the selection. The status shows the backend actually loaded, its device, and the number of layers offloaded. Selections remain pending until reload.
- Inspect **Context supplied** to see the actual facts and explanations submitted with the question. This records the model's input; it does not certify that the answer used every fact correctly.
- **Cancel**, **Clear**, and closing the panel cancel a pending answer while keeping the model loaded. **Reload model** and leaving the scene release the owned helper process. Reload replaces that process using the selected configuration.
- The conversation and debug controls are session-only. Opening the panel releases the cursor and blocks player input while the world continues updating.

The panel is a debug tool rather than an in-game PDA screen. It does not add gameplay settings or change world saves. This version returns text; speech input and generated audio are separate integrations.

## Information available

| Domain | Supplied readings |
|---|---|
| Player location and movement | Canonical coordinates, altitude above terrain, velocity, flight mode, gravity factor, swimming and underwater state |
| Celestial conditions | Local sunlight and eclipse readings, sun and giant visibility, and the celestial clock |
| Eclipse response | Current event phase, actual hub-light state, and whether a debug override is active |
| Local weather | Weather-system type and influence, cloud coverage, wind, cloud heights, precipitation potential, initialized rain/snow effect drive and nearby rain streaks, and the weather clock |
| Megastorm | Activation, local phase, distance and available arrival estimates, hazards, shelter and exposure |
| Underground | Availability, mode, depth, openness, and whether underground lighting is darkened |

The feed reports unavailable providers explicitly. Prototype dialogue fixtures do not establish live health, oxygen, inventory, or semantic object-identification readings. Questions needing those readings must remain unanswered until real providers exist.

Precipitation potential is distinct from current rain/snow effect drive. Local effects must be processing and have initialized finite readings before their zero values can establish negligible drive. Underground presentation suspension and nearby rain-streak activity are reported separately; these are not rain-gauge, wetness, injury or current hail measurements. Local readings do not establish conditions everywhere on the ring. Celestial and weather clocks retain their separate names. Inactive events and non-finite measurements must not turn into positive claims or invented countdowns.

## Prepared feed and authored knowledge

`PdaContextFeed` gathers readings on the main thread, keeps a periodically refreshed snapshot, and captures again when a question is submitted. Model inference happens only on request. Snapshots contain stable fact identifiers, sources, capture time and version, authored knowledge, and bounded recent changes.

Recent history records categorical changes, such as entering an underground mode or an event changing phase. It retains at most 12 changes from the last five observation minutes. It does not produce an event for every changing coordinate or timer. Initial capture establishes a baseline rather than inventing a backlog.

`resources/pda/debug_knowledge.json` holds explanations of supported game mechanics. Each entry has full `text` for inspection and an optional concise `model_text` for inference. Edit this catalog to improve the PDA's understanding of what the readings mean, then reload the game scene to load the edited catalog. Keep live values in the providers; catalog text should explain mechanics without claiming that a condition is currently active. Author explanations from established game behavior and intended lore.

Question preparation selects relevant topics and keeps the prompt bounded. Facts can supply concise `ModelText` while retaining complete `Text` and source provenance in the prepared snapshot and inspector. Current readings and negative facts take priority over background explanations and older events. A previous question can help interpret a short follow-up. The context inspector starts with the exact compact payload supplied to the model, followed by full readings and selection diagnostics. `PdaContextComposer.Compose(..., useCompactText: false)` retains verbose formatting for the opt-in A/B benchmark.

## Local inference

The game launches the standalone `prototypes/pda-voice` helper with its `qa-worker` command. The game assembly has no LLamaSharp dependency. The helper loads one local GGUF model and handles serialized JSONL requests over redirected standard streams. Diagnostics use stderr.

The default is `qwen3-0.6b` in system RAM on the CPU with four inference threads. VRAM mode requests CUDA offload of all model layers and attention work to GPU 0. The helper verifies a CUDA device and positive actual layer offload before reporting VRAM ready; a failed GPU load produces an error rather than silently switching to CPU. GPU mode still needs some host memory for the runtime and model metadata.

Model inference runs outside the Godot main thread; UI updates and game-state capture stay on the main thread. Loaded weights remain resident between questions and while the panel is closed. Each question still requires prompt processing and answer generation; resident weights remove the repeated model-loading step. The panel reports exact prompt tokens, first-text latency, total inference time, and material queue or loading delays after an answer. First-text latency includes context setup, prompt processing and the first decoded text. The pinned wrapper does not collect native prefill/decode counters; these appear as unavailable rather than zero milliseconds.

Automatic preload is skipped in headless runs. Set `RINGWORLD_PDA_DISABLE_PRELOAD=1` to suppress it for an automated windowed capture or measurement. An explicit question can still initialize the model.

The helper must be built after changes to its source. Its executable is expected under:

```text
prototypes/pda-voice/bin/qa-runtime/PdaVoicePrototype.exe
```

Model files remain under `prototypes/pda-voice/models/`. The debug feature does not download models or build the helper during gameplay. Missing files produce an actionable error in the panel. RAM mode can use the previous Release output when the new helper is absent; VRAM mode requires the new build. The separate output allows the new helper to be built while the previous Release helper is running.

Build the helper with the pinned SDK and project-local package cache:

```powershell
.\tools\build_pda_worker.ps1
# Once dependencies are restored:
.\tools\build_pda_worker.ps1 -NoRestore
```

If `qa-runtime` itself is already running, leave the debug world before rebuilding so Windows can release its executable. Start the world again after the build. Normal RAM/VRAM selection only needs **Reload model** and does not require rebuilding.

Requests and replies carry IDs. Closing and cancellation invalidate pending answers and request cooperative cancellation while retaining the model. The client drains the terminal reply before another inference starts. An unrecoverable timeout, model switch or scene teardown terminates only the helper owned by this panel. Answers are associated with the snapshot submitted; the world can change while inference is running.

The worker pins LLamaSharp and its CPU/CUDA 12 Windows backends to 0.27.0. VRAM mode uses a compatible CUDA 12 toolkit runtime, or the existing CUDA 12.8 DLLs under the prototype's Torch environment. Runtime DLL search paths are configured only in the child process. A CUDA-12-only selection policy chooses the bundled native backend directly, including when Windows has runtime DLLs without a toolkit `CUDA_PATH`. Normal dependency checks and actual device/layer verification remain enabled. The feature does not install drivers or change global environment variables. Backend installation and GPU-layer configuration follow the [pinned LLamaSharp quick start](https://github.com/SciSharp/LLamaSharp/blob/v0.27.0/docs/QuickStart.md).

## Acceptance

Run the focused checks through the normal test runner:

```powershell
.\tools\run_tests.ps1 -Tests pda_context_test,pda_debug_ui_test,megastorm_pda_test -Label pda_debug -Cache reuse -DryRun
.\tools\run_tests.ps1 -Tests pda_context_test,pda_debug_ui_test,megastorm_pda_test -Label pda_debug -Cache reuse
```

Context and UI checks are deterministic and headless. Actual model answers need a separate local inference check across supported topics, unknown readings, false premises and follow-ups. A working process protocol is not an answer-quality result. The small existing models were originally benchmarked for short announcement rewrites; free-form question quality and latency require their own evidence.

The opt-in real-model check exercises resident PID reuse, cancellation during prefill and generation, and explicit teardown/restart:

```powershell
.\tools\run_tests.ps1 -Tests pda_local_model_test -Label pda_debug -Cache reuse
```

It writes `.godot/pda_debug/resident_model_results.json` with contexts, answers, loading and response times, resident memory and lifecycle results. Its PASS marker certifies transport and lifecycle assertions. Review the generated answers separately. The panel labels model answers experimental because local probes have produced factual mistakes, including confusing an unavailable observation with an absent condition.

The opt-in latency tools compare verbose and compact contexts from identical synthetic prepared snapshots. Run CPU and GPU measurements serially; the GPU variant takes the shared queue:

```powershell
.\tools\run_tests.ps1 -Tests pda_latency_test -Label pda_latency -Cache reuse -Parallel 1
.\tools\run_tests.ps1 -Tests pda_latency_gpu_test -Label pda_latency -Cache reuse
```

Reports are `.godot/pda_latency/latency_ram.json` and `latency_vram.json`. They retain complete inputs and generated answers, actual backend, prompt-token counts, first-text/inference/wall timings and resident PID reuse. Uncollected native prefill/decode timings are null in the summary. A PASS verifies the transport and requested backend; speed ratios and answer correctness require review of the measurements. In-game frame-rate impact is a separate acceptance check.

### Local measurement, 2026-11-05

Qwen3 0.6B, four CPU threads, RTX A1000 6GB Laptop GPU. The comparison uses two identical underlying prepared snapshots, two repetitions per snapshot and format, one unmeasured priming request, and one resident helper per backend. There are four measured requests per format. Figures below are medians; complete-answer time includes client transport.

| Placement and context | Prompt tokens | First generated text | Complete answer |
|---|---:|---:|---:|
| System RAM, verbose | 1,304 | 6.16 s | 7.72 s |
| System RAM, compact | 826 | 3.71 s | 4.60 s |
| CUDA VRAM, verbose | 1,304 | 0.24 s | 0.60 s |
| CUDA VRAM, compact | 826 | 0.16 s | 0.40 s |

Compact context reduced model input tokens by about 37 percent in these fixtures. The ready cache and inspector retain full readings. CUDA readiness verified all 29 model layers offloaded to the RTX A1000, while RAM readiness verified zero GPU layers. Existing unrelated game processes were not stopped by the test runner; their workload was not a controlled benchmark route. These timings do not certify gameplay frame rate or every question's latency.

Generated-answer review still found incorrect causal explanations in the lighting scenario. Backend and latency PASS results do not establish factual accuracy. Native prefill/decode counters were unavailable in the pinned wrapper and were not used to infer the speedup; first-text and complete-answer wall times are measured directly.

For visual acceptance, inspect the panel at normal game resolution, verify typing cannot move or edit the world, and check cursor ownership when opening, closing and switching other debug UI. Keep evidence under `.godot/pda_debug/` and prune disposable output at task completion.
