"""One job per process: CUDA allocations are released when this worker exits."""
from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import shutil
import sys
import threading
import time
from uuid import UUID

import psutil

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts" / "audio-studio"
OUTPUT = Path(__file__).resolve().parent / "output"
for key, value in {
    "HF_HOME": str(ARTIFACTS / "cache" / "huggingface"),
    "TORCH_HOME": str(ARTIFACTS / "cache" / "torch"),
    "XDG_CACHE_HOME": str(ARTIFACTS / "cache"),
    "MPLCONFIGDIR": str(ARTIFACTS / "cache" / "matplotlib"),
    "NUMBA_CACHE_DIR": str(ARTIFACTS / "cache" / "numba"),
    "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1",
    "PYTHONIOENCODING": "utf-8",
}.items():
    os.environ.setdefault(key, value)


def resolve_path(value):
    path = Path(value)
    return path if path.is_absolute() else ROOT / path


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_ogg(path, samples, sample_rate):
    """Bound each native Vorbis write to avoid libsndfile's large-write stack overflow."""
    import soundfile as sf

    with sf.SoundFile(str(path), mode="w", samplerate=sample_rate, channels=samples.shape[1],
                      format="OGG", subtype="VORBIS") as output:
        for start in range(0, len(samples), 4096):
            output.write(samples[start:start + 4096])


def owner_watchdog(owner_pid, owner_created):
    """Fail closed if the controller disappears; never leave an orphan CUDA worker."""
    done = threading.Event()

    def ensure_owner():
        try:
            owner = psutil.Process(owner_pid)
            if not owner.is_running() or abs(owner.create_time() - owner_created) > 0.001:
                os._exit(70)
        except Exception:
            os._exit(70)

    # Check synchronously before importing Torch or touching the GPU.
    ensure_owner()

    def monitor():
        while not done.wait(1):
            ensure_owner()

    thread = threading.Thread(target=monitor, name="controller-watchdog", daemon=True)
    thread.start()
    return done, ensure_owner


def stable_backend(job):
    import torch
    from diffusers import StableAudioPipeline

    settings = job["settings"]
    pipe = StableAudioPipeline.from_pretrained(
        str(resolve_path(job["model"]["model_path"])),
        torch_dtype=torch.float16, local_files_only=True,
    )
    pipe.enable_model_cpu_offload()

    def render(seed):
        generator = torch.Generator("cuda").manual_seed(seed)
        with torch.inference_mode():
            result = pipe(prompt=settings["prompt"], negative_prompt=settings["negative_prompt"],
                          num_inference_steps=settings["steps"], audio_end_in_s=settings["duration"],
                          num_waveforms_per_prompt=1, guidance_scale=settings["guidance_scale"],
                          generator=generator)
        return result.audios[0].T.float().cpu().numpy().copy(), int(pipe.vae.sampling_rate)

    render.provenance = {"backend": "diffusers", "precision": "float16", "sampler": "StableAudioPipeline configured scheduler"}
    return render


def native_model_config(config, text_path):
    patched = copy.deepcopy(config)
    conditioners = patched["model"]["conditioning"]["configs"]
    text = [item for item in conditioners if item["type"] == "t5"]
    if len(text) != 1 or text[0]["config"].get("t5_model_name") != "t5-base":
        raise ValueError("Native Stable Audio Open requires its official t5-base conditioner")
    text[0]["config"]["model_path"] = str(text_path)
    return patched


