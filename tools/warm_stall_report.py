"""Phase C WO C3 step 1 (diagnostics only): summarises a tools/menu_warm_stall_probe.ps1 run.

Usage:  python tools/warm_stall_report.py .godot/phase_c_live/<Label> [--min-ms 100] [--top 4] [--depth 12]

Inputs (all written by the probe run):
  stall_detail.txt       per-frame decomposition for frames >= 50 ms (tools/WarmStallProbe.cs)
  stall_frames.csv       every frame (buckets, GC count/pause/allocation deltas, pipeline compile deltas, context)
  console.log            STALL_PROBE_ALIGN lines (main OS thread id + engine usec of two alignment spins)
  summary.json           loader report, warm metrics, owner readiness times, boot markers
  trace.speedscope.json  optional (-Trace): dotnet-trace sampled managed stacks

For every frame >= --min-ms it prints the probe buckets, the largest marker gaps (the gap "A -> B" is the process time of the
world child B, or of whatever ran between the two markers) and, with a trace, the hot managed call path per owner (root-first,
heaviest child while it keeps >= 40% of its parent). It then prints the run timeline: menu shown, last stall, warm ready.

Reading the stacks: speedscope only records stack changes and a thread with no managed frames (engine C++ or GDScript) is not
sampled, so its last managed stack appears to persist. A long "single-stack run" (flagged) whose time is NOT confirmed by the
probe's marker gap for that owner is native/GDScript time, not the printed managed method.
"""
import csv
import json
import os
import re
import sys
from collections import defaultdict

SKIP_PREFIX = ("Thread", "Threads", "Process64", "G.Bridge.CSharpInstanceBridge.CallViaTrampoline", "G.DelegateUtils", "G.Callable")


def parse_args(argv):
    opts = {"min_ms": 100.0, "top": 4, "depth": 12}
    run = None
    i = 0
    while i < len(argv):
        a = argv[i]
        if a in ("--min-ms", "--top", "--depth"):
            key = a[2:].replace("-", "_")
            opts[key] = float(argv[i + 1]) if key == "min_ms" else int(argv[i + 1])
            i += 2
            continue
        run = a
        i += 1
    if run is None:
        raise SystemExit(__doc__)
    return run, opts


def clean(name):
    name = re.sub(r"\(.*$", "", name)
    for prefix, repl in (("ringworldtechdemo!RingworldTechDemo.", ""), ("ringworldtechdemo!", ""), ("godotsharp!Godot.", "G."),
                         ("System.Private.CoreLib.il!", "")):
        name = name.replace(prefix, repl)
    return name


def load_frames(run):
    frames = []
    with open(os.path.join(run, "stall_detail.txt"), encoding="utf-8") as fh:
        for line in fh:
            if line.startswith("FRAME"):
                m = re.search(r"FRAME (\d+) end_usec=(\d+) t_ms=([\d.]+) total=([\d.]+)", line)
                frames.append({"index": int(m.group(1)), "end": int(m.group(2)), "t_ms": float(m.group(3)),
                               "total": float(m.group(4)), "head": line.strip(), "gaps": []})
            elif frames and line.strip():
                frames[-1]["gaps"].append(line.strip())
    return frames


