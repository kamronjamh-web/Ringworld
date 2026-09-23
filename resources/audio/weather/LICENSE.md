# Initial thunder asset notice

The three thunder clips in this directory are edited variations of one source
sound, not three independent field recordings.

- Original author: rubberduck.
- Source collection: [100 CC0 SFX #2](https://opengameart.org/content/100-cc0-sfx-2).
- Source archive: [sfx_100_v2.zip](https://opengameart.org/sites/default/files/sfx_100_v2.zip).
- Original archive member: `sfx100v2_thunder_01.ogg` (48 kHz stereo, 5.26475 seconds).
- License listed by the author: [CC0 1.0 Universal](https://creativecommons.org/publicdomain/zero/1.0/).
- Retrieved and source listing checked: 2026-09-05.
- Source archive SHA-256: `0FC61B4494E2E893C0C015CED4877B3F689C7D84A48CB61DAECD7DDB52DB797B`.

CC0 attribution is not required; this notice records provenance and edits.
No unrelated sounds from the collection are included in the game.

## Conversion and variants

All clips were trimmed to source seconds 0.09 through 4.60, downmixed to mono
for positional playback, encoded as 44.1 kHz Ogg Vorbis (quality 5), and reduced
by 3 dB. A 4 ms fade-in and 250 ms tail fade avoid edit-boundary clicks.
The leading trim removes most of the source's initial silence so its audible
onset follows the scheduled arrival. Timing tests must still allow for the
clip's envelope and the audio mixer's latency.

| Game file | Offline variation | High-pass | Tail fade starts |
| --- | --- | ---: | ---: |
| `thunder_01.ogg` | Original speed | 30 Hz | 4.260 s |
| `thunder_02.ogg` | Rate changed to 42,240 Hz before resampling; lower/longer | 30 Hz | 4.875 s |
| `thunder_03.ogg` | Rate changed to 51,840 Hz before resampling; brisker | 40 Hz | 3.926 s |

Processing used FFmpeg 9.0.1. Reproduction filter for the original-speed clip:

```text
atrim=start=0.09:end=4.6,asetpts=PTS-STARTPTS,highpass=f=30,afade=t=in:d=0.004,afade=t=out:st=4.26:d=0.25,volume=-3dB
```

For the two rate variants, insert `asetrate=42240,aresample=44100` or
`asetrate=51840,aresample=44100` after `asetpts` and use the table's high-pass
and tail-fade values. Output options: `-ac 1 -ar 44100 -c:a libvorbis -q:a 5`.

The runtime uses normal playback speed; these offline variations do not alter
the sound-travel delay. Distance filtering and attenuation are applied by the
game. This is the initial thunder set; independent recordings and surface-impact
sounds remain future phase 3 audio work. The separately documented rain/wind
loops follow below.

## Weather ambience loops

The four looping ambience beds below are separate from the thunder set. All
outputs are stereo, 44.1 kHz Ogg Vorbis quality 5. They are continuous rain,
wind, and diffuse storm-bed material only: `storm_distant_loop.ogg` contains
**no added thunder**. Subjective listening and final mix QA remain pending.

### Sources and provenance

- `rain_light_loop.ogg`, `rain_heavy_loop.ogg`, and the rain portion of
  `storm_distant_loop.ogg` derive from Ylmir's
  [Rain loopable](https://opengameart.org/content/rain-loopable), published
  2016-03-28 under [CC0 1.0 Universal](https://creativecommons.org/publicdomain/zero/1.0/).
  Source archive: `Rain OGG.zip`, SHA-256
  `E68F3E1C77493CF43BEC84CEBFF5043BFF6BE9CE16D59B24CAA988CD460AA75B`.
  Archive members: `1.ogg` (light), `2.ogg` (heavy), and `3.ogg` (distant-rain mix).
- `wind_loop.ogg` and the wind portion of `storm_distant_loop.ogg` derive from
  Luke.RUSTLTD's [wind1](https://opengameart.org/content/wind1), published
  2010-10-01 under CC0 1.0 Universal. The author describes it as synthesized
  in PureData, not a field recording. Source `wind1.wav` SHA-256:
  `A2856F794FBF77E05F8241DC4ADEADE7F62FA21C3C9DC49F7725B6C6218060C3`.

CC0 attribution is not required; these details preserve reproducible source
and processing provenance.

### Offline processing

| Game file | Source and processing | Delivered duration |
| --- | --- | ---: |
| `rain_light_loop.ogg` | `1.ogg`, high-pass 160 Hz, low-pass 10,000 Hz, -3 dB | 26 s |
| `rain_heavy_loop.ogg` | `2.ogg`, high-pass 100 Hz, low-pass 11,000 Hz, -4 dB | 25 s |
| `wind_loop.ogg` | `wind1.wav`, high-pass 35 Hz, low-pass 4,500 Hz, +16 dB | 57 s |
| `storm_distant_loop.ogg` | `3.ogg`: high-pass 60 Hz, low-pass 650 Hz, +2 dB at 0.65 weight; plus `wind1.wav`: high-pass 35 Hz, low-pass 650 Hz, +16 dB at 0.35 weight; no normalization | 29 s |

The original light, heavy, wind, and distant-rain source portions were 27 s,
26 s, 58 s, and 30 s respectively. Each output uses the same 1-second
equal-power wrap crossfade: retain the middle `1..D-1` seconds, mix the tail
`D-1..D` and head `0..1` across the fade with sine/sine equal-power gains, and
append that seam. This produces the delivered durations shown above and softens
the edit boundary. The decoded files have finite PCM below full scale; their
wrap steps are within the range of ordinary internal sample steps. These
numeric checks do not replace listening for clicks or tonal repetition.

### Spatial rain derivatives — 2026-09-05

The gameplay scene now selects `rain_light_spatial_loop.ogg` (26 s) and
`rain_heavy_spatial_loop.ogg` (25 s) for the local 3D rain emitters. These are
mono derivatives of the corresponding stereo loops above, not new recordings.
Their source authors and CC0 provenance are unchanged. The original stereo
files are retained; the wind and distant-storm assets are unchanged.

Conversion used FFmpeg 9.0.1 with an explicit channel average and no additional
gain, trimming, or time stretching:

```text
-af "pan=mono|c0=0.5*c0+0.5*c1" -ar 44100 -c:a libvorbis -q:a 5
```

- `rain_light_spatial_loop.ogg` SHA-256: `648D9FB53851B5BC726310FD5E679658F7D930F2516E4EF5A51ADE3ADC714871`.
- `rain_heavy_spatial_loop.ogg` SHA-256: `1EA09CB830C5D40A871178144D85FE8EA2087C6D16F9D3186B1B762E0CB9340D`.

Independent probing verified one channel, 44.1 kHz, and the original loop
durations. Full decoded sample peaks were approximately 0.246 and 0.385,
with finite PCM and wrap steps below each file's maximum internal sample step.
The runtime uses four phase-staggered voices per rain layer with a 0.5 gain
factor per voice before 3D attenuation. Listening and final mix balance remain
separate from numeric validation.

### Surface rain recordings — 2026-09-05

The surface-rain layer uses three additional recordings. No Minecraft audio or
other Minecraft assets are included. Each author's source page explicitly lists
CC0; these notes preserve provenance, rather than asserting that the recordings
were made specifically for this game.

- Ground/soft-surface texture: SamsterBirdies,
  [Rain on grass and leaves](https://freesound.org/people/SamsterBirdies/sounds/584268/),
  published 2021-08-21. The recording includes grass, leaves, and quiet splashes;
  its use for the game's soil category is a sound-design approximation, not an
  isolated bare-dirt recording. Downloaded the publicly available
  [HQ MP3 preview](https://cdn.freesound.org/previews/584/584268_5487341-hq.mp3),
  not the login-gated original FLAC. Downloaded file: 48 kHz stereo, 169.951229 s;
  SHA-256 `B79F9077FE56B5FF84D1CF8DE61DA53A8F1469F76FDC27045805484A0A4C65F8`.
- Water texture: bbrownmuse,
  [Rain in puddle](https://freesound.org/people/bbrownmuse/sounds/735468/),
  published 2024-05-14. Downloaded the publicly available
  [HQ MP3 preview](https://cdn.freesound.org/previews/735/735468_15903677-hq.mp3),
  not the login-gated original WAV. Downloaded file: 48 kHz mono, 81.247833 s;
  SHA-256 `BD7CB3A937D5CF5EC9E0008B762A5375548AC88D9461C5BCD7C83B6A9B2168D3`.
- Hard-surface/roof texture: alxl,
  [Rain on Window Loop](https://opengameart.org/content/rain-on-window-loop),
  published 2023-01-19. The author explicitly permits choosing CC0 from the
  listed licenses. Downloaded the
  [original WAV](https://opengameart.org/sites/default/files/rain_on_window_loop.wav):
  44.1 kHz stereo, 10.866259 s;
  SHA-256 `97B9405025E5ED7ED8A58D24DD7146D06E925519B1F718EFDA94BB85DCD1A61A`.
  This is a window recording used as a hard-surface approximation; it is not
  a collection of separate concrete, rock, glass, and metal recordings.

Source listings and downloads were checked on 2026-09-05. The source license
is [CC0 1.0 Universal](https://creativecommons.org/publicdomain/zero/1.0/).
Raw downloads are retained in `.godot/rain_surface_sources/` for local processing;
only the processed Ogg resources are referenced by the gameplay scene.

Processing: take a 10-second excerpt starting at 30 s (soil), 2 s (water),
or 0 s (hard). FFmpeg converts with
`aformat=sample_rates=44100:channel_layouts=mono`, applies `volume=13dB`,
`volume=4dB`, or `volume=-6dB` respectively, then
`alimiter=limit=0.6:level=false:latency=true`. Retain excerpt seconds
0.5–9.5; crossfade 9.5–10 against 0–0.5 using
`acrossfade=d=0.5:c1=qsin:c2=qsin`, and append that seam to the retained
middle. Encode with `libvorbis -q:a 5`. The resulting loop is 9.5 seconds,
with a wrap crossfade rather than a recurring fade to silence.

Primary verification confirmed mono, 44.1 kHz, 9.5-second Vorbis files and
full-file decoded mean/peak levels below full scale:

| Asset | Mean / peak dBFS | SHA-256 |
| --- | --- | --- |
| `rain_surface_soil.ogg` | -24.8 / -4.2 | `6A136AE1F388374C0DA847E09F516C86426301D550516538242A4F10A30DA662` |
| `rain_surface_water.ogg` | -27.8 / -4.5 | `79DC2C5E50DDCE1277D02B406C85E6AA8F328C4176ADD0C51282B8D0A17C4FFE` |
| `rain_surface_hard.ogg` | -24.0 / -8.3 | `CF89C29C4B9B2D8E1D80E7B2729798BA77E6AE0602A470AD1C726E879C3599B9` |

The surface follow-up lowers the earlier four-voice rain field's per-voice
factor from 0.5 to 0.18, and adds at most eight surface voices with a 0.35
maximum factor each, before user volume, shelter mixing, and spatial attenuation.
Surface starts are staggered by 11.3% of the clip per slot. Numeric decoding and
engine PCM checks do not replace listening for repetition and combined mix balance.
