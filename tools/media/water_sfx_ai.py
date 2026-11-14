#!/usr/bin/env python3
"""AI water loops via the local audio studio (Stable Audio Open 1.0), replacing the synthesised loops of water_sfx_gen.py.

Subcommands (numpy plus ffmpeg only; the studio server is never started or stopped here):
  submit   queue one 47 s, 4 variant job per loop at http://127.0.0.1:7861; writes <scratch>/manifest.json
  audition build per-loop MP3s (each take ~10 s, 1 s gaps), a seam-check MP3 and a montage; prints loop / letter / take ID
  build    --pick name=GEN-xxxx-NN ... : trim 1 s each end, equal-power crossfade loop, normalise, encode OGG Vorbis q5

Usage:
  python -I tools/media/water_sfx_ai.py submit --scratch .godot/captures/audio/t3r10-ai [--only water_lap ...]
  python -I tools/media/water_sfx_ai.py audition --scratch .godot/captures/audio/t3r10-ai
  python -I tools/media/water_sfx_ai.py build --pick water_lap=GEN-0042-02 ... [--xfade 3] [--out-dir resources/audio/water]
  python -I tools/media/water_sfx_gen.py --out-dir resources/audio/water --verify --ai
Channels and sample rate match the synthesised loops (mono except open_water and underwater, 44.1 kHz); levels -20 dBFS RMS,
peaks under -3 dBFS (Spec W2/W5).
"""
import argparse
import glob
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zlib

import numpy as np

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.abspath(os.path.join(_HERE, "..", ".."))
_spec = importlib.util.spec_from_file_location("water_sfx_gen", os.path.join(_HERE, "water_sfx_gen.py"))
wg = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(wg)

SR = wg.SR
API = "http://127.0.0.1:7861"
STUDIO_OUT = os.path.join(_ROOT, "prototypes", "audio-studio", "output")
DEFAULT_OUT = os.path.join(_ROOT, "resources", "audio", "water")
DURATION, VARIANTS, STEPS, GUIDANCE = 47, 4, 100, 7
TRIM_S = 1.0
VORBIS_Q = "5"  # same as water_sfx_gen.generate
LETTERS = "ABCD"
NEGATIVE = ("music, melody, musical instruments, voices, speech, people, birds, seagulls, insects, wind, rain, thunder, engine, "
            "motor, boat engine, footsteps, swimmer splashing, fade in, fade out, low quality, distorted, lo-fi, 8-bit, bitcrushed, "
            "synthesizer")
PROMPTS = {
    "water_lap": "Gentle small waves lapping against a rocky lake shore, soft close splashes and trickles, calm fresh water, steady continuous ambience, high quality field recording",
    "surf_break": "Ocean waves breaking on a sandy beach, rolling surf crashes and foam washing up the shore and draining back, steady continuous seaside ambience, high quality field recording",
    "surf_far": "Distant ocean surf heard from far inland, soft low rumble of breaking waves, steady continuous, high quality field recording",
    "open_water": "Open sea far from shore, water sloshing and gentle swells against a floating hull, steady continuous, high quality field recording",
    "stream_babble": "Small shallow mountain stream babbling over pebbles, close bright trickling and gurgling water, steady continuous, high quality field recording",
    "river_rush": "Medium river flowing fast over rocks, rushing water, steady continuous, high quality field recording",
    "river_roar": "Large powerful river rapids roaring, white water, heavy continuous rush, high quality field recording",
    "waterfall": "Tall waterfall crashing into a plunge pool, heavy continuous roar and spray, high quality field recording",
    "underwater": "Underwater ambience recorded with a hydrophone, muffled low rumble, soft distant bubbles, steady continuous",
}
CHANNELS = {n[:-5]: wg.EXPECT[n][1] for n in wg.EXPECT}  # loop name without "_loop" -> channels
assert set(CHANNELS) == set(PROMPTS)


