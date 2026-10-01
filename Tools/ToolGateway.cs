// Tools/ToolGateway.cs
using System.Text;
using System.Text.Json;
using evren_cli.Models;

namespace evren_cli.Tools;

/// <summary>
/// Mod duvarlı araç kapısı: <see cref="FileTools"/>'a ek olarak kip (mode)
/// kısıtlarını uygular ve plan kipine özel <c>create_plan</c> aracını sunar.
/// <list type="bullet">
/// <item><c>ask</c>: yalnızca okuma araçları çalışır (write_file kapalı, run_command salt okuma).</item>
/// <item><c>plan</c>: kaynak dosyalara dokunulmaz; plan yalnızca plans/&lt;ad&gt;/plan.md altına yazılır.</item>
/// <item><c>normal</c>: tüm araçlar serbest.</item>
/// </list>
/// </summary>
public sealed class ToolGateway
{
    private const int MaxOutputChars = 20_000;
    private const string PlansDir = "plans";
    private const string PlanFileName = "plan.md";

    // Salt okuma sayılabilecek kabuk komutları (ask/plan kiplerinde izinli).
    private static readonly HashSet<string> ReadVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "dir", "gci", "get-childitem", "gi", "get-item",
        "cat", "type", "gc", "get-content",
        "select-string", "sls", "grep", "egrep", "rg", "find", "findstr",
        "head", "tail", "more", "less", "wc", "tree", "stat", "file", "pwd",
        "test-path", "resolve-path"
    };

    // Salt okuma git alt komutları.
    private static readonly HashSet<string> GitReadVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "show", "branch", "tag", "remote", "blame",
        "ls-files", "ls-tree", "rev-parse", "describe", "shortlog", "reflog"
    };

    private static readonly char[] SegmentSeparators = [';', '|', '&'];

    private readonly FileTools _files;
    private AgentMode _mode = AgentMode.Normal;

    public ToolGateway(FileTools files) => _files = files;

    public string WorkingDirectory => _files.WorkingDirectory;

    public AgentMode Mode => _mode;

    public void SetMode(AgentMode mode) => _mode = mode;

    /// <summary>
    /// <c>ask_user</c> aracının gerçek yürütücüsü (konsol G/Ç Agent'ta kalır).
    /// Atanmazsa araç hata metni döner.
    /// </summary>
    public Func<AskUserArgs, CancellationToken, Task<string>>? OnAskUser { get; set; }

    /// <summary>Aktif kipe göre izin verilen araç tanımları (ask_user her kipte dahil).</summary>
    public List<ToolDefinition> Definitions
    {
        get
        {
            var defs = _files.Definitions
                .Where(d => IsToolAllowed(d.Function.Name))
                .ToList();

            if (_mode == AgentMode.Plan)
                defs.Add(CreatePlanTool());

            defs.Add(AskUserTool());
            return defs;
        }
    }

    /// <summary>Zorunlu kontrol noktası: yalnızca <c>ask_user</c> sunulur.</summary>
    public List<ToolDefinition> CheckpointDefinitions => [AskUserTool()];

    /// <summary>
    /// Çağrı araştırma bütçesine sayılır mı? read_file / list_files ve salt okuma
    /// run_command sayılır; ask_user, write_file, create_plan ve değiştiren komutlar sayılmaz.
    /// </summary>
    public bool IsReadOnlyCall(string name, string argumentsJson)
    {
        switch (name)
        {
            case "read_file":
            case "list_files":
                return true;
            case "run_command":
                var args = Deserialize(argumentsJson, EvrenJsonContext.Default.RunCommandArgs);
                return IsReadOnlyCommand(args.Command, out _);
            default:
                return false;
        }
    }

    /// <summary>Araç çağrısı; kip ihlallerinde hata metni döner.</summary>
    public async Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct)
    {
        if (name == "ask_user")
        {
            if (OnAskUser is null)
                return "Error: ask_user is not available in this session.";
            var ask = Deserialize(argumentsJson, EvrenJsonContext.Default.AskUserArgs);
            if (string.IsNullOrWhiteSpace(ask.Question))
                return "Error: ask_user requires a non-empty 'question'.";
            return await OnAskUser(ask, ct);
        }

        if (name == "create_plan")
        {
            if (_mode != AgentMode.Plan)
                return Blocked("create_plan", "switch to plan mode first (plan: … or /mode plan)");
            return CreatePlan(Deserialize(argumentsJson, EvrenJsonContext.Default.CreatePlanArgs));
        }

        if (!IsToolAllowed(name))
            return Blocked(name, null);

        if (name == "write_file" && _mode != AgentMode.Normal)
            return Blocked("write_file", "switch to normal mode first (/mode normal)");

        if (name == "run_command" && _mode != AgentMode.Normal)
        {
            var args = Deserialize(argumentsJson, EvrenJsonContext.Default.RunCommandArgs);
            if (!IsReadOnlyCommand(args.Command, out var why))
                return $"Error: command rejected in {ModeInfo.Tag(_mode)} mode — {why}. " +
                       "Only read-only inspection commands are allowed; use /mode normal for changes.";
        }

        return await _files.ExecuteAsync(name, argumentsJson, ct);
    }

    private bool IsToolAllowed(string tool) => _mode switch
    {
        AgentMode.Ask => tool is "read_file" or "list_files" or "run_command",
        AgentMode.Plan => tool is "read_file" or "list_files" or "run_command" or "create_plan",
        _ => tool is "read_file" or "write_file" or "list_files" or "run_command"
    };

    private string Blocked(string tool, string? hint)
    {
        var suffix = hint is null ? "" : $" {hint}.";
        return $"Error: tool '{tool}' is not allowed in {ModeInfo.Tag(_mode)} mode.{suffix}";
    }

    /// <summary>
    /// Salt okuma denetimi: komutun her parçası izinli bir okuma fiiliyle başlamalı,
    /// yönlendirme (<c>&gt;</c>) içermemeli ve <c>git</c> için alt komut salt okuma olmalı.
    /// </summary>
    private static bool IsReadOnlyCommand(string? command, out string reason)
    {
        reason = "";

        if (string.IsNullOrWhiteSpace(command))
        {
            reason = "empty command";
            return false;
        }

        if (command.Contains('>'))
        {
            reason = "output redirection is not read-only";
            return false;
        }

        foreach (var raw in command.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Trim().Trim('"');
            if (segment.Length == 0)
                continue;

            var words = segment.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var verb = words[0].Trim('"');

            if (verb.Equals("git", StringComparison.OrdinalIgnoreCase))
            {
                if (words.Length < 2 || !GitReadVerbs.Contains(words[1].Trim('"')))
                {
                    reason = $"'git {(words.Length < 2 ? "" : words[1])}' is not a read-only git command";
                    return false;
                }
                continue;
            }

            if (!ReadVerbs.Contains(verb))
            {
                reason = $"'{verb}' is not an approved read-only command";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Planı <c>plans/&lt;ad&gt;/plan.md</c> altına yazar; klasör adı adlandırılamazsa
    /// zaman damgalı bir ad üretilir, çakışma halinde <c>-2</c>, <c>-3</c>… eklenir.
    /// </summary>
    private string CreatePlan(CreatePlanArgs args)
    {
        var slug = Slugify(args.Name);
        if (slug.Length == 0)
            slug = "plan-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");

        var plansRoot = Path.Combine(_files.WorkingDirectory, PlansDir);
        var dir = Path.Combine(plansRoot, slug);
        var suffix = 2;
        while (File.Exists(Path.Combine(dir, PlanFileName)))
            dir = Path.Combine(plansRoot, $"{slug}-{suffix++}");

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PlanFileName), (args.Content ?? "").TrimEnd() + "\n", new UTF8Encoding(false));

        var relative = Path.GetRelativePath(_files.WorkingDirectory, dir).Replace('\\', '/');
        var lines = File.ReadAllText(Path.Combine(dir, PlanFileName)).Split('\n').Length;
        Console.WriteLine($"\u001b[32m✎ wrote {relative}/{PlanFileName} ({lines} lines)\u001b[0m");
        return $"Created plan: {relative}/{PlanFileName}";
    }

    /// <summary>Metni URL-safe bir klasör adı haline getirir.</summary>
    private static string Slugify(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(c);
            else if (c == ' ' || c == '/' || c == '\\')
                sb.Append('-');
            else if (c == '.' || c == ',')
                sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>Yardımcı: JSON deserialization.</summary>
    private static T Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : new()
    {
        if (string.IsNullOrWhiteSpace(json))
            return new T();
        return JsonSerializer.Deserialize(json, typeInfo) ?? new T();
    }

    /// <summary>Plan araç tanımlaması.</summary>
    private static ToolDefinition CreatePlanTool() => new()
    {
        Function = new FunctionDefinition
        {
            Name = "create_plan",
            Description = "Create a plan file in a new or existing 'plans/<name>' directory. The plan is saved as 'plan.md'.",
            Parameters = JsonDocument.Parse("{" +
                "\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Plan name (used as directory name)\"},\"content\":{\"type\":\"string\",\"description\":\"The plan text to save\"}},\"required\":[\"name\"]" +
                "}")
                .RootElement.Clone()
        }
    };

    /// <summary>ask_user araç tanımlaması — modelin tur ortasında kullanıcıya soru sorması için.</summary>
    private static ToolDefinition AskUserTool() => new()
    {
        Function = new FunctionDefinition
        {
            Name = "ask_user",
            Description = "Pause and ask the user a question in the terminal; returns their answer. " +
                          "Use kind='confirm' to ask for approval to start implementing (a positive answer unlocks implementation). " +
                          "Use kind='question' for clarifications or option choices.",
            Parameters = JsonDocument.Parse("{" +
                "\"type\":\"object\",\"properties\":{" +
                "\"question\":{\"type\":\"string\",\"description\":\"The question to ask, in the user's language. Include your findings/assumptions briefly.\"}," +
                "\"options\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Optional short answer choices (2-5). The user may still type a free answer.\"}," +
                "\"kind\":{\"type\":\"string\",\"enum\":[\"question\",\"confirm\"],\"description\":\"question (default) or confirm (ask to proceed with implementation).\"}" +
                "},\"required\":[\"question\"]" +
                "}")
                .RootElement.Clone()
        }
    };
}
