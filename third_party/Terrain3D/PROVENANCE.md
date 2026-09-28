# Terrain3D provenance

- **Package:** Terrain3D v1.0.2-stable
- **Upstream:** [TokisanGames/Terrain3D](https://github.com/TokisanGames/Terrain3D)
- **Release:** [v1.0.2-stable](https://github.com/TokisanGames/Terrain3D/releases/tag/v1.0.2-stable)
- **Release asset:** `Terrain3D_v1.0.2-stable.zip`
- **Release asset URL:** `https://github.com/TokisanGames/Terrain3D/releases/download/v1.0.2-stable/Terrain3D_v1.0.2-stable.zip`
- **SHA-256:** `a071850250ec5e596aa54da61c01d75768774eb379ee997584d426a45f4884a2`
- **Installed location:** `addons/terrain_3d`
- **License:** MIT; the upstream license is retained at `addons/terrain_3d/LICENSE.txt`.

The upstream v1.0.2-stable release states support for Godot 4.4--4.6+. This project uses Godot 4.7.2, so compatibility is verified by the native API smoke test in `tools/terrain3d_addon_smoke.gd` rather than claimed by the upstream release notes.

## Windows-only Ringworld patches

`patches/all_axis_culling.patch` and `patches/standard_grid_diagonal.patch` apply to Terrain3D source commit `0077405b52e353c5e5dc3a094e7ede49833ba6fe` from tag `v1.0.2-stable`; its pinned `godot-cpp` submodule is `6388e26dd8a42071f65f764a3ef3d9523dda3d6e`.

`all_axis_culling.patch` keeps Terrain3D's existing height-range Y AABB update and applies that same bounded `cull_margin` as `instance_set_extra_visibility_margin` to every clipmap instance. Ringworld's terrain vertex shader curves the local terrain frame, so the additional bounded instance margin prevents CPU culling on rotated/negative-Z sections of the ring.

`standard_grid_diagonal.patch` adds an opt-in `standard_grid_diagonal_flipped` Terrain3D property. RingTerrain3DSurface enables it before tree entry, so every standard grid uses the ring surface's canonical-positive lattice diagonal, including LOD0 tiles, standard edges, and standard trims. The local Terrain3D frame sees that diagonal as negative because `AcrossDirection` is `-1`; this is the same orientation after the coordinate transform. Terrain3D's nonstandard alternating grids retain their upstream parity and index order.

Windows x64 binaries are rebuilt with LLVM-MinGW `20260922` UCRT x64 (`llvm-mingw-20260922-ucrt-x86_64.zip`, SHA-256 `e3ad77d117a4bea19a7a3b333341824d79a5a371004a10e25b8504e7b3047666`) and SCons `4.10.1`, both staged only below `.godot`. `build_windows.ps1` records the reproducible commands and pin checks. Patched Windows binaries use distinct `libterrain.ringworld.windows.*.dll` names; the official release DLLs remain in place. Linux, macOS, Android, iOS, and web binaries remain the unmodified upstream release pending their own rebuilds.

Built Windows x64 artifact hashes:

- `libterrain.ringworld.windows.debug.x86_64.dll`: `3494f0ebdd8face266b8e62257da6b547ed6dc2d749059a0ee6092d33f160ffb`
- `libterrain.ringworld.windows.release.x86_64.dll`: `5d588368cccd98c4f48723f5934987894a0593be097f92e4b55926d86316675c`

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
python -m pip install --target .godot/terrain3d-native-tools/python scons==4.10.1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./third_party/Terrain3D/build_windows.ps1 -Target template_debug
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./third_party/Terrain3D/build_windows.ps1 -Target template_release
```

Pass `-BuildOnly` to compile and hash an artifact without replacing the checked-in Ringworld DLL.