def loop_seed(name):
    return zlib.crc32(("ai:" + name).encode()) % 1000000 * 10  # variants use consecutive seeds, so leave room


def find_ffmpeg():
    p = shutil.which("ffmpeg")
    if p:
        return p
    root = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "WinGet", "Packages")
    hits = glob.glob(os.path.join(root, "Gyan.FFmpeg*", "**", "ffmpeg.exe"), recursive=True)
    if hits:
        return hits[0]
    sys.exit("ffmpeg not found (PATH or the winget Gyan.FFmpeg package). Install: winget install Gyan.FFmpeg")


# ---- studio API ---------------------------------------------------------------------------------------------------

def api(path, body=None, timeout=15):
    req = urllib.request.Request(API + path, data=None if body is None else json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"} if body is not None else {})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())


def health_or_exit():
    try:
        h = api("/api/health")
    except (urllib.error.URLError, OSError, ValueError) as exc:
        sys.exit(f"audio studio unreachable at {API} ({exc}). Start it with prototypes/audio-studio/run.ps1; this script never starts it.")
    eng = h.get("engines", {}).get("stable-audio-open", {})
    if not eng.get("ready"):
        sys.exit(f"stable-audio-open is not ready: {eng.get('message', 'unknown')}")
    return h


def cmd_submit(args):
    h = health_or_exit()
    names = [n for n in PROMPTS if not args.only or any(s in n for s in args.only)]
    if not names:
        sys.exit("--only matched no loop")
    os.makedirs(args.scratch, exist_ok=True)
    mpath = os.path.join(args.scratch, "manifest.json")
    manifest = json.load(open(mpath)) if os.path.exists(mpath) else {}
    for name in names:
        deadline = time.time() + args.max_wait * 60
        while h["queue_waiting"] >= h["queue_capacity"]:  # the studio queues at most eight waiting jobs
            if time.time() > deadline:
                sys.exit(f"queue stayed full; submitted so far are in {mpath}; rerun with --only for the rest")
            print("queue full, waiting 60 s ...")
            time.sleep(60)
            h = health_or_exit()
        seed = loop_seed(name) + args.seed_offset
        body = {"engine": "stable-audio-open", "backend": "diffusers", "prompt": PROMPTS[name], "negative_prompt": NEGATIVE,
                "duration": DURATION, "variants": VARIANTS, "seed": seed, "steps": STEPS, "guidance_scale": GUIDANCE}
        try:
            job = api("/api/jobs", body)
        except urllib.error.HTTPError as exc:
            sys.exit(f"{name}: submit failed HTTP {exc.code} {exc.read().decode(errors='replace')[:300]}")
        manifest[name] = {"job_id": job["id"], "generation_id": job.get("generation_id"), "seed": seed, "prompt": PROMPTS[name],
                          "negative_prompt": NEGATIVE, "duration": DURATION, "variants": VARIANTS, "steps": STEPS, "guidance_scale": GUIDANCE}
        json.dump(manifest, open(mpath, "w"), indent=1)
        print(f"{name}: job {job['id']} {job.get('generation_id')} seed {seed}")
        h["queue_waiting"] += 1
    print(f"manifest: {mpath}")


# ---- audio helpers ------------------------------------------------------------------------------------------------

def decode(ffmpeg, path, channels):
    return wg.decode(ffmpeg, path, channels)


def write_wav(path, x):
    wg.write_wav(path, x)


def encode_mp3(ffmpeg, x, path, tmp):
    write_wav(tmp, x)
    subprocess.run([ffmpeg, "-v", "error", "-y", "-i", tmp, "-c:a", "libmp3lame", "-q:a", "4", path], check=True)
    os.remove(tmp)


def find_take(take_id):
    """-> (job metadata, output dict, wav path) for a permanent take ID such as GEN-0042-03."""
    for meta_path in glob.glob(os.path.join(STUDIO_OUT, "*", "metadata.json")):
        try:
            m = json.load(open(meta_path))
        except (OSError, ValueError):
            continue
        for o in m.get("outputs", []):
            if o.get("take_id") == take_id:
                return m, o, os.path.join(os.path.dirname(meta_path), o["wav"])
    sys.exit(f"take {take_id} not found under {STUDIO_OUT}")


def make_loop(x, xfade_s):
    """x: (n, ch) with the end artefacts already trimmed. Equal-power crossfade of the tail onto the head; the result is
    xfade shorter, and its last sample flows straight into its first (out[0] is x[n-c], out[-1] is x[n-c-1])."""
    c = int(round(xfade_s * SR))
    n = len(x)
    if c <= 0 or 2 * c >= n:
        sys.exit(f"crossfade {xfade_s}s does not fit a {n / SR:.1f}s take")
    t = (np.arange(c) + 0.5) / c
    fin, fout = np.sin(0.5 * np.pi * t)[:, None], np.cos(0.5 * np.pi * t)[:, None]
    out = x[: n - c].copy()
    out[:c] = x[:c] * fin + x[n - c:] * fout
    return out


def conform(x, channels):
    if channels == 1 and x.shape[1] > 1:
        x = x.mean(axis=1, keepdims=True)
    return x


def normalise(x, ffmpeg, ogg, tmp_wav, channels):
    """-20 dBFS RMS, DC removed; if the peak stays over the ceiling a soft knee (as water_sfx_gen) with the knee lowered
    until the decoded Vorbis peaks under -3 dBFS. Returns the normalised signal."""
    x = x - x.mean(axis=0, keepdims=True)
    for ceiling in (None, -5.5, -7.5, -9.5, -11.5, -13.5):
        y = x * (10 ** (-20.0 / 20) / (np.sqrt(np.mean(x * x)) + 1e-12))
        if ceiling is not None and np.max(np.abs(y)) > wg.db(ceiling):
            knee = wg.db(ceiling) * 0.7
            span = wg.db(ceiling) - knee
            src = y
            for _ in range(6):  # the knee lowers the RMS, so raise the gain into it again until the RMS is back at -20
                y = src.copy()
                over = np.abs(y) > knee
                y[over] = np.sign(y[over]) * (knee + span * np.tanh((np.abs(y[over]) - knee) / span))
                rms_db = 20 * np.log10(np.sqrt(np.mean(y * y)) + 1e-12)
                if abs(rms_db + 20.0) < 0.1:
                    break
                src = src * 10 ** ((-20.0 - rms_db) / 20)
        elif ceiling is None and np.max(np.abs(y)) > wg.db(-3.2):
            continue
        write_wav(tmp_wav, y)
        subprocess.run([ffmpeg, "-v", "error", "-y", "-i", tmp_wav, "-ac", str(channels), "-ar", str(SR), "-c:a", "libvorbis",
                        "-q:a", VORBIS_Q, "-map_metadata", "-1", "-fflags", "+bitexact", "-flags:a", "+bitexact", ogg], check=True)
        if np.max(np.abs(decode(ffmpeg, ogg, channels))) < wg.db(-3.2):
            return y
    sys.exit(f"{ogg}: could not get the decoded peak under -3 dBFS")


# ---- audition -----------------------------------------------------------------------------------------------------

def cmd_audition(args):
    ffmpeg = find_ffmpeg()
    manifest = json.load(open(os.path.join(args.scratch, "manifest.json")))
    audi = os.path.join(args.scratch, "audition")
    os.makedirs(audi, exist_ok=True)
    gap = np.zeros((SR, 2))
    montage = []
    rows = []
    for name, entry in manifest.items():
        if args.only and not any(s in name for s in args.only):
            continue
        meta_path = os.path.join(STUDIO_OUT, entry["job_id"], "metadata.json")
        if not os.path.exists(meta_path):
            print(f"{name}: no output yet")
            continue
        m = json.load(open(meta_path))
        if m.get("status") != "succeeded":
            print(f"{name}: job status {m.get('status')}")
            continue
        parts, seams = [], []
        for i, o in enumerate(m["outputs"]):
            x = decode(ffmpeg, os.path.join(STUDIO_OUT, entry["job_id"], o["wav"]), 2)
            letter = LETTERS[i]
            rows.append((name, letter, o["take_id"]))
            body = x[int(TRIM_S * SR):int(TRIM_S * SR) + 10 * SR]
            parts += [body, gap]
            montage += [x[int(TRIM_S * SR):int(TRIM_S * SR) + 4 * SR], gap[: SR // 3]]
            loop = make_loop(x[int(TRIM_S * SR):len(x) - int(TRIM_S * SR)], args.xfade)
            seams += [np.concatenate((loop[-4 * SR:], loop[:4 * SR])), gap]
        encode_mp3(ffmpeg, np.concatenate(parts), os.path.join(audi, f"{name}_takes.mp3"), os.path.join(audi, "_t.wav"))
        encode_mp3(ffmpeg, np.concatenate(seams), os.path.join(audi, f"{name}_seams.mp3"), os.path.join(audi, "_t.wav"))
    if montage:
        encode_mp3(ffmpeg, np.concatenate(montage), os.path.join(audi, "montage.mp3"), os.path.join(audi, "_t.wav"))
    print("| loop | letter | take ID |\n| --- | --- | --- |")
    for r in rows:
        print("| %s | %s | %s |" % r)
    print(f"audition files: {audi} (each take 10 s from 1 s in, takes in letter order, 1 s gaps; seams MP3: last 4 s + first 4 s per take; "
          f"montage: 4 s per take, loops in manifest order)")


# ---- build --------------------------------------------------------------------------------------------------------

def cmd_build(args):
    ffmpeg = find_ffmpeg()
    picks = {}
    for p in args.pick:
        name, _, take = p.partition("=")
        if name not in PROMPTS or not re.fullmatch(r"GEN-\d{4,}-\d{2}", take):
            sys.exit(f"bad --pick {p!r}; use name=GEN-xxxx-NN with name in {sorted(PROMPTS)}")
        picks[name] = take
    os.makedirs(args.out_dir, exist_ok=True)
    scratch = args.scratch or os.path.join(args.out_dir, "_tmp")
    os.makedirs(scratch, exist_ok=True)
    records = {}
    for name, take in picks.items():
        meta, out, wav = find_take(take)
        ch = CHANNELS[name]
        x = decode(ffmpeg, wav, 2 if ch == 2 else 1)
        t = int(TRIM_S * SR)
        x = x[t:len(x) - t]
        loop = make_loop(conform(x, ch), args.xfade)
        ogg = os.path.join(args.out_dir, name + "_loop.ogg")
        y = normalise(loop, ffmpeg, ogg, os.path.join(scratch, name + ".wav"), ch)
        os.remove(os.path.join(scratch, name + ".wav"))
        rms = 20 * np.log10(np.sqrt(np.mean(y * y)) + 1e-12)
        print(f"{name}_loop.ogg: take {take}, {len(y) / SR:.3f} s, {ch} ch, peak {20 * np.log10(np.max(np.abs(y))):.1f} dBFS, rms {rms:.1f} dBFS")
        records[name] = {"take": take, "seed": out.get("seed"), "prompt": meta["settings"]["prompt"],
                         "negative": meta["settings"].get("negative_prompt", ""), "steps": meta["settings"].get("steps"),
                         "guidance": meta["settings"].get("guidance_scale"), "backend": meta.get("backend", "diffusers"),
                         "length": len(y) / SR, "channels": ch, "model_revision": meta.get("model", {}).get("model_revision", "")}
    if not args.no_license:
        update_license(args.out_dir, records, args.xfade)
    if not args.scratch and os.path.isdir(scratch):
        shutil.rmtree(scratch, ignore_errors=True)


LIC_BEGIN, LIC_END = "<!-- ai-water-loops:begin -->", "<!-- ai-water-loops:end -->"


def update_license(out_dir, records, xfade):
    path = os.path.join(out_dir, "LICENSE.md")
    text = open(path, encoding="utf-8").read() if os.path.exists(path) else ""
    old = {}
    if LIC_BEGIN in text:  # keep the entries of loops not rebuilt this run
        block = text[text.index(LIC_BEGIN):text.index(LIC_END)]
        for m in re.finditer(r"^- `(\w+)_loop\.ogg`: (\{.*\})$", block, re.M):
            old[m.group(1)] = json.loads(m.group(2))
    old.update(records)
    lines = [LIC_BEGIN, "## AI-generated water loops (T3R-10 replacement)", "",
             "The `*_loop.ogg` files listed here are generated with Stable Audio Open 1.0 "
             "(https://huggingface.co/stabilityai/stable-audio-open-1.0), used under the Stability AI Community License "
             "(terms linked from the model card), through the local audio studio (`prototypes/audio-studio`, "
             "diffusers backend, 47 s, 100 steps, guidance 7). They replace the synthesised loops of `tools/media/water_sfx_gen.py`; "
             "file names, loop flags and levels are unchanged.", "",
             f"Processing (`tools/media/water_sfx_ai.py build`): decode the picked take, drop the first and last {TRIM_S:g} s, "
             f"equal-power crossfade of the tail onto the head ({xfade:g} s by default; the loop is that much shorter), "
             "mono downmix except open_water and underwater (stereo), 44.1 kHz, DC removed, normalised to -20 dBFS RMS with a soft "
             f"knee only if the peak would exceed -3 dBFS, Ogg Vorbis quality {VORBIS_Q}. Check: "
             "`python -I tools/media/water_sfx_gen.py --out-dir resources/audio/water --verify --ai`.", "",
             "Per file (JSON: take ID, seed, prompt, negative prompt, steps, guidance, backend, loop length s, channels, model revision):", ""]
    for name in PROMPTS:
        if name in old:
            lines.append(f"- `{name}_loop.ogg`: " + json.dumps(old[name], sort_keys=True))
    lines += ["", LIC_END]
    block = "\n".join(lines)
    if LIC_BEGIN in text:
        text = text[:text.index(LIC_BEGIN)] + block + text[text.index(LIC_END) + len(LIC_END):]
    else:
        text = text.rstrip("\n") + "\n\n" + block + "\n"
    open(path, "w", encoding="utf-8", newline="\n").write(text)
    print(f"updated {path}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("submit")
    s.add_argument("--scratch", required=True)
    s.add_argument("--only", nargs="*", default=None)
    s.add_argument("--max-wait", type=float, default=600.0, help="minutes to wait for queue space")
    s.add_argument("--seed-offset", type=int, default=0, help="added to the loop seed, for a fresh set of takes (keep it a multiple of 10)")
    s.set_defaults(fn=cmd_submit)
    a = sub.add_parser("audition")
    a.add_argument("--scratch", required=True)
    a.add_argument("--only", nargs="*", default=None)
    a.add_argument("--xfade", type=float, default=3.0)
    a.set_defaults(fn=cmd_audition)
    b = sub.add_parser("build")
    b.add_argument("--pick", nargs="+", required=True, metavar="NAME=GEN-XXXX-NN")
    b.add_argument("--xfade", type=float, default=3.0)
    b.add_argument("--out-dir", default=DEFAULT_OUT)
    b.add_argument("--scratch", default=None, help="scratch dir for temporary wavs (default: inside out-dir, removed)")
    b.add_argument("--no-license", action="store_true", help="do not touch LICENSE.md")
    b.set_defaults(fn=cmd_build)
    args = ap.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
