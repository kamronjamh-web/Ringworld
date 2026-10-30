#!/usr/bin/env python3
"""Procedural weather sound generator for the storm revamp (docs/storm-revamp-s5-rain-audio-design.md).

Every output is synthesised from seeded noise and simple physical models (no recordings), except the thunder
variants, which also process the CC0 thunder clips already in resources/audio/weather (see LICENSE.md there).

Models:
  - Water plips and cave drips: an impact click plus a Minnaert bubble (damped sine whose pitch rises as the
    bubble settles), after van den Doel, "Physically based models for liquid sounds" (2005).
  - Ticks, plinks, hail and the metal/roof loops: modal synthesis (seeded sets of damped partials) excited by
    noise clicks.
  - Loops are built circularly (events wrap around the end, noise beds and modulations are periodic in the loop
    length), so the seam is seamless by construction and needs no crossfade.

Usage (numpy is the only dependency; ffmpeg encodes the Ogg files):
  python -I tools/media/weather_sfx_gen.py --wav-dir <scratch> --out-dir resources/audio/weather [--only name ...]
The thunder variants read resources/audio/weather/thunder_0{1,2,3}.ogg through ffmpeg.
"""
import argparse
import os
import shutil
import subprocess
import sys
import wave
import zlib

import numpy as np

SR = 44100


# ---- basic helpers ------------------------------------------------------------------------------------------------

def rng_for(name):
    return np.random.default_rng(zlib.crc32(name.encode()))


def butter_mag(f, lo=None, hi=None, order=2):
    """Butterworth magnitude response (zero phase when applied in the frequency domain)."""
    f = np.maximum(f, 1e-3)
    h = np.ones_like(f)
    if lo:
        h /= np.sqrt(1.0 + (lo / f) ** (2 * order))
    if hi:
        h /= np.sqrt(1.0 + (f / hi) ** (2 * order))
    return h


def bell(f, f0, gain_db, q):
    """Peaking-filter style magnitude bump."""
    f = np.maximum(f, 1e-3)
    g = 10 ** (gain_db / 20)
    return 1.0 + (g - 1.0) / (1.0 + ((f / f0 - f0 / f) * q) ** 2)


def fft_filter(x, response, pad=0):
    """Zero-phase filter. With pad=0 the filtering is circular (correct for loops)."""
    n = len(x) + pad
    spec = np.fft.rfft(x, n)
    spec *= response(np.fft.rfftfreq(n, 1 / SR))
    return np.fft.irfft(spec, n)[: len(x)]


def noise(rng, n, lo=None, hi=None, order=2, tilt=0.0):
    """Unit-RMS shaped noise; periodic in n (usable as a loop bed)."""
    spec = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1 / SR)
    h = butter_mag(f, lo, hi, order)
    if tilt:
        h = h * (np.maximum(f, 20.0) / 1000.0) ** tilt
    x = np.fft.irfft(spec * h, n)
    return x / (np.sqrt(np.mean(x * x)) + 1e-12)


def t_axis(n):
    return np.arange(n) / SR


def attack(n, ms=0.4):
    """Raised-cosine onset envelope of length n (ones after the onset)."""
    a = np.ones(n)
    k = max(1, min(n, int(ms * 1e-3 * SR)))
    a[:k] = np.sin(0.5 * np.pi * np.arange(k) / k) ** 2
    return a


def fade_out(x, ms):
    k = min(len(x), int(ms * 1e-3 * SR))
    if k > 0:
        x[-k:] *= np.cos(0.5 * np.pi * np.arange(k) / k) ** 2
    return x


def bubble(rng, f0, tau, rise, n):
    """Minnaert bubble: damped sine whose pitch rises by `rise` (fraction) as it settles."""
    t = t_axis(n)
    f = f0 * (1.0 + rise * (1.0 - np.exp(-t / (tau * 1.5))))
    phase = 2 * np.pi * np.cumsum(f) / SR + rng.uniform(0, 2 * np.pi)
    return np.sin(phase) * np.exp(-t / tau) * attack(n, 0.6)


def click(rng, n, tau, lo=1500.0, hi=16000.0, order=2):
    """Short broadband impact transient (filtered noise with a fast exponential decay)."""
    m = max(n, 512)
    x = noise(rng, m, lo, hi, order)[:n]
    return x * np.exp(-t_axis(n) / tau) * attack(n, 0.08)


def modes(rng, n, freqs, taus, amps):
    """Sum of damped partials with random phases."""
    t = t_axis(n)
    out = np.zeros(n)
    for f, tau, a in zip(freqs, taus, amps):
        out += a * np.sin(2 * np.pi * f * t + rng.uniform(0, 2 * np.pi)) * np.exp(-t / tau)
    return out * attack(n, 0.15)


def place(buf, start, sig):
    end = min(len(buf), start + len(sig))
    if end > start:
        buf[start:end] += sig[: end - start]


def fold(buf, length):
    """Wrap everything past `length` onto the start: events that ring past the loop end continue at its start."""
    out = buf[:length].copy()
    rest = buf[length:]
    while len(rest):
        k = min(len(rest), length)
        out[:k] += rest[:k]
        rest = rest[k:]
    return out


