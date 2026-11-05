"""Prepare the official native Stable Audio Open checkpoint at the existing model revision."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts/audio-studio"
MODEL_ID = "stabilityai/stable-audio-open-1.0"
MODEL_REVISION = "f21265c1e2710b3bd2386596943f0007f55f802e"
SOURCE_REVISION = "3241adba4fc2a85cf5b29d9eb68d42f40a28e820"
SOURCE = ARTIFACTS / "source" / f"stable-audio-tools-{SOURCE_REVISION}"
NATIVE = ARTIFACTS / "models/stable-audio-open-native"
SHARED = ARTIFACTS / "models/stable-audio-open-1.0"
os.environ["HF_HOME"] = str(ARTIFACTS / "cache/huggingface")
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def prepare():
    from huggingface_hub import HfApi, hf_hub_download

    assert (SOURCE / "stable_audio_tools/models/factory.py").is_file(), "Pinned native source missing"
    required = ("model_config.json", "model.safetensors")
    info = HfApi().model_info(MODEL_ID, revision=MODEL_REVISION, files_metadata=True)
    native_files = {file.rfilename: file for file in info.siblings if file.rfilename in required}
    if set(native_files) != set(required):
        raise RuntimeError("Pinned model revision has no complete native safetensors checkpoint")
    NATIVE.mkdir(parents=True, exist_ok=True)
    files = []
    for name in required:
        remote = native_files[name]
        print(f"Preparing native {name} ({remote.size or 0:,} bytes)", flush=True)
        path = Path(hf_hub_download(MODEL_ID, name, revision=MODEL_REVISION, local_dir=str(NATIVE)))
        digest = sha256(path)
        if not path.is_file() or not path.stat().st_size or (remote.size is not None and path.stat().st_size != remote.size):
            raise RuntimeError(f"Native checkpoint size mismatch: {name}")
        if remote.lfs is not None and digest != remote.lfs.sha256:
            raise RuntimeError(f"Native checkpoint hash mismatch: {name}")
        files.append({"path":name, "size":path.stat().st_size, "sha256":digest})
    # The native T5 conditioner expects model+tokenizer in one folder. Reuse verified
    # Diffusers files without changing that model or allocating a second T5 weight copy.
    base_manifest = json.loads((ARTIFACTS / "model-manifest.json").read_text(encoding="utf-8-sig"))
    shared_spec = base_manifest["models"]["stable-audio-open"]
    if shared_spec["model_revision"] != MODEL_REVISION:
        raise RuntimeError("Shared T5 files must come from the same model revision")
    text_dir = NATIVE / "t5-base"
    text_dir.mkdir(exist_ok=True)
    shared_files = []
    for record in shared_spec["files"]:
        relative = Path(record["path"])
        if relative.parts[0] not in ("text_encoder", "tokenizer"):
            continue
        source = SHARED / relative
        target = text_dir / relative.name
        if relative.parts[0] == "text_encoder" and relative.name not in ("config.json", "model.safetensors"):
            continue
        if sha256(source) != record["sha256"]:
            raise RuntimeError(f"Shared text model hash mismatch: {relative.as_posix()}")
        if target.exists():
            if sha256(target) != record["sha256"]:
                raise RuntimeError(f"Native text file conflicts with shared model: {target.name}")
        else:
            try:
                os.link(source, target)
            except OSError:
                shutil.copyfile(source, target)
        shared_files.append({"path":target.relative_to(NATIVE).as_posix(), "size":target.stat().st_size, "sha256":record["sha256"], "source":str(source)})
    for name in ("config.json", "model.safetensors", "tokenizer_config.json", "spiece.model"):
        if not (text_dir / name).is_file():
            raise RuntimeError(f"Incomplete native text model: {name}")
    manifest = {
        "schema_version":1,
        "source_revision":SOURCE_REVISION,
        "source_path":str(SOURCE),
        "python_path":str(ARTIFACTS / "venv-stable-audio-tools/Scripts/python.exe"),
        "model":{
            "model_id":MODEL_ID, "model_revision":MODEL_REVISION, "model_path":str(NATIVE),
            "license":shared_spec["license"], "files":files + shared_files,
        },
    }
    path = ARTIFACTS / "native-runtime.json"
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)
    print("Verified native checkpoint and local T5; native-runtime.json written.", flush=True)


if __name__ == "__main__":
    try:
        prepare()
    except Exception as exc:
        # Avoid exposing request headers or stored Hugging Face credentials.
        print(f"Native model preparation failed ({type(exc).__name__}).", flush=True)
        raise SystemExit(2)
