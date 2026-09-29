# Changelog

Bu dosyada evren-cli'nin kullanıcıya görünür değişiklikleri tutulur.
Her geliştirme tesliminde `evren-cli.csproj` içindeki `<Version>` **bir arttırılır**
ve buraya yeni bir satır eklenir.

Format: `[sürüm] — tarih — kısa açıklama`

## 1.5.0 — 2025-01-18

- Çalışma kipleri (modes) eklendi: `normal` (tam düzenleme), `ask` (salt okuma — `write_file`
  kapalı, `run_command` yalnızca salt okuma komutlarına izin verir) ve `plan` (kaynak
  dosyalara dokunmadan `create_plan` aracıyla `plans/<ad>/plan.md` üretir).
- Yeni `ToolGateway` katmanı kip kısıtlarını araç çağrısında zorlar; `create_plan` aracı
  yalnızca plan kipinde açıktır.
- Kip seçimi: REPL komutu `/mode <ask|plan|normal>`, satır önü ön eki (`ask: …`, `plan: …`,
  `normal: …`) ve `--mode` CLI bayrağı.
- REPL prompt'unda aktif kip rozeti gösterilir (normal kipte gizli).

## 1.4.0 — 2025-01-18

- `read_file` tool'u hedefli okuma destekler: `offset` (1 tabanlı başlangıç satırı) ve
  `limit` (döndürülecek satır sayısı) parametreleriyle büyük dosyaların yalnızca ilgili
  bölümü okunur — token maliyeti düşer.
- Sistem prompt'una "Token cost optimization" kuralları eklendi: hedefli okuma, küçük
  dosya bazında düzenleme, `git diff`/`git log` ile önce kontrol ve `list_files`'a her
  zaman `pattern` verme.

## 1.3.0 — 2025-01-18

- REPL prompt'u tamamlanır: çalışma dizininin tam yolu (ev dizini `~` ile kısaltılır)
  ve git dalı artık `evren>` yerine iki satırlı, renkli ve ikonlu gösterilir:
  `🪐 evren 📁 ~/source/repos/evren-cli 🌿 (main)` + `❯`.
- Prompt ikonları: 🪐 (ürün), 📁 (dizin), 🌿 (dal), ❯ (imleç).
- Ayrık (detached) HEAD durumunda dal yerine kısa commit hash'i (`@1a2b3c4`) gösterilir.
