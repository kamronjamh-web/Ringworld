# PDA voice prototype

Standalone prototype (not part of the game build) for a British female suit-PDA assistant: practical, observant and warmly supportive, with restrained expression and clear, fully voiced delivery. It includes the original CPU LLM/TTS benchmarks, Emma comparisons, GPU Qwen expressive auditions, and isolated streaming diagnostics. The current preferred direction is the Qwen expressive concept, with authored prerenders and a proposed resident streaming worker for novel text; Godot integration is still planned.

[Developer handoff — 2026-10-27](../../docs/pda-voice-handoff-2026-10-27.md) records the accepted direction, source/runtime map, reviewed listening pages, measured results, transfer requirements and next acceptance gates.

Isolation: `RingworldTechDemo.csproj` removes `prototypes/**` from Compile/None/Content/EmbeddedResource, and `prototypes/.gdignore` hides it from Godot. `models/` and `output/` are git-ignored. Do not commit model binaries.

## Setup and run

```powershell
prototypes\pda-voice\download-models.ps1      # ~2.0 GB into models/, skips files already present
prototypes\pda-voice\run.ps1 llm [--models qwen2-0.5b,qwen2.5-0.5b,qwen3-0.6b] [--threads 8,4]
prototypes\pda-voice\run.ps1 tts
prototypes\pda-voice\run.ps1 all
```

`run.ps1` uses the repo's pinned .NET 10 SDK (`artifacts/engine-upgrade/dotnet`) and the same `NUGET_PACKAGES` / `DOTNET_CLI_HOME` locations as `tools/use_godot_48.ps1`. Each model/thread count (and each TTS engine) runs in its own child process so RAM numbers are clean.

Inputs: `events.json` (20 PDA events: id, category, priority, authored fallback, structured facts, required terms) and `tts_picks.json` (the five LLM lines chosen for TTS, picked by hand from the results).

## Female voice audition

```powershell
prototypes\pda-voice\run.ps1 audition
Start-Process prototypes\pda-voice\output\audition\index.html
```

This separate listening comparison uses the existing local `models/kokoro.onnx` and packaged voices: Sarah (`af_sarah`, American), Emma (`bf_emma`, British), Kore (`af_kore`, American), and Heart (`af_heart`, American). The target is a cool, clinical female voice without an intimate whisper; listening determines whether a candidate fits. No LLM, downloads, game integration, or changes to the benchmark defaults are involved. Missing models/voices or invalid synthesis output fail the command with a nonzero exit code.

All four voices read exactly the authored fallbacks for `storm_incoming`, `emitter_discovered`, and `low_oxygen`, in that order, at normal speed with four CPU threads. The local HTML report has native audio controls for clean/light reels and each line, without autoplay or network calls. Reels concatenate the same three clips with 0.7 seconds of silence between them. The command reuses only `output/audition/`; the older `output/tts/` clips and benchmark reports are untouched.

Each speech clip gets one fixed gain toward -20 dBFS gated RMS, capped at -1 dBFS peak, without a dynamic compressor. RMS uses 20 ms blocks above -45 dBFS; the peak cap and gating can prevent exact matching. Clean and light variants are each normalized with the same rule, and achieved levels are reported rather than assumed to be perceptually identical. Light PDA mixes 75% dry speech with 25% second-order 120–7500 Hz filtering, without saturation, cues, spatial processing, pitch changes or speed changes. It preserves the candidate voice's delivery, including any inherent breathiness.

`output/audition/manifest.json` records UTC generation time, texts, voice IDs and accents, settings, relative WAV paths, durations, synthesis timings, gains, and RMS/gated RMS/peak measurements before PCM16 quantization. The first inference is not warmed up, so these timings are descriptive rather than a controlled benchmark. Partial WAVs can remain after a failed run; the report and manifest are published only after every voice completes.

## Emma wording and device tuning

```powershell
prototypes\pda-voice\run.ps1 audition-emma
Start-Process prototypes\pda-voice\output\emma-tuning\index.html
```

This separate audition keeps Emma (`bf_emma`, British female) and compares the original three authored events against hand-authored, warmer conversational wording. Both use normal speed (1.0) and unchanged package defaults for segmentation and pauses. Kokoro has no arbitrary emotion control: this is a script comparison, not an emotion toggle or a guarantee of warmer delivery. There is no voice mixing, pitch change, whisper effect or LLM. Original texts and structured event facts accompany each script's text in the manifest so factual preservation can be inspected.

