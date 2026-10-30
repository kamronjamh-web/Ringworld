# PDA voice developer handoff — 2026-10-27

## State and accepted direction

Checkout: `C:\Projects\RingworldTechDemo`; branch `storm-revamp-wip`; base commit before the PDA work: `e2485b7fcd7ddefebcdcfa48ea93108e29207133`.
This handoff records the PDA changes developed from that base. Use `git log -1 -- docs/pda-voice-handoff-2026-10-27.md` to identify the handoff commit on `storm-revamp-wip`. Preserve unrelated weather, asset-import and tools changes in this checkout. Do not reset, stash or overwrite the working tree to install this handoff.

The user chose British female Emma as the initial anchor, then preferred the Qwen expressive concept. Delivery must be normal, fully voiced and clear: **no whispering or intimate breathy delivery**. The suit assistant should be practical and observant, with restrained warmth and a supportive companion manner. Keep facts first, useful guidance concise, and uncertainty honest. Avoid jokes, invented readings, omniscient ring AI, lore claims and filler reassurance.

The three audition styles are Neutral, Reassuring and Urgent. The current dialogue catalog contains 20 draft fixtures; only storm, emitter discovery and oxygen have been rendered in all three styles. These are review drafts, not final shipping dialogue. Numeric examples and hypothetical sensor/vitals/inventory/scan/transit statements do not establish implemented gameplay features.

Recommended direction: **prerender authored/critical dialogue; keep a resident serialized streaming worker for novel text**. This is a proposed architecture, not an adopted game default. No Godot announcer queue, worker bridge, streaming playback, subtitle integration or live-tuning controls have been implemented by this prototype work.

## Review the listening artifacts first

All links below are local files in the active checkout; their relative audio paths require the sibling output folders to stay together.

- [Current dialogue r1: three styles and effects](../prototypes/pda-voice/output/dialogue-r1/index.html).
- [Fast 1.7B VoiceDesign: accepted concept, two oxygen attempts](../prototypes/pda-voice/output/fast-voicedesign-smoke/index.html).
- [Earlier Qwen wording and Emma reference](../prototypes/pda-voice/output/expressive/index.html).
- [Emma wording/device comparison](../prototypes/pda-voice/output/emma-tuning/index.html).
- [Initial four female voices](../prototypes/pda-voice/output/audition/index.html).
- [Unoptimized smaller-model diagnostic](../prototypes/pda-voice/output/dynamic-clone-probe/index.html) and [fast smaller-model diagnostic](../prototypes/pda-voice/output/fast-smoke/index.html).

Native players pause one another and do not autoplay or use the network. Finished PCM/WAV players **do not recreate time to first audio or chunk arrival delays**. Compare full spoken wording, numbers, onset, chunk boundaries, voice identity and acting by listening; successful generation and plausible duration do not certify these.

## What exists

The standalone project is **`prototypes/pda-voice/PdaVoicePrototype.csproj`**. It is excluded from the Godot project build/import path. Original CPU LLM/TTS benches remain available alongside the newer GPU auditions. Generated models/output and runtime artifacts are ignored by Git.

| Source | Responsibility |
|---|---|
| `Program.cs`, `run.ps1` | C# commands through the repository's pinned .NET SDK. |
| `Common.cs` | Event loading, original text validation, WAV and metric helpers. |
| `LlmBench.cs`, `LlmReport.cs`, `TtsBench.cs` | Original CPU benchmarks; not the expressive streaming implementation. |
| `VoiceAudition.cs` | Female/Emma auditions; shared validation, gain matching, clips and reels. |
| `RadioDsp.cs` | Original radio, gentle light effect, and explicit PDA/radio profiles. Original behavior is preserved. |
| `ExpressiveAuditionReport.cs` | Raw manifest/hash/path validation, C# effects, local reports and r1 comparison route. |
| `expressive_audition.py`, `run-expressive.ps1` | Offline official Qwen generation, smoke/full/cache workflows and report dispatch. |
| `expressive-prompts.json` | Earlier three conversational texts, fixed persona/styles/seeds. |
| `dialogue-catalog-r1.json`, `expressive-dialogue-r1.json` | Twenty fixture drafts and the three r1 audio inputs; event metadata preserved. |
| `dynamic_clone_probe.py` | Fresh unoptimized 0.6B reference-conditioned diagnostic; importable level/DSP helpers. |
| `fast_streaming_probe.py` | Two-attempt native CUDA-graph streaming diagnostic; 0.6B default or allowlisted 1.7B `--voice-design`. |
| `events.json`, `tts_picks.json` | Original benchmark fixtures; not live event bindings. |

