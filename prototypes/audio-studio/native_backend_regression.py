"""CPU-only native readiness, dispatch, and local text configuration regression."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def require(value, message):
    if not value:
        raise AssertionError(message)


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def run():
    from fastapi import HTTPException

    server = load("native_studio_server", "server.py")
    worker = load("native_studio_worker", "generate.py")
    parent = ROOT / "artifacts" / "audio-studio" / "regression"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="native-backend-", dir=parent) as name:
        scratch = Path(name).resolve()
        require(scratch.parent == parent.resolve(), "Unsafe regression scratch")
        server.NATIVE_MANIFEST = scratch / "native-runtime.json"
        server.NATIVE_PYTHON = scratch / "venv-stable-audio-tools" / "Scripts" / "python.exe"
        server.NATIVE_T5 = scratch / "models" / "native" / "t5-base"
        server.OUTPUT = scratch / "output"
        server.SEQUENCE = scratch / "generation-sequence.json"
        source = scratch / "source"
        model = server.NATIVE_T5.parent
        require(not server.native_status()["ready"], "Missing native runtime incorrectly ready")
        paths = [server.NATIVE_PYTHON, source / "stable_audio_tools" / "models" / "factory.py",
                 model / "model_config.json", model / "model.safetensors",
                 server.NATIVE_T5 / "config.json", server.NATIVE_T5 / "model.safetensors",
                 server.NATIVE_T5 / "tokenizer_config.json", server.NATIVE_T5 / "spiece.model"]
        for path in paths:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"fixture")
        record = {"schema_version": 1, "source_revision": "3241adba4fc2a85cf5b29d9eb68d42f40a28e820",
                  "source_path": str(source), "python_path": str(server.NATIVE_PYTHON),
                  "model": {"model_id": "stabilityai/stable-audio-open-1.0", "model_revision": "fixture-revision",
                            "model_path": str(model), "license": "Stability AI Community License",
                            "files": [{"path": "model.safetensors", "size": 7, "sha256": "fixture"}]}}
        def save():
            server.NATIVE_MANIFEST.write_text(json.dumps(record), encoding="utf-8")
        save()
        require(server.native_status()["ready"], "Complete native fixture not ready")
        original_python = record["python_path"]
        record["python_path"] = str(scratch / "untrusted.exe")
        save()
        require(not server.native_status()["ready"], "Manifest selected an arbitrary executable")
        record["python_path"] = original_python
        save()
        (model / "model.safetensors").write_bytes(b"partial")
        # A differently sized incomplete download must not report readiness.
        (model / "model.safetensors").write_bytes(b"short")
        require(not server.native_status()["ready"], "Incomplete model incorrectly ready")
        (model / "model.safetensors").write_bytes(b"fixture")
        server.NATIVE_T5.joinpath("spiece.model").unlink()
        require(not server.native_status()["ready"], "Missing local tokenizer incorrectly ready")
        server.NATIVE_T5.joinpath("spiece.model").write_bytes(b"fixture")
        job = server.create_job(server.JobRequest(engine="stable-audio-open", backend="stable-audio-tools", prompt="test", seed=42))
        require(job["settings"]["backend"] == "stable-audio-tools" and job["model"] == record["model"], "Native job lost backend/model snapshot")
        require(job["native_source_revision"] == record["source_revision"] and job["generation_id"] == "GEN-0001", "Native job lost source/GEN ID")
        require(server.worker_python(job) == str(server.NATIVE_PYTHON.resolve()), "Native job used wrong worker runtime")
        require(server.worker_python({"settings": {"engine": "stable-audio-open"}}) == sys.executable, "Legacy jobs changed runtime")
        require(server.JobRequest(prompt="test").backend == "diffusers", "Default backend changed")
        try:
            server.create_job(server.JobRequest(engine="ace-step", backend="stable-audio-tools", prompt="test"))
        except HTTPException as exc:
            require(exc.status_code == 422, "ACE native validation returned wrong status")
        else:
            raise AssertionError("ACE accepted native Stable backend")
        config = {"sample_size": 2097152, "model": {"conditioning": {"configs": [
            {"id": "prompt", "type": "t5", "config": {"t5_model_name": "t5-base", "max_length": 128}},
            {"id": "seconds_total", "type": "number", "config": {"min_val": 0, "max_val": 512}}]}}}
        original = copy.deepcopy(config)
        patched = worker.native_model_config(config, server.NATIVE_T5)
        require(config == original, "Native config patch mutated the original config")
        t5 = patched["model"]["conditioning"]["configs"][0]["config"]
        require(t5["model_path"] == str(server.NATIVE_T5) and t5["t5_model_name"] == "t5-base", "Native local text model patch incorrect")
        require(patched["sample_size"] == 2097152, "Native inference domain was shortened")
        require("torch" not in sys.modules and "stable_audio_tools" not in sys.modules, "CPU regression imported model runtime")
    print("AUDIO_STUDIO_NATIVE=PASS", flush=True)


if __name__ == "__main__":
    try:
        run()
    except Exception as exc:
        print(f"AUDIO_STUDIO_NATIVE=FAIL {exc}", file=sys.stderr, flush=True)
        sys.exit(1)