Six speech renders produce three effects each from the same waveform: Clean, PDA speaker (180–4800 Hz, 85% wet / 15% dry, tanh drive 1.35), and Radio (300–3400 Hz, 100% wet, tanh drive 1.8). Each filtered effect uses two cascaded second-order biquads per edge with Q 0.7071, then `wet * tanh(filteredSample * drive) + (1 - wet) * originalSample`. Saturation acts at the filtered signal's amplitude, without the older radio chain's peak-boost stage. No beeps, noise or spatial effects are added. These presets aim for stronger coloration than the earlier light option; audibility and suitability require listening.

The same fixed-gain normalization rule targets approximately -20 dBFS gated speech RMS with a -1 dBFS peak cap. Each script has three effect reels with 0.7-second gaps. Only `output/emma-tuning/` is reused: 18 individual clips, six reels, a local HTML listening page, and `manifest.json`. Previous auditions and benchmarks remain untouched. Errors return nonzero; partial WAVs may remain, but reports are published only after all six speech renders and derived effects complete.

The manifest includes exact effect/script IDs and settings, relative paths, durations, gains and levels, synthesis time per line, effect-processing time per clip, model load time, and current-process peak working set after load and after rendering. The clean effect's processing time measures only selecting its unchanged waveform. DSP timings exclude normalization, measurements and file writes. CPU inference uses four threads, without warm-up; the first render includes first-inference overhead. These measurements describe the standalone run, not in-game frame cost or performance acceptance. No second model or GPU provider is used.

## Expressive British female concept audition

```powershell
prototypes\pda-voice\run-expressive.ps1 -Smoke           # Oxygen line, three styles; raw audio/progress only
prototypes\pda-voice\run-expressive.ps1                  # Reuse matching smoke clips, complete nine lines, build report
prototypes\pda-voice\run-expressive.ps1 -ReportOnly      # Rebuild processing/report from completed raw manifest
Start-Process prototypes\pda-voice\output\expressive\index.html
# Explicit slower CPU alternative; never selected automatically:
# prototypes\pda-voice\run-expressive.ps1 -Device cpu -Smoke
```

This isolated audition uses Qwen voice design for Neutral, Reassuring and Urgent direction on the same three approved conversational texts. The shared persona requests a composed adult British English female voice with warmth, normal volume and restrained expression. This is an expressive voice concept beside Emma, not an exact clone. Repeating a persona and per-event seed does not preserve an exact speaker identity. The local page includes the existing Emma conversational reels as a reference; those baseline files must already exist and are never modified.

The runtime is prepared separately, without installation or download logic in these commands. Pins: Python 3.12.16 and uv 0.13.6 under `artifacts/pda-expressive/`, isolated Python at `artifacts/pda-expressive/venv/Scripts/python.exe`; official Qwen3-TTS code archive commit `022e286b98fbec7e1e916cb940cdf532cd9f488e` (`qwen-tts` 0.1.1), Transformers 4.57.3, Accelerate 1.12.0, Torch/Torchaudio 2.10.0 with CUDA 12.8. The model is `Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign` revision `5ecdb67327fd37bb2e042aab12ff7391903235d3`, downloaded in full to `models/qwen3-tts-1.7b-voice-design/`. Generation requires its completed `download_manifest.json` with matching PascalCase `Model` and `Revision` fields. Installed `qwen-tts` provenance (`direct_url.json`) must identify the pinned official archive commit. Package versions are checked/recorded at runtime; the prepared environment's freeze is kept at `artifacts/pda-expressive/installed-requirements.txt`.

The successful environment setup installed Torch/Torchaudio `2.10.0+cu128` from the official `https://download.pytorch.org/whl/cu128` index. uv's isolated build of the Qwen source stalled with setuptools 84.2.1. In the existing isolated audition environment, the working build tools were setuptools 78.1.0 (already present from the Torch installation), wheel 0.45.1 and packaging 25.0. With those tools available in that environment, the successful source-install command was:

```powershell
# Run from the repository root, using the locally installed uv executable.
uv pip install --python artifacts/pda-expressive/venv/Scripts/python.exe --no-build-isolation 'https://github.com/QwenLM/Qwen3-TTS/archive/022e286b98fbec7e1e916cb940cdf532cd9f488e.zip' psutil==7.2.2
```

The `--no-build-isolation` workaround applies only to this prepared audition environment with the build tools above. It is not a global pip/uv setting. The complete installed package versions are recorded in `artifacts/pda-expressive/installed-requirements.txt`.

