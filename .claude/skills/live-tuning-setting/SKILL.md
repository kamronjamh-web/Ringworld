---
name: live-tuning-setting
description: Add or change a persisted setting in the grave-key (`) live tuning menu end to end - settings resource, defaults owner with ranges/sanitize/atomic save, drawer partial row, runtime apply, and the matching settings smoke test. Use whenever a gameplay or rendering feature needs a tunable, per the AGENTS.md feature conventions.
---

# Adding a live-tuning setting

## 0. Should this be in the menu at all? (AGENTS.md)

- Yes if it is a genuine tunable someone would adjust (density, falloff, colour, distance, budget).
- **No** per-subsystem toggles for behaviour that should simply follow an existing system.
  POIs get haze, shadows and lighting like every other surface.
  `atmosphere_menu_smoke_test.gd` asserts that `AtmospherePoiHaze` and `AtmospherePoiCloudShadow` do not exist.
- Debug or A/B switches belong in debug tooling (Diagnostics, CLI flags), not in the menu.
- If you are unsure, ask the user before adding it.

## 1. Find the subsystem's existing trio. Extend it; do not invent a new pattern.

| Piece | Path | Role |
|---|---|---|
| Settings resource | `scripts/ui/Gameplay<X>Settings.cs` | `partial class : Resource`, `[Export]` auto-props; the initialiser **is** the default; enums stored as int |
| Defaults owner | `scripts/ui/Gameplay<X>Defaults.cs` | path constant `user://gameplay_<x>_defaults.tres`, `Load`, `Sanitize`, setters, `Dirty`/`LastError`, atomic `Save`, `Reset`, `Apply(node)` |
| Drawer partial | `scripts/ui/RingLiveTuningDrawer.<X>.cs` | `Initialize…`, `Process…`, `Flush…`, `Install…Defaults(isolatedPath)`, `Build…Section`, `Queue…`, `Refresh…Controls`/`Status` |

There are three variants in the codebase; follow whichever one the subsystem already uses.
- **Table-driven** (newest; reference: emitter grid `CoreDepthThreshold`):
  - `GameplayEmitterGridDefaults.cs` has `FloatField[] FloatFields = { new(nameof(...), default, min, max) }`, plus `IntFields`/`BoolFields`, and `RangeOf`, `SetFloat/SetInt/SetBool`.
  - Setters return true only when the value changed, and throw on unknown names.
  - Each field gets a typed wrapper such as `SetCoreDepthThreshold`.
- **String-key switch** (atmosphere/haze):
  - `AtmosphereVisualDefaults.Set(string key, float v)`, with ranges hard-coded in `Sanitize()` (`Clean(v, default, min, max)`).
  - The owner lives on `RingGraphicsQualityController`, not on the drawer; the drawer re-binds every 0.25 s.
- **Sparse cloud overrides:** `AddPersistentSlider/Toggle/Option(propertyName)` calls `GameplayCloudDefaults.SetOverride`, which accepts only whitelisted keys. Add the key to the whitelist.

## 2. Steps (table-driven example)

1. **Settings field:** add `[Export] public float Foo { get; set; } = <default>;`.
   - Add a comment saying older files without the field load the default.
   - There is no schema version; absent fields fall back to the initialiser.
2. **Defaults owner:**
   - Add the `FloatFields` entry `(nameof(Foo), default, min, max)` and a `SetFoo` wrapper.
   - `Sanitize` must clamp the value and replace NaN/Inf with the default.
3. **Subsystem:** read the value in `Configure(settings)` / `ApplySettings()` and push it (for example `SetBeamUniform("foo", ...)`).
   - Shader uniform defaults in `.gdshader`/`.gdshaderinc` must equal the C# default.
   - If the node self-loads defaults in `_Ready` (`LoadSavedDefaults`), there is nothing more to do there.
4. **Drawer row:** in `Build<X>Section`, add `Add<X>Slider(section, "<X>Foo", "Readable label", min, max, step, s.Foo, tooltip)`.
   - The node `Name` (`<X>Foo`) is how tests find the control, so keep it stable.
   - Slider min/max are hand-written: they **must match** the defaults table.
   - The section comes from `TuningMenuUi.CreateSection(parent, title, note)`.
   - Put the row in the tab the subsystem already uses. Tabs are built in `RingLiveTuningDrawer.BuildDrawer()` (`RingLiveTuningDrawer.cs` ~278-375).
5. **Queue, apply and save path** (usually already present; extend it, do not duplicate):
   - `slider.ValueChanged` calls `Queue<X>Settings()`, which reads every control, calls `_defaults.SetFoo(...)`, then `_defaults.Apply(node)`.
   - If anything changed, set the save delay (~0.5-0.75 s).
   - `Process<X>Tuning` flushes when `Dirty` and the delay has expired.
   - Drawer close and `_ExitTree` call `Flush<X>Defaults()`.
6. **Refresh and reset:**
   - `Refresh<X>Controls` sets the value without emitting a signal (`SetValueNoSignal` / `SetPressedNoSignal` helpers).
   - `Reset<X>` runs Reset, Apply, Refresh, Flush.
7. **Brand-new subsystem only:**
   - Wire `Initialize/Process/Flush` into `_Ready`, `_Process`, `_ExitTree` and `SetOpen` in `RingLiveTuningDrawer.cs`, or chain them from a sibling's methods.
   - Add `Install<X>Defaults(path)`, which accepts only `res://.godot/` or `user://generated/` paths.
   - Add it to `MinimapWindContractTest.InstallIsolatedPreferences` (`scripts/Tools/`) so menu tests never touch real user files.
     Known gap: EmitterGrid and EmitterSite are not in that list yet.
   - Drawer partials find siblings by relative path, for example `../EmitterGrid`. Add the node to `scenes/VoxelRingIntegrationTest.tscn` and `scenes/Main.tscn` with the expected name.

## 3. Tests (required)

- **Settings smoke:** extend the subsystem's `tools/<X>SettingsSmoke.cs`, or create one modelled on `EmitterGridSettingsSmoke.cs` (see the `test-scaffold` skill, Template B). Cover:
  1. missing file gives defaults
  2. a clean save is a no-op
  3. changed-flag semantics
  4. set, save, reload, compare
  5. clamps and NaN fallback via `RangeOf`
  6. `Sanitize`
  7. reset round-trip
  8. an invalid path fails safely
  9. an unknown field throws
  10. an older file without the field loads the default: strip its line from a saved `.tres` (`EclipseEventSettingsSmoke.cs` ~170-187)
- **Subsystem test:** assert that `Configure` pushes the value (shader parameter, property). Example: `EmitterGridLayerTest.cs` ~272-275.
- **Menu wiring** (for a new section or tab): `atmosphere_menu_smoke_test.gd` style.
  - Build a minimal fixture, call `SetBenchmarkDrawerOpen(true)`, then `find_child("<X>Foo", true, false)`.
  - Drive the real signal, then assert the diagnostics.
- Run headless via the `godot-test-run` skill. The smoke wrappers print `<X>_SETTINGS=PASS`.

## 4. Done checklist

- [ ] the default matches in C#, the shader and the doc
- [ ] slider range = Sanitize range
- [ ] the value applies live, without a restart
- [ ] the value persists across a relaunch
- [ ] reset works
- [ ] smokes pass
- [ ] no new per-subsystem toggle
- [ ] if the feature has a spec doc in `docs/`, its settings table lists the new value
