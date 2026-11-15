#!/usr/bin/env python3
"""TR-7d lead prototype (CPU only): unbiased thinning of the far tree cards.

Reads a read-only dump of the baked card atlas (level-0 albedo and coverage, the selected normal mips), the LOD3 card
geometry and the checked-in calibration, written by .godot/tr7d/dump_tr7d.gd (the `dump-script` command writes that
script again if .godot/tr7d has been pruned):
    tools/use_godot_48.ps1 --headless --script res://.godot/tr7d/dump_tr7d.gd      (isolated APPDATA)
and compares two ways of thinning the cards to the calibrated crown area:

  current   Image.GenerateMipmaps + ImportedTreeImpostorCatalog.PreserveAlphaCoverage(cutoff), sampled trilinear with
            4x anisotropy, alpha tested against the calibrated cutoff (the shipped path, ported 1:1).
  thinned   TR-7d: per mip level alpha = 1 - noise / coverage (clamped, 8 bit), coverage = box average of the baked
            coverage, colour = coverage-weighted mean (linear light) of the children, noise = one fixed blue-noise
            value per texel and level; one nearest texel read at an explicit integer level, alpha tested against a
            cutoff: a texel passes with probability coverage * (1 - cutoff). Refinements measured here: the balanced
            order with an exact area per card (balance_thinned), noise shared in clumps (noise_masks(clump=8)), and,
            in the pictures only, the bare trunk kept solid (trunk_guess).

Commands (python tools/tr7d_card_thinning_prototype.py <command>):
  texture   per variant and mip level: area against the cutoff, colour and light of the passing texels
  render    crossed cards drawn at the 325 / 650 / 900 m pixel sizes, current against the thinned variants: area, light, quarter-pixel change; area against the cutoff
  quad      the far quad (one picture, face on) at 900 m and beyond
  dolly     one tree from 300 m to 1,000 m: change per metre, level switches
  sheet     preview sheet (maple v3, fir v13, pine v19 at 325 / 650 / 900 m) and the true-size forest strips
  motion [start stop]   frames of a slow sideways camera move over the forest strips (for tools/media/ffmpeg_tools.ps1 mp4 and flicker)
  dump-script           writes .godot/tr7d/dump_tr7d.gd
  all       everything but motion
Output: .godot/tr7d/ (tables as .md and .csv, sheets as .png). Nothing under resources/ or materials/ is read or written
except through the dump. This file is the reference for the C# builder of the TR-7d work order (void_and_cluster,
noise_masks, build_thinned, balance_thinned); design note: docs/work-orders/terrain-vegetation/TR.md, "TR-7d".
"""
import json, os, sys, math, hashlib
import numpy as np
from PIL import Image, ImageDraw

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
OUT = os.path.join(ROOT, ".godot", "tr7d")
DATA = os.path.join(OUT, "data")
S = 256
LEVELS = 9
SPECIES = ["maple", "fir", "pine"]
HEIGHT_M = [18.0, 11.0, 20.0]          # typical placed heights (tree_billboard_match_gpu_test.gd)
PX_PER_M_325 = 128.0 / (2.0 * 325.0 * math.tan(math.radians(8.3 * 0.5)))  # the test's pixel scale: 2.714 px per metre at 325 m
DISTANCES = [325.0, 650.0, 900.0]
ELEVATIONS = [90.0, 30.0, 8.0]
MAX_ANISO = 4                          # project default anisotropic_filtering_level (4x)
SKY = np.array([0.62, 0.66, 0.72])     # the D7 / D8 sheets' neutral sky grey (sRGB)

SRGB_LUT = np.array([(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4) for c in (np.arange(256) / 255.0)])
def lin2srgb(x):
    x = np.clip(x, 0, 1)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(np.maximum(x, 1e-12), 1 / 2.4) - 0.055)
def lum(rgb): return rgb[..., 0] * 0.2126 + rgb[..., 1] * 0.7152 + rgb[..., 2] * 0.0722
def species_of(v): return 0 if v < 12 else (1 if v < 15 else 2)
def level_size(l): return max(1, S >> l)
def normal_offset(l): return sum(level_size(k) ** 2 * 4 for k in range(l))

# ---------------------------------------------------------------------------------------------------------- data
class Data:
    def __init__(self):
        self.info = json.load(open(os.path.join(DATA, "info.json")))
        self.albedo = np.fromfile(os.path.join(DATA, "albedo_base.bin"), dtype=np.uint8).reshape(30, S, S, 4)
        nb = normal_offset(LEVELS)
        raw = np.fromfile(os.path.join(DATA, "normal_mips.bin"), dtype=np.uint8).reshape(30, nb)
        self.normal = [[raw[v, normal_offset(l):normal_offset(l) + level_size(l) ** 2 * 4].reshape(level_size(l), level_size(l), 4)[..., :3]
                        for l in range(LEVELS)] for v in range(30)]
        self.cut = np.array(self.info["calibration"]["cutoffs"])
        self.quad_cut = np.array(self.info["calibration"]["quad_cutoffs"])
        self._cards = {}
    def cards(self, v):
        if v not in self._cards: self._cards[v] = build_cards(self.info["variants"][v])
        return self._cards[v]

# ---------------------------------------------------------------------------------------------------------- cards (port of TreeImpostorAtlasBaker.BuildCards / Finish / OwnerMap)
class Card: pass

