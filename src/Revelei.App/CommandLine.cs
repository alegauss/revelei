using System.IO;
using Revelei.Inference;

namespace Revelei.App;

/// <summary>The quality mode a run starts in (PB29 names them; PB39 lets a flag pick one).</summary>
public enum QualityMode
{
    /// <summary>The FP16 models the installer carries (PB51): the default.</summary>
    Standard,

    /// <summary>The FP32 models PB13 downloads: the faithful result, and on the CPU the faster one.</summary>
    High,
}

/// <summary>What the command line asked for. Every field null or default means "as a user would start it".</summary>
/// <param name="Open">An image to open at start, instead of dropping or picking one.</param>
/// <param name="Models">The models folder, instead of the default.</param>
/// <param name="Provider">Where inference runs; Auto unless forced.</param>
/// <param name="Export">Where to write the result when the run ends, staying open.</param>
/// <param name="Quality">The quality mode to start in, or null for the saved one.</param>
/// <param name="Manifest">A manifest file to download models from instead of the pinned one (PB59).</param>
public sealed record AppOptions(
    string? Open = null,
    string? Models = null,
    ProviderPreference Provider = ProviderPreference.Auto,
    string? Export = null,
    QualityMode? Quality = null,
    string? Manifest = null)
{
    /// <summary>%LOCALAPPDATA%\Revelei\models, where downloaded models go unless --models says otherwise.</summary>
    public static string DefaultModels =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Revelei", "models");

    /// <summary>The models the installer put beside the app (PB51), read before <see cref="DefaultModels"/>.</summary>
    public static string BundledModels => Path.Combine(AppContext.BaseDirectory, "models");

    /// <summary>The models folder this run downloads into and names when a model is missing.</summary>
    public string ModelsFolder => Models ?? DefaultModels;

    /// <summary>The folders this run reads models from, in order: --models alone, else the bundled set then the downloads.</summary>
    public IReadOnlyList<string> ModelFolders => Models is { } given ? [given] : [BundledModels, DefaultModels];
}

/// <summary>A parse: the options, or why the command line was refused.</summary>
/// <param name="Options">What was asked for, when it parsed.</param>
/// <param name="Error">The message for the user, when it did not.</param>
public sealed record ParsedCommandLine(AppOptions? Options, string? Error)
{
    /// <summary>Whether the command line was accepted.</summary>
    public bool Ok => Options is not null;
}

/// <summary>
/// The command line, parsed in one place (PB39): flags in the quickshell style, so a winwright case
/// reaches a state without the file dialogs and drag and drop it cannot drive yet (WW550, WW551).
/// A flag it does not know, a value missing or out of range, or a flag given twice is refused with
/// a message that names it, never ignored. docs/COMMAND-LINE.md is the user's description.
/// </summary>
public static class CommandLine
{
    private static readonly string[] Flags = ["--open", "--models", "--provider", "--export", "--quality", "--manifest"];

    /// <summary>Parses the arguments after the executable's name.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The options, or the refusal.</returns>
    public static ParsedCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Dictionary<string, string> given = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string flag = arg;
            string? value = null;
            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                flag = arg[..equals];
                value = arg[(equals + 1)..];
            }

            if (!Flags.Contains(flag, StringComparer.Ordinal))
            {
                return Refuse(arg.StartsWith('-')
                    ? $"Unknown option {flag}. Known: {string.Join(", ", Flags)}."
                    : $"Unexpected argument \"{arg}\". To open an image, use --open \"{arg}\".");
            }

            if (value is null)
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return Refuse($"{flag} needs a value.");
                }

                value = args[++i];
            }

            if (value.Length == 0)
            {
                return Refuse($"{flag} needs a value.");
            }

            if (!given.TryAdd(flag, value))
            {
                return Refuse($"{flag} is given twice.");
            }
        }

        ProviderPreference provider = ProviderPreference.Auto;
        if (given.TryGetValue("--provider", out string? providerValue) && !ProviderDetector.TryParse(providerValue, out provider))
        {
            return Refuse($"--provider {providerValue} is not one of auto, winml, dml, cpu.");
        }

        QualityMode? quality = null;
        if (given.TryGetValue("--quality", out string? qualityValue))
        {
            quality = qualityValue.ToUpperInvariant() switch
            {
                // compact is the name PB39 gave Standard before INT8 left (PB29), kept so a script still runs.
                "STANDARD" or "COMPACT" => QualityMode.Standard,
                "HIGH" => QualityMode.High,
                _ => null,
            };
            if (quality is null)
            {
                return Refuse($"--quality {qualityValue} is not one of standard, high.");
            }
        }

        if (given.TryGetValue("--manifest", out string? manifest) && !File.Exists(manifest))
        {
            return Refuse($"--manifest {manifest} is not a file.");
        }

        return new ParsedCommandLine(
            new AppOptions(
                Open: given.GetValueOrDefault("--open"),
                Models: given.GetValueOrDefault("--models"),
                Provider: provider,
                Export: given.GetValueOrDefault("--export"),
                Quality: quality,
                Manifest: manifest),
            null);
    }

    private static ParsedCommandLine Refuse(string message) => new(null, message);
}
