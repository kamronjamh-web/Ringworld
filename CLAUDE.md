@AGENTS.md

## Claude Code notes

- Subagents for each role live in `.claude/agents/` (`implementer`, `claude-lead`, `claude-deep`). Spawn them with the Agent tool by name.
- Do not fall back to the built-in `general-purpose` agent for delegated implementation; use `implementer` so the model and effort match the role table.
