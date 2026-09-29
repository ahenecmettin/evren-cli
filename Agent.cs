// Agent.cs (459 lines)
// Agent.cs (386 lines)
// Agent.cs (337 lines)
// Agent.cs
using System.Diagnostics;
using System.Text;
using evren_cli.Models;
using evren_cli.Tools;

namespace evren_cli;

public sealed class Agent
{
    private const int MaxContinuations = 3;
    private const int DefaultMaxRounds = 100;
    private const int DefaultMaxTokens = 16384;
    private const string Dim = "\u001b[2m";
    private const string Bold = "\u001b[1m";
    private const string Reset = "\u001b[0m";
    private const string Cyan = "\u001b[36m";
    private const string Red = "\u001b[31m";
    private const string Yellow = "\u001b[33m";
    private const string Green = "\u001b[32m";

    // Prompt icons (emoji — konsol yazı tipine bağımlı değildir).
    private const string PromptIcon = "\U0001fa90";   // 🪐 evren
    private const string FolderIcon = "\U0001f4c1";   // 📁 klasör
    private const string BranchIcon = "\U0001f33f";   // 🌿 git dalı
    private const string Caret = "\u276f";            // ❯ giriş imleci

    private const int DefaultContextWindow = 128_000;
    private const int DefaultReserveTokens = 16_384;

    private readonly EvrenClient _client;
    private readonly FileTools _tools;
    private readonly List<ChatMessage> _history = new();
    private readonly TokenManager _tokens;
    private string _model;
    private int _maxTokens;
    private int _maxRounds;

    public Agent(EvrenClient client, FileTools tools, string model, int? maxTokens = null, int? maxRounds = null,
        int? contextWindow = null, int? reserveTokens = null)
    {
        _client = client;
        _tools = tools;
        _model = model;
        _maxTokens = maxTokens is > 0 ? maxTokens.Value : DefaultMaxTokens;
        _maxRounds = maxRounds is > 0 ? maxRounds.Value : DefaultMaxRounds;
        _tokens = new TokenManager(
            contextWindow is > 0 ? contextWindow.Value : DefaultContextWindow,
            reserveTokens is > 0 ? reserveTokens.Value : DefaultReserveTokens);
        ResetHistory();
    }

    /// <summary>Current <c>max_tokens</c> cap — shown in the welcome banner.</summary>
    public int MaxTokens => _maxTokens;

    /// <summary>Current tool-round limit per turn — shown in the welcome banner.</summary>
    public int MaxRounds => _maxRounds;

    /// <summary>Session-wide token usage reported by the server.</summary>
    public TokenManager Tokens => _tokens;

    private void Push(ChatMessage message)
    {
        _history.Add(message);
        _tokens.TrackAdded(message);
    }

    private void ResetHistory()
    {
        _history.Clear();
        _tokens.ResetTracking(_history);
        Push(new ChatMessage { Role = "system", Content = BuildSystemPrompt() });
    }

    private string BuildSystemPrompt() =>
        $"""
        You are EVREN CLI, an expert coding agent working inside the user's terminal.
        Working directory: {_tools.WorkingDirectory}
        Operating system: {(OperatingSystem.IsWindows() ? "Windows (PowerShell)" : "Unix (sh)")}

        You have tools: list_files, read_file, write_file, run_command.
        Rights: list_files, read_file, write_file, run_command.
        Rules:
        - Always read a file with read_file before modifying it; then write the COMPLETE updated file with write_file.
        - Use list_files to discover the project structure when paths are unknown.
        - Use run_command for builds, tests and git. Verify your changes compile when practical.
        - Make minimal, focused changes. Preserve existing code style.
        - Do not ask for confirmation; changes are applied automatically.
        - When finished, briefly summarize what you changed. Keep the summary short.
        - Reply in the same language the user writes in.
        """;

    /// <summary>
    /// Builds the REPL prompt: product icon, the working directory path (home
    /// shortened to <c>~</c>) and the checked-out git branch — each colored and
    /// marked with its own icon:
    /// <c>🪐 evren 📁 ~/repo 🌿 (main) ❯</c>
    /// </summary>
    private string BuildPrompt()
    {
        var prompt = new StringBuilder();

        // 🪐 evren — ürün adı (parlak camgöbeği)
        prompt.Append($"{Cyan}{Bold}{PromptIcon} evren{Reset}");

        // 📁 /path/to/dir — çalışma dizini (sarı), ev dizini ~ ile kısaltılır
        prompt.Append($" {Yellow}{FolderIcon} {ShortenHome(_tools.WorkingDirectory)}{Reset}");

        // 🌿 (branch) — git dalı (yeşil); repo değilse gösterilmez
        var branch = GetGitBranch();
        if (branch is not null)
            prompt.Append($" {Green}{BranchIcon} ({branch}){Reset}");

        // ❯ — giriş imleci (camgöbeği)
        prompt.Append($"\n{Cyan}{Caret}{Reset} ");
        return prompt.ToString();
    }