Generation loads the model once when uncached work remains. Default CUDA uses bfloat16 and SDPA; explicit CPU mode uses float32 and eager attention. Neither changes the speech tokenizer's attention configuration. Four CPU threads are used. No FlashAttention installation or automatic CPU fallback is attempted; VRAM fit on a 6 GB GPU remains a runtime question. Missing dependencies/model files, an out-of-memory error or invalid audio fail with a nonzero exit code. All generation is offline, with Hugging Face/Torch caches directed under `artifacts/pda-expressive/`. No paid service or game integration is involved.

Generation uses `non_streaming_mode=true`, `max_new_tokens=256`, `do_sample=true`, `top_k=50`, `top_p=1.0`, `temperature=0.9`, `repetition_penalty=1.05`, and matching subtalker sampling (`subtalker_dosample=true`, `subtalker_top_k=50`, `subtalker_top_p=1.0`, `subtalker_temperature=0.9`). Seeds are fixed per event and reused between styles. Prompts, exact texts and structured event facts are in `expressive-prompts.json`; facts are checked against `events.json`. The model may still omit, mispronounce or truncate a fact, so listen to every line before choosing a result.

Only `output/expressive/` is reused. Python writes nine raw mono FLOAT WAVs without clipping, DSP or normalization, an atomic `progress.json` after each successful clip, and `raw_manifest.json` only when all nine complete. A smoke run produces three oxygen clips and progress, without a full-success manifest or listening page. Full runs reuse matching successful raw clips after checking the generation cache key and recorded SHA256; keys include text, prompt, seed, model/code revisions, device, dtype, attention, package versions and generation settings. Changed inputs or damaged audio are regenerated. Interrupted runs can leave partial raw clips. Starting generation removes the previous expressive success reports so they cannot describe partial replacement audio.

The C# `audition-expressive-report` command requires nine unique expected style/event pairs, current prompts/facts, confined expected raw paths and matching WAV hashes/sample metadata. It reuses the exact Emma Clean/PDA speaker/Radio processing and fixed-gain normalization, producing 27 processed line clips and nine reels, with 0.7-second reel gaps. `manifest.json` includes the original Python metadata plus processed paths, levels, gains and DSP timings. The HTML report offers three style sections, collapsed individual transcripts and native players that pause one another, without autoplay or network calls. Processing/report generation does not require the Python environment or model snapshot.

Raw metadata records package versions, prompts/texts/facts, seeds, generation flags, model/code revisions and provenance, device/dtype/GPU, model-load/synthesis times, peak CUDA allocated/reserved bytes and process peak RAM (Windows `peak_wset`, otherwise RSS). Timings synchronize CUDA and include first-inference overhead because no warm-up is run. Reused clip timings belong to the original render; current-run memory/load figures describe only work done in that run. These measurements do not establish in-game frame cost or performance acceptance. Final reports publish only after all raw validation and processing succeed; partial processed WAVs may remain after failure.

## Dialogue r1 draft

```powershell
prototypes\pda-voice\run-expressive.ps1 -DialogueR1
prototypes\pda-voice\run-expressive.ps1 -DialogueR1 -ReportOnly
Start-Process prototypes\pda-voice\output\dialogue-r1\index.html
```

`-DialogueR1` selects only the fixed `expressive-dialogue-r1.json` input and `output/dialogue-r1/` destination. It also works with `-Smoke` or explicit `-Device cpu`. The default expressive command still uses its original input/output. No existing audition audio or reports are replaced by the r1 command.

`dialogue-catalog-r1.json` contains 20 author-directed drafts for a practical, observant suit assistant with restrained warmth and supportive companionship. It preserves the existing event IDs, categories, priorities and structured facts. These are benchmark fixtures, not final shipping dialogue or new gameplay features. Notes identify hypothetical sensor/vitals/inventory/scan/transit readings, sample timings and claims removed from older wording. The guide puts facts first, keeps critical warnings brief, states uncertainty honestly and avoids invented readings, route promises, builders/lore, jokes and filler reassurance.

Only the storm, emitter and oxygen drafts are synthesized, each in the same three existing delivery styles. The persona, style directions, per-event seeds, model/runtime flags and effects remain unchanged. R1 validation checks catalog metadata against `events.json`, its three texts against the catalog, and persona/styles/seeds/facts against the original expressive configuration. New raw and final manifests identify `DialogueRevision` and `ConfigFile`. Existing full baseline manifests remain supported without those fields.

