#!/usr/bin/env python3
"""Offline render of the megastorm audio passage (docs/megastorm-design.md 5.2, WO6c) so it can be listened to without running the game.

Reads the voice-gain timeline that tools/MegastormAudioTest.cs writes (megastorm_wo6c_passage.csv, the real RingMegastormAudio node's gains for a
350 m/s passage at 0.5 s steps) and mixes the shipped Ogg assets with the same dB calibration the node uses: hum, the roar and the one boom
centred (the rim-wall singing voices were removed 2026-11-04). The airborne shelter bus is open (outdoors); --indoors applies the
Indoors class instead (roar at 0.15 gain through a 1.4 kHz low-pass, hum untouched). The mix is scaled by one common factor to peak
at -3 dBFS, so relative levels are kept (pass --scale-db with the outdoors value to level-match the indoors render).

Usage: python -I tools/media/megastorm_audio_render.py --csv <passage.csv> --out <dir> [--from -90 --to 45] [--indoors]
Needs numpy and ffmpeg.
"""
import argparse
import csv
import os
import shutil
import subprocess
import wave

import numpy as np

SR = 44100
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "resources", "audio", "weather")
BOOM_DB = 0.0


def decode(ffmpeg, name):
    raw = subprocess.run([ffmpeg, "-v", "error", "-i", os.path.join(ROOT, name + ".ogg"), "-ac", "1", "-ar", str(SR), "-f", "f32le", "-"],
                         check=True, capture_output=True).stdout
    return np.frombuffer(raw, dtype="<f4").astype(np.float64)


def loop(x, n, start, rate=1.0):
    pos = (start * SR + np.arange(n) * rate) % len(x)
    i = pos.astype(int)
    f = pos - i
    return x[i] * (1 - f) + x[(i + 1) % len(x)] * f


def lowpass(x, cutoff):
    spec = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / SR)
    return np.fft.irfft(spec / np.sqrt(1.0 + (f / cutoff) ** 4), len(x))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--from", dest="t0", type=float, default=-90.0)
    ap.add_argument("--to", dest="t1", type=float, default=45.0)
    ap.add_argument("--indoors", action="store_true")
    ap.add_argument("--scale-db", type=float, default=None, help="fixed mix gain in dB (use the outdoors render's printed value so both renders are level-matched)")
    ap.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or "ffmpeg")
    args = ap.parse_args()
    rows = list(csv.DictReader(open(args.csv)))
    t = np.array([float(r["t"]) for r in rows])
    col = lambda k: np.array([float(r[k]) for r in rows])
    n = int((args.t1 - args.t0) * SR)
    ts = args.t0 + np.arange(n) / SR
    amp_db = lambda db: np.where(db <= -79.0, 0.0, 10 ** (db / 20.0))
    hum = np.interp(ts, t, amp_db(col("hum_db")))
    roar = np.interp(ts, t, amp_db(col("roar_db")))
    hum_x, roar_x = decode(args.ffmpeg, "megastorm_ground_hum_loop"), decode(args.ffmpeg, "megastorm_roar_loop")
    humv = hum * loop(hum_x, n, 0.618 * 24)
    roarv = roar * loop(roar_x, n, 0.472 * 30)
    boom = np.zeros(n)
    fired = [i for i in range(1, len(rows)) if int(rows[i]["booms"]) > int(rows[i - 1]["booms"])]
    for i in fired:
        x = decode(args.ffmpeg, "megastorm_boom_%d" % (int(rows[i]["boom_variant"]) + 1))
        g = float(rows[i]["boom_gain"])
        start = int((t[i] - args.t0) * SR)
        if 0 <= start < n:
            end = min(n, start + len(x))
            boom[start:end] += 10 ** (BOOM_DB / 20.0) * g * x[: end - start]
    if args.indoors:
        roarv = lowpass(roarv, 1400.0) * 0.15
        boom = lowpass(boom, 1400.0) * 0.15
    mix_l = mix_r = humv + roarv + boom
    peak = max(np.max(np.abs(mix_l)), np.max(np.abs(mix_r)), 1e-9)
    scale = 10 ** (-3 / 20.0) / peak if args.scale_db is None else 10 ** (args.scale_db / 20.0)
    stereo = np.stack([mix_l, mix_r], axis=1) * scale
    os.makedirs(args.out, exist_ok=True)
    name = "megastorm_passage_350_" + ("indoors" if args.indoors else "outdoors")
    wav = os.path.join(args.out, name + ".wav")
    with wave.open(wav, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((np.clip(stereo, -1, 1) * 32767).astype("<i2").tobytes())
    subprocess.run([args.ffmpeg, "-v", "error", "-y", "-i", wav, "-c:a", "libvorbis", "-q:a", "5", os.path.join(args.out, name + ".ogg")], check=True)
    print(f"{name}: T{args.t0:+.0f}..T{args.t1:+.0f} s, scale {20 * np.log10(scale):+.1f} dB, boom at {[round(float(t[i]), 1) for i in fired]}")


if __name__ == "__main__":
    main()
