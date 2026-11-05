"""Loopback-only audio studio controller. Models run in isolated subprocesses."""
from __future__ import annotations

import hashlib
import json
import logging
import os
from pathlib import Path
import queue
import re
import secrets
import sys
import subprocess
import threading
import time
from contextlib import asynccontextmanager, contextmanager
from datetime import datetime, timezone
from uuid import UUID, uuid4
from typing import Literal

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
ARTIFACTS = ROOT / "artifacts" / "audio-studio"
OUTPUT = HERE / "output"
MANIFEST = ARTIFACTS / "model-manifest.json"
SEQUENCE = ARTIFACTS / "generation-sequence.json"
NATIVE_MANIFEST = ARTIFACTS / "native-runtime.json"
NATIVE_PYTHON = ARTIFACTS / "venv-stable-audio-tools" / "Scripts" / "python.exe"
NATIVE_T5 = ARTIFACTS / "models" / "stable-audio-open-native" / "t5-base"
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

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, ConfigDict, Field
import psutil

log = logging.getLogger("audio-studio")
LICENSE_NOTE = "Stable Audio Open: Stability AI Community License; review its terms for your use. ACE-Step 1.5: MIT."
jobs: dict[str, dict] = {}
lock = threading.RLock()
pending: queue.Queue = queue.Queue(maxsize=8)
stopping = threading.Event()
model_loaded = False
accepting_jobs = True


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def persist(job):
    folder = OUTPUT / job["id"]
    folder.mkdir(parents=True, exist_ok=True)
    temp = folder / "metadata.tmp"
    temp.write_text(json.dumps(job, indent=2, allow_nan=False), encoding="utf-8")
    temp.replace(folder / "metadata.json")


def update(job, **fields):
    with lock:
        job.update(fields, updated_at=utc_now())
        persist(job)


def generation_number(value):
    if not isinstance(value, str) or not re.fullmatch(r"GEN-[0-9]{4,}", value):
        raise ValueError(f"Invalid generation ID: {value!r}")
    number = int(value[4:])
    if number < 1 or value != f"GEN-{number:04d}":
        raise ValueError(f"Invalid generation ID: {value!r}")
    return number


def read_sequence():
    if not SEQUENCE.exists():
        return 0
    record = json.loads(SEQUENCE.read_text(encoding="utf-8"))
    value = record.get("last_reserved") if isinstance(record, dict) else None
    if type(value) is not int or value < 0:
        raise ValueError("Invalid generation-sequence.json: last_reserved must be a nonnegative integer")
    return value


def write_sequence(value):
    SEQUENCE.parent.mkdir(parents=True, exist_ok=True)
    temp = SEQUENCE.with_suffix(".tmp")
    temp.write_text(json.dumps({"last_reserved": value}, indent=2), encoding="utf-8")
    temp.replace(SEQUENCE)


def reserve_generation_id():
    with lock:
        number = read_sequence() + 1
        # Reserve durably before metadata publication. Interrupted reservations leave gaps.
        write_sequence(number)
        return f"GEN-{number:04d}"


def outputs_with_take_ids(generation_id, outputs):
    generation_number(generation_id)
    result, seen = [], set()
    for output in outputs:
        variation = output.get("variation")
        if type(variation) is not int or variation < 1 or variation in seen:
            raise ValueError(f"Invalid or duplicate variation for {generation_id}: {variation!r}")
        seen.add(variation)
        take_id = f"{generation_id}-{variation:02d}"
        if "take_id" in output and output["take_id"] != take_id:
            raise ValueError(f"Invalid existing take ID for {generation_id}: {output['take_id']!r}")
        result.append({**output, "take_id": take_id})
    return result


