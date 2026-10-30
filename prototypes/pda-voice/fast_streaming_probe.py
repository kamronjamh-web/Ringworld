"""Two fresh oxygen streams using native Windows CUDA graphs in the isolated fast runtime."""
import html
from importlib import metadata
import json
import math
import os
from pathlib import Path
import random
import sys
import time

sys.dont_write_bytecode = True
from dynamic_clone_probe import normalize, write_pcm16, measure, validate_audio
from expressive_audition import (MODEL as VOICE_DESIGN_MODEL, MODEL_ID as VOICE_DESIGN_ID,
                                 MODEL_REVISION as VOICE_DESIGN_REVISION,
                                 atomic_json, file_hash, read_json, utc_now)

ROOT = Path(__file__).resolve().parent
RUNTIME = ROOT.parent.parent / "artifacts" / "pda-expressive-fast"
OUTPUT = ROOT / "output" / "fast-smoke"
MODEL = ROOT / "models" / "qwen3-tts-0.6b-base"
MODEL_ID = "Qwen/Qwen3-TTS-12Hz-0.6B-Base"
MODEL_REVISION = "5d83992436eae1d760afd27aff78a71d676296fc"
FAST_COMMIT = "b35fdc7d71eb3e4d19e63f4b4a330a1aef31d682"
REFERENCE = ROOT / "output" / "expressive" / "raw" / "reassuring" / "low_oxygen.wav"
REFERENCE_TEXT = "Your oxygen is at 15 percent. Let's get you back to breathable air."
SETTINGS = dict(non_streaming_mode=True, max_new_tokens=256, min_new_tokens=2,
                temperature=0.9, top_k=50, top_p=1.0, do_sample=True,
                repetition_penalty=1.05, chunk_size=8, parity_mode=False)


def offline_environment():
    for name, path in {
        "HF_HOME": RUNTIME / "hf", "HUGGINGFACE_HUB_CACHE": RUNTIME / "hf" / "hub",
        "HF_ASSETS_CACHE": RUNTIME / "hf" / "assets", "TORCH_HOME": RUNTIME / "torch",
        "XDG_CACHE_HOME": RUNTIME / "cache", "NUMBA_CACHE_DIR": RUNTIME / "numba",
        "TORCHINDUCTOR_CACHE_DIR": RUNTIME / "inductor", "TRITON_CACHE_DIR": RUNTIME / "triton",
    }.items():
        os.environ[name] = str(path)
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1")


