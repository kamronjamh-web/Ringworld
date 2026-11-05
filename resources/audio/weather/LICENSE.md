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

### Spatial wind and distant-storm derivatives — 2026-10-25

The rain sound field (storm revamp S5) plays wind and the distant storm from four
ring-fixed directional voices each, so it needs mono sources. `wind_spatial_loop.ogg`
(57 s) and `storm_distant_spatial_loop.ogg` (29 s) are mono derivatives of
`wind_loop.ogg` and `storm_distant_loop.ogg` above, not new recordings. Source
authors and CC0 provenance are unchanged; the stereo originals are retained (the
gameplay scene no longer selects them). The rain beds keep using the stereo
`rain_light_loop.ogg` / `rain_heavy_loop.ogg`; `rain_*_spatial_loop.ogg` are no
longer selected either.

Conversion used FFmpeg 9.0 with the same explicit channel average, no gain, trimming
or time stretching, and no new wrap crossfade (the stereo sources already carry one):

```text
-af "pan=mono|c0=0.5*c0+0.5*c1" -ar 44100 -c:a libvorbis -q:a 5
```

| Asset | Mean / peak dBFS | SHA-256 |
| --- | --- | --- |
| `wind_spatial_loop.ogg` | -26.3 / -9.8 | `43A194C38914F0E58CDF06F7B47DCEA57C3E917137AAB0D72C9651FEBFFD284F` |
| `storm_distant_spatial_loop.ogg` | -33.6 / -18.3 | `935E40C2D03CBDA67BDA883E9DD5E83175249FC39565EA332785D2994083C88D` |

Probing confirmed one channel, 44.1 kHz and durations equal to the sources. Listening
for the mono fold of the wind (phase cancellation) remains pending.

Foliage, sheet-metal and roof-drum rain, hail, the one-shot sets, the gutter trickle, the
foliage wind rustle and the thunder near/far variants are now generated procedurally (next
section); no further recordings were needed.

### Procedurally generated weather sounds — 2026-10-25

The storm revamp's missing rain, hail, drip and thunder assets are **original synthesised
work generated for this project**, not recordings: no downloaded audio is involved. The thunder
variants additionally process the CC0 thunder clips above (rubberduck, `100 CC0 SFX #2`), so
their provenance is that source plus this generator. No other third-party material is used.

- Generator: `tools/media/weather_sfx_gen.py` (Python 3.13.2, numpy 2.5.4; FFmpeg 9.0 for
  decoding the thunder sources and encoding). Every asset is seeded by the CRC-32 of its output
  name, so the run is reproducible:
  `python -I tools/media/weather_sfx_gen.py --wav-dir <scratch> --out-dir resources/audio/weather`.
- Encoding: mono, 44.1 kHz, `-c:a libvorbis -q:a 5 -map_metadata -1 -fflags +bitexact -flags:a +bitexact`.
- Models (parameters are in the generator, one function per asset family):
  - Plips, cave drips and eaves dribbles: impact click plus a Minnaert bubble (damped sine with a
    rising pitch, after van den Doel 2005). Cave drips add a synthetic cave reverb (early
    reflections, dark exponential tail, RT60 1.8-2.6 s).
  - Leaf drips: dark papery tap, leaf-flex tone (170-420 Hz) and 1-3 follow-up droplets.
  - Hard ticks: `hard_tick_01-04` stone (click plus spray), `05-08` metal (click plus five
    inharmonic partials).
  - Roof plinks: sheet-panel plate modes (0.7-6 kHz) plus a body thump (90-190 Hz).
  - Hail hits: ice click, short ice partials, a thud, and 2-4 decaying bounces.
  - Loops are built circularly: events wrap past the end onto the start, and noise beds and
    modulations are periodic in the loop length. The seam is continuous by construction and has
    no crossfade. Rain on metal and the roof drum use circular modal synthesis (one fixed panel
    mode set, per-drop random excitation).
  - `thunder_near_*`: a synthesised crack (14-30 N-waves from the channel), a low boom and
    tearing crackle at sample 0, then `thunder_0N.ogg` (N = 1, 2, 3, 1) at 0.90-1.05x rate
    0.12-0.3 s behind. Loudness is matched to the existing clips (-25.5 dBFS RMS), with a soft
    knee on the crack to -1 dBFS.
  - `thunder_far_*`: two existing clips slowed to 0.60-0.78x rate (lower, longer), layered
    0.7-1.6 s apart, plus a swelling sub-rumble (25-110 Hz), low-passed at 450-750 Hz with a
    120 ms onset. The clip pairs are (01,02), (02,03), (03,01), (01,02).

