using System.Windows;

namespace Revelei.App;

/// <summary>Which way the chrome is painted (PB37, from quickshell).</summary>
public enum ChromeTheme
{
    /// <summary>Follow whatever Windows is doing, and keep following it while running.</summary>
    System,

    /// <summary>Light, whatever Windows is doing.</summary>
    Light,

    /// <summary>Dark, whatever Windows is doing.</summary>
    Dark,
}

/// <summary>
/// How the window looks. Following the system means following it, not reading it once: the theme
/// is handed to WPF's own <see cref="ThemeMode"/>, which watches the setting while the app runs.
/// </summary>
public sealed record Appearance
{
    /// <summary>What a fresh installation looks like.</summary>
    public static Appearance Default { get; } = new();

    /// <summary>The chrome's theme; follows Windows unless the user says otherwise.</summary>
    public ChromeTheme Theme { get; init; } = ChromeTheme.System;
}

/// <summary>
/// The one mapping from the theme a user chose to the one WPF paints with. Set on each window from
/// here, never on the Application: freewilly measured the Application-wide setting rendering
/// differently from a window's, and quickshell and claude-tray both keep it per window.
/// </summary>
public static class Theme
{
    /// <summary>What WPF is told for a chosen theme.</summary>
    /// <param name="theme">The user's choice.</param>
    /// <returns>WPF's mode; System keeps following Windows.</returns>
    public static ThemeMode Mode(ChromeTheme theme) => theme switch
    {
        ChromeTheme.Light => ThemeMode.Light,
        ChromeTheme.Dark => ThemeMode.Dark,
        _ => ThemeMode.System,
    };
}
