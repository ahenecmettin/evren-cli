// Models/JsonContext.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace evren_cli.Models;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatChunk))]
[JsonSerializable(typeof(TermsStatus))]
[JsonSerializable(typeof(TermsAcceptRequest))]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(CliConfig))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ReadFileArgs))]
[JsonSerializable(typeof(WriteFileArgs))]
[JsonSerializable(typeof(ListFilesArgs))]
[JsonSerializable(typeof(RunCommandArgs))]
[JsonSerializable(typeof(CreatePlanArgs))]
[JsonSerializable(typeof(AskUserArgs))]
public sealed partial class EvrenJsonContext : JsonSerializerContext
{
}

public sealed class ReadFileArgs
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("offset")] public int? Offset { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
}

public sealed class WriteFileArgs
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

public sealed class ListFilesArgs
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("pattern")] public string? Pattern { get; set; }
}

public sealed class RunCommandArgs
{
    [JsonPropertyName("command")] public string? Command { get; set; }
}

/// <summary>
/// create_plan aracinin argumanlari.
/// </summary>
public sealed class CreatePlanArgs
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("content")] public string? Content { get; set; }
}

/// <summary>
/// ask_user aracinin argumanlari: soru, istege bagli secenekler ve tur
/// ("question" | "confirm"). confirm olumlu yanitlanirsa evre implement'a gecer.
/// </summary>
public sealed class AskUserArgs
{
    [JsonPropertyName("question")] public string Question { get; set; } = "";
    [JsonPropertyName("options")] public List<string>? Options { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
}