Wiring: `RingWeatherAmbience` plays `rain_surface_foliage` (foliage columns),
`rain_surface_metal` (POI and building occluders from the occlusion map) and `rain_roof_drum`
(roof voices). The near one-shot pool loads `shots/{plip,leaf_drip,hard_tick,roof_plink,cave_drip,eaves_dribble}_NN.ogg`.
`RingWeatherAudio` loads `thunder_near_NN` (strikes under 1.5 km) and `thunder_far_NN`
(over 8 km). `hail_loop` and `shots/hail_hit_*` (storm S3 hail) and `leaf_skitter_loop` (S3 gust front,
2026-10-26) are wired into `RingWeatherAmbience`; `gutter_trickle_loop` and
`foliage_wind_rustle_loop` are made ahead of storm S2 and are not referenced yet. The gust-front roar has no
asset of its own: it is the wind loop pitched down. Fat-drop plips reuse the plip set pitched down.
`leaf_skitter_loop` is generated by `leaf_skitter` in the same generator (clustered dry leaf ticks and scrapes over a
soft rush; seeded like the others).

Verification:
- Probing and decoding confirmed one channel, 44.1 kHz and finite PCM.
- Loop seams: on the decoded Ogg files, the wrap step and the spectral flux across the seam lie
  inside each loop's own internal distribution.
- Long-term octave spectra were compared with the CC0 recordings above. Foliage centroid is
  4.1 kHz, against 5.1 kHz for the "rain on grass and leaves" recording.
- Subjective listening is still pending.

| Loop | Length | Peak / RMS dBFS | SHA-256 |
| --- | ---: | --- | --- |
| `rain_surface_foliage.ogg` | 24 s | -4.0 / -24.0 | `AE2A22E873C70E4E9387AF7C922926CF6078ADC812AF329B96698E54B726D95C` |
| `rain_surface_metal.ogg` | 24 s | -5.0 / -24.0 | `D90A63E4BBAA8944C04F181BB4D643F2CA2AB3836EDA39ABACFDBE7B8806D152` |
| `rain_roof_drum.ogg` | 24 s | -7.1 / -24.0 | `E5078D4BAF388473A8D88E4168F56004AFFD8D40D4C8F01A5DB75D9A47420E59` |
| `hail_loop.ogg` | 24 s | -3.0 / -22.1 | `6BA5DD9C4A156CD39CFB6E355B723B211F0EDDE8638AA82243D238150A5A29BB` |
| `gutter_trickle_loop.ogg` | 24 s | -5.3 / -24.0 | `4FAD4534ED20A5072E94B8372A632BCC9AE156DB476BAB7FE5A03554CF713498` |
| `foliage_wind_rustle_loop.ogg` | 30 s | -6.0 / -26.0 | `17C798B5C67178770FF72C77CF962EFFB375CA2B79C4F64606534EC3CA8DE2A8` |
| `leaf_skitter_loop.ogg` | 24 s | -6.0 / -26.1 | `BD29504FDF0180DAB73A77383F8E07AE93D1A6463EBD37131ABB6C829E93519B` |

