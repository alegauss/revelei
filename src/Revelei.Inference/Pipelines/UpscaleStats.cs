namespace Revelei.Inference.Pipelines;

/// <summary>What an upscale did, collected while it runs, for the operation log (PB22).</summary>
public sealed class UpscaleStats
{
    /// <summary>The model's folder name.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>The tile side the run started with.</summary>
    public int Tile { get; set; }

    /// <summary>Tiles finished.</summary>
    public int Tiles { get; set; }

    /// <summary>Each recovery step, in order.</summary>
    public List<string> Retries { get; } = [];
}
