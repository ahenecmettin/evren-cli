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

    /// <summary>Başka bir anahtara geçilerek devam edilebilir mi (401 / kota / zaman aşımı).</summary>
    public bool Failover { get; init; }

    /// <summary>Bu hatayı veren anahtarın masked gösterimi (biliniyorsa).</summary>
    public string? FailedKey { get; init; }

    /// <summary>429 yanıtındaki <c>Retry-After</c> değeri.</summary>
    public TimeSpan? RetryAfter { get; init; }

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
    private const string Yellow = "\u001b[33m";
    private const string Dim = "\u001b[2m";
    private const string Reset = "\u001b[0m";

    private readonly HttpClient _http;
    private readonly ApiKeyPool _pool;
    private readonly TimeSpan _perRequestTimeout;

    /// <summary>
    /// Tek bir istek için üst sınır. Sonuç akışı başladıktan sonra zaman aşımı
    /// olsa bile istek tekrarlanmaz (yarı yazılmış yazı çoğalmasın diye).
    /// </summary>
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(5);

    public EvrenClient(string baseUrl, ApiKeyPool pool, TimeSpan? perRequestTimeout = null)
    {
        _pool = pool;
        _perRequestTimeout = perRequestTimeout ?? DefaultRequestTimeout;

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            // Bütünlüklü zaman aşımı perRequestTimeout ile yönetilir.
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("evren-cli/1.0");
    }

    /// <summary>Anahtar havuzu (<c>/keys</c> komutu için).</summary>
    public ApiKeyPool Pool => _pool;

    public async Task EnsureTermsAcceptedAsync(CancellationToken ct)
    {
        var apiKey = AcquireKey();
        using var statusResponse = await GetWithKeyAsync("terms/status", apiKey, ct);
        await ThrowIfErrorAsync(statusResponse, apiKey, ct);

        var status = await JsonSerializer.DeserializeAsync(
            await statusResponse.Content.ReadAsStreamAsync(ct), EvrenJsonContext.Default.TermsStatus, ct);

        if (status is null || status.Accepted)
            return;

        var body = JsonSerializer.Serialize(new TermsAcceptRequest { Version = status.CurrentVersion },
            EvrenJsonContext.Default.TermsAcceptRequest);
        using var acceptResponse = await PostWithKeyAsync("terms/accept", body, apiKey, ct);
        await ThrowIfErrorAsync(acceptResponse, apiKey, ct);

        _pool.ReportSuccess(apiKey);
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

        // Akış başladıysa tekrar deneme/anahtar değiştirme yapılmaz: konsola
        // yarısı yazılmış bir yanıtın tekrarı kullanıcıyı yanıltır.
        var started = false;

        for (var attempt = 1; ; attempt++)
        {
            var apiKey = AcquireKey();

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_perRequestTimeout > TimeSpan.Zero)
                attemptCts.CancelAfter(_perRequestTimeout);

            try
            {
                var result = await AttemptAsync(
                    apiKey, json,
                    s => { started = true; onReasoning(s); },
                    s => { started = true; onContent(s); },
                    attemptCts.Token);
                _pool.ReportSuccess(apiKey);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // kullanıcı iptali — yutma
            }
            catch (OperationCanceledException)
            {
                // Zaman aşımı: anaşa yazılmadıysa sıradaki anahtarla devam.
                var timeoutError = new EvrenApiException(
                    $"İstek zaman aşımına uğradı ({_perRequestTimeout.TotalSeconds:0} sn).")
                {
                    Failover = true,
                    FailedKey = ApiKeyPool.Mask(apiKey)
                };

                if (started || attempt >= _pool.Count)
                    throw timeoutError;

                AnnounceFailover(timeoutError, attempt);
            }
            catch (EvrenApiException ex) when (!started && ex.Failover && attempt < _pool.Count)
            {
                // Kota/limit/geçersiz anahtar: anahtar havuzda işaretlendi, sıradakine geç.
                AnnounceFailover(ex, attempt);
            }
        }
    }

    private void AnnounceFailover(EvrenApiException ex, int attempt)
    {
        Console.WriteLine();
        Console.WriteLine($"{Yellow}[{ex.FailedKey ?? "?"} devre d\u0131\u015f\u0131: {ShortReason(ex)}]{Reset}");
        Console.WriteLine($"{Dim}[devam: s\u0131radaki API anahtar\u0131 deneniyor \u2014 deneme {attempt + 1}/{_pool.Count}]{Reset}");
    }

    /// <summary>Bir anahtarla tek deneme: isteği gönderir, SSE akışını toplar.</summary>
    private async Task<StreamResult> AttemptAsync(
        string apiKey,
        string json,
        Action<string> onReasoning,
        Action<string> onContent,
        CancellationToken requestCt)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyKey(httpRequest, apiKey);
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, requestCt);
        await ThrowIfErrorAsync(response, apiKey, requestCt);

        var content = new StringBuilder();
        var toolCalls = new SortedDictionary<int, ToolCall>();
        var argBuffers = new Dictionary<int, StringBuilder>();
        string? finishReason = null;
        Usage? usage = null;

        await using var stream = await response.Content.ReadAsStreamAsync(requestCt);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(requestCt) is { } line)
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

    private string AcquireKey() =>
        _pool.Acquire() ?? throw PoolExhausted();

    /// <summary>Havuzdaki hiçbir anahtar şu an kullanılamıyorsa üretilen hata.</summary>
    private EvrenApiException PoolExhausted()
    {
        var wait = _pool.NextAvailableUtc;
        var tail = wait is null
            ? "Anahtar eklemek için /keys komutunu kullanın."
            : $"En erken {FormatRemaining(wait.Value - DateTime.UtcNow)} sonra yeniden denenmeye açılır.";
        return new EvrenApiException(
            $"Kullan\u0131labilecek API anahtar\u0131 kalmad\u0131. {_pool.StatusLine()} {tail}",
            HttpStatusCode.TooManyRequests)
        {
            Failover = true
        };
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        if (seconds < 0) seconds = 0;
        return seconds >= 60 ? $"{(int)Math.Ceiling(seconds / 60.0)} dk" : $"{seconds} sn";
    }

    private static string ShortReason(EvrenApiException ex) =>
        ex.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "anahtar ge\u00e7ersiz (401)",
            HttpStatusCode.PaymentRequired => "kota doldu (402)",
            HttpStatusCode.Forbidden => "yetki/kota engeli (403)",
            HttpStatusCode.TooManyRequests => "limit doldu (429)",
            null => ex.Message,
            _ => $"HTTP {(int)ex.StatusCode}"
        };

    private void ApplyKey(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Remove("X-API-Key");
        request.Headers.Authorization = null;
        request.Headers.Add("X-API-Key", apiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private async Task<HttpResponseMessage> GetWithKeyAsync(string path, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        ApplyKey(request, apiKey);
        return await _http.SendAsync(request, ct);
    }

    private async Task<HttpResponseMessage> PostWithKeyAsync(string path, string body, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        ApplyKey(request, apiKey);
        return await _http.SendAsync(request, ct);
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

    private async Task ThrowIfErrorAsync(HttpResponseMessage response, string apiKey, CancellationToken ct)
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

        TimeSpan? retryAfter = null;
        if (response.Headers.RetryAfter?.Delta is { } delta)
            retryAfter = delta;

        var masked = ApiKeyPool.Mask(apiKey);

        // Kota/limit: bu anahtar geçici dinlenmeye; geçersiz anahtar kalıcı dışına.
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                _pool.ReportDisabled(apiKey, apiMessage);
                break;
            case HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden:
                _pool.ReportRateLimited(apiKey, retryAfter, apiMessage);
                break;
        }

        var failover = response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $"Invalid API key [{masked}]. Generate an LLM key (evren_llm_...) at /api-keys and set it in {CliConfig.ConfigPath} or EVREN_API_KEY.",
            HttpStatusCode.Forbidden => $"Forbidden [{masked}] (terms not accepted, key lacks permission or quota).",
            HttpStatusCode.PaymentRequired => $"Quota exhausted [{masked}].",
            HttpStatusCode.TooManyRequests => $"Rate limit / daily quota exceeded [{masked}].",
            HttpStatusCode.ServiceUnavailable => "Server busy. Retry shortly or switch model with /model.",
            _ => $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
        };

        var message = string.IsNullOrWhiteSpace(apiMessage) ? hint : $"{hint} ({apiMessage})";
        if (_pool.Count > 1)
            message += $" | {_pool.StatusLine()}";

        throw new EvrenApiException(message, response.StatusCode)
        {
            Failover = failover,
            FailedKey = masked,
            RetryAfter = retryAfter
        };
    }

    public void Dispose() => _http.Dispose();
}