def initialize_history():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    loaded = {}
    for path in OUTPUT.glob("*/metadata.json"):
        try:
            job = json.loads(path.read_text(encoding="utf-8"))
            if str(UUID(job["id"])) != path.parent.name:
                continue
            # Require the same basic fields consumed by the history UI.
            job["created_at"], job["status"], job["outputs"]
            loaded[job["id"]] = job
        except (ValueError, KeyError, OSError):
            log.warning("Skipping unreadable job metadata: %s", path)
    with lock:
        counter = read_sequence()
        assigned = {}
        # Validate all assigned identifiers before reserving or rewriting anything.
        for job in loaded.values():
            if "generation_id" in job:
                number = generation_number(job["generation_id"])
                if number in assigned:
                    raise ValueError(f"Duplicate generation ID {job['generation_id']}: {assigned[number]} and {job['id']}")
                assigned[number] = job["id"]
                outputs_with_take_ids(job["generation_id"], job["outputs"])
        write_sequence(max(counter, max(assigned, default=0)))
        for job in sorted(loaded.values(), key=lambda item: (item["created_at"], item["id"])):
            before = json.dumps(job, sort_keys=True)
            if "generation_id" not in job:
                job["generation_id"] = reserve_generation_id()
            job["outputs"] = outputs_with_take_ids(job["generation_id"], job["outputs"])
            if job["status"] not in ("succeeded", "failed"):
                job.update(status="failed", message="Interrupted by a previous server shutdown", error="Server restarted")
            if json.dumps(job, sort_keys=True) != before:
                persist(job)
        jobs.clear()
        jobs.update(loaded)


def read_manifest():
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8-sig"))
    if manifest.get("schema_version") != 1 or not isinstance(manifest.get("models"), dict):
        raise ValueError("Invalid model manifest; run setup.ps1")
    return manifest


def backend_status(manifest, engine):
    model = manifest.get("models", {}).get(engine)
    if not model:
        return {"ready": False, "message": "Weights not downloaded. Stable Audio Open requires Hugging Face license access." if engine == "stable-audio-open" else "Run setup.ps1 to download ACE-Step."}
    path = Path(model.get("model_path", ""))
    if not path.is_absolute():
        path = ROOT / path
    required = [path / "model_index.json"] if engine == "stable-audio-open" else [
        path / "acestep-v15-turbo" / "config.json", path / "vae" / "config.json",
        path / "Qwen3-Embedding-0.6B" / "config.json"]
    ready = path.is_dir() and all(item.is_file() for item in required)
    return {"ready": ready, "message": "Ready" if ready else "Model files incomplete; rerun setup.ps1",
            "model": model}


def local_path(value):
    path = Path(value)
    return path.resolve() if path.is_absolute() else (ROOT / path).resolve()


def read_native_manifest():
    record = json.loads(NATIVE_MANIFEST.read_text(encoding="utf-8-sig"))
    if not isinstance(record, dict) or record.get("schema_version") != 1:
        raise ValueError("Native runtime manifest requires schema_version 1")
    for name in ("source_revision", "source_path", "python_path", "model"):
        if not record.get(name):
            raise ValueError(f"Native runtime manifest is missing {name}")
    if local_path(record["python_path"]) != NATIVE_PYTHON.resolve():
        raise ValueError("Native runtime must use the dedicated local Python environment")
    model = record["model"]
    if not isinstance(model, dict):
        raise ValueError("Native model manifest must be an object")
    for name in ("model_id", "model_revision", "model_path", "license", "files"):
        if not model.get(name):
            raise ValueError(f"Native model manifest is missing {name}")
    model_path = local_path(model["model_path"])
    if not isinstance(model["files"], list):
        raise ValueError("Native model files must be a list")
    for entry in model["files"]:
        if not isinstance(entry, dict) or not isinstance(entry.get("path"), str) or type(entry.get("size")) is not int or entry["size"] <= 0:
            raise ValueError("Invalid native model file record")
        path = (model_path / entry["path"]).resolve()
        if not path.is_relative_to(model_path) or not path.is_file() or path.stat().st_size != entry["size"]:
            raise ValueError(f"Native model file missing or incomplete: {entry['path']}")
    required = [NATIVE_PYTHON, local_path(record["source_path"]) / "stable_audio_tools" / "models" / "factory.py",
                model_path / "model_config.json", model_path / "model.safetensors",
                NATIVE_T5 / "config.json", NATIVE_T5 / "model.safetensors", NATIVE_T5 / "tokenizer_config.json"]
    if not all(path.is_file() and path.stat().st_size > 0 for path in required):
        raise ValueError("Native backend files are incomplete; run setup-native.ps1")
    if not any((NATIVE_T5 / name).is_file() for name in ("tokenizer.json", "spiece.model")):
        raise ValueError("Native local T5 tokenizer files are missing")
    return record


def native_status():
    try:
        record = read_native_manifest()
        return {"ready": True, "message": "Ready", "model": record["model"]}
    except (OSError, ValueError, TypeError, KeyError) as exc:
        return {"ready": False, "message": f"Native backend unavailable: {exc}"}


