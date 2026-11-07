# Local PDA model comparison

The comparison began with Qwen3 8B Q4_K_M, Granite 3.3 8B Instruct Q4_K_M, Llama 3.1 8B Instruct Q4_K_M, and Gemma 3 4B Instruct Q4_K_M. **Gemma4B and Qwen8B are now retained for testing only** and appear in the debug picker; Granite/Llama comparison weights were removed. Historical catalog/provenance entries do not imply that their weights remain installed. No final model was chosen and the legacy Qwen3 0.6B default is unchanged. The [2026-11-06 handoff](pda-handoff-2026-11-06.md) records current validation and the unresolved gameplay slowdown.

## Controlled method

The standalone helper links the game's actual `LocalPdaQaClient`, `PdaModelCatalog` and `PdaContextComposer`. It does not launch Godot, stream terrain or clone a saved world/profile. Each invocation selects one model/backend, verifies size and SHA-256 against tracked `resources/pda/model_artifacts.json`, starts one resident helper, records readiness separately, runs an unscored warmup, then evaluates the same ten scored cases in order. The ignored `.godot/pda_8b/candidates.json` was an earlier local acquisition record, not a fresh-checkout dependency. Startup is cold only with respect to the helper process: hash verification reads the model first and OS/disk caching is uncontrolled.

Fixtures are synthetic snapshots written against the real provider contracts in `resources/pda/model_comparison_cases.json`. They include realistic independent clocks, mandatory cross-domain states, unavailable telemetry, current precipitation-effect readings and knowledge from `resources/pda/debug_knowledge.json`. They are not captures of a running game. Snapshot metadata, context hashes, knowledge/catalog hashes and rendered native prompts are retained so reviewers can verify identical inputs across models.

Controlled defaults are four CPU threads, a 4096-token context, at most 160 output tokens, seed 42, temperature 0.2, top-k 20 and top-p 0.9. CUDA split/full entries request 16/99 layers explicitly. Historical RAM/CUDA entries for removed candidates remain provenance, not a recommendation to reacquire them. Gemma has no RAM-only entry in this original text matrix; its separate CPU vision smoke is available. The handshake must report CPU with zero offloaded layers, or CUDA with `min(requested layers, total model layers)` and a positive verified total. A request for 99 can therefore correctly yield 33/33 or 41/41. Silent CPU fallback fails verification.

The reported actual/total layer fraction is not an exact VRAM/RAM byte split: layer sizes, output weights, KV cache, compute buffers and allocations differ. Full GPU weight offload still requires some host RAM. These isolated comparisons run without the game, so a full-offload success is not evidence of adequate memory headroom or FPS while rendering. An allocation/OOM/backend failure remains a recorded failure; the harness does not retry on CPU or change quantization/native dependencies.

Target prompts are roughly 500–1000 tokens. Actual token counts are recorded per model because tokenizers differ; falling outside that target is reported, not misrepresented as semantic failure. The harness retains at most the existing complete-entry context budget and refuses fixture omission. JSON parameters explicitly record `benchmarkRequestDeadlineSeconds=75`, `totalBudgetSeconds=825`, and the effective `startupDeadlineSeconds` (the lesser of the catalog startup timeout and 240 seconds). These trial catalog entries allow 180 seconds per request, but the harness deliberately applies the stricter 75-second question deadline to fit the runner's 900-second cap. A timed-out answer is classified as a performance limit and remains unevaluated for semantic quality. An interrupted or failing run retains completed cases and partial evidence.

## Build and run

Provision the pinned engine/.NET environment first. Install only the retained test assets, verify them and build explicitly; neither evaluation runner builds or downloads automatically:

```powershell
.\tools\install_pda_test_models.ps1 -IncludeVisionProjector
.\tools\install_pda_test_models.ps1 -VerifyOnly -IncludeVisionProjector
.\tools\build_pda_worker.ps1 -Comparison
.\tools\run_tests.ps1 -Tests pda_model_catalog_test -Label pda_8b -Cache reuse
```

