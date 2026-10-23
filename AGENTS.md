If a request lacks key details, do not guess, Ask up to 3 clarifying questions.

## Agent roles

This file is shared by Codex (OpenAI) and Claude Code (Anthropic). Claude Code loads it through `CLAUDE.md`. Roles are defined once here; each runtime maps them to its own models:

| Role | Purpose | Codex (`.codex/`) | Claude Code (`.claude/agents/`) |
|---|---|---|---|
| Primary | Lead planner, reviewer, final acceptance | `gpt-6.1-sol` @ max | session model (Opus 5.5) |
| Implementer | Bounded, well-specified code changes | `sol_implementer` — `gpt-6.1-sol` @ medium | `implementer` — Sonnet 5.5 @ medium |
| Lead | Investigation, root-causing, fix design, reviewing implementer output | `sol_lead` — `gpt-6.1-sol` @ high | `claude-lead` — Opus 5.5 @ high |
| Deep | Exceptionally hard or unresolved problems only | `sol_deep` — `gpt-6.1-sol` @ max | `claude-deep` — Opus 5.5 @ max |

Keep the two agent sets in sync: when a role's instructions change, update both `.codex/agents/*.toml` and `.claude/agents/*.md`.

## Default workflow

Until the user revokes this instruction, the primary agent acts as lead planner and reviewer. Delegate nontrivial implementation work to the Implementer role for your runtime (see table). Use the Lead role first when the correct change is not already obvious, and escalate to the Deep role only when the problem is unusually difficult, ambiguous, or unresolved. The primary agent must personally review all edits and run relevant tests before reporting completion. If your runtime's mapped model for a role is unavailable, report that limitation instead of silently substituting a different model.

## Tools

- **ffmpeg** (installed via `winget install Gyan.FFmpeg`) for reviewing visual behaviour that frame-time and state checks cannot see: transitions, crossfades, pops, flashes, black frames, HUD flicker. Use `tools/media/ffmpeg_tools.ps1`, which finds ffmpeg even in shells opened before the install:
  - Record a run with Godot's Movie Maker: `--write-movie .godot/captures/<label>/frame.png --fixed-fps 30` (writes `frame00000000.png`, ...).
  - `sheet -Path <video or frame.png>`: evenly spaced frames tiled into one PNG with frame numbers; read it as an image to review a transition.
  - `compare -Path a -Reference b`: SSIM/PSNR plus an amplified difference heatmap; use for before/after "no visible change" claims instead of eyeballing.
  - `flicker -Path ...`: per-frame luma change with the largest spikes listed; use to catch single-frame pops and flashes.
  - `mp4 -Path ...`: an H.264 video for the user to watch.
- Keep captures under `.godot/` or the scratchpad, never in tracked folders. Cloud noise alone gives SSIM around 0.997 between identical scenes, so judge the heatmap, not just the number.

## Feature conventions

For new gameplay or rendering features, expose relevant configurable settings in the grave-key live tuning menu. Extend the existing menu and persistence conventions as part of the feature.

Only expose a value in the grave-key menu when it is a genuine tunable someone would need to adjust. Do not add per-subsystem toggles for behaviour that should simply follow an existing system.
- Good: haze as a whole (density, falloff, color).
- Bad: separate "POI haze" or "POI shadows" toggles. POIs must automatically receive regular haze, shadows and lighting like every other world surface.
- Debug or A/B switches belong in debug tooling, not the menu.
