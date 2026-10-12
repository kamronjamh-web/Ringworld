# Terrain3D provenance

- **Package:** Terrain3D v1.0.2-stable
- **Upstream:** [TokisanGames/Terrain3D](https://github.com/TokisanGames/Terrain3D)
- **Release:** [v1.0.2-stable](https://github.com/TokisanGames/Terrain3D/releases/tag/v1.0.2-stable)
- **Release asset:** `Terrain3D_v1.0.2-stable.zip`
- **Release asset URL:** `https://github.com/TokisanGames/Terrain3D/releases/download/v1.0.2-stable/Terrain3D_v1.0.2-stable.zip`
- **SHA-256:** `a071850250ec5e596aa54da61c01d75768774eb379ee997584d426a45f4884a2`
- **Installed location:** `addons/terrain_3d`
- **License:** MIT; the upstream license is retained at `addons/terrain_3d/LICENSE.txt`.

The upstream v1.0.2-stable release states support for Godot 4.4--4.6+. This project now uses Godot 4.8 dev7 .NET. Its inherited Resource method registration rejects five methods in the original addon binary; the compatibility patch below is required in addition to the existing Ringworld geometry patches. Validate with `tools/terrain3d_godot48_compatibility_test.gd` and `tools/terrain3d_addon_smoke.gd`, followed by the real surface pilot.

## Windows-only Ringworld patches

`patches/all_axis_culling.patch`, `patches/standard_grid_diagonal.patch`, and `patches/godot_48_resource_methods.patch` apply to Terrain3D source commit `0077405b52e353c5e5dc3a094e7ede49833ba6fe` from tag `v1.0.2-stable`; its pinned `godot-cpp` submodule is `6388e26dd8a42071f65f764a3ef3d9523dda3d6e`.

`all_axis_culling.patch` keeps Terrain3D's existing height-range Y AABB update and applies that same bounded `cull_margin` as `instance_set_extra_visibility_margin` to every clipmap instance. Ringworld's terrain vertex shader curves the local terrain frame, so the additional bounded instance margin prevents CPU culling on rotated/negative-Z sections of the ring.

`standard_grid_diagonal.patch` adds an opt-in `standard_grid_diagonal_flipped` Terrain3D property. RingTerrain3DSurface enables it before tree entry, so every standard grid uses the ring surface's canonical-positive lattice diagonal, including LOD0 tiles, standard edges, and standard trims. The local Terrain3D frame sees that diagonal as negative because `AcrossDirection` is `-1`; this is the same orientation after the coordinate transform. Terrain3D's nonstandard alternating grids retain their upstream parity and index order.

`godot_48_resource_methods.patch` binds addon-specific asset names as `set_asset_name` / `get_asset_name`, keeping the serialized `name` property unchanged. The addon docks read that property, which also works with older platform binaries. It binds the custom region-copy operation as `duplicate_region`; native C++ calls retain their original custom implementation. This avoids silently using inherited Resource methods, which change Resource metadata instead of addon names and omit region runtime location during generic copying. Third-party scripts using the old addon-specific `duplicate` entry point must use `duplicate_region` with these patched binaries.

Windows x64 binaries are rebuilt with LLVM-MinGW `20260922` UCRT x64 (`llvm-mingw-20260922-ucrt-x86_64.zip`, SHA-256 `e3ad77d117a4bea19a7a3b333341824d79a5a371004a10e25b8504e7b3047666`), official Python `3.13.7` embedded x64 (archive SHA-256 `f6cca216a359be84797cabb54149ce5e062afb16cc7567eb7fc51cacb2d86b65`), and SCons `4.10.1` (wheel SHA-256 `bd9d1c52f908d874eba92a8c0c0a8dcf2ed9f3b88ab956d0fce1da479c4e7126`), all staged only below `.godot`. LLVM's bundled minimal Python lacks zlib, so the official embedded Python is required for godot-cpp documentation generation. `build_windows.ps1` records the reproducible commands and pin checks and tolerates Windows source/patch line endings. Patched Windows binaries use distinct `libterrain.ringworld.windows.*.dll` names; the official release DLLs remain in place. Linux, macOS, Android, iOS, and web binaries remain the unmodified upstream release pending their own rebuilds and have not been validated on 4.8.

Built Windows x64 artifact hashes:

- `libterrain.ringworld.windows.debug.x86_64.dll`: `e93954029fd9202176d68a28248af62ee9d2430013617ff1ca81f3d869b4aaf9`
- `libterrain.ringworld.windows.release.x86_64.dll`: `1a670788a4ad6069fc31c204c27760cdfc33db45c78d57aec1080c16fedb7273`

These identify the vendored artifacts; rebuilds can differ because the PE linker records a build timestamp.

`llvm-objdump -p` confirms that both artifacts import only Windows UCRT/API-set libraries and `KERNEL32.dll`; they do not depend on toolchain-distributed libc++, libunwind, or MinGW runtime DLLs. The `libterrain.windows.*.dll` names shown by the export table are each artifact's internal DLL name, not imports.

Rebuild setup, all below the ignored `.godot` directory:

```powershell
New-Item -ItemType Directory -Force .godot/terrain3d-native-tools | Out-Null
git clone --depth 1 --branch v1.0.2-stable --recurse-submodules https://github.com/TokisanGames/Terrain3D.git .godot/terrain3d-native-src
Invoke-WebRequest https://github.com/mstorsjo/llvm-mingw/releases/download/20260922/llvm-mingw-20260922-ucrt-x86_64.zip -OutFile .godot/terrain3d-native-tools/llvm-mingw-20260922-ucrt-x86_64.zip
$llvmArchive = '.godot/terrain3d-native-tools/llvm-mingw-20260922-ucrt-x86_64.zip'
if ((Get-FileHash $llvmArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'e3ad77d117a4bea19a7a3b333341824d79a5a371004a10e25b8504e7b3047666') { throw 'LLVM-MinGW archive hash mismatch.' }
Expand-Archive -LiteralPath $llvmArchive -DestinationPath .godot/terrain3d-native-tools -Force
Invoke-WebRequest https://www.python.org/ftp/python/3.13.7/python-3.13.7-embed-amd64.zip -OutFile .godot/terrain3d-native-tools/python-3.13.7-embed-amd64.zip
if ((Get-FileHash .godot/terrain3d-native-tools/python-3.13.7-embed-amd64.zip).Hash.ToLowerInvariant() -ne 'f6cca216a359be84797cabb54149ce5e062afb16cc7567eb7fc51cacb2d86b65') { throw 'Python archive hash mismatch.' }
Expand-Archive .godot/terrain3d-native-tools/python-3.13.7-embed-amd64.zip .godot/terrain3d-native-tools/python-runtime -Force
Add-Content .godot/terrain3d-native-tools/python-runtime/python313._pth '../python'
$sconsRelease = Invoke-RestMethod https://pypi.org/pypi/scons/4.10.1/json
$sconsWheel = $sconsRelease.urls | Where-Object filename -Like '*.whl' | Select-Object -First 1
Invoke-WebRequest $sconsWheel.url -OutFile .godot/terrain3d-native-tools/scons-4.10.1.whl
if ((Get-FileHash .godot/terrain3d-native-tools/scons-4.10.1.whl).Hash.ToLowerInvariant() -ne 'bd9d1c52f908d874eba92a8c0c0a8dcf2ed9f3b88ab956d0fce1da479c4e7126') { throw 'SCons wheel hash mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory("$PWD/.godot/terrain3d-native-tools/scons-4.10.1.whl", "$PWD/.godot/terrain3d-native-tools/python", $true)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./third_party/Terrain3D/build_windows.ps1 -Target template_debug
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./third_party/Terrain3D/build_windows.ps1 -Target template_release
```

Pass `-BuildOnly` to compile and hash an artifact without replacing the checked-in Ringworld DLL.
