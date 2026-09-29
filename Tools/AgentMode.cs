// Tools/AgentMode.cs
namespace evren_cli.Tools;

/// <summary>Çalışma kipi (mode): normal düzenleme, ask (salt okuma) ve plan (plan üretimi).</summary>
public enum AgentMode
{
    Normal,
    Ask,
    Plan
}

/// <summary>
/// Kip adlandırması, satır önü ayrıştırma (<c>ask: …</c>, <c>plan: …</c>) ve
/// görünüm (ikon / etiket) yardımcıları.
/// </summary>
public static class ModeInfo
{
    private static readonly (string Keyword, AgentMode Mode)[] Keywords =
    [
        ("ask", AgentMode.Ask),
        ("/ask", AgentMode.Ask),
        ("plan", AgentMode.Plan),
        ("/plan", AgentMode.Plan),
        ("normal", AgentMode.Normal),
        ("/normal", AgentMode.Normal),
    ];

    /// <summary>
    /// Satırın <c>ask:</c>/<c>plan:</c>/<c>normal:</c> (veya <c>/ask …</c>, <c>/plan …</c>)
    /// ön ekiyle başlayıp başlamadığını söyler; <paramref name="rest"/> mesajın kalanıdır.
    /// </summary>
    public static bool TryParse(string text, out AgentMode mode, out string rest)
    {
        foreach (var (keyword, candidate) in Keywords)
        {
            if (text.Length <= keyword.Length)
                continue;
            if (!text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                continue;

            // 'ask: ...' her zaman; '/' ile baslamayan anahtar sozcukler nokta iki nokta ister
            // (boylece "plan yapalim..." gibi mesajlar yanlislikla kipi degistirmez).
            var sep = text[keyword.Length];
            var slash = keyword[0] == '/';
            if (sep == ':' || (slash && (sep == ' ' || sep == '\t')))
            {
                mode = candidate;
                rest = text[(keyword.Length + 1)..].Trim();
                return true;
            }

            if (slash && text.Length == keyword.Length)
            {
                mode = candidate;
                rest = "";
                return true;
            }

            mode = candidate;
            rest = text[(keyword.Length + 1)..].Trim();
            return true;
        }

        mode = AgentMode.Normal;
        rest = text;
        return false;
    }

    /// <summary>Ön eki ayıklar; varsa kipi döndürür ve <paramref name="input"/> kalan mesaja güncellenir.</summary>
    public static AgentMode? StripPrefix(ref string input)
    {
        if (!TryParse(input, out var mode, out var rest))
            return null;

        input = rest;
        return mode;
    }

    /// <summary><c>/mode</c> için kip adı eşlemesi (kısayollar dahil).</summary>
    public static AgentMode? ParseName(string name) => name.Trim().ToLowerInvariant() switch
    {
        "ask" or "read" or "soru" or "a" => AgentMode.Ask,
        "plan" or "p" => AgentMode.Plan,
        "normal" or "edit" or "reset" or "n" => AgentMode.Normal,
        _ => null
    };

    /// <summary>Kısa etiket — REPL prompt rozeti için.</summary>
    public static string Tag(AgentMode mode) => mode switch
    {
        AgentMode.Ask => "ask",
        AgentMode.Plan => "plan",
        _ => "normal"
    };

    /// <summary>Açıklamalı özet — <c>/mode</c> çıktısı için.</summary>
    public static string Summary(AgentMode mode) => mode switch
    {
        AgentMode.Ask => "ask · salt okuma (read-only)",
        AgentMode.Plan => "plan · plan üretimi (plans/<ad>/plan.md)",
        _ => "normal · serbest düzenleme"
    };

    /// <summary>Prompt rozetindeki ikon (normal kipte boş).</summary>
    public static string Icon(AgentMode mode) => mode switch
    {
        AgentMode.Ask => "\U0001f50d",   // 🔍 salt okuma
        AgentMode.Plan => "\U0001f4dd",  // 📝 plan
        _ => ""
    };
}