def worker_python(job):
    if job["settings"].get("backend", "diffusers") == "stable-audio-tools":
        if job["settings"]["engine"] != "stable-audio-open":
            raise ValueError("Native Stable Audio backend cannot run ACE-Step jobs")
        return str(NATIVE_PYTHON.resolve())
    return sys.executable


def queue_command(action, label, timeout=30):
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
         str(ROOT / "tools" / "gpu_queue.ps1"), f"-{action}", "-Label", label],
        capture_output=True, text=True, timeout=timeout,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    if result.returncode:
        raise RuntimeError(f"GPU queue {action} failed: {result.stderr or result.stdout}")
    return result.stdout


@contextmanager
def gpu_ticket(job):
    label = f"audio-studio-{job['id']}"
    done = threading.Event()
    heartbeat_errors = []

    def heartbeat():
        while not done.wait(300):
            try:
                queue_command("Heartbeat", label)
            except Exception as exc:
                heartbeat_errors.append(str(exc))
                log.exception("GPU queue heartbeat failed")

    thread = threading.Thread(target=heartbeat, daemon=True)
    thread.start()
    try:
        update(job, status="waiting_gpu", message="Waiting for the shared GPU queue")
        queue_command("Acquire", label, timeout=181 * 60)
        # The shared script consumes its acquisition message in an if-condition;
        # verify ownership from the live ticket head, including on timeout exit 0.
        cutoff = time.time() - 15 * 60
        ticket_dir = ROOT / ".godot" / "gpu_queue"
        live_tickets = sorted(
            (ticket for ticket in ticket_dir.glob("*.ticket") if ticket.stat().st_mtime > cutoff),
            key=lambda ticket: ticket.name,
        )
        if not live_tickets or not live_tickets[0].name.endswith(f"_{label}.ticket"):
            raise RuntimeError("GPU queue did not confirm acquisition")
        yield
        if heartbeat_errors:
            raise RuntimeError("GPU queue heartbeat failed: " + heartbeat_errors[-1])
    finally:
        done.set()
        thread.join(timeout=35)
        queue_command("Release", label)


def worker_failure_message(returncode, stderr):
    code_hex = f"0x{returncode & 0xFFFFFFFF:08X}"
    clean = re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", stderr)
    lines = [line.strip() for line in clean.splitlines() if line.strip()]
    # Keep the final exception line rather than progress bars or a raw traceback.
    exceptions = [line for line in lines
                  if re.match(r"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*:\s*\S", line)
                  and not re.search(r"\d+%\|.*\||\d+/\d+.*(?:it/s|s/it)", line)]
    detail = exceptions[-1][:300] if exceptions else "no Python traceback"
    if returncode & 0xFFFFFFFF == 0xC00000FD:
        detail = "Windows stack overflow; " + detail
    return f"Generation worker exited {returncode} ({code_hex}): {detail}. See worker log."


