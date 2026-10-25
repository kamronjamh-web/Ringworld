"""Summarise (tools/quality_preset_capture.gd output) WarmStallProbe stall_frames.csv + stall_detail.txt by context group.
Usage: python -I tools/quality_preset_probe_report.py <run_dir> [context_prefix ...]
Per context: frame count, median/p95/p99 of total and buckets, hitch (>25 ms) count, hitch cause classes
(gc, pipeline compile, jit, top owner gap), and the owners that dominate the nodes bucket in hitch frames."""
import csv
import re
import sys
from collections import Counter, defaultdict


def pct(values, q):
    if not values:
        return float("nan")
    s = sorted(values)
    pos = q * (len(s) - 1)
    lo = int(pos)
    hi = min(lo + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (pos - lo)


def group_of(ctx):
    c = ctx.split("|")[-1]
    if c.startswith("pose_setup"):
        return "pose_setup"
    if ":" in c and not c.startswith("flight"):
        pose, label = c.split(":", 1)
        return pose + (":baseline" if label == "baseline" else ":ablation")
    return c


def main():
    run = sys.argv[1]
    want = sys.argv[2:]
    rows = list(csv.DictReader(open(run + "/stall_frames.csv", newline="")))
    detail = open(run + "/stall_detail.txt").read()
    # owner gaps per hitch frame
    frame_gaps = {}
    for block in re.split(r"(?m)^FRAME ", detail)[1:]:
        lines = block.splitlines()
        idx = int(lines[0].split()[0])
        gaps = []
        for ln in lines[1:]:
            m = re.match(r"\s+([\d.]+) ms\s+(.*)", ln)
            if m:
                gaps.append((float(m.group(1)), m.group(2)))
        frame_gaps[idx] = gaps
    groups = defaultdict(list)
    for r in rows:
        groups[group_of(r["context"])].append(r)
    order = list(dict.fromkeys(group_of(r["context"]) for r in rows))
    for g in order:
        if want and not any(g.startswith(w) for w in want):
            continue
        fr = groups[g]
        tot = [float(r["total_ms"]) for r in fr]
        b = {k: [float(r[k]) for r in fr] for k in ["physics_ms", "signal_handlers_ms", "nodes_ms", "post_process_ms", "draw_ms", "pre_physics_ms"]}
        hit = [r for r in fr if float(r["total_ms"]) > 25.0]
        causes = Counter()
        owners = Counter()
        owner_ms = defaultdict(float)
        for r in hit:
            c = []
            if int(r["gc0"]) + int(r["gc1"]) + int(r["gc2"]) > 0:
                c.append("gc%s" % ("2" if int(r["gc2"]) else ("1" if int(r["gc1"]) else "0")))
            if sum(int(r[k]) for k in ["pipe_surface", "pipe_mesh", "pipe_draw", "pipe_spec"]) > 0:
                c.append("pipeline")
            if float(r["jit_ms"]) > 2.0:
                c.append("jit")
            causes[",".join(c) or "none"] += 1
            for ms, what in frame_gaps.get(int(r["frame"]), [])[:3]:
                key = re.sub(r"^.* -> ", "", what) if "->" in what else what
                key = "after:" + what.split(" -> ")[0] + "->" + key if key in ("__last", "post_draw", "pre_draw") else key
                owners[key] += 1
                owner_ms[key] += ms
        print("== %s frames=%d total med=%.1f p95=%.1f p99=%.1f max=%.1f | hitches>25=%d (%.0f%%)" % (
            g, len(fr), pct(tot, .5), pct(tot, .95), pct(tot, .99), max(tot) if tot else 0, len(hit), 100.0 * len(hit) / max(1, len(fr))))
        print("   buckets median: " + " ".join("%s=%.1f" % (k.replace("_ms", ""), pct(v, .5)) for k, v in b.items()))
        print("   buckets p95:    " + " ".join("%s=%.1f" % (k.replace("_ms", ""), pct(v, .95)) for k, v in b.items()))
        if hit:
            hb = {k: [float(r[k]) for r in hit] for k in b}
            print("   hitch-frame bucket median: " + " ".join("%s=%.1f" % (k.replace("_ms", ""), pct(v, .5)) for k, v in hb.items()))
            print("   hitch causes: " + ", ".join("%s=%d" % kv for kv in causes.most_common(6)))
            print("   top owners in hitch frames (count, total ms): " + ", ".join("%s=%d/%.0f" % (k, owners[k], owner_ms[k]) for k, _ in owners.most_common(8)))


main()
