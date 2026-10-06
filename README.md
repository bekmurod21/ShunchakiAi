# Shunchaki AI

An interactive AI coding agent for your terminal, written in C# on **.NET 10** and compiled
with **Native AOT**. It works like Claude Code: you describe a task in natural language, and the
agent reads files, edits code and runs shell commands in your project, asking before anything
that changes your system.

```
› add input validation to UserService and run the tests
✻ Planning: read UserService, find the tests, add guards, run dotnet test.
⏺ read_file Read src/UserService.cs
⏺ edit_file Edit src/UserService.cs
╭─ edit_file wants to run ─╮
Allow? [y/n] (y): y
⏺ run_shell $ dotnet test
  ⎿ Exit code: 0 (6.2s)
```

## Features

- **REPL and single-shot modes.** Run `shunchaki` for a chat session, or `shunchaki "refactor this file"` for one request.
- **Tool calling.** `read_file`, `list_directory`, `write_file`, `edit_file` and `run_shell`.
- **Safety.**
  - File access is confined to the workspace root, including symlinks that point outside it.
  - Writes and shell commands need your approval unless you pass `--yes`.
  - A deny-list blocks catastrophic commands even with `--yes`.
  - Shell commands get a timeout and closed stdin, and the whole process tree is killed when they're cancelled.
- **Bounded memory.** Command output is drained concurrently into a fixed-size head-and-tail buffer, so a command that prints gigabytes never grows the process.
- **Rich terminal UI** built with Spectre.Console:
  - Animated spinners while it waits on the API.
  - Markdown rendering: headings, lists, quotes, inline code and bordered code blocks.
  - Dimmed thinking summaries.
- **Claude and Gemini in one chain.** Anthropic and Google models can be mixed in the failover chain, and work moves between them with the same context.
- **Model failover.** If a model hits its rate limit or token quota, is overloaded or unavailable, can't fit the conversation, or declines a request, the work moves to the next model in the chain and keeps going until the task is finished. See [Model failover and session history](#model-failover-and-session-history).
- **Saved sessions and work log.** The full history and a readable work log are saved after every step, and `shunchaki --resume` continues where you left off.
- **Instant startup.** It ships as a single native binary (about 10 MB, starts in under 10 ms) with no runtime to install.

## Requirements

- An Anthropic API key in `ANTHROPIC_API_KEY`, a Google Gemini API key in `GEMINI_API_KEY` (`GOOGLE_API_KEY` also works), or both.
- To build: the .NET 10 SDK. Native AOT also needs the platform linker: `clang` on Linux, the Xcode command-line tools on macOS, or "Desktop development with C++" on Windows.

## Build

```bash
# Development run
dotnet run --project src/ShunchakiAi -- "explain this project"

# Native AOT binary (pick your runtime identifier)
dotnet publish src/ShunchakiAi -c Release -r linux-x64   -o publish   # or win-x64, osx-arm64, linux-arm64
./publish/shunchaki --help
```

Put `publish/shunchaki` (`shunchaki.exe` on Windows) somewhere on your `PATH`.

## Usage

```bash
export ANTHROPIC_API_KEY=sk-ant-...   # Claude models
export GEMINI_API_KEY=...             # Gemini models (optional; at least one key is required)

shunchaki                                   # interactive session
shunchaki "why does the build fail?"        # single request; exit code 0 on success
git diff | shunchaki "review this change"   # piped stdin is attached to the prompt
shunchaki -C ~/code/app -e xhigh "add tests for the parser"
```

| Option | Description |
| --- | --- |
| `-m, --model <id[,id...]>` | Failover chain; Claude and Gemini models can be mixed. The default is the Claude chain `claude-opus-5-5,claude-sonnet-5-5,claude-haiku-4-5` followed by the Gemini chain, each only if its key is set (or set `SHUNCHAKI_MODELS`). A single model is tried first, with the default chain behind it |
| `--no-failover` | Use only the first model |
| `-r, --resume` | Continue the most recent session in this workspace |
| `-s, --session <id>` | Continue a specific saved session |
| `-e, --effort <level>` | `low`, `medium`, `high` (default), `xhigh` or `max` |
| `-C, --cwd <dir>` | Workspace root (default: current directory) |
| `-y, --yes` | Auto-approve writes and shell commands |
| `-h, --help` / `-v, --version` | Show help or version |

The REPL accepts:

- `/help`
- `/clear`: start a new session
- `/usage`: token totals, per model
- `/models`: the failover chain and each model's state
- `/sessions`: saved sessions in this workspace
- `/resume [id]`: continue a saved session
- `/exit`