def poisson_times(rng, rate, seconds):
    """Event times of a Poisson process; `rate` may be a callable of time for modulated density."""
    if callable(rate):
        peak = max(rate(t) for t in np.linspace(0, seconds, 512))
        times = poisson_times(rng, peak, seconds)
        return times[rng.uniform(0, 1, len(times)) < np.array([rate(t) for t in times]) / peak]
    count = rng.poisson(rate * seconds)
    return np.sort(rng.uniform(0, seconds, count))


def periodic_lfo(rng, length_s, periods, depth):
    """Smooth modulation in [1-depth, 1] that is exactly periodic over the loop (whole cycles only)."""
    n = int(length_s * SR)
    t = t_axis(n)
    x = np.zeros(n)
    for p in periods:
        cycles = max(1, round(length_s / p))
        x += np.sin(2 * np.pi * cycles * t / length_s + rng.uniform(0, 2 * np.pi))
    x = (x - x.min()) / (x.max() - x.min() + 1e-12)
    return 1.0 - depth + depth * x


def modal_loop(rng, length, times, amp_matrix, freqs, taus):
    """Circular modal synthesis: per mode an impulse train (event amplitudes) convolved with the mode's ring."""
    out = np.zeros(length)
    idx = (np.asarray(times) * SR).astype(int) % length
    for k, (f, tau) in enumerate(zip(freqs, taus)):
        train = np.zeros(length)
        np.add.at(train, idx, amp_matrix[:, k])
        hn = min(length, int(tau * 8 * SR))
        h = np.zeros(length)
        th = t_axis(hn)
        h[:hn] = np.sin(2 * np.pi * f * th + rng.uniform(0, 2 * np.pi)) * np.exp(-th / tau) * attack(hn, 0.1)
        out += np.fft.irfft(np.fft.rfft(train) * np.fft.rfft(h), length)
    return out


def scatter_loop(length, times, make):
    """Places per-event signals (make(i) -> array) at the times and wraps the tails."""
    tail = int(4 * SR)
    buf = np.zeros(length + tail)
    for i, t in enumerate(times):
        sig = make(i)
        place(buf, int(t * SR), sig[:tail])
    return fold(buf, length)


def normalise_peak(x, peak_db):
    return x * (10 ** (peak_db / 20) / (np.max(np.abs(x)) + 1e-12))


def normalise_loop(x, rms_db, peak_ceiling_db, remove_mean=True):
    if remove_mean:  # loops only: a one-shot's silent tail would keep the offset as a DC step
        x = x - np.mean(x)
    x = x * (10 ** (rms_db / 20) / (np.sqrt(np.mean(x * x)) + 1e-12))
    peak = np.max(np.abs(x))
    ceiling = 10 ** (peak_ceiling_db / 20)
    if peak > ceiling:
        # Gentle soft knee on the rare loud transients rather than lowering the whole loop.
        knee = ceiling * 0.7
        over = np.abs(x) > knee
        y = x.copy()
        span = ceiling - knee
        y[over] = np.sign(x[over]) * (knee + span * np.tanh((np.abs(x[over]) - knee) / span))
        x = y
    return x


# ---- one-shots ----------------------------------------------------------------------------------------------------