def json_safe(value):
    if value is None or isinstance(value, (str, bool, int)):
        return value
    if isinstance(value, float):
        return value if math.isfinite(value) else str(value)
    if isinstance(value, dict):
        return {str(key): json_safe(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [json_safe(item) for item in value]
    if hasattr(value, "tolist"):
        return json_safe(value.tolist())
    return str(value)


def listening_page(results, text, generated, voice_design=False):
    def player(path, label):
        return f'<audio controls preload="none" aria-label="{html.escape(label, quote=True)}" src="{html.escape(path, quote=True)}"></audio>'

    page = ["""<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Faster dynamic speech trial</title><style>body{font:16px system-ui,sans-serif;max-width:1000px;margin:2rem auto;padding:0 1rem;color:#e6edf3;background:#111923}h1,h2{color:#aaddf4}audio{width:100%}a{color:#aaddf4}.players{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:1rem}</style></head><body>
        <h1>Faster dynamic speech trial</h1><p>Two fresh attempts at the same oxygen alert, compared with the accepted expressive voice and the earlier smaller-model trial. The candidate uses the accepted voice recording as a reference; identity and expression may differ.</p>
        <p>Listen for the complete warning, clear numbers and smooth delivery. Finishing generation does not establish that every word is correct. Levels are approximately matched; keep playback volume consistent. Starting a clip pauses the others.</p>"""]
    if voice_design:
        page[0] = page[0].replace("Faster dynamic speech trial", "Faster expressive voice trial").replace(
            "The candidate uses the accepted voice recording as a reference; identity and expression may differ.",
            "The 1.7B VoiceDesign candidate uses the same persona and reassuring direction as the accepted expressive voice, without cloning. Identity and expression may still differ.")
    page.append(f'<p>{html.escape(text)}</p><p>Generated {generated[:10]} · <a href="results.json">Measurements and settings (JSON)</a></p><div class="players">')
    for path, label in (("../dialogue-r1/processed/reassuring/low_oxygen_clean.wav", "Accepted expressive voice — reassuring"),
                        ("../dynamic-clone-probe/processed/low_oxygen_clean.wav", "Earlier smaller-model trial")):
        page.append(f'<div><h2>{label}</h2>{player(path, label)}</div>')
    for result in results:
        label = f'Faster {"1.7B VoiceDesign" if voice_design else "candidate"} — attempt {result["Attempt"]}'
        page.append(f'<div><h2>{label}</h2>{player(result["CleanPath"], label)}</div>')
    page.append("</div><p>These recordings demonstrate the voice output. Their generation measurements are in the linked report; the players do not recreate live streaming delays.</p><script>document.addEventListener('play',event=>{if(event.target instanceof HTMLAudioElement){document.querySelectorAll('audio').forEach(audio=>{if(audio!==event.target)audio.pause();});}},true);</script></body></html>")
    return "\n".join(page)


def main():
    voice_design = sys.argv[1:] == ["--voice-design"]
    if sys.argv[1:] and not voice_design:
        raise ValueError("This fixed two-attempt probe accepts only --voice-design.")
    model_path = VOICE_DESIGN_MODEL if voice_design else MODEL
    model_id = VOICE_DESIGN_ID if voice_design else MODEL_ID
    model_revision = VOICE_DESIGN_REVISION if voice_design else MODEL_REVISION
    output = ROOT / "output" / "fast-voicedesign-smoke" if voice_design else OUTPUT
    settings = {key: value for key, value in SETTINGS.items() if not voice_design or key != "parity_mode"}
    if Path(sys.prefix).resolve() != (RUNTIME / "venv").resolve():
        raise RuntimeError("Run with artifacts/pda-expressive-fast/venv/Scripts/python.exe; the older environment must stay separate.")
    offline_environment()
    snapshot = read_json(model_path / "download_manifest.json")
    if snapshot.get("Model") != model_id or snapshot.get("Revision") != model_revision:
        raise ValueError("The selected local model snapshot does not match the pinned revision.")
    reference = None
    if not voice_design:
        accepted = read_json(ROOT / "output" / "expressive" / "raw_manifest.json")
        refs = [clip for clip in accepted["Clips"] if clip["StyleId"] == "reassuring" and clip["EventId"] == "low_oxygen"]
        if not accepted.get("Complete") or len(refs) != 1:
            raise ValueError("A completed accepted audition reference is required.")
        reference = refs[0]
        if reference["RawPath"] != "raw/reassuring/low_oxygen.wav" or reference["Text"] != REFERENCE_TEXT or reference["Sha256"] != file_hash(REFERENCE):
            raise ValueError("Accepted reference hash, path or transcript differs.")
    config = read_json(ROOT / "expressive-dialogue-r1.json")
    matches = [line for line in config["Lines"] if line["EventId"] == "low_oxygen"]
    if config.get("DialogueRevision") != "dialogue-r1" or len(matches) != 1 or matches[0]["Seed"] != 4103:
        raise ValueError("Expected the approved dialogue-r1 oxygen line and seed 4103.")
    line = matches[0]
    instruction = None
    if voice_design:
        reassuring = [style for style in config["Styles"] if style["Id"] == "reassuring"]
        if len(reassuring) != 1 or not config["Persona"].strip():
            raise ValueError("Expected the fixed r1 persona and reassuring style.")
        instruction = config["Persona"] + " " + reassuring[0]["Prompt"]
    catalog = read_json(ROOT / "dialogue-catalog-r1.json")
    entry = next(entry for entry in catalog["Entries"] if entry["EventId"] == "low_oxygen")
    event = next(event for event in read_json(ROOT / "events.json")["events"] if event["id"] == "low_oxygen")
    if line["Text"] != entry["Text"] or line["Facts"] != entry["Facts"] or line["Facts"] != event["facts"]:
        raise ValueError("Oxygen dialogue text/facts do not match the catalog and event.")
    comparisons = ("../dialogue-r1/processed/reassuring/low_oxygen_clean.wav", "../dynamic-clone-probe/processed/low_oxygen_clean.wav")
    for path in comparisons:
        if not (output / path).is_file():
            raise FileNotFoundError(output / path)

    import numpy as np
    import psutil
    import soundfile as sf
    import torch
    from packaging.version import Version
    from faster_qwen3_tts import FasterQwen3TTS

    versions = {name: metadata.version(name) for name in ("faster-qwen3-tts", "qwen-tts-hf", "transformers", "huggingface-hub", "torch", "torchaudio", "numpy", "soundfile", "psutil")}
    for name, expected in {"faster-qwen3-tts": "0.5.4", "qwen-tts-hf": "0.1.1.post1", "transformers": "5.15.1", "torch": "2.10.0", "torchaudio": "2.10.0"}.items():
        if versions[name].split("+")[0] != expected:
            raise ValueError(f"Expected {name} {expected}, found {versions[name]}.")
    if not Version("1.5") <= Version(versions["huggingface-hub"]) < Version("2"):
        raise ValueError("Expected huggingface-hub >=1.5,<2.")
    try:
        metadata.version("qwen-tts")
    except metadata.PackageNotFoundError:
        pass
    else:
        raise RuntimeError("The original qwen-tts distribution must not share the fast environment.")
    provenance = json.loads(metadata.distribution("faster-qwen3-tts").read_text("direct_url.json") or "{}")
    if FAST_COMMIT not in provenance.get("url", ""):
        raise ValueError("Faster package provenance does not identify the pinned source commit.")
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is required; capture failures or OOM do not trigger a fallback.")
    torch.set_num_threads(4)
    if not voice_design:
        samples, rate = sf.read(REFERENCE, dtype="float32")
        validate_audio(samples, rate)
        if rate != 24000 or sf.info(REFERENCE).subtype != "FLOAT":
            raise ValueError("Expected the accepted 24 kHz raw FLOAT reference.")
    process = psutil.Process()

    def memory():
        info = process.memory_info()
        return {"CudaAllocatedBytes": torch.cuda.memory_allocated(), "CudaReservedBytes": torch.cuda.memory_reserved(),
                "PeakCudaAllocatedBytes": torch.cuda.max_memory_allocated(), "PeakCudaReservedBytes": torch.cuda.max_memory_reserved(),
                "PeakProcessRamBytes": getattr(info, "peak_wset", info.rss)}

    def stage(function):
        torch.cuda.reset_peak_memory_stats()
        torch.cuda.synchronize()
        start = time.perf_counter()
        value = function()
        torch.cuda.synchronize()
        return value, {"Seconds": time.perf_counter() - start, **memory()}

    output.mkdir(parents=True, exist_ok=True)
    for name in ("results.json", "index.html"):
        (output / name).unlink(missing_ok=True)
    print("Loading isolated faster model on CUDA...", flush=True)
    fast, load = stage(lambda: FasterQwen3TTS.from_pretrained(str(model_path), backend="torch", device="cuda",
                        dtype=torch.bfloat16, attn_implementation="sdpa", max_seq_len=512))
    prompt_items, prompt = None, None
    if not voice_design:
        prompt_items, prompt = stage(lambda: fast.model.create_voice_clone_prompt(ref_audio=str(REFERENCE),
                                            ref_text=REFERENCE_TEXT, x_vector_only_mode=False))
        if not isinstance(prompt_items, list) or not prompt_items:
            raise ValueError("Expected newly created reusable reference prompt items.")
    print("Capturing predictor/talker CUDA graphs...", flush=True)
    _, graph = stage(lambda: fast.warmup(prefill_len=100))
    observed_lengths = []
    original_prefill = fast.talker_graph.prefill_kv

    def observed_prefill(past_key_values):
        value = original_prefill(past_key_values)
        observed_lengths.append(int(value))
        return value

    def stream():
        chunks, arrivals = [], []
        first = None
        previous = None
        observed_lengths.clear()
        fast.talker_graph.prefill_kv = observed_prefill
        torch.cuda.synchronize()
        started = time.perf_counter()
        try:
            generator = fast.generate_voice_design_streaming(text=line["Text"], instruct=instruction, language="English", **settings) if voice_design else fast.generate_voice_clone_streaming(text=line["Text"], language="English", voice_clone_prompt=prompt_items, **settings)
            for audio_chunk, sr, internal in generator:
                chunk = np.array(audio_chunk, dtype=np.float32, copy=True)
                arrived = time.perf_counter() - started
                if sr != 24000 or chunk.ndim != 1 or not np.isfinite(chunk).all():
                    raise ValueError("Expected finite mono streaming chunks at 24 kHz.")
                arrivals.append({"Index": len(arrivals), "ArrivalSeconds": arrived,
                                 "ArrivalGapSeconds": None if previous is None else arrived - previous,
                                 "FrameCount": int(chunk.size), "AudioSeconds": chunk.size / sr,
                                 "InternalTiming": json_safe(internal)})
                previous = arrived
                if chunk.size:
                    if first is None:
                        first = arrived
                    chunks.append(chunk)
        finally:
            fast.talker_graph.prefill_kv = original_prefill
        torch.cuda.synchronize()
        elapsed = time.perf_counter() - started
        if not chunks or first is None:
            raise ValueError("Streaming returned no nonempty audio.")
        audio = np.concatenate(chunks)
        validate_audio(audio, 24000)
        duration = audio.size / 24000
        if not 1 <= duration <= 15:
            raise ValueError(f"Unexpected short-alert duration: {duration:.2f}s; inspect truncation or runaway generation.")
        codec_counts = [item["InternalTiming"].get("chunk_steps") for item in arrivals]
        codec_frames = sum(codec_counts) if all(isinstance(count, int) and count >= 0 for count in codec_counts) else None
        prefill_length = observed_lengths[0] if len(observed_lengths) == 1 else None
        limits_excluded = prefill_length is not None and codec_frames is not None and codec_frames < 256 and prefill_length + codec_frames < 512
        return audio, {"TimeToFirstAudioSeconds": first, "TotalWallSeconds": elapsed,
                       "AudioSeconds": duration, "ComputeRealTimeFactor": elapsed / duration,
                       "SumFrameCounts": sum(item["FrameCount"] for item in arrivals),
                       "ChunkCount": len(arrivals), "NonemptyChunkCount": len(chunks), "Chunks": arrivals,
                       "TerminationEvidence": {"NormalGeneratorExhaustion": True, "ObservedPrefillLengths": list(observed_lengths),
                           "PrefillLength": prefill_length, "GeneratedCodecFrameCount": codec_frames,
                           "GenerationLimitsExcluded": limits_excluded, "NaturalEosInferred": limits_excluded,
                           "Rationale": "Pinned fast streaming loop exits by EOS, max_new_tokens or static-cache boundary. Exactly one observed prefill plus generated codec frames below 512, and fewer than 256 generated frames, excludes both limits. Otherwise termination is inconclusive. This does not prove spoken fidelity; is_final only flags a partial final chunk."}}

    print("Warming one complete oxygen stream; excluded from measured attempts...", flush=True)
    _, speech_warmup = stage(stream)
    results = []
    for attempt in (1, 2):
        random.seed(4103)
        np.random.seed(4103)
        torch.manual_seed(4103)
        torch.cuda.manual_seed_all(4103)
        torch.cuda.reset_peak_memory_stats()
        print(f"Measuring fresh oxygen stream {attempt}/2...", flush=True)
        audio, timing = stream()
        timing.update(memory())
        raw_path, clean_path = f"attempt_{attempt}_raw.wav", f"attempt_{attempt}_clean.wav"
        sf.write(output / raw_path, audio, 24000, subtype="FLOAT")
        normalized, gain = normalize(audio, 24000)
        write_pcm16(output / clean_path, normalized, 24000)
        results.append({"Attempt": attempt, "Seed": 4103, "RawPath": raw_path, "CleanPath": clean_path,
                        "RawSha256": file_hash(output / raw_path), "CleanGainDb": gain,
                        "CleanLevels": measure(normalized, 24000), **timing})
        print(f"Attempt {attempt}: first audio {timing['TimeToFirstAudioSeconds']:.3f}s, total {timing['TotalWallSeconds']:.3f}s, audio {timing['AudioSeconds']:.3f}s, compute/audio {timing['ComputeRealTimeFactor']:.3f}", flush=True)
    generated = utc_now()
    setup = {"ModelLoad": load, "GraphWarmup": graph, "FullStreamWarmup": speech_warmup}
    if not voice_design:
        setup["ReferencePrompt"] = prompt
    report = {"GeneratedUtc": generated, "Complete": True, "ModelId": model_id, "ModelRevision": model_revision,
              "FastCodeRevision": FAST_COMMIT, "FastCodeProvenance": provenance, "PackageVersions": versions,
              "PythonVersion": sys.version, "Device": "cuda", "Dtype": "bfloat16", "Attention": "sdpa", "Backend": "torch",
              "CpuThreads": 4, "GpuName": torch.cuda.get_device_name(0), "MaxSequenceLength": 512, "GenerationSettings": settings,
              "Mode": "VoiceDesign" if voice_design else "ReferenceConditionedBase", "Instruction": instruction,
              "Reference": None if voice_design else {"Path": "../expressive/raw/reassuring/low_oxygen.wav", "Text": REFERENCE_TEXT, "Sha256": reference["Sha256"], "XVectorOnlyMode": False},
              "Input": line, "Setup": setup,
              "Results": results, "Comparisons": comparisons,
              "LevelMatching": "Shared dynamic_clone_probe helpers: constant gain toward -20 dBFS absolute-gated 20 ms RMS above -45 dBFS, peak cap -1 dBFS; approximate levels, not exact perceptual matching.",
              "Caveats": ["Resident, serialized worker required: load, graph capture and speech warm-up are separate startup costs; VoiceDesign performs no reference preparation." if voice_design else "Resident, serialized worker required: load, reference preparation, graph capture and speech warm-up are separate startup costs.",
                          "Time to first audio measures first nonempty copied NumPy chunk; no playback or per-chunk disk writes. Total wall time synchronizes CUDA boundaries. ComputeRealTimeFactor = total wall seconds / audio seconds.",
                          "non_streaming_mode=True conditions on full text; audio still streams. Internal package timings are recorded separately and are not equivalent to wall arrival timings.",
                          "Static cache max_seq_len=512 can truncate or constrain generation. A completed call and plausible duration do not prove complete or correct spoken text; listen to the full warning and numbers.",
                          "VoiceDesign uses the fixed r1 persona plus reassuring direction without a reference; identity and acting may vary. Not game integration or performance acceptance." if voice_design else "Reference-conditioned candidate; no Base-model instruction/style control or exact identity/acting guarantee. Not game integration or performance acceptance."]}
    page = listening_page(results, line["Text"], generated, voice_design)
    atomic_json(output / "results.json", report)
    (output / "index.html").write_text(page, encoding="utf-8")
    print(f"Fast streaming probe complete: {output / 'index.html'}", flush=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"Fast streaming probe failed: {type(error).__name__}: {error}", file=sys.stderr, flush=True)
        sys.exit(2)
