// Agent.Help.cs
// REPL komut yardımı: REPL açılışında kısa tek satır (PrintHelpBrief),
// /help ile de her komutun açıklamasını ve örneklerini gösteren ayrıntılı liste.
namespace evren_cli;

public sealed partial class Agent
{
    /// <summary>Açılışta gösterilen kısa tek satırlık komut özeti.</summary>
    private void PrintHelpBrief()
    {
        Console.WriteLine(
            $"{Dim}commands: /mode <ask|plan|normal>  /model <name>  /keys [add|rm <no>|reset]  /maxtokens <n>  /maxrounds <n>  /tokens  /clear  /commit  /version  /help  /exit " +
            $"(ayrıntı için /help — önerilen: /model glm-5.3){Reset}");
    }

    /// <summary>
    /// <c>/help</c> çıktısı: her komutu ayrı satırda, ne işe yaradığını ve
    /// gerçek bir kullanım örneğiyle birlikte açıklar.
    /// </summary>
    private void PrintHelp()
    {
        Console.WriteLine($"""
            {Bold}Komutlar{Reset} {Dim}(sohbet satırına yazılır; hepsi '/' ile başlar){Reset}

              {Bold}/mode <ask|plan|normal>{Reset}   {Dim}Çalışma kiplerini değiştirir.{Reset}
                {Dim}normal → serbest düzenleme (varsayılan): dosya yazar, komut çalıştırır.{Reset}
                {Dim}ask    → salt okuma: yalnızca dosya okuma/inceleme, hiçbir değişiklik yapmaz.{Reset}
                {Dim}plan   → değişiklik yapmaz; önce plan üretir (plans/<ad>/plan.md).{Reset}
                {Dim}Örnek: /mode ask        {Dim}→ "Kodu incele ama hiçbir şeye dokunma" demenin kısa yolu.{Reset}
                {Dim}Örnek: /mode normal     {Dim}→ düzenlemeye geri dön.{Reset}
                {Dim}İpucu: ayrı komut yerine mesaj ön eki de olur: "ask: sunucu ne yapıyor?"{Reset}

              {Bold}/model <name>{Reset}              {Dim}Kullanılan modeli değiştirir.{Reset}
                {Dim}Argümansız yazılırsa aktif modeli gösterir: /model → [model: auto]{Reset}
                {Dim}Örnek: /model glm-5.3   {Dim}→ düzenleme işleri için önerilen model.{Reset}

              {Bold}/keys [komut]{Reset}               {Dim}API anahtarı havuzunu yönetir (kota dolunca sıradakine geçilir).{Reset}
                {Dim}/keys                  → anahtarları maskeli listeler: [1] evren_llm…ab12 — hazır · 7 çağrı{Reset}
                {Dim}/keys <anahtar>        → yeni anahtar ekler; virgülle birden çok verilebilir.{Reset}
                {Dim}/keys rm <no>          → listeden siler (numara 1'den başlar).{Reset}
                {Dim}/keys reset            → "beklemede/devre dışı" durumlarını temizler (kota yenilendiyse).{Reset}
                {Dim}Örnek: /keys evren_llm_xxx,evren_llm_yyy{Reset}

              {Bold}/maxtokens <n>{Reset}             {Dim}Modelin tek yanıtta üretebileceği en fazla token (varsayılan {DefaultMaxTokens}).{Reset}
                {Dim}Yanıt "max_tokens limitine takıldı" uyarısıyla kesilirse yükseltin.{Reset}
                {Dim}Argümansız → mevcut değeri gösterir: [max_tokens: {DefaultMaxTokens}]{Reset}
                {Dim}Örnek: /maxtokens 32000 {Dim}→ uzun dosya yazımları için.{Reset}

              {Bold}/maxrounds <n>{Reset}             {Dim}Tek kullanıcı turunda izin verilen araç (tool) adımı sayısı (varsayılan {DefaultMaxRounds}).{Reset}
                {Dim}Bir tur = modelin art arda dosya okuyup komut çalıştırabildiği döngü.{Reset}
                {Dim}Örnek: /maxrounds 20    {Dim}→ küçük görevlerde erken durdur, maliyeti sınırla.{Reset}

              {Bold}/tokens{Reset}                     {Dim}Oturumun token harcamasını ve geçmiş doluluk oranını gösterir.{Reset}
                {Dim}Örnek çıktı: [tokens: 12400 prompt + 890 completion across 6 request(s) | history ~9300/111616]{Reset}
                {Dim}Geçmiş bütçesi dolmaya yakınsa: /clear ile sıfırlayın.{Reset}

              {Bold}/clear{Reset}                     {Dim}Konuşma geçmişini sıfırlar; sadece sistem talimatı kalır.{Reset}
                {Dim}Uzun sohbet sonrası kullanmak hem bağlamı hem token maliyetini düşürür.{Reset}

              {Bold}/commit{Reset}  {Dim}veya{Reset} {Bold}/commitat{Reset}       {Dim}Değişiklikleri otomatik commit'ler: git add -A → kısa mesaj üret → git commit.{Reset}
                {Dim}Mesaj model tarafından üretilir; son commit başlıklarının stilini takip eder (feat:/fix:/chore: …).{Reset}
                {Dim}/commit hızlı düzeltmeler  {Dim}→ mesajı kendiniz verirsiniz, üretime gerek kalmaz.{Reset}
                {Dim}Not: konuşma geçmişini kirletmez; commit mesajı ayrı, araçsız bir istekle üretilir.{Reset}

              {Bold}/version{Reset}                   {Dim}Ürün adı ve sürümü yazar: {VersionInfo.Product} {VersionInfo.Version}{Reset}

              {Bold}/help{Reset}                      {Dim}Bu listeyi gösterir.{Reset}

              {Bold}/exit{Reset}  {Dim}veya{Reset} {Bold}/quit{Reset}        {Dim}Oturumu kapatır (Ctrl+C ise devam eden isteği iptal eder).{Reset}
            """);
    }
}