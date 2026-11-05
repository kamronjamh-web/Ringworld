# Local audio studio

Standalone local sound and music generation for Ringworld. **Stable Audio Open 1.0** creates sound effects, textures, and ambience; **ACE-Step 1.5 Turbo** creates instrumental music. The browser and API use `http://127.0.0.1:7861`. Generation requires NVIDIA CUDA; a missing CUDA runtime fails the job rather than selecting CPU inference.

This prototype is isolated from Godot by `prototypes/.gdignore` and the project's existing `prototypes/**` build exclusion. It never copies generated audio into `resources/audio`. Audition and select takes before importing them into the game.

## Setup and start

Run these commands from the repository root in PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\prototypes\audio-studio\setup.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\prototypes\audio-studio\run.ps1 -OpenBrowser
```

These commands bypass script restrictions for that process only, without changing the machine or user execution policy. The scripts resolve their paths independently of the current directory. Setup prepares an isolated Python runtime, pinned ACE-Step source/dependencies, and model weights under `artifacts/audio-studio/`. Downloads can be large. The standard official ACE bundle includes the 1.7B language-model files (about 3.75 GB extra) because upstream model validation requires them, although instrumental generation leaves that language model uninitialized. `setup.ps1 -Engine ace-step` or `-Engine stable-audio-open` selects one model; `-SkipModels` prepares only the runtime. One unavailable model does not disable the other.

Stable Audio Open is a gated Hugging Face model. If its download requires access:

1. Sign in to Hugging Face and complete the access/license form on [stabilityai/stable-audio-open-1.0](https://huggingface.co/stabilityai/stable-audio-open-1.0).
2. Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\prototypes\audio-studio\login.ps1` and enter a Hugging Face token with permission to read that model. The interactive prompt hides the token.
3. Rerun `powershell -NoProfile -ExecutionPolicy Bypass -File .\prototypes\audio-studio\setup.ps1 -Engine stable-audio-open`.

Stop the managed server with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\prototypes\audio-studio\stop.ps1
```

Managed stopping refuses while jobs are active or queued, allowing the worker to finish and release the shared GPU. An atomic stop-preparation request prevents new jobs from arriving between the idle check and shutdown.

## Creating and refining sounds

Choose **Sound effects** for Stable Audio Open or **Music** for ACE-Step. Describe the source, character, environment, and desired texture. Prompt starters cover mechanical doors, reactors, habitat ambience, and instrumental exploration/tension music. Use the audio controls to audition each variation, then **Reuse settings** to refine its prompt or seed. Keep a seed fixed for comparisons; variations use consecutive seeds modulo 2³². Matching seeds do not guarantee identical samples across different GPUs or dependency versions.

Each generation has a permanent visible ID such as **GEN-0012**, and each take has its own ID such as **GEN-0012-02**. Use these IDs when choosing sounds: “Use GEN-0012-02 for the shield under stress.” The **Copy ID** buttons copy the generation or take ID; if clipboard access is unavailable, the visible ID is selected for manual copying. IDs remain stable when cards move or the server restarts. Existing jobs receive IDs in creation order on first startup. UUIDs continue to identify API routes and output folders.

The sequence is reserved atomically in `artifacts/audio-studio/generation-sequence.json` before publishing a new job. Deleted jobs and interrupted reservations leave gaps; numbers are not reused. Startup rejects malformed or duplicate IDs and malformed counters rather than renumbering existing jobs. Migration adds IDs only to job metadata; audio, worker results, provenance, and historical asset-manifest source hashes remain untouched.

Stable Audio Open accepts 0.5–47 seconds, defaults to 5 seconds and 50 diffusion steps, and exposes negative prompt and guidance. ACE-Step accepts 10–120 seconds, defaults to 30 seconds, and uses the upstream 8-step Turbo configuration. Music generation is instrumental with language-model thinking disabled; optional BPM is 30–300. Both engines generate 1–4 variations sequentially.

For Stable Audio Open comparisons, **Generation settings → Stable Audio implementation** selects the existing **Diffusers** backend or **stable-audio-tools**. Diffusers remains the default, including for older jobs. The native backend uses its separately prepared local runtime, pinned official source revision `3241adba4fc2a85cf5b29d9eb68d42f40a28e820`, native checkpoint, and a unified local T5 model/tokenizer folder. Run the separately provided `setup-native.ps1` provisioning command before selecting it. Missing or incomplete native files fail readiness independently of Diffusers and ACE-Step; inference never downloads models.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\prototypes\audio-studio\setup-native.ps1
```

