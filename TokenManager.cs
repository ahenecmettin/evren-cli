// TokenManager.cs
using System.Text.Json;
using evren_cli.Models;

namespace evren_cli;

/// <summary>
/// Estimates the token size of chat messages and manages the context budget.
///
/// The estimate is deliberately conservative (no tokenizer is available offline):
/// roughly 1 token per 3.5 characters of text plus JSON framing overhead per
/// message. When the server reports real usage it is used to calibrate the
/// estimate so the numbers converge to the truth within a few turns.
/// </summary>
public sealed class TokenManager
{
    private const double CharsPerToken = 3.5;
    private const int PerMessageOverhead = 8;          // role/name/tool_call_id JSON framing
    private const int PerToolCallOverhead = 12;
    private const int PerToolDefinitionOverhead = 24;  // JSON framing for a tools[] entry

    private readonly int _contextWindow;
    private readonly int _reserveTokens;

    // Calibration: real usage / estimated usage, smoothed over turns.
    private double _calibration = 1.0;

    // Incremental bookkeeping so Estimate(history) is O(1) instead of re-walking
    // the whole history on every compaction check / request.
    private long _estimatedTotal;
    private int _messageCount;

    /// <summary>Cumulative prompt tokens billed by the server this session.</summary>
    public long TotalPromptTokens { get; private set; }

    /// <summary>Cumulative completion tokens billed by the server this session.</summary>
    public long TotalCompletionTokens { get; private set; }

    /// <summary>Number of requests that reported usage this session.</summary>
    public int RequestsReported { get; private set; }

    /// <summary>Token budget that must stay free for the model's reply + tool calls.</summary>
    public int ReserveTokens => _reserveTokens;

    /// <summary>Usable budget for history + tools definitions.</summary>
    public int HistoryBudget => Math.Max(0, _contextWindow - _reserveTokens);

    public TokenManager(int contextWindow, int reserveTokens)
    {
        _contextWindow = contextWindow > 0 ? contextWindow : 128_000;
        _reserveTokens = reserveTokens > 0 ? reserveTokens : 8_192;
    }

    /// <summary>
    /// Appends a message's contribution to the incremental estimate. Call
    /// whenever a message is added to history so Estimate() stays cheap.
    /// </summary>
    public void TrackAdded(ChatMessage message)
    {
        _estimatedTotal += EstimateUncalibrated(message);
        _messageCount++;
    }

    /// <summary>
    /// Subtracts a message's contribution. Call BEFORE mutating the message
    /// (the estimate is computed from its current content), i.e. remove first,
    /// then mutate, then call <see cref="TrackAdded"/> again if it stays.
    /// </summary>
    public void TrackRemoved(ChatMessage message)
    {
        _estimatedTotal = Math.Max(0, _estimatedTotal - EstimateUncalibrated(message));
        _messageCount = Math.Max(0, _messageCount - 1);
    }

    /// <summary>Rebuilds the incremental estimate from scratch.</summary>
    public void ResetTracking(IEnumerable<ChatMessage> messages)
    {
        _estimatedTotal = 0;
        _messageCount = 0;
        foreach (var message in messages)
            TrackAdded(message);
    }

    /// <summary>
    /// Feeds the server-reported usage of one request back into the tracker.
    /// Also calibrates the character-based estimator against real numbers.
    /// </summary>
    public void RecordUsage(Usage? usage, int estimatedPromptTokens)
    {
        if (usage is null)
            return;

        TotalPromptTokens += usage.PromptTokens;
        TotalCompletionTokens += usage.CompletionTokens;
        RequestsReported++;

        if (estimatedPromptTokens > 0 && usage.PromptTokens > 0)
        {
            var ratio = (double)usage.PromptTokens / estimatedPromptTokens;
            // Smooth calibration so a single outlier doesn't skew the estimate.
            _calibration = _calibration * 0.7 + Math.Clamp(ratio, 0.25, 4.0) * 0.3;
        }
    }

    /// <summary>Estimated tokens for a single message (approximate, calibrated).</summary>
    public int Estimate(ChatMessage message) =>
        Math.Max(1, (int)Math.Ceiling(EstimateUncalibrated(message) * _calibration));