All one-shots are peak-normalised to -6 dBFS. Lengths: plip 0.32 s, leaf drip 0.45 s,
tick 0.35 s, plink 0.7 s, hail 0.5 s, cave drip 2.6-3.0 s, eaves dribble 2.2 s.
Thunder near is 4.3-5.5 s at -1.0 to -1.4 dBFS peak; thunder far is 10 s at -4 dBFS peak.

| File | SHA-256 |
| --- | --- |
| `thunder_near_01.ogg` | `5CA9976157E93C62DCCDD2CA1FA474D32E4FFDA59D00B7FBC06C10B711EF9BFD` |
| `thunder_near_02.ogg` | `DB71550AAD2071F89AB965623FF878D66A5C69753AA7D3497839AC9357F2EA7D` |
| `thunder_near_03.ogg` | `A3B22B344674E2027AE22DCA1276B607A7CCB75D96DC95BF2E7B86E3B42D46B3` |
| `thunder_near_04.ogg` | `7A1734B70ED33E0598BFDC7503CE70F9E65563D02FCEC8A91B8F1379067848A4` |
| `thunder_far_01.ogg` | `96CD2A7EA41C5B42DCE89A924D4290DCF4DA5B364866B37FA72C33B20BBD1E7F` |
| `thunder_far_02.ogg` | `C721136DC0349D87B10E7BE7AE27743D0320683FA453BE1ED9F7FF1D625780D8` |
| `thunder_far_03.ogg` | `192ABA759EC9D1B4C6E57B157AE920BC9E9B7E216BC397DFA2293E1D0C14B3ED` |
| `thunder_far_04.ogg` | `69F6E07F16A7E544FD3AF562DAA1DD9208549713A505C1A8BC573A40E4C47267` |
| `shots/cave_drip_01.ogg` | `0095F8B649C7A2B1667773963F3B585838D1669AF25DFC5DB09062B581DF7A4E` |
| `shots/cave_drip_02.ogg` | `C3F1ADD05F3463BF4E515E31804FBF619D380481CA27181A21A583DEA58E854E` |
| `shots/cave_drip_03.ogg` | `4F490A3FAEB36958B24F2B38D1FEDE18CBEB7A4D4C05067226FEAEB8BE2F5BBA` |
| `shots/cave_drip_04.ogg` | `B3720199CB46CCFEAA1119B899FB3A455E176769DC7D84D44CEACC3186F3CB3F` |
| `shots/cave_drip_05.ogg` | `46CC542A67FCD0C0A2E4E0F6C153B20C2586B1227F270E574F7A92413739C238` |
| `shots/cave_drip_06.ogg` | `597785114D116C3DF8FD979C7104142155C425FDB8175AE3D06BBC5D6F494C9E` |
| `shots/cave_drip_07.ogg` | `028B7FB1B739881957EB6A9D22D0D84547E011080FF42E233E58198D3E39F10C` |
| `shots/cave_drip_08.ogg` | `B8C22E9279C1482B60B291DF19DB43F5A95037D294FD8277F19B513414BF965A` |
| `shots/eaves_dribble_01.ogg` | `4B9617262A0E9E0326FE6A31E43B4BAE433D3D46B51338409C975FE4900829C5` |
| `shots/eaves_dribble_02.ogg` | `0F91EEC867B63D0E763F80750B9B5C3105C06EAF082F0C006CF9E66B9F5470F9` |
| `shots/eaves_dribble_03.ogg` | `BC5C08228832D340813936BE89A7BDFAEBBE1A7B8FEB40C77EC1127CAFC2AE98` |
| `shots/eaves_dribble_04.ogg` | `46F437FD577FA58F7E35877DA74B7D20E7CEA3A42679B53F3CB9F17E1249FB79` |
| `shots/hail_hit_01.ogg` | `2EEFB08B27CEF55C4B681BB1044B0B8BA82274887D52B5499362A75F79A6FDEF` |
| `shots/hail_hit_02.ogg` | `C40C629DB69F0C86FF2DD2BE956792DC2D4BB45C2A60215954D4684A9CDB684B` |
| `shots/hail_hit_03.ogg` | `2D715965453A0E14F802CD63F0CBC8D10B2AD6863CFC9D256FA5671767F0AE35` |
| `shots/hail_hit_04.ogg` | `92A032A83BDDDE132C266D41179E4894BD11588EE182D7692D2558EC881F4DFF` |
| `shots/hail_hit_05.ogg` | `6FAC0C201150228413A73925D7AEC1807F509423AFEF947078625FB2E3CABE68` |
| `shots/hail_hit_06.ogg` | `A05FD350FE96E6FE441DFAF5663D3AC29EE1A310A6AA5D426017E4ACCC23F463` |
| `shots/hail_hit_07.ogg` | `3004123EFDF80FE1655C89A0105A1C455E606DE314160A6EB4FFE0149DF038D5` |
| `shots/hail_hit_08.ogg` | `4F057C86D73BA147F926897DE1CD9A61CCE563793E7A3BF4842321A3A9FB5278` |
| `shots/hard_tick_01.ogg` | `51E8389CA7AEEB13E023FACD600CF2C8DF2A9D361384843EF0AEB0B508495622` |
| `shots/hard_tick_02.ogg` | `E47842E8612C63C9C00CA639A93B90DC9485703F319FA91AC153102FB97F733F` |
| `shots/hard_tick_03.ogg` | `C8B1E567274792467B5B58CA802BDEDC8E2179E6BCA940ABA56A6C8682668797` |
| `shots/hard_tick_04.ogg` | `83727534D451FF58BB16A7F6E479BDAFD85E166D8CFAE79625CE338508D02000` |
| `shots/hard_tick_05.ogg` | `CF14166C131CF286D09D93CAB39616075CE3F7DD3648373595FF33C491828237` |
| `shots/hard_tick_06.ogg` | `8E0999B7DF2D2C1252F4B72AC7EAD0AD8340E49F858FE8B399857D69D9AD5481` |
| `shots/hard_tick_07.ogg` | `CD61E0431A98CF3C5CFDCC91063A10F2AD35F839A3A5373B1232D724B4DA80A0` |
| `shots/hard_tick_08.ogg` | `E4DC400B3313BA60F08D79345F93EF8B8BABF73796ECFB4FD460AAA88F891F3A` |
| `shots/leaf_drip_01.ogg` | `3F128AADF00EEA13F8E9A72A92F3C687567A22881485A9BAE5077397ED08A7F7` |
| `shots/leaf_drip_02.ogg` | `802B22616963D2C7F901EFF698EF7ACBD2A03C2B5114E924D9101E205DD4D700` |
| `shots/leaf_drip_03.ogg` | `EDDF550CC075705CB0DC981F201EE02DF3BA3465DD17093559212C7C4E986CE8` |
| `shots/leaf_drip_04.ogg` | `DC53FE4556B7A0549651C22F1F673D5DDA134A0CB07D1B63A2BE94F2DB2E3B42` |
| `shots/leaf_drip_05.ogg` | `B00F5A46ACB43355ABEF979AB45B1177127C2EF5ADB9ECF70AAC1CE7566221EC` |
| `shots/leaf_drip_06.ogg` | `69538319C5F951DEADDB34DEF0886ED3AAC3D92AF3D5030ED0CBBF4AED7E7FD3` |
| `shots/leaf_drip_07.ogg` | `3346C19084DA7E8909D9B233F880469D6A463A6ACBB9AFEB18989B86874EDE93` |
| `shots/leaf_drip_08.ogg` | `417F36A7A544861D9417E362F693FA89CCA27E498FCA565F173AE5CA658684E5` |
| `shots/plip_01.ogg` | `64804641E7BFED25937B62DE9BB8922C377431F8079D3B1A325C8123936A4DA0` |
| `shots/plip_02.ogg` | `3361444FD1060E4E5121DB7203C0DE86C9C9F0656F04B716E8704585039B22B0` |
| `shots/plip_03.ogg` | `4B9122289631610FE5480B301FDD7CE6A0330278DC9976DA6E97AF4E7210720E` |
| `shots/plip_04.ogg` | `B2811B64FF92E840533A4A9E4A0A3840080F4688654E360F54CC35497E54FB8B` |
| `shots/plip_05.ogg` | `3B76A742707315E119A2D398AED4A54BCCCB05F2F9E69FC5C8DFE2E15C9AC7F2` |
| `shots/plip_06.ogg` | `1BC818840B4ECBE4738DE023A138C7B65102F7EADD735782BD3C37387F836A5C` |
| `shots/plip_07.ogg` | `5B3B0B41A7947DDEED63A8F8DF7469C22F204363B751F6A5E1FC46B3B4D61633` |
| `shots/plip_08.ogg` | `42D3E85860B8238CAB9E0601905B11FC0891F37605EBF78369CCD1A02977B784` |
| `shots/plip_09.ogg` | `5EA902D1B213686E465EDC156561C9CAF5E4E07B52E5E84102A6A1E52C61DEE2` |
| `shots/plip_10.ogg` | `DA216988174DC7906A6A34320AB4FCA7AAC2B115B0EAE935F4B2F1A053C39365` |
| `shots/roof_plink_01.ogg` | `DF14D4D81F5085673C7BD056D5518DFEA1E2073886C575CB6EC7DAAB863971F2` |
| `shots/roof_plink_02.ogg` | `62ACFA57EA7DED8CF16B3A301EA0E814D94D876BFA7DE6C70D9668F8D6114AFE` |
| `shots/roof_plink_03.ogg` | `8D3C201B3F0977F5CD82E13F22E939A1C47F636D55701DBDDA87170E065A3A45` |
| `shots/roof_plink_04.ogg` | `4681782D4C3CCF8BAFE4D0B9CAF81EA1F49B380FC98931F45CCA4604745E8B9A` |
| `shots/roof_plink_05.ogg` | `8AF6DCC762F3E4449E61D6C9B16859531626F7C7BDDDBE1F2CE3C014D43DB791` |
| `shots/roof_plink_06.ogg` | `FB0B781AB712D8A872579DEC3969B8FF1B2E9E272A169B549B48B9E1BC89D9DC` |
| `shots/roof_plink_07.ogg` | `8BF19C5C2BC2E304274919FFAEA6307A5B9BE6B746BE0098A5DD6BFC571D024C` |
| `shots/roof_plink_08.ogg` | `ACF7E27DB7E2BC75B5A2F60A51AFA394A5D39E0458B1B903B03BD3F02AEBEAB5` |

