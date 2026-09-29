// Tools/FileTools.cs (215 lines)
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using evren_cli.Models;

namespace evren_cli.Tools;

public sealed class FileTools
{
    private const int MaxReadBytes = 200 * 1024;
    private const int MaxOutputChars = 20_000;
    private const int MaxListEntries = 500;
    private static readonly string[] SkipDirs = ["bin", "obj", ".git", "node_modules", ".vs"];

    private readonly string _root;

    public FileTools(string workingDirectory)
    {
        _root = Path.GetFullPath(workingDirectory);
    }

    public string WorkingDirectory => _root;

    public List<ToolDefinition> Definitions =>
    [
        Tool("read_file", "Read the text content of a file. Always read a file before editing it. " +
             "Use 'offset' (1-based start line) and 'limit' (line count) to read a targeted range of a large file instead of the whole thing.",
            """{"type":"object","properties":{"path":{"type":"string","description":"File path relative to the working directory"},"offset":{"type":"integer","description":"1-based line number to start reading from (default: 1)"},"limit":{"type":"integer","description":"Maximum number of lines to return"}},"required":["path"]}"""),
        Tool("write_file", "Create or overwrite a file with the given full content. Parent directories are created automatically.",
            """{"type":"object","properties":{"path":{"type":"string","description":"File path relative to the working directory"},"content":{"type":"string","description":"Complete new file content"}},"required":["path","content"]}"""),
        Tool("list_files", "Recursively list files under a directory (bin/obj/.git/node_modules are skipped).",
            """{"type":"object","properties":{"path":{"type":"string","description":"Directory relative to the working directory (default: '.')"},"pattern":{"type":"string","description":"Glob pattern such as *.cs (default: *)"}},"required":[]}"""),
        Tool("run_command", "Run a shell command in the working directory (PowerShell on Windows, sh otherwise) and return its output. Use for builds, tests, git, etc.",
            """{"type":"object","properties":{"command":{"type":"string","description":"The command line to execute"}},"required":["command"]}""")
    ];

    private static ToolDefinition Tool(string name, string description, string schema) => new()
    {
        Function = new FunctionDefinition
        {
            Name = name,
            Description = description,
            Parameters = JsonDocument.Parse(schema).RootElement.Clone()
        }
    };

    public async Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct)
    {
        try
        {
            return name switch
            {
                "read_file" => ReadFile(Deserialize(argumentsJson, EvrenJsonContext.Default.ReadFileArgs)),
                "write_file" => WriteFile(Deserialize(argumentsJson, EvrenJsonContext.Default.WriteFileArgs)),
                "list_files" => ListFiles(Deserialize(argumentsJson, EvrenJsonContext.Default.ListFilesArgs)),
                "run_command" => await RunCommandAsync(Deserialize(argumentsJson, EvrenJsonContext.Default.RunCommandArgs), ct),
                _ => $"Error: unknown tool '{name}'"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static T Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : new()
    {
        if (string.IsNullOrWhiteSpace(json))
            return new T();
        return JsonSerializer.Deserialize(json, typeInfo) ?? new T();
    }

    private string Resolve(string? relative)
    {
        var combined = Path.GetFullPath(Path.Combine(_root, string.IsNullOrWhiteSpace(relative) ? "." : relative));
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!combined.Equals(_root, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path '{relative}' escapes the working directory.");
        return combined;
    }

    private string ReadFile(ReadFileArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return "Error: 'path' is required.";

        var full = Resolve(args.Path);
        if (!File.Exists(full))
            return $"Error: file not found: {args.Path}";

        var info = new FileInfo(full);
        var text = File.ReadAllText(full);

        // Targeted range: 'offset' is the 1-based first line, 'limit' the max line count.
        // When either is set, only the requested slice is returned (and the byte cap
        // applies to the slice, not the whole file).
        if (args.Offset is > 0 || args.Limit is > 0)
        {
            var lines = text.Split('\n');
            var totalLines = lines.Length;

            var start = args.Offset is > 0 ? args.Offset.Value : 1;
            if (start > totalLines)
                return $"Error: offset {start} is beyond the end of the file ({totalLines} lines).";

            var count = args.Limit is > 0 ? args.Limit.Value : totalLines - start + 1;
            var end = Math.Min(start + count - 1, totalLines);
            var slice = string.Join('\n', lines[(start - 1)..end]);

            if (slice.Length > MaxReadBytes)
                slice = slice[..MaxReadBytes] + "\n... (truncated)";

            return $"// {args.Path} (lines {start}-{end} of {totalLines})\n{slice}";
        }

        var truncated = false;
        if (info.Length > MaxReadBytes)
        {
            text = text[..Math.Min(text.Length, MaxReadBytes)];
            truncated = true;
        }

        var lines2 = text.Split('\n').Length;
        var header = $"// {args.Path} ({lines2} lines{(truncated ? ", TRUNCATED" : "")})\n";
        return header + text;
    }

    private string WriteFile(WriteFileArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return "Error: 'path' is required.";

        var full = Resolve(args.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var content = args.Content ?? "";
        File.WriteAllText(full, content, new UTF8Encoding(false));

        var lines = content.Length == 0 ? 0 : content.Split('\n').Length;
        Console.WriteLine($"\u001b[32m✎ wrote {args.Path} ({lines} lines)\u001b[0m");
        return $"Wrote {args.Path} ({lines} lines).";
    }

    private string ListFiles(ListFilesArgs args)
    {
        var dir = Resolve(args.Path);
        if (!Directory.Exists(dir))
            return $"Error: directory not found: {args.Path ?? "."}";

        var pattern = string.IsNullOrWhiteSpace(args.Pattern) ? "*" : args.Pattern;
        var sb = new StringBuilder();
        var count = 0;
        var truncated = false;

        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0 && !truncated)
        {
            var current = stack.Pop();
            foreach (var file in Directory.EnumerateFiles(current, pattern))
            {
                if (count >= MaxListEntries) { truncated = true; break; }
                sb.AppendLine(Path.GetRelativePath(_root, file).Replace('\\', '/'));
                count++;
            }

            foreach (var sub in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(sub);
                if (SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;
                stack.Push(sub);
            }
        }

        if (count == 0)
            return "(no files)";
        if (truncated)
            sb.AppendLine($"... (truncated at {MaxListEntries} entries)");
        return sb.ToString();
    }

    private async Task<string> RunCommandAsync(RunCommandArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Command))
            return "Error: 'command' is required.";

        Console.WriteLine($"\u001b[33m$ {args.Command}\u001b[0m");

        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", args.Command }
            }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", args.Command } };

        psi.WorkingDirectory = _root;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            return "Error: command timed out after 60s.";
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var sb = new StringBuilder();
        sb.AppendLine($"exit code: {process.ExitCode}");
        if (stdout.Length > 0) sb.AppendLine("stdout:").AppendLine(stdout.TrimEnd());
        if (stderr.Length > 0) sb.AppendLine("stderr:").AppendLine(stderr.TrimEnd());

        var result = sb.ToString();
        if (result.Length > MaxOutputChars)
            result = result[..MaxOutputChars] + "\n... (output truncated)";
        return result;
    }
}
