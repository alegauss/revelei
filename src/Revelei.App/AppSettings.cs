using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revelei.App;

/// <summary>
/// What the person chose that outlives a run (PB74), kept in %LOCALAPPDATA%\Revelei\settings.json.
/// A file that is missing or does not read is the defaults, so a damaged one never stops a start.
/// </summary>
/// <param name="ModelsFolder">Where downloaded models live, or null for <see cref="AppOptions.DefaultModels"/>.</param>
/// <param name="Prompt">PB80: the last prompt Create generated from, kept between sessions.</param>
public sealed record AppSettings(string? ModelsFolder = null, string? Prompt = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        WriteIndented = true,
    };

    /// <summary>%LOCALAPPDATA%\Revelei\settings.json.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Revelei", "settings.json");

    /// <summary>Reads the settings, or the defaults when there are none or they do not read.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The settings.</returns>
    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Writes the settings, replacing the file whole so a crash never leaves half of it.</summary>
    /// <param name="path">The file.</param>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
        File.Move(temporary, path, overwrite: true);
    }
}
