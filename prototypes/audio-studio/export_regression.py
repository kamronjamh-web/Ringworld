"""CPU export regression. Run through tools/run_tests.ps1 -Tests audio_studio_export_regression."""
from __future__ import annotations

import argparse
import importlib.util
import os
from pathlib import Path
import subprocess
import sys

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
SCRATCH = ROOT / "artifacts" / "audio-studio" / "regression" / "audio-studio-export"
CASES = ((5, 44100), (15, 44100), (47, 44100), (120, 48000))


def load_module(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def case_path(seconds, rate):
    path = (SCRATCH / f"stereo-{seconds}s-{rate}hz.ogg").resolve()
    check(path.parent == SCRATCH.resolve(), "Export path escaped regression scratch")
    return path


def run_case(seconds, rate):
    import numpy as np
    import soundfile as sf

    check((seconds, rate) in CASES, "Unexpected export regression case")
    worker = load_module("audio_studio_export_worker", "generate.py")
    frames = seconds * rate
    time = np.arange(frames, dtype=np.float32) / rate
    samples = np.column_stack((0.15 * np.sin(2 * np.pi * 220 * time),
                               0.12 * np.sin(2 * np.pi * 330 * time))).astype(np.float32)
    path = case_path(seconds, rate)
    check(not path.exists(), "Refusing to overwrite an existing regression file")
    # Exercise the production encoder in this disposable process: native faults
    # cannot terminate the driver or hide which duration failed.
    worker.write_ogg(path, samples, rate)
    decoded, decoded_rate = sf.read(str(path), dtype="float32", always_2d=True)
    check(decoded_rate == rate, f"Wrong sample rate: {decoded_rate}")
    check(decoded.shape == (frames, 2), f"Incomplete OGG: {decoded.shape}, expected {(frames, 2)}")
    check(np.isfinite(decoded).all(), "Decoded OGG contains non-finite samples")
    check(float(np.max(np.abs(decoded))) > 0.01, "Decoded OGG is silent")
    check("torch" not in sys.modules and "diffusers" not in sys.modules and "acestep" not in sys.modules,
          "Export-only regression imported a model runtime")
    print(f"OGG_EXPORT_CASE=PASS duration={seconds} rate={rate} frames={frames} channels=2", flush=True)


def check_failure_messages():
    server = load_module("audio_studio_export_server", "server.py")
    progress = "\x1b[32mDiffusion: 100%|##########| 50/50 [00:01<00:00, 40.0it/s]\x1b[0m\r"
    for code in (3221225725, -1073741571):
        message = server.worker_failure_message(code, progress)
        check(str(code) in message and "0xC00000FD" in message, "Native exit code lost")
        check("stack overflow" in message and "no Python traceback" in message, "Native crash detail lost")
        check("100%" not in message and "\x1b" not in message, "Progress/ANSI leaked into error")
    message = server.worker_failure_message(1, "Traceback (most recent call last):\n  File \"worker.py\", line 1\n\x1b[31mRuntimeError: model failed\x1b[0m\n" + progress)
    check("RuntimeError: model failed" in message and "0x00000001" in message,
          "Python exception or exit code lost")
    check("100%" not in message and "\x1b" not in message, "Progress/ANSI leaked into Python error")
    check("no Python traceback" in server.worker_failure_message(2, ""), "Empty stderr fallback lost")
    check("no Python traceback" in server.worker_failure_message(3, progress), "Progress-only fallback lost")
    check("torch" not in sys.modules, "Controller-only regression imported Torch")
    print("WORKER_ERROR_FORMAT=PASS", flush=True)


def run():
    expected = ROOT / "artifacts" / "audio-studio" / "regression" / "audio-studio-export"
    check(SCRATCH.resolve() == expected.absolute(), "Regression scratch must not redirect outside its owned path")
    SCRATCH.mkdir(parents=True, exist_ok=True)
    lock_path = SCRATCH / "regression.lock"
    # Exclusive lock avoids two runs overwriting or cleaning up each other's files.
    with lock_path.open("x", encoding="utf-8") as lock:
        lock.write(str(os.getpid()))
    created = []
    try:
        check_failure_messages()
        for seconds, rate in CASES:
            path = case_path(seconds, rate)
            check(not path.exists(), f"Regression output already exists: {path}")
            created.append(path)
            result = subprocess.run(
                [sys.executable, str(Path(__file__).resolve()), "--case", str(seconds), "--rate", str(rate)],
                capture_output=True, text=True, timeout=60, cwd=ROOT,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            check(result.returncode == 0,
                  f"Export {seconds}s/{rate}Hz exited {result.returncode} (0x{result.returncode & 0xFFFFFFFF:08X}): {result.stderr[-2000:]}")
            check("OGG_EXPORT_CASE=PASS" in result.stdout, "Export subprocess omitted its pass marker")
            print(result.stdout.strip(), flush=True)
        print("AUDIO_STUDIO_EXPORT=PASS", flush=True)
    finally:
        # Explicit files only; no recursive deletion and no model/output folders.
        for path in created:
            check(path.resolve().parent == SCRATCH.resolve(), "Refusing cleanup outside regression scratch")
            path.unlink(missing_ok=True)
        lock_path.unlink()
        if not any(SCRATCH.iterdir()):
            SCRATCH.rmdir()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--case", type=int)
    parser.add_argument("--rate", type=int)
    args = parser.parse_args()
    try:
        if args.case is None:
            run()
        else:
            run_case(args.case, args.rate)
    except Exception as exc:
        print(f"AUDIO_STUDIO_EXPORT=FAIL {exc}", file=sys.stderr, flush=True)
        sys.exit(1)
