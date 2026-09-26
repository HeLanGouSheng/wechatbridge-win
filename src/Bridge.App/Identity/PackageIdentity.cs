using System.Runtime.InteropServices;

namespace Bridge.App.Identity;

/// <summary>Whether this process runs with package identity, i.e. was started from the folder the
/// identity package is registered against, by an exe whose app.manifest names that package.</summary>
public static class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);

    /// <summary>With a zero-length buffer the call returns ERROR_INSUFFICIENT_BUFFER (122) when there is
    /// an identity and APPMODEL_ERROR_NO_PACKAGE (15700) when there is not. Never 0.</summary>
    public static bool HasIdentity()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    public static string? FullName()
    {
        uint length = 0;
        if (GetCurrentPackageFullName(ref length, null) == AppModelErrorNoPackage || length == 0)
        {
            return null;
        }

        var buffer = new char[length];
        return GetCurrentPackageFullName(ref length, buffer) == 0 ? new string(buffer, 0, (int)length - 1) : null;
    }
}
