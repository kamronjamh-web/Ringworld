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
| `water_lap_loop.ogg` | 40 s | 1 | -20.2 | -4.6 | `4EB539F8B809B022581B7A316CAF3580C55558251F3BFA3075E23DB776253691` |
| `surf_break_loop.ogg` | 64 s | 1 | -20.5 | -4.1 | `7F6AF991D10AB8B9461E17B2971737EF904BE9E9557F2E11933FAE84AF2A9808` |
| `surf_far_loop.ogg` | 32 s | 1 | -20.2 | -5.8 | `DF86B384828868FCB5625B7ACA987B1925809250A7505E341DBCAEB61C4814A8` |
| `open_water_loop.ogg` | 40 s | 2 | -20.2 | -5.1 | `F63CF21925D15240BEB16EBF18ED63F97C8767EBC64C430AE0CB8ABFDBDF6470` |
| `stream_babble_loop.ogg` | 32 s | 1 | -20.2 | -4.9 | `39497090E51BE931525BAD11718B66164F9991302A88FADDDA4D915755638E8E` |
| `river_rush_loop.ogg` | 32 s | 1 | -20.2 | -5.6 | `C820B839DA09FB0C7D855E729FBE848655015F510AFFAD2B667F8EA14307691D` |
| `river_roar_loop.ogg` | 32 s | 1 | -20.1 | -6.0 | `7F2BCB995E2095E0DFB72D3B4C723837F908246F2A4D174639D97CDBB877D764` |
| `waterfall_loop.ogg` | 32 s | 1 | -20.1 | -6.4 | `D1EA679D17ADEEC704497DFB973D7FD3B0702722E9B2D4A12C26862A0DB5F3F3` |
| `underwater_loop.ogg` | 40 s | 2 | -20.1 | -6.5 | `311FE9166AEE1B9CA49DB6EA866F3DA089B240C96FD94192E3028D9338F06039` |
