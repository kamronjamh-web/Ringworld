"""Fresh 0.6B reference-conditioned speech diagnostic; never changes accepted audition outputs."""
import html
from importlib import metadata
import json
import math
import random
import sys
import time

# Import the shared offline/settings helpers without writing caches beside prototype sources.
sys.dont_write_bytecode = True
from expressive_audition import (CODE_REVISION, FLAGS, ROOT, atomic_json,
                                 file_hash, offline_environment, read_json, utc_now)

MODEL_ID = "Qwen/Qwen3-TTS-12Hz-0.6B-Base"
MODEL_REVISION = "5d83992436eae1d760afd27aff78a71d676296fc"
MODEL = ROOT / "models" / "qwen3-tts-0.6b-base"
OUTPUT = ROOT / "output" / "dynamic-clone-probe"
REFERENCE_PATH = ROOT / "output" / "expressive" / "raw" / "reassuring" / "low_oxygen.wav"
REFERENCE_TEXT = "Your oxygen is at 15 percent. Let's get you back to breathable air."
EVENT_IDS = ("storm_incoming", "emitter_discovered", "low_oxygen")
DSP_SETTINGS = {"LowHz": 180, "HighHz": 4800, "Q": 0.7071, "SectionsPerEdge": 2,
                "Wet": 0.85, "Drive": 1.35, "TargetGatedRmsDbfs": -20, "PeakCapDbfs": -1,
                "GateDbfs": -45, "GateWindowMs": 20, "ReelGapSeconds": 0.7}


def validate_audio(samples, rate):
    import numpy as np
    if rate <= 15000 or samples.ndim != 1 or samples.size == 0 or not np.isfinite(samples).all():
        raise ValueError("Invalid, empty or nonfinite speech waveform.")
    if float(np.max(np.abs(samples))) <= 1e-6:
        raise ValueError("Silent speech waveform.")


