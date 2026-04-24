using System.Runtime.InteropServices;

namespace MediaDebrid_cli.SecretsManager;

public static class SecretsManagerFactory
{
    public static ISecureStorage GetStorage()
    {
        if (OperatingSystem.IsWindows())
        {
            return new SecureStorageWindows();
        }
        if (OperatingSystem.IsMacOS())
        {
            return new SecureStorageMacOS();
        }
        if (OperatingSystem.IsLinux())
        {
            return new SecureStorageLinux();
        }

        throw new PlatformNotSupportedException("Secure storage is not supported on this platform.");
    }
}
