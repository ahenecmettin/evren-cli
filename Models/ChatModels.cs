using System.Text.Json;
using System.Text.Json.Serialization;

namespace evren_cli.Models;

public sealed class ChatRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "auto";
    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();
    [JsonPropertyName("tools")] public List<ToolDefinition>? Tools { get; set; }
    [JsonPropertyName("tool_choice")] public string? ToolChoice { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("stream_options")] public StreamOptions? StreamOptions { get; set; }
}

public sealed class StreamOptions
{
    [JsonPropertyName("include_usage")] public bool IncludeUsage { get; set; }
}

public sealed class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("tool_calls")] public List<ToolCall>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class ToolDefinition
{
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public FunctionDefinition Function { get; set; } = new();
}

public sealed class FunctionDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; }
}

public sealed class ToolCall
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public FunctionCall Function { get; set; } = new();
}

public sealed class FunctionCall
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
}

public sealed class ChatChunk
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("choices")] public List<ChunkChoice>? Choices { get; set; }
    [JsonPropertyName("usage")] public Usage? Usage { get; set; }
    [JsonPropertyName("error")] public ApiErrorBody? Error { get; set; }
}

public sealed class ChunkChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("delta")] public ChunkDelta? Delta { get; set; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class ChunkDelta
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }
    [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
    [JsonPropertyName("tool_calls")] public List<DeltaToolCall>? ToolCalls { get; set; }
}

public sealed class DeltaToolCall
{
    [JsonPropertyName("index")] public int? Index { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("function")] public FunctionCall? Function { get; set; }
}

public sealed class Usage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
}

public sealed class TermsStatus
{
    [JsonPropertyName("current_version")] public int CurrentVersion { get; set; }
    [JsonPropertyName("is_material")] public bool IsMaterial { get; set; }
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
}

public sealed class TermsAcceptRequest
{
    [JsonPropertyName("version")] public int Version { get; set; }
}

public sealed class ApiErrorResponse
{
    [JsonPropertyName("error")] public ApiErrorBody? Error { get; set; }
}

public sealed class ApiErrorBody
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
}