def plip(rng):
    n = int(0.32 * SR)
    out = np.zeros(n)
    f0 = rng.uniform(900, 2600)
    tau = rng.uniform(0.008, 0.022)
    c =click(rng, int(0.004 * SR), rng.uniform(0.0004, 0.0009), 2500, 15000)
    out[: len(c)] += 0.35 * c
    delay = int(rng.uniform(0.001, 0.004) * SR)
    place(out, delay, bubble(rng, f0, tau, rng.uniform(0.25, 0.8), n - delay))
    if rng.uniform() < 0.5:  # a second, smaller bubble from the splash crown falling back
        d2 = int(rng.uniform(0.025, 0.08) * SR)
        place(out, d2, 0.35 * bubble(rng, f0 * rng.uniform(1.3, 2.0), tau * 0.6, rng.uniform(0.2, 0.6), n - d2))
    out = fft_filter(out, lambda f: butter_mag(f, 120, 14000), pad=SR // 2)
    return normalise_peak(fade_out(out, 30), -6)


def leaf_drip(rng):
    n = int(0.45 * SR)
    out = np.zeros(n)
    # Drop on a leaf: a soft papery tap (band noise) and the leaf flexing (low damped tone), then the leaf springing
    # back and a few droplets landing on leaves below.
    tap = click(rng, int(0.014 * SR), rng.uniform(0.002, 0.005), rng.uniform(600, 1000), rng.uniform(2500, 4200), order=4)
    out[: len(tap)] += tap
    flex = modes(rng, int(0.12 * SR), [rng.uniform(170, 420)], [rng.uniform(0.012, 0.03)], [0.45])
    out[: len(flex)] += flex
    t = 0.0
    for k in range(rng.integers(1, 4)):
        t += rng.uniform(0.03, 0.13)
        s = int(t * SR)
        sub = click(rng, int(0.01 * SR), rng.uniform(0.0015, 0.003), 800, 4500, order=4) * rng.uniform(0.15, 0.45)
        place(out, s, sub)
        place(out, s, modes(rng, int(0.06 * SR), [rng.uniform(250, 600)], [rng.uniform(0.008, 0.02)], [rng.uniform(0.1, 0.25)]))
    out = fft_filter(out, lambda f: butter_mag(f, 100, 7000), pad=SR // 2)
    return normalise_peak(fade_out(out, 40), -6)


def hard_tick(rng, metal):
    n = int(0.35 * SR)
    out = np.zeros(n)
    c = click(rng, int(0.006 * SR), rng.uniform(0.0003, 0.0007), 1800, 16000)
    out[: len(c)] += c
    # Splash: a few millisecond spray of tiny droplets.
    spray_n = int(0.06 * SR)
    spray = noise(rng, spray_n, 2500, 11000) * np.exp(-t_axis(spray_n) / rng.uniform(0.008, 0.016)) * attack(spray_n, 1.5) * rng.uniform(0.3, 0.45)
    place(out, int(0.001 * SR), spray)
    if metal:
        base = rng.uniform(1800, 3200)
        ratios = np.array([1.0, 1.59, 2.14, 2.83, 3.67]) * rng.uniform(0.97, 1.03, 5)
        taus = rng.uniform(0.02, 0.09, 5) / np.arange(1, 6) ** 0.4
        amps = rng.uniform(0.05, 0.22, 5)
        place(out, 0, modes(rng, n, base * ratios, taus, amps))
    else:
        place(out, 0, modes(rng, int(0.05 * SR), [rng.uniform(2500, 5000)], [0.003], [0.15]))
    out = fft_filter(out, lambda f: butter_mag(f, 400, 16000), pad=SR // 2)
    return normalise_peak(fade_out(out, 40), -6)


def roof_plink(rng):
    n = int(0.7 * SR)
    out = np.zeros(n)
    # A drop on a sheet panel overhead: the ping of a few plate modes plus the panel's body thump.
    c = click(rng, int(0.005 * SR), rng.uniform(0.0004, 0.0008), 1200, 12000) * 0.6
    out[: len(c)] += c
    base = rng.uniform(700, 1400)
    ratios = np.array([1.0, 1.47, 2.09, 2.61, 3.38, 4.12]) * rng.uniform(0.97, 1.03, 6)
    taus = rng.uniform(0.05, 0.14, 6) / np.arange(1, 7) ** 0.5
    amps = rng.uniform(0.08, 0.3, 6)
    place(out, 0, modes(rng, n, base * ratios, taus, amps))
    place(out, 0, modes(rng, int(0.25 * SR), [rng.uniform(90, 190)], [rng.uniform(0.025, 0.05)], [0.5]))
    out = fft_filter(out, lambda f: butter_mag(f, 60, 9000), pad=SR // 2)
    return normalise_peak(fade_out(out, 80), -6)


def cave_reverb(rng, x, rt60):
    """Synthetic cave room: a few early reflections and a dark exponentially decaying tail."""
    n_ir = int(rt60 * 1.1 * SR)
    t = t_axis(n_ir)
    lows = noise(rng, n_ir, 80, 1800) * np.exp(-6.9 * t / rt60)
    highs = noise(rng, n_ir, 1800, 7000) * np.exp(-6.9 * t / (rt60 * 0.45))
    ir = (lows + 0.5 * highs) * attack(n_ir, 8)
    ir *= 0.04 / np.sqrt(np.sum(ir ** 2) / SR)
    for d, g in zip(rng.uniform(0.012, 0.09, 6), rng.uniform(0.15, 0.4, 6)):
        ir[int(d * SR)] += g
    n = len(x) + n_ir
    wet = np.fft.irfft(np.fft.rfft(x, n) * np.fft.rfft(ir, n), n)
    dry = np.zeros(n)
    dry[: len(x)] = x
    return dry + wet


def cave_drip(rng):
    n = int(0.4 * SR)
    dry = np.zeros(n)
    c = click(rng, int(0.004 * SR), 0.0006, 1500, 12000) * 0.25
    dry[: len(c)] += c
    f0 = rng.uniform(550, 1400)
    place(dry, int(0.002 * SR), bubble(rng, f0, rng.uniform(0.018, 0.04), rng.uniform(0.3, 0.9), n - 100))
    if rng.uniform() < 0.6:
        d2 = int(rng.uniform(0.04, 0.1) * SR)
        place(dry, d2, 0.3 * bubble(rng, f0 * rng.uniform(1.4, 2.2), 0.012, 0.5, n - d2))
    dry = fft_filter(dry, lambda f: butter_mag(f, 150, 9000), pad=SR // 2)
    out = cave_reverb(rng, dry, rng.uniform(1.8, 2.6))[: int(3.0 * SR)]
    return normalise_peak(fade_out(out, 300), -6)


def eaves_dribble(rng):
    n = int(2.2 * SR)
    out = np.zeros(n)
    count = rng.integers(6, 15)
    span = rng.uniform(0.6, 1.5)
    # Drops come off the eave quickly, then thin out as the gathered water drains.
    times = np.sort(span * rng.uniform(0, 1, count) ** 1.6)
    for t in times:
        s = int(t * SR)
        gain = rng.uniform(0.35, 1.0)
        if rng.uniform() < 0.7:
            m = int(0.3 * SR)
            sig = np.zeros(m)
            c = click(rng, int(0.003 * SR), 0.0005, 2500, 14000) * 0.3
            sig[: len(c)] += c
            sig += bubble(rng, rng.uniform(900, 2400), rng.uniform(0.006, 0.016), rng.uniform(0.3, 0.8), m)
        else:
            m = int(0.06 * SR)
            sig = click(rng, m, rng.uniform(0.0004, 0.0012), 1500, 14000)
        place(out, s, gain * sig)
    trickle_n = int((span + 0.3) * SR)
    trickle = noise(rng, trickle_n, 900, 3500) * periodic_lfo(rng, trickle_n / SR, [0.07, 0.11, 0.05], 0.8) ** 2
    trickle *= np.exp(-t_axis(trickle_n) / (span * 0.6)) * attack(trickle_n, 30) * 0.05
    place(out, 0, trickle)
    out = fft_filter(out, lambda f: butter_mag(f, 150, 13000), pad=SR // 2)
    return normalise_peak(fade_out(out, 200), -6)


def hail_hit(rng):
    n = int(0.5 * SR)
    out = np.zeros(n)
    t = 0.0
    gain = 1.0
    gap = rng.uniform(0.05, 0.09)
    for b in range(rng.integers(2, 5)):
        s = int(t * SR)
        c = click(rng, int(0.006 * SR), rng.uniform(0.0002, 0.0005), 1000, 18000)
        ice = modes(rng, int(0.04 * SR), rng.uniform(2500, 7000, 3), rng.uniform(0.004, 0.012, 3), rng.uniform(0.3, 0.6, 3))
        thud = modes(rng, int(0.04 * SR), [rng.uniform(300, 700)], [0.006], [0.25]) if b == 0 else 0
        place(out, s, gain * c)
        place(out, s, gain * ice)
        if b == 0:
            place(out, s, thud)
        t += gap
        gap *= rng.uniform(0.5, 0.75)
        gain *= rng.uniform(0.2, 0.45)
    out = fft_filter(out, lambda f: butter_mag(f, 250, 17000), pad=SR // 2)
    return normalise_peak(fade_out(out, 40), -6)


# ---- loops --------------------------------------------------------------------------------------------------------

def rain_foliage(rng, seconds=24.0):
    L = int(seconds * SR)
    # Dense patter on leaves: thousands of tiny papery taps, a few hundred larger drops with a leaf-flex tone,
    # and a soft canopy hiss. Densities breathe slightly with whole-cycle modulations.
    breath = periodic_lfo(rng, seconds, [6.0, 4.0, 9.0], 0.25)
    pools = [noise(rng, SR, lo, hi) for lo, hi in ((900, 4000), (1500, 6500), (2500, 9000))]
    times = poisson_times(rng, 900, seconds)

    def tap(i):
        m = int(rng.uniform(0.002, 0.008) * SR)
        p = pools[rng.integers(3)]
        o = rng.integers(0, SR - m)
        a = np.exp(rng.normal(-1.6, 0.7))
        return a * p[o:o + m] * np.exp(-t_axis(m) / (m / SR / 3)) * attack(m, 0.2)
    taps = scatter_loop(L, times, tap)
    big_times = poisson_times(rng, 45, seconds)

    def drop(i):
        m = int(0.15 * SR)
        s = click(rng, int(0.01 * SR), rng.uniform(0.0015, 0.003), 900, 7000)
        sig = np.zeros(m)
        sig[: len(s)] += s
        sig += modes(rng, m, [rng.uniform(180, 450)], [rng.uniform(0.01, 0.025)], [0.35])
        return np.exp(rng.normal(-0.6, 0.5)) * sig
    drops = scatter_loop(L, big_times, drop)
    hiss = noise(rng, L, 400, 9000, tilt=-0.3) * 0.12
    out = (taps + 0.5 * drops) * breath + hiss * breath
    out = fft_filter(out, lambda f: butter_mag(f, 120, 12000) * bell(f, 2500, 2.0, 0.7))
    return normalise_loop(out, -24, -4)


def plate_modes(rng, count, f_low, f_high):
    """Seeded inharmonic plate-like mode set (denser toward the top, like a real panel)."""
    m = np.arange(1, 9)
    grid = np.unique(np.round((m[:, None] ** 2 + 1.37 * m[None, :] ** 2).ravel(), 3))
    grid = grid / grid[0]
    freqs = f_low * grid
    freqs = freqs[freqs < f_high][:count] * rng.uniform(0.985, 1.015, min(count, np.sum(freqs < f_high)))
    return freqs


def rain_metal(rng, seconds=24.0):
    L = int(seconds * SR)
    # Rain on sheet metal nearby: tinny pings of one panel (fixed mode set), drop impacts, and a bright spray bed.
    freqs = plate_modes(rng, 40, 620, 9500)
    taus = 0.11 * (freqs / 1000.0) ** -0.45 * rng.uniform(0.8, 1.2, len(freqs))
    times = poisson_times(rng, 380, seconds)
    amps = np.exp(rng.normal(-1.2, 0.8, len(times)))[:, None] * rng.uniform(0, 1, (len(times), len(freqs))) ** 3
    ring = modal_loop(rng, L, times, amps * 0.2, freqs, taus)
    clicks = scatter_loop(L, times, lambda i: amps[i].sum() * 0.08 * click(rng, int(0.003 * SR), 0.0004, 2000, 16000))
    big = poisson_times(rng, 12, seconds)
    bong_f = plate_modes(rng, 8, 140, 900)
    bong_amps = np.exp(rng.normal(-0.5, 0.4, len(big)))[:, None] * rng.uniform(0.2, 1, (len(big), len(bong_f)))
    bong = modal_loop(rng, L, big, bong_amps, bong_f, np.full(len(bong_f), 0.06))
    spray = noise(rng, L, 3000, 14000) * 0.25
    out = ring + clicks + 0.4 * bong + spray * periodic_lfo(rng, seconds, [8.0, 5.0], 0.2)
    out = fft_filter(out, lambda f: butter_mag(f, 150, 15000))
    return normalise_loop(out, -24, -4)


def roof_drum(rng, seconds=24.0):
    L = int(seconds * SR)
    # Heard from under a sheet roof: the panel's low modes drummed densely, the high ring absorbed by the structure,
    # and a low rumble of the whole roof. Interior: dark, full, no splash.
    freqs = plate_modes(rng, 30, 85, 2200)
    taus = 0.09 * (freqs / 200.0) ** -0.4 * rng.uniform(0.8, 1.2, len(freqs))
    times = poisson_times(rng, 520, seconds)
    amps = np.exp(rng.normal(-1.0, 0.7, len(times)))[:, None] * rng.uniform(0, 1, (len(times), len(freqs))) ** 2
    drum = modal_loop(rng, L, times, amps * 0.15, freqs, taus)
    taps = scatter_loop(L, times[::2], lambda i: 0.05 * click(rng, int(0.004 * SR), 0.0008, 300, 3000))
    rumble = noise(rng, L, 40, 400, tilt=-0.5) * 0.35 * periodic_lfo(rng, seconds, [6.0, 8.0, 3.0], 0.3)
    out = drum + taps + rumble
    out = fft_filter(out, lambda f: butter_mag(f, 70, 2600, order=3))
    return normalise_loop(out, -24, -4)


def hail_loop(rng, seconds=24.0):
    L = int(seconds * SR)
    # Hailstones bouncing on hard ground and roofs: loud nearby hits with bounces, many quieter ones, and the dense
    # clatter of the whole field. Log-normal levels give the near/far mix.
    times = poisson_times(rng, 110, seconds)

    def hit(i):
        return np.exp(rng.normal(-1.3, 0.8)) * hail_hit(rng)
    hits = scatter_loop(L, times, hit)
    far_t = poisson_times(rng, 1800, seconds)
    pool = noise(rng, SR, 1200, 9000)

    def tick(i):
        m = int(0.003 * SR)
        o = rng.integers(0, SR - m)
        return np.exp(rng.normal(-3.2, 0.6)) * pool[o:o + m] * np.exp(-t_axis(m) / 0.0006)
    clatter = scatter_loop(L, far_t, tick)
    bed = noise(rng, L, 600, 8000, tilt=-0.4) * 0.02
    out = hits + clatter + bed
    out = fft_filter(out, lambda f: butter_mag(f, 200, 16000))
    return normalise_loop(out, -22, -3)


def gutter_trickle(rng, seconds=24.0):
    L = int(seconds * SR)
    # Water running down a gutter and downpipe: a population of small bubbles whose density and size follow a
    # slowly varying flow, occasional larger glugs, and the pipe's resonance.
    flow = periodic_lfo(rng, seconds, [3.0, 4.8, 8.0, 12.0], 0.6)
    flow_at = lambda t: flow[int(t * SR) % L]
    times = poisson_times(rng, lambda t: 260 * flow_at(t), seconds)

    def bub(i):
        r = np.exp(rng.normal(0.0, 0.45))
        f0 = np.clip(1500 / r, 350, 4000)
        m = int(0.08 * SR)
        return 0.25 * r * bubble(rng, f0, np.clip(0.004 * r, 0.002, 0.03), rng.uniform(0.1, 0.5), m)
    bubbles = scatter_loop(L, times, bub)
    glug_t = poisson_times(rng, 2.5, seconds)
    glugs = scatter_loop(L, glug_t, lambda i: 0.6 * bubble(rng, rng.uniform(160, 320), rng.uniform(0.03, 0.06), 0.4, int(0.3 * SR)))
    hiss = noise(rng, L, 300, 5000, tilt=-0.5) * 0.15 * flow
    out = bubbles + glugs + hiss
    out = fft_filter(out, lambda f: butter_mag(f, 90, 9000) * bell(f, 720, 5.0, 2.0) * bell(f, 1650, 3.0, 2.5))
    return normalise_loop(out, -24, -4)


def foliage_wind(rng, seconds=30.0):
    L = int(seconds * SR)
    # Wind through leaves: a gusting "shh" whose brightness rises with the gust, plus leaf-collision crackle whose
    # density follows the gust squared. Gusts are whole-cycle modulations, so the loop is exact.
    gust = periodic_lfo(rng, seconds, [10.0, 6.0, 15.0, 3.75], 0.75)
    dark = noise(rng, L, 250, 2500, tilt=-0.3)
    bright = noise(rng, L, 1200, 6500)
    shh = (dark * (0.6 + 0.4 * gust) + bright * gust ** 2) * gust ** 1.3 * 0.25
    gust_at = lambda t: gust[int(t * SR) % L]
    times = poisson_times(rng, lambda t: 1600 * gust_at(t) ** 2, seconds)
    pool = noise(rng, SR, 1800, 7500)

    def crackle(i):
        m = int(rng.uniform(0.0005, 0.003) * SR)
        o = rng.integers(0, SR - m)
        return np.exp(rng.normal(-2.2, 0.7)) * pool[o:o + m] * np.hanning(m)
    crackles = scatter_loop(L, times, crackle)
    out = shh + crackles
    out = fft_filter(out, lambda f: butter_mag(f, 150, 9000))
    return normalise_loop(out, -26, -6)


def leaf_skitter(rng, seconds=24.0):
    L = int(seconds * SR)
    # Dry leaves and grass litter driven along the ground by a gust front: clustered scrapes (a few hundred ms of
    # dense dry ticks) over a soft rushing bed, with each cluster accelerating then thinning. Cluster onsets are
    # Poisson events that wrap, so the loop is exact; a slow periodic modulation sets the gust envelope.
    gust = periodic_lfo(rng, seconds, [8.0, 4.8, 12.0, 3.0], 0.6)
    gust_at = lambda t: gust[int(t * SR) % L]
    pool = noise(rng, SR, 1500, 9000)
    low_pool = noise(rng, SR, 400, 2800)
    cluster_t = poisson_times(rng, lambda t: 5.0 * (0.25 + gust_at(t)), seconds)

    def cluster(i):
        n = int(rng.uniform(0.25, 0.9) * SR)
        sig = np.zeros(n)
        amp = np.exp(rng.normal(-1.2, 0.5))
        k = int(rng.integers(8, 40))
        # ticks bunch toward the middle of the cluster (a leaf catching, then tumbling free)
        pos = (rng.beta(2.2, 2.2, k) * (n - int(0.01 * SR))).astype(int)
        for p0 in pos:
            m = int(rng.uniform(0.0004, 0.004) * SR)
            o = int(rng.integers(0, SR - m))
            src = pool if rng.random() < 0.7 else low_pool
            sig[p0:p0 + m] += np.exp(rng.normal(-0.6, 0.6)) * src[o:o + m] * np.hanning(m)
        scrape = noise(rng, n, 900, 5200) * np.hanning(n) * 0.35
        return amp * (sig + scrape * np.hanning(n) ** 0.5)
    clusters = scatter_loop(L, cluster_t, cluster)
    rush = noise(rng, L, 300, 3800, tilt=-0.4) * gust ** 1.5 * 0.08
    out = clusters + rush
    out = fft_filter(out, lambda f: butter_mag(f, 180, 10000))
    return normalise_loop(out, -26, -6)


# ---- thunder ------------------------------------------------------------------------------------------------------

def decode(ffmpeg, path, rate_factor=1.0):
    af = f"asetrate={int(SR * rate_factor)},aresample={SR}" if rate_factor != 1.0 else "anull"
    raw = subprocess.run([ffmpeg, "-v", "error", "-i", path, "-af", af, "-ac", "1", "-ar", str(SR), "-f", "f32le", "-"],
                         check=True, capture_output=True).stdout
    return np.frombuffer(raw, dtype=np.float32).astype(np.float64)


def thunder_near(rng, ffmpeg, source):
    # A close strike: an immediate crack (a train of N-waves from the tortuous channel), a low boom, tearing crackle,
    # then the CC0 rumble arriving behind it. The onset is at sample 0 so it follows the scheduled arrival.
    n = int(6.5 * SR)
    out = np.zeros(n)
    t = 0.0
    for k in range(rng.integers(14, 30)):
        d = int(rng.uniform(0.0006, 0.003) * SR)
        a = rng.uniform(0.4, 1.0) * np.exp(-t / 0.18)
        place(out, int(t * SR), a * np.linspace(1, -1, d))
        t += rng.exponential(0.018)
    boom_n = int(rng.uniform(0.02, 0.045) * SR)
    boom = np.zeros(int(0.4 * SR))
    boom[:boom_n] = np.linspace(1, -1, boom_n)
    boom = fft_filter(boom, lambda f: butter_mag(f, 20, 160), pad=SR)
    place(out, int(rng.uniform(0.0, 0.02) * SR), 4.0 * boom / (np.max(np.abs(boom)) + 1e-9))
    tear_n = int(1.2 * SR)
    gate = np.abs(noise(rng, tear_n, None, 40)) ** 2
    tear = noise(rng, tear_n, 700, 11000) * gate * np.exp(-t_axis(tear_n) / 0.3) * attack(tear_n, 2) * 0.12
    place(out, int(0.01 * SR), tear)
    out = fft_filter(out, lambda f: butter_mag(f, 28, rng.uniform(7000, 11000)), pad=SR)
    out *= 0.5  # crack sits about 6 dB under full scale relative to the rumble, so the rumble keeps the existing clips' level
    rumble = decode(ffmpeg, source, rng.uniform(0.9, 1.05))
    rumble = fft_filter(rumble, lambda f: bell(f, 90, 3.0, 0.8), pad=SR)
    place(out, int(rng.uniform(0.12, 0.3) * SR), 0.9 * rumble / (np.max(np.abs(rumble)) + 1e-9))
    out = out[: int(min(n, (np.nonzero(np.abs(out) > 1e-4 * np.abs(out).max())[0][-1] + 1)))]
    out[0] = 0.0
    out *= attack(len(out), 0.5)
    # Match the loudness of the existing clips (about -25 dBFS RMS); the crack peaks go through a soft knee to -1 dBFS.
    return normalise_loop(fade_out(out, 400), -25.5, -1, remove_mean=False)


def thunder_far(rng, ffmpeg, sources):
    # Distant thunder: the CC0 recording slowed (lower, longer), darkened, layered with a second slowed copy
    # for a rolling tail, and a synthesised sub rumble that swells and fades.
    a = decode(ffmpeg, sources[0], rng.uniform(0.62, 0.78))
    b = decode(ffmpeg, sources[1], rng.uniform(0.6, 0.75))
    n = int(10.0 * SR)
    out = np.zeros(n)
    place(out, 0, a)
    place(out, int(rng.uniform(0.7, 1.6) * SR), 0.55 * b)
    sub = noise(rng, n, 25, 110, tilt=-1.0)
    env = np.zeros(n)
    tt = t_axis(n)
    for c in np.sort(rng.uniform(0.6, 6.5, rng.integers(3, 6))):
        env += rng.uniform(0.4, 1.0) * np.exp(-0.5 * ((tt - c) / rng.uniform(0.4, 1.0)) ** 2)
    out = out / (np.max(np.abs(out)) + 1e-9) + 0.35 * sub * env / (np.max(env) + 1e-9) / 3
    out = fft_filter(out, lambda f: butter_mag(f, 22, rng.uniform(450, 750)), pad=SR)
    out *= attack(n, 120)
    return normalise_peak(fade_out(out, 1200), -4)


# ---- megastorm (docs/megastorm-design.md 5.2) -----------------------------------------------------------------------

def whole_cycle_sines(rng, seconds, freqs, amps, am_periods, am_depth):
    """Sine partials snapped to whole cycles per loop (exactly periodic), each with its own whole-cycle amplitude wobble."""
    n = int(seconds * SR)
    t = t_axis(n)
    out = np.zeros(n)
    for f, a in zip(freqs, amps):
        f = max(1, round(f * seconds)) / seconds
        cycles = max(1, round(seconds / rng.choice(am_periods)))
        am = 1.0 - am_depth * (0.5 + 0.5 * np.sin(2 * np.pi * cycles * t / seconds + rng.uniform(0, 2 * np.pi)))
        out += a * am * np.sin(2 * np.pi * f * t + rng.uniform(0, 2 * np.pi))
    return out


def megastorm_ground_hum(rng, seconds=24.0):
    # Structure-borne bed: 20-60 Hz partials with slow beating, weak 2nd/3rd harmonics so small speakers still carry it, and a
    # 15-90 Hz noise floor. Whole-cycle modulations only, so the loop is exact.
    base = [21.0, 27.5, 34.0, 41.5, 49.0, 58.0]
    out = whole_cycle_sines(rng, seconds, base, [1.0, 0.8, 0.9, 0.6, 0.45, 0.3], [24.0, 12.0, 8.0, 6.0], 0.45)
    out += whole_cycle_sines(rng, seconds, [2 * f for f in base[1:5]], [0.2] * 4, [12.0, 8.0], 0.6)
    out += whole_cycle_sines(rng, seconds, [3 * f for f in base[2:5]], [0.08] * 3, [8.0, 6.0], 0.7)
    out += 0.45 * noise(rng, int(seconds * SR), 15, 90, order=3)
    return normalise_loop(out, -22, -6)


def megastorm_wall_sing(rng, seconds=30.0):
    # The rim walls singing: a slightly inharmonic metallic drone (detuned pairs beat slowly), with air moving through the
    # structure as narrow-band noise on top of the partials.
    n = int(seconds * SR)
    ratios = [1.0, 2.003, 3.01, 4.02, 5.97, 7.9, 10.1]
    amps = [1.0, 0.7, 0.55, 0.4, 0.3, 0.2, 0.12]
    f0 = 98.0
    freqs, a = [], []
    for r, amp in zip(ratios, amps):
        for d in (-0.35, 0.35):
            freqs.append(f0 * r + d * rng.uniform(0.6, 1.4))
            a.append(amp * 0.5)
    out = whole_cycle_sines(rng, seconds, freqs, a, [30.0, 15.0, 10.0, 7.5], 0.7)
    breath = noise(rng, n, 180, 1500, order=3) * periodic_lfo(rng, seconds, [10.0, 6.0], 0.6)
    out += 0.12 * breath
    return normalise_loop(out, -24, -6)


def megastorm_boom(rng, seconds=4.4):
    # The Mach pile-up arriving at once: a crack, a sub thump with a pitch drop, then low rolling bursts that darken and thin
    # out. Shape follows MegastormSound.BoomEnvelope (fast attack, decaying roll, taper into the roar crossfade).
    n = int(seconds * SR)
    t = t_axis(n)
    out = np.zeros(n)
    sweep = 28.0 + 44.0 * np.exp(-t / 0.35)
    out += 1.0 * np.sin(2 * np.pi * np.cumsum(sweep) / SR) * np.exp(-t / 0.9)
    out += 0.45 * noise(rng, n, 300, 3500) * np.exp(-t / 0.12)
    bursts = np.sort(rng.uniform(0.05, 3.0, rng.integers(9, 14)))
    for c in bursts:
        width = rng.uniform(0.22, 0.6)
        cutoff = rng.uniform(110, 420) * np.exp(-c / 3.0)
        env = np.exp(-0.5 * ((t - c) / width) ** 2) * np.exp(-c / 1.8)
        layer = fft_filter(noise(rng, n, 30, cutoff, order=3), lambda f: butter_mag(f, 25, cutoff, 3))
        out += rng.uniform(0.35, 0.8) * layer * env
    out *= attack(n, 90)
    taper = 1.0 - np.clip((t - 0.6 * 4.0) / (0.4 * 4.0 + 0.4), 0.0, 1.0) ** 2 * (3 - 2 * np.clip((t - 2.4) / 2.0, 0, 1))
    out *= np.clip(taper, 0.0, 1.0)
    out = fft_filter(out, lambda f: butter_mag(f, 18, 2600, 2), pad=SR)
    return normalise_peak(fade_out(out, 600), -3)


def megastorm_roar(rng, seconds=30.0):
    # The wall roar bed: a dark turbulent rush (35-900 Hz) with a mid growl and a thin hiss, three independently swelling bands.
    n = int(seconds * SR)
    low = noise(rng, n, 35, 420, order=3, tilt=-0.4) * periodic_lfo(rng, seconds, [9.0, 5.0, 3.3], 0.35)
    mid = noise(rng, n, 160, 900, order=2) * periodic_lfo(rng, seconds, [6.0, 3.75, 2.5], 0.5)
    hiss = noise(rng, n, 1500, 6500, order=2) * periodic_lfo(rng, seconds, [7.5, 4.0], 0.6)
    out = 1.0 * low + 0.55 * mid + 0.14 * hiss
    return normalise_loop(out, -20, -4)


# ---- driver -------------------------------------------------------------------------------------------------------

def write_wav(path, x):
    pcm = np.clip(x, -1.0, 1.0)
    data = (pcm * 32767.0).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(data.tobytes())


def build_jobs(ffmpeg, src_dir):
    jobs = {}
    sets = [("plip", 10, plip), ("leaf_drip", 8, leaf_drip), ("roof_plink", 8, roof_plink), ("cave_drip", 8, cave_drip),
            ("eaves_dribble", 4, eaves_dribble), ("hail_hit", 8, hail_hit)]
    for prefix, count, fn in sets:
        for i in range(1, count + 1):
            name = f"shots/{prefix}_{i:02d}"
            jobs[name] = (lambda fn=fn, name=name: fn(rng_for(name)))
    for i in range(1, 9):
        name = f"shots/hard_tick_{i:02d}"
        jobs[name] = (lambda name=name, metal=i > 4: hard_tick(rng_for(name), metal))
    loops = [("rain_surface_foliage", rain_foliage), ("rain_surface_metal", rain_metal), ("rain_roof_drum", roof_drum),
             ("hail_loop", hail_loop), ("gutter_trickle_loop", gutter_trickle), ("foliage_wind_rustle_loop", foliage_wind), ("leaf_skitter_loop", leaf_skitter)]
    for name, fn in loops:
        jobs[name] = (lambda fn=fn, name=name: fn(rng_for(name)))
    srcs = [os.path.join(src_dir, f"thunder_0{k}.ogg") for k in (1, 2, 3)]
    for i in range(1, 5):
        name = f"thunder_near_{i:02d}"
        jobs[name] = (lambda name=name, i=i: thunder_near(rng_for(name), ffmpeg, srcs[(i - 1) % 3]))
        name = f"thunder_far_{i:02d}"
        jobs[name] = (lambda name=name, i=i: thunder_far(rng_for(name), ffmpeg, [srcs[(i - 1) % 3], srcs[i % 3]]))
    jobs["megastorm_ground_hum_loop"] = lambda: megastorm_ground_hum(rng_for("megastorm_ground_hum_loop"))
    jobs["megastorm_wall_sing_loop"] = lambda: megastorm_wall_sing(rng_for("megastorm_wall_sing_loop"))
    jobs["megastorm_roar_loop"] = lambda: megastorm_roar(rng_for("megastorm_roar_loop"))
    for i in range(1, 4):
        name = f"megastorm_boom_{i}"
        jobs[name] = (lambda name=name: megastorm_boom(rng_for(name)))
    return jobs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--wav-dir", required=True)
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or "ffmpeg")
    ap.add_argument("--thunder-src", default=None, help="directory with thunder_01..03.ogg (default: --out-dir)")
    ap.add_argument("--only", nargs="*", default=None, help="substring filters on output names")
    args = ap.parse_args()
    jobs = build_jobs(args.ffmpeg, args.thunder_src or args.out_dir)
    for name, make in jobs.items():
        if args.only and not any(s in name for s in args.only):
            continue
        x = make()
        if not np.all(np.isfinite(x)):
            sys.exit(f"{name}: non-finite samples")
        wav = os.path.join(args.wav_dir, name.replace("/", "_") + ".wav")
        os.makedirs(os.path.dirname(wav), exist_ok=True)
        write_wav(wav, x)
        ogg = os.path.join(args.out_dir, name + ".ogg")
        os.makedirs(os.path.dirname(ogg), exist_ok=True)
        subprocess.run([args.ffmpeg, "-v", "error", "-y", "-i", wav, "-ac", "1", "-ar", str(SR), "-c:a", "libvorbis",
                        "-q:a", "5", "-map_metadata", "-1", "-fflags", "+bitexact", "-flags:a", "+bitexact", ogg], check=True)
        peak = 20 * np.log10(np.max(np.abs(x)) + 1e-12)
        rms = 20 * np.log10(np.sqrt(np.mean(x * x)) + 1e-12)
        print(f"{name}: {len(x) / SR:.3f} s, peak {peak:.1f} dBFS, rms {rms:.1f} dBFS")


if __name__ == "__main__":
    main()
