using System.Text;
using evren_cli.Models;
using evren_cli.Tools;

namespace evren_cli;

public sealed class Agent
{
    private const int MaxRounds = 20;
    private const string Dim = "\u001b[2m";
    private const string Reset = "\u001b[0m";
    private const string Cyan = "\u001b[36m";
    private const string Red = "\u001b[31m";

    private readonly EvrenClient _client;
    private readonly FileTools _tools;
    private readonly List<ChatMessage> _history = new();
    private string _model;

    public Agent(EvrenClient client, FileTools tools, string model)
    {
        _client = client;
        _tools = tools;
        _model = model;
        ResetHistory();
    }

    private void ResetHistory()
    {
        _history.Clear();
        _history.Add(new ChatMessage { Role = "system", Content = BuildSystemPrompt() });
    }

    private string BuildSystemPrompt() =>
        $"""
        You are EVREN CLI, an expert coding agent working inside the user's terminal.
        Working directory: {_tools.WorkingDirectory}
        Operating system: {(OperatingSystem.IsWindows() ? "Windows (PowerShell)" : "Unix (sh)")}

        You have tools: list_files, read_file, write_file, run_command.
        Rules:
        - Always read a file with read_file before modifying it; then write the COMPLETE updated file with write_file.
        - Use list_files to discover the project structure when paths are unknown.
        - Use run_command for builds, tests and git. Verify your changes compile when practical.
        - Make minimal, focused changes. Preserve existing code style.
        - Do not ask for confirmation; changes are applied automatically.
        - When finished, briefly summarize what you changed.
        - Reply in the same language the user writes in.
        """;

    public async Task RunReplAsync(CancellationToken appCt)
    {
        PrintHelp();
        while (!appCt.IsCancellationRequested)
        {
            Console.Write($"{Cyan}evren>{Reset} ");
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
            case "/clear":
                ResetHistory();
                Console.WriteLine($"{Dim}[history cleared]{Reset}");
                return true;
            case "/model":
                if (parts.Length > 1)
                {
                    _model = parts[1];
                    Console.WriteLine($"{Dim}[model → {_model}]{Reset}");
                }
                else
                {
                    Console.WriteLine($"{Dim}[model: {_model}]{Reset}");
                }
                return true;
            case "/help":
                PrintHelp();
                return true;
            default:
                Console.WriteLine($"{Red}Unknown command: {parts[0]}{Reset}");
                return true;
        }
    }

    private void PrintHelp()
    {
        Console.WriteLine($"{Dim}model: {_model} | cwd: {_tools.WorkingDirectory}");
        Console.WriteLine($"commands: /model <name>  /clear  /help  /exit   (recommended for editing: /model glm-5.3){Reset}");
    }

    public async Task RunTurnAsync(string userInput, CancellationToken appCt)
    {
        _history.Add(new ChatMessage { Role = "user", Content = userInput });

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            turnCts.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            for (var round = 0; round < MaxRounds; round++)
            {
                var request = new ChatRequest
                {
                    Model = _model,
                    Messages = _history,
                    Tools = _tools.Definitions,
                    ToolChoice = "auto",
                    MaxTokens = 4096
                };

                var reasoningOpen = false;
                var assistant = await _client.StreamChatAsync(
                    request,
                    onReasoning: text =>
                    {
                        if (!reasoningOpen) { Console.Write(Dim); reasoningOpen = true; }
                        Console.Write(text);
                    },
                    onContent: text =>
                    {
                        if (reasoningOpen) { Console.Write(Reset); Console.WriteLine(); reasoningOpen = false; }
                        Console.Write(text);
                    },
                    turnCts.Token);

                if (reasoningOpen)
                {
                    Console.Write(Reset);
                    Console.WriteLine();
                }

                _history.Add(assistant);

                if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
                {
                    if (assistant.Content is not null)
                        Console.WriteLine();
                    return;
                }

                if (assistant.Content is not null)
                    Console.WriteLine();

                foreach (var call in assistant.ToolCalls)
                {
                    var name = call.Function.Name ?? "";
                    var args = call.Function.Arguments ?? "{}";
                    Console.WriteLine($"{Dim}⚙ {name} {Abbrev(args, 120)}{Reset}");

                    var result = await _tools.ExecuteAsync(name, args, turnCts.Token);
                    _history.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = call.Id,
                        Name = name,
                        Content = result
                    });
                }
            }

            Console.WriteLine($"{Red}[stopped: reached {MaxRounds} tool rounds]{Reset}");
        }
        catch (OperationCanceledException) when (!appCt.IsCancellationRequested)
        {
            Console.WriteLine($"{Reset}\n{Dim}[interrupted]{Reset}");
            TrimDanglingToolCalls();
        }
        catch (EvrenApiException ex)
        {
            Console.WriteLine($"{Reset}\n{Red}API error: {ex.Message}{Reset}");
            TrimDanglingToolCalls();
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"{Reset}\n{Red}Network error: {ex.Message}{Reset}");
            TrimDanglingToolCalls();
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private void TrimDanglingToolCalls()
    {
        while (_history.Count > 1 && _history[^1].Role is "assistant" or "tool" &&
               (_history[^1].Role == "tool" || _history[^1].ToolCalls is { Count: > 0 }))
        {
            _history.RemoveAt(_history.Count - 1);
        }
    }

    private static string Abbrev(string text, int max)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
