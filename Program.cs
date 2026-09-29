// Program.cs
using System.Text;
using evren_cli.Tools;

namespace evren_cli
{
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string? model = null, cwd = null;
            var keys = new List<string>();
            var once = false;
            string? mode = null;
            var prompt = new List<string>();

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--model" or "-m" when i + 1 < args.Length:
                        model = args[++i];
                        break;
                    case "--key" or "-k" when i + 1 < args.Length:
                        // Tekrarlanabilir: her -k yeni bir anahtar ekler.
                        keys.AddRange(CliConfig.SplitKeys(args[++i]));
                        break;
                    case "--cwd" or "-C" when i + 1 < args.Length:
                        cwd = args[++i];
                        break;
                    case "--mode" when i + 1 < args.Length:
                        mode = args[++i];
                        break;
                    case "--once":
                        once = true;
                        break;
                    case "--version" or "-v":
                        Console.WriteLine($"{VersionInfo.Product} {VersionInfo.Version}");
                        return 0;
                    case "--help" or "-h":
                        PrintUsage();
                        return 0;
                    default:
                        prompt.Add(args[i]);
                        break;
                }
            }

            var config = CliConfig.Load(out var created);
            if (created)
                Console.WriteLine($"\u001b[2m[config created at {CliConfig.ConfigPath}]\u001b[0m");

            // Komut satırı anahtarları listenin başına eklenir: -k > EVREN_API_KEY > config.json.
            foreach (var key in Enumerable.Reverse(keys))
                config.AddKey(key, atFront: true);
            if (model is not null) config.Model = model;

            var workingDirectory = cwd is null ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd);
            if (!Directory.Exists(workingDirectory))
            {
                Console.Error.WriteLine($"Directory not found: {workingDirectory}");
                return 1;
            }

            // Welcome banner — version first, then the resolved settings.
            Console.WriteLine($"\u001b[36m{VersionInfo.Product} {VersionInfo.Version}\u001b[0m — agentic file editing over EVREN LLM API");

            // API anahtar havuzu: tek anahtar eskisi gibi çalışır; birden çok anahtar
            // round-robin dağıtılır ve kota/limit yiyen anahtarın yerine otomatik geçilir.
            var pool = new ApiKeyPool(config.ApiKeys);
            pool.KeysChanged = keyList =>
            {
                config.ApiKeys = keyList.ToList();
                config.Save();
            };

            // API token check: warn and guide the user when it is missing/invalid.
            if (!HasAnyValidKey(pool, out var keyWarning))
            {
                Console.WriteLine($"\u001b[33m{keyWarning}\u001b[0m");
                Console.WriteLine($"\u001b[2m  Nasıl alınır: portalda 'Modeller ve API > API Anahtarları' sayfasından bir anahtar oluşturun (evren_llm_... ile başlar).\u001b[0m");
                Console.WriteLine($"\u001b[2m  Nasıl ayarlanır: `evren-cli -k evren_llm_...` (birden çok -k verilebilir), EVREN_API_KEY\n  (virgülle birden çok anahtar alınabilir) veya ~/.evren-cli/config.json içindeki \"ApiKeys\" listesi.\u001b[0m");
                Console.WriteLine($"\u001b[33mToken olmadan model çağrıları başarısız olur. /help ile komutları görebilirsiniz.\u001b[0m");
            }
            else if (pool.Count > 1)
            {
                Console.WriteLine($"\u001b[2m[api key havuzu: {pool.Count} anahtar; biri dolunca sıradakine otomatik geçilir — /keys ile görün]\u001b[0m");
            }

            using var appCts = new CancellationTokenSource();
            using var client = new EvrenClient(config.BaseUrl!, pool);
            var tools = new FileTools(workingDirectory);
            var agent = new Agent(client, tools, config.Model!);

            if (mode is not null)
            {
                var parsed = ModeInfo.ParseName(mode);
                if (parsed is null)
                {
                    Console.Error.WriteLine($"Unknown mode: {mode} (ask | plan | normal)");
                    return 1;
                }
                agent.SetMode(parsed.Value);
            }

            // Register Ctrl+C handler to cancel the ongoing operation.
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                appCts.Cancel();
                Console.WriteLine($"\u001b[31m\n[Ctrl+C pressed, canceling...]\u001b[0m");
            };

            try
            {
                await client.EnsureTermsAcceptedAsync(appCts.Token);
            }
            catch (EvrenApiException ex)
            {
                Console.Error.WriteLine($"\u001b[31mAPI error: {ex.Message}\u001b[0m");
                return 1;
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"\u001b[31mNetwork error: {ex.Message}\u001b[0m");
                return 1;
            }

            if (prompt.Count > 0)
            {
                await agent.RunTurnAsync(string.Join(' ', prompt), appCts.Token);
                if (once)
                    return 0;
            }

            await agent.RunReplAsync(appCts.Token);
            return 0;
        }

        /// <summary>
        /// Havuzda en az bir geçerli görünümlü anahtar var mı? Yoksa
        /// <paramref name="warning"/> kılavuz mesajını taşır.
        /// </summary>
        static bool HasAnyValidKey(ApiKeyPool pool, out string warning)
        {
            if (pool.Count == 0)
            {
                warning = "Uyarı: API anahtarı (token) ayarlı değil.";
                return false;
            }

            var invalid = pool.Entries.Count(e => !e.Key.StartsWith("evren_llm_", StringComparison.Ordinal));
            if (invalid == pool.Count)
            {
                warning = "Uyarı: hiçbir anahtar 'evren_llm_' ile başlamıyor, geçersiz olabilirler.";
                return false;
            }

            warning = "";
            return true;
        }

        static void PrintUsage()
        {
            Console.WriteLine($"""
                {VersionInfo.Product} {VersionInfo.Version}

                evren-cli [options] [prompt...]

                Options:
                  -m, --model <name>   Model id (default from config, e.g. auto, glm-5.3)
                  -k, --key <key>      API key override. Repeatable (or comma separated):
                                       multiple keys form a pool; when one hits its quota
                                       the next one takes over automatically.
                  -C, --cwd <dir>      Working directory (default: current)
                      --mode <mode>     Start in ask | plan | normal (default: normal)
                      --once           Run the given prompt once and exit (no REPL)
                  -v, --version        Show version and exit
                  -h, --help           Show this help

                An interactive REPL starts after the optional initial prompt.
                Env: EVREN_API_KEY — one or more keys (comma separated) overriding the config.
                Manage keys live with /keys (list, add, remove).
                Quick-commit with /commit or /commitat: stages everything, generates a
                short commit message from the diff and commits (/commit <msg> uses yours).
                """);
        }
    }
}