def native_stable_backend(job):
    import gc
    import torch

    source = resolve_path(job["native_source_path"])
    if job["native_source_revision"] != "3241adba4fc2a85cf5b29d9eb68d42f40a28e820":
        raise ValueError("Native backend source revision differs from the pinned comparison implementation")
    text_path = ARTIFACTS / "models" / "stable-audio-open-native" / "t5-base"
    if not (text_path / "model.safetensors").is_file():
        raise ValueError("Native local T5 text model is missing; run setup-native.ps1")
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    sys.path.insert(0, str(source))
    from stable_audio_tools.models.factory import create_model_from_config
    from stable_audio_tools.models.utils import load_ckpt_state_dict
    from stable_audio_tools.inference.generation import generate_diffusion_cond

    folder = resolve_path(job["model"]["model_path"])
    original_config = json.loads((folder / "model_config.json").read_text(encoding="utf-8"))
    config = native_model_config(original_config, text_path)
    model = create_model_from_config(config)
    state = load_ckpt_state_dict(str(folder / "model.safetensors"))
    model.load_state_dict(state, strict=True)
    del state
    model.eval().requires_grad_(False).to(device="cpu", dtype=torch.float16)
    settings = job["settings"]
    sample_size = int(config["sample_size"])
    timing = {"seconds_start": 0, "seconds_total": settings["duration"]}
    positive = [{"prompt": settings["prompt"], **timing}]
    negative = [{"prompt": settings["negative_prompt"], **timing}]

    def clear_cuda():
        torch.cuda.synchronize()
        gc.collect()
        torch.cuda.empty_cache()

    conditioning_started = time.monotonic()
    model.conditioner.to("cuda")
    # Frozen T5 is unregistered and needs explicit movement with its conditioner.
    for conditioner in model.conditioner.conditioners.values():
        if hasattr(conditioner, "model"):
            conditioner.model.eval().requires_grad_(False).to(device="cuda", dtype=torch.float16)
    with torch.inference_mode(), torch.autocast("cuda", dtype=torch.float16):
        positive_tensors = model.conditioner(positive, "cuda")
        negative_tensors = model.conditioner(negative, "cuda")
    model.conditioner.to("cpu")
    for conditioner in model.conditioner.conditioners.values():
        if hasattr(conditioner, "model"):
            conditioner.model.to("cpu")
    clear_cuda()
    conditioning_seconds = time.monotonic() - conditioning_started

    def render(seed):
        diffusion_started = time.monotonic()
        model.model.to("cuda")
        with torch.inference_mode(), torch.autocast("cuda", dtype=torch.float16):
            latents = generate_diffusion_cond(
                model, steps=settings["steps"], cfg_scale=settings["guidance_scale"],
                conditioning=positive, negative_conditioning=negative,
                conditioning_tensors=positive_tensors, negative_conditioning_tensors=negative_tensors,
                batch_size=1, sample_size=sample_size, seed=seed, device="cuda",
                sampler_type="dpmpp-3m-sde", sigma_min=0.3, sigma_max=500,
                adapt_duration_to_conditioning=False, apg_scale=0.0, scale_phi=0.0, return_latents=True,
            )
        model.model.to("cpu")
        clear_cuda()
        diffusion_seconds = time.monotonic() - diffusion_started
        decode_started = time.monotonic()
        model.pretransform.to("cuda")
        with torch.inference_mode(), torch.autocast("cuda", dtype=torch.float16):
            audio = model.pretransform.decode(latents.to(next(model.pretransform.parameters()).dtype))
        samples = audio[0].T.float().cpu().numpy().copy()
        del audio, latents
        model.pretransform.to("cpu")
        clear_cuda()
        render.provenance["stage_timings"].append({
            "seed": seed, "diffusion_with_transfers_seconds": round(diffusion_seconds, 3),
            "decode_with_transfers_seconds": round(time.monotonic() - decode_started, 3),
        })
        return samples, int(config["sample_rate"])

    files = ("models/factory.py", "models/conditioners.py", "inference/generation.py", "inference/sampling.py")
    render.provenance = {
        "backend": "stable-audio-tools", "implementation_version": "0.0.20",
        "source_revision": job["native_source_revision"],
        "source_files_sha256": {name: sha256(source / "stable_audio_tools" / name) for name in files},
        "model_config_sha256": sha256(folder / "model_config.json"),
        "precision": "CUDA float16 model and official frozen T5; float16 autocast",
        "sampler": "dpmpp-3m-sde", "sigma_min": 0.3, "sigma_max": 500,
        "apg_scale": 0.0, "apg_reason": "Disable recent upstream APG default for vanilla classifier-free guidance comparison",
        "cfg_rescale_scale_phi": 0.0,
        "cfg_rescale_note": "Official helper passes rescale_cfg=True, but DiTWrapper ignores that boolean; scale_phi=0 disables the actual DiT rescale.",
        "cpu_offload": True, "checkpoint_load_strict": True,
        "stage_policy": "CPU float16 model creation; GPU text conditioning once per job; text encoder including unregistered T5 offloaded to CPU; GPU DiT diffusion with VAE on CPU; DiT offloaded before GPU VAE decode; VAE offloaded after CPU audio copy. Raw positive/negative conditioning tensors reused between variations. All inference runs on CUDA.",
        "conditioning_with_transfers_seconds": round(conditioning_seconds, 3),
        "stage_timings": [],
        "sample_size": sample_size, "adapt_duration_to_conditioning": False,
        "seed_policy": "Explicit per-variation seed passed to generate_diffusion_cond",
        "conditioning": {"seconds_start": 0, "seconds_total": settings["duration"]},
        "text_encoder_path": str(text_path),
    }
    return render


