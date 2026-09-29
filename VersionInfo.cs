// VersionInfo.cs
using System.Reflection;

namespace evren_cli;

/// <summary>
/// Single source of truth for the version shown in the welcome banner,
/// <c>--version</c> and <c>/version</c>.
/// The real value lives in <c>&lt;Version&gt;</c> in evren-cli.csproj — bump it by one
/// on every development change and add a line to CHANGELOG.md.
/// </summary>
public static class VersionInfo
{
    /// <summary>Keep in sync with &lt;Version&gt; in evren-cli.csproj (used if reflection is trimmed away).</summary>
    public const string FallbackVersion = "1.6.0";

    public const string Product = "EVREN CLI";

    public static string Version { get; } = Resolve();

    private static string Resolve()
    {
        try
        {
            var asm = typeof(VersionInfo).Assembly;

            var informational = asm
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+'); // strip the source-revision suffix
                return plus > 0 ? informational[..plus] : informational;
            }

            var name = asm.GetName().Version;
            if (name is not null)
                return name.ToString(3);
        }
        catch
        {
            // fall through to the constant
        }

        return FallbackVersion;
    }
}