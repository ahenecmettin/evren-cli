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

            string? model = null, key = null, cwd = null;
            var once = false;
            var prompt = new List<string>();

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--model" or "-m" when i + 1 < args.Length:
                        model = args[++i];
                        break;
                    case "--key" or "-k" when i + 1 < args.Length:
                        key = args[++i];
                        break;
                    case "--cwd" or "-C" when i + 1 < args.Length:
                        cwd = args[++i];
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

            if (key is not null) config.ApiKey = key;
            if (model is not null) config.Model = model;

            var workingDirectory = cwd is null ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd);
            if (!Directory.Exists(workingDirectory))
            {
                Console.Error.WriteLine($"Directory not found: {workingDirectory}");
                return 1;
            }

            // Welcome banner — version first, then the resolved settings.
            Console.WriteLine($"\u001b[36m{VersionInfo.Product} {VersionInfo.Version}\u001b[0m — agentic file editing over EVREN LLM API");

            // API token check: warn and guide the user when it is missing/invalid.
            if (!HasValidKey(config.ApiKey, out var keyWarning))
            {
                Console.WriteLine($"\u001b[33m{keyWarning}\u001b[0m");
                Console.WriteLine($"\u001b[2m  Nasıl alınır: portalda 'Modeller ve API > API Anahtarları' sayfasından bir anahtar oluşturun (evren_llm_... ile başlar).\u001b[0m");
                Console.WriteLine($"\u001b[2m  Nasıl ayarlanır: `evren-cli -k evren_llm_...` ya da environment variable EVREN_API_KEY, veya\t~/.evren-cli/config.json içindeki ApiKey alanı.\u001b[0m");
                Console.WriteLine($"\u001b[33mToken olmadan model çağrıları başarısız olur. /help ile komutları görebilirsiniz.\u001b[0m");
            }

            using var appCts = new CancellationTokenSource();
            using var client = new EvrenClient(config.BaseUrl!, config.ApiKey!);
            var tools = new FileTools(workingDirectory);
            var agent = new Agent(client, tools, config.Model!);

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
        /// Returns true when the key looks like a usable EVREN LLM key.
        /// Otherwise <paramref name="warning"/> carries the guidance message.
        /// </summary>
        static bool HasValidKey(string? apiKey, out string warning)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                warning = "Uyarı: API anahtarı (token) ayarlı değil.";
                return false;
            }

            if (!apiKey.StartsWith("evren_llm_", StringComparison.Ordinal))
            {
                warning = "Uyarı: anahtar 'evren_llm_' ile başlamıyor, geçersiz olabilir.";
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
                  -k, --key <key>      API key override
                  -C, --cwd <dir>      Working directory (default: current)
                      --once           Run the given prompt once and exit (no REPL)
                  -v, --version        Show version and exit
                  -h, --help           Show this help

                An interactive REPL starts after the optional initial prompt.
                Env: EVREN_API_KEY overrides the configured key.
                """);
        }
    }
}