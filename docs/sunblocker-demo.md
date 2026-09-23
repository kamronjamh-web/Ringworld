# Tree sunblocker demo

A standalone comparison scene using the same imported LOLIPOP maple, fir and
pine catalog as the game. The grave-key drawer selects among all 30 variants.
The scene starts with the mature `Acer_large_1` maple (index 3). It loads the tree
meshes and textures without starting the ringworld, terrain streaming or weather
simulation.

## Run

Double-click **Start Sunblocker Demo.cmd** in the project root. The launcher
builds the C# project and opens `scenes/SunblockerDemo.tscn`.

Alternatively, open that scene in Godot and run the current scene with F6. The
project requires Godot .NET 4.7.2 and the .NET 8 SDK. If Godot is installed
elsewhere, use:

```powershell
.\tools\run_sunblocker_demo.ps1 -Godot 'C:\path\to\Godot.exe'
```

## Compare

Press the grave/backtick key to open the existing live tuning drawer. The demo
adds its own controls and saves them separately from gameplay settings.

- **Full geometry (LOD0):** the visible production tree also casts its shadow.
- **Authored LOD2 proxy:** the visible LOD0 tree has casting disabled; the
  asset's simpler LOD2 mesh casts the shadow. It retains the authored leaf
  cutouts, so this tests reduced geometry and still includes alpha testing.
- **Sun-facing authored cards:** the visible LOD0 tree has casting disabled;
  the asset's LOD3 crossed cards cast its shadow. These stay upright and rotate
  toward the sun's horizontal direction. They are existing view billboards,
  so their outlines approximate the tree and work less well near overhead sun.
- **Distance handoff:** compare detailed nearby casting with proxy casting
  beyond an adjustable 100-200 m threshold. This demo uses groups of trees for
  the handoff; a production forest would need finer spatial selection.
- **Show proxy geometry:** reveal the normally invisible casters for inspection.
  The corresponding visible tree is hidden during this inspection so it cannot
  conceal the proxy inside its crown.
- **Single-method view:** compare one method at a time with the same layout and
  camera. The simultaneous comparison view renders all three methods together.

The reviewed captures show broader, darker shadows from LOD3 cards, especially
at low sun. LOD2 is the more conservative starting point for a production trial;
these authored cards are an approximation, not a fitted light-view silhouette.

Hold the right mouse button to look around. Use the wheel to move forward/back,
and R to reset the camera. Release the button or press Escape to release the
mouse. Open the tuning drawer to select a tree and adjust the sun, map size and
tree count. Selecting a different variant reframes the camera for its size.
Variant selection is saved with the other demo settings.

All three columns use the same visible LOD0 mesh, textures, placement and scale.
The catalog's normalized meshes are scaled back to their authored dimensions.
Demo copies of the tree materials disable wind and ring-specific day/cloud
modulation, while retaining the imported textures, leaf cutouts and maple bark's
second UV channel. Shared production materials and source assets are unchanged.

## What the numbers mean

The FPS and GPU counters describe the whole viewport. They do not isolate the
shadow pass. Compare single-method views with identical sun, tree count, camera,
map size, tree variant and debug visibility; allow warmup before reading timings.
This small scene uses real game assets, but does not reproduce tree streaming,
wind or a dense forest. Loading the full catalog has a higher startup and memory
cost than the former procedural stand-ins. Selecting a cheaper caster does not
unload the visible tree's textures or the cached catalog.

Invisible proxies still use Godot's directional shadow atlas. A simpler caster
changes rendering work; reducing atlas resolution changes its allocated storage.

| D16 atlas size | Theoretical depth texture storage |
| --- | ---: |
| 1024 x 1024 | 2 MiB |
| 2048 x 2048 | 8 MiB |
| 4096 x 4096 | 32 MiB |

These figures exclude driver overhead and other render resources. Proxy meshes
also require some geometry and instance storage.

## Repeatable checks

After a successful build:

```powershell
.\tools\run_sunblocker_demo.ps1 -SkipBuild -Smoke
.\tools\run_sunblocker_demo.ps1 -SkipBuild -Capture '.\artifacts\sunblocker-imported-review\maple.png'
```

To choose a variant for a direct Godot run, pass a user argument after `--`:

```powershell
& 'C:\path\to\Godot_console.exe' --path . 'res://scenes/SunblockerDemo.tscn' -- --sunblocker-tree=12
```

Indices 0-11 are maple, 12-14 fir, and 15-29 pine. Optional capture arguments
include `--sunblocker-capture=res://artifacts/sunblocker-imported-review/fir.png`,
`--sunblocker-debug`, `--sunblocker-drawer`, `--sunblocker-elevation=12` and
`--sunblocker-mode=full|proxy|cards` (choose one mode).

The smoke route checks caster ownership through the distance handoff, catalog
reuse, isolated material overrides and a settings round trip including the
selected variant. Automated smoke/capture routes use deterministic defaults
and leave the interactive demo's saved settings alone.

Headless Godot cannot read back the rendered MultiMesh transforms. Its orientation
check evaluates the intended transform; the same smoke argument in a normal
Vulkan run checks actual MultiMesh transform readback.

## Validation performed

Reviewed on Godot .NET 4.7.2, Vulkan Forward+, NVIDIA RTX A1000 6GB Laptop GPU.

- [x] Primary-agent review of all demo code and the narrow existing-drawer change.
- [x] [.NET build](../artifacts/sunblocker-imported-review/build.log): zero warnings and errors.
- [x] [Headless smoke through the launcher](../artifacts/sunblocker-imported-review/smoke.log): handoff, atlas update, catalog selection/reset, material isolation, UV channel presence and saved variant.
- [x] [Vulkan smoke](../artifacts/sunblocker-imported-review/gpu-smoke.log): actual MultiMesh orientation readback and the same behavior checks.
- [x] [Production catalog regression](../artifacts/sunblocker-imported-review/catalog-smoke.log): all 30 variants passed.
- [x] [Existing gameplay UI regression](../artifacts/sunblocker-imported-review/existing-ui-regression.log): passed.
- [x] Captured and personally inspected [maple](../artifacts/sunblocker-imported-review/maple.png), [fir](../artifacts/sunblocker-imported-review/fir.png), [pine with revealed casters](../artifacts/sunblocker-imported-review/pine-proxies.png), [tuning drawer](../artifacts/sunblocker-imported-review/tuning.png), and [low sun](../artifacts/sunblocker-imported-review/sunset.png).
- [x] [Production code/shader/project hashes](../artifacts/sunblocker-imported-review/production-unchanged.json) matched; [gameplay defaults were unchanged](../artifacts/sunblocker-imported-review/defaults-check.json) and automated checks created no interactive demo settings.
- [ ] User visual acceptance.
- [ ] Dense-forest performance comparison on target lower presets.

Render captures help review shadow coverage and the controls. The user's visual
acceptance and performance on a target lower-end GPU remain separate from those
automated checks.

## Clouds

See [the cloud-shadow assessment](cloud-shadow-proxy-assessment.md) for the
current opacity-map architecture, a smaller-map experiment and a proposal for
cheaper shadow-producing cloud shapes. Cloud changes are an investigation in
this task; the demo does not replace the production cloud renderer.