Effects are derived from each synthesized waveform: Clean, PDA speaker and Radio. Level matching uses one constant gain per clip toward -20 dBFS gated RMS, capped at -1 dBFS peak. It is approximate, not exact perceptual loudness matching. Reels use 0.7-second gaps. Settings and achieved measurements are in the JSON reports.

## Existing-machine commands

Run these from a PowerShell prompt in the active checkout. They use already prepared local environments/models; they do not provision a new machine.

```powershell
Set-Location 'C:\Projects\RingworldTechDemo'
Start-Process '.\prototypes\pda-voice\output\dialogue-r1\index.html'
Start-Process '.\prototypes\pda-voice\output\fast-voicedesign-smoke\index.html'

# Rebuild r1 effects/report from the completed raw manifest; no model inference:
.\prototypes\pda-voice\run-expressive.ps1 -DialogueR1 -ReportOnly

# Generate r1 using the official environment; matching successful raw clips are reused:
.\prototypes\pda-voice\run-expressive.ps1 -DialogueR1

# Two fresh streamed oxygen attempts using the isolated fast environment and 1.7B model:
.\artifacts\pda-expressive-fast\venv\Scripts\python.exe -u .\prototypes\pda-voice\fast_streaming_probe.py --voice-design
```

R1 writes only `output/dialogue-r1/`; default expressive generation writes `output/expressive/`. The fast flag writes only `output/fast-voicedesign-smoke/`; omitting it selects 0.6B and `output/fast-smoke/`. The probe is not a general-purpose text/path interface. Re-running fresh probes replaces only their fixed output label. Preserve baseline copies before experiments.

## Runtime and model transfer requirements

Test host: NVIDIA RTX A1000 6 GB Laptop GPU, 20 logical CPU cores, 16 GB system RAM. GPU tests used four CPU threads, bfloat16 and SDPA. The old C# wrapper expects the repository's .NET 10 SDK at `artifacts/engine-upgrade/dotnet/` and its established package/cache directories.

| Environment | Pins and location |
|---|---|
| Official Qwen | Python 3.12.16, uv 0.13.6; `artifacts/pda-expressive/venv/Scripts/python.exe`; `qwen-tts` 0.1.1, Transformers 4.57.3, Accelerate 1.12.0, Torch/Torchaudio 2.10.0+cu128. |
| Fast Qwen | Separate `artifacts/pda-expressive-fast/venv/Scripts/python.exe`; Python 3.12.16, `faster-qwen3-tts` 0.5.4, `qwen-tts-hf` 0.1.1.post1, Transformers 5.15.1, Hugging Face Hub 1.33.0, Torch/Torchaudio 2.10.0+cu128. |

**Do not combine these environments.** `qwen-tts` and `qwen-tts-hf` both supply the `qwen_tts` namespace. The fast probe rejects the original distribution in its environment. Both runtimes enforce offline generation and use their own artifact cache roots. No FlashAttention, Triton or compilation was required for the tested native Windows Torch/CUDA-graph path.

- Official source commit: `022e286b98fbec7e1e916cb940cdf532cd9f488e` from `QwenLM/Qwen3-TTS`.
- Fast source commit: `b35fdc7d71eb3e4d19e63f4b4a330a1aef31d682` from `andimarafioti/faster-qwen3-tts`.
- Existing-machine freezes: `artifacts/pda-expressive/installed-requirements.txt` and `artifacts/pda-expressive-fast/installed-requirements.txt`. Portable bundle copies: `handoff/runtime-freezes/original.txt` and `handoff/runtime-freezes/fast.txt`.

