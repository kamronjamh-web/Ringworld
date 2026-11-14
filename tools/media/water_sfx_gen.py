#!/usr/bin/env python3
"""Procedural water loop generator (docs/work-orders/terrain-vegetation/T3.md, T3R-10c, Spec W5).

Nine seamless loops, synthesised from seeded noise and the Minnaert bubble model already used by
tools/media/weather_sfx_gen.py (whose helpers are imported, not changed). Every loop is built circularly (events wrap,
beds and modulations are periodic in the loop length), so the seam needs no crossfade. The families differ the way real
water does: the size and number of bubbles and the share of noise.

Usage (numpy is the only dependency; ffmpeg encodes and decodes):
  python -I tools/media/water_sfx_gen.py --wav-dir <scratch> --out-dir resources/audio/water [--only name ...]
  python -I tools/media/water_sfx_gen.py --out-dir resources/audio/water --verify [--markdown]
Each loop is normalised to -20 dBFS RMS with peaks under -3 dBFS, so every level in the game comes from Spec W2.
--verify checks length, channels, rate, RMS, peak, the seam and the steadiness of quarter-second windows for the nine
loops, and runs the negative control (the old shore_wave_cc0.ogg), which must FAIL the gap and range checks.
"""
import argparse
import hashlib
import importlib.util
import os
import shutil
import subprocess
import sys
import wave
import zlib

import numpy as np

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("weather_sfx_gen", os.path.join(_HERE, "weather_sfx_gen.py"))
wx = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(wx)

SR = wx.SR
noise, bubble, fold, poisson_times, normalise_loop, t_axis = wx.noise, wx.bubble, wx.fold, wx.poisson_times, wx.normalise_loop, wx.t_axis
rng_for = wx.rng_for


# ---- helpers ------------------------------------------------------------------------------------------------------

def db(x):
    return 10 ** (x / 20)


def unit(x):
    return x / (np.sqrt(np.mean(x * x)) + 1e-12)


def swell(rng, seconds, periods, range_db, n=None):
    """Smooth gain, exactly periodic over the loop, with `range_db` peak to peak (centred on 0 dB)."""
    n = n or int(seconds * SR)
    t = np.arange(n) / n
    x = np.zeros(n)
    for p in periods:
        cycles = max(1, round(seconds / p))
        x += rng.uniform(0.6, 1.0) * np.sin(2 * np.pi * cycles * t + rng.uniform(0, 2 * np.pi))
    x = (x - x.min()) / (x.max() - x.min() + 1e-12) - 0.5
    return db(range_db * x)


def minnaert_hz(radius_mm):
    return 3260.0 / radius_mm


def bubble_field(rng, seconds, rate, r_lo, r_hi, tau_ms=(3.0, 9.0), rise=0.12, amp_pow=1.0):
    """Circular population of Minnaert bubbles (radius log-uniform in mm); unit RMS. `rate` may be a function of t."""
    length = int(seconds * SR)
    tail = int(0.12 * SR)
    buf = np.zeros(length + tail)
    for ti in poisson_times(rng, rate, seconds):
        r = np.exp(rng.uniform(np.log(r_lo), np.log(r_hi)))
        tau = rng.uniform(*tau_ms) * 1e-3
        n = min(tail, int(6 * tau * SR))
        amp = (r / r_lo) ** amp_pow * np.exp(rng.normal(0, 0.35))
        sig = bubble(rng, minnaert_hz(r), tau, rise * rng.uniform(0.3, 1.0), n) * amp
        s = int(ti * SR)
        buf[s:s + n] += sig[: len(buf) - s] if s + n > len(buf) else sig
    return unit(fold(buf, length))


def event_times(rng, seconds, lo, hi):
    """Irregular intervals in [lo, hi] scaled so the sum is exactly the loop length (wraps seamlessly)."""
    gaps = []
    while sum(gaps) < seconds:
        gaps.append(rng.uniform(lo, hi))
    gaps = np.array(gaps)
    gaps *= seconds / gaps.sum()
    return np.cumsum(gaps) - gaps[0] + rng.uniform(0, 0.5)


def scatter(length, events, tail_s):
    """events: (time s, signal); the tails wrap onto the start."""
    buf = np.zeros(length + int(tail_s * SR))
    for t, sig in events:
        s = int(t * SR) % length
        e = min(len(buf), s + len(sig))
        buf[s:e] += sig[: e - s]
    return fold(buf, length)


def rise_fall(n, rise_s, tau_s):
    t = np.arange(n) / SR
    r = np.minimum(1.0, t / rise_s)
    return np.sin(0.5 * np.pi * r) ** 2 * np.exp(-np.maximum(0.0, t - rise_s) / tau_s)