### Megastorm sounds (procedurally generated) - 2026-10-30

The megastorm's own voices (docs/megastorm-design.md 5.2) are **original synthesised work generated for this project**, from seeded noise and sine
partials only: no recordings, no downloaded audio and none of the CC0 thunder clips. Generator: the same `tools/media/weather_sfx_gen.py`
(`--only megastorm`), seeded by the CRC-32 of the output name, encoded like the other weather assets (mono, 44.1 kHz, `libvorbis -q:a 5`).

| File | Length | Model | SHA-256 |
| --- | ---: | --- | --- |
| `megastorm_ground_hum_loop.ogg` | 24 s | Structure-borne bed: six 20-60 Hz partials with slow whole-cycle beating, weak 2nd/3rd harmonics, a 15-90 Hz noise floor. Exactly periodic. | `F9171207EB8B0F3CE5D31669A9299697983A2064BA8F7C2E838BC71813A84C9A` |
| `megastorm_roar_loop.ogg` | 30 s | Wall roar: three independently swelling bands (35-420 Hz rush, 160-900 Hz growl, 1.5-6.5 kHz hiss). Exactly periodic. | `07E72440BA7F4C8B8F4102CB29ABE8BDD323C3E2A7E3FFD12DCBC068A7F2E4B0` |
| `megastorm_boom_1.ogg` | 4.4 s | The Mach pile-up: a 3.5 kHz-band crack, a sub thump gliding 72 to 28 Hz, then 9-13 darkening rolling noise bursts; the shape follows `MegastormSound.BoomEnvelope`. | `7E8BEDAFC6C5115BC48667A3CA647038CC3C093D2BD70EC497092FEA94E7072F` |
| `megastorm_boom_2.ogg` | 4.4 s | As above, other seed. | `605BAED53B23C76CCCBABB5CD70613EBC5EEEDD6952AFCA4BFD007086B43048E` |
| `megastorm_boom_3.ogg` | 4.4 s | As above, other seed. | `452A0FF9FDF6F30484A6DDFBE5F5AF49484403518D284914DDF434404A9AD3C7` |
| `megastorm_scythe_crackle_1.ogg` | 0.9 s | Lightning-scythe tear (WO12, 5.5.4): a dense burst of sharp 1.5-12 kHz clicks (about 1,100 per second falling to 60) over a short hissing bed, 2-4 low pops, fast decay. One per path segment, each fired at its own d / 343 delay. Seeded by the output name. | `88F784298CC16585CCB60774D0E236558173437B1554A9375C13FC46EF0E21FF` |
| `megastorm_scythe_crackle_2.ogg` | 0.9 s | As above, other seed. | `700D990C0B13AFAC1F94C7499268CE3BC373BF883E1BEC2E5706B37D6598008C` |
| `megastorm_scythe_crackle_3.ogg` | 0.9 s | As above, other seed. | `3CF3C9C068CD37343B9AC5A9215119BEA330248BCB6B3963A8527F379F09CB4D` |
| `megastorm_rain_hammer_loop.ogg` | 24 s | Bullet rain on structures (WO11, 5.5.2): about 2,600 hard 0.4-8 kHz impact clicks per second, a few heavy panel thwacks, over a grainy 150-3500 Hz bed. Events wrap, so the loop is exact. | `D1B1E6D02E1F7C48971C08EEFBB8631312B44C6B2D3C60A5258A1B1091D6761A` |
| `megastorm_ear_pop.ogg` | 0.7 s | Wake ear pop (WO11, 5.5.3), cosmetic: a soft 70-35 Hz thump, a few tiny clicks and a short low-passed breath. | `0BF888E00AF81F36F337FDBE7657FF46C04DE5B2FFCA9FF66499B2DBED866EA8` |
| `megastorm_rim_sizzle_loop.ogg` | 12 s | Near-rim contact-arc sizzle (WO13, 5.5.5): about 1,500 tiny 2-14 kHz clicks per second over a swelling 2.5-9.5 kHz hiss and a faint 100 Hz buzz with 2nd/3rd harmonics. Events wrap, bed and buzz are whole-cycle, so the loop is exact. Played by `RingFieldStrikeAudio` (scripts/Weather/Megastorm) on the `MegastormAir` bus. | `A3BED8B8C1BD224D9B95E6C26C869B3DFD011A7CEC6C863DC6CAB9DF78521AF2` |

