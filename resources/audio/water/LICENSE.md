# Water ambience asset notice

`shore_wave_cc0.ogg` is a 16-second mono Ogg Vorbis loop made from
`wave_01_cc0-18363__jasinski__alkaibeach.flac`.

- Original author: jasinski (submitted to OpenGameArt by qubodup)
- Source: https://opengameart.org/content/beach-ocean-waves
- Original listing: https://www.freesound.org/people/jasinski/sounds/18363/
- License: CC0 1.0 Universal, https://creativecommons.org/publicdomain/zero/1.0/
- Conversion: 44.1 kHz mono Ogg Vorbis; source repeated to 16 seconds with
  short in/out fades for a compact looping ambience bed.

CC0 does not require attribution. This notice is retained for provenance.

## Synthesised water loops (T3R-10c)

Since 2026-11-14 only `surf_break_loop.ogg`, `surf_far_loop.ogg` and `river_rush_loop.ogg` are still the synthesised
files described here; the other six were replaced by Stable Audio Open takes (see the generated section below). The
table keeps only the synthesised files still in use.

The nine loops `water_lap_loop.ogg`, `surf_break_loop.ogg`, `surf_far_loop.ogg`, `open_water_loop.ogg`,
`stream_babble_loop.ogg`, `river_rush_loop.ogg`, `river_roar_loop.ogg`, `waterfall_loop.ogg` and `underwater_loop.ogg`
are original work, synthesised from seeded noise and a Minnaert bubble model (no recordings, no third-party material,
no licence to track). They are CC0 as far as the project is concerned; the generator is part of the repository.
`shore_wave_cc0.ogg` above is unchanged and is the only recording in this folder.

- Generator: `tools/media/water_sfx_gen.py` (imports helpers from `tools/media/weather_sfx_gen.py`, unchanged). Seeded by
  the CRC-32 of the output name, so two runs give identical files.
- Environment of the committed files: Python 3.13.2, numpy 2.5.4, ffmpeg 9.0 (libvorbis, quality 5, bit-exact flags).
- Regenerate: `python -I tools/media/water_sfx_gen.py --wav-dir <scratch> --out-dir resources/audio/water`
- Check: `python -I tools/media/water_sfx_gen.py --out-dir resources/audio/water --verify` (length, format, RMS, peak, seam,
  and steadiness of quarter-second windows; the negative control `shore_wave_cc0.ogg` must fail the gap and range checks).
- Each loop is normalised to -20 dBFS RMS with peaks under -3 dBFS (decoded), 44.1 kHz. Mono except `open_water_loop` and
  `underwater_loop` (stereo, decorrelated channels).

| File | Length | Channels | RMS dBFS | Peak dBFS | SHA-256 |
| --- | ---: | ---: | ---: | ---: | --- |
| `surf_break_loop.ogg` | 64 s | 1 | -20.5 | -4.1 | `7F6AF991D10AB8B9461E17B2971737EF904BE9E9557F2E11933FAE84AF2A9808` |
| `surf_far_loop.ogg` | 32 s | 1 | -20.2 | -5.8 | `DF86B384828868FCB5625B7ACA987B1925809250A7505E341DBCAEB61C4814A8` |
| `river_rush_loop.ogg` | 32 s | 1 | -20.2 | -5.6 | `C820B839DA09FB0C7D855E729FBE848655015F510AFFAD2B667F8EA14307691D` |

<!-- ai-water-loops:begin -->
## AI-generated water loops (T3R-10 replacement)

