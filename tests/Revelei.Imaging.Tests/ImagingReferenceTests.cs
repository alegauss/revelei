using System.Reflection;

namespace Revelei.Imaging.Tests;

/// <summary>
/// PB2: Imaging is unit-testable on its own only while it loads no UI and no ONNX Runtime
/// assembly. A project reference is checked by the architecture tests; this reads what the
/// compiled assembly references, which a package reference changes too.
/// </summary>
public sealed class ImagingReferenceTests
{
    private static readonly string[] Forbidden =
    [
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "Microsoft.ML.OnnxRuntime",
        "Revelei.Inference",
        "Revelei.Render",
    ];

    [Fact]
    public void ReferencesNoUiAndNoInferenceAssembly()
    {
        string[] referenced = Assembly.Load("Revelei.Imaging")
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        string[] found = referenced.Intersect(Forbidden, StringComparer.Ordinal).ToArray();

        Assert.True(found.Length == 0, $"Revelei.Imaging references {string.Join(", ", found)}.");
    }
}
