using System.Text.Json;

namespace Revelei.Inference;

/// <summary>One finished operation, as the log keeps it (PB22).</summary>
/// <param name="At">When it finished.</param>
/// <param name="Operation">What it was, e.g. "upscale x4".</param>
/// <param name="Model">The model's folder name.</param>
/// <param name="Variant">fp32, fp16 or int8.</param>
/// <param name="Provider">DirectML, Cpu, or the Windows ML provider's short name, e.g. TensorRT-RTX (PB43).</param>
/// <param name="Adapter">The GPU, when there was one.</param>
/// <param name="Width">Input columns.</param>
/// <param name="Height">Input rows.</param>
/// <param name="Tile">The tile side it started with.</param>
/// <param name="Tiles">Tiles run.</param>
/// <param name="Retries">Each recovery step, in order (PB21).</param>
/// <param name="Milliseconds">Wall time.</param>
/// <param name="Outcome">ok, cancelled or the failure's message.</param>
public sealed record OperationRecord(
    DateTimeOffset At,
    string Operation,
    string Model,
    string Variant,
    string Provider,
    string? Adapter,
    int Width,
    int Height,
    int Tile,
    int Tiles,
    IReadOnlyList<string> Retries,
    double Milliseconds,
    string Outcome);

/// <summary>
/// Appends a JSON line per operation to operations.jsonl (PB22), for bug reports and for the
/// README's benchmark table, rotating to operations.1.jsonl and on when the file grows past its
/// limit so the folder never grows without bound.
/// </summary>
public sealed class OperationLog
{
    private const string Name = "operations";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly Lock gate = new();

    /// <summary>A log in a folder.</summary>
    /// <param name="folder">Where the files go; created when missing.</param>
    /// <param name="maxBytes">Size at which the current file is rotated.</param>
    /// <param name="keep">Rotated files kept beside the current one.</param>
    public OperationLog(string folder, long maxBytes = 1 << 20, int keep = 5)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(keep);
        Folder = folder;
        MaxBytes = maxBytes;
        Keep = keep;
    }

    /// <summary>%LOCALAPPDATA%\Revelei\logs, where the app writes it.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Revelei", "logs");

    /// <summary>Where the files go.</summary>
    public string Folder { get; }

    /// <summary>Size at which the current file is rotated.</summary>
    public long MaxBytes { get; }

    /// <summary>Rotated files kept.</summary>
    public int Keep { get; }

    /// <summary>The file being written.</summary>
    public string CurrentFile => Path.Combine(Folder, Name + ".jsonl");

    /// <summary>Writes a record, rotating first when the file is full.</summary>
    /// <param name="record">The operation.</param>
    public void Append(OperationRecord record)
    {
        string line = JsonSerializer.Serialize(record, Json) + "\n";
        lock (gate)
        {
            Directory.CreateDirectory(Folder);
            FileInfo current = new(CurrentFile);
            if (current.Exists && current.Length + line.Length > MaxBytes)
            {
                Rotate();
            }

            File.AppendAllText(CurrentFile, line);
        }
    }

    /// <summary>The records in the current file, oldest first.</summary>
    /// <returns>The records; empty when nothing was logged.</returns>
    public IReadOnlyList<OperationRecord> ReadCurrent()
    {
        lock (gate)
        {
            return File.Exists(CurrentFile)
                ? [.. File.ReadAllLines(CurrentFile).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<OperationRecord>(l, Json)!)]
                : [];
        }
    }

    private void Rotate()
    {
        string Numbered(int n) => Path.Combine(Folder, $"{Name}.{n}.jsonl");
        if (Keep == 0)
        {
            File.Delete(CurrentFile);
            return;
        }

        File.Delete(Numbered(Keep));
        for (int n = Keep - 1; n >= 1; n--)
        {
            if (File.Exists(Numbered(n)))
            {
                File.Move(Numbered(n), Numbered(n + 1));
            }
        }

        File.Move(CurrentFile, Numbered(1));
    }
}
