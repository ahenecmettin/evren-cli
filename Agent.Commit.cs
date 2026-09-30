// Agent.Commit.cs
// /commit — /commitat: git add -A ile değişiklikleri aşamaya alır, modele göre
// çok kısa bir commit mesajı üretir ve git commit ile gönderir.
// Ana konuşma geçmişine dokunmaz: mesaj üretimi araçsız, bağımsız tek istektir.
using System.Diagnostics;
using System.Text;
using evren_cli.Models;

namespace evren_cli;

public sealed partial class Agent
{
    /// <summary>
    /// Değişiklikleri aşamaya alır, kısa commit mesajı üretir ve commit'i atar.
    /// <c>/commit &lt;mesaj&gt;</c> verilirse üretime gerek kalmaz, mesaj olduğu
    /// gibi kullanılır.
    /// </summary>
    private async Task HandleCommitAsync(string input, CancellationToken ct)
    {
        var args = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!(await GitAsync(["rev-parse", "--is-inside-work-tree"], ct)).Ok)
        {
            Console.WriteLine($"{Red}/commit yalnızca bir git deposunda çalışır.{Reset}");
            return;
        }

        // Yeni/silinen/tümü değişen dosyalar kapsansın diye önce aşamaya al.
        if (!(await GitAsync(["add", "-A"], ct)).Ok)
        {
            Console.WriteLine($"{Red}git add başarısız.{Reset}");
            return;
        }

        var (ok, diff) = await GitAsync(["diff", "--cached", "--unified=0"], ct);
        if (!ok || diff.Length == 0)
        {
            Console.WriteLine($"{Dim}[commit edilecek değişiklik yok]{Reset}");
            return;
        }

        string message;
        if (args.Length > 1)
        {
            // '/commit düzeltmeler uygulandi' → kullanıcı mesajı hazır.
            message = string.Join(' ', args.Skip(1));
        }
        else
        {
            var truncated = false;
            if (diff.Length > 12_000)
            {
                diff = diff[..12_000];
                truncated = true;
            }

            Console.WriteLine($"{Dim}• commit mesajı üretiliyor…{Reset}");
            var generated = await GenerateCommitMessageAsync(diff, ct);
            if (generated is null)
            {
                Console.WriteLine($"{Yellow}commit mesajı diff'ten deterministik olarak türetiliyor…{Reset}");
                generated = FallbackCommitMessage(diff);
            }

            message = generated;
            if (truncated)
                Console.WriteLine($"{Dim}[diff kısaltıldı; mesaj ilk bölüme göre üretildi]{Reset}");
        }

        Console.WriteLine($"{Dim}• mesaj: {message}{Reset}");

        var commit = await GitAsync(["commit", "-m", message], ct);
        if (!commit.Ok)
        {
            Console.WriteLine($"{Red}git commit başarısız:{Reset} {commit.Output}");
            return;
        }

