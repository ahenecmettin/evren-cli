// README.md (10 lines)
// README.md (140 lines)
# evren-cli

A minimal agentic coding assistant for your terminal, powered by the EVREN LLM API. It can read and write files, list directories, run shell commands, and iterate on a task until it's done — all from an interactive prompt.

```
EVREN CLI 1.3.0 — agentic file editing over EVREN LLM API
commands: /model <name>  /maxtokens <n>  /maxrounds <n>  /tokens  /clear  /version  /help  /exit   (recommended for editing: /model glm-5.3)
🪐 evren 📁 ~/source/repos/my-project 🌿 (main)
❯ 
```

The prompt shows the working directory (home shortened to `~`) and the current git branch, each with its own icon and color. The branch part is hidden outside a git repo; on a detached HEAD the short commit hash is shown instead.