def ace_backend(job, scratch):
    source = resolve_path(job["source_path"])
    if not (source / "acestep" / "handler.py").is_file():
        raise RuntimeError("Pinned ACE-Step source is missing; rerun setup.ps1")
    os.environ["ACESTEP_CHECKPOINTS_DIR"] = str(resolve_path(job["model"]["model_path"]))
    sys.path.insert(0, str(source))
    # Upstream can fall back to ModelScope; never download during an inference job.
    import acestep.model_downloader as downloader

    def no_download(*args, **kwargs):
        raise RuntimeError("Required ACE-Step model files are missing. Run setup.ps1; inference downloads are disabled.")

    downloader._smart_download = no_download
    from acestep.handler import AceStepHandler
    from acestep.llm_inference import LLMHandler
    from acestep.inference import GenerationConfig, GenerationParams, generate_music

    dit = AceStepHandler()
    status, success = dit.initialize_service(
        project_root=str(source), config_path="acestep-v15-turbo", device="cuda",
        use_flash_attention=False, compile_model=False, offload_to_cpu=True,
        offload_dit_to_cpu=False, quantization=None,
    )
    if not success:
        raise RuntimeError(f"ACE-Step initialization failed: {status}")
    llm = LLMHandler()  # Thinking is disabled: no language-model weights are loaded.
    settings = job["settings"]

    def render(seed):
        params = GenerationParams(
            caption=settings["prompt"], duration=settings["duration"],
            instrumental=True, lyrics="[Instrumental]", thinking=False,
            inference_steps=8, seed=seed, bpm=settings.get("bpm"),
            enable_normalization=False,
            use_cot_metas=False, use_cot_caption=False, use_cot_language=False,
        )
        config = GenerationConfig(batch_size=1, use_random_seed=False, seeds=[seed], audio_format="wav")
        result = generate_music(dit, llm, params, config, save_dir=str(scratch))
        if not result.success or not result.audios:
            raise RuntimeError(f"ACE-Step generation failed: {result.error or result.status_message}")
        audio = result.audios[0]
        tensor = audio.get("tensor")
        if tensor is not None:
            return tensor.detach().float().cpu().numpy().T.copy(), int(audio["sample_rate"])
        import soundfile as sf
        return sf.read(audio["path"], dtype="float32", always_2d=True)

    return render


