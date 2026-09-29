// EvrenClient.cs
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using evren_cli.Models;

namespace evren_cli;

public sealed class EvrenApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public EvrenApiException(string message, HttpStatusCode? statusCode = null) : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Assembled assistant message plus the provider's finish_reason and (when the
/// server reports it) token usage, so callers can detect truncated responses
/// ("length") and track real prompt/completion token consumption.
/// </summary>
public sealed record StreamResult(ChatMessage Message, string? FinishReason, Usage? Usage = null)
{
    public bool Truncated => string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase);
}

public sealed class EvrenClient : IDisposable
{
    private readonly HttpClient _http;

    public EvrenClient(string baseUrl, string apiKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("evren-cli/1.0");
    }

    public async Task EnsureTermsAcceptedAsync(CancellationToken ct)
    {
        using var statusResponse = await _http.GetAsync("terms/status", ct);
        await ThrowIfErrorAsync(statusResponse, ct);

        var status = await JsonSerializer.DeserializeAsync(
            await statusResponse.Content.ReadAsStreamAsync(ct), EvrenJsonContext.Default.TermsStatus, ct);

        if (status is null || status.Accepted)
            return;

        var body = JsonSerializer.Serialize(new TermsAcceptRequest { Version = status.CurrentVersion },
            EvrenJsonContext.Default.TermsAcceptRequest);
        using var acceptResponse = await _http.PostAsync("terms/accept",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        await ThrowIfErrorAsync(acceptResponse, ct);

        Console.WriteLine($"\u001b[2m[terms v{status.CurrentVersion} accepted]\u001b[0m");
    }

    public async Task<StreamResult> StreamChatAsync(
        ChatRequest request,
        Action<string> onReasoning,
        Action<string> onContent,
        CancellationToken ct)
    {
        request.Stream = true;
        request.StreamOptions ??= new StreamOptions { IncludeUsage = true };

        var json = JsonSerializer.Serialize(request, EvrenJsonContext.Default.ChatRequest);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfErrorAsync(response, ct);

        var content = new StringBuilder();
        var toolCalls = new SortedDictionary<int, ToolCall>();
        var argBuffers = new Dictionary<int, StringBuilder>();
        string? finishReason = null;
        Usage? usage = null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0 || line[0] == ':')
                continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line.AsSpan(5).Trim();
            if (payload.SequenceEqual("[DONE]"))
                break;

            ChatChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize(payload, EvrenJsonContext.Default.ChatChunk);
            }
            catch (JsonException)
            {
                continue;
            }

            if (chunk is null)
                continue;

            if (chunk.Error is not null)
                throw new EvrenApiException(chunk.Error.Message ?? "Unknown stream error");

            // usage typically arrives on the final chunk (with include_usage=true).
            if (chunk.Usage is not null)
                usage = chunk.Usage;

            if (chunk.Choices is null)
                continue;

            foreach (var choice in chunk.Choices)
            {
                // finish_reason arrives on a chunk whose delta is usually absent,
                // so it must be captured before the delta null-check.
                if (!string.IsNullOrEmpty(choice.FinishReason))
                    finishReason = choice.FinishReason;

                var delta = choice.Delta;
                if (delta is null)
                    continue;

                var reasoning = delta.Reasoning ?? delta.ReasoningContent;
                if (!string.IsNullOrEmpty(reasoning))
                    onReasoning(reasoning);

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    content.Append(delta.Content);
                    onContent(delta.Content);
                }

                if (delta.ToolCalls is null)
                    continue;

                foreach (var tc in delta.ToolCalls)
                {
                    var index = tc.Index ?? toolCalls.Count;
                    if (!toolCalls.TryGetValue(index, out var call))
                    {
                        call = new ToolCall { Type = "function" };
                        toolCalls[index] = call;
                        argBuffers[index] = new StringBuilder();
                    }

                    if (!string.IsNullOrEmpty(tc.Id))
                        call.Id = tc.Id;
                    if (tc.Function?.Name is { Length: > 0 } name)
                        call.Function.Name = name;
                    if (tc.Function?.Arguments is { Length: > 0 } args)
                        argBuffers[index].Append(args);
                }
            }
        }

        foreach (var (index, call) in toolCalls)
        {
            call.Function.Arguments = SanitizeArguments(argBuffers[index].ToString());
            call.Id ??= $"call_{Guid.NewGuid():N}";
        }

        var message = new ChatMessage
        {
            Role = "assistant",
            Content = content.Length > 0 ? content.ToString() : null,
            ToolCalls = toolCalls.Count > 0 ? toolCalls.Values.ToList() : null
        };

        return new StreamResult(message, finishReason, usage);
    }

    /// <summary>
    /// Makes sure tool-call arguments are valid JSON before they are echoed back
    /// to the server in the next request. Streaming can produce a truncated or
    /// malformed fragment; the server then fails to parse it and answers with
    /// HTTP 400 ("Expecting ',' delimiter ..."). We defensively keep the longest
    /// valid JSON prefix or fall back to an empty object.
    /// </summary>
    private static string SanitizeArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return "{}";

        if (IsValidJson(arguments))
            return arguments;

        // Try to salvage the longest valid JSON prefix (the common case is a
        // truncated trailing quote/brace), then fall back to a clean object.
        for (var cut = arguments.Length - 1; cut > 0; cut--)
        {
            if (IsValidJson(arguments[..cut]))
                return arguments[..cut];
        }

        return "{}";
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task ThrowIfErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        string? apiMessage = null;
        try
        {
            apiMessage = JsonSerializer.Deserialize(body, EvrenJsonContext.Default.ApiErrorResponse)?.Error?.Message;
        }
        catch (JsonException)
        {
        }

        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $"Invalid API key. Generate an LLM key (evren_llm_...) at /api-keys and set it in {CliConfig.ConfigPath} or EVREN_API_KEY.",
            HttpStatusCode.Forbidden => "Forbidden (terms not accepted or key lacks permission).",
            HttpStatusCode.TooManyRequests => "Rate limit / daily quota exceeded. Wait up to 60s and retry.",
            HttpStatusCode.ServiceUnavailable => "Server busy. Retry shortly or switch model with /model.",
            _ => $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
        };

        var message = string.IsNullOrWhiteSpace(apiMessage) ? hint : $"{hint} ({apiMessage})";
        throw new EvrenApiException(message, response.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}