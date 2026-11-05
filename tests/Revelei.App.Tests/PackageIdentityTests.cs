namespace Revelei.App.Tests;

/// <summary>
/// PB90: a test process has no package, so it reads as unpackaged, which is the side the PhotoBoost
/// data move runs on. The packaged side is checked by registering the MSIX (build-msix.cmd -Register).
/// </summary>
public sealed class PackageIdentityTests
{
    [Fact]
    public void AProcessStartedOutsideAPackageIsNotPackaged() => Assert.False(PackageIdentity.IsPackaged);
}
