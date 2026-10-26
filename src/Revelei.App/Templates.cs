using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Revelei.App;

/// <summary>
/// The control templates the design needs and Fluent does not draw (PB37, from quickshell's
/// Templates). Built in code rather than parsed, and a new one per call: a template is sealed on the
/// thread that first applies it, so one shared through a static field is refused by a second window.
/// </summary>
internal static class Templates
{
    /// <summary>The name a template's outer border goes by, which its triggers fill.</summary>
    private const string Ground = "Ground";

    /// <summary>A button with no fill until the pointer is over it, rounded at 6: the toolbar's buttons.</summary>
    /// <returns>A new template.</returns>
    public static ControlTemplate Ghost()
    {
        FrameworkElementFactory content = new(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        ControlTemplate template = new(typeof(ButtonBase)) { VisualTree = Rounded(content) };
        template.Triggers.Add(Filled(UIElement.IsMouseOverProperty, "SubtleFillColorSecondaryBrush"));
        template.Triggers.Add(Filled(ButtonBase.IsPressedProperty, "SubtleFillColorTertiaryBrush"));

        Trigger disabled = new() { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
        template.Triggers.Add(disabled);
        return template;
    }

    /// <summary>A list row: rounded, a subtle fill under the pointer, the theme's selection brush when selected.</summary>
    /// <param name="selection">The resource key of the selected row's brush.</param>
    /// <returns>A new template.</returns>
    public static ControlTemplate Row(string selection)
    {
        FrameworkElementFactory content = new(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        ControlTemplate template = new(typeof(ListBoxItem)) { VisualTree = Rounded(content) };

        // Selected after the pointer, so a selected row under the pointer still reads as selected.
        template.Triggers.Add(Filled(UIElement.IsMouseOverProperty, "SubtleFillColorSecondaryBrush"));
        template.Triggers.Add(Filled(ListBoxItem.IsSelectedProperty, selection));
        return template;
    }

    private static FrameworkElementFactory Rounded(FrameworkElementFactory inside)
    {
        FrameworkElementFactory ground = new(typeof(Border), Ground);
        ground.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        ground.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        ground.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        ground.AppendChild(inside);
        return ground;
    }

    private static Trigger Filled(DependencyProperty property, string key)
    {
        Trigger trigger = new() { Property = property, Value = true };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(key), Ground));
        return trigger;
    }
}