# ---- the nine loops -----------------------------------------------------------------------------------------------

def water_lap(rng, seconds=40.0):
    """Small waves on a shore: overlapping gentle washes 1.5 to 4 s apart, each a swell of noise with a few bubbles."""
    length = int(seconds * SR)
    times = event_times(rng, seconds, 1.5, 4.0)
    events = []
    for t in times:
        d = rng.uniform(3.0, 5.5)
        n = int(d * SR)
        env = rise_fall(n, rng.uniform(0.5, 1.1), rng.uniform(0.7, 1.2))
        body = noise(rng, n, 150, rng.uniform(1800, 3200)) * env
        fizz = noise(rng, n, 1500, 7000) * rise_fall(n, rng.uniform(0.9, 1.4), rng.uniform(0.5, 0.9)) * 0.35
        events.append((t, (body + fizz) * rng.uniform(0.7, 1.2)))
        for _ in range(rng.integers(3, 9)):
            r = np.exp(rng.uniform(np.log(1.0), np.log(7.0)))
            tau = rng.uniform(4, 11) * 1e-3
            bn = int(6 * tau * SR)
            events.append((t + rng.uniform(0.6, 2.8), bubble(rng, minnaert_hz(r), tau, 0.15, bn) * rng.uniform(0.3, 1.1)))
    washes = unit(scatter(length, events, 6.0))
    bed = noise(rng, length, 120, 2200) * swell(rng, seconds, [9, 14], 5.0)
    return unit(washes + 0.55 * bed)


def surf_break(rng, seconds=64.0):
    """Breakers: thump, burst, a roar dying over 2 to 4 s, the swash up the beach and back; sets of 3 to 7 waves."""
    length = int(seconds * SR)
    sets = [4, 3] if rng.uniform() < 0.5 else [3, 5]
    times, t = [], rng.uniform(1.0, 3.0)
    scale = seconds / (sum(sets) * 7.6)
    for si, count in enumerate(sets):
        for wi in range(count):
            times.append((t, si, wi, count))
            t += rng.uniform(5.0, 8.5) * scale if wi < count - 1 else rng.uniform(9.5, 11.0) * scale
    events = []
    for t, si, wi, count in times:
        strength = rng.uniform(0.7, 1.15) * (0.8 + 0.3 * np.sin(np.pi * (wi + 0.5) / count))
        n = int(8.0 * SR)
        tt = np.arange(n) / SR
        thump = wx.modes(rng, n, [rng.uniform(40, 62), rng.uniform(75, 110)], [0.35, 0.22], [1.0, 0.6]) * 0.9
        roar_tau = rng.uniform(0.8, 1.6)
        roar = noise(rng, n, 90, rng.uniform(2500, 4500)) * wx.attack(n, 25) * np.exp(-tt / roar_tau) * 1.3
        burst = noise(rng, n, 700, 9000) * wx.attack(n, 8) * np.exp(-tt / 0.35) * 0.9
        up = rise_fall(n, rng.uniform(1.2, 2.0), rng.uniform(1.4, 2.4))
        swash = noise(rng, n, 2000, 9000) * np.concatenate((np.zeros(int(0.5 * SR)), up[: n - int(0.5 * SR)])) * 0.55
        back = np.roll(rise_fall(n, 1.6, 2.0), int(3.2 * SR)) * (tt > 2.8)
        backwash = noise(rng, n, 400, 3500) * back * 0.35
        events.append((t, (thump + roar + burst + swash + backwash) * strength))
    waves = unit(scatter(length, events, 8.0))
    bed = noise(rng, length, 80, 2200) * swell(rng, seconds, [10, 17, 25], 6.0)
    return unit(waves + 0.6 * unit(bed))


def surf_far(rng, seconds=32.0):
    """Surf from a distance: a continuous band 80 to 2,500 Hz with a slow irregular swell of at most 4 dB."""
    length = int(seconds * SR)
    x = noise(rng, length, 80, 2500, order=3)
    x = x + 0.5 * noise(rng, length, 120, 900, order=2)
    return x * swell(rng, seconds, [7, 11, 17], 3.6)