def load_trace(run):
    path = os.path.join(run, "trace.speedscope.json")
    if not os.path.exists(path):
        return None
    aligns = []
    with open(os.path.join(run, "console.log"), encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = re.search(r"STALL_PROBE_ALIGN begin_usec=(\d+) .*os_thread=(\d+)", line)
            if m:
                aligns.append((int(m.group(1)), m.group(2)))
    if len(aligns) < 2:
        print("trace present but fewer than two STALL_PROBE_ALIGN lines; stacks omitted")
        return None
    data = json.load(open(path, encoding="utf-8"))
    names = [f["name"] for f in data["shared"]["frames"]]
    profile = next((p for p in data["profiles"] if p["name"].startswith("Thread (%s)" % aligns[0][1])), None)
    if profile is None:
        print("main thread %s not in trace; stacks omitted" % aligns[0][1])
        return None
    intervals, stack, last = [], [], None
    for ev in profile["events"]:
        at = ev["at"]
        if last is not None and at > last and stack:
            intervals.append((last, at, tuple(stack)))
        if ev["type"] == "O":
            stack.append(ev["frame"])
        elif stack:
            stack.pop()
        last = at
    spins = [s for s, e, st in intervals if any("StallProbeAlignmentSpin" in names[f] for f in st)]
    if not spins:
        print("no StallProbeAlignmentSpin samples (method names unresolved?); stacks omitted")
        return None
    t0 = spins[0]
    later = [s for s in spins if s - t0 > 1000]
    if not later:
        print("only one alignment spin sampled; stacks omitted")
        return None
    t1 = later[-1]
    scale = (aligns[-1][0] - aligns[0][0]) / (t1 - t0)
    offset = aligns[0][0] - scale * t0
    return {"names": names, "intervals": intervals, "scale": scale, "offset": offset}


def hot_paths(trace, start_usec, end_usec, opts):
    a = (start_usec - trace["offset"]) / trace["scale"]
    b = (end_usec - trace["offset"]) / trace["scale"]
    tree = {}
    longest = 0.0
    for s, e, st in trace["intervals"]:
        if e <= a or s >= b:
            continue
        ms = min(e, b) - max(s, a)
        longest = max(longest, ms)
        chain = [clean(trace["names"][f]) for f in st]
        chain = [x for x in chain if x.strip() and not x.startswith(SKIP_PREFIX) and "+GodotInternal" not in x
                 and not x.startswith(("System.", "system."))]
        node = tree
        for x in chain:
            child = node.setdefault(x, [0.0, {}])
            child[0] += ms
            node = child[1]
    lines = []
    if longest > 60:
        lines.append("    NOTE single-stack run %.0f ms: if no marker gap confirms it, it is native/GDScript time" % longest)
    for name, (weight, children) in sorted(tree.items(), key=lambda kv: -kv[1][0])[: opts["top"]]:
        if weight < 15:
            continue
        path = [(name, weight)]
        node = children
        while node:
            child_name, (child_weight, grand) = max(node.items(), key=lambda kv: kv[1][0])
            if child_weight < 0.4 * path[-1][1] or child_weight < 10:
                break
            path.append((child_name, child_weight))
            node = grand
        lines.append("    %6.0f ms  %s" % (weight, "  >  ".join("%s[%.0f]" % p for p in path[: opts["depth"]])))
    return lines


def timeline(run):
    path = os.path.join(run, "stall_frames.csv")
    if not os.path.exists(path):
        return
    rows = list(csv.DictReader(open(path, encoding="utf-8")))
    menu = next((float(r["t_ms"]) for r in rows if float(r["total_ms"]) > 1000), None)
    if menu is None:
        return
    slow = [(float(r["t_ms"]), float(r["total_ms"]), r["context"]) for r in rows if float(r["total_ms"]) > 100 and float(r["t_ms"]) > menu]
    warm = next((float(r["t_ms"]) for r in rows if "warm2" in r["context"]), None)
    alloc = sum(float(r["alloc_mb"]) for r in rows) / 1024.0
    pause = sum(float(r["gc_pause_ms"]) for r in rows)
    gcs = [sum(int(r["gc%d" % g]) for r in rows) for g in range(3)]
    print("=" * 120)
    print("menu first presented at t=%.1f s (probe time); frames >100 ms after that: %d, total %.0f ms, max %.0f ms"
          % (menu / 1000, len(slow), sum(s[1] for s in slow), max([s[1] for s in slow] or [0])))
    if slow:
        print("menu shown -> last stall end: %.1f s (%s)" % ((slow[-1][0] - menu) / 1000, slow[-1][2]))
    if warm:
        print("menu shown -> warm state: %.1f s" % ((warm - menu) / 1000))
    print("GC over the run: gen0/1/2 = %s, pause %.0f ms, allocated %.1f GB" % (gcs, pause, alloc))


def main():
    run, opts = parse_args(sys.argv[1:])
    trace = load_trace(run)
    for fr in load_frames(run):
        if fr["total"] < opts["min_ms"]:
            continue
        print("### FRAME %d end=%.2fs total=%.0f ms" % (fr["index"], fr["end"] / 1e6, fr["total"]))
        parts = fr["head"].split("|")
        print("    " + " | ".join(p.strip() for p in parts[1:]))
        for gap in fr["gaps"][:4]:
            print("    gap " + gap)
        if trace is not None:
            for line in hot_paths(trace, fr["end"] - fr["total"] * 1000.0, fr["end"], opts):
                print(line[:900])
    timeline(run)


if __name__ == "__main__":
    main()