def run(job_path, ensure_owner):
    import numpy as np
    import soundfile as sf
    import torch

    started = time.monotonic()
    job_path = job_path.resolve()
    folder = job_path.parent
    output_root = OUTPUT.resolve()
    if job_path.name != "metadata.json" or folder.parent != output_root:
        raise ValueError("Worker job path must be output/<uuid>/metadata.json")
    try:
        valid_id = str(UUID(folder.name))
    except ValueError as exc:
        raise ValueError("Worker output directory must be a canonical UUID") from exc
    if folder.name != valid_id:
        raise ValueError("Worker output directory must be a canonical UUID")
    job = json.loads(job_path.read_text(encoding="utf-8"))
    if job.get("id") != valid_id:
        raise ValueError("Job metadata ID does not match its output directory")
    settings = job["settings"]
    scratch = (folder / "worker-temp").resolve()
    if scratch.parent != folder or scratch.name != "worker-temp":
        raise ValueError("Worker scratch path escapes its output directory")
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is required. Check the NVIDIA driver and the installed CUDA PyTorch build.")
    torch.cuda.reset_peak_memory_stats()
    scratch.mkdir(exist_ok=True)
    try:
        ensure_owner()
        if settings["engine"] == "stable-audio-open":
            render = native_stable_backend(job) if settings.get("backend", "diffusers") == "stable-audio-tools" else stable_backend(job)
        else:
            render = ace_backend(job, scratch)
        load_seconds = time.monotonic() - started
        outputs = []
        for index in range(settings["variants"]):
            seed = (settings["seed"] + index) % (2 ** 32)
            print(f"Generating variation {index + 1}/{settings['variants']}, seed {seed}", flush=True)
            variation_started = time.monotonic()
            torch.manual_seed(seed)
            torch.cuda.manual_seed_all(seed)
            ensure_owner()
            samples, sample_rate = render(seed)
            samples = np.asarray(samples, dtype=np.float32)
            if samples.ndim == 1:
                samples = samples[:, None]
            if samples.ndim != 2 or not samples.size or samples.shape[1] not in (1, 2):
                raise RuntimeError(f"Unexpected audio shape: {samples.shape}")
            if not np.isfinite(samples).all():
                raise RuntimeError("Model generated non-finite audio")
            # Some diffusion backends decode padded audio. Keep the requested clip length.
            requested_frames = round(settings["duration"] * sample_rate)
            if sample_rate <= 0 or len(samples) < requested_frames - max(1, round(sample_rate * 0.001)):
                raise RuntimeError(f"Model returned short audio: {len(samples)} frames; expected at least {requested_frames} at {sample_rate} Hz")
            samples = samples[:requested_frames].copy()
            peak = float(np.max(np.abs(samples)))
            if peak <= 1e-7:
                raise RuntimeError("Model returned silent audio; no usable signal was generated")
            gain = min(1.0, 0.999 / peak) if peak else 1.0
            samples *= gain
            stem = f"variation-{index + 1:02d}-seed-{seed}"
            wav, ogg = folder / f"{stem}.wav", folder / f"{stem}.ogg"
            sf.write(str(wav), samples, sample_rate, format="WAV", subtype="PCM_16")
            write_ogg(ogg, samples, sample_rate)
            outputs.append({
                "variation": index + 1, "seed": seed, "wav": wav.name, "ogg": ogg.name,
                "sample_rate": sample_rate, "channels": int(samples.shape[1]), "frames": len(samples),
                "duration_seconds": len(samples) / sample_rate,
                "generation_seconds": round(time.monotonic() - variation_started, 3),
                "metrics": {"raw_peak": peak, "gain": gain, "peak": float(np.max(np.abs(samples))),
                            "rms": float(np.sqrt(np.mean(samples.astype(np.float64) ** 2))),
                            "clipped_samples": int(np.count_nonzero(np.abs(samples) >= 1))},
                "sha256": {"wav": sha256(wav), "ogg": sha256(ogg)},
            })
            del samples
        packages = {}
        for name in ("torch", "torchaudio", "diffusers", "stable-audio-tools", "transformers", "accelerate", "numpy", "soundfile"):
            try:
                packages[name] = importlib.metadata.version(name)
            except importlib.metadata.PackageNotFoundError:
                packages[name] = "not installed"
        result = {
            "outputs": outputs, "model": job["model"], "device": torch.cuda.get_device_name(0),
            "elapsed_seconds": round(time.monotonic() - started, 3),
            "peak_vram_bytes": int(torch.cuda.max_memory_allocated()),
            "provenance": {"packages": packages, "python": sys.version, "cuda": torch.version.cuda,
                           "backend": settings.get("backend", "diffusers") if settings["engine"] == "stable-audio-open" else "ace-step",
                           "implementation": getattr(render, "provenance", {"backend": "ace-step"}),
                           "source_revision": job.get("source_revision"),
                           "worker_sha256": sha256(Path(__file__)),
                           "server_sha256": sha256(Path(__file__).with_name("server.py")),
                           "model_load_seconds": round(load_seconds, 3),
                           "postprocessing": "Trim decoded padding to requested duration; gain attenuation only to cap peak at 0.999; PCM16 WAV and Vorbis OGG. No loudness normalization."},
        }
        temp = folder / "worker-result.tmp"
        temp.write_text(json.dumps(result, indent=2, allow_nan=False), encoding="utf-8")
        temp.replace(folder / "worker-result.json")
    finally:
        resolved_scratch = scratch.resolve()
        if resolved_scratch.parent != folder or resolved_scratch.name != "worker-temp" or folder.parent != output_root:
            raise RuntimeError("Refusing cleanup outside the job scratch directory")
        shutil.rmtree(resolved_scratch)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--job", required=True, type=Path)
    parser.add_argument("--owner-pid", required=True, type=int)
    parser.add_argument("--owner-created", required=True, type=float)
    arguments = parser.parse_args()
    done, ensure_owner = owner_watchdog(arguments.owner_pid, arguments.owner_created)
    try:
        run(arguments.job.resolve(), ensure_owner)
    finally:
        done.set()