Wiring: `RingMegastormAudio` (scripts/Weather/Megastorm). The scythe crackles are played by `RingMegastormScythes` (same folder) on the `MegastormAir` bus. Subjective listening is still pending; `tools/media/megastorm_audio_render.py` renders a
passage from the test's gain timeline for that.

### Emitter field shot sounds (procedurally generated) - 2026-11-05

The emitters' field shot voices (the orb a ground emitter fires up to the containment field) are **original synthesised work generated for this project**,
from seeded noise and sine partials only: no recordings, no downloaded audio and none of the CC0 thunder clips. Generator: the same
`tools/media/weather_sfx_gen.py` (`--only emitter_orb_launch field_contact_crackle`), seeded by the CRC-32 of the output name, encoded like the other weather
assets (mono, 44.1 kHz, `libvorbis -q:a 5`).

| File | Length | Model | SHA-256 |
| --- | ---: | --- | --- |
| `emitter_orb_launch_1.ogg` | 1.6 s | Orb launch heard near the emitter: a soft 40-90 Hz thump gliding down with a 2nd harmonic, plus a short rising band-passed air whoosh (0.3-2.6 kHz, then 1.2-4.8 kHz as it climbs); raised-cosine onset, no click. | `AC23A06BEBB8B8955597D947EF3E5DAE4AC6030C484D21B9BABE6AE9533FD05D` |
| `emitter_orb_launch_2.ogg` | 1.6 s | As above, other seed. | `B688B30BF23E0C5446738DE3F06B3292095501F065DDF31775C161D521A893EC` |
| `emitter_orb_launch_3.ogg` | 1.6 s | As above, other seed. | `1D96D99C6921EE1F8A03B358B4ADAE45163F3A543E529F87873437EBAC32EBB3` |
| `field_contact_crackle_1.ogg` | 1.8 s | Orb meeting the field, heard from far: sparse 1-6 kHz electrical crackle clicks (about 90 per second thinning to 6), darkened by a 4.2 kHz low-pass, over a soft 25-220 Hz rumble tail. | `5CC9FCC75190AC0BB9A75316FB1B8F780C6509436F0E2D6808F3FCEAAAD36845` |
| `field_contact_crackle_2.ogg` | 1.8 s | As above, other seed. | `A6764939A6D2A82C48EC33A857833E26C83DA1A9462FD1A2EC24ED7915E9D77B` |
| `field_contact_crackle_3.ogg` | 1.8 s | As above, other seed. | `4E5A8E65A8D6D52AE5CE7F7D76A34AC5A8FFAF09146328BEB21B7ADEC45ADA1E` |