| Local checkpoint | Pinned model revision |
|---|---|
| `prototypes/pda-voice/models/qwen3-tts-1.7b-voice-design/` | `Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign`, `5ecdb67327fd37bb2e042aab12ff7391903235d3`. |
| `prototypes/pda-voice/models/qwen3-tts-0.6b-base/` | `Qwen/Qwen3-TTS-12Hz-0.6B-Base`, `5d83992436eae1d760afd27aff78a71d676296fc`. |

Each checkpoint needs its whole snapshot and completed `download_manifest.json` (`Model`, `Revision`, file paths/sizes/SHA256). The transfer contains those manifests, not weights. Source provenance is checked through installed package `direct_url.json`. Preserve the pinned source installation provenance when rebuilding environments.

**Setup gap:** `download-models.ps1` provisions the original CPU benchmark models only. It does **not** provision Qwen checkpoints or either Python environment. Recreate them separately using the pins, freezes and manifests. The [prototype README](../prototypes/pda-voice/README.md) records the successful official source build with setuptools 78.1.0, wheel 0.45.1, packaging 25.0 and a scoped `--no-build-isolation` install; Torch wheels came from the official CUDA 12.8 index. Virtual environments and absolute machine paths are not portable installation artifacts.

## Measurements and practical limits

These numbers were read from existing JSON results; this handoff did not run new inference.

| Existing result | Observation |
|---|---|
| Official 1.7B r1, Reassuring | Storm 11.21 s generation / 4.80 s audio; emitter 10.45 / 5.12; oxygen 7.69 / 3.92. Whole-line calls, no warm-up. |
| Unoptimized 0.6B reference conditioning | Approximately 2.0–2.15 compute seconds per audio second; reducing model size alone did not improve latency. |
| Fast 0.6B | Approximately 0.50 s first audio, 3.15 s total, 4.48 s output. |
| Fast 1.7B VoiceDesign, attempts 1 / 2 | First audio 0.5848 / 0.5953 s; total 3.6747 / 3.6889 s; 4.24 s output each; compute/audio 0.867 / 0.870. |
| Fast 1.7B memory | Peak 4.0794 GiB allocated / 4.1914 GiB reserved; process peak RAM approximately 4.39 GiB. |
| Fast 1.7B separate setup | Model load 14.7969 s; graph warm-up 1.4275 s; full-stream warm-up 3.5880 s. No reference preparation for VoiceDesign. |

Evidence: [official r1 raw manifest](../prototypes/pda-voice/output/dialogue-r1/raw_manifest.json), [small probe results](../prototypes/pda-voice/output/dynamic-clone-probe/results.json), [fast Base results](../prototypes/pda-voice/output/fast-smoke/results.json), [fast VoiceDesign results](../prototypes/pda-voice/output/fast-voicedesign-smoke/results.json).

Both fast VoiceDesign traces contained seven chunks, 111 observed prefill positions and 53 generated codec frames. Normal exhaustion plus counts below cache length 512 and token limit 256 excludes those limits, so EOS is inferred by pinned-source control flow. This is not speech-content verification. Trace continuity analysis allowed playback to start at first arrival in both attempts, without an extra initial buffer; actual audio-device/Godot playback was not tested.

The fast gate is **two repeats of one short alert**, with no concurrent game workload or LLM. Setup must be amortized by a resident serialized worker. Controls are not perfectly matched across backends: the fast streaming API does not expose the original subtalker keyword set and uses its graph sampling path. Repeated persona/seed does not lock identity or identical acting. The smaller Base model has no style instruction control; VoiceDesign does. Assess voice consistency and factual pronunciation separately from latency.

The fast path demonstrates TTS of provided new text. It does **not** implement game-state interpretation, conversational dialogue authoring or an LLM response pipeline. The original numeric/term fixture validator checks text patterns and required numbers; it is not a semantic correctness guarantee.

The 6 GB GPU has limited headroom once the game renders. Standalone fit and first-audio latency are not in-game performance acceptance. Longer text, multiple speakers, cache boundaries, cancellation, thermal variation and competing GPU work remain unqualified.