    /// <summary>Replaces the user-profile prefix of <paramref name="path"/> with <c>~</c>.</summary>
    private static string ShortenHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return path;

        home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (full.Equals(home, StringComparison.OrdinalIgnoreCase))
            return "~";

        if (full.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(home + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "~" + full[home.Length..];

        return full;
    }

    private static string? GetGitBranch()
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                ArgumentList = { "rev-parse", "--abbrev-ref", "HEAD" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);

            if (process.ExitCode != 0 || output.Length == 0)
                return null;

            if (output == "HEAD")
            {
                // Ayrık (detached) HEAD — kısa commit hash'i göster.
                var sha = GetDetachedSha();
                return sha is null ? null : $"@{sha}";
            }

            return output;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Short commit hash for a detached HEAD checkout (or <c>null</c> on failure).</summary>
    private static string? GetDetachedSha()
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                ArgumentList = { "rev-parse", "--short", "HEAD" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);

            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task RunReplAsync(CancellationToken appCt)
    {
        PrintHelp();
        while (!appCt.IsCancellationRequested)
        {
            Console.Write(BuildPrompt());
            var input = Console.ReadLine();
            if (input is null)
                break;

            input = input.Trim();
            if (input.Length == 0)
                continue;

            if (input.StartsWith('/'))
            {
                if (!HandleSlashCommand(input))
                    break;
                continue;
            }

            await RunTurnAsync(input, appCt);
        }
    }

    private bool HandleSlashCommand(string input)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/exit":
            case "/quit":
                return false;
            case "/help":
                PrintHelp();
                return true;
            case "/version":
                Console.WriteLine($"{Dim}{VersionInfo.Product} {VersionInfo.Version}{Reset}");
                return true;
            case "/clear":
                ResetHistory();
                Console.WriteLine($"{Dim}[history cleared]{Reset}");
                return true;
            case "/tokens":
                var used = _tokens.Estimate(_history);
                Console.WriteLine(
                    $"{Dim}[tokens: {_tokens.TotalPromptTokens} prompt + {_tokens.TotalCompletionTokens} completion " +
                    $"across {_tokens.RequestsReported} request(s) | history ~{used}/{_tokens.HistoryBudget}]{Reset}");
                return true;
            case "/model":
                if (parts.Length > 1)
                {
                    _model = parts[1];
                    Console.WriteLine($"{Dim}[model \u2192 {_model}]{Reset}");
                }
                else
                {
                    Console.WriteLine($"{Dim}[model: {_model}]{Reset}");
                }
                return true;
            case "/maxtokens":
                if (parts.Length > 1)
                {
                    if (int.TryParse(parts[1], out var tokens) && tokens > 0)
                    {
                        _maxTokens = tokens;
                        Console.WriteLine($"{Dim}[max_tokens \u2192 {_maxTokens}]{Reset}");
                    }
                    else
                    {
                        Console.WriteLine($"{Red}Usage: /maxtokens <positive integer>{Reset}");
                    }
                }
                else
                {
                    Console.WriteLine($"{Dim}[max_tokens: {_maxTokens}]{Reset}");
                }
                return true;
            case "/maxrounds":
                if (parts.Length > 1)
                {
                    if (int.TryParse(parts[1], out var rounds) && rounds > 0)
                    {
                        _maxRounds = rounds;
                        Console.WriteLine($"{Dim}[max_rounds \u2192 {_maxRounds}]{Reset}");
                    }
                    else
                    {
                        Console.WriteLine($"{Red}Usage: /maxrounds <positive integer>{Reset}");
                    }
                }
                else
                {
                    Console.WriteLine($"{Dim}[max_rounds: {_maxRounds}]{Reset}");
                }
                return true;
            default:
                Console.WriteLine($"{Red}Unknown command: {parts[0]} (try /help){Reset}");
                return true;
        }
    }

    private void PrintHelp()
    {
        Console.WriteLine(
            $"{Dim}commands: /model <name>  /maxtokens <n>  /maxrounds <n>  /tokens  /clear  /version  /help  /exit " +
            $"(recommended for editing: /model glm-5.3){Reset}");
    }

    /// <summary>
    /// Runs one user turn: streams the model response, executes any requested
    /// tools, and keeps looping until the model stops asking for tools (or the
    /// per-turn round limit / continuation limit is reached).
    /// </summary>
    public async Task RunTurnAsync(string input, CancellationToken ct)
    {
        Push(new ChatMessage { Role = "user", Content = input });

        var continuations = 0;

        for (var round = 1; round <= _maxRounds && !ct.IsCancellationRequested; round++)
        {
            // Keep the conversation inside the context budget before every request.
            var compaction = HistoryCompactor.Compact(_history, _tokens);
            if (compaction is not null)
                Console.WriteLine($"{Dim}[context compacted: {compaction}]{Reset}");

            var promptEstimate = _tokens.PromptEstimate(_history, _tools.Definitions);
            var request = new ChatRequest
            {
                Model = _model,
                Messages = _history,
                Tools = _tools.Definitions,
                MaxTokens = _maxTokens
            };

            StreamResult result;
            try
            {
                result = await _client.StreamChatAsync(
                    request,
                    onReasoning: chunk => Console.Write($"{Dim}{chunk}{Reset}"),
                    onContent: Console.Write,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                RollbackLastUserMessage();
                Console.WriteLine($"\n{Yellow}[canceled]{Reset}");
                return;
            }
            catch (EvrenApiException ex)
            {
                RollbackLastUserMessage();
                Console.WriteLine();
                Console.Error.WriteLine($"{Red}API error: {ex.Message}{Reset}");
                if (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    Console.Error.WriteLine($"{Dim}[ipucu: /clear ile gecmisi sifirlayip tekrar deneyin]{Reset}");
                return;
            }
            catch (HttpRequestException ex)
            {
                RollbackLastUserMessage();
                Console.WriteLine();
                Console.Error.WriteLine($"{Red}Network error: {ex.Message}{Reset}");
                return;
            }

            Console.WriteLine();

            // Feed real usage back so the estimator self-calibrates.
            _tokens.RecordUsage(result.Usage, promptEstimate);

            Push(result.Message);

            if (result.Message.ToolCalls is { Count: > 0 } calls)
            {
                for (var c = 0; c < calls.Count; c++)
                {
                    var call = calls[c];
                    var name = call.Function.Name ?? "?";

                    string output;
                    try
                    {
                        Console.WriteLine($"{Dim}\u2022 {name}{Reset}");
                        output = await _tools.ExecuteAsync(name, call.Function.Arguments ?? "{}", ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // The user pressed Ctrl+C mid-tool. Every tool_call in this
                        // assistant message must still get a tool result, otherwise
                        // the history is protocol-invalid and the next request
                        // fails with HTTP 400. Close out the remaining calls and stop.
                        output = "Canceled by user before the tool finished.";
                        Push(new ChatMessage { Role = "tool", ToolCallId = call.Id, Content = output });
                        for (var rest = c + 1; rest < calls.Count; rest++)
                            Push(new ChatMessage
                            {
                                Role = "tool",
                                ToolCallId = calls[rest].Id,
                                Content = "Canceled by user before the tool ran."
                            });

                        Console.WriteLine($"{Yellow}[canceled]{Reset}");
                        return;
                    }

                    Push(new ChatMessage { Role = "tool", ToolCallId = call.Id, Content = output });
                }

                continue;
            }

            if (result.Truncated)
            {
                if (continuations >= MaxContinuations)
                {
                    Console.WriteLine(
                        $"{Yellow}[response hit the max_tokens limit; raise it with /maxtokens]{Reset}");
                    break;
                }

                continuations++;
                Push(new ChatMessage
                {
                    Role = "user",
                    Content = "Your previous message was cut off by the max_tokens limit. " +
                              "Continue exactly where you left off without repeating what you already wrote."
                });
                continue;
            }

            break;
        }

        if (_tokens.RequestsReported > 0)
            Console.WriteLine(
                $"{Dim}[tokens: {_tokens.TotalPromptTokens} prompt + {_tokens.TotalCompletionTokens} completion]{Reset}");
    }

    /// <summary>
    /// Removes the trailing user message when a request failed outright (API or
    /// network error), so the user can simply retry without a dangling turn.
    /// </summary>
    private void RollbackLastUserMessage()
    {
        if (_history.Count > 1 && _history[^1].Role == "user")
        {
            _tokens.TrackRemoved(_history[^1]);
            _history.RemoveAt(_history.Count - 1);
        }
    }
}