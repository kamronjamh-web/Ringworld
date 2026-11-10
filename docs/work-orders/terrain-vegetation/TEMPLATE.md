# Work order template (Codex)

Every phase file `T<n>.md` in this folder uses this structure. These work orders are executed by Codex. The roles are defined in `.codex/agents/` and the role table in `AGENTS.md`:

| Role | Model and effort | Use for |
|---|---|---|
| `sol_implementer` | gpt-6.1-sol @ medium | Bounded, well-specified changes. This is the default. |
| `sol_lead` | gpt-6.1-sol @ high | Design, investigation, and reviewing implementer output. |
| `sol_deep` | gpt-6.1-sol @ max | Only for problems that are unusually hard or still unresolved. |

## Phase file layout

```
# T<n>. <phase name>: work orders

Parent: docs/terrain-vegetation-revamp-plan.md section 11 (T<n>). Prerequisite reading: <list>.
Status: not started | in progress | done (date)
Depends on: <phases / WOs>
Freeze: <generation step name, or "none">

## Phase facts          (verified file:line facts the WOs rely on; mark anything unverified)
## Order and parallelism (a small table: WO, role, depends on, can run alongside)
## Work orders          (each WO below)
## Phase acceptance     (what the primary checks before the phase counts as done, incl. the freeze checklist if any)
```

## Work order layout

```
### T<n>-<k>. <short title>

ROLE: sol_implementer | sol_lead (design) | sol_deep
SIZE: S (< half day) | M (about a day) | L (split it if you can)
DEPENDS ON: <WO ids>

OBJECTIVE
One or two sentences: what exists afterwards that did not exist before.

CONTEXT / ROOT CAUSE
Why this is needed. Cite file:line and the plan or spec section.

FILES/SYMBOLS
The files to create or touch, and the main symbols involved.

REQUIRED CHANGES
A numbered, concrete list. Name the settings keys, the recipe fields and the shader globals.

DO-NOT-CHANGE CONSTRAINTS
Generation 1 worlds must stay identical: world_generation_fingerprint_test must pass unchanged. Also list the
files, behaviour and goldens that must not move.

VALIDATION STEPS
The exact commands, using tools/run_tests.ps1 -Tests ... or -Files ... (with -DryRun first), and the
new tests with their manifest entries (tags, mode, tier, marker, expected_s, timeout_s). Include a check
that the test can fail. For visual changes, say which captures the agent sends the user in chat.

ESCALATION CONDITIONS
When to stop and report instead of improvising.
```

## Rules for every WO

- **New worlds only.** Visible behaviour sits behind the world generation, using the feature mask from T1. Generation 1, and every frozen generation, stays byte-identical. The exception is the grass Cards default, which already landed.
- **Grave-key menu.** Only genuine tunables go in the menu (AGENTS.md "Feature conventions"); use the live-tuning-setting conventions. No per-subsystem toggles. New surfaces automatically receive haze, shadows and lighting.
- **Tests.** Every new test gets a `tools/test_manifest.json` entry. Insert it with a small targeted edit, because other agents edit that file in parallel.
- **Running tests.** Use `-Tests` when the manifest is among the changed files, because `-Files` then pulls in the smoke set. GPU runs go through the queue, and no agent ever kills another agent's Godot.
- **Cleanup.** Follow AGENTS.md for ffmpeg and output cleanup, and run `tools/prune_test_output.ps1 -Apply` at the end.
- **Commits.** Never commit. The primary agent commits at the phase's commit point, after running `-Suite gate`.
- **Pictures for the user.** Every visual result goes to the user as pictures in chat (a few key images with captions).
- **Size.** A WO should be completable and verifiable on its own. Prefer more small WOs over one large one.
- **Designs.** Where the plan leaves a design open, the first WO in the phase is a `sol_lead` design WO. It produces a short design section in the phase file, and later WOs reference it. Do not invent the design inside implementer WOs.