def generate(job):
    global model_loaded
    started = time.monotonic()
    with gpu_ticket(job):
        folder = OUTPUT / job["id"]
        result_path = folder / "worker-result.json"
        update(job, status="generating", message="Loading model and generating audio; see worker logs for detailed progress")
        try:
            model_loaded = True
            with (folder / "worker-stdout.log").open("w", encoding="utf-8") as stdout, (folder / "worker-stderr.log").open("w", encoding="utf-8") as stderr:
                process = subprocess.run(
                    [worker_python(job), str(HERE / "generate.py"), "--job", str(folder / "metadata.json"),
                     "--owner-pid", str(os.getpid()), "--owner-created", str(psutil.Process().create_time())],
                    stdout=stdout, stderr=stderr, cwd=ROOT,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
            update(job, worker_exit_code=process.returncode,
                   worker_exit_code_hex=f"0x{process.returncode & 0xFFFFFFFF:08X}")
            if process.returncode:
                tail = (folder / "worker-stderr.log").read_text(encoding="utf-8", errors="replace")[-5000:]
                raise RuntimeError(worker_failure_message(process.returncode, tail))
            result = json.loads(result_path.read_text(encoding="utf-8"))
            outputs = result.get("outputs", [])
            if len(outputs) != job["settings"]["variants"]:
                raise RuntimeError("Worker returned an unexpected variation count")
            for output in outputs:
                for fmt in ("wav", "ogg"):
                    file = (folder / output[fmt]).resolve()
                    if file.parent != folder.resolve() or file.suffix != f".{fmt}" or not file.is_file():
                        raise RuntimeError("Worker returned an invalid audio file")
                    if hashlib.sha256(file.read_bytes()).hexdigest() != output["sha256"][fmt]:
                        raise RuntimeError("Worker audio hash verification failed")
            outputs = outputs_with_take_ids(job["generation_id"], outputs)
            update(job, outputs=outputs, model=result["model"], provenance=result["provenance"],
                   device=result["device"], peak_vram_bytes=result["peak_vram_bytes"],
                   worker_elapsed_seconds=result["elapsed_seconds"])
        finally:
            # The worker has exited; its CUDA context and model allocations are gone.
            model_loaded = False
    update(job, status="succeeded", message="Ready to audition", completed_at=utc_now(),
           elapsed_seconds=round(time.monotonic() - started, 3))


def worker():
    while not stopping.is_set():
        try:
            job = pending.get(timeout=0.5)
        except queue.Empty:
            continue
        try:
            generate(job)
        except Exception as exc:
            log.exception("Job %s failed", job["id"])
            update(job, status="failed", message=str(exc), error=str(exc), completed_at=utc_now())
        finally:
            pending.task_done()


@asynccontextmanager
async def lifespan(app):
    initialize_history()
    stopping.clear()
    thread = threading.Thread(target=worker, name="audio-studio-worker", daemon=True)
    thread.start()
    yield
    stopping.set()
    # Let in-flight inference finish so its finally block releases the GPU ticket.
    thread.join()


app = FastAPI(title="Ringworld Audio Studio", lifespan=lifespan)


@app.middleware("http")
async def local_only(request: Request, call_next):
    if request.headers.get("host", "").lower() not in ("localhost:7861", "127.0.0.1:7861"):
        return JSONResponse({"detail": "Only the loopback studio host is allowed"}, status_code=403)
    origin = request.headers.get("origin")
    if origin:
        if origin not in ("http://localhost:7861", "http://127.0.0.1:7861") or origin.split("://", 1)[-1].lower() != request.headers["host"].lower():
            return JSONResponse({"detail": "Cross-origin requests are not allowed"}, status_code=403)
    response = await call_next(request)
    response.headers["X-Content-Type-Options"] = "nosniff"
    response.headers["X-Frame-Options"] = "DENY"
    response.headers["Referrer-Policy"] = "no-referrer"
    return response


class JobRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    engine: Literal["stable-audio-open", "ace-step"] = "stable-audio-open"
    backend: Literal["diffusers", "stable-audio-tools"] = "diffusers"
    prompt: str = Field(min_length=1, max_length=2000)
    duration: float | None = Field(default=None, ge=0.5, le=120)
    variants: int = Field(default=1, ge=1, le=4)
    seed: int | None = Field(default=None, ge=0, le=2 ** 32 - 1)
    negative_prompt: str = Field(default="Low quality, distorted.", max_length=2000)
    steps: int | None = Field(default=None, ge=1, le=200)
    guidance_scale: float = Field(default=7, ge=1, le=20)
    bpm: int | None = Field(default=None, ge=30, le=300)


@app.get("/api/health")
def health():
    try:
        manifest = read_manifest()
        error = None
    except (OSError, ValueError) as exc:
        manifest, error = {}, str(exc)
    engines = {name: backend_status(manifest, name) for name in ("stable-audio-open", "ace-step")}
    native = native_status()
    with lock:
        active_jobs = sum(job["status"] not in ("succeeded", "failed") for job in jobs.values())
    return {"app": "ringworld-audio-studio", "pid": os.getpid(),
            "status": "ok" if native["ready"] or any(item["ready"] for item in engines.values()) else "setup_required",
            "model_loaded": model_loaded, "busy": active_jobs > 0, "active_jobs": active_jobs,
            "accepting_jobs": accepting_jobs, "engines": engines, "setup_error": error,
            "stable_backends": {"diffusers": engines["stable-audio-open"], "stable-audio-tools": native},
            "queue_waiting": pending.qsize(), "queue_capacity": pending.maxsize, "license_note": LICENSE_NOTE}


@app.post("/api/jobs", status_code=202)
def create_job(body: JobRequest):
    if not body.prompt.strip():
        raise HTTPException(422, "Prompt must contain text")
    native = None
    if body.backend == "stable-audio-tools":
        if body.engine != "stable-audio-open":
            raise HTTPException(422, "stable-audio-tools is available only for Stable Audio Open")
        try:
            native = read_native_manifest()
        except (OSError, ValueError, TypeError, KeyError) as exc:
            raise HTTPException(503, f"Run setup-native.ps1 first: {exc}") from exc
        manifest = {}
        status = {"ready": True, "model": native["model"]}
    else:
        try:
            manifest = read_manifest()
        except (OSError, ValueError) as exc:
            raise HTTPException(503, f"Run setup.ps1 first: {exc}") from exc
        status = backend_status(manifest, body.engine)
    if not status["ready"]:
        raise HTTPException(503, status["message"])
    stable = body.engine == "stable-audio-open"
    duration = body.duration if body.duration is not None else (5 if stable else 30)
    if not ((0.5 <= duration <= 47) if stable else (10 <= duration <= 120)):
        raise HTTPException(422, "Stable Audio Open duration must be 0.5-47 seconds; ACE-Step must be 10-120 seconds")
    if not stable and len(body.prompt) > 512:
        raise HTTPException(422, "ACE-Step music captions must be at most 512 characters")
    if not stable and body.steps not in (None, 8):
        raise HTTPException(422, "ACE-Step turbo uses 8 inference steps")
    settings = body.model_dump()
    settings["duration"] = duration
    settings["steps"] = body.steps if body.steps is not None else (50 if stable else 8)
    if not stable:
        settings.update(instrumental=True, lyrics="[Instrumental]", thinking=False,
                        guidance_scale=7, negative_prompt="")
    settings["seed"] = body.seed if body.seed is not None else secrets.randbits(32)
    job_id = str(uuid4())
    job = {"id": job_id, "status": "queued", "message": "Queued", "created_at": utc_now(),
           "updated_at": utc_now(), "settings": settings, "outputs": [], "license_note": LICENSE_NOTE,
           "metadata_url": f"/api/jobs/{job_id}/files/metadata.json",
           "model": status["model"], "source_revision": manifest.get("source_revision"),
           "source_path": manifest.get("source_path")}
    job["backend"] = body.backend
    if native:
        job.update(native_source_revision=native["source_revision"], native_source_path=native["source_path"])
    with lock:
        if not accepting_jobs:
            raise HTTPException(503, "Server is stopping; restart it before submitting another job")
        if pending.full():
            raise HTTPException(429, "Queue is full; wait for a job to finish", headers={"Retry-After": "30"})
        job["generation_id"] = reserve_generation_id()
        persist(job)
        jobs[job_id] = job
        pending.put_nowait(job)
        return json.loads(json.dumps(job))


@app.post("/api/prepare-stop")
def prepare_stop():
    global accepting_jobs
    with lock:
        if any(job["status"] not in ("succeeded", "failed") for job in jobs.values()):
            raise HTTPException(409, "Audio jobs are active or queued; wait for them to finish before stopping")
        accepting_jobs = False
        return {"ready": True, "pid": os.getpid()}


@app.get("/api/jobs")
def list_jobs():
    with lock:
        return json.loads(json.dumps({"jobs": sorted(jobs.values(), key=lambda job: job["created_at"], reverse=True)}))


@app.get("/api/jobs/{job_id}")
def get_job(job_id: str):
    with lock:
        if job_id not in jobs:
            raise HTTPException(404, "Job not found")
        return json.loads(json.dumps(jobs[job_id]))


@app.get("/api/jobs/{job_id}/files/{filename}")
def get_file(job_id: str, filename: str):
    job = get_job(job_id)
    allowed = {"metadata.json", "worker-stdout.log", "worker-stderr.log"}
    for output in job["outputs"]:
        allowed.update((output["wav"], output["ogg"]))
    folder = (OUTPUT / job_id).resolve()
    path = (folder / filename).resolve()
    if filename not in allowed or path.parent != folder or not folder.is_relative_to(OUTPUT.resolve()) or not path.is_file():
        raise HTTPException(404, "File not found")
    media = {".wav": "audio/wav", ".ogg": "audio/ogg", ".json": "application/json", ".log": "text/plain"}[path.suffix]
    return FileResponse(path, media_type=media, filename=filename)


app.mount("/", StaticFiles(directory=HERE / "web", html=True), name="web")
