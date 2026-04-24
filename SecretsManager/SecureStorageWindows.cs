using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace MediaDebrid_cli.SecretsManager;

[SupportedOSPlatform("windows")]
public sealed class SecureStorageWindows : ISecureStorage
{
    private static readonly SemaphoreSlim Lock = new(1, 1);
    
    // Prefix to identify our app's credentials in the Vault
    private const string TargetPrefix = "MediaDebrid:";
    
    // Windows Credential Manager hard limit for CRED_TYPE_GENERIC
    private const int MaxBlobSize = 512;
    private const int ERROR_NOT_FOUND = 1168;

    public Task SaveAsync(string key, string value)
    {
        // Bug Fix: Offload to thread pool to prevent blocking UI thread
        return Task.Run(() =>
        {
            Lock.Wait();
            try
            {
                var targetName = TargetPrefix + key;
                var credentialBlob = Encoding.UTF8.GetBytes(value);

                if (credentialBlob.Length > MaxBlobSize)
                {
                    throw new InvalidOperationException(
                        $"Credential value exceeds the Windows Vault limit of {MaxBlobSize} bytes. " +
                        "Consider storing a reference or reducing token size.");
                }

                var credential = new NativeMethods.CREDENTIAL
                {
                    Type = NativeMethods.CRED_TYPE_GENERIC,
                    TargetName = targetName,
                    CredentialBlob = Marshal.AllocCoTaskMem(credentialBlob.Length),
                    CredentialBlobSize = (uint)credentialBlob.Length,
                    Persist = NativeMethods.CRED_PERSIST_ENTERPRISE, // Correct user-profile scoping
                    AttributeCount = 0,
                    Attributes = IntPtr.Zero,
                    UserName = Environment.UserName // Meaningful metadata for Windows UI
                };

                Marshal.Copy(credentialBlob, 0, credential.CredentialBlob, credentialBlob.Length);

                try
                {
                    if (!NativeMethods.CredWrite(ref credential, 0))
                    {
                        int error = Marshal.GetLastWin32Error();
                        throw new InvalidOperationException($"Failed to write to Windows Vault. Win32 Error: {error}");
                    }
                }
                finally
                {
                    if (credential.CredentialBlob != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(credential.CredentialBlob);
                }
            }
            finally
            {
                Lock.Release();
            }
        });
    }

    public Task<string?> LoadAsync(string key)
    {
        // Bug Fix: Offload to thread pool to prevent blocking UI thread
        return Task.Run(() =>
        {
            Lock.Wait();
            try
            {
                var targetName = TargetPrefix + key;

                if (NativeMethods.CredRead(targetName, NativeMethods.CRED_TYPE_GENERIC, 0, out var credentialPtr))
                {
                    try
                    {
                        var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credentialPtr);
                        var blob = new byte[credential.CredentialBlobSize];
                        Marshal.Copy(credential.CredentialBlob, blob, 0, (int)credential.CredentialBlobSize);
                        return Encoding.UTF8.GetString(blob);
                    }
                    finally
                    {
                        NativeMethods.CredFree(credentialPtr);
                    }
                }

                return (string?)null; // Item not found
            }
            finally
            {
                Lock.Release();
            }
        });
    }

    public Task DeleteAsync(string key)
    {
        // Bug Fix: Offload to thread pool to prevent blocking UI thread
        return Task.Run(() =>
        {
            Lock.Wait();
            try
            {
                var targetName = TargetPrefix + key;
                if (!NativeMethods.CredDelete(targetName, NativeMethods.CRED_TYPE_GENERIC, 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    // If it's already gone, we consider the deletion a success. Any other error (Access Denied, etc.) should be reported.
                    if (error != ERROR_NOT_FOUND)
                    {
                        throw new InvalidOperationException($"Failed to delete from Windows Vault. Win32 Error: {error}");
                    }
                }
            }
            finally
            {
                Lock.Release();
            }
        });
    }

    #region Native Methods (Win32 API)

    private static class NativeMethods
    {
        public const uint CRED_TYPE_GENERIC = 1;
        public const uint CRED_PERSIST_ENTERPRISE = 3;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string targetName, uint type, uint flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern void CredFree(IntPtr buffer);
    }

    #endregion
}