The first build restores dependencies; use `-NoRestore` only after a successful restore. The tracked model/projector manifests carry pinned revisions, byte sizes and hashes. CUDA additionally requires real CUDA12 runtime DLLs; an ignored local Torch fallback is not portable. See the [bootstrap](pda-handoff-2026-11-06.md#fresh-checkout-bootstrap).

Run the retained Qwen CPU comparison explicitly:

```powershell
.\tools\run_tests.ps1 -Tests pda_compare_qwen3_ram -NoBuild -Label pda_8b -Cache reuse -Parallel 1
```

Run retained-model split comparisons through the same runner, which owns the existing GPU queue:

```powershell
.\tools\run_tests.ps1 -Tests pda_compare_qwen3_vram_split,pda_compare_gemma_vram_split -NoBuild -Label pda_8b -Cache reuse -Parallel 1
```

Then run full GPU weight offload:

```powershell
.\tools\run_tests.ps1 -Tests pda_compare_qwen3_vram_full,pda_compare_gemma_vram_full -NoBuild -Label pda_8b -Cache reuse -Parallel 1
```

Gemma's key is `gemma-3-4b-q4`, with file `prototypes/pda-voice/models/_comparison/gemma-3-4b-it-Q4_K_M.gguf`. Its matched vision projector is pinned separately in `resources/pda/gemma_vision_projector.json`. Candidate metadata and the shared catalog must agree. Retention/debug selection supports continued testing, not promotion to the default.

Split entries estimate 300 seconds and full entries 90 seconds for queue planning; these are scheduling estimates, not measured performance promises. Both retain the 900-second runner limit. Every registered GPU entry passes `-GpuLayers 16` or `-GpuLayers 99` explicitly; the standalone runner's legacy default is not used by this matrix.

The individual standalone CPU command is:

```powershell
.\tools\run_pda_model_comparison.ps1 -ModelKey qwen3-8b-q4 -MemoryMode SystemRam -Threads 4 -Label pda_8b
```

A direct GPU invocation requires the caller to own the GPU queue first; the registered runner entries handle this automatically. Do not wrap `run_tests.ps1` in another GPU-queue acquisition.

## Evidence and interpretation

Each run writes `results.json` and `review.md` under `.godot/pda_8b/comparison/<label>/<model>-<configuration>-<timestamp>/`, where configuration is `ram`, `vram-gpu16` or `vram-gpu99`. JSON is checkpointed after startup, warmup and every scored case, then in `finally`. It records requested GPU layers, expected offload, actual/total layers and their count fraction alongside model provenance, explicit deadlines, new-helper readiness, per-question end-to-end wall time, first-output latency, prompt token count, worker timings, raw answer, cleaned answer, rendered prompt, execution outcomes, errors, supplied context and full source inspection. A rejected or malformed answer remains auditable instead of disappearing from the comparison.

`OutputPieces` counts decoded stream pieces. Nullable `OutputTokens` is a retokenization count of the raw answer, not a native generation-token counter. Native prefill/decode counters are unavailable in the pinned wrapper; zero denotes uncollected data and is summarized as unavailable/null, not instantaneous work. TTFT measures the first decoded output event; family-specific thought suppression and cleaning can affect what the user sees.

Independent polling records the owned helper's working set, private bytes and OS peak working set. Sampled maxima can miss short peaks. Optional `nvidia-smi` captures a baseline before helper startup and device-wide samples during the run; they include other applications and driver allocations and cannot be attributed exactly to this helper. Closing other GPU workloads improves comparability. These are standalone measurements; a chosen configuration still needs separate testing while the game is rendering.

Transport/backend PASS means that requests completed, the requested backend was verified, the same owned helper was reused, and its exit was confirmed after disposal. It does not mean the language model's explanations are correct. Every answer carries expected facts and a human rubric; there is no keyword-based semantic PASS or automatic winner.

Review all ten scored answers for unsupported readings, incorrect premises, local-versus-global scope, current-versus-recent state, source-specific effects, independent lighting causes and false action claims. Compare latency and memory after correctness review. A missing model, unsupported chat template, CUDA failure, timeout or rejected answer remains a recorded limitation, not a successful comparison.

After a final model choice, promote it through a separately reviewed change and validate live interaction. Current retention is exactly Gemma4B plus Qwen8B for further tests. The harness never deletes, downloads or promotes models. Raw reports and captures under `.godot` are ignored and absent from a fresh checkout; the [tracked handoff](pda-handoff-2026-11-06.md#validation-checkpoint) preserves compact verdicts, run IDs and limitations.

## Debug placement controls

The F10 debug panel exposes a GPU-layer request alongside its CPU/CUDA selector. Choose CUDA and 16 layers for the tested split, or 99 to request all available model layers, then press **Reload model**. Changes remain pending until Reload. CPU mode always requests zero GPU layers; the layer selector retains its previous value for a later CUDA selection. Status distinguishes the requested count from the actual offloaded/total layer count. Full offload still uses host RAM for other allocations.

The shared `DebugChoices` picker contains the original three small models followed by retained Qwen8B and Gemma4B. Deleted Granite/Llama trials do not appear. The canonical runtime supports the retained choices; a separate comparison helper is used only by explicitly configured evaluation. The Qwen3 0.6B default remains unchanged.

## Later focused/vision work and open performance issue

The [memory/vision guide](pda-debug-memory-and-vision.md) describes the later Focused-plus-policy checks, bounded memory retrieval and optional Gemma images. Each retained model made seven model calls and three policy-only replies in those ten-case checks. Report model and policy times separately. Comparing them with an older raw baseline does not isolate context selection as the only cause of improvement.

The user confirmed that the slow live case had **Gemma4B, CUDA selected and Attach current view enabled**. It remains unresolved. Do not diagnose it using the separate 47–50 s CPU image baseline or infer live-game headroom from a 3.15 GiB isolated CUDA peak. Next, compare image off/on in the same world with the same question and collect actual language/projector placement, per-question timing, RAM commitment, GPU headroom and game frame times as specified in the [handoff](pda-handoff-2026-11-06.md#unresolved-live-cuda--image-slowdown-next-work).