    private static int EstimateUncalibrated(ChatMessage message)
    {
        var chars = message.Content?.Length ?? 0;

        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var call in message.ToolCalls)
            {
                chars += (call.Function.Name?.Length ?? 0) + (call.Function.Arguments?.Length ?? 0);
                chars += PerToolCallOverhead;
            }
        }

        return (int)Math.Ceiling(chars / CharsPerToken) + PerMessageOverhead;
    }

    /// <summary>Estimated tokens for the whole message list (full walk).</summary>
    public int Estimate(IEnumerable<ChatMessage> messages)
    {
        var total = 0L;
        foreach (var message in messages)
            total += EstimateUncalibrated(message);
        return total <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(total * _calibration));
    }

    /// <summary>
    /// O(1) estimate of the current history via the incremental cache; rebuilds
    /// the cache automatically when it has drifted out of sync.
    /// </summary>
    public int Estimate(ICollection<ChatMessage> messages)
    {
        if (_messageCount != messages.Count)
            ResetTracking(messages);
        return _estimatedTotal <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(_estimatedTotal * _calibration));
    }

    /// <summary>
    /// Estimate of a full prompt: history plus the tool definitions. Used as
    /// the baseline the server-reported usage is calibrated against.
    /// </summary>
    public int PromptEstimate(List<ChatMessage> messages, List<ToolDefinition> tools)
    {
        if (_messageCount != messages.Count)
            ResetTracking(messages);

        var total = _estimatedTotal;
        foreach (var tool in tools)
        {
            var f = tool.Function;
            var schema = f.Parameters.ValueKind == JsonValueKind.Undefined
                ? string.Empty
                : f.Parameters.GetRawText();
            var chars = (f.Name?.Length ?? 0) + (f.Description?.Length ?? 0) + schema.Length;
            total += (int)Math.Ceiling(chars / CharsPerToken) + PerToolDefinitionOverhead;
        }

        return total <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(total * _calibration));
    }

    /// <summary>Remaining headroom in the history budget.</summary>
    public int RemainingBudget(List<ChatMessage> messages) => HistoryBudget - Estimate(messages);
}

/// <summary>
/// Compacts the agent's conversation history to keep it inside the context
/// budget. Strategy (in order of cheapness):
///   1. Trim oversized tool outputs, keeping head and tail.
///   2. Evict whole middle turns — an assistant tool-call message TOGETHER with
///      its tool results — so the messages array stays protocol-valid (an
///      orphaned tool result makes many servers reject the request with 400).
///   3. As a last resort, drop everything except the system prompt and the
///      last turn, with a system note telling the model history was compacted.
/// </summary>
public static class HistoryCompactor
{
    private const int ToolOutputCap = 3_000;   // chars kept per tool result after trim
    private const int KeepHeadChars = 1_200;
    private const int KeepTailChars = 1_200;

    /// <summary>
    /// Compacts <paramref name="history"/> in place until it fits into the
    /// budget. Returns a human-readable summary of what was done, or null when
    /// nothing needed compacting.
    /// </summary>
    public static string? Compact(List<ChatMessage> history, TokenManager tokens)
    {
        var budget = tokens.HistoryBudget;
        if (tokens.Estimate(history) <= budget)
            return null;

        var report = new List<string>();

        // Phase 1: trim long tool outputs, oldest first (recent context matters most).
        for (var i = 0; i < history.Count && tokens.Estimate(history) > budget; i++)
        {
            var message = history[i];
            if (message.Role != "tool" || string.IsNullOrEmpty(message.Content) ||
                message.Content.Length <= ToolOutputCap)
                continue;

            var trimmed = TrimMiddle(message.Content, KeepHeadChars, KeepTailChars);
            var savedChars = message.Content.Length - trimmed.Length;

            tokens.TrackRemoved(message);   // subtract the OLD (pre-trim) size
            message.Content = trimmed;
            tokens.TrackAdded(message);     // add back the NEW (smaller) size

            report.Add($"trimmed tool output (-{savedChars / 1000}k chars)");
        }

        if (tokens.Estimate(history) <= budget)
            return report.Count > 0 ? string.Join(", ", report) : null;

        // Phase 2: evict whole middle turns (assistant tool-call + its tool
        // results, as one atomic group). Never touch the first message (system
        // prompt) or the last 4 messages (the active turn).
        const int KeepRecent = 4;
        var evicted = 0;
        var i2 = 1; // skip system prompt
        while (i2 < history.Count - KeepRecent && tokens.Estimate(history) > budget)
        {
            if (history[i2].Role is "assistant" or "tool")
            {
                tokens.TrackRemoved(history[i2]);
                history.RemoveAt(i2);
                evicted++;

                // Also remove the tool results that belonged to that assistant
                // message so no orphaned tool message remains behind.
                while (i2 < history.Count && history[i2].Role == "tool")
                {
                    tokens.TrackRemoved(history[i2]);
                    history.RemoveAt(i2);
                    evicted++;
                }

                continue;
            }
            i2++;
        }

        if (evicted > 0)
            report.Add($"evicted {evicted} old message(s)");

        if (tokens.Estimate(history) <= budget)
            return string.Join(", ", report);

        // Phase 3: hard reset — keep system prompt + note + last user turn.
        var note = new ChatMessage
        {
            Role = "system",
            Content = "Note: earlier conversation history was removed to free context space. " +
                      "The working directory state on disk is still authoritative."
        };
        var lastUser = history.LastOrDefault(m => m.Role == "user");
        var system = history[0];

        history.Clear();
        history.Add(system);
        history.Add(note);
        if (lastUser is not null)
            history.Add(lastUser);
        tokens.ResetTracking(history);

        report.Add("hard-compacted history (kept system prompt + last user message)");
        return string.Join(", ", report);
    }

    private static string TrimMiddle(string text, int keepHead, int keepTail)
    {
        if (text.Length <= keepHead + keepTail + 16)
            return text;

        return text[..keepHead]
               + $"\n... [{(text.Length - keepHead - keepTail) / 1000}k chars omitted to save context] ...\n"
               + text[^keepTail..];
    }
}
