# evren-cli

A minimal agentic coding assistant for your terminal, powered by the EVREN LLM API. It can read and write files, list directories, run shell commands, and iterate on a task until it's done — all from an interactive prompt.

```
EVREN CLI 1.7.0 — agentic file editing over EVREN LLM API
commands: /mode <ask|plan|normal>  /model <name>  /keys [add|rm <no>|reset]  /maxtokens <n>  /maxrounds <n>  /tokens  /clear  /commit  /version  /help  /exit   (recommended for editing: /model glm-5.3)
🪐 evren 📁 ~/source/repos/my-project 🌿 (main)
❯ 
```

The prompt shows the working directory (home shortened to `~`) and the current git branch, each with its own icon and color. The branch part is hidden outside a git repo; on a detached HEAD the short commit hash is shown instead.

## Shallow research, inference checkpoints

Research steps are kept deliberately shallow: one batch means at most 2 exploration tool calls
(one listing/search plus one targeted read). After each batch the agent pauses with a short
inference checkpoint — findings, the inference with its explicit assumptions, and up to 2
clarifying questions — and waits for your answer before researching further or editing.
Ambiguities and unverified assumptions are resolved by asking you, not by deeper digging;
only trivially clear single-step tasks skip the checkpoint.

## Multiple API keys

You can configure several API keys at once; when one runs out of quota the next one takes over automatically (round-robin):

- `~/.evren-cli/config.json` → `"ApiKeys": ["evren_llm_aaa…", "evren_llm_bbb…"]` (the legacy
  single `"ApiKey"` field is migrated automatically),
- `evren-cli -k <key>` — repeatable, or comma-separate several keys in one flag,
- `EVREN_API_KEY` env var — comma separated (wins over config; CLI wins over env).

Failover rules: a key that returns 429/403/402 rests for its `Retry-After` window (min 60 s),
a 401 key is disabled permanently, and the request continues with the next ready key.
Once a response has started streaming it is never replayed on another key. Manage the pool
live with `/keys` (list), `/keys <anahtar[,anahtar]>` (add), `/keys rm <no>` (remove),
`/keys reset` (clear resting/disabled states). Keys are always shown masked
(`evren_llm_abcd…wxyz`).

## Working modes

- `normal` (default) — full editing: `read_file`, `write_file`, `list_files`, `run_command`.
- `ask` — read-only: `write_file` is disabled and `run_command` only accepts read-only
  inspection commands (`ls`, `cat`, `git status`, `git diff`, …). Prefix a prompt with
  `ask: …` or use `/mode ask` / `--mode ask`.
- `plan` — produces a plan only: source files are left untouched and changes are written
  through the `create_plan` tool as `plans/<name>/plan.md`. Prefix with `plan: …` or use
  `/mode plan` / `--mode plan`.