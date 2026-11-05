"""Download and verify the studio's pinned, official model snapshots."""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts" / "audio-studio"
SOURCE_REVISION = "ca1e85fe9430179831e6bc6be790c332190a3866"
SOURCE = ARTIFACTS / "source" / f"ACE-Step-1.5-{SOURCE_REVISION}"
MANIFEST = ARTIFACTS / "model-manifest.json"
os.environ["HF_HOME"] = str(ARTIFACTS / "cache" / "huggingface")
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["MPLCONFIGDIR"] = str(ARTIFACTS / "cache" / "matplotlib")
os.environ["NUMBA_CACHE_DIR"] = str(ARTIFACTS / "cache" / "numba")
os.environ["TORCH_HOME"] = str(ARTIFACTS / "cache" / "torch")
os.environ["GRADIO_TEMP_DIR"] = str(ARTIFACTS / "cache" / "gradio")

MODELS = {
    "ace-step": {
        "model_id": "ACE-Step/Ace-Step1.5",
        "model_revision": "19671f406d603126926c1b7e2adc169acbcade22",
        "model_path": SOURCE / "checkpoints",
        "license": "MIT",
        "patterns": ["Qwen3-Embedding-0.6B/*", "acestep-5Hz-lm-1.7B/*", "acestep-v15-turbo/*", "vae/*", "config.json", "README.md"],
        "ignore_patterns": ["acestep-v15-turbo/*.py"],
        "required": ["Qwen3-Embedding-0.6B/model.safetensors", "acestep-5Hz-lm-1.7B/model.safetensors", "acestep-v15-turbo/model.safetensors", "vae/diffusion_pytorch_model.safetensors"],
    },
    "stable-audio-open": {
        "model_id": "stabilityai/stable-audio-open-1.0",
        "model_revision": "f21265c1e2710b3bd2386596943f0007f55f802e",
        "model_path": ARTIFACTS / "models" / "stable-audio-open-1.0",
        "license": "Stability AI Community License",
        "patterns": ["model_index.json", "projection_model/*", "scheduler/*", "text_encoder/*", "tokenizer/*", "transformer/*", "vae/*", "LICENSE.md", "README.md"],
        "required": ["model_index.json", "projection_model/diffusion_pytorch_model.safetensors", "text_encoder/model.safetensors", "transformer/diffusion_pytorch_model.safetensors", "vae/diffusion_pytorch_model.safetensors"],
    },
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(engine: str) -> None:
    from huggingface_hub import HfApi, snapshot_download

    spec = MODELS[engine]
    print(f"Downloading {engine} from official pinned revision {spec['model_revision']}", flush=True)
    info = HfApi().model_info(spec["model_id"], revision=spec["model_revision"], files_metadata=True)
    snapshot_download(repo_id=spec["model_id"], revision=spec["model_revision"],
                      local_dir=str(spec["model_path"]), allow_patterns=spec["patterns"],
                      ignore_patterns=spec.get("ignore_patterns"))
    selected = [file for file in info.siblings
                if any(fnmatch.fnmatchcase(file.rfilename, pattern) for pattern in spec["patterns"])
                and not any(fnmatch.fnmatchcase(file.rfilename, pattern)
                            for pattern in spec.get("ignore_patterns", []))]
    names = {file.rfilename for file in selected}
    if not set(spec["required"]).issubset(names):
        raise RuntimeError(f"{engine}: pinned snapshot is missing required files")
    files = []
    for file in sorted(selected, key=lambda item: item.rfilename):
        path = spec["model_path"] / file.rfilename
        if not path.is_file() or path.stat().st_size == 0:
            raise RuntimeError(f"{engine}: missing or empty file {file.rfilename}")
        size = path.stat().st_size
        if file.size is not None and size != file.size:
            raise RuntimeError(f"{engine}: size mismatch for {file.rfilename}")
        digest = sha256(path)
        if file.lfs is not None and digest != file.lfs.sha256:
            raise RuntimeError(f"{engine}: SHA256 mismatch for {file.rfilename}")
        files.append({"path": file.rfilename, "size": size, "sha256": digest})
    code_files = []
    if engine == "ace-step":
        code_source = SOURCE / "acestep" / "models" / "turbo"
        source_files = sorted(path for path in code_source.glob("*.py") if path.name != "__init__.py")
        required_code = {"configuration_acestep_v15.py", "modeling_acestep_v15_turbo.py"}
        if not required_code.issubset({path.name for path in source_files}):
            raise RuntimeError("Pinned ACE source is missing required turbo implementation files")
        for source_file in source_files:
            if source_file.stat().st_size == 0:
                raise RuntimeError(f"Empty pinned source code file: {source_file.name}")
            target = spec["model_path"] / "acestep-v15-turbo" / source_file.name
            shutil.copyfile(source_file, target)
            source_hash = sha256(source_file)
            if sha256(target) != source_hash:
                raise RuntimeError(f"Copied source code hash mismatch: {source_file.name}")
            code_files.append({"path": target.relative_to(spec["model_path"]).as_posix(),
                               "size": target.stat().st_size, "sha256": source_hash,
                               "source_revision": SOURCE_REVISION, "source_path": str(source_file)})
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8-sig")) if MANIFEST.exists() else {}
    manifest.update(schema_version=1, source_revision=SOURCE_REVISION, source_path=str(SOURCE))
    manifest.setdefault("models", {})[engine] = {
        key: str(spec[key]) for key in ("model_id", "model_revision", "model_path", "license")
    }
    manifest["models"][engine]["files"] = files
    if engine == "ace-step":
        manifest["models"][engine]["code_source_revision"] = SOURCE_REVISION
        manifest["models"][engine]["source_code_files"] = code_files
    temporary = MANIFEST.with_suffix(".tmp")
    temporary.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    temporary.replace(MANIFEST)
    print(f"Verified {engine}: {len(files)} files; manifest updated.", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine", choices=[*MODELS, "all"], default="all")
    args = parser.parse_args()
    engines = list(MODELS) if args.engine == "all" else [args.engine]
    for engine in engines:
        try:
            download(engine)
        except Exception as exc:
            # Avoid printing request details, authentication headers or token values.
            print(f"Setup incomplete: {engine} download/verification failed ({type(exc).__name__}).", file=sys.stderr)
            if engine == "stable-audio-open":
                print("Accept the model conditions at https://huggingface.co/stabilityai/stable-audio-open-1.0\n"
                      "Then run these commands from the project root:\n"
                      "powershell -NoProfile -ExecutionPolicy Bypass -File .\\prototypes\\audio-studio\\login.ps1\n"
                      "powershell -NoProfile -ExecutionPolicy Bypass -File .\\prototypes\\audio-studio\\setup.ps1\n"
                      "Previously verified models remain available.", file=sys.stderr)
            return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
