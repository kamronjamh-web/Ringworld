# Sky work orders

Small, self-contained sky fixes. One section per order.

| Order | Title | Role | Size | Status |
|---|---|---|---|---|
| SKY-1 | Stars only where the sky behind them is dark | implementer | S | Written 2026-11-15, not started |

### SKY-1. Stars only where the sky behind them is dark

ROLE: implementer (Claude Code `implementer`, Codex `sol_implementer`)
SIZE: S
DEPENDS ON: none

OBJECTIVE
Stars are hidden in daylight by the brightness of the sky behind them, as in reality, instead of by fixed day and height ramps. One rule in the sky shader: a star shows only where the sky in its direction is dark enough for it. The same rule then gives every case without special handling: no stars in a daytime sky at any height the player can stand, stars coming out first overhead and away from the sun at dusk (brightest first), stars at eclipse totality, and stars in daylight only far above the surface (about 20 to 30 km and up, where the Physical haze sky turns dark).

CONTEXT
- The user saw stars through the daytime atmosphere. Realism (user, 2026-11-15): the 44 km of air above the surface has an 8 km Rayleigh scale height (`RingAtmosphereModel.BaseRayleighHeightMeters`), so from the ground it is effectively a full Earth atmosphere. It does not block starlight (zenith transmittance about 0.85 to 0.9); it hides stars because the scattered daylight is far brighter than any star. On Earth a point source must be about magnitude -3 to -4 to be seen against a clear day sky.
- `materials/ring_local_sky.gdshader` `sky()` adds `star_color * procedural_stars(...) * star_brightness * star_visibility` (about line 264) to `color` before the atmosphere. Both atmosphere branches then only transmit it (`color * T + in_scatter` in the Physical branch, `color * (1 - a) + rgb` in the Legacy branch), which dims a star slightly but never hides it against a bright sky.
- `star_visibility` comes from `scripts/day_side_atmosphere.gd` (`_star_visibility_for_height_and_day` :1448, combined at :426-429 and :561-562): a height ramp (20 to 26 km), a night ramp (`night_star_twilight_end_day_factor`, `night_surface_star_visibility`), the outside-the-ring term and the eclipse floor. Find and report which term makes stars visible in the daytime case the user saw.
- `procedural_stars` returns a per-star magnitude (0.32 to 1.0), so brighter stars can pass the rule first.

FILES/SYMBOLS
`materials/ring_local_sky.gdshader` (`sky()`, `procedural_stars`), `scripts/day_side_atmosphere.gd` (`_star_visibility_for_height_and_day`, the star exports, `_set_sky_param`), the grave-key menu and its persistence for the sky/atmosphere section (use the `live-tuning-setting` skill), `tools/celestial_visual_smoke_test.gd`, `tools/celestial_runtime_integration_smoke_test.gd`, `tools/eclipse_darkness_parity_test.gd`.

REQUIRED CHANGES
1. In `sky()`, keep the star radiance separate (`vec3 star_light`) instead of adding it to `color` before the atmosphere. After the atmosphere branch, add it as `star_light * transmittance * star_contrast`, where transmittance is `haze.T` (Physical) or `1 - curved_air.a` (Legacy), and `star_contrast` compares the star with the sky behind it: `smoothstep(0.5, 1.5, star_value * star_brightness / max(star_contrast_threshold * sky_luminance, 1e-6))`, with `sky_luminance` the luminance of the in-scatter added in that direction (`haze.S_sun + haze.S_amb` times the interior factor, or `curved_air.rgb`, plus the Legacy `atmospheric_sky` term). Brighter stars pass first; a dark sky passes all.
   Cost: sky pixels only, a luminance dot, a divide and a smoothstep on values `sky()` already computes (well under 0.01 ms; measure once with the GPU `Sky` marker, perf pause permitting). If it costs more than 0.02 ms, or the per-star test shimmers at dusk (escalation below), use the simpler form the user approved instead: one fade for the whole field, `star_contrast = 1 - smoothstep(sky_dark, sky_bright, sky_luminance)`, at a sky brightness around the end of civil twilight (user, 2026-11-15).
2. Order: the giant planet still covers stars (apply the planet mask to `star_light` exactly as it covers `color` today). Clouds and storms composite over the sky already; do not add a separate storm rule.
3. The radiance cubemap pass still gets no stars (`!AT_CUBEMAP_PASS`).
4. In `day_side_atmosphere.gd`, stop the script's day ramp from being what hides the stars: at the surface by night the visibility stays as today (`night_surface_star_visibility`), and by day it must no longer need to be 0 for the sky to show none. Keep `star_visibility` as an overall scale, the eclipse floor, the outside-the-ring term, underwater and underground. Simplify the height ramp only if the shader rule makes it redundant (report the decision). Do not change the eclipse settings.
5. One new tunable in the grave-key menu next to the existing star settings: "Star contrast threshold" (`star_contrast_threshold`), with persistence, sanitize and the settings smoke test, per the `live-tuning-setting` skill. Default from calibration (step 6). No other new menu rows.
6. Calibrate the default: measure the zenith in-scatter luminance at the surface (Physical haze, clear sky) with the sun at 0, -3, -6 and -12 degrees elevation, and at noon. Choose the threshold so that the brightest stars (magnitude 1.0 from `procedural_stars`) just appear overhead at about sun -4 to -6 degrees (end of civil twilight), the median star at about -9 to -12 degrees, and no star at noon or with the sun above the horizon. Write the measured luminances and the chosen value into this section.

DO-NOT-CHANGE CONSTRAINTS
Star positions, density, colour, rotation and night brightness are unchanged (no visible change in a dark night sky beyond noise). The giant planet, sun disc, halo, hub halo and both haze branches are unchanged. No visual boost: realism by default.

VALIDATION STEPS
- Captures at a surface pose (`tools/run_tests.ps1` with the celestial tests, or a small harness case): noon, sun 0, -3, -6, -12 degrees, midnight, eclipse totality; plus noon at 10 km and 30 km above the surface if the harness can place the camera there. Expected: no stars at noon, sun 0 and 10 km noon; brightest stars overhead first at about -4 to -6 degrees and first away from the sun; full field at midnight and totality as today; stars visible at 30 km noon. Send one contact sheet (`tools/media/ffmpeg_tools.ps1 sheet`).
- A `compare` of midnight before and after: noise-level heatmap.
- `tools/run_tests.ps1 -Files <changed files> -Label sky1 -DryRun`, then the run; `celestial_visual_smoke_test`, `celestial_runtime_integration_smoke_test`, `eclipse_darkness_parity_test`, `haze_consistency_gpu_test` and the settings smoke test pass (update a test only where it asserted the old day ramp, and say so).

ESCALATION CONDITIONS
Stars flicker or crawl at dusk as the threshold passes (temporal aliasing of the smoothstep on tiny points), or the sky-view LUT path and the analytic path give a visibly different star fade.

STOP RULE
1 watchdog period, 3 build-plus-test attempts.