def open_water(rng, seconds=40.0):
    """Chop and slosh around the listener, decorrelated channels, a soft hiss of wind on the water."""
    length = int(seconds * SR)
    out = np.zeros((length, 2))
    for c in range(2):
        r = np.random.default_rng(rng.integers(1 << 31))
        slosh = noise(r, length, 90, 1400) * swell(r, seconds, [4, 6, 9], 9.0)
        slosh2 = noise(r, length, 250, 2600) * swell(r, seconds, [3, 5.5], 8.0)
        chop = bubble_field(r, seconds, 35, 1.5, 9.0, (5, 14), 0.15, 1.2)
        hiss = noise(r, length, 1800, 9000) * swell(r, seconds, [8, 13], 4.0)
        out[:, c] = unit(slosh) + 0.6 * unit(slosh2) + 0.5 * chop + 0.3 * unit(hiss)
    return out


def stream_babble(rng, seconds=32.0):
    """A dense population of small bubbles, 150 to 300 a second with a slow wander, on a light bed of noise."""
    length = int(seconds * SR)
    rate = lambda t: 225.0 + 75.0 * np.sin(2 * np.pi * round(seconds / 8) * t / seconds + 1.3)
    b = bubble_field(rng, seconds, rate, 1.0, 6.0, (3.0, 8.0), 0.15, 1.1)
    bed = noise(rng, length, 700, 6500)
    return unit(b + 0.25 * bed)


def river_rush(rng, seconds=32.0):
    """A steady rush, 300 Hz to 6 kHz, with larger bubbles (3 to 15 mm) at about 100 a second."""
    length = int(seconds * SR)
    b = bubble_field(rng, seconds, 100, 3.0, 15.0, (6.0, 16.0), 0.12, 1.0)
    bed = noise(rng, length, 300, 6000, order=2)
    return unit(0.8 * unit(bed) + 0.5 * b)


def river_roar(rng, seconds=32.0):
    """Heavy water: a strong band 60 to 400 Hz under noise up to 4 kHz, with slow surges of at most 3 dB."""
    length = int(seconds * SR)
    low = noise(rng, length, 60, 400, order=3)
    high = noise(rng, length, 400, 4000, order=2)
    return (unit(low) * 1.1 + unit(high) * 0.7) * swell(rng, seconds, [6, 10, 16], 2.8)


def waterfall(rng, seconds=32.0):
    """The impact, low and steady, under a steady hiss of spray from 2 to 10 kHz."""
    length = int(seconds * SR)
    impact = noise(rng, length, 45, 320, order=3) * swell(rng, seconds, [8, 14], 1.6)
    body = noise(rng, length, 250, 2000, order=2)
    spray = noise(rng, length, 2000, 10000, order=2)
    return unit(impact) * 1.0 + 0.25 * unit(body) + 0.7 * unit(spray)


def underwater(rng, seconds=40.0):
    """A rumble under 300 Hz and sparse rising bubbles."""
    length = int(seconds * SR)
    out = np.zeros((length, 2))
    for c in range(2):
        r = np.random.default_rng(rng.integers(1 << 31))
        rumble = noise(r, length, 25, 300, order=3) * swell(r, seconds, [8, 13], 2.5)
        bub = bubble_field(r, seconds, 3.0, 2.0, 9.0, (30.0, 70.0), 0.7, 0.8)
        out[:, c] = unit(rumble) + 0.12 * bub
    return out


LOOPS = [("water_lap_loop", water_lap, "loose", True), ("surf_break_loop", surf_break, "loose", True),
         ("surf_far_loop", surf_far, "tight", False), ("open_water_loop", open_water, "loose", False),
         ("stream_babble_loop", stream_babble, "tight", False), ("river_rush_loop", river_rush, "tight", False),
         ("river_roar_loop", river_roar, "tight", False), ("waterfall_loop", waterfall, "tight", False),
         ("underwater_loop", underwater, "tight", False)]
EXPECT = {"water_lap_loop": (40.0, 1), "surf_break_loop": (64.0, 1), "surf_far_loop": (32.0, 1), "open_water_loop": (40.0, 2),
          "stream_babble_loop": (32.0, 1), "river_rush_loop": (32.0, 1), "river_roar_loop": (32.0, 1),
          "waterfall_loop": (32.0, 1), "underwater_loop": (40.0, 2)}


# ---- output -------------------------------------------------------------------------------------------------------

def write_wav(path, x):
    x = x.reshape(len(x), -1)
    data = (np.clip(x, -1.0, 1.0) * 32767.0).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(x.shape[1])
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(data.tobytes())