The r1 page links the previous expressive page and includes previous-wording reels inside each style's collapsed comparison section, using matching effects. All previous reels and the comparison page must already exist. Emma remains an earlier-wording reference. Words are identical across r1's delivery styles; the previous-wording clips intentionally use the earlier script. A repeated prompt/seed still does not guarantee identical speaker identity, so this comparison also needs listening judgment. The other 17 catalog entries remain data-only drafts.

## Smaller dynamic speech diagnostic

```powershell
# From the repository root, using the existing isolated runtime:
artifacts\pda-expressive\venv\Scripts\python.exe -u prototypes\pda-voice\dynamic_clone_probe.py
Start-Process prototypes\pda-voice\output\dynamic-clone-probe\index.html
```

This separate probe requires the prepared local `Qwen/Qwen3-TTS-12Hz-0.6B-Base` snapshot at `models/qwen3-tts-0.6b-base/`, revision `5d83992436eae1d760afd27aff78a71d676296fc`, with its completed download manifest. It uses the same pinned runtime, CUDA bfloat16/SDPA, four CPU threads and sampling flags as the expressive audition. There are no installs, downloads, automatic fallback or changes to accepted settings in this command.

The accepted expressive audition's raw reassuring oxygen clip supplies the reference audio and exact transcript, checked against its recorded SHA256. A reusable full voice-clone prompt is prepared once. The smaller Base model has no instruction/style control: this is a reference-conditioned candidate, not an exact clone or identical acting. After one separate untimed warm-up, three dialogue-r1 lines are synthesized fresh without cache reuse. Model loading, reference preparation, synchronized wall time, autoregressive codec generation and speech-tokenizer decode are recorded separately. Warm-up and lightweight timing instrumentation differ from the earlier audition, so standalone results need careful comparison rather than an in-game speed claim.

Only `output/dynamic-clone-probe/` is reused: three raw FLOAT WAVs, six normalized Clean/PDA speaker clips, two reels, `results.json`, and a local comparison page. Existing accepted/revised clips are linked without modification. The Python PDA filter follows the C# 180–4800 Hz, 85% wet, drive 1.35 profile using float32 RBJ biquads, with the same fixed-gain target and peak cap; numerical compatibility requires validation and is not claimed bit-exact. `process_pda`, `normalize`, `measure` and `write_pcm16` can be imported for processor checks without loading the model. The report publishes only after all three lines complete with valid finite, nonsilent audio.

## Native CUDA-graph streaming diagnostic

```powershell
artifacts\pda-expressive-fast\venv\Scripts\python.exe -u prototypes\pda-voice\fast_streaming_probe.py
Start-Process prototypes\pda-voice\output\fast-smoke\index.html
# Test the existing 1.7B expressive checkpoint, without reference cloning:
artifacts\pda-expressive-fast\venv\Scripts\python.exe -u prototypes\pda-voice\fast_streaming_probe.py --voice-design
Start-Process prototypes\pda-voice\output\fast-voicedesign-smoke\index.html
```

This fixed two-attempt oxygen probe uses a separate environment: `faster-qwen3-tts` 0.5.4, source commit `b35fdc7d71eb3e4d19e63f4b4a330a1aef31d682`, `qwen-tts-hf` 0.1.1.post1, Transformers 5.15.1, Hugging Face Hub >=1.5,<2 and Torch/Torchaudio 2.10.0+cu128. The original `qwen-tts` distribution must not share this environment. It reuses the local smaller Base checkpoint and hash-checked accepted reference without downloading models or changing earlier runtimes/settings. Caches stay under `artifacts/pda-expressive-fast/`.

The native Windows Torch backend uses bfloat16/SDPA with four CPU threads and CUDA graphs, without Triton, compilation or FlashAttention. Model load, reference preparation, graph capture (`prefill_len=100`, three warm-ups per graph), and a complete oxygen-stream warm-up are separate setup costs. Two fresh measured streams reset seed 4103 and use eight-codec-frame chunks. Outputs are confined to `output/fast-smoke/`: two raw FLOAT WAVs, two level-matched clean PCM16 clips, JSON measurements and a local listening comparison against both earlier same-word candidates.