The native setup uses an isolated Python 3.10 environment and a pinned Windows dependency lock, leaving the existing music/SFX environment intact. It also installs the upstream LoRA callback imports needed during model construction, including Lightning, without installing the full training or Gradio extras. The native checkpoint uses the same model revision as Diffusers; the verified local T5 files are reused.

Native inference strictly loads the checkpoint, uses CUDA float16 for the model and frozen T5 encoder, and samples the official full `sample_size` before the common duration trim/export. It uses `dpmpp-3m-sde` with sigma 0.3–500, explicit per-take seeds, and the requested CFG scale. Adaptive projected guidance is disabled (`apg_scale=0`) and DiT CFG rescaling is disabled (`scale_phi=0`) for the comparison. Native inference moves components between CPU memory and CUDA in phases: text conditioning once per job, DiT diffusion, then VAE decode. The inactive components remain in CPU memory; all inference still runs on CUDA, and raw conditioning tensors are reused for variations. Stage policy and timings are recorded in provenance. Matching prompt/settings/seeds compare practical pipelines; their noise generation, sampler implementation, and precision differ, so outputs are not expected to match sample for sample. Backend, pinned source-file hashes, precision, sampler, full sample size, guidance behavior, and package versions are recorded in provenance. Cards show their backend alongside their permanent generation/take IDs.

Jobs run one at a time, with up to eight waiting in the local queue. A job first waits for the repository's shared GPU queue, then starts a fresh inference process. Model loading and all variations occur inside that process. Its exit releases CUDA allocations before the controller releases the GPU ticket. The controller imports neither Torch nor the model libraries. Model loading repeats for each job, trading startup time for reliable GPU release between jobs. Existing editor/test processes are not stopped.

## Files and provenance

Each job owns `output/<job-uuid>/` beneath this prototype:

- `variation-<number>-seed-<seed>.wav`: PCM16 WAV.
- Matching `.ogg`: Vorbis preview/export.
- `metadata.json`: exact submitted prompt and resolved settings, per-variation seeds, model ID/revision/license, source revision, package/Python/CUDA versions, server/worker hashes, timings, peak allocated VRAM, sample rate/channels/duration, peak/RMS/gain/clipping metrics, and WAV/OGG SHA-256 hashes.
- `worker-stdout.log`, `worker-stderr.log`: inference diagnostics, accessible from failed job cards.
- `worker-result.json`: successful worker result, verified by the controller before publishing success.

OGG export streams 4,096-frame writes into the Vorbis encoder. This bounds each native call and avoids the Windows stack overflow caused by exporting a long stereo waveform in one write. WAV export, audio gain, generation settings, and metrics use the same samples as before. Completed worker processes record `worker_exit_code` (the actual numeric process result) and `worker_exit_code_hex` (normalized unsigned 32-bit hexadecimal) in job metadata, including successful exit zero. Failure messages retain both forms even when stderr contains only progress output; full diagnostics remain in the worker log.

Decoded padding is trimmed to the requested duration. Audio is not normalized upward; attenuation only caps peaks above 0.999 before encoding. Metrics describe the floating-point audio before PCM16 quantization and lossy Vorbis encoding. ACE's built-in loudness normalization is disabled. Generated music is not automatically looped or mastered.

Job metadata and finished takes survive server restarts. Interrupted jobs become failed rather than restarting automatically. Each worker checks its controller PID and process creation time every second and before GPU rendering; if the controller disappears or its PID is reused, the worker terminates itself, releasing its CUDA context. After an abrupt controller exit the abandoned shared-queue ticket expires through the queue's normal stale-ticket policy. Temporary upstream ACE outputs are removed after each worker run; final audio and diagnostics remain available. Model weights and runtime caches stay under `artifacts/audio-studio/`; outputs are separate and should remain untracked.

## Local API

`GET /api/health` reports `app`, `pid`, per-engine `ready`/`message`/model details, queue depth/capacity, `busy`/`active_jobs`, and whether an inference worker is active. This readiness check verifies model configuration files; actual model loading and CUDA errors appear on the job. `GET /api/jobs` returns `{ "jobs": [...] }`, newest first. `GET /api/jobs/<id>` returns one job. `POST /api/prepare-stop` returns 409 while work remains; when idle it atomically disables new submissions and returns the controller PID for the managed stop script.

