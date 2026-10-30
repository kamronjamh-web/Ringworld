"""Offline Qwen voice-design audition. DSP and level matching live in the C# report command."""
import argparse
from datetime import datetime, timezone
import hashlib
from importlib import metadata
import json
import os
from pathlib import Path
import random
import sys
import time

ROOT = Path(__file__).resolve().parent
ARTIFACTS = ROOT.parent.parent / "artifacts" / "pda-expressive"
MODEL = ROOT / "models" / "qwen3-tts-1.7b-voice-design"
MODEL_ID = "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign"
MODEL_REVISION = "5ecdb67327fd37bb2e042aab12ff7391903235d3"
CODE_REVISION = "022e286b98fbec7e1e916cb940cdf532cd9f488e"
FLAGS = dict(non_streaming_mode=True, max_new_tokens=256, do_sample=True, top_k=50,
             top_p=1.0, temperature=0.9, repetition_penalty=1.05,
             subtalker_dosample=True, subtalker_top_k=50, subtalker_top_p=1.0,
             subtalker_temperature=0.9)


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def atomic_json(path, data):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(data, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def file_hash(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def offline_environment():
    for name, path in {
        "HF_HOME": ARTIFACTS / "hf", "HUGGINGFACE_HUB_CACHE": ARTIFACTS / "hf" / "hub",
        "TORCH_HOME": ARTIFACTS / "torch", "XDG_CACHE_HOME": ARTIFACTS / "cache",
        "NUMBA_CACHE_DIR": ARTIFACTS / "numba", "HF_ASSETS_CACHE": ARTIFACTS / "hf" / "assets",
        "TORCHINDUCTOR_CACHE_DIR": ARTIFACTS / "inductor", "TRITON_CACHE_DIR": ARTIFACTS / "triton",
    }.items():
        os.environ[name] = str(path)
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--device", choices=("cuda", "cpu"), default="cuda")
    parser.add_argument("--smoke", action="store_true")
    parser.add_argument("--dialogue-r1", action="store_true")
    args = parser.parse_args()
    config_file = "expressive-dialogue-r1.json" if args.dialogue_r1 else "expressive-prompts.json"
    output = ROOT / "output" / ("dialogue-r1" if args.dialogue_r1 else "expressive")
    offline_environment()
    snapshot = read_json(MODEL / "download_manifest.json")
    if snapshot.get("Model") != MODEL_ID or snapshot.get("Revision") != MODEL_REVISION:
        raise ValueError("Local download_manifest.json does not identify the pinned model revision.")
    for name in ("config.json", "generation_config.json"):
        if not (MODEL / name).is_file():
            raise FileNotFoundError(MODEL / name)
    prompts = read_json(ROOT / config_file)
    events = {e["id"]: e for e in read_json(ROOT / "events.json")["events"]}
    if args.dialogue_r1:
        baseline = read_json(ROOT / "expressive-prompts.json")
        catalog = read_json(ROOT / "dialogue-catalog-r1.json")
        entries = {entry["EventId"]: entry for entry in catalog["Entries"]}
        if prompts.get("DialogueRevision") != "dialogue-r1" or catalog.get("DialogueRevision") != "dialogue-r1" or catalog.get("GlobalStatus") != "draft_fixture_catalog":
            raise ValueError("Expected the draft dialogue-r1 catalog and config.")
        if len(catalog["Entries"]) != len(events) or set(entries) != set(events):
            raise ValueError("Dialogue catalog must contain exactly the existing event IDs.")
        for event_id, entry in entries.items():
            ev = events[event_id]
            if entry["Category"] != ev["category"] or entry["Priority"] != ev["priority"] or entry["Facts"] != ev["facts"]:
                raise ValueError(f"Dialogue catalog metadata differs: {event_id}")
        if prompts["Persona"] != baseline["Persona"] or prompts["Styles"] != baseline["Styles"]:
            raise ValueError("Dialogue-r1 must keep the existing persona and styles.")
        baseline_lines = {line["EventId"]: line for line in baseline["Lines"]}
        for line in prompts["Lines"]:
            original = baseline_lines[line["EventId"]]
            if line["Seed"] != original["Seed"] or line["Facts"] != original["Facts"] or line["Text"] != entries[line["EventId"]]["Text"]:
                raise ValueError(f"Dialogue-r1 input differs from its catalog or fixed seed/facts: {line['EventId']}")
    expected_events = {"storm_incoming", "emitter_discovered", "low_oxygen"}
    if {line["EventId"] for line in prompts["Lines"]} != expected_events or len(prompts["Lines"]) != 3:
        raise ValueError("Expected exactly the three approved event lines.")
    if {style["Id"] for style in prompts["Styles"]} != {"neutral", "reassuring", "urgent"} or len(prompts["Styles"]) != 3:
        raise ValueError("Expected exactly Neutral, Reassuring and Urgent styles.")
    for line in prompts["Lines"]:
        if line["Facts"] != events[line["EventId"]]["facts"] or not line["Text"].strip():
            raise ValueError(f"Missing text or mismatched event facts: {line['EventId']}")

    import numpy as np
    import psutil
    import soundfile as sf
    import torch
    from qwen_tts import Qwen3TTSModel

    versions = {name: metadata.version(name) for name in
                ("qwen-tts", "transformers", "accelerate", "torch", "torchaudio", "soundfile", "numpy", "psutil")}
    provenance = json.loads(metadata.distribution("qwen-tts").read_text("direct_url.json") or "{}")
    if CODE_REVISION not in provenance.get("url", ""):
        raise ValueError("qwen-tts installation provenance does not identify the pinned official code revision.")
    for name, expected in {"qwen-tts": "0.1.1", "transformers": "4.57.3", "accelerate": "1.12.0",
                           "torch": "2.10.0", "torchaudio": "2.10.0"}.items():
        if versions[name].split("+")[0] != expected:
            raise ValueError(f"Expected {name} {expected}; found {versions[name]}.")
    torch.set_num_threads(4)
    if args.device == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA is unavailable. CPU fallback requires an explicit --device cpu run.")
    device = "cuda:0" if args.device == "cuda" else "cpu"
    dtype_name = "bfloat16" if args.device == "cuda" else "float32"
    attention = "sdpa" if args.device == "cuda" else "eager"
    process = psutil.Process()

    def peak_ram():
        info = process.memory_info()
        return getattr(info, "peak_wset", info.rss)

    def synchronize():
        if args.device == "cuda":
            torch.cuda.synchronize()

    def audio_info(path):
        samples, rate = sf.read(path, dtype="float32", always_2d=False)
        if rate <= 15000 or samples.ndim != 1 or samples.size == 0 or not np.isfinite(samples).all():
            raise ValueError(f"Invalid speech audio: {path}")
        peak = float(np.max(np.abs(samples)))
        if peak <= 1e-6:
            raise ValueError(f"Silent speech audio: {path}")
        return {"SampleRate": rate, "SampleCount": int(samples.size), "AudioSeconds": samples.size / rate,
                "Peak": peak, "Rms": float(np.sqrt(np.mean(samples.astype(np.float64) ** 2)))}

    output.mkdir(parents=True, exist_ok=True)
    progress_path = output / "progress.json"
    old = read_json(progress_path) if progress_path.exists() else {}
    cached = {(c.get("StyleId"), c.get("EventId")): c for c in old.get("Clips", [])}
    for name in ("raw_manifest.json", "manifest.json", "index.html"):
        (output / name).unlink(missing_ok=True)
    run = {
        "GeneratedUtc": utc_now(), "Complete": False, "Smoke": args.smoke,
        "ModelId": MODEL_ID, "ModelRevision": MODEL_REVISION, "CodeRevision": CODE_REVISION,
        "PackageVersions": versions, "CodeProvenance": provenance, "PythonVersion": sys.version, "Persona": prompts["Persona"],
        "Styles": prompts["Styles"], "Lines": prompts["Lines"], "GenerationSettings": FLAGS,
        "Language": "English", "Device": device, "Dtype": dtype_name, "Attention": attention,
        "CpuThreads": 4, "GpuName": torch.cuda.get_device_name(0) if args.device == "cuda" else None,
        "IdentityNote": "Expressive British female concept, not an Emma clone. Repeating persona and seed does not lock speaker identity.",
        "TimingNote": "No warm-up. First inference includes startup overhead. Descriptive standalone timings and process peaks, not game-performance acceptance. Cached clip synthesis timings come from the original render.",
        "ModelLoadedThisRun": False, "LoadSeconds": 0.0, "PeakProcessRamAfterLoadBytes": peak_ram(), "PeakProcessRamBytes": peak_ram(),
        "PeakCudaAllocatedBytes": 0, "PeakCudaReservedBytes": 0, "Clips": [],
    }
    if args.dialogue_r1:
        run.update(DialogueRevision="dialogue-r1", ConfigFile=config_file)
    atomic_json(progress_path, run)
    model = None
    if args.device == "cuda":
        torch.cuda.reset_peak_memory_stats()
    for style in prompts["Styles"]:
        for line in prompts["Lines"]:
            if args.smoke and line["EventId"] != "low_oxygen":
                continue
            prompt = prompts["Persona"] + " " + style["Prompt"]
            relative = f"raw/{style['Id']}/{line['EventId']}.wav"
            path = output / relative
            key_data = {"Text": line["Text"], "Facts": line["Facts"], "Prompt": prompt, "Seed": line["Seed"],
                        "ModelRevision": MODEL_REVISION, "CodeRevision": CODE_REVISION,
                        "Device": device, "Dtype": dtype_name, "Attention": attention,
                        "GenerationSettings": FLAGS, "Language": "English", "PackageVersions": versions}
            key = hashlib.sha256(json.dumps(key_data, sort_keys=True).encode()).hexdigest()
            previous = cached.get((style["Id"], line["EventId"]))
            if previous and previous.get("CacheKey") == key and previous.get("RawPath") == relative and path.is_file() and previous.get("Sha256") == file_hash(path):
                measured = audio_info(path)
                clip = dict(previous, Reused=True, **measured)
                print(f"Reused {style['Label']}/{line['EventId']}: {clip['AudioSeconds']:.2f}s", flush=True)
            else:
                if model is None:
                    print(f"Loading local model on {device} ({dtype_name}, {attention})...", flush=True)
                    synchronize()
                    start = time.perf_counter()
                    model = Qwen3TTSModel.from_pretrained(
                        str(MODEL), device_map=device,
                        dtype=torch.bfloat16 if args.device == "cuda" else torch.float32,
                        attn_implementation=attention, local_files_only=True)
                    synchronize()
                    run["LoadSeconds"] = time.perf_counter() - start
                    run["ModelLoadedThisRun"] = True
                    run["PeakProcessRamAfterLoadBytes"] = peak_ram()
                random.seed(line["Seed"])
                np.random.seed(line["Seed"])
                torch.manual_seed(line["Seed"])
                if args.device == "cuda":
                    torch.cuda.manual_seed_all(line["Seed"])
                print(f"Rendering {style['Label']}/{line['EventId']}...", flush=True)
                synchronize()
                start = time.perf_counter()
                with torch.inference_mode():
                    wavs, rate = model.generate_voice_design(
                        text=line["Text"], instruct=prompt, language="English", **FLAGS)
                synchronize()
                seconds = time.perf_counter() - start
                if len(wavs) != 1:
                    raise ValueError("Expected exactly one waveform from voice design.")
                samples = np.asarray(wavs[0], dtype=np.float32)
                if rate <= 15000 or samples.ndim != 1 or samples.size == 0 or not np.isfinite(samples).all() or float(np.max(np.abs(samples))) <= 1e-6:
                    raise ValueError("Synthesis returned empty, nonfinite or silent speech audio.")
                path.parent.mkdir(parents=True, exist_ok=True)
                # IEEE float WAV preserves peaks above full scale; do not clip or normalize the model output.
                sf.write(path, samples, rate, subtype="FLOAT")
                clip = {"StyleId": style["Id"], "EventId": line["EventId"], "Text": line["Text"],
                        "Facts": line["Facts"], "Prompt": prompt, "Seed": line["Seed"], "RawPath": relative,
                        "CacheKey": key, "Sha256": file_hash(path), "SynthSeconds": seconds,
                        "RenderedUtc": utc_now(), "Reused": False, **audio_info(path)}
                print(f"Rendered {style['Label']}/{line['EventId']}: {clip['AudioSeconds']:.2f}s in {seconds:.2f}s", flush=True)
            run["Clips"].append(clip)
            run["PeakProcessRamBytes"] = peak_ram()
            if args.device == "cuda":
                run["PeakCudaAllocatedBytes"] = torch.cuda.max_memory_allocated()
                run["PeakCudaReservedBytes"] = torch.cuda.max_memory_reserved()
            atomic_json(progress_path, run)
    if not args.smoke:
        if len(run["Clips"]) != 9:
            raise ValueError("Full audition requires nine successful clips.")
        run["Complete"] = True
        atomic_json(progress_path, run)
        atomic_json(output / "raw_manifest.json", run)
    print("Smoke complete; no full report published." if args.smoke else "All nine raw clips complete.", flush=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"Expressive audition failed: {type(error).__name__}: {error}", file=sys.stderr, flush=True)
        sys.exit(2)
