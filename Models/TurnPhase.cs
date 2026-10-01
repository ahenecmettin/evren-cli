// Models/TurnPhase.cs
namespace evren_cli.Models;

/// <summary>
/// Bir görevin akış evresi: clarify (araçsız netleştirme) → research (bütçeli
/// salt-okuma keşif) → implement (onay sonrası sınırsız uygulama).
/// </summary>
public enum TurnPhase
{
    Clarify,
    Research,
    Implement
}

/// <summary>Evre adlandırması ve prompt rozeti (ikon / etiket) yardımcıları.</summary>
public static class PhaseInfo
{
    /// <summary>Kısa etiket — REPL prompt rozeti için.</summary>
    public static string Tag(TurnPhase phase) => phase switch
    {
        TurnPhase.Research => "research",
        TurnPhase.Implement => "implement",
        _ => "clarify"
    };

    /// <summary>Prompt rozetindeki ikon.</summary>
    public static string Icon(TurnPhase phase) => phase switch
    {
        TurnPhase.Research => "\U0001f52c",   // 🔬 araştırma
        TurnPhase.Implement => "\U0001f6e0",  // 🛠 uygulama
        _ => "\U0001f4ac"                     // 💬 netleştirme
    };

    /// <summary>Açıklamalı özet — /phase çıktısı için.</summary>
    public static string Summary(TurnPhase phase) => phase switch
    {
        TurnPhase.Research => "research · bütçeli salt-okuma keşif (ask_user ile kontrol noktası)",
        TurnPhase.Implement => "implement · onay alındı, araçlar sınırsız",
        _ => "clarify · araçsız; görev yeniden ifade edilir, sorular sorulur"
    };
}