Queue a sound effect from PowerShell:

```powershell
$body = @{
    engine = 'stable-audio-open'
    prompt = 'A heavy metal door sliding open with a hydraulic hiss and deep mechanical clunk, no music.'
    duration = 5
    variants = 2
    seed = 42
    steps = 50
    guidance_scale = 7
    negative_prompt = 'Low quality, distorted.'
} | ConvertTo-Json
$job = Invoke-RestMethod http://127.0.0.1:7861/api/jobs -Method Post -ContentType 'application/json' -Body $body
Invoke-RestMethod "http://127.0.0.1:7861/api/jobs/$($job.id)"
```

Queue instrumental music:

```powershell
$body = @{
    engine = 'ace-step'
    prompt = 'Instrumental atmospheric science-fiction exploration music, warm synth pads, delicate arpeggios, no vocals.'
    duration = 30
    variants = 1
    seed = 42
    bpm = 90
} | ConvertTo-Json
$job = Invoke-RestMethod http://127.0.0.1:7861/api/jobs -Method Post -ContentType 'application/json' -Body $body
```

`POST /api/jobs` returns HTTP 202 with the job immediately. Status progresses through `queued`, `waiting_gpu`, `generating`, then `succeeded` or `failed`. An omitted/null seed is randomized and returned in `settings.seed`. Duration and steps resolve to engine defaults when omitted. Invalid settings return 422; unavailable models return 503; a full waiting queue returns 429. File downloads use `GET /api/jobs/<id>/files/<filename>`; only recorded audio, metadata, and worker logs are exposed.

For native Stable Audio jobs, add `backend = 'stable-audio-tools'` to the request body; the default is `'diffusers'`. The native selection is rejected for ACE-Step. `/api/health` also reports `stable_backends.diffusers` and `stable_backends.stable-audio-tools` without changing the existing per-engine fields. Native jobs execute only the fixed workspace Python environment at `artifacts/audio-studio/venv-stable-audio-tools/Scripts/python.exe`; request bodies cannot provide executable paths.

The local [A/B listening page](http://127.0.0.1:7861/compare.html) presents the prepared comparison pairs from `web/comparison-data.json`, with backend and permanent take IDs beside each player. **Match playback levels** defaults on and applies the supplied attenuation-only playback gains; switching it off restores each file's original playback level. Downloads are unchanged. Starting a player pauses the others. The page compares existing takes and creates no generation jobs.

The server accepts only the `localhost:7861` and `127.0.0.1:7861` Host values and same-origin browser requests. Shell clients may omit Origin. It is intended for this machine's loopback interface; there is no remote-user authentication. Inference uses local weights and offline Hugging Face/Transformers defaults. ACE's automatic missing-model download fallback is disabled; download through setup instead.

## Export regression

Native readiness, fixed-runtime dispatch, request validation, and local T5 config patching have an isolated CPU regression:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\run_tests.ps1 -Tests audio_studio_native_regression -NoBuild -Label audio-studio-native
```

The permanent-ID CPU regression uses isolated temporary output and sequence files, with no GPU or models:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\run_tests.ps1 -Tests audio_studio_ids_regression -NoBuild -Label audio-studio-ids
```

After preparing the optional local audio runtime, run the CPU regression through the repository test runner:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\run_tests.ps1 -Tests audio_studio_export_regression -NoBuild -Label audio-studio-export
```

The test calls the production OGG writer in separate processes for 5-, 15-, and 47-second 44.1 kHz stereo clips and a 120-second 48 kHz stereo clip. It checks successful process exits, full decoded frame counts, sample rates, channels, and finite nonzero signal. It also checks Python/native worker error reporting for positive and signed Windows exit codes, traceback stderr, progress-only stderr, and empty stderr. It imports no model runtime and requires no GPU or downloaded weights. Its fixed scratch directory is `artifacts/audio-studio/regression/audio-studio-export`; an exclusive lock protects concurrent runs, and the driver removes only the files it created, including partial files left by a native crash.

## Licenses

The manifest at `artifacts/audio-studio/model-manifest.json` records each installed model's exact revision and license. Stable Audio Open uses the **Stability AI Community License**; consult the [model card and linked license terms](https://huggingface.co/stabilityai/stable-audio-open-1.0) for your use. ACE-Step 1.5 uses **MIT**; see its [official repository](https://github.com/ace-step/ACE-Step-1.5). Listening quality, suitability for a particular game scene, and final asset selection require review.
