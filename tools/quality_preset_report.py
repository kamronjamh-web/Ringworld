"""Summarise a tools/quality_preset_capture.gd report: baseline table per pose (median of baseline windows) and A-X-A deltas.
Usage: python -I tools/quality_preset_report.py <run dir | report.json> [--deltas]
Windows carrying an "invalid" key (paused tree, A1000 power verdict) are skipped for the baseline medians; the skip count is printed."""
import json
import os
import statistics
import sys

_path = sys.argv[1]
if os.path.isdir(_path):
    _path = os.path.join(_path, "report.json")
r = json.load(open(_path))
q = r["quality"]
print("preset=%s window=%s atlas=%s sun_max=%s haze=%s raymarch=%s" % (q["preset"], q["window"], q["atlas"], q["sun_max"], q["haze"]["enabled"], q["raymarch_steps"]))


def m(w, metric, stat="median"):
    return w["metrics"].get(metric, {}).get(stat, float("nan"))


def brief(w):
    return [m(w, "wall_ms"), m(w, "wall_ms", "p95"), m(w, "wall_ms", "p99"), m(w, "gpu_sum_ms"), m(w, "gpu_sum_ms", "p95"), m(w, "gpu_sum_ms", "p99"),
            m(w, "cpu_render_ms"), m(w, "frame_setup_ms"), m(w, "root_cpu_ms"), m(w, "sub_cpu_ms"), m(w, "draw_calls"), w["hitch_count"], w["frames"] / max(w["seconds"], 1e-6)]


cols = ["wall", "wall95", "wall99", "gpu", "gpu95", "gpu99", "cpuR", "setup", "rootC", "subC", "draws", "hitch", "fps"]
print("%-18s" % "pose/run" + "".join("%8s" % c for c in cols))
fm = r.get("first_minute")
if fm:
    print("%-18s" % "first_minute" + "".join("%8.1f" % v for v in brief(fm)))
    if "first_30s" in fm:
        print("   first30s fps=%.1f wall med=%.1f p95=%.1f | after: med=%.1f p95=%.1f" % (fm["first_30s"]["fps"], fm["first_30s"]["wall"]["median"], fm["first_30s"]["wall"]["p95"], fm["after_30s"]["wall"]["median"], fm["after_30s"]["wall"]["p95"]))
skipped_invalid = 0
for pose, p in r["poses"].items():
    base = [w for w in p["windows"] if w["label"] == "baseline" and "invalid" not in w]
    skipped_invalid += len([w for w in p["windows"] if w["label"] == "baseline" and "invalid" in w])
    if not base:
        print("%-18s (no valid baseline windows)" % pose)
        continue
    rows = [brief(w) for w in base]
    med = [statistics.median([row[i] for row in rows]) for i in range(len(cols))]
    med[11] = sum(row[11] for row in rows) / len(rows)
    print("%-18s" % pose + "".join("%8.1f" % v for v in med) + "   (%d baseline windows)" % len(base))
for f in r.get("flights", []):
    print("%-18s" % f["tag"] + "".join("%8.1f" % v for v in brief(f)) + "   dist=%.0f m busy=%d" % (f["distance_m"], f["streaming_busy_frames"]))

print("invalid baseline windows skipped: %d" % skipped_invalid)

if "--deltas" in sys.argv[2:]:
    labels = []
    for p in r["poses"].values():
        for l in p.get("summary", {}):
            if l not in labels:
                labels.append(l)
    for metric in ["gpu_sum_ms", "cpu_render_ms", "wall_ms"]:
        print("\nDelta %s (X minus mean of neighbours); drift in []" % metric)
        print("%-22s" % "label" + "".join("%17s" % pose[:16] for pose in r["poses"]))
        for l in labels:
            line = "%-22s" % l
            for pose, p in r["poses"].items():
                d = p.get("summary", {}).get(l)
                line += "%17s" % ("" if d is None else "%+.2f [%.2f]" % (d[metric], abs(d[metric + "@drift"])))
            print(line)
    print("\nGPU.Clouds marker (baseline median):", {pose: round(statistics.median([w["markers"].get("GPU.Clouds", {}).get("median", 0) for w in p["windows"] if w["label"] == "baseline"]), 2) for pose, p in r["poses"].items()})
    print("Far viewport GPU/CPU (baseline):", {pose: (round(statistics.median([w["viewports"].get("FarRingViewport", {}).get("gpu", {}).get("median", 0) for w in p["windows"] if w["label"] == "baseline"]), 2), round(statistics.median([w["viewports"].get("FarRingViewport", {}).get("cpu", {}).get("median", 0) for w in p["windows"] if w["label"] == "baseline"]), 2)) for pose, p in r["poses"].items()})
