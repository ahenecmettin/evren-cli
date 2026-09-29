# Changelog

Bu dosyada evren-cli'nin kullanıcıya görünür değişiklikleri tutulur.
Her geliştirme tesliminde `evren-cli.csproj` içindeki `<Version>` **bir arttırılır**
ve buraya yeni bir satır eklenir.

Format: `[sürüm] — tarih — kısa açıklama`

## 1.3.0 — 2025-01-18

- REPL prompt'u tamamlanır: çalışma dizininin tam yolu (ev dizini `~` ile kısaltılır)
  ve git dalı artık `evren>` yerine iki satırlı, renkli ve ikonlu gösterilir:
  `🪐 evren 📁 ~/source/repos/evren-cli 🌿 (main)` + `❯`.
- Prompt ikonları: 🪐 (ürün), 📁 (dizin), 🌿 (dal), ❯ (imleç).
- Ayrık (detached) HEAD durumunda dal yerine kısa commit hash'i (`@1a2b3c4`) gösterilir.

## 1.2.0 — 2025-01-18

- `Ctrl+C` ile iptal edildiğinde araç (tool) çalışması tamamlanır, `HTTP 400` hatası ("Expecting ',' delimiter") önlenir.
- `Agent.RunTurnAsync` içindeki `OperationCanceledException`'ı yakalayarak geçersiz isteklerin oluşmasını önler.
- `Agent.RunTurnAsync` içindeki `RollbackLastUserMessage` ile iptalde bir önceki kullanıcı mesajı silinir.
- `TokenManager` sınıfı optimize edilir: `PromptEstimate` ve `Estimate` performansı artar, `TrackAdded`/`TrackRemoved` ile O(1) tahmin.
- `EvrenClient` içinde `SanitizeArguments` ile akış sırasında kesik JSON gelen argümanlar düzgün hale getirilir.
- `Agent.cs`'te `PrintHelp` metodu ve `RunReplAsync` için `Console.CancelKeyPress` olayı eklenir.
- `Agent.cs`'te `RunTurnAsync` içinde `ToolCall`'ların iptal durumunda `tool` sonucu eklenir.
- `README.md`'ye "Sürüm politikası" bölümü güncellenir.

## 1.1.0 — 2025-01-17

- Başlangıçta hoş geldiniz mesajı: ürün adı + sürüm numarası artık açılışta gösteriliyor.
- `--version` / `-v` bayrağı eklendi: yalnızca sürümü yazıp çıkar.
- Token (API anahtarı) eksik/geçersizse açılışta sarı renkli uyarı ve adım adım yönlendirme:
  nereden alınır, nasıl ayarlanır (`-k`, `EVREN_API_KEY`, `~/.evren-cli/config.json`).
- `VersionInfo.cs` eklendi: sürüm bilgisi için tek doğruluk kaynağı (AOT-trim güvenli fallback ile).
- `<Version>`, `<InformationalVersion>` vb. csproj'a eklendi; README ve yardım metnine sürüm politikası notu işlendi.
- `/help` ve `/version` REPL komutları sürüm bilgisini gösterir.
- README'ye "Sürüm politikası" bölümü eklendi (aşağıda).
