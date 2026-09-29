# evren-cli

A minimal agentic coding assistant for your terminal, powered by the EVREN LLM API. It can read and write files, list directories, run shell commands, and iterate on a task until it's done — all from an interactive prompt.

```
EVREN CLI 1.4.0 — agentic file editing over EVREN LLM API
commands: /mode <ask|plan|normal>  /model <name>  /maxtokens <n>  /maxrounds <n>  /tokens  /clear  /version  /help  /exit   (recommended for editing: /model glm-5.3)
🪐 evren 📁 ~/source/repos/my-project 🌿 (main)
❯ 
```

The prompt shows the working directory (home shortened to `~`) and the current git branch, each with its own icon and color. The branch part is hidden outside a git repo; on a detached HEAD the short commit hash is shown instead.

## Working modes

- `normal` (default) — full editing: `read_file`, `write_file`, `list_files`, `run_command`.
- `ask` — read-only: `write_file` is disabled and `run_command` only accepts read-only
  inspection commands (`ls`, `cat`, `git status`, `git diff`, …). Prefix a prompt with
  `ask: …` or use `/mode ask` / `--mode ask`.
- `plan` — produces a plan only: source files are left untouched and changes are written
  through the `create_plan` tool as `plans/<name>/plan.md`. Prefix with `plan: …` or use
  `/mode plan` / `--mode plan`.