The report records wall time to the first nonempty copied audio chunk, total synchronized wall time, each chunk's arrival/gap/duration/frame count, internal timing data and stage memory peaks. `ComputeRealTimeFactor` means compute wall seconds divided by output audio seconds. Full-text conditioning (`non_streaming_mode=true`) still produces streaming audio. Chunks are copied promptly into memory without playback or per-chunk disk writes. A resident serialized worker would be needed to keep preparation costs out of each request. Static-cache length 512 and plausible audio duration do not prove complete spoken text; inspect the whole alert and numbers. Identity and acting can differ from the reference. No automatic fallback or game-performance approval is implied.

The local 0.6B CUDA-graph probe measured approximately 0.50 seconds to first audio and 3.15 seconds total for 4.48 seconds of output, after preparation and warm-up. This is one short oxygen alert on the test GPU, not a general latency or quality guarantee. The accepted model and game defaults are unchanged.

The allowlisted `--voice-design` option selects the existing 1.7B VoiceDesign model and its pinned revision from `expressive_audition.py`, with a separate fixed destination, `output/fast-voicedesign-smoke/`. It uses exactly the r1 persona plus reassuring direction, the oxygen text and seed 4103, and two fresh streaming attempts. It does not create or consume a voice-clone reference. The same sampling/chunk settings apply, omitting the unsupported VoiceDesign `parity_mode` argument. Model load, graph warm-up and full-stream warm-up are recorded separately; no reference-prompt cost is included for this mode. The default command continues to use 0.6B and `output/fast-smoke/`.

Per-request termination evidence records the actual cache-prefill length observed from `talker_graph.prefill_kv`, the sum of generated `chunk_steps`, and normal generator exhaustion. Exactly one observed prefill, fewer than 256 generated codec frames, and prefill plus frames below 512 exclude the two configured generation limits in the pinned source; EOS is then inferred by control flow. Missing or limit-adjacent evidence is reported as inconclusive. This evidence does not establish spoken completeness or fidelity, and the package's `is_final` flag only identifies a partial final chunk.

The local 1.7B VoiceDesign trial produced 4.24 seconds of oxygen-alert audio in each of two attempts. First audio arrived at 0.5848 / 0.5953 seconds; total generation took 3.6747 / 3.6889 seconds. Peak GPU memory was 4.0794 GiB allocated and 4.1914 GiB reserved. Separate startup costs were 14.797 seconds for model loading, 1.427 seconds for graph warm-up and 3.588 seconds for full-stream warm-up; these are excluded from the measured request times. Both chunk traces could begin continuous playback at the first arrival without an additional initial buffer under the recorded arrival schedule. This is trace analysis, not an in-game audio playback test.

Both attempts observed 111 prefill positions and 53 generated codec frames, below the 512-position cache and 256-frame generation limits, so EOS was inferred from the pinned source's control flow. That excludes those configured limits for these attempts; it does not verify spoken fidelity. These results cover one alert and two standalone attempts, with no game workload or LLM latency. They do not adopt the model as a default.

The proposed integration is a hybrid: prerender authored dialogue, then use a resident serialized streaming worker for novel text. This gives authored warnings immediate playback while keeping dynamic speech available. GPU contention must be benchmarked with the game on the 6 GB GPU before treating the standalone latency or memory fit as acceptable in play.

## Outputs (all under `output/`)

- `llm_results.md` / `.json`: metrics table, reject reasons, every sampled line next to the authored line.
- `tts_results.md` / `.json`: metrics and the list of every WAV.
- `tts/<voice>/<event>_<authored|llm>.wav` plain, and `..._radio.wav` with band-pass 300-3400 Hz (two cascaded biquads per edge), tanh saturation, a 1250 Hz beep before and an 880 Hz beep after. Open them in any player (for example `start output\tts\piper-lessac`). Voices: `kokoro-af_nicole`, `kokoro-bm_george`, `piper-lessac`.

## Licenses

