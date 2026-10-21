# Terrain altitude LOD: capture on the A1000 laptop

Measures the altitude LOD change (docs/terrain-altitude-lod-coverage-plan.md) on the A1000. Everything runs
against an isolated copy of one saved world, so your real saves, caches and settings are never touched.

## Prerequisites
- A saved Standard (Certified, runtime-surface) world in slot 1 (use `-Slot N` for another).
- Close the game and every Godot editor first (`Get-Process *Godot* | Stop-Process`).
- Build once: `dotnet build RingworldTechDemo.csproj --no-restore`.
- GPU numbers are only valid on the machine that captured them; do not compare A1000 results to 4070 results.

## 1. Invariant + perf run (about 8 minutes per preset)
From the repo root in PowerShell. `-Preset` is the coordinator quality preset (0 Low, 1 Medium, 2 High,
3 Desktop High). Always pass it: a saved slot may carry a different terrain quality.

```powershell
# first run copies the slot and your shader/ring caches into .godot\terrain_altitude_lod\appdata_a1000
.\tools\terrain_altitude_lod_flight_test.ps1 -Label a1000 -OutLabel a1000_low    -Preset 0 -Cache copy  -ScriptArgs '--alts=500,2000,8000,16000,23000,60000 --pitches=-15,-45,-89 --move=16000'
.\tools\terrain_altitude_lod_flight_test.ps1 -Label a1000 -OutLabel a1000_medium -Preset 1 -Cache reuse -ScriptArgs '--alts=500,2000,8000,16000,23000,60000 --pitches=-15,-45,-89 --move=16000'
```

Flies to 0.5, 2, 8, 16, 23 and 60 km at pitches -15, -45 and -89, then a 30 s leg at 1.1 km/s at 16 km. At each pose it
casts a 24x16 ray grid onto the ground and fails on:
- a hole: a visible ground point with no installed sector of the tier that owns its distance,
- a projected-error violation: proxy tier above 2.5x, or global backdrop above 5x, the preset's TargetPixelError.

The script exits 1 and lists the failing poses when anything fails. A pose "fails" on the A1000 for streaming
reasons too (slow uploads leaving holes); the moving leg tolerates up to 2% of sampled rays as holes.

## 2. Screenshots and frame cost, same views as the 4070 capture
```powershell
.\tools\terrain_altitude_lod_flight_test.ps1 -Label a1000 -OutLabel a1000_low_shots    -Preset 0 -Cache reuse -ScriptArgs '--alts=16000,23000,60000 --pitches=-25,-70 --shots --move=16000'
.\tools\terrain_altitude_lod_flight_test.ps1 -Label a1000 -OutLabel a1000_medium_shots -Preset 1 -Cache reuse -ScriptArgs '--alts=16000,23000,60000 --pitches=-25,-70 --shots --move=16000'
```
Add `--quiet-only` to skip the ray checks when you only want frame times and screenshots.

## 3. Reading the results
Everything is under `.godot\terrain_altitude_lod\<OutLabel>\`:
- `console.log`: one `FLIGHT <pose> {...}` line per pose (active tiers, p50/p95 frame ms, draw calls, primitives,
  mesh MB, hit/hole/violation counts), `FLIGHT MOVE {...}` for the moving leg and a final `FLIGHT_RESULT=PASS|FAIL`.
- `results.json`: the same data in full, including the per-tier LOD state at each pose.
- `<pose>.png` and `<pose>_dbg.png` (`--shots`): plain and tier-coloured views. Review only, not goldens.

Compare, per preset, frame p50/p95, draw calls, primitives and mesh MB at 16 km and 60 km stationary and in the moving
leg, plus the `FLIGHT_RESULT` line. If Low or Medium miss budget on the A1000 the tunable levers are the preset's
`TargetPixelError` (wider error = fewer, coarser tiers) and the global mesh resolution floor
(`RingTerrainLodChainPlanner.GlobalResolutionFloor`).

## 4. Getting a before/after pair
The numbers above are "after". For "before", temporarily put the tracked sources back to the old code and re-run
section 2 with new `-OutLabel` names (the invariant section is expected to fail on the old code):

```powershell
git stash push -- scripts/Voxel scripts/ui scripts/Tools scripts/world_size_launcher.gd
# the probe reads one coordinator property the old code lacks; add it, then build
#   public float CurrentTargetPixelError => _currentProfile?.TargetPixelError ?? 0.0f;
# (next to CurrentViewerAltitudeAboveTerrainMeters in scripts/Voxel/LOD/RingTerrainLodCoordinator.cs)
dotnet build RingworldTechDemo.csproj --no-restore
# ... run the section 2 commands ...
git checkout -- scripts/Voxel/LOD/RingTerrainLodCoordinator.cs
git stash pop
dotnet build RingworldTechDemo.csproj --no-restore
```
