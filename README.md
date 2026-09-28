# evren-cli

A minimal agentic coding assistant for your terminal, powered by the EVREN LLM API. It can read and write files, list directories, run shell commands, and iterate on a task until it's done — all from an interactive prompt.

```
EVREN CLI — agentic file editing over EVREN LLM API
model: auto | cwd: C:\work\my-project
commands: /model <name>  /clear  /help  /exit   (recommended for editing: /model glm-5.3)
evren> refactor the config loader to lazy-initialize
⚙ read_file Config.cs
⚙ write_file Config.cs
Done. The loader now caches the config...
```

## Features

- **Agentic tool loop** — the model plans, calls tools, reads the results, and keeps working until the task is finished (bounded at 20 tool rounds per turn).
- **File tools** — `read_file`, `write_file`, `list_files`, all sandboxed to a working directory (paths cannot escape it).
- **Shell tool** — `run_command` executes PowerShell (Windows) or `sh` (Unix) inside the working directory. Ideal for builds, tests and git.
- **Streaming output** — token-by-token streaming with separate reasoning/answer channels when the model provides them.
- **Context preservation** — full conversation history is kept so follow-up prompts can reference earlier work. `/clear` resets it.
- **Safe interruption** — `Ctrl+C` cancels the in-flight turn and trims dangling tool calls so the conversation stays valid.
- **NativeAOT** — the project publishes as a self-contained single binary with fast startup and no runtime dependency.

## Requirements

- .NET SDK 10.0 or later
- An EVREN LLM API key (`evren_llm_...`). Create one in the portal under **Modeller ve API > API Anahtarları**.

## Install & build

```powershell
git clone https://github.com/ahenecmettin/evren-cli.git
cd evren-cli

# run straight from source
dotnet run -- "help me clean up this repo"

# or publish a self-contained AOT binary
dotnet publish -c Release
```

## Configuration

evren-cli resolves its settings in this order (later wins):

1. **Config file** — created automatically at `~/.evren-cli/config.json` on first run:

   ```json
   {
     "ApiKey": "",
     "Model": "auto",
     "BaseUrl": "https://evren-llmapi.ssyz.org.tr/v1"
   }
   ```

2. **`EVREN_API_KEY` environment variable** — overrides the stored key.
3. **`--key` / `--model` flags** — override everything for that run.

> The API key is **never** committed to this repository. Keep it in the config file or in `EVREN_API_KEY` only.

## Usage

```text
evren-cli [options] [prompt...]

Options:
  -m, --model <name>   Model id (default from config, e.g. auto, glm-5.3)
  -k, --key <key>      API key override
  -C, --cwd <dir>      Working directory (default: current)
      --once           Run the given prompt once and exit (no REPL)
  -h, --help           Show this help
```

### Examples

```powershell
# start an interactive REPL in the current directory
evren-cli

# run a single task and exit
evren-cli --once "add a --verbose flag to Program.cs"

# work against a different project
evren-cli -C ..\other-project "fix the failing tests"

# one-off model / key override
evren-cli -m glm-5.3 -k evren_llm_xxx "refactor this class"
```

### REPL commands

| Command | Description |
| --- | --- |
| `/model <name>` | Switch the model for subsequent turns. `glm-5.3` is recommended for editing tasks. |
| `/clear` | Clear the conversation history and start a fresh context. |
| `/help` | Show the command list and current model/cwd. |
| `/exit`, `/quit` | Leave the REPL. |

## Tools

The model has access to four tools inside the chosen working directory:

| Tool | Purpose |
| --- | --- |
| `read_file` | Read a file's full text (truncated at 200 KB). Required before editing. |
| `write_file` | Create or overwrite a file with full content. |
| `list_files` | Recursively list files matching a glob (`bin`, `obj`, `.git`, `node_modules` are skipped). |
| `run_command` | Run a shell command with a 60s timeout and return its output. |

Every tool call is printed to the console (`⚙ read_file Config.cs`), so you always see what the agent is doing.

## Architecture

| File | Responsibility |
| --- | --- |
| `Program.cs` | CLI argument parsing, config bootstrap, wiring. |
| `Agent.cs` | Conversation state, tool loop, REPL and slash commands. |
| `EvrenClient.cs` | HTTP client: terms acceptance, streaming chat completions, tool-call delta assembly. |
| `Config.cs` | Load/save `~/.evren-cli/config.json`, env var and CLI overrides. |
| `Tools/FileTools.cs` | Tool implementations and JSON schemas. |
| `Models/` | DTOs and the System.Text.Json source-generation context (AOT-safe). |

## Notes & limits

- The agent stops after 20 tool rounds per turn and reports it, so runaway loops can't spin forever.
- Shell commands time out after 60 seconds and are killed automatically.
- File and command access is confined to the working directory; attempts to escape it are rejected.

## License

See the repository for license information.