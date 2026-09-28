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

            if (!config.ApiKey!.StartsWith("evren_llm_", StringComparison.Ordinal))
                Console.WriteLine("\u001b[33mUyarı: anahtar 'evren_llm_' ile başlamıyor. LLM API için portalda Modeller ve API > API Anahtarları sayfasından anahtar oluşturun.\u001b[0m");

            var workingDirectory = cwd is null ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd);
            if (!Directory.Exists(workingDirectory))
            {
                Console.Error.WriteLine($"Directory not found: {workingDirectory}");
                return 1;
            }

            using var appCts = new CancellationTokenSource();
            using var client = new EvrenClient(config.BaseUrl!, config.ApiKey!);
            var tools = new FileTools(workingDirectory);
            var agent = new Agent(client, tools, config.Model!);

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

            Console.WriteLine("\u001b[36mEVREN CLI\u001b[0m — agentic file editing over EVREN LLM API");

            if (prompt.Count > 0)
            {
                await agent.RunTurnAsync(string.Join(' ', prompt), appCts.Token);
                if (once)
                    return 0;
            }

            await agent.RunReplAsync(appCts.Token);
            return 0;
        }

        static void PrintUsage()
        {
            Console.WriteLine("""
                evren-cli [options] [prompt...]

                Options:
                  -m, --model <name>   Model id (default from config, e.g. auto, glm-5.3)
                  -k, --key <key>      API key override
                  -C, --cwd <dir>      Working directory (default: current)
                      --once           Run the given prompt once and exit (no REPL)
                  -h, --help           Show this help

                An interactive REPL starts after the optional initial prompt.
                Env: EVREN_API_KEY overrides the configured key.
                """);
        }
    }
}
