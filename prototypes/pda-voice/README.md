# PDA voice prototype

Standalone console app (not part of the game build) that tests whether a small local LLM plus a local TTS can voice a Subnautica-style suit-PDA announcer: calm, concise, dry, slightly clinical. The LLM rewrites an authored line (keeping every fact), the TTS speaks it, and a C# DSP chain previews the suit-radio feel. Everything is CPU only so the game keeps the GPU.

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

## Results summary

Machine: 20 logical cores, CPU only. Full numbers and lines are in `output/*.md`.

LLM (3 samples x 20 events = 60 lines per row; temperature 0.3, GBNF single-line grammar, max 48 tokens, stateless executor so each line pays context creation plus a ~200-token prompt):

| Model | Threads | Tokens/s | Latency p50 / p95 ms | Peak RAM MB | Reject rate |
|---|---|---|---|---|---|
| qwen2-0.5b Q4_K_M | 8 / 4 | 48.5 / 46.6 | 1198 / 1375 and 1011 / 1173 | 602 | 50% |
| qwen2.5-0.5b Q4_K_M | 8 / 4 | 67.2 / 59.3 | 1261 / 1375 and 1037 / 1145 | 597 | 17% |
| qwen3-0.6b Q8_0 | 8 / 4 | 49.3 / 42.9 | 1430 / 1685 and 1235 / 1415 | 881 | 12% |

Model load is about 2.3 s each. "RAM after load" in the report is low because GGUF files are memory-mapped; use peak RAM. The headline is that Qwen3-0.6B and Qwen2.5-0.5B are usable with a validator and authored fallback, Qwen2-0.5B is not (drops numbers constantly). Latency is about 1.0-1.6 s per line, which is fine for a background thread but not for instant reactions. Quality caveat: at this size the "rewrite" is mostly the authored line with sentences joined by commas or semicolons; the model adds little voice, and sometimes leaks fact keys or the prompt example, which the validator catches.

TTS: Kokoro (CPU, 4 threads) real-time factor about 0.3, ~1.2 GB peak RAM (ONNX Runtime arena); Piper about 0.17 per call including process launch, ~170 MB. Kokoro sounds noticeably more natural; Piper is far lighter. Both are well below real time.

## Intended in-game pipeline

1. Game event (eclipse timer, depth milestone, vitals, discovery) is raised with its structured facts.
2. Announcer queue orders by priority, drops stale or duplicate events, and enforces cooldowns.
3. For non-critical events, an LLM rewrite runs on a background thread with the authored line as the immediate fallback. Output is validated (numbers and key terms kept, length, plain text); on reject or timeout (about 2 s) the authored line is used. Critical events (low oxygen, eclipse T-1) skip the LLM.
4. TTS renders the final line to PCM on a background thread (caching by line text is worthwhile for repeated authored lines).
5. Audio goes to a dedicated Godot bus with a radio filter (band-pass, light distortion, beeps) and the same text shows as a subtitle.

Open items before integrating: reuse a long-lived llama context with a KV-cached system prompt to cut latency, cap CPU threads (4 is nearly as fast as 8 here), decide between Kokoro and Piper on voice quality versus the 1.2 GB RAM cost, and settle voice licensing. Per the project convention, any gameplay or rendering settings added at integration time (announcer enable, volume, LLM rewrite toggle, radio filter amount) must be exposed in the grave-key tuning menu.