Wiring: `RingEmitterFieldShotAudio` (scripts/poi/Emitters) on the `Thunder` bus.

### Containment field crossing sound (procedurally generated) - 2026-11-05

The soft membrane "whump" played when the player passes through the containment field (decision 4, contact ripples) is **original synthesised work generated for
this project**, from seeded noise and sine partials only: no recordings, no downloaded audio and none of the CC0 thunder clips. Generator: the same
`tools/media/weather_sfx_gen.py` (`--only field_crossing_whump`), seeded by the CRC-32 of the output name, encoded like the other weather assets (mono, 44.1 kHz,
`libvorbis -q:a 5`).

| File | Length | Model | SHA-256 |
| --- | ---: | --- | --- |
| `field_crossing_whump_1.ogg` | 1.4 s | Membrane whump: a 52-70 Hz pressure body gliding down from a 38 Hz base with a 2nd harmonic and a small second swell, plus a short dark 80-900 Hz breath; raised-cosine onset, band-limited to 28-1200 Hz, no clicks. | `8BD962D1E0196D309A4BF8A000B756E02FB48A786134D652706A3A5F951837FC` |
| `field_crossing_whump_2.ogg` | 1.4 s | As above, other seed. | `3680C2EB54E1AE523FB9303884324CF1CAFB8BA6DBFEE42A60147D82EAA9F094` |

### Containment field hum (user-supplied slot) - 2026-11-05

`field_hum_loop.ogg` (or `field_hum_loop.wav`) in this folder is **reserved for the user's own recording or synthesis** and is not shipped or generated. `RingContainmentFieldAudio` loads
`res://resources/audio/weather/field_hum_loop.ogg`, else `.wav`; with no file the hum is silently off and nothing logs an error. Expected format: a seamless loop (the code turns
looping on for Ogg and for a WAV with loop mode off), mono or stereo, any sample rate; keep it a low, steady hum (peak near -6 dBFS: the code applies a -12 dB base plus the
"Field hum volume (dB)" menu setting). Add a row for the file here when it is dropped in (source, author, licence).
