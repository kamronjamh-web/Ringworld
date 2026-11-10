# Third-party assets

## GodotOceanWaves compute kernels

`third_party/godot_ocean_waves/compute/` contains the JONSWAP/TMA spectrum,
Stockham FFT, transpose, and unpack/foam compute kernels from
[GodotOceanWaves](https://github.com/2Retr0/GodotOceanWaves), created by Ethan
Truong. They were imported from commit
`a171446f8174348895aaafc426576c26261058b9` and are licensed under MIT; the
verbatim license is retained at `third_party/godot_ocean_waves/LICENSE`.

## Clouds for Little Planets (1.0)

The optional cloud-provider demo in `addons/clouds_lp/` uses **Clouds for
Little Planets**, created by BinarySemaphore and distributed under the MIT
License.

The addon was imported from
https://github.com/BinarySemaphore/clouds_little_planets at commit
`a97dd22d834bba5aea93b299873d1cc1483abd5d`. The upstream license is retained
verbatim at `addons/clouds_lp/LICENSE`.

## KayKit: Forest Nature Pack (1.0)

The tree models in `resources/trees/kaykit_forest/` are from **KayKit: Forest
Nature Pack (1.0)**, created and distributed by Kay Lousberg
([kaylousberg.com](https://www.kaylousberg.com)).

They are provided under [Creative Commons Zero 1.0 (CC0)](https://creativecommons.org/publicdomain/zero/1.0/).
The source-pack license is retained verbatim at
`resources/trees/kaykit_forest/License.txt`.

Included files are the `Tree_1_A_Color1`, `Tree_1_B_Color1`,
`Tree_2_A_Color1`, `Tree_2_C_Color1`, `Tree_3_A_Color1`, and
`Tree_4_B_Color1` glTF models, their matching binary buffers, and the shared
`forest_texture.png` texture.

## Beach Ocean Waves

`resources/audio/water/shore_wave_cc0.ogg` is derived from
`wave_01_cc0-18363__jasinski__alkaibeach.flac`, recorded by jasinski and
published through OpenGameArt by qubodup:
https://opengameart.org/content/beach-ocean-waves

The recording is provided under [Creative Commons Zero 1.0 (CC0)](https://creativecommons.org/publicdomain/zero/1.0/).
Conversion and loop details are retained in `resources/audio/water/LICENSE.md`.

## LOLIPOP Tree Packs

The production trees in `resources/trees/lolipop/{fir,pine,maple}/` are
derived from **Realistic Fir Trees Pack (LODS, gameready)**, **Pine trees pack
(lowpoly, game ready, LODs)**, and **Maple trees pack (lowpoly, game ready,
LODs)** by LOLIPOP (https://sketchfab.com/lolipop_1707). They are licensed
under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The original
per-pack attribution, source links, and license terms are retained verbatim in
each imported `license.txt`.

These are production assets, not review-only imports: the imported-forest
catalog (30 variants, four authored LODs each) is drawn by the GPU-driven tree
renderer, with one shared LOD3 impostor for far trees.

Changes were made to the originals. The prepared catalog
(`resources/trees/prepared/lolipop_catalog.res`), the composed material
adaptation, and the shared far impostor built from the authored LOD3 card
meshes are adaptations of LOLIPOP's work and carry the same credit, with a
"modified" note. The supplied source files themselves are unmodified.
