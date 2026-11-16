"""Summarises a tree_visibility_oracle.gd report.json (docs/occlusion-culling-plan.md section 3).
Usage: python tools/tree_visibility_summary.py <report.json> [--table]   (--table prints the markdown rows of plan section 3.2)"""
import json, sys, statistics

r = json.load(open(sys.argv[1]))
if "--table" in sys.argv:
    for pname, p in r["poses"].items():
        for vname, v in p["views"].items():
            T = dict(n=0, t=0, o=0, ot=0, a=0, at=0, g=0, gt=0)
            for lod, b in v["trees"].items():
                T["n"] += b["instances"]; T["t"] += b["tris"]; T["o"] += b["outside"]; T["ot"] += b["outside_tris"]
                T["a"] += b["occl_any"]; T["at"] += b["occl_any_tris"]; T["g"] += b["occl_terrain"]; T["gt"] += b["occl_terrain_tris"]
            n, t = T["n"], T["t"]
            inn = n - T["o"]; intt = t - T["ot"]
            g = v["grass"]["all"]; tp = v["other"].get("terrain_proxy", {})
            gi = g["instances"] - g["outside"]; pi = tp["instances"] - tp["outside"]; pit = tp["tris"] - tp["outside_tris"]
            print(f"| {pname} | {v['yaw']:.0f} | {n:,} / {t/1e6:.2f} M | {T['o']/n:.0%} / {T['ot']/t:.0%} | {T['a']/n:.0%} / {T['at']/t:.0%} | {T['g']/n:.0%} / {T['gt']/t:.0%} | {T['a']/max(1,inn):.0%} / {T['at']/max(1,intt):.0%} | {g['instances']:,}: {g['outside']/g['instances']:.0%} / {g['occl_any']/g['instances']:.0%} | {tp['instances']:,}: {tp['occl_any']/max(1,pi):.0%} / {tp['occl_any_tris']/max(1,pit):.0%} |")
    sys.exit(0)
print("wind", r.get("wind_response_y"), r.get("wind_globals"))
for pname, p in r["poses"].items():
    print(f"\n=== {pname}")
    for vname, v in p["views"].items():
        if not v:
            continue
        T = {"n": 0, "tris": 0, "out": 0, "out_t": 0, "occ": 0, "occ_t": 0, "occT": 0, "occT_t": 0}
        rows = []
        for lod, b in sorted(v["trees"].items()):
            for k, kk in (("n", "instances"), ("tris", "tris"), ("out", "outside"), ("out_t", "outside_tris"), ("occ", "occl_any"), ("occ_t", "occl_any_tris"), ("occT", "occl_terrain"), ("occT_t", "occl_terrain_tris")):
                T[k] += b[kk]
            rows.append(f"{lod}:{b['instances']}/{b['outside']}/{b['occl_any']}/{b['occl_terrain']}")
        n = max(1, T["n"]); t = max(1, T["tris"])
        g = v["grass"]["all"]
        o = v["other"]
        tp = o.get("terrain_proxy", {})
        poi = o.get("poi", {})
        def frac(b, k):
            return b.get(k, 0) / max(1, b.get("instances", 0))
        def fract(b, k):
            return b.get(k, 0) / max(1, b.get("tris", 0))
        print(f"{vname} yaw {v['yaw']:.0f} sky {v['depth']['sky_fraction_full']:.2f}/{v['depth']['sky_fraction_terrain']:.2f} | trees n={T['n']} tris={T['tris']/1e6:.2f}M out {T['out']/n:.0%} ({T['out_t']/t:.0%}t) occ {T['occ']/n:.0%} ({T['occ_t']/t:.0%}t) occTerrain {T['occT']/n:.0%} ({T['occT_t']/t:.0%}t)"
              f" | grass n={g['instances']} out {frac(g,'outside'):.0%} occ {frac(g,'occl_any'):.0%} occT {frac(g,'occl_terrain'):.0%}"
              f" | proxy n={tp.get('instances',0)} occ {frac(tp,'occl_any'):.0%} ({fract(tp,'occl_any_tris'):.0%}t) occT {frac(tp,'occl_terrain'):.0%} | poi n={poi.get('instances',0)} occ {poi.get('occl_any',0)}")
        print("    " + "  ".join(rows))
    t = p.get("timing")
    if t:
        w = t["windows"]
        bases = [x for x in w if x["tag"].startswith("base")]
        for i, x in enumerate(w):
            if x["tag"].startswith("copy"):
                b0, b1 = w[i - 1], w[i + 1]
                bw = (b0["wall_p50"] + b1["wall_p50"]) / 2
                bo = (b0["opaque_p50"] + b1["opaque_p50"]) / 2
                print(f"  {x['tag']:28s} wall {x['wall_p50']:6.2f} (base {bw:6.2f}, d {x['wall_p50']-bw:+5.2f})  opaque {x['opaque_p50']:5.2f} (d {x['opaque_p50']-bo:+5.2f})  gpu {x['gpu_p50']:6.2f}")
        print(f"  base spread {min(b['wall_p50'] for b in bases):.2f}-{max(b['wall_p50'] for b in bases):.2f}")