        Console.WriteLine($"{Green}[commit atıldı]{Reset}");
    }

    /// <summary>
    /// Diff'e göre tek satırlık kısa commit mesajı üretir. Son commit başlıkları
    /// stile örnek olarak eklenir; model yanıtından yalın mesaj ayıklanır.
    /// Model boş yanıt dönerse/hataya düşerse null döner ve çağıran deterministik
    /// fallback kullanır.
    /// </summary>
    private async Task<string?> GenerateCommitMessageAsync(string diff, CancellationToken ct)
    {
        var (_, log) = await GitAsync(["log", "--oneline", "-5"], ct);

        var request = new ChatRequest
        {
            Model = _model,
            // Reasoning yapan modeller yanıt üretmeden önce düşünme akışına
            // token harcayabilir; 80 token'ın tamamı reasoning'e gidince content
            // boş kalıyordu (→ "Commit mesajı üretilemedi"). Bütçeyi büyütüp
            // yanıt için de alan bıraktık.
            MaxTokens = 512,
            Messages =
            [
                new ChatMessage
                {
                    Role = "system",
                    Content = "Verilen git diff'i için TEK SATIRLIK kısa bir commit mesajı üret. " +
                              "Conventional commit öneki kullan (feat:, fix:, chore:, refactor:, docs:, test:, style:, build:, ci:). " +
                              "Stili verilen son commit başlıklarına uydur. " +
                              "Yalnızca commit mesajını yaz: tırnak, kod bloğu, madde imi, açıklama ya da ek metin ekleme."
                },
                new ChatMessage
                {
                    Role = "user",
                    Content = $"Son commit başlıkları:\n{log}\n\ndiff:\n{diff}\n\nCommit mesajı:"
                }
            ]
        };

        var streamed = new StringBuilder();
        StreamResult result;
        try
        {
            result = await _client.StreamChatAsync(request, _ => { }, chunk => streamed.Append(chunk), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine($"{Yellow}[canceled]{Reset}");
            return null;
        }
        catch (EvrenApiException ex)
        {
            // Sebebi görünür yap; sessizce fallback'e geçme.
            Console.Error.WriteLine($"{Yellow}API error: {ex.Message}{Reset}");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"{Yellow}Network error: {ex.Message}{Reset}");
            return null;
        }

        var text = streamed.Length > 0 ? streamed.ToString() : result.Message.Content ?? "";
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null)
            return null;

        var sanitized = SanitizeCommitMessage(line);
        return sanitized.Length > 0 ? sanitized : null;
    }

    /// <summary>
    /// Model başarısız olursa diff'ten deterministik bir conventional-commit
    /// mesajı üretir: değişen dosyaların yollarından kapsam ve özet inşa edilir.
    /// Böylece /commit hiçbir durumda takılmaz.
    /// </summary>
    private static string FallbackCommitMessage(string diff)
    {
        var files = new List<string>();
        foreach (var line in diff.Split('\n'))
        {
            // Yeni/eklenen dosyanın yolu (+) tarafından alınır; silinen dosyada
            // bu taraf "/dev/null" olur ve atlanır.
            if (line.StartsWith("+++ ", StringComparison.Ordinal) && line.Length > 4)
            {
                var path = line[4..].Trim().TrimStart('a', 'b');
                if (path.Length > 0 && path != "/dev/null")
                    files.Add(path);
            }
        }

        var distinct = files.Distinct().ToList();
        var menu = distinct.Count > 0 ? distinct : new List<string> { "değişiklikler" };

        // Kod, belge yoksa genel amaçlı "chore:" kullanılır.
        var kind = menu.Any(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
            ? "fix"
            : menu.Any(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                ? "docs"
                : "chore";

        var scope = distinct.Count > 0 && distinct[0].Contains('/')
            ? System.IO.Path.GetDirectoryName(distinct[0])?.Replace('\\', '/') ?? ""
            : "";

        var detail = distinct.Take(4).Select(f => f.Contains('/')
            ? f[(f.LastIndexOf('/') + 1)..]
            : f);

        var msg = string.IsNullOrEmpty(scope)
            ? $"{kind}: {string.Join(", ", detail)}"
            : $"{kind}({scope}): {string.Join(", ", detail)}";

        return msg.Length > 120 ? msg[..120].TrimEnd() : msg;
    }

    /// <summary>Tek satıra indirir; tırnak/kod bloğu gibi süsleri atar, aşırı uzunsa keser.</summary>
    private static string SanitizeCommitMessage(string line)
    {
        var msg = line.Trim().Trim('"', '\'', '`').Trim();
        msg = msg.Replace("**", "").Trim();

        if (msg.StartsWith("Commit mesajı:", StringComparison.OrdinalIgnoreCase))
            msg = msg["Commit mesajı:".Length..].Trim();

        while (msg.Length > 0 && (msg[0] == '-' || msg[0] == '*'))
            msg = msg[1..].Trim();

        return msg.Length > 120 ? msg[..120].TrimEnd() : msg;
    }

    /// <summary>Çalışma dizininde git komutu çalıştırır; (başarı, stdout+stderr) döner.</summary>
    private static async Task<(bool Ok, string Output)> GitAsync(string[] arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process is null)
                return (false, "git başlatılamadı");

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return (process.ExitCode == 0, (stdout + stderr).Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