## Ordered next work and acceptance

1. **Runtime/audio owner — first implementation task:** build a resident serialized worker and small PCM-consumer harness, with a reproducible pinned bootstrap and explicit request/chunk IDs. Keep inference and immutable PCM production off the game main thread. Acceptance: multiple fresh texts play continuously; cancellation stops consumption; stale/late chunks are discarded; worker requests never overlap. Record cold/warm cost, p50/p95 first-audio, total time, chunk gaps, memory and visible failures over a varied corpus, without silently changing model/backend.
2. **Godot/performance owner — before event or LLM wiring:** inspect existing audio APIs and test the worker through a narrow Godot playback harness under matched gameplay routes/settings on the 6 GB GPU. Main-thread code owns Godot audio resources/playback and UI changes. Acceptance: measured GPU/frame-time contention, VRAM, startup and actual continuous playback meet agreed budgets, with cancellation and underflow recovery checked; no audible gaps/pops or main-thread inference stalls. Standalone TTFA does not substitute for this gate.
3. **Dialogue/audio owner:** review all 20 draft fixtures against actual gameplay capabilities; approve a shipping subset and critical fallback wording. Listen to every selected render for complete numbers, pronunciation, identity and intended acting. Acceptance: an explicit per-line approved/rejected list; unsupported fixtures remain disconnected. Dialogue review can proceed alongside the harness work.
4. **Queue owner:** design priority, staleness, deduplication, cooldown and preemption semantics before binding game events. Critical authored clips should bypass synthesis. Acceptance: deterministic queue cases show no stale warning or cancelled speech playing; the resident worker retains one active generation request.
5. **Audio/cache owner:** key prerenders by text, voice/model revision, prompt/style and effect settings; keep raw and processed provenance. Acceptance: repeat requests hit the intended cache, changed inputs miss it, broken entries fail safely, and critical clips play without waiting for model load or generation.
6. **Integration owner/reviewer:** after playback/contention gates pass, inspect actual event/audio/UI APIs before choosing hooks. Bind only approved supported events, add subtitle timing/lifecycle, and evaluate any dynamic text authoring separately from TTS. Acceptance: event-to-audio/subtitle cases retain facts, respect queue/cancellation policy and leave no stuck subtitles. Genuine announcer tunables belong in the existing grave-key menu/persistence conventions, not a separate settings UI.

No game hooks, queue policy or budget above is claimed to exist yet. Preserve original fixtures, defaults and reviewed artifacts while implementing in bounded steps. Reuse task output labels and run `tools/prune_test_output.ps1 -Apply` after testing under the repository's cache-preservation rules.

## Transfer bundle and safe continuation

Transfer archive: `artifacts/pda-voice-handoff-2026-10-27.zip`. It is an ignored transfer artifact: copy it separately from Git. It contains prototype source and all 247 current generated output artifacts (76,098,871 bytes, approximately 72.6 MiB uncompressed), plus this handoff, runtime freezes and model manifests. Runtime freezes are packaged as `handoff/runtime-freezes/original.txt` and `fast.txt`; model manifests as `handoff/model-manifests/qwen3-tts-1.7b-voice-design.json` and `qwen3-tts-0.6b-base.json`. Root `AGENTS.md` and `RingworldTechDemo.csproj` are included for context only. There are **no model weights, virtual environments or runtime tools**: the ZIP is not a self-contained runtime. Before using the bundle, verify the archive against its SHA256 sidecar, then verify extracted file hashes against `HANDOFF-MANIFEST.json`.

Extract and review the bundle outside the active checkout. Preserve relative output paths so listening comparisons work. Do not extract over this dirty working tree. The archive was assembled before publication of the PDA source commit; its source-state metadata describes packaging time. Obtain the handoff commit on `storm-revamp-wip` for source changes; Git does not carry the ignored recordings, runtime environments or transfer archive. The transfer is not a replacement for unrelated weather/assets/tools changes. Once reviewed, the receiving developer should explicitly choose the source changes to integrate and provision the pinned runtimes/checkpoints before running generation.
