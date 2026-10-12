# Godot 4.8 development engine and native texture streaming

The project now targets **Godot 4.8 dev7 .NET**, commit `c971f93e7`, and `Godot.NET.Sdk/4.8.0-dev.7` with `net10.0`. The official engine runtime configuration requires .NET 10. This is a development engine upgrade; production visual and performance acceptance must be checked separately from build and headless tests.

## Pinned local toolchain

Download the [official Windows .NET archive](https://github.com/godotengine/godot-builds/releases/download/4.8-dev7/Godot_v4.8-dev7_mono_win64.zip) and extract into `artifacts/engine-upgrade`. Install the .NET 10 SDK into `artifacts/engine-upgrade/dotnet` with Microsoft's `dotnet-install.ps1 -Version 10.0.401 -InstallDir <absolute-project>/artifacts/engine-upgrade/dotnet -NoPath`. Keep `artifacts/.gdignore` in place so Godot does not scan downloaded tools. No machine SDK or user NuGet configuration changes are required.

Validated local executable paths:

- `artifacts/engine-upgrade/Godot_v4.8-dev7_mono_win64/Godot_v4.8-dev7_mono_win64_console.exe`
- `artifacts/engine-upgrade/dotnet/dotnet.exe` (SDK `10.0.401`)

`tools/use_godot_48.ps1` sets `DOTNET_ROOT`, `DOTNET_ROOT_X64`, and the process `PATH`, with SDK/NuGet caches in ignored artifacts. It restores the caller's environment afterward. Repository `NuGet.Config` resolves the Godot SDK and bindings directly from the official engine's bundled `GodotSharp/Tools/nupkgs` feed. Scoped builds disable network NuGet auditing; the helper does not change global auditing settings.

```powershell
./tools/use_godot_48.ps1 -Build
./tools/use_godot_48.ps1 --headless --editor --import
./tools/use_godot_48.ps1 --headless --script res://tools/native_texture_streaming_test.gd
./tools/use_godot_48.ps1 -EngineArguments @('--script', 'res://tools/native_texture_streaming_test.gd', '--', '--rendered')
./tools/use_godot_48.ps1 --headless res://scenes/ImportedTreeImpostorCatalogRegressionSmoke.tscn
```

Close the active project editor before upgrading its import cache. A previous Godot 4.7 cache is reconstructible; retain tracked source files, import UIDs, and save data. Switching back to 4.7 requires reverting the engine/SDK/import changes and reconstructing imports.

## Native controls and import coverage

`rendering/textures/streaming/enabled=true` enables the native subsystem at engine startup. `memory_budget_enabled=true` is also necessary: setting a memory budget alone does not activate enforcement. The initial budget is 512 MiB **for native streamed textures only**, not a total GPU budget. Grave-key Far Rendering controls apply the singleton's budget and mip overrides. Startup bootstrap applies project defaults; gameplay preferences are applied by the existing tuning controller.

The pinned dev7 source uses **`min_lod` as the finest allowed mip and `max_lod` as the coarsest allowed mip**. The documentation wording at upgrade time reverses these meanings. The [pinned StreamedTexture2D source](https://github.com/godotengine/godot/blob/c971f93e7/scene/resources/streamed_texture.cpp) explicitly identifies best/min and worst/max; its initial load starts at the coarsest permitted mip. Defaults `min_lod=0`, `max_lod=3` permit full resolution when feedback requests it and retain a 1/8-dimension resident floor. The adapter's `FinestMip` binds min and `InitialMip` binds max. The coarsest mip limits how far native budget pressure can reduce resolution; this makes the budget a target rather than an absolute cap when the floor itself exceeds it.

Fifteen large near-tree 2D texture imports now use the exact native importer `streamed_texture_2d` / `StreamedTexture2D` / `.stex`, preserving existing source paths and UIDs. They cover bark albedo, bark normals, bark metallic/roughness, and standalone foliage normals and metallic/roughness across fir, maple, and pine. Generated shader lookup images, Texture2DArrays, compressed layered data, and terrain/RF maps remain outside this native Texture2D coverage.

**Alpha foliage albedo is deliberately excluded.** The [dev7 streamed importer](https://github.com/godotengine/godot/blob/c971f93e7/editor/import/resource_importer_streamed_texture.cpp) exposes no alpha-coverage, alpha-border, or alpha-threshold options and generates ordinary mipmaps. Copying `mipmaps/preserve_alpha_test_coverage` into its metadata would silently have no effect. Leaves, branches, and authored billboard alpha albedos retain their current ordinary Texture2D imports. A later engine importer fix is the preferred route to streaming those textures while preserving leaf silhouettes.

## Validation and remaining acceptance

Godot 4.8 dev7 rounds RGBA8 channels in `Image.SetPixel`: `[0.5, 0.1, 0.999, 1.0]` becomes `[128, 26, 255, 255]`. Gameplay terrain cache packing preserves its established truncation bytes `[127, 25, 254, 255]` so existing biome blends stay stable across the engine upgrade. The stream regression now checks literal legacy byte fixtures, synchronous initial image packing, and the produced biome image's quantization intervals, rather than comparing against the upgraded engine's native encoder. Initial image uploads, cache exports, and worker tiles use the same unchanged byte contract.

An isolated dev7 import fixture successfully imported 1024x2048 bark and 2048x2048 foliage-normal sources using the actual native importer. `StreamedTexture2D.get_image()` returned full-resolution mip chains, including decompression and CPU resizing into a Texture2DArray, matching the imported-tree impostor catalog path. Runtime budget/min/max setter/getter binding passed. These checks do not establish native GPU feedback efficacy or visual leaf quality.

The full-project test `tools/native_texture_streaming_test.gd` verifies native types, singleton controls, full-resolution CPU readback against source PNG dimensions, decompression, and texture-array compatibility after project import. With the engine user argument `--rendered` (pass an explicit `-EngineArguments` array to retain the `--` separator in PowerShell), it checks actual GPU resident texture dimensions on the rendering thread: magnified visible bark must recover mip 0, a 1 MiB test budget must reduce residency, restoring 512 MiB must recover detail, and invisibility must cause residency decay followed by visible recovery. The native singleton exposes no resident-mip getter, so the probe reads `RDTextureFormat` dimensions through `texture_get_rd_texture`. The existing `ImportedTreeImpostorCatalogRegressionSmoke.tscn` exercises real catalog generation. Headless tests can report zero native residency and cannot validate feedback. Compare resolution, weather, route, and vegetation workload before claiming performance improvement.

The rendered native probe passed on the local NVIDIA RTX A1000 6 GB with Vulkan Forward+: initial bark residency was 256x512, visible recovery reached the source's full 2048x4096, budget pressure reduced it to 512x1024, and invisibility decayed it to 1024x2048 before visible recovery. Streamed memory changed from 5,592,424 bytes at fine resolution to 349,544 bytes under the reduced test budget (`artifacts/engine-upgrade/native-stream-gpu.log`). This proves the narrow native feedback/budget path works; it does not measure full-world FPS, total GPU memory, or visual quality.

Terrain3D keeps its original GDExtension descriptor and receives rebuilt Windows Ringworld libraries with the existing culling/diagonal patches plus `godot_48_resource_methods.patch`. Original v1.0.2 binaries produced five duplicate member-registration errors on dev7. An isolated comparison showed that inherited Resource methods replaced addon-specific asset names and lost the region's runtime location during generic duplication. Collision-free `set_asset_name` / `get_asset_name` and `duplicate_region` bindings correct this while retaining serialized property names and native internal copy logic. The patched debug library passes `tools/terrain3d_godot48_compatibility_test.gd` without duplicate-registration errors, including asset-name and Resource metadata save/load round-trips and region deep/shallow map ownership. Full-project addon height import and gameplay surface smoke tests also pass (`terrain3d_addon_smoke.log`, `terrain3d_gameplay_smoke.log` under the upgrade artifacts). These are functional acceptance checks; full-world visual and performance acceptance remains separate. See [native provenance and rebuild instructions](../third_party/Terrain3D/PROVENANCE.md).

The optional native voxel mesher descriptor is unchanged; its missing runtime library is a pre-existing independent fallback condition. Non-Windows Terrain3D binaries remain upstream and have not been upgraded or validated for dev7. Do not substitute stock Terrain3D libraries for this checkout's custom Ringworld builds.