End a line with `\` to keep typing on the next line. **Ctrl+C** interrupts the running turn, and
pressing it at the prompt quits.

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `ANTHROPIC_API_KEY` | (one key required) | Anthropic key for Claude models |
| `GEMINI_API_KEY` | (one key required) | Google key for Gemini models (`GOOGLE_API_KEY` also works) |
| `SHUNCHAKI_GEMINI_MODELS` | `gemini-3.1-pro-preview,gemini-3.8-flash` | Gemini part of the default chain |
| `ANTHROPIC_BASE_URL` | `https://api.anthropic.com/` | Endpoint override |
| `GEMINI_BASE_URL` | `https://generativelanguage.googleapis.com/` | Endpoint override |
| `SHUNCHAKI_MAX_WAIT` | `600` | Seconds to keep retrying when every model is unavailable |
| `SHUNCHAKI_MAX_TOKENS` | `16000` | Max output tokens per response |
| `SHUNCHAKI_MAX_CONTINUE` | `5` | Automatic "continue" requests when a reply hits the output limit |
| `SHUNCHAKI_MAX_TOOL_ITERATIONS` | `100` | Tool round-trips allowed per turn |
| `SHUNCHAKI_SHELL_TIMEOUT` | `120` | Default shell timeout in seconds |
| `SHUNCHAKI_FALLBACK` | on | Set to `off` to disable server-side refusal fallback |

## Model failover and session history

The agent is built to finish the task even when a model can't continue.

**When a model fails, the same request goes to the next model in the chain.**

| Failure | What happens |
| --- | --- |
| 429 rate limit or token quota | Model cools down for `retry-after` (or 60 s) |
| 529 overloaded, 5xx, network error | Model cools down for 20 to 30 s |
| Model not found or not enabled, context window too small | Model is excluded for this conversation |
| Model declined the request (after Anthropic's server-side fallback) | Model is skipped for this turn |
| Out of credit, invalid API key | Every model of that provider is excluded, and the work continues on the other provider. If no models remain, the work stops with a clear message |

Each request goes to the first ready model, so the agent returns to your preferred model once it recovers. If every model is cooling down, it waits for the soonest one, giving up after `SHUNCHAKI_MAX_WAIT`. When a reply is cut off by the output limit, the agent sends "continue" automatically.

**The context stays the same when the model changes:**

- Every model gets the same system prompt, tool list and append-only history, including the previous model's thinking blocks. A model that can't read them ignores them, and nothing is stripped or rewritten.
- The new model also gets a `<handoff>` note on the pending message. It names the model it's taking over from and why, and contains a work-log summary: the current request, the progress notes, and the latest actions with their results.
- The model calls the `record_progress` tool after significant steps ("done: ..., next: ..."). These notes are what the next model uses to continue the work.

**Switching between Claude and Gemini.** The history is stored in one canonical format (Messages API), and every provider translates it on each request.

- Gemini receives the same system prompt, tools and turns as `contents`. Tool calls and results become `functionCall` and `functionResponse` parts, and Claude's thinking blocks are left out.
- Gemini 3 needs a thought signature on every tool call. Gemini's own signatures are stored on the canonical blocks in `_`-prefixed fields and returned to it unchanged. Tool calls made by Claude get Google's documented placeholder, `skip_thought_signature_validator`.
- Gemini replies are converted back into canonical text and `tool_use` blocks. The `_` fields are stripped before anything is sent to Anthropic, so Claude sees a valid history whichever model produced a turn.

**Everything is saved to `.shunchaki/sessions/<id>/` in the workspace after every step.**

- `session.json` holds the complete message history, the exact system prompt and the work log.
- `worklog.md` is a readable log of requests, actions (✅/❌), progress notes, model switches (🔁), problems and answers.

`.shunchaki/` contains its own `.gitignore`, so session data is never committed.

After an interruption, crash or outage, run `shunchaki --resume` or use `/resume` in the REPL. The stored system prompt is reused verbatim, so the resumed conversation has exactly the same context as before.

## Architecture

```
src/ShunchakiAi/
├── Program.cs              Top-level entry: args → config → wiring → single-shot or REPL
├── Repl.cs                 Read-Eval-Print Loop and slash commands
├── TurnCancellation.cs     Ctrl+C cancels the current turn, not the process
├── StandardInput.cs        Piped-stdin reader that never hangs on an idle pipe
├── Configuration/          CLI parsing, environment configuration, model capabilities
├── Api/                    IModelProvider, AnthropicClient, GeminiClient (history translation), HttpSender (retries)
├── Agent/                  Agent loop, conversation, ModelRouter (failover chain), FailoverPolicy
├── Sessions/               SessionStore (session.json + worklog.md), WorkLog
├── Tools/                  ITool, file/shell/record_progress tools, ToolRegistry
├── Execution/              FileSystemService (sandboxed IO), ShellExecutor, BoundedTextBuffer
└── Ui/                     Spectre.Console renderer and Markdown renderer
```

- **Layers.** The agent loop (`Agent/`) talks to the UI only through `IAgentView`. The API layer (`Api/`) knows nothing about tools or the console. System access lives only in `Execution/`.
- **AOT-safe JSON.** No reflection-based serialization is used. Requests are streamed through `Utf8JsonWriter`, and responses are handled as `JsonNode`. The build has `TreatWarningsAsErrors` on, so any trim or AOT warning fails the build.
- **Conversation history.** It's append-only, and assistant content is stored exactly as received, so thinking blocks go back to the API unchanged. Prompt caching (`cache_control`) re-reads the stable prefix on every loop iteration.
- **Error handling.**
  - Tool failures go back to the model as `is_error` tool results so it can recover.
  - API errors (429, 5xx, timeouts) are retried with backoff that honors `retry-after`.
  - Cancellation or a failed request leaves the history valid for the next turn.
