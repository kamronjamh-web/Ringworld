# GodotOceanWaves compute import

This directory imports the six compute kernels from
[`2Retr0/GodotOceanWaves`](https://github.com/2Retr0/GodotOceanWaves) at
commit `a171446f8174348895aaafc426576c26261058b9` under its retained MIT
license.

Ringworld uses the upstream JONSWAP/TMA spectrum, Stockham inverse FFT,
transpose, and unpack/foam kernels. The C# controller owns their RenderingDevice
resources and schedules bounded updates. No upstream scene, UI, audio, demo
water mesh, or global shader parameters are imported. Ringworld binds the
published displacement and normal/foam arrays to its nearby ocean materials,
uses filtered spectral displacement for their 2 m geometry, and supplies canonical reduced tile origins so ring
floating-origin coordinates do not enter the GPU spectrum as large floats.

Adaptations are deliberately mechanical: `fft_compute_128.glsl` and
`fft_compute_256.glsl` set the Stockham workgroup/shared-memory size to the
selected bounded map size, so no invocation can return before a workgroup
barrier. The spectrum image declaration is `rgba32f`, matching its RGBA32F RD
allocation. The provider clears a new normal/foam image before its first
accumulating unpack dispatch.

The C# provider supplies exact reflected push-constant sizes and separate
read-only/write-only spectrum bindings for Godot 4.7. Its published
`Texture2DArrayRD` has at least two backing layers (a Godot requirement), even
when only one cascade is active. Foam uses elapsed simulation time per cascade.
The packed output is signed gradient in RG, gradient second moment in B, and
persistent foam in A; it is not an encoded RGB normal map. Ringworld generates
a bounded GPU mip chain after each updated cascade: each level averages RG,
the second moment, and foam independently. This makes explicit footprint
sampling valid and lets filtered-away slope variance increase roughness.

The runtime requires a main RenderingDevice with 1024 compute invocations per
workgroup for the upstream transpose pass. Headless or unsupported devices keep
the existing analytic water. Defaults are two 128 x 128 cascades targeting
24 updates per second per cascade, with at most one cascade dispatch per frame.
Only nearby ocean materials sample the output. Lakes, rivers, and global/scaled-far
water retain their existing paths. Ringworld's own `fft_surface_query.glsl` returns
nine filtered surface heights in a 144-byte asynchronous readback for swimming;
it inverts horizontal displacement and applies the same distance fade as the mesh.
This query and `fft_mipmap.glsl` are integration additions, not upstream kernels.
Generated water columns, terrain density checks, and dynamic-fluid precedence
still determine whether the player is in water.
