using System.Runtime.InteropServices;

namespace Revelei.App;

/// <summary>
/// PB90: whether this process runs inside the Microsoft Store's MSIX package. Asked of the process
/// at run time, never set by a build flag, so the exe the tests run is the one both packages ship.
/// </summary>
/// <remarks>
/// Inside a package, %LOCALAPPDATA% reads fall through to the real folder while writes land in the
/// package's private copy, which uninstalling deletes. Anything that moves or deletes a real folder
/// there takes it from the installed copy outside the package; measured on 05/11/2026, when the
/// PhotoBoost to Revelei move carried the installer copy's models into the package's folder.
/// </remarks>
public static class PackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Whether this process has a package identity.</summary>
    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            // A zero-length buffer answers ERROR_INSUFFICIENT_BUFFER when there is a package to name and
            // APPMODEL_ERROR_NO_PACKAGE when there is none; anything else is read as unpackaged.
            int length = 0;
            return GetCurrentPackageFullName(ref length, IntPtr.Zero) == ErrorInsufficientBuffer;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);
}