def generate(args):
    for name, fn, _, _ in LOOPS:
        if args.only and not any(s in name for s in args.only):
            continue
        raw = fn(rng_for(name))
        for ceiling in (-5.5, -7.5, -9.5, -11.5):  # Vorbis can overshoot a hard-limited transient; lower the knee until the decode is under -3 dBFS
            x = normalise_loop(raw, -20.0, ceiling)
            if not np.all(np.isfinite(x)):
                sys.exit(f"{name}: non-finite samples")
            os.makedirs(args.wav_dir, exist_ok=True)
            wav = os.path.join(args.wav_dir, name + ".wav")
            write_wav(wav, x)
            ogg = os.path.join(args.out_dir, name + ".ogg")
            os.makedirs(args.out_dir, exist_ok=True)
            ch = 2 if x.ndim == 2 else 1
            subprocess.run([args.ffmpeg, "-v", "error", "-y", "-i", wav, "-ac", str(ch), "-ar", str(SR), "-c:a", "libvorbis",
                            "-q:a", "5", "-map_metadata", "-1", "-fflags", "+bitexact", "-flags:a", "+bitexact", ogg], check=True)
            if np.max(np.abs(decode(args.ffmpeg, ogg, ch))) < db(-3.2):
                break
        rms = 20 * np.log10(np.sqrt(np.mean(x * x)) + 1e-12)
        print(f"{name}: {len(x) / SR:.3f} s, {ch} ch, peak {20 * np.log10(np.max(np.abs(x))):.1f} dBFS, rms {rms:.1f} dBFS")


# ---- verification -------------------------------------------------------------------------------------------------

def decode(ffmpeg, path, channels):
    raw = subprocess.run([ffmpeg, "-v", "error", "-i", path, "-ac", str(channels), "-ar", str(SR), "-f", "f32le", "-"],
                         check=True, capture_output=True).stdout
    return np.frombuffer(raw, dtype="<f4").astype(np.float64).reshape(-1, channels)


def window_db(x, win=0.25):
    w = int(win * SR)
    k = len(x) // w
    p = np.mean(x[: k * w].reshape(k, w, -1) ** 2, axis=(1, 2))
    return 10 * np.log10(p + 1e-14), np.sqrt(p)


def circ_autocorr_peak(env, lo_lag, hi_lag):
    e = env - env.mean()
    ac = np.fft.irfft(np.abs(np.fft.rfft(e)) ** 2, len(e))
    ac = ac / (ac[0] + 1e-12)
    lags = np.arange(lo_lag, hi_lag + 1)
    mid = ac[lags]
    ok = (mid > ac[lags - 1]) & (mid >= ac[(lags + 1) % len(ac)])
    return float(mid[ok].max()) if ok.any() else 0.0


# --verify --ai (loops from tools/media/water_sfx_ai.py): recorded-style water drifts more than the synthesised beds, so the
# quarter-second range limit (dB, 5th to 95th percentile) is per loop. The synthesised limits stay 6 (tight) and 14 (loose).
# The shore and open-water loops come and go with the waves; their limits were set from the takes the user picked by ear
# (2026-11-14: water_lap GEN-0036-03 range 20.8 gap 17.8, open_water GEN-0039-03 range 28.3 gap 21.2) with some margin.
# The gap limit still rejects true silence (the control sits 110 dB under its RMS).
AI_RANGE_LIMIT = {"water_lap_loop": 24.0, "surf_break_loop": 24.0, "open_water_loop": 32.0, "surf_far_loop": 18.0,
                  "stream_babble_loop": 8.0, "river_rush_loop": 8.0, "river_roar_loop": 8.0, "waterfall_loop": 8.0,
                  "underwater_loop": 8.0}
AI_GAP_LIMIT = {"water_lap_loop": 22.0, "surf_break_loop": 22.0, "open_water_loop": 25.0, "surf_far_loop": 18.0}
AI_LENGTH = (15.0, 47.0)  # 47 s take minus 2 s trim minus the crossfade; the synthesised files keep their exact lengths