def measure(samples, rate):
    """Match the C# absolute 20 ms block gate, reporting pre-PCM16 levels."""
    import numpy as np
    validate_audio(samples, rate)
    window = max(1, rate // 50)
    squares = samples.astype(np.float64) ** 2
    total = float(np.sum(squares))
    gated_sum, gated_count = 0.0, 0
    for offset in range(0, samples.size, window):
        block = squares[offset:offset + window]
        power = float(np.sum(block))
        if power / block.size > 10 ** (-45 / 10):
            gated_sum += power
            gated_count += block.size
    if not gated_count:
        raise ValueError("Speech contains no blocks above the -45 dBFS gate.")
    db = lambda amplitude: 20 * math.log10(max(amplitude, 1e-12))
    return {"RmsDbfs": db(math.sqrt(total / samples.size)),
            "GatedRmsDbfs": db(math.sqrt(gated_sum / gated_count)),
            "PeakDbfs": db(float(np.max(np.abs(samples))))}


def normalize(samples, rate):
    """One constant gain per clip; no dynamic compressor."""
    import numpy as np
    levels = measure(samples, rate)
    gain_db = min(-20 - levels["GatedRmsDbfs"], -1 - levels["PeakDbfs"])
    normalized = (samples.astype(np.float64) * 10 ** (gain_db / 20)).astype(np.float32)
    return normalized, gain_db


def process_pda(samples, rate):
    """RBJ coefficients and float32 filter state matching the C# PDA profile's math."""
    import numpy as np
    from scipy.signal import sosfilt
    validate_audio(samples, rate)

    def section(hz, highpass):
        w = 2 * math.pi * hz / rate
        c, a = math.cos(w), math.sin(w) / (2 * 0.7071)
        b = ((1 + c) / 2, -(1 + c), (1 + c) / 2) if highpass else ((1 - c) / 2, 1 - c, (1 - c) / 2)
        a0 = 1 + a
        return np.array([b[0] / a0, b[1] / a0, b[2] / a0, 1, -2 * c / a0, (1 - a) / a0], dtype=np.float32)

    hp, lp = section(180, True), section(4800, False)
    sos = np.stack((hp, hp, lp, lp))
    filtered = sosfilt(sos, np.asarray(samples, dtype=np.float32))
    # The C# profile promotes filtered float samples to double for tanh/mixing, then casts to float.
    return ((1 - 0.85) * samples.astype(np.float64) + 0.85 * np.tanh(filtered.astype(np.float64) * 1.35)).astype(np.float32)


def write_pcm16(path, samples, rate):
    import numpy as np
    import soundfile as sf
    path.parent.mkdir(parents=True, exist_ok=True)
    # Keep the C# multiplication in float32 before round-to-even and PCM16 conversion.
    pcm = np.rint(samples * np.float32(32767)).clip(-32768, 32767).astype(np.int16)
    sf.write(path, pcm, rate, subtype="PCM_16")


def listening_page(lines, generated):
    def player(path, label):
        return f'<audio controls preload="none" aria-label="{html.escape(label, quote=True)}" src="{html.escape(path, quote=True)}"></audio>'

    page = ["""<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Smaller dynamic speech trial</title><style>body{font:16px system-ui,sans-serif;max-width:1100px;margin:2rem auto;padding:0 1rem;color:#e6edf3;background:#111923}h1,h2,h3{color:#aaddf4}a{color:#aaddf4}audio{width:100%}.players{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:1rem}details{margin:2rem 0}summary{cursor:pointer}</style></head><body>
        <h1>Smaller dynamic speech trial</h1><p>Compare fresh speech from a smaller model with the accepted expressive voice. The smaller candidate uses an existing clean voice recording as its reference. Identity and expression may differ; this is not an exact clone or identical acting.</p>
        <p>The first three reels read the same revised dialogue. Start with the clean versions to judge the voice, then try the PDA speaker tone. Levels are approximately matched; keep your playback volume consistent. Starting a clip pauses the others.</p>"""]
    page.append(f'<p>Generated {generated[:10]} · <a href="results.json">Measurements and settings (JSON)</a></p><div class="players">')
    for path, label in (("../dialogue-r1/processed/reassuring/reel_clean.wav", "Accepted expressive voice — reassuring, revised dialogue"),
                        ("reel_clean.wav", "Smaller candidate — clean"), ("reel_pda_speaker.wav", "Smaller candidate — PDA speaker")):
        page.append(f'<div><h2>{html.escape(label)}</h2>{player(path, label)}</div>')
    page.append('</div><details><summary>Individual lines and transcripts</summary>')
    for line in lines:
        page.append(f'<p>{html.escape(line["Text"])}</p><div class="players">')
        baseline = f'../dialogue-r1/processed/reassuring/{line["EventId"]}_clean.wav'
        for path, label in ((baseline, "Accepted voice — clean"), (line["Clean"]["Path"], "Smaller candidate — clean"), (line["PdaSpeaker"]["Path"], "Smaller candidate — PDA speaker")):
            page.append(f'<div><p>{label}</p>{player(path, line["EventId"] + " " + label)}</div>')
        page.append('</div>')
    page.append('</details><h2>Original accepted reference — earlier wording</h2><p>This oxygen line supplied the clean voice reference; its wording differs from the revised clips above.</p>')
    page.append(player("../expressive/processed/reassuring/low_oxygen_clean.wav", "Original accepted voice reference, earlier wording"))
    page.append("<script>document.addEventListener('play',event=>{if(event.target instanceof HTMLAudioElement){document.querySelectorAll('audio').forEach(audio=>{if(audio!==event.target)audio.pause();});}},true);</script></body></html>")
    return "\n".join(page)


def main():
    if len(sys.argv) != 1:
        raise ValueError("This fixed diagnostic takes no arguments.")
    offline_environment()
    snapshot = read_json(MODEL / "download_manifest.json")
    if snapshot.get("Model") != MODEL_ID or snapshot.get("Revision") != MODEL_REVISION:
        raise ValueError("Local snapshot manifest does not identify the pinned smaller model revision.")
    for name in ("config.json", "generation_config.json"):
        if not (MODEL / name).is_file():
            raise FileNotFoundError(MODEL / name)
    raw_manifest = read_json(ROOT / "output" / "expressive" / "raw_manifest.json")
    references = [c for c in raw_manifest["Clips"] if c["StyleId"] == "reassuring" and c["EventId"] == "low_oxygen"]
    if not raw_manifest.get("Complete") or len(references) != 1:
        raise ValueError("Completed expressive audition with one reassuring oxygen reference is required.")
    reference = references[0]
    if reference["RawPath"] != "raw/reassuring/low_oxygen.wav" or reference["Text"] != REFERENCE_TEXT or reference["Sha256"] != file_hash(REFERENCE_PATH):
        raise ValueError("Accepted raw reference text, path or hash changed.")
    prompts = read_json(ROOT / "expressive-dialogue-r1.json")
    catalog = read_json(ROOT / "dialogue-catalog-r1.json")
    entries = {entry["EventId"]: entry for entry in catalog["Entries"]}
    events = {event["id"]: event for event in read_json(ROOT / "events.json")["events"]}
    if prompts.get("DialogueRevision") != "dialogue-r1" or len(prompts["Lines"]) != 3 or {l["EventId"] for l in prompts["Lines"]} != set(EVENT_IDS):
        raise ValueError("Expected exactly the three dialogue-r1 inputs.")
    baseline_config = read_json(ROOT / "expressive-prompts.json")
    baseline_seeds = {line["EventId"]: line["Seed"] for line in baseline_config["Lines"]}
    for line in prompts["Lines"]:
        entry, event = entries[line["EventId"]], events[line["EventId"]]
        if line["Text"] != entry["Text"] or line["Facts"] != entry["Facts"] or line["Facts"] != event["facts"] or line["Seed"] != baseline_seeds[line["EventId"]]:
            raise ValueError(f"Dialogue text, facts or seed changed: {line['EventId']}")
    baseline_paths = [ROOT / "output" / "dialogue-r1" / "processed" / "reassuring" / "reel_clean.wav",
                      ROOT / "output" / "expressive" / "processed" / "reassuring" / "low_oxygen_clean.wav"]
    baseline_paths += [ROOT / "output" / "dialogue-r1" / "processed" / "reassuring" / f"{event_id}_clean.wav" for event_id in EVENT_IDS]
    for path in baseline_paths:
        if not path.is_file():
            raise FileNotFoundError(path)

    import numpy as np
    import psutil
    import soundfile as sf
    import torch
    from qwen_tts import Qwen3TTSModel

    versions = {name: metadata.version(name) for name in ("qwen-tts", "transformers", "accelerate", "torch", "torchaudio", "soundfile", "numpy", "scipy", "psutil")}
    provenance = json.loads(metadata.distribution("qwen-tts").read_text("direct_url.json") or "{}")
    if CODE_REVISION not in provenance.get("url", ""):
        raise ValueError("Installed Qwen code provenance differs from the pinned official commit.")
    for name, expected in {"qwen-tts": "0.1.1", "transformers": "4.57.3", "accelerate": "1.12.0", "torch": "2.10.0", "torchaudio": "2.10.0"}.items():
        if versions[name].split("+")[0] != expected:
            raise ValueError(f"Expected {name} {expected}; found {versions[name]}.")
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is required for this fixed probe; no automatic CPU fallback.")
    reference_samples, reference_rate = sf.read(REFERENCE_PATH, dtype="float32")
    validate_audio(reference_samples, reference_rate)
    if sf.info(REFERENCE_PATH).subtype != "FLOAT":
        raise ValueError("The accepted reference must be the original FLOAT WAV, without radio processing.")
    torch.set_num_threads(4)
    process = psutil.Process()
    peak_ram = lambda: getattr(process.memory_info(), "peak_wset", process.memory_info().rss)
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for name in ("results.json", "index.html"):
        (OUTPUT / name).unlink(missing_ok=True)
    torch.cuda.reset_peak_memory_stats()
    torch.cuda.synchronize()
    start = time.perf_counter()
    print("Loading smaller local model (CUDA, bfloat16, SDPA)...", flush=True)
    model = Qwen3TTSModel.from_pretrained(str(MODEL), device_map="cuda:0", dtype=torch.bfloat16,
                                         attn_implementation="sdpa", local_files_only=True)
    torch.cuda.synchronize()
    load_seconds = time.perf_counter() - start
    peak_after_load = peak_ram()
    torch.cuda.synchronize()
    start = time.perf_counter()
    items = model.create_voice_clone_prompt(ref_audio=str(REFERENCE_PATH), ref_text=REFERENCE_TEXT, x_vector_only_mode=False)
    torch.cuda.synchronize()
    prompt_seconds = time.perf_counter() - start
    if not isinstance(items, list) or not items:
        raise ValueError("Expected reusable voice clone prompt items.")
    print("Reference prompt ready; running one untimed speech warm-up...", flush=True)
    with torch.inference_mode():
        warmup, warmup_rate = model.generate_voice_clone(text="Speech check complete.", language="English", voice_clone_prompt=items, **FLAGS)
    torch.cuda.synchronize()
    if len(warmup) != 1:
        raise ValueError("Warm-up returned an unexpected waveform count.")
    validate_audio(np.asarray(warmup[0], dtype=np.float32), warmup_rate)
    setup_peak_allocated = torch.cuda.max_memory_allocated()
    setup_peak_reserved = torch.cuda.max_memory_reserved()

    timings = {"CodecGenerationSeconds": 0.0, "CodecDecodeSeconds": 0.0}

    def timed_call(function, field):
        def wrapped(*args, **kwargs):
            torch.cuda.synchronize()
            began = time.perf_counter()
            result = function(*args, **kwargs)
            torch.cuda.synchronize()
            timings[field] += time.perf_counter() - began
            return result
        return wrapped

    original_generate = model.model.generate
    original_decode = model.model.speech_tokenizer.decode
    model.model.generate = timed_call(original_generate, "CodecGenerationSeconds")
    model.model.speech_tokenizer.decode = timed_call(original_decode, "CodecDecodeSeconds")
    results, parts = [], {"clean": [], "pda_speaker": []}
    reel_rate = None
    for line in prompts["Lines"]:
        for field in timings:
            timings[field] = 0.0
        random.seed(line["Seed"])
        np.random.seed(line["Seed"])
        torch.manual_seed(line["Seed"])
        torch.cuda.manual_seed_all(line["Seed"])
        torch.cuda.reset_peak_memory_stats()
        print(f"Rendering fresh speech: {line['EventId']}...", flush=True)
        torch.cuda.synchronize()
        start = time.perf_counter()
        with torch.inference_mode():
            wavs, rate = model.generate_voice_clone(text=line["Text"], language="English", voice_clone_prompt=items, **FLAGS)
        torch.cuda.synchronize()
        seconds = time.perf_counter() - start
        if len(wavs) != 1:
            raise ValueError("Expected one generated waveform per fresh input.")
        samples = np.asarray(wavs[0], dtype=np.float32)
        validate_audio(samples, rate)
        if reel_rate is not None and reel_rate != rate:
            raise ValueError("Sample rate changed within the probe.")
        reel_rate = rate
        raw_path = f"raw/{line['EventId']}.wav"
        (OUTPUT / "raw").mkdir(exist_ok=True)
        sf.write(OUTPUT / raw_path, samples, rate, subtype="FLOAT")
        result = {**line, "RawPath": raw_path, "RawSha256": file_hash(OUTPUT / raw_path),
                  "SampleRate": rate, "SampleCount": int(samples.size), "AudioSeconds": samples.size / rate,
                  "SynthSeconds": seconds, "Rtf": seconds / (samples.size / rate), **timings,
                  "PeakCudaAllocatedBytes": torch.cuda.max_memory_allocated(),
                  "PeakCudaReservedBytes": torch.cuda.max_memory_reserved(), "PeakProcessRamBytes": peak_ram(),
                  "RawLevels": measure(samples, rate)}
        for effect, field in (("clean", "Clean"), ("pda_speaker", "PdaSpeaker")):
            start = time.perf_counter()
            buffer = samples if effect == "clean" else process_pda(samples, rate)
            dsp_seconds = time.perf_counter() - start
            normalized, gain = normalize(buffer, rate)
            relative = f"processed/{line['EventId']}_{effect}.wav"
            write_pcm16(OUTPUT / relative, normalized, rate)
            result[field] = {"Path": relative, "DspSeconds": dsp_seconds, "GainDb": gain, "Levels": measure(normalized, rate)}
            parts[effect].append(normalized)
        results.append(result)
        print(f"{line['EventId']}: {result['AudioSeconds']:.2f}s audio in {seconds:.2f}s, RTF {result['Rtf']:.3f}; generation {timings['CodecGenerationSeconds']:.2f}s, decode {timings['CodecDecodeSeconds']:.2f}s", flush=True)
    model.model.generate = original_generate
    model.model.speech_tokenizer.decode = original_decode
    reels = {}
    for effect, clips in parts.items():
        gap = np.zeros(int(0.7 * reel_rate), dtype=np.float32)
        sequence = []
        for index, clip in enumerate(clips):
            if index:
                sequence.append(gap)
            sequence.append(clip)
        reel = np.concatenate(sequence)
        relative = f"reel_{effect}.wav"
        write_pcm16(OUTPUT / relative, reel, reel_rate)
        reels[effect] = {"Path": relative, "AudioSeconds": reel.size / reel_rate, "Levels": measure(reel, reel_rate)}
    generated = utc_now()
    manifest = {
        "GeneratedUtc": generated, "Complete": True, "ModelId": MODEL_ID, "ModelRevision": MODEL_REVISION,
        "CodeRevision": CODE_REVISION, "CodeProvenance": provenance, "PackageVersions": versions,
        "PythonVersion": sys.version, "Device": "cuda:0", "Dtype": "bfloat16", "Attention": "sdpa",
        "GpuName": torch.cuda.get_device_name(0), "CpuThreads": 4, "GenerationSettings": FLAGS,
        "Reference": {"Path": "../expressive/raw/reassuring/low_oxygen.wav", "Text": REFERENCE_TEXT, "Sha256": reference["Sha256"], "XVectorOnlyMode": False},
        "IdentityNote": "Reference-conditioned candidate, not an exact clone or identical acting. Base model has no instruct/style control.",
        "LoadSeconds": load_seconds, "ReferencePromptSeconds": prompt_seconds,
        "Warmup": "One separate 'Speech check complete.' render, excluded from measured calls; reference prompt items reused unchanged.",
        "TimingNote": "Three fresh uncached calls after warm-up. CUDA synchronization brackets wall time and lightweight model.model.generate / speech_tokenizer.decode wrappers. Their instrumentation adds overhead. This is a standalone diagnostic, not game-frame cost or a controlled cross-run benchmark.",
        "PeakProcessRamAfterLoadBytes": peak_after_load, "PeakProcessRamBytes": peak_ram(),
        "PeakCudaAllocatedBytes": max(setup_peak_allocated, *(line["PeakCudaAllocatedBytes"] for line in results)),
        "PeakCudaReservedBytes": max(setup_peak_reserved, *(line["PeakCudaReservedBytes"] for line in results)),
        "Processing": {**DSP_SETTINGS, "Math": "Float32 RBJ SOS filters; double tanh/mix then float32. Fixed gain per speech clip; no reel gain. Pre-PCM16 levels; np.rint(float32 * 32767), clipped int16, written as PCM_16. Numerical compatibility with C# requires validation; no bit-exact claim."},
        "ConfigFile": "expressive-dialogue-r1.json", "DialogueRevision": "dialogue-r1", "Lines": results, "Reels": reels,
    }
    page = listening_page(results, generated)
    atomic_json(OUTPUT / "results.json", manifest)
    (OUTPUT / "index.html").write_text(page, encoding="utf-8")
    print(f"Probe complete: {OUTPUT / 'index.html'}", flush=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"Dynamic clone probe failed: {type(error).__name__}: {error}", file=sys.stderr, flush=True)
        sys.exit(2)