def build_cards(entry):
    P = np.array(entry["p"], dtype=np.float64); N = np.array(entry["n"], dtype=np.float64)
    UV = np.array(entry["uv"], dtype=np.float64); T = np.array(entry["t"], dtype=np.float64)
    normals, offsets, members = [], [], []
    for t in range(0, len(P) - 2, 3):
        cross = np.cross(P[t + 1] - P[t], P[t + 2] - P[t])
        if np.linalg.norm(cross) * 0.5 < 1e-9: continue
        n = cross / np.linalg.norm(cross)
        if n[0] < -1e-4 or (abs(n[0]) <= 1e-4 and (n[2] < -1e-4 or (abs(n[2]) <= 1e-4 and n[1] < 0))): n = -n
        d = n.dot(P[t]); group = -1
        for g in range(len(normals)):
            if normals[g].dot(n) > 0.9995 and abs(offsets[g] - d) < 0.01: group = g; break
        if group < 0: normals.append(n); offsets.append(d); members.append([]); group = len(normals) - 1
        members[group].append(t)
    cards = []
    for g, tris in enumerate(members):
        parent = list(range(len(tris)))
        def find(i):
            while parent[i] != i: parent[i] = parent[parent[i]]; i = parent[i]
            return i
        owner = {}
        for i, t in enumerate(tris):
            for k in range(3):
                key = tuple(np.round(P[t + k] * 1000).astype(int)) + tuple(np.round(UV[t + k] * 1000).astype(int))
                if key in owner: parent[find(i)] = find(owner[key])
                else: owner[key] = i
        by_root = {}
        for i, t in enumerate(tris):
            r = find(i)
            if r not in by_root: by_root[r] = Card(); by_root[r].tris = []; cards.append(by_root[r])
            by_root[r].tris.append(t)
        for c in by_root.values(): finish(c, normals[g], P, N, UV, T)
    owner = np.full((S, S), -1, dtype=np.int16)
    sub = 3
    for ci, c in enumerate(cards):
        for t in c.tris:
            a, b, d = UV[t] * S * sub, UV[t + 1] * S * sub, UV[t + 2] * S * sub
            area = (b[0] - a[0]) * (d[1] - a[1]) - (d[0] - a[0]) * (b[1] - a[1])
            if abs(area) < 1e-9: continue
            x0 = max(0, int(math.floor(min(a[0], b[0], d[0])))); x1 = min(S * sub - 1, int(math.ceil(max(a[0], b[0], d[0]))))
            y0 = max(0, int(math.floor(min(a[1], b[1], d[1])))); y1 = min(S * sub - 1, int(math.ceil(max(a[1], b[1], d[1]))))
            if x1 < x0 or y1 < y0: continue
            xs, ys = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
            w0 = ((b[0] - xs) * (d[1] - ys) - (d[0] - xs) * (b[1] - ys)) / area
            w1 = ((d[0] - xs) * (a[1] - ys) - (a[0] - xs) * (d[1] - ys)) / area
            inside = (w0 >= 0) & (w1 >= 0) & (1 - w0 - w1 >= 0)
            ty = (ys[inside] // sub).astype(int); tx = (xs[inside] // sub).astype(int)
            free = owner[ty, tx] < 0
            owner[ty[free], tx[free]] = ci
    for ci, c in enumerate(cards): c.index = ci
    return cards, owner

def finish(c, plane, P, N, UV, T):
    vn = np.zeros(3); tan = np.zeros(3); winding = np.zeros(3); w = T[c.tris[0]][3]
    for t in c.tris:
        winding -= np.cross(P[t + 1] - P[t], P[t + 2] - P[t])
        for k in range(3): vn += N[t + k]; tan += T[t + k][:3]
    c.normal = vn / np.linalg.norm(vn) if vn.dot(vn) > 1e-12 else np.array([0.0, 1, 0])
    c.tangent = tan / np.linalg.norm(tan) if tan.dot(tan) > 1e-12 else np.array([1.0, 0, 0])
    b = np.cross(c.normal, c.tangent); c.binormal = b / np.linalg.norm(b) * w
    c.plane = plane; c.is_top = abs(plane[1]) > 0.7
    c.view = (plane if plane[1] >= 0 else -plane) if c.is_top else (plane if plane.dot(c.normal) >= 0 else -plane)
    c.frame_sign = 1.0 if winding.dot(c.view) >= 0 else -1.0
    ref = np.array([0.0, 1, 0]) if abs(plane[1]) < 0.9 else np.array([1.0, 0, 0])
    e1 = np.cross(ref, plane); c.e1 = e1 / np.linalg.norm(e1)
    e2 = np.cross(plane, c.e1); c.e2 = e2 / np.linalg.norm(e2)
    rows = np.array([[P[t + k].dot(c.e1), P[t + k].dot(c.e2), 1.0] for t in c.tris for k in range(3)])
    uv = np.array([UV[t + k] for t in c.tris for k in range(3)])
    sol, *_ = np.linalg.lstsq(rows, uv, rcond=None)          # uv = [s t 1] . sol
    c.M = sol[:2].T.copy(); c.b = sol[2].copy()
    c.st = rows[:, :2].reshape(-1, 3, 2)                     # triangles in the plane's (s, t)
    c.p0 = P[c.tris[0]].copy()
    c.area = sum(np.linalg.norm(np.cross(P[t + 1] - P[t], P[t + 2] - P[t])) * 0.5 for t in c.tris)

def card_id_levels(data, v):
    """Card id per texel and level: level 0 from the owner map, coarser levels from the child whose normal bytes the stored mip texel carries."""
    cards, owner = data.cards(v)
    ids = [owner.copy()]
    for l in range(1, LEVELS):
        n = level_size(l); prev = ids[-1]; pn = data.normal[v][l - 1]; cn = data.normal[v][l]
        out = np.full((n, n), -1, dtype=np.int16)
        cov_prev = None
        for k in (3, 2, 1, 0):  # first matching child wins (k = 0 written last)
            child_n = pn[(k >> 1)::2, (k & 1)::2][:n, :n]; child_id = prev[(k >> 1)::2, (k & 1)::2][:n, :n]
            match = np.all(child_n == cn, axis=-1) & (child_id >= 0)
            out[match] = child_id[match]
        ids.append(out)
    return ids

def world_normals(data, v, ids):
    """Per level: the world normal each texel shades with when its card is seen from the baked side, and the same mirrored through the card plane (a card seen from behind)."""
    cards, _ = data.cards(v)
    T = np.array([c.tangent for c in cards]); B = np.array([c.binormal for c in cards]); Nn = np.array([c.normal for c in cards]); sg = np.array([c.frame_sign for c in cards])
    front, back = [], []
    for l in range(LEVELS):
        raw = data.normal[v][l].astype(np.float64) / 255.0 * 2 - 1
        idx = np.maximum(ids[l], 0)
        f = (T[idx] * raw[..., 0:1] + B[idx] * raw[..., 1:2] + Nn[idx] * raw[..., 2:3]) * sg[idx][..., None]
        bk = (T[idx] * raw[..., 0:1] + B[idx] * raw[..., 1:2] - Nn[idx] * raw[..., 2:3]) * sg[idx][..., None]
        front.append(f / np.maximum(np.linalg.norm(f, axis=-1, keepdims=True), 1e-9)); back.append(bk / np.maximum(np.linalg.norm(bk, axis=-1, keepdims=True), 1e-9))
    return front, back

def sun_dirs(elevation, count=12):
    el = math.radians(elevation)
    if elevation >= 89.9: return np.array([[0.0, 1.0, 0.0]])
    return np.array([[math.cos(el) * math.cos(math.radians(15 + 360.0 / count * k)), math.sin(el), math.cos(el) * math.sin(math.radians(15 + 360.0 / count * k))] for k in range(count)])

def response(n, elevation):
    """Mean over the sun azimuths of max(n.L, 0)."""
    L = sun_dirs(elevation)
    return np.maximum(n @ L.T, 0).mean(axis=-1)

# ---------------------------------------------------------------------------------------------------------- blue noise (void and cluster)
def hash_u32(i, seed):
    """The integer hash every fixed random choice of the rule uses (uint32 arithmetic)."""
    M = np.uint64(0xFFFFFFFF)
    h = ((np.asarray(i).astype(np.uint64) * np.uint64(73856093)) ^ (np.uint64(seed) * np.uint64(83492791))) & M
    h = ((h ^ (h >> np.uint64(13))) * np.uint64(1274126177)) & M
    h = ((h ^ (h >> np.uint64(16))) * np.uint64(2246822519)) & M
    return h ^ (h >> np.uint64(15))

def void_and_cluster(n, seed):
    """Blue-noise rank mask (void and cluster, Ulichney 1993) of n x n texels, toroidal, in integer arithmetic so that any implementation gives the same ranks:
    kernel k(dx, dy) = round(2^20 * exp(-(dx^2 + dy^2) / 4.5)) with wrapped distances; energy = sum of the kernel over the set points (int64);
    start pattern = the n*n/10 texels with the smallest hash_u32(index, seed) (ties by index); every argmax / argmin takes the lowest index on a tie."""
    N = n * n
    start = np.lexsort((np.arange(N), hash_u32(np.arange(N), seed)))
    if n <= 2:
        rank = np.zeros(N, dtype=np.int64); rank[start] = np.arange(N); return rank.reshape(n, n)
    ax = np.minimum(np.arange(n), n - np.arange(n)).astype(np.float64)
    g = np.rint(1048576.0 * np.exp(-(ax[:, None] ** 2 + ax[None, :] ** 2) / 4.5)).astype(np.int64)
    g2 = np.tile(g, (2, 2))
    def kern(i): y, x = divmod(i, n); return g2[n - y:2 * n - y, n - x:2 * n - x]
    BIG = np.int64(1) << np.int64(60)
    ones = max(1, N // 10)
    pat = np.zeros(N, dtype=bool); pat[start[:ones]] = True
    e = np.zeros((n, n), dtype=np.int64)
    for i in np.flatnonzero(pat): e += kern(i)
    ef = e.reshape(-1)
    while True:  # relax: move the tightest cluster into the largest void until they are the same texel
        c = int(np.where(pat, ef, -1).argmax()); pat[c] = False; e -= kern(c)
        vd = int(np.where(pat, BIG, ef).argmin())
        pat[vd] = True; e += kern(vd)
        if vd == c: break
    rank = np.zeros(N, dtype=np.int64)
    p1 = pat.copy(); e1 = e.copy(); e1f = e1.reshape(-1)
    for r in range(ones - 1, -1, -1):   # phase 1: remove the tightest clusters
        c = int(np.where(p1, e1f, -1).argmax()); p1[c] = False; e1 -= kern(c); rank[c] = r
    half = N // 2
    for r in range(ones, half):         # phase 2: fill the largest voids
        vd = int(np.where(pat, BIG, ef).argmin()); pat[vd] = True; e += kern(vd); rank[vd] = r
    z = (g.sum() - e); zf = z.reshape(-1)  # phase 3: energy of the unset texels (now the minority); set the tightest cluster of them
    for r in range(half, N):
        c = int(np.where(pat, -1, zf).argmax()); pat[c] = True; z -= kern(c); rank[c] = r
    return rank.reshape(n, n)

def noise_masks(kind="blue", clump=1):
    """Per level a threshold u in (0, 1) per texel: (rank + 0.5) / texels.
    clump = 1: one blue-noise value per texel (the D9 wording). clump = k (a power of two): neighbouring texels share their value over about k level-0 texels, so texels pass in
    clumps instead of singly: at level l, with j = clump / 2^l, the rank mask of level l + log2(j) (j times larger texels) is read bilinearly at the texel centres (wrapped, exact
    integer weights) and ranked again (ties by hash_u32(index, 9000 + l), then index), which keeps the values uniform, so a texel still passes with probability
    coverage * (1 - cutoff). Levels where j <= 1 (or the coarser mask would be under 4 texels) use the per-texel mask."""
    base = []
    for l in range(LEVELS):
        n = level_size(l)
        if kind == "white":
            rank = np.zeros(n * n, dtype=np.int64); rank[np.lexsort((np.arange(n * n), hash_u32(np.arange(n * n), 7000 + l)))] = np.arange(n * n); rank = rank.reshape(n, n)
        else:
            path = os.path.join(DATA, f"blue_rank_int_{n}.npy")
            if os.path.exists(path): rank = np.load(path)
            else: rank = void_and_cluster(n, 1000 + l); np.save(path, rank)
        base.append((rank + 0.5) / (n * n))
    if clump <= 1: return base
    masks = []
    for l in range(LEVELS):
        k = clump >> l; n = level_size(l)
        if k <= 1 or n // k < 4: masks.append(base[l]); continue
        src = np.rint(base[l + int(math.log2(k))] * (n // k) ** 2 - 0.5).astype(np.int64); m = n // k   # the ranks of the mask with k times larger texels
        num = 2 * np.arange(n) + 1 - k                      # texel centre in the coarse mask, in units of 1 / (2k) coarse texels
        i0 = num // (2 * k); f = num - 2 * k * i0           # floor and the fraction's numerator (0 .. 2k - 1)
        rows = src[i0 % m, :] * (2 * k - f)[:, None] + src[(i0 + 1) % m, :] * f[:, None]
        val = rows[:, i0 % m] * (2 * k - f)[None, :] + rows[:, (i0 + 1) % m] * f[None, :]     # bilinear, exact integers
        order = np.lexsort((np.arange(n * n), hash_u32(np.arange(n * n), 9000 + l), val.reshape(-1)))
        rank = np.zeros(n * n, dtype=np.int64); rank[order] = np.arange(n * n)
        masks.append(((rank + 0.5) / (n * n)).reshape(n, n))
    return masks

# ---------------------------------------------------------------------------------------------------------- mip chains
def godot_mips(rgba):
    """Image.generate_mipmaps on RGBA8: rounded 2 x 2 byte average, no linear-light conversion."""
    out = [rgba]
    for l in range(1, LEVELS):
        p = out[-1].astype(np.uint32)
        out.append(((p[0::2, 0::2] + p[0::2, 1::2] + p[1::2, 0::2] + p[1::2, 1::2] + 2) >> 2).astype(np.uint8))
    return out

def bilinear_sub(a, sub=4):
    """a: (n, n) float; value at the sub x sub sample positions of every texel, clamped edges (CoveredTexels)."""
    n = a.shape[0]
    pos = (np.arange(n * sub) // sub) + ((np.arange(n * sub) % sub) + 0.5) / sub - 0.5
    i0 = np.floor(pos).astype(int); f = pos - i0
    ia = np.clip(i0, 0, n - 1); ib = np.clip(i0 + 1, 0, n - 1)
    rows = a[ia, :] * (1 - f)[:, None] + a[ib, :] * f[:, None]
    return rows[:, ia] * (1 - f)[None, :] + rows[:, ib] * f[None, :]

def preserve_alpha_coverage(mips, cutoff):
    """Port of ImportedTreeImpostorCatalog.PreserveAlphaCoverage: alpha of every mip scaled so the bilinear 4 x 4 sub-sample area at the cutoff equals level 0's."""
    out = [m.copy() for m in mips]
    thr = np.float32(cutoff * 255.0)
    def covered(alpha, scale): return (bilinear_sub(alpha.astype(np.float32)) * np.float32(scale) >= thr).sum() / 16.0
    base = covered(mips[0][..., 3], 1.0)
    for l in range(1, LEVELS):
        n = level_size(l); target = base * (n * n / float(S * S)); lo, hi = 0.25, 4.0
        a = mips[l][..., 3]
        for _ in range(14):
            mid = math.sqrt(lo * hi)
            if covered(a, mid) < target: lo = mid
            else: hi = mid
        scale = math.sqrt(lo * hi)
        out[l][..., 3] = np.minimum(255, np.round(a.astype(np.float32) * np.float32(scale))).astype(np.uint8)
    return out

def trunk_guess(data, v):
    """Preview only: the bare trunk below the crown, guessed as the texels whose colour is bark (red at least green + 4 in sRGB bytes) and whose height on their side card is
    below the catalog's trunk height. The shipped bake would write it exactly: the share of each texel's sub-sample hits that lie on the bark surface below the trunk height."""
    cards, owner = data.cards(v); rgba0 = data.albedo[v]; dim = data.info["dimensions"][v]; limit = dim[3] / dim[1]
    bark = (rgba0[..., 3] > 0) & (rgba0[..., 0].astype(int) >= rgba0[..., 1].astype(int) + 4)
    height = np.full((S, S), np.inf)
    ys, xs = np.mgrid[0:S, 0:S]; uv = np.stack([(xs + 0.5) / S, (ys + 0.5) / S], axis=-1)
    for c in cards:
        if c.is_top: continue
        st = (uv - c.b) @ np.linalg.inv(c.M).T                       # plane coordinates of every texel centre
        y = st[..., 0] * c.e1[1] + st[..., 1] * c.e2[1] + c.plane[1] * c.p0.dot(c.plane)
        height[owner == c.index] = y[owner == c.index]
    return bark & (height <= limit)

def build_thinned(rgba0, masks, solid0=None):
    """TR-7d chain. Returns per level (rgba uint8 with the thinned alpha, coverage float). The reference rule for the C# builder:
       coverage[l] = 2 x 2 box average of coverage[l - 1] (level 0: alpha / 255);
       colour[l]   = sum(coverage_child * linear(colour_child)) / sum(coverage_child), back to sRGB bytes (plain mean of the children where the sum is 0);
       alpha[l]    = round(255 * clamp(1 - u[l] / coverage[l], 0, 1)), 0 where coverage is 0; u[l] the level's fixed noise in (0, 1)."""
    cov = rgba0[..., 3].astype(np.float64) / 255.0
    col = SRGB_LUT[rgba0[..., :3]]
    sol = cov * solid0 if solid0 is not None else None   # bare-trunk coverage (solid0: bool per level-0 texel)
    out = []
    for l in range(LEVELS):
        if l > 0:
            if sol is not None: sol = (sol[0::2, 0::2] + sol[0::2, 1::2] + sol[1::2, 0::2] + sol[1::2, 1::2]) * 0.25
            w = cov[..., None]
            num = (col * w)[0::2, 0::2] + (col * w)[0::2, 1::2] + (col * w)[1::2, 0::2] + (col * w)[1::2, 1::2]
            den = cov[0::2, 0::2] + cov[0::2, 1::2] + cov[1::2, 0::2] + cov[1::2, 1::2]
            plain = (col[0::2, 0::2] + col[0::2, 1::2] + col[1::2, 0::2] + col[1::2, 1::2]) * 0.25
            col = np.where(den[..., None] > 0, num / np.maximum(den[..., None], 1e-12), plain)
            cov = den * 0.25
        u = masks[l]
        alpha = np.where(cov > 0, np.clip(1.0 - u / np.maximum(cov, 1e-12), 0, 1), 0.0)
        if sol is not None:   # a texel that is mostly bare trunk is never thinned: drawn when at least half covered (every card shows the same trunk in the same place)
            bark = sol * 2 >= cov
            alpha = np.where(bark & (cov > 0), np.where(cov >= 0.5, 1.0, 0.0), alpha)
        rgba = np.empty(cov.shape + (4,), dtype=np.uint8)
        rgba[..., :3] = np.round(lin2srgb(col) * 255).astype(np.uint8)
        rgba[..., 3] = np.round(alpha * 255).astype(np.uint8)
        out.append((rgba, cov.copy(), (sol * 2 >= cov) & (cov > 0) if sol is not None else np.zeros(cov.shape, dtype=bool)))
    return out


def balance_thinned(chain, masks, ids, normal_bytes, window=16):
    """TR-7d refinement ("balanced order, exact count"), per card and level, on the output of build_thinned():
       1. order the card's covered texels by t = u / coverage (the cutoff distance at which the plain rule would draw them; ties by texel index);
       2. keep the first round(A) of them, A = the card's coverage sum in texels of that level (the rest get alpha 0: at cutoff 0 the card draws exactly its baked area);
       3. re-order them by error diffusion inside a sliding window of `window` texels of that order: the next texel is the window member that keeps the running error of
          the feature sums smallest (features: albedo luminance times the six clamped axes of the stored normal, divided by the sum of their six coverage-weighted
          card means, and linear r, g, b divided by the sum of their three means), so every prefix of the order has the crown's mean light response and colour;
       4. alpha of the k-th texel (k from 0) = max(1, round(255 * (1 - (k + 0.5) / A))): the card's drawn area is A * (1 - cutoff) to within half a texel.
       Texels with coverage but no card keep the plain rule."""
    out = []
    for l, (rgba, cov, bark) in enumerate(chain):
        rgba = rgba.copy(); n = level_size(l)
        lin = SRGB_LUT[rgba[..., :3]]; raw = normal_bytes[l].astype(np.float64) * 2 - 255
        lm = lum(lin)
        feat = np.concatenate([np.stack([np.maximum(raw[..., 0], 0), np.maximum(-raw[..., 0], 0), np.maximum(raw[..., 1], 0), np.maximum(-raw[..., 1], 0),
                                         np.maximum(raw[..., 2], 0), np.maximum(-raw[..., 2], 0)], axis=-1) * lm[..., None], lin], axis=-1).reshape(n * n, 9)
        covf = cov.reshape(-1); idf = ids[l].reshape(-1); uf = masks[l].reshape(-1); alpha = rgba[..., 3].reshape(-1).copy(); leaf = ~bark.reshape(-1)
        for card in np.unique(idf[(idf >= 0) & (covf > 0) & leaf]):
            members = np.flatnonzero((idf == card) & (covf > 0) & leaf)
            A = covf[members].sum(); take = int(min(len(members), math.floor(A + 0.5)))
            t = uf[members] / covf[members]
            order = members[np.argsort(t, kind="stable")]
            alpha[members] = 0
            if take <= 0: continue
            order = order[:take]
            w = covf[members][:, None]; mu = (feat[members] * w).sum(0) / A
            scale = np.concatenate([np.full(6, max(mu[:6].sum(), 1e-9)), np.full(3, max(mu[6:].sum(), 1e-9))]); f = feat[order] / scale; target = mu / scale
            E = np.zeros(9); win = list(range(min(window, take))); nxt = len(win); seq = []
            while win:
                cand = f[win]; d = ((E + target - cand) ** 2).sum(1); j = int(np.argmin(d))
                E += target - cand[j]; seq.append(win.pop(j))
                if nxt < take: win.append(nxt); nxt += 1
            k = np.arange(take)
            alpha[order[seq]] = np.maximum(1, np.round(255.0 * (1.0 - (k + 0.5) / A))).astype(np.uint8)
        rgba[..., 3] = alpha.reshape(n, n)
        out.append((rgba, cov, bark))
    return out

# ---------------------------------------------------------------------------------------------------------- texture-space statistics
def texture_stats(data, masks):
    rows = []; cut_scan = [0.05, 0.2, 0.35, 0.5, 0.65, 0.8, 0.9]
    lines_area = []; summary = {}
    for v in range(30):
        cards, owner = data.cards(v)
        ids = card_id_levels(data, v); wn_front, _ = world_normals(data, v, ids)
        side = np.array([not c.is_top for c in cards])
        rgba0 = data.albedo[v]; cov0 = rgba0[..., 3] / 255.0; lin0 = SRGB_LUT[rgba0[..., :3]]
        side0 = (owner >= 0) & side[np.maximum(owner, 0)]
        w0 = cov0 * side0
        ref_area_all = cov0.sum(); ref_area = w0.sum()
        ref_rgb = (lin0 * w0[..., None]).sum((0, 1)) / ref_area
        resp0 = [response(wn_front[0], e) for e in ELEVATIONS]
        ref_light = [(lum(lin0) * resp0[k] * w0).sum() / ref_area for k in range(3)]
        cur_base = godot_mips(rgba0)
        plain = build_thinned(rgba0, masks); thin = balance_thinned(plain, masks, ids, data.normal[v])
        c_cal = float(data.cut[v])
        cur = preserve_alpha_coverage(cur_base, c_cal)
        # the area the current cards draw at level 0 at the calibrated cutoff (bilinear, as the catalog counts it), as a share of the baked coverage
        share = (bilinear_sub(cur[0][..., 3].astype(np.float32)) >= c_cal * 255.0).sum() / 16.0 / ref_area_all
        c_thin = 1.0 - share   # the thinned cutoff that draws the same area
        for l in range(0, 6):
            n = level_size(l); texel_area = 4.0 ** l
            sidel = (ids[l] >= 0) & side[np.maximum(ids[l], 0)]
            # ---- current: bilinear 4 x 4 sub-samples
            a = bilinear_sub(cur[l][..., 3].astype(np.float32)); passed = a >= c_cal * 255.0
            colour = np.stack([bilinear_sub(SRGB_LUT[cur[l][..., k]]) for k in range(3)], axis=-1)
            rep = lambda x: np.repeat(np.repeat(x, 4, axis=0), 4, axis=1)
            ps = passed & rep(sidel)
            cur_area = passed.sum() / 16.0 * texel_area
            cur_rgb = colour[ps].mean(0) if ps.any() else np.zeros(3)
            cur_light = [(lum(colour) * rep(response(wn_front[l], e)))[ps].mean() if ps.any() else 0.0 for e in ELEVATIONS]
            # ---- thinned: whole texels
            def thin_measure(chain, cutoff):
                rgba, cov, _ = chain[l]
                p = rgba[..., 3] >= cutoff * 255.0
                p &= rgba[..., 3] > 0
                psd = p & sidel
                lin = SRGB_LUT[rgba[..., :3]]
                area = p.sum() * texel_area
                rgb = lin[psd].mean(0) if psd.any() else np.zeros(3)
                light = [(lum(lin) * response(wn_front[l], e))[psd].mean() if psd.any() else 0.0 for e in ELEVATIONS]
                return area, rgb, light, psd.sum()
            t_area, t_rgb, t_light, t_n = thin_measure(thin, c_thin)
            w_area, w_rgb, w_light, _ = thin_measure(plain, c_thin)
            lin_dev = []
            for c in cut_scan:
                a_c = thin_measure(thin, c)[0]
                lin_dev.append(a_c / (ref_area_all * (1 - c)))
            # the current chain along the cutoff, rebuilt for each cutoff as the catalog does
            rows.append(dict(v=v, sp=species_of(v), level=l, c_cal=c_cal, c_thin=c_thin, share=share,
                             cur_area=cur_area / (share * ref_area_all), thin_area=t_area / (share * ref_area_all), plain_area=w_area / (share * ref_area_all),
                             cur_lum=lum(cur_rgb) / lum(ref_rgb), thin_lum=lum(t_rgb) / lum(ref_rgb), plain_lum=lum(w_rgb) / lum(ref_rgb),
                             cur_r=cur_rgb[0] / ref_rgb[0], thin_r=t_rgb[0] / ref_rgb[0], cur_g=cur_rgb[1] / ref_rgb[1], thin_g=t_rgb[1] / ref_rgb[1],
                             cur_noon=cur_light[0] / ref_light[0], thin_noon=t_light[0] / ref_light[0], plain_noon=w_light[0] / ref_light[0],
                             cur_s30=cur_light[1] / ref_light[1], thin_s30=t_light[1] / ref_light[1],
                             cur_s8=cur_light[2] / ref_light[2], thin_s8=t_light[2] / ref_light[2],
                             lin_min=min(lin_dev), lin_max=max(lin_dev), passing=int(t_n)))
        print(f"texture v{v:02d} done", flush=True)
    return rows

def cutoff_scan(data, masks):
    """Light and area along the cutoff at level 1 (the 325 m level), current against thinned, species means: the CPU twin of the TR-7c-2 'cards along the cutoff' table."""
    scan = [0.35, 0.60, 0.74, 0.80, 0.86]; out = []
    for v in range(30):
        cards, owner = data.cards(v); ids = card_id_levels(data, v); wn, _ = world_normals(data, v, ids)
        side = np.array([not c.is_top for c in cards]); rgba0 = data.albedo[v]; cov0 = rgba0[..., 3] / 255.0; lin0 = SRGB_LUT[rgba0[..., :3]]
        w0 = cov0 * ((owner >= 0) & side[np.maximum(owner, 0)])
        ref = [(lum(lin0) * response(wn[0], e) * w0).sum() / w0.sum() for e in ELEVATIONS]; ref_r = (lin0[..., 0] * w0).sum() / w0.sum() / ((lum(lin0) * w0).sum() / w0.sum())
        base = godot_mips(rgba0); thin = balance_thinned(build_thinned(rgba0, masks), masks, ids, data.normal[v]); l = 1
        sidel = (ids[l] >= 0) & side[np.maximum(ids[l], 0)]; rep = lambda x: np.repeat(np.repeat(x, 4, axis=0), 4, axis=1)
        for c in scan:
            cur = preserve_alpha_coverage(base, c)
            a = bilinear_sub(cur[l][..., 3].astype(np.float32)); ps = (a >= c * 255.0)
            share = ps.sum() / 16.0 * 4 / cov0.sum()
            ps &= rep(sidel)
            colour = np.stack([bilinear_sub(SRGB_LUT[cur[l][..., k]]) for k in range(3)], axis=-1)
            cl = [(lum(colour) * rep(response(wn[l], e)))[ps].mean() / ref[k] for k, e in enumerate(ELEVATIONS)]
            cr = colour[ps][:, 0].mean() / lum(colour[ps]).mean() / ref_r
            rgba, _, _ = thin[l]; pt = (rgba[..., 3] >= (1 - share) * 255.0) & (rgba[..., 3] > 0) & sidel; lin = SRGB_LUT[rgba[..., :3]]
            tl = [(lum(lin) * response(wn[l], e))[pt].mean() / ref[k] for k, e in enumerate(ELEVATIONS)]
            tr = lin[pt][:, 0].mean() / lum(lin[pt]).mean() / ref_r
            out.append(dict(v=v, sp=species_of(v), cutoff=c, share=share, cur_noon=cl[0], cur_s30=cl[1], cur_s8=cl[2], cur_red=cr, thin_noon=tl[0], thin_s30=tl[1], thin_s8=tl[2], thin_red=tr))
    return out

# ---------------------------------------------------------------------------------------------------------- renderer (orthographic, horizontal camera)
def hash01(a, b, c):
    """Fixed white noise in [0, 1) from three integer arrays (level-transition value per texel)."""
    h = (a.astype(np.uint64) * np.uint64(73856093)) ^ (b.astype(np.uint64) * np.uint64(19349663)) ^ (np.uint64(c) * np.uint64(83492791))
    h = (h ^ (h >> np.uint64(13))) * np.uint64(1274126177); h &= np.uint64(0xFFFFFFFF)
    h = (h ^ (h >> np.uint64(16))) * np.uint64(2246822519); h &= np.uint64(0xFFFFFFFF)
    return (h ^ (h >> np.uint64(15))).astype(np.float64) % 1048576 / 1048576.0

class Tree:
    """Everything the renderer needs for one variant."""
    def __init__(self, data, v, masks, balanced=True, solid_bark=False):
        self.v = v; self.cards, self.owner = data.cards(v)
        self.ids = card_id_levels(data, v)
        self.normal_raw = [data.normal[v][l].astype(np.float64) / 255.0 * 2 - 1 for l in range(LEVELS)]
        rgba0 = data.albedo[v]
        self.cur_base = godot_mips(rgba0); self._cur = {}
        self.thin = build_thinned(rgba0, masks, trunk_guess(data, v) if solid_bark else None)
        if balanced: self.thin = balance_thinned(self.thin, masks, self.ids, data.normal[v])
        self.thin_lin = [SRGB_LUT[t[0][..., :3]] for t in self.thin]
        self.cov0 = rgba0[..., 3] / 255.0; self.lin0 = SRGB_LUT[rgba0[..., :3]]
    def cur(self, cutoff):
        key = round(cutoff, 5)
        if key not in self._cur:
            chain = preserve_alpha_coverage(self.cur_base, cutoff)
            self._cur[key] = [np.concatenate([SRGB_LUT[m[..., :3]], m[..., 3:4].astype(np.float64) / 255.0], axis=-1) for m in chain]
        return self._cur[key]

def tex_bilinear(tex, u, v):
    """tex (n, n, k); u, v in texel units of that level (0..n), repeat addressing."""
    n = tex.shape[0]
    x = u - 0.5; y = v - 0.5
    x0 = np.floor(x).astype(int); y0 = np.floor(y).astype(int); fx = (x - x0)[..., None]; fy = (y - y0)[..., None]
    xa = x0 % n; xb = (x0 + 1) % n; ya = y0 % n; yb = (y0 + 1) % n
    return (tex[ya, xa] * (1 - fx) + tex[ya, xb] * fx) * (1 - fy) + (tex[yb, xa] * (1 - fx) + tex[yb, xb] * fx) * fy

def tex_trilinear(chain, u, v, lod):
    lod = min(max(lod, 0.0), LEVELS - 1.0); l0 = int(math.floor(lod)); f = lod - l0; l1 = min(l0 + 1, LEVELS - 1)
    a = tex_bilinear(chain[l0], u * level_size(l0), v * level_size(l0))
    if f <= 0: return a
    return a * (1 - f) + tex_bilinear(chain[l1], u * level_size(l1), v * level_size(l1)) * f

def render(tree, azimuth, px_per_unit, mode, cutoff, offset=(0.0, 0.0), lod_rule="long", level_pick="stochastic", ss=1, size=None, lod_bias=0.0, only=None):
    """Orthographic view from a horizontal camera at `azimuth` degrees. px_per_unit: pixels per unit of the unit-height tree. mode: 'current' | 'thinned' | 'reference'.
    Returns dict: mask (bool), rgb (linear albedo), normal (world), weight (reference: coverage-composited weight), all (H, W[, 3])."""
    a = math.radians(azimuth)
    d = np.array([math.cos(a), 0.0, math.sin(a)])            # camera looks along d
    right = np.array([-math.sin(a), 0.0, math.cos(a)]); up = np.array([0.0, 1.0, 0.0])
    scale = px_per_unit * ss
    if size is None: size = (int(math.ceil(px_per_unit * 1.1)) + 4, int(math.ceil(px_per_unit * 1.02)) + 4)
    W, H = size[0] * ss, size[1] * ss
    xs = (np.arange(W) + 0.5 - W * 0.5 + offset[0] * ss) / scale
    ys = ((H - (np.arange(H) + 0.5)) - 2 * ss + offset[1] * ss) / scale    # two pixels of ground margin
    X, Y = np.meshgrid(xs, ys)
    depth = np.full((H, W), np.inf); mask = np.zeros((H, W), dtype=bool); rgb = np.zeros((H, W, 3)); normal = np.zeros((H, W, 3))
    weight = np.zeros((H, W)); trans = np.ones((H, W))
    layers = []
    for c in tree.cards:
        if only is not None and c.index not in only: continue
        dn = d.dot(c.plane)
        if abs(dn) < 0.02: continue                            # edge-on
        # ray origin o = right X + up Y, P = o + t d on the plane
        o_dot = right.dot(c.plane) * X + up.dot(c.plane) * Y
        t = (c.p0.dot(c.plane) - o_dot) / dn
        s_ = (right.dot(c.e1) * X + up.dot(c.e1) * Y + t * d.dot(c.e1)); t_ = (right.dot(c.e2) * X + up.dot(c.e2) * Y + t * d.dot(c.e2))
        inside = np.zeros((H, W), dtype=bool)
        for tri in c.st:
            (ax, ay), (bx, by), (cx, cy) = tri
            area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay)
            if abs(area) < 1e-12: continue
            w0 = ((bx - s_) * (cy - t_) - (cx - s_) * (by - t_)) / area; w1 = ((cx - s_) * (ay - t_) - (ax - s_) * (cy - t_)) / area
            inside |= (w0 >= 0) & (w1 >= 0) & (1 - w0 - w1 >= 0)
        if not inside.any(): continue
        u = c.M[0, 0] * s_ + c.M[0, 1] * t_ + c.b[0]; vv = c.M[1, 0] * s_ + c.M[1, 1] * t_ + c.b[1]
        # UV derivative per screen pixel (constant over the card): moving one pixel along right / up
        def duv(axis):
            dP = (axis - d * (axis.dot(c.plane) / dn)) / px_per_unit
            return c.M @ np.array([dP.dot(c.e1), dP.dot(c.e2)]) * S
        gx, gy = duv(right), duv(up); lx, ly = np.linalg.norm(gx), np.linalg.norm(gy)
        major, minor = (gx, ly) if lx >= ly else (gy, lx); lmaj = max(lx, ly); lmin = max(min(lx, ly), 1e-6)
        front = (c.frame_sign * c.view).dot(-d) >= 0           # Godot front face: the winding seen from the camera
        nsign = 1.0 if front else -1.0
        def decode(raw):
            n = (c.tangent * raw[..., 0:1] + c.binormal * raw[..., 1:2] + c.normal * (raw[..., 2:3] * nsign))
            return n / np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-9)
        ui, vi = u[inside], vv[inside]
        if mode == "current":
            chain = tree.cur(cutoff)
            taps = int(min(math.ceil(lmaj / lmin), MAX_ANISO)); lod = math.log2(max(lmaj / taps, 1e-6)) + lod_bias
            acc = 0
            for k in range(taps):
                off = ((k + 0.5) / taps - 0.5)
                acc = acc + tex_trilinear(chain, ui + off * major[0] / S, vi + off * major[1] / S, lod)
            samp = acc / taps
            passed = samp[..., 3] >= cutoff
            col = samp[..., :3]
            nl = min(max(math.log2(max(lmaj, 1e-6)) + lod_bias, 0.0), LEVELS - 1.0); n0 = int(math.floor(nl)); nf = nl - n0; n1 = min(n0 + 1, LEVELS - 1)
            def nearest(l):
                n = level_size(l); return tree.normal_raw[l][np.floor(vi * n).astype(int) % n, np.floor(ui * n).astype(int) % n]
            nrm = decode(nearest(n0) * (1 - nf) + nearest(n1) * nf)
            wgt = passed.astype(np.float64)
        elif mode == "thinned":
            base = {"long": lmaj, "short": lmin, "area": math.sqrt(lmaj * lmin)}[lod_rule]
            lod = min(max(math.log2(max(base, 1e-6)) + lod_bias, 0.0), LEVELS - 1.0)
            if level_pick == "round": lev = np.full(ui.shape, int(math.floor(lod + 0.5)))
            else:
                l1 = min(int(math.floor(lod)) + 1, LEVELS - 1); n1 = level_size(l1)
                r = hash01(np.floor(ui * n1).astype(np.int64) % n1, np.floor(vi * n1).astype(np.int64) % n1, l1 * 31 + tree.v)
                lev = np.where((lod - math.floor(lod)) > r, l1, int(math.floor(lod)))
            passed = np.zeros(ui.shape, dtype=bool); col = np.zeros(ui.shape + (3,)); raw = np.zeros(ui.shape + (3,))
            for l in np.unique(lev):
                sel = lev == l; n = level_size(int(l)); ty = np.floor(vi[sel] * n).astype(int) % n; tx = np.floor(ui[sel] * n).astype(int) % n
                al = tree.thin[int(l)][0][ty, tx, 3]
                passed[sel] = (al >= cutoff * 255.0) & (al > 0)
                col[sel] = tree.thin_lin[int(l)][ty, tx]; raw[sel] = tree.normal_raw[int(l)][ty, tx]
            nrm = decode(raw); wgt = passed.astype(np.float64)
        else:  # reference: level 0, nearest, every texel weighted by its coverage
            ty = np.floor(vi * S).astype(int) % S; tx = np.floor(ui * S).astype(int) % S
            wgt = tree.cov0[ty, tx]; passed = wgt > 0; col = tree.lin0[ty, tx]; nrm = decode(tree.normal_raw[0][ty, tx])
        layers.append((t, inside, passed, col, nrm, wgt))
    if mode == "reference":
        # front-to-back composite with alpha = coverage
        order = sorted(range(len(layers)), key=lambda i: np.median(layers[i][0][layers[i][1]]))
        # per-pixel depth order differs between crossing cards: sort per pixel
        if layers:
            T = np.stack([np.where(L[1], L[0], np.inf) for L in layers]); idx = np.argsort(T, axis=0)
            Wt = np.zeros((len(layers), H, W)); C = np.zeros((len(layers), H, W, 3)); Nn = np.zeros((len(layers), H, W, 3))
            for i, L in enumerate(layers): Wt[i][L[1]] = L[5]; C[i][L[1]] = L[3]; Nn[i][L[1]] = L[4]
            for k in range(len(layers)):
                sel = np.take_along_axis(idx, np.full((1, H, W), k), axis=0)[0]
                w = np.take_along_axis(Wt, sel[None], axis=0)[0] * trans
                rgb += np.take_along_axis(C, sel[None, ..., None], axis=0)[0] * w[..., None]
                normal += np.take_along_axis(Nn, sel[None, ..., None], axis=0)[0] * w[..., None]   # not normalised: used through per-layer light below
                weight += w; trans *= 1 - np.take_along_axis(Wt, sel[None], axis=0)[0]
            return dict(weight=weight, rgb=rgb, layers=layers, order=idx, Wt=Wt, C=C, N=Nn, ss=ss)
        return dict(weight=weight, rgb=rgb, layers=[], ss=ss)
    for (t, inside, passed, col, nrm, wgt) in layers:
        tt = t[inside]; closer = passed & (tt < depth[inside])
        ys_, xs_ = np.nonzero(inside); ys_, xs_ = ys_[closer], xs_[closer]
        depth[ys_, xs_] = tt[closer]; mask[ys_, xs_] = True; rgb[ys_, xs_] = col[closer]; normal[ys_, xs_] = nrm[closer]
    return dict(mask=mask, rgb=rgb, normal=normal)

def measure(img, sun_az_rel, azimuth):
    """Covered pixels, mean ambient luminance and mean sun-only luminance (noon, 30, 8; mean over sun azimuths relative to the camera) of the covered pixels."""
    m = img["mask"]; n = m.sum()
    if n == 0: return 0, 0.0, [0.0, 0.0, 0.0], np.zeros(3)
    l = lum(img["rgb"][m]); out = []
    for e in ELEVATIONS:
        el = math.radians(e); acc = 0.0
        azs = [0.0] if e >= 89.9 else sun_az_rel
        for sa in azs:
            a = math.radians(azimuth + 180 + sa)   # sun azimuth measured from the camera's side of the tree
            L = np.array([math.cos(el) * math.cos(a), math.sin(el), math.cos(el) * math.sin(a)])
            acc += (l * np.maximum(img["normal"][m] @ L, 0)).mean()
        out.append(acc / len(azs))
    return int(n), float(l.mean()), out, img["rgb"][m].mean(0)

def measure_reference(ref, sun_az_rel, azimuth):
    ss = ref["ss"]; w = ref["weight"]; area = w.sum() / (ss * ss)
    if not ref["layers"]: return 0.0, 0.0, [0.0] * 3, np.zeros(3)
    amb = lum(ref["rgb"]).sum() / w.sum(); out = []
    # light: per layer in per-pixel depth order
    Wt, C, Nn, idx = ref["Wt"], ref["C"], ref["N"], ref["order"]
    for e in ELEVATIONS:
        el = math.radians(e); acc = 0.0; azs = [0.0] if e >= 89.9 else sun_az_rel
        for sa in azs:
            a = math.radians(azimuth + 180 + sa); L = np.array([math.cos(el) * math.cos(a), math.sin(el), math.cos(el) * math.sin(a)])
            lit = lum(C) * np.maximum(Nn @ L, 0)            # (cards, H, W)
            trans = np.ones(w.shape); tot = 0.0
            for k in range(len(Wt)):
                sel = np.take_along_axis(idx, np.full((1,) + w.shape, k), axis=0)
                wk = np.take_along_axis(Wt, sel, axis=0)[0]
                tot += (np.take_along_axis(lit, sel, axis=0)[0] * wk * trans).sum(); trans *= 1 - wk
            acc += tot / w.sum()
        out.append(acc / len(azs))
    return float(area), float(amb), out, ref["rgb"].sum((0, 1)) / w.sum()

AZIMUTHS = [15 + 30 * k for k in range(12)]
SUN_REL = [30.0, 120.0, 210.0, 300.0]

def calibrate_thin(tree, target_px, px, azimuths, lod_rule, level_pick):
    """The thinned cutoff whose covered area (mean over the azimuths at this pixel size) equals target_px: what the GPU calibration would bisect."""
    lo, hi = 0.02, 0.98
    for _ in range(9):
        mid = 0.5 * (lo + hi)
        area = np.mean([render(tree, az, px, "thinned", mid, lod_rule=lod_rule, level_pick=level_pick)["mask"].sum() for az in azimuths])
        if area > target_px: lo = mid
        else: hi = mid
    return 0.5 * (lo + hi)

def render_stats(data, masks, variants=range(30), lod_rule="area", level_pick="stochastic", balanced=True):
    rows = []
    for v in variants:
        tree = Tree(data, v, masks, balanced); sp = species_of(v); c_cal = float(data.cut[v])
        px325 = PX_PER_M_325 * HEIGHT_M[sp]
        az4 = AZIMUTHS[::3]
        target = np.mean([render(tree, az, px325, "current", c_cal)["mask"].sum() for az in AZIMUTHS])
        c_thin = calibrate_thin(tree, target, px325, AZIMUTHS, lod_rule, level_pick)
        refs = {az: measure_reference(render(tree, az, px325, "reference", 0.0, ss=6), SUN_REL, az) for az in AZIMUTHS}
        ref_area = np.mean([refs[az][0] for az in AZIMUTHS]); ref_amb = np.mean([refs[az][1] for az in AZIMUTHS])
        ref_sun = [np.mean([refs[az][2][k] for az in AZIMUTHS]) for k in range(3)]
        ref_rgb = np.mean([refs[az][3] for az in AZIMUTHS], axis=0)
        for dist in DISTANCES:
            px = px325 * 325.0 / dist
            for mode, cutoff in (("current", c_cal), ("thinned", c_thin)):
                res = [measure(render(tree, az, px, mode, cutoff, lod_rule=lod_rule, level_pick=level_pick), SUN_REL, az) for az in AZIMUTHS]
                area = np.mean([r[0] for r in res]) * (dist / 325.0) ** 2     # in 325 m pixels
                amb = np.mean([r[1] for r in res]); sun = [np.mean([r[2][k] for r in res]) for k in range(3)]
                rgbm = np.mean([r[3] for r in res], axis=0)
                # quarter-pixel turn: the share of covered pixels that change
                flick = []
                for az in az4:
                    a0 = render(tree, az, px, mode, cutoff, lod_rule=lod_rule, level_pick=level_pick)["mask"]
                    a1 = render(tree, az, px, mode, cutoff, offset=(0.25, 0.0), lod_rule=lod_rule, level_pick=level_pick)["mask"]
                    flick.append((a0 ^ a1).sum() / max(1.0, 0.5 * (a0.sum() + a1.sum())))
                rows.append(dict(v=v, sp=sp, dist=dist, mode=mode, cutoff=cutoff, area_px325=area, area_over_target=area / target, area_over_ref=area / ref_area,
                                 amb=amb / ref_amb, noon=sun[0] / ref_sun[0], s30=sun[1] / ref_sun[1], s8=sun[2] / ref_sun[2],
                                 red=(rgbm[0] / lum(rgbm)) / (ref_rgb[0] / lum(ref_rgb)), quarter=float(np.mean(flick))))
        print(f"render v{v:02d} c_cal={c_cal:.3f} c_thin={c_thin:.3f}", flush=True)
    return rows

def linearity(data, masks, variants=(3, 13, 19, 0, 24), lod_rule="long", level_pick="stochastic"):
    """Rendered covered area against the cutoff at 325 m and 900 m: thinned (should be a straight line through 0 at cutoff 1) and current."""
    scan = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9]; out = []
    for v in variants:
        tree = Tree(data, v, masks); sp = species_of(v)
        for dist in (325.0, 900.0):
            px = PX_PER_M_325 * HEIGHT_M[sp] * 325.0 / dist
            for c in scan:
                t = np.mean([render(tree, az, px, "thinned", c, lod_rule=lod_rule, level_pick=level_pick)["mask"].sum() for az in AZIMUTHS[::2]])
                cu = np.mean([render(tree, az, px, "current", c)["mask"].sum() for az in AZIMUTHS[::2]])
                out.append(dict(v=v, dist=dist, cutoff=c, thin=t, cur=cu))
    return out

def main_card(tree):
    """The card the far quad copies: the largest upright card, the first on a tie (ImportedTreeImpostorCatalog.LargestPlane)."""
    best = None
    for c in tree.cards:
        if c.is_top: continue
        if best is None or c.area > best.area + 1e-7: best = c
    return best

def quad_stats(data, masks, variants=range(30), lod_rule="long", level_pick="stochastic"):
    """The far quad (one picture, face on) at 900 m and beyond: the cutoff that gives the cards' area at 900 m, its light, and how the area holds to 1,920 and 3,000 m."""
    rows = []
    offsets = [(0.0, 0.0), (0.5, 0.25), (0.25, 0.75), (0.75, 0.5)]
    for v in variants:
        tree = Tree(data, v, masks); sp = species_of(v); c_cal = float(data.cut[v]); q_cal = float(data.quad_cut[v]); px325 = PX_PER_M_325 * HEIGHT_M[sp]; px900 = px325 * 325.0 / 900.0
        target325 = np.mean([render(tree, az, px325, "current", c_cal)["mask"].sum() for az in AZIMUTHS])
        c_thin = calibrate_thin(tree, target325, px325, AZIMUTHS, lod_rule, level_pick)
        cards_cur = np.mean([render(tree, az, px900, "current", c_cal)["mask"].sum() for az in AZIMUTHS])
        cards_thin = np.mean([render(tree, az, px900, "thinned", c_thin, lod_rule=lod_rule, level_pick=level_pick)["mask"].sum() for az in AZIMUTHS])
        card = main_card(tree); az = math.degrees(math.atan2(-card.view[2], -card.view[0])); only = [card.index]
        def area(mode, cutoff, px):
            return float(np.mean([render(tree, az, px, mode, cutoff, offset=o, lod_rule=lod_rule, level_pick=level_pick, only=only)["mask"].sum() for o in offsets]))
        top = area("thinned", 0.004, px900)          # every drawable texel: the picture's baked coverage
        lo, hi = 0.004, 0.98
        for _ in range(9):
            mid = 0.5 * (lo + hi)
            if area("thinned", mid, px900) > cards_thin: lo = mid
            else: hi = mid
        q_thin = 0.5 * (lo + hi)
        ref = measure_reference(render(tree, az, px325, "reference", 0.0, ss=6, only=only), SUN_REL, az)
        def light(mode, cutoff):
            res = [measure(render(tree, az, px900, mode, cutoff, offset=o, lod_rule=lod_rule, level_pick=level_pick, only=only), SUN_REL, az) for o in offsets]
            return [float(np.mean([r[2][k] for r in res]) / ref[2][k]) for k in range(3)] + [float(np.mean([r[1] for r in res]) / ref[1])]
        lc = light("current", q_cal); lt = light("thinned", q_thin)
        row = dict(v=v, sp=sp, q_cal=q_cal, q_thin=q_thin, reach=top / cards_thin, cur_quad_over_cards=area("current", q_cal, px900) / cards_cur, thin_quad_over_cards=area("thinned", q_thin, px900) / cards_thin,
                   cur_noon=lc[0], thin_noon=lt[0], cur_s30=lc[1], thin_s30=lt[1], cur_amb=lc[3], thin_amb=lt[3])
        for dist in (1920.0, 3000.0):
            px = px325 * 325.0 / dist
            row[f"cur_area_{int(dist)}"] = area("current", q_cal, px) * (dist / 900.0) ** 2 / area("current", q_cal, px900)
            row[f"thin_area_{int(dist)}"] = area("thinned", q_thin, px) * (dist / 900.0) ** 2 / area("thinned", q_thin, px900)
        rows.append(row); print(f"quad v{v:02d} q_cal={q_cal:.3f} q_thin={q_thin:.3f} reach={row['reach']:.3f}", flush=True)
    return rows

def dolly(data, v, configs, d0=300.0, d1=1000.0, step=1.0):
    """Camera backing away in `step` metre steps, one azimuth: per step the share of covered pixels that differ from the previous step (the outline shrinking is in every
    mode alike) and the covered area in 325 m pixels. configs: (label, mode, masks, lod rule, level pick)."""
    sp = species_of(v); c_cal = float(data.cut[v]); px325 = PX_PER_M_325 * HEIGHT_M[sp]; out = []; az = 45.0
    for name, mode, masks, rule, pick in configs:
        tree = Tree(data, v, masks)
        target = np.mean([render(tree, a, px325, "current", c_cal)["mask"].sum() for a in AZIMUTHS])
        cutoff = c_cal if mode == "current" else calibrate_thin(tree, target, px325, AZIMUTHS, rule, pick)
        size = (int(px325 * 1.1) + 6, int(px325 * 1.02) + 6); prev = None; dist = d0; series = []
        while dist <= d1:
            m = render(tree, az, px325 * 325.0 / dist, mode, cutoff, lod_rule=rule, level_pick=pick, size=size)["mask"]
            if prev is not None: series.append((dist, float((m ^ prev).sum() / max(1.0, 0.5 * (m.sum() + prev.sum()))), float(m.sum() * (dist / 325.0) ** 2)))
            prev = m; dist += step
        out.append((name, cutoff, series, target))
    return out

# ---------------------------------------------------------------------------------------------------------- pictures
SUN_PREVIEW = (50.0, 140.0)   # elevation, azimuth relative to the camera's side: behind the camera's left shoulder
def shade(img, azimuth, sun=SUN_PREVIEW, ambient=0.35, sun_energy=4.0, exposure=0.42):
    """Linear albedo * (sky ambient + sun n.L), one exposure for every tile, no tonemap curve, to sRGB. The game's light mix (sun 4.0, ambient 0.35); no haze."""
    el = math.radians(sun[0]); a = math.radians(azimuth + 180 + sun[1])
    L = np.array([math.cos(el) * math.cos(a), math.sin(el), math.cos(el) * math.sin(a)])
    lit = img["rgb"] * (ambient * np.array([0.75, 0.85, 1.0]) + sun_energy * np.maximum(img["normal"] @ L, 0)[..., None]) * exposure
    out = np.where(img["mask"][..., None], lin2srgb(lit), SKY)
    return (np.clip(out, 0, 1) * 255 + 0.5).astype(np.uint8)

def upscale(a, k): return np.repeat(np.repeat(a, k, axis=0), k, axis=1)

def label(draw, xy, text, fill=(20, 20, 20)): draw.text(xy, text, fill=fill)

class Look:
    """One way of drawing the cards in the pictures: 'current', or a thinned chain (its masks, bark rule) with its level rule."""
    def __init__(self, name, mode, masks=None, solid_bark=False, lod_rule="long", level_pick="stochastic"):
        self.name = name; self.mode = mode; self.masks = masks; self.solid_bark = solid_bark; self.lod_rule = lod_rule; self.level_pick = level_pick; self.trees = {}
    def tree(self, data, v):
        if v not in self.trees:
            t = Tree(data, v, self.masks if self.masks is not None else noise_masks(), True, self.solid_bark); sp = species_of(v); c_cal = float(data.cut[v]); px325 = PX_PER_M_325 * HEIGHT_M[sp]
            if self.mode == "current": cutoff = c_cal
            else:
                target = np.mean([render(t, az, px325, "current", c_cal)["mask"].sum() for az in AZIMUTHS])
                cutoff = calibrate_thin(t, target, px325, AZIMUTHS, self.lod_rule, self.level_pick)
            self.trees[v] = (t, cutoff, px325)
        return self.trees[v]
    def draw(self, data, v, azimuth, dist, offset=(0.0, 0.0)):
        t, cutoff, px325 = self.tree(data, v)
        return render(t, azimuth, px325 * 325.0 / dist, self.mode, cutoff, offset=offset, lod_rule=self.lod_rule, level_pick=self.level_pick), cutoff

def default_looks():
    return [Look("current cards", "current"),
            Look("thinned, per texel (D9 as worded)", "thinned", noise_masks("blue", 1)),
            Look("thinned in clumps, trunk solid", "thinned", noise_masks("blue", CLUMP), solid_bark=True)]
CLUMP = 8   # level-0 texels (about 1.2 m of crown) over which neighbouring texels share their noise value

def preview_sheet(data, looks, variants=(3, 13, 19), name="preview_sheet.png", azimuth=45.0):
    tile = 300; pad = 6; head = 60; sky = tuple(int(c * 255) for c in SKY)
    cols = len(DISTANCES) * len(looks)
    sheet = Image.new("RGB", (cols * (tile + pad) + pad, head + len(variants) * (tile + pad + 28) + pad), sky)
    draw = ImageDraw.Draw(sheet)
    label(draw, (pad, 6), "TR-7d preview (CPU prototype, not the game renderer). Per distance, left to right: " + " | ".join(l.name for l in looks) + ".")
    label(draw, (pad, 22), "Every look is thinned to the same covered area at 325 m (mean of 12 view directions). Each tile: the tree enlarged without smoothing, and at its true 1080p size in the lower left corner.")
    label(draw, (pad, 38), "Light: sun 50 degrees up, the game's sun and ambient mix, one exposure for all tiles, no haze, nothing boosted. The clumped look guesses the bare trunk from colour and height (the bake would know it).")
    info = []
    for r, v in enumerate(variants):
        sp = species_of(v)
        for di, dist in enumerate(DISTANCES):
            for li, look in enumerate(looks):
                img, cutoff = look.draw(data, v, azimuth, dist)
                pic = shade(img, azimuth); k = max(1, int((tile - 8) // max(pic.shape[0], pic.shape[1])))
                big = upscale(pic, k)
                x = pad + (di * len(looks) + li) * (tile + pad); y = head + r * (tile + pad + 28)
                t = Image.new("RGB", (tile, tile), sky)
                t.paste(Image.fromarray(big), ((tile - big.shape[1]) // 2, tile - big.shape[0]))
                t.paste(Image.fromarray(pic), (2, tile - pic.shape[0] - 2))
                sheet.paste(t, (x, y + 28))
                label(draw, (x, y), f"{SPECIES[sp]} v{v}, {int(dist)} m: {look.name}")
                label(draw, (x, y + 13), f"cutoff {cutoff:.2f}, x{k}, {int(img['mask'].sum())} px drawn")
                info.append((v, dist, look.name, round(cutoff, 3), int(img["mask"].sum())))
    sheet.save(os.path.join(OUT, name)); return info

def forest_bands(data, looks, seed=5, shift=(0.0, 0.0), width=960, distances=DISTANCES, scale=1.0):
    """Trees of all variants at their true 1080p size in rows at each distance, one canvas per look: the same trees in the same places. shift moves the camera sideways (pixels);
    scale divides the distances (a dolly)."""
    bands = []
    for dist in distances:
        rng = np.random.default_rng(seed + int(dist))
        count = int(width / (PX_PER_M_325 * 325.0 / dist * 7.0)); rows_n = 2 if dist < 600 else 3
        places = [(int(rng.integers(0, 30)), float(rng.uniform(0, 360)), float(rng.uniform(0, 1)), float(rng.uniform(0, 1)), float(rng.uniform(-0.3, 0.3))) for _ in range(count * rows_n)]
        d = dist / scale; row_step = PX_PER_M_325 * 325.0 / d * 4
        h = int(PX_PER_M_325 * 20.0 * 325.0 / dist * 1.05) + 6 + (rows_n - 1) * int(PX_PER_M_325 * 325.0 / dist * 4)
        canvases = []
        for look in looks:
            canvas = np.tile((SKY * 255 + 0.5).astype(np.uint8), (h, width, 1))
            for i, (v, az, fx, fy, jit) in enumerate(places):
                row = i % rows_n
                cx = (i // rows_n + 0.5 + jit) * width / count + shift[0]; cy = (rows_n - 1 - row) * row_step + fy
                img, _ = look.draw(data, v, az, d, offset=(-(cx - math.floor(cx)), fy - 0.5))
                pic = shade(img, az, sun=(SUN_PREVIEW[0], SUN_PREVIEW[1] - az + 45.0))
                x = int(math.floor(cx)) - pic.shape[1] // 2; y = h - pic.shape[0] - int(cy)
                x0 = max(x, 0); y0 = max(y, 0); x1 = min(x + pic.shape[1], width); y1 = min(y + pic.shape[0], h)
                if x1 <= x0 or y1 <= y0: continue
                m = img["mask"][y0 - y:y1 - y, x0 - x:x1 - x]
                canvas[y0:y1, x0:x1][m] = pic[y0 - y:y1 - y, x0 - x:x1 - x][m]
            canvases.append(canvas)
        bands.append((dist, canvases))
    return bands

def forest_strip(data, looks, name="forest_strip.png", width=960):
    bands = forest_bands(data, looks, width=width); sky = tuple(int(c * 255) for c in SKY)
    k = 2; total_h = sum(len(looks) * (b[1][0].shape[0] * k + 18) for b in bands) + 44
    sheet = Image.new("RGB", (width * k, total_h), sky); draw = ImageDraw.Draw(sheet)
    label(draw, (6, 6), "TR-7d forest strips (CPU prototype, not the game renderer): all 30 tree variants at their 1080p pixel size, enlarged x2 without smoothing.")
    label(draw, (6, 22), "Per distance the same trees in the same places, top to bottom: " + " | ".join(l.name for l in looks) + ". Same covered area at 325 m, nothing boosted.")
    y = 44
    for dist, canvases in bands:
        for look, canvas in zip(looks, canvases):
            label(draw, (6, y + 2), f"{int(dist)} m: {look.name}")
            sheet.paste(Image.fromarray(upscale(canvas, k)), (0, y + 16)); y += canvas.shape[0] * k + 18
    sheet.save(os.path.join(OUT, name))
    total_h = sum(len(looks) * (b[1][0].shape[0] + 2) for b in bands)
    small = Image.new("RGB", (width, total_h), sky); y = 0
    for dist, canvases in bands:
        for canvas in canvases: small.paste(Image.fromarray(canvas), (0, y)); y += canvas.shape[0] + 2
    small.save(os.path.join(OUT, name.replace(".png", "_1to1.png")))

def motion_frames(data, looks, start=0, stop=120, width=640, out_dir=None):
    """A slow sideways camera move (0.05 pixel per frame at 325 m, less with distance, as parallax gives) over the forest bands, one frame set for tools/media/ffmpeg_tools.ps1 mp4 / flicker.
    Rows: per distance, the looks top to bottom; enlarged x2 without smoothing."""
    out_dir = out_dir or os.path.join(OUT, "motion"); os.makedirs(out_dir, exist_ok=True)
    for f in range(start, stop):
        rows = []
        for dist in DISTANCES:
            bands = forest_bands(data, looks, shift=(f * 0.05 * 325.0 / dist * 3.0, 0.0), width=width, distances=[dist])
            rows += [upscale(c, 2) for c in bands[0][1]]
        Image.fromarray(np.concatenate(rows, axis=0)).save(os.path.join(out_dir, f"frame{f:08d}.png"))
        if f % 10 == 0: print("motion frame", f, flush=True)

# ---------------------------------------------------------------------------------------------------------- tables
def species_mean(rows, key, where=lambda r: True):
    return [float(np.mean([r[key] for r in rows if r["sp"] == s and where(r)])) for s in range(3)]
def species_worst(rows, key, where=lambda r: True):
    return [float(max(abs(r[key] - 1) for r in rows if r["sp"] == s and where(r))) for s in range(3)]
def fmt3(a): return " / ".join(f"{x:.3f}" for x in a)

def write_csv(rows, name):
    if not rows: return
    keys = list(rows[0].keys())
    with open(os.path.join(OUT, name), "w") as f:
        f.write(",".join(keys) + "\n")
        for r in rows: f.write(",".join(f"{r[k]:.5f}" if isinstance(r[k], float) else str(r[k]) for k in keys) + "\n")

def cmd_texture(data, masks):
    rows = texture_stats(data, masks); write_csv(rows, "texture_stats.csv")
    scan = cutoff_scan(data, masks); write_csv(scan, "texture_cutoff_scan.csv")
    md = ["# TR-7d texture-space statistics (CPU prototype)", "",
          "Species means of the variant ratios, maple / fir / pine. Reference: the coverage-weighted mean over the level-0 side-card texels (the close-up crown).",
          "cur = the shipped chain (GenerateMipmaps + PreserveAlphaCoverage at the calibrated cutoff, bilinear read); thin = TR-7d with the balanced order; plain = TR-7d as worded (alpha = 1 - noise / coverage only).",
          "All draw the area the current cards draw at level 0 at the calibrated cutoff (where the baked coverage allows it: a ratio below 1 at level 0 means the cutoff reached 0).",
          "Light = albedo luminance x max(n.L, 0), mean over 12 sun azimuths, cards seen from the baked side.", ""]
    md.append("| level (texels) | area cur | area thin | area plain | lum cur | lum thin | noon cur | noon thin | noon plain | sun30 cur | sun30 thin | sun8 cur | sun8 thin | red cur | red thin |")
    md.append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    for l in range(6):
        w = lambda r: r["level"] == l
        md.append(f"| {l} ({level_size(l)}) | " + " | ".join(fmt3(species_mean(rows, k, w)) for k in ("cur_area", "thin_area", "plain_area", "cur_lum", "thin_lum", "cur_noon", "thin_noon", "plain_noon", "cur_s30", "thin_s30", "cur_s8", "thin_s8", "cur_r", "thin_r")) + " |")
    md += ["", "Worst variant (largest |ratio - 1|), maple / fir / pine:", "", "| level | noon cur | noon thin | noon plain | sun30 cur | sun30 thin | lum cur | lum thin | area thin | area plain |", "|---|---|---|---|---|---|---|---|---|---|"]
    for l in range(6):
        w = lambda r: r["level"] == l
        md.append(f"| {l} | " + " | ".join(fmt3(species_worst(rows, k, w)) for k in ("cur_noon", "thin_noon", "plain_noon", "cur_s30", "thin_s30", "cur_lum", "thin_lum", "thin_area", "plain_area")) + " |")
    md += ["", "Thinned (balanced) area against cutoff: area / (baked coverage x (1 - cutoff)) over cutoffs 0.05 to 0.9, lowest and highest over all variants, per level:", ""]
    for l in range(6):
        sel = [r for r in rows if r["level"] == l]
        md.append(f"- level {l}: {min(r['lin_min'] for r in sel):.3f} to {max(r['lin_max'] for r in sel):.3f} (passing side texels per variant at the calibrated area: {min(r['passing'] for r in sel)} to {max(r['passing'] for r in sel)})")
    md += ["", "Along the cutoff at level 1 (current chain rebuilt per cutoff; thinned, balanced, at the cutoff that draws the same area). Species means maple / fir / pine:", "",
           "| current cutoff | share of coverage drawn | noon cur | noon thin | sun30 cur | sun30 thin | red/lum cur | red/lum thin |", "|---|---|---|---|---|---|---|---|"]
    for c in sorted(set(r["cutoff"] for r in scan)):
        w = lambda r: r["cutoff"] == c
        md.append(f"| {c:.2f} | " + " | ".join(fmt3(species_mean(scan, k, w)) for k in ("share", "cur_noon", "thin_noon", "cur_s30", "thin_s30", "cur_red", "thin_red")) + " |")
    open(os.path.join(OUT, "texture_stats.md"), "w").write("\n".join(md) + "\n"); print("\n".join(md))

CONFIGS = [("plain rule, per texel", False, 1, "long", "stochastic"), ("balanced, per texel", True, 1, "long", "stochastic"), ("balanced, per texel", True, 1, "long", "round"),
           ("balanced, per texel", True, 1, "area", "stochastic"), ("balanced, clumps of 8", True, 8, "long", "stochastic")]
def config_csv(cfg): return f"render_stats_{'balanced' if cfg[1] else 'plain'}_clump{cfg[2]}_{cfg[3]}_{cfg[4]}.csv"

def read_csv(name):
    import csv
    rows = []
    for r in csv.DictReader(open(os.path.join(OUT, name))):
        rows.append({k: (x if k == "mode" else (int(x) if k in ("v", "sp") else float(x))) for k, x in r.items()})
    return rows

def cmd_render(data, masks, which=None):
    """Crossed cards drawn at the three distances: the current cards and the thinned variants (the D9 rule as worded, the balanced order, the level rules, the clumped mask).
    `render <i>` computes one configuration (they are independent, so they can run side by side), `render lin` the area-against-cutoff scan, `render table` writes render_stats.md
    from whatever has been computed; `render` alone does everything."""
    todo = range(len(CONFIGS)) if which is None else ([int(which)] if which.isdigit() else [])
    for i in todo:
        name, bal, clump, rule, pick = CONFIGS[i]
        write_csv(render_stats(data, noise_masks("blue", clump), lod_rule=rule, level_pick=pick, balanced=bal), config_csv(CONFIGS[i]))
    if which in (None, "lin"): write_csv(linearity(data, masks), "render_linearity.csv")
    if which not in (None, "table"): return
    md = ["# TR-7d rendered statistics (CPU prototype)", "",
          "Crossed cards, orthographic horizontal camera, 12 azimuths, the match test's pixel scale (maple 18 m = 49 px at 325 m), all 30 variants.",
          "Reference: the same cards at level 0, 6 x supersampled, every texel weighted by its coverage (front to back). Thinned cutoffs are bisected to the current cards' area at 325 m.",
          "Quarter-pixel change: the share of the covered pixels that differ when the picture moves a quarter pixel sideways.", "",
          "## All variants: mean and range of the variant ratios; rms and worst = deviation of the variant ratios from 1", "",
          "| chain | lod rule | level pick | distance | area / 325 m target (range) | ambient rms | noon mean, rms, worst | sun 30 rms, worst | quarter-pixel change |", "|---|---|---|---|---|---|---|---|---|"]
    sp_md = ["", "## Species means of the variant ratios, maple / fir / pine", "",
             "| chain | lod rule | level pick | distance | area / 325 m target | ambient lum | noon | sun 30 | sun 8 | red / lum | worst variant noon | worst sun 30 | quarter-pixel change | cutoff |", "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    def line(name, rule, pick, rows, mode):
        for dist in DISTANCES:
            sel = [r for r in rows if r["mode"] == mode and r["dist"] == dist]
            a = np.array([r["area_over_target"] for r in sel]); n = np.array([r["noon"] for r in sel]); s3 = np.array([r["s30"] for r in sel]); am = np.array([r["amb"] for r in sel]); q = np.array([r["quarter"] for r in sel])
            md.append(f"| {name} | {rule} | {pick} | {int(dist)} | {a.mean():.3f} ({a.min():.3f} to {a.max():.3f}) | {np.sqrt(((am - 1) ** 2).mean()):.3f} | {n.mean():.3f}, {np.sqrt(((n - 1) ** 2).mean()):.3f}, {np.abs(n - 1).max():.3f} | {np.sqrt(((s3 - 1) ** 2).mean()):.3f}, {np.abs(s3 - 1).max():.3f} | {q.mean():.3f} |")
            w = lambda r: r["dist"] == dist and r["mode"] == mode
            sp_md.append(f"| {name} | {rule} | {pick} | {int(dist)} | " + " | ".join(fmt3(species_mean(rows, k, w)) for k in ("area_over_target", "amb", "noon", "s30", "s8", "red")) + f" | {fmt3(species_worst(rows, 'noon', w))} | {fmt3(species_worst(rows, 's30', w))} | {fmt3(species_mean(rows, 'quarter', w))} | {fmt3(species_mean(rows, 'cutoff', w))} |")
    first = True
    for cfg in CONFIGS:
        if not os.path.exists(os.path.join(OUT, config_csv(cfg))): continue
        rows = read_csv(config_csv(cfg))
        if first: line("current cards", "gpu, 4x aniso", "trilinear", rows, "current"); first = False
        line("thinned, " + cfg[0], cfg[3], cfg[4], rows, "thinned")
    md += sp_md
    if os.path.exists(os.path.join(OUT, "render_linearity.csv")):
        lin = read_csv("render_linearity.csv")
        md += ["", "## Covered area against the cutoff (pixels at the drawn size; mean of 6 azimuths)", "",
               "Per card the thinned area is linear in (1 - cutoff) by construction; drawn as crossed cards the planes overlap, so the tree's area bends (maple and fir have 8 and 4 side cards on 4 and 2 planes).", "",
               "| variant, distance | " + " | ".join(f"{c:.1f}" for c in sorted(set(r['cutoff'] for r in lin))) + " | thinned: largest gap to the straight line through (1, 0), in units of the area at cutoff 0.5 |", "|---|" + "---|" * 10]
        for v in sorted(set(r["v"] for r in lin)):
            for dist in (325.0, 900.0):
                sel = sorted([r for r in lin if r["v"] == v and r["dist"] == dist], key=lambda r: r["cutoff"])
                x = np.array([1 - r["cutoff"] for r in sel]); y = np.array([r["thin"] for r in sel]); k = (x * y).sum() / (x * x).sum()
                md.append(f"| v{v} {int(dist)} m thinned | " + " | ".join(f"{r['thin']:.0f}" for r in sel) + f" | {np.max(np.abs(y - k * x)) / (k * 0.5):.3f} |")
                md.append(f"| v{v} {int(dist)} m current | " + " | ".join(f"{r['cur']:.0f}" for r in sel) + " | |")
    open(os.path.join(OUT, "render_stats.md"), "w").write("\n".join(md) + "\n"); print("\n".join(md))

def cmd_quad(data, masks):
    rows = quad_stats(data, masks); write_csv(rows, "quad_stats.csv")
    md = ["# TR-7d far quad (CPU prototype)", "", "One picture, face on, at the 900 m pixel size (4 sub-pixel positions). Species means maple / fir / pine unless stated.", "",
          "| | current | thinned |", "|---|---|---|"]
    md.append(f"| quad cutoff | {fmt3(species_mean(rows, 'q_cal'))} | {fmt3(species_mean(rows, 'q_thin'))} |")
    md.append(f"| quad area / cards' area at 900 m | {fmt3(species_mean(rows, 'cur_quad_over_cards'))} | {fmt3(species_mean(rows, 'thin_quad_over_cards'))} |")
    md.append(f"| the picture's whole baked coverage / cards' area at 900 m (below 1: the target cannot be reached) | | {fmt3(species_mean(rows, 'reach'))}; lowest variant {min(r['reach'] for r in rows):.3f}; variants below 1: {sum(1 for r in rows if r['reach'] < 1)} |")
    for k, name in (("noon", "noon"), ("s30", "sun 30"), ("amb", "ambient luminance")):
        md.append(f"| {name} against the picture's coverage-weighted mean | {fmt3(species_mean(rows, 'cur_' + k))}; worst {fmt3(species_worst(rows, 'cur_' + k))} | {fmt3(species_mean(rows, 'thin_' + k))}; worst {fmt3(species_worst(rows, 'thin_' + k))} |")
    for dist in (1920, 3000):
        md.append(f"| area at {dist} m against 900 m (same solid angle units) | {fmt3(species_mean(rows, f'cur_area_{dist}'))} | {fmt3(species_mean(rows, f'thin_area_{dist}'))} |")
    open(os.path.join(OUT, "quad_stats.md"), "w").write("\n".join(md) + "\n"); print("\n".join(md))

def cmd_dolly(data, masks):
    md = ["# TR-7d dolly (CPU prototype)", "", "One tree, azimuth 45, camera backing from 300 m to 1,000 m in 1 m steps. Change = share of the covered pixels that differ from the previous step.", "",
          "| variant | look | cutoff | mean change per metre | largest three | steps above twice the mean | area against the 325 m target: mean, std |", "|---|---|---|---|---|---|---|"]
    configs = [("current cards", "current", masks, "long", "round"), ("thinned per texel, nearest level", "thinned", masks, "long", "round"), ("thinned per texel, stochastic level", "thinned", masks, "long", "stochastic"),
               ("thinned in clumps of 8, stochastic level", "thinned", noise_masks("blue", 8), "long", "stochastic")]
    csv_rows = []
    for v in (3, 13, 19):
        for name, cutoff, series, target in dolly(data, v, configs):
            ch = np.array([x[1] for x in series]); ar = np.array([x[2] for x in series]); top = np.sort(ch)[-3:][::-1]
            md.append(f"| v{v} | {name} | {cutoff:.2f} | {ch.mean():.3f} | {', '.join(f'{x:.2f}' for x in top)} | {(ch > 2 * ch.mean()).sum()} | {ar.mean() / target:.3f}, {ar.std() / target:.3f} |")
            for (dist, c, a) in series: csv_rows.append(dict(v=v, look=name, dist=dist, change=c, area=a))
    write_csv(csv_rows, "dolly.csv")
    open(os.path.join(OUT, "dolly.md"), "w").write("\n".join(md) + "\n"); print("\n".join(md))

def cmd_sheet(data, masks):
    looks = default_looks()
    info = preview_sheet(data, looks)
    with open(os.path.join(OUT, "preview_sheet_tiles.csv"), "w") as f:
        f.write("variant,distance,look,cutoff,pixels\n")
        for row in info: f.write(",".join(str(x) for x in row) + "\n")
    forest_strip(data, looks)

def cmd_motion(data, masks, start=0, stop=120):
    motion_frames(data, default_looks(), start, stop)

DUMP_GD = r'''extends SceneTree
# TR-7d lead prototype: read-only dump of the baked card atlas (level-0 albedo, normal mips), the LOD3 card geometry and the calibration,
# for tr7d_prototype.py. Headless: <engine> --headless --path . --script res://.godot/tr7d/dump_tr7d.gd
const OUT := "res://.godot/tr7d/data"

func _init() -> void:
	call_deferred("_run")

func _write(path: String, bytes: PackedByteArray) -> void:
	var f := FileAccess.open(OUT + "/" + path, FileAccess.WRITE)
	f.store_buffer(bytes)
	f.close()

func _run() -> void:
	var atlas = load("res://resources/trees/prepared/lolipop_impostor_atlas.res")
	_write("albedo_base.bin", atlas.AlbedoBase)
	_write("normal_mips.bin", atlas.NormalMips)
	var catalog = load("res://resources/trees/prepared/lolipop_catalog.res")
	var info := {"names": Array(catalog.VariantNames), "species": Array(catalog.SpeciesIds), "dimensions": [], "variants": []}
	for d in catalog.Dimensions: info["dimensions"].append([d.x, d.y, d.z, d.w])
	for v in range(30):
		var mesh: ArrayMesh = catalog.Meshes[v * 4 + 3]
		var entry := {"surfaces": mesh.get_surface_count(), "p": [], "n": [], "uv": [], "t": []}
		var material := mesh.surface_get_material(0) as ShaderMaterial
		if material != null:
			entry["alpha_cutoff"] = material.get_shader_parameter("alpha_cutoff")
			entry["use_alpha_cutout"] = material.get_shader_parameter("use_alpha_cutout")
			var tint = material.get_shader_parameter("albedo_tint")
			entry["tint"] = [tint.r, tint.g, tint.b, tint.a] if tint != null else null
		for s in range(mesh.get_surface_count()):
			var a := mesh.surface_get_arrays(s)
			var p: PackedVector3Array = a[Mesh.ARRAY_VERTEX]
			var n: PackedVector3Array = a[Mesh.ARRAY_NORMAL] if a[Mesh.ARRAY_NORMAL] != null else PackedVector3Array()
			var uv: PackedVector2Array = a[Mesh.ARRAY_TEX_UV] if a[Mesh.ARRAY_TEX_UV] != null else PackedVector2Array()
			var t: PackedFloat32Array = a[Mesh.ARRAY_TANGENT] if a[Mesh.ARRAY_TANGENT] != null else PackedFloat32Array()
			var idx: PackedInt32Array = a[Mesh.ARRAY_INDEX] if a[Mesh.ARRAY_INDEX] != null else PackedInt32Array()
			var order: Array = []
			if idx.size() > 0: order = Array(idx)
			else: order = range(p.size())
			for i in order:
				entry["p"].append([p[i].x, p[i].y, p[i].z])
				entry["n"].append([n[i].x, n[i].y, n[i].z] if n.size() > i else [0.0, 1.0, 0.0])
				entry["uv"].append([uv[i].x, uv[i].y] if uv.size() > i else [0.0, 0.0])
				entry["t"].append([t[i * 4], t[i * 4 + 1], t[i * 4 + 2], t[i * 4 + 3]] if t.size() >= (i + 1) * 4 else [1.0, 0.0, 0.0, 1.0])
		info["variants"].append(entry)
	var calibration := ResourceLoader.load("res://resources/trees/prepared/lolipop_impostor_calibration.res", "", ResourceLoader.CACHE_MODE_IGNORE)
	if calibration != null and calibration.has_meta("cutoffs"):
		info["calibration"] = {"version": calibration.get_meta("version"), "fingerprint": calibration.get_meta("fingerprint"),
			"cutoffs": Array(calibration.get_meta("cutoffs")), "quad_cutoffs": Array(calibration.get_meta("quad_cutoffs"))}
	var f := FileAccess.open(OUT + "/info.json", FileAccess.WRITE)
	f.store_string(JSON.stringify(info))
	f.close()
	print("TR7D_DUMP=PASS albedo=%d normal=%d" % [atlas.AlbedoBase.size(), atlas.NormalMips.size()])
	quit(0)
'''

if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True); os.makedirs(DATA, exist_ok=True)
    command = sys.argv[1] if len(sys.argv) > 1 else "all"
    if command == "dump-script":
        open(os.path.join(OUT, "dump_tr7d.gd"), "w", newline=chr(10)).write(DUMP_GD); print("wrote", os.path.join(OUT, "dump_tr7d.gd")); sys.exit(0)
    data = Data(); masks = noise_masks("blue")
    if command in ("texture", "all"): cmd_texture(data, masks)
    if command in ("render", "all"): cmd_render(data, masks, sys.argv[2] if len(sys.argv) > 2 else None)
    if command in ("quad", "all"): cmd_quad(data, masks)
    if command in ("dolly", "all"): cmd_dolly(data, masks)
    if command in ("sheet", "all"): cmd_sheet(data, masks)
    if command == "motion": cmd_motion(data, masks, *(int(x) for x in sys.argv[2:4]))