| Item | License | Notes |
|---|---|---|
| Qwen2-0.5B-Instruct (GGUF Q4_K_M, 398 MB) | Apache-2.0 | HF `Qwen/Qwen2-0.5B-Instruct-GGUF` |
| Qwen2.5-0.5B-Instruct (GGUF Q4_K_M, 491 MB) | Apache-2.0 | HF `Qwen/Qwen2.5-0.5B-Instruct-GGUF` |
| Qwen3-0.6B (GGUF Q8_0, 639 MB, only official quant) | Apache-2.0 | HF `Qwen/Qwen3-0.6B-GGUF` |
| LLamaSharp 0.27.0, LLamaSharp.Backend.Cpu 0.27.0 | MIT | wraps llama.cpp (MIT) |
| Kokoro-82M v1.0 ONNX (fp32, 326 MB) | Apache-2.0 | weights from hexgrad/Kokoro-82M, ONNX hosted in Lyrcaxis/KokoroSharpBinaries (MIT repo) |
| KokoroSharp.CPU 0.8.4 | MIT | brings MisakiSharp 2.2.0 (Apache-2.0, managed G2P), NAudio (MIT), NumSharp (Apache-2.0), ONNX Runtime (MIT); voices (`voices/*.npy`) ship in the package |
| Piper `2023.11.14-2` Windows binary (22 MB) | MIT (rhasspy/piper) | **That repo is archived; development moved in 2025 to OHF-Voice/piper1-gpl, which is GPL-3.0.** This prototype uses the old MIT binary. It bundles espeak-ng (GPL-3.0), so shipping it in a game needs a license review; the newer Piper is GPL-3.0 outright. |
| Piper voice `en_US-lessac-medium` (63 MB) | Blizzard 2013 / Lessac Technologies research licence | Prototype use only. Do not ship without clearing the licence. Other piper-voices have their own terms (for example `ryan` is CC BY-NC-SA 4.0, so non-commercial). |

For shipping, Kokoro (Apache-2.0 model, MIT wrapper) is the cleanest path.

## Original CPU benchmark results

Machine: 20 logical cores, CPU only. Full numbers and lines are in `output/*.md`.

LLM (3 samples x 20 events = 60 lines per row; temperature 0.3, GBNF single-line grammar, max 48 tokens, stateless executor so each line pays context creation plus a ~200-token prompt):

| Model | Threads | Tokens/s | Latency p50 / p95 ms | Peak RAM MB | Reject rate |
|---|---|---|---|---|---|
| qwen2-0.5b Q4_K_M | 8 / 4 | 48.5 / 46.6 | 1198 / 1375 and 1011 / 1173 | 602 | 50% |
| qwen2.5-0.5b Q4_K_M | 8 / 4 | 67.2 / 59.3 | 1261 / 1375 and 1037 / 1145 | 597 | 17% |
| qwen3-0.6b Q8_0 | 8 / 4 | 49.3 / 42.9 | 1430 / 1685 and 1235 / 1415 | 881 | 12% |

Model load is about 2.3 s each. "RAM after load" in the report is low because GGUF files are memory-mapped; use peak RAM. The headline is that Qwen3-0.6B and Qwen2.5-0.5B are usable with a validator and authored fallback, Qwen2-0.5B is not (drops numbers constantly). Latency is about 1.0-1.6 s per line, which is fine for a background thread but not for instant reactions. Quality caveat: at this size the "rewrite" is mostly the authored line with sentences joined by commas or semicolons; the model adds little voice, and sometimes leaks fact keys or the prompt example, which the validator catches.

TTS: Kokoro (CPU, 4 threads) real-time factor about 0.3, ~1.2 GB peak RAM (ONNX Runtime arena); Piper about 0.17 per call including process launch, ~170 MB. Kokoro sounds noticeably more natural; Piper is far lighter. Both are well below real time.

## Original CPU pipeline proposal

1. Game event (eclipse timer, depth milestone, vitals, discovery) is raised with its structured facts.
2. Announcer queue orders by priority, drops stale or duplicate events, and enforces cooldowns.
3. For non-critical events, an LLM rewrite runs on a background thread with the authored line as the immediate fallback. Output is validated (numbers and key terms kept, length, plain text); on reject or timeout (about 2 s) the authored line is used. Critical events (low oxygen, eclipse T-1) skip the LLM.
4. TTS renders the final line to PCM on a background thread (caching by line text is worthwhile for repeated authored lines).
5. Audio goes to a dedicated Godot bus with a radio filter (band-pass, light distortion, beeps) and the same text shows as a subtitle.

Open items for the original CPU proposal: reuse a long-lived llama context with a KV-cached system prompt to cut latency, cap CPU threads (4 is nearly as fast as 8 here), decide between Kokoro and Piper on voice quality versus the 1.2 GB RAM cost, and settle voice licensing. The Qwen expressive concept and hybrid streaming proposal above are separate options now available for review. Per the project convention, any gameplay or rendering settings added at integration time (announcer enable, volume, LLM rewrite toggle, radio filter amount) must be exposed in the grave-key tuning menu.
