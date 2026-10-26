using System.Reflection;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB2: the inference queue runs off the UI thread and is tested without a window, so the
/// compiled assembly may reference no WPF and no display assembly, by project or by package.
/// </summary>
public sealed class InferenceReferenceTests
{
    private static readonly string[] Forbidden =
    [
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "Revelei.Render",
        "Revelei",
    ];

    [Fact]
    public void ReferencesNoUiAndNoDisplayAssembly()
    {
        string[] referenced = Assembly.Load("Revelei.Inference")
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        string[] found = referenced.Intersect(Forbidden, StringComparer.Ordinal).ToArray();

        Assert.True(found.Length == 0, $"Revelei.Inference references {string.Join(", ", found)}.");
    }
}