The `*_loop.ogg` files listed here are generated with Stable Audio Open 1.0 (https://huggingface.co/stabilityai/stable-audio-open-1.0), used under the Stability AI Community License (terms linked from the model card), through the local audio studio (`prototypes/audio-studio`, diffusers backend, 47 s, 100 steps, guidance 7). They replace the synthesised loops of `tools/media/water_sfx_gen.py`; file names, loop flags and levels are unchanged.

Processing (`tools/media/water_sfx_ai.py build`): decode the picked take, drop the first and last 1 s, equal-power crossfade of the tail onto the head (3 s by default; the loop is that much shorter), mono downmix except open_water and underwater (stereo), 44.1 kHz, DC removed, normalised to -20 dBFS RMS with a soft knee only if the peak would exceed -3 dBFS, Ogg Vorbis quality 5. Check: `python -I tools/media/water_sfx_gen.py --out-dir resources/audio/water --verify --ai`.

Per file (JSON: take ID, seed, prompt, negative prompt, steps, guidance, backend, loop length s, channels, model revision):

- `water_lap_loop.ogg`: {"backend": "diffusers", "channels": 1, "guidance": 7.0, "length": 42.0, "model_revision": "f21265c1e2710b3bd2386596943f0007f55f802e", "negative": "music, melody, musical instruments, voices, speech, people, birds, seagulls, insects, wind, rain, thunder, engine, motor, boat engine, footsteps, swimmer splashing, fade in, fade out, low quality, distorted, lo-fi, 8-bit, bitcrushed, synthesizer", "prompt": "Gentle small waves lapping against a rocky lake shore, soft close splashes and trickles, calm fresh water, steady continuous ambience, high quality field recording", "seed": 7154972, "steps": 100, "take": "GEN-0036-03"}
- `open_water_loop.ogg`: {"backend": "diffusers", "channels": 2, "guidance": 7.0, "length": 42.0, "model_revision": "f21265c1e2710b3bd2386596943f0007f55f802e", "negative": "music, melody, musical instruments, voices, speech, people, birds, seagulls, insects, wind, rain, thunder, engine, motor, boat engine, footsteps, swimmer splashing, fade in, fade out, low quality, distorted, lo-fi, 8-bit, bitcrushed, synthesizer", "prompt": "Open sea far from shore, water sloshing and gentle swells against a floating hull, steady continuous, high quality field recording", "seed": 3736822, "steps": 100, "take": "GEN-0039-03"}
- `stream_babble_loop.ogg`: {"backend": "diffusers", "channels": 1, "guidance": 7.0, "length": 42.0, "model_revision": "f21265c1e2710b3bd2386596943f0007f55f802e", "negative": "music, melody, musical instruments, voices, speech, people, birds, seagulls, insects, wind, rain, thunder, engine, motor, boat engine, footsteps, swimmer splashing, fade in, fade out, low quality, distorted, lo-fi, 8-bit, bitcrushed, synthesizer", "prompt": "Small shallow mountain stream babbling over pebbles, close bright trickling and gurgling water, steady continuous, high quality field recording", "seed": 4349141, "steps": 100, "take": "GEN-0040-02"}
- `river_roar_loop.ogg`: {"backend": "diffusers", "channels": 1, "guidance": 7.0, "length": 42.0, "model_revision": "f21265c1e2710b3bd2386596943f0007f55f802e", "negative": "music, melody, musical instruments, voices, speech, people, birds, seagulls, insects, wind, rain, thunder, engine, motor, boat engine, footsteps, swimmer splashing, fade in, fade out, low quality, distorted, lo-fi, 8-bit, bitcrushed, synthesizer", "prompt": "Large powerful river rapids roaring, white water, heavy continuous rush, high quality field recording", "seed": 550992, "steps": 100, "take": "GEN-0042-03"}
- `waterfall_loop.ogg`: {"backend": "diffusers", "channels": 1, "guidance": 7.0, "length": 42.0, "model_revision": "f21265c1e2710b3bd2386596943f0007f55f802e", "negative": "music, melody, musical instruments, voices, speech, people, birds, seagulls, insects, wind, rain, thunder, engine, motor, boat engine, footsteps, swimmer splashing, fade in, fade out, low quality, distorted, lo-fi, 8-bit, bitcrushed, synthesizer", "prompt": "Tall waterfall crashing into a plunge pool, heavy continuous roar and spray, high quality field recording", "seed": 9335872, "steps": 100, "take": "GEN-0043-03"}
- `underwater_loop.ogg`: {"backend": "diffusers", "channels": 2, "guidance": 7.0, "length": 40.0, "model_revision": "f21265c1e2710b3bd2386596943f0007f55f802e", "negative": "music, melody, musical instruments, voices, speech, people, birds, seagulls, insects, wind, rain, thunder, engine, motor, boat engine, footsteps, swimmer splashing, fade in, fade out, low quality, distorted, lo-fi, 8-bit, bitcrushed, synthesizer", "prompt": "Underwater ambience recorded with a hydrophone, muffled low rumble, soft distant bubbles, steady continuous", "seed": 2778203, "steps": 100, "take": "GEN-0044-04"}

<!-- ai-water-loops:end -->
