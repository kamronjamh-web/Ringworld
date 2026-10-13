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

## Feature conventions

For new gameplay or rendering features, expose relevant configurable settings in the grave-key live tuning menu. Extend the existing menu and persistence conventions as part of the feature.