def analyse(x, kind, autocorr, limit=None, gap_limit=15.0):
    r = {}
    rms = np.sqrt(np.mean(x * x))
    r["rms_db"] = 20 * np.log10(rms + 1e-12)
    r["peak_db"] = 20 * np.log10(np.max(np.abs(x)) + 1e-12)
    wdb, wlin = window_db(x)
    r["gap_db"] = float(r["rms_db"] - wdb.min())  # how far the quietest window sits under the loop's RMS
    r["range_db"] = float(np.percentile(wdb, 95) - np.percentile(wdb, 5))
    n = int(0.05 * SR)
    mono = x.mean(axis=1)
    seam_jump = abs(mono[0] - mono[-1])
    d = np.concatenate((np.diff(mono[:n]), np.diff(mono[-n:])))
    r["seam_ratio"] = float(seam_jump / (np.sqrt(np.mean(d * d)) + 1e-12))
    a, b = np.sqrt(np.mean(mono[-n:] ** 2)), np.sqrt(np.mean(mono[:n] ** 2))
    r["seam_db"] = float(abs(20 * np.log10((a + 1e-12) / (b + 1e-12))))
    r["autocorr"] = circ_autocorr_peak(wlin, 2, len(wlin) // 2) if autocorr else None
    limit = limit or (6.0 if kind == "tight" else 14.0)
    fails = []
    if r["gap_db"] > gap_limit:
        fails.append("gap")
    if r["range_db"] > limit:
        fails.append("range")
    if r["seam_ratio"] > 4.0 or r["seam_db"] > 6.0:
        fails.append("seam")
    if autocorr and r["autocorr"] > 0.5:
        fails.append("period")
    r["fails"] = fails
    r["limit"] = limit
    return r


def sha256(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest().upper()


def verify(args):
    ok = True
    rows = []
    for name, _, kind, autocorr in LOOPS:
        if args.only and not any(s in name for s in args.only):
            continue
        path = os.path.join(args.out_dir, name + ".ogg")
        want_len, want_ch = EXPECT[name]
        x = decode(args.ffmpeg, path, want_ch)
        probe = subprocess.run([args.ffmpeg, "-hide_banner", "-i", path], capture_output=True, text=True).stderr
        info_ok = (f"{SR} Hz" in probe) and (("stereo" in probe) == (want_ch == 2))
        length_ok = AI_LENGTH[0] <= len(x) / SR <= AI_LENGTH[1] if args.ai else abs(len(x) / SR - want_len) < 0.05
        r = analyse(x, kind, autocorr, AI_RANGE_LIMIT[name] if args.ai else None,
                    AI_GAP_LIMIT.get(name, 15.0) if args.ai else 15.0)
        r["fails"] = r["fails"] + ([] if info_ok else ["format"]) + ([] if length_ok else ["length"]) \
            + (["peak"] if r["peak_db"] > -3.0 else []) + (["rms"] if abs(r["rms_db"] + 20.0) > 0.7 else [])
        passed = not r["fails"]
        ok &= passed
        rows.append((name, len(x) / SR, want_ch, r, sha256(path), passed))
        ac = "-" if r["autocorr"] is None else f"{r['autocorr']:.2f}"
        print(f"{'PASS' if passed else 'FAIL'} {name}: {len(x) / SR:.2f} s {want_ch} ch rms {r['rms_db']:.1f} peak {r['peak_db']:.1f} "
              f"gap {r['gap_db']:.1f} dB range {r['range_db']:.1f} dB (max {r['limit']:.0f}) seam {r['seam_ratio']:.2f}/{r['seam_db']:.1f} dB "
              f"autocorr {ac} {r['fails'] or ''}")
    ctl = args.control
    if os.path.exists(ctl):
        x = decode(args.ffmpeg, ctl, 1)
        r = analyse(x, "tight", False)
        need = {"gap", "range"}
        control_ok = need.issubset(r["fails"])
        ok &= control_ok
        print(f"{'PASS' if control_ok else 'FAIL'} control {os.path.basename(ctl)} must fail gap and range: "
              f"gap {r['gap_db']:.1f} dB range {r['range_db']:.1f} dB fails {r['fails']}")
    else:
        print(f"FAIL control {ctl} missing")
        ok = False
    if args.markdown:
        print("\n| File | Length | Channels | RMS dBFS | Peak dBFS | SHA-256 |\n| --- | ---: | ---: | ---: | ---: | --- |")
        for name, ln, ch, r, h, _ in rows:
            print(f"| `{name}.ogg` | {ln:.0f} s | {ch} | {r['rms_db']:.1f} | {r['peak_db']:.1f} | `{h}` |")
    print("VERIFY " + ("PASS" if ok else "FAIL"))
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--wav-dir", default=None)
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or "ffmpeg")
    ap.add_argument("--only", nargs="*", default=None, help="substring filters on output names (also with --verify)")
    ap.add_argument("--verify", action="store_true", help="check the encoded loops instead of generating")
    ap.add_argument("--control", default=os.path.join(_HERE, "..", "..", "resources", "audio", "water", "shore_wave_cc0.ogg"))
    ap.add_argument("--ai", action="store_true", help="with --verify: accept the AI loops (length 15 to 47 s, per-loop range limits)")
    ap.add_argument("--markdown", action="store_true", help="with --verify: print the LICENSE.md table")
    args = ap.parse_args()
    if args.verify:
        sys.exit(verify(args))
    if not args.wav_dir:
        sys.exit("--wav-dir is required to generate")
    generate(args)


if __name__ == "__main__":
    main()
