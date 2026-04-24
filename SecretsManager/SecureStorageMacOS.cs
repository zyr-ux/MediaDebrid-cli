using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MediaDebrid_cli.SecretsManager;

[SupportedOSPlatform("macos")]
public sealed class SecureStorageMacOS : ISecureStorage
{
    private const string ServiceName = "MediaDebridService";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task SaveAsync(string key, string value)
    {
        await _lock.WaitAsync();
        try
        {
            using var service = CFString.Create(ServiceName);
            using var account = CFString.Create(key);
            using var secretData = CFData.Create(Encoding.UTF8.GetBytes(value));

            var query = new Dictionary<IntPtr, IntPtr>
            {
                { Native.kSecClass, Native.kSecClassGenericPassword },
                { Native.kSecAttrService, service.Handle },
                { Native.kSecAttrAccount, account.Handle }
            };


            var attributesToUpdate = new Dictionary<IntPtr, IntPtr>
            {
                { Native.kSecValueData, secretData.Handle },
                { Native.kSecAttrAccessible, Native.kSecAttrAccessibleAfterFirstUnlock }
            };

            using var queryDict = CFDictionary.Create(query);
            using var attrDict = CFDictionary.Create(attributesToUpdate);

            var status = Native.SecItemUpdate(queryDict.Handle, attrDict.Handle);

            if (status == ErrSecItemNotFound)
            {
                var addQuery = new Dictionary<IntPtr, IntPtr>(query)
                {
                    { Native.kSecValueData, secretData.Handle },
                    { Native.kSecAttrAccessible, Native.kSecAttrAccessibleAfterFirstUnlock }
                };
                using var addDict = CFDictionary.Create(addQuery);
                status = Native.SecItemAdd(addDict.Handle, IntPtr.Zero);
            }

            if (status != ErrSecSuccess) ThrowKeychainError(status, "Save");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> LoadAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            using var service = CFString.Create(ServiceName);
            using var account = CFString.Create(key);

            var query = new Dictionary<IntPtr, IntPtr>
            {
                { Native.kSecClass, Native.kSecClassGenericPassword },
                { Native.kSecAttrService, service.Handle },
                { Native.kSecAttrAccount, account.Handle },
                { Native.kSecReturnData, Native.kCFBooleanTrue },
                { Native.kSecMatchLimit, Native.kSecMatchLimitOne }
            };

            using var queryDict = CFDictionary.Create(query);
            var status = Native.SecItemCopyMatching(queryDict.Handle, out var resultHandle);

            if (status == ErrSecItemNotFound) return null;
            if (status != ErrSecSuccess) ThrowKeychainError(status, "Load");

            using (var dataHandle = new SafeCFHandle(resultHandle))
            {
                return CFData.ToString(dataHandle.DangerousGetHandle());
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            using var service = CFString.Create(ServiceName);
            using var account = CFString.Create(key);

            var query = new Dictionary<IntPtr, IntPtr>
            {
                { Native.kSecClass, Native.kSecClassGenericPassword },
                { Native.kSecAttrService, service.Handle },
                { Native.kSecAttrAccount, account.Handle }
            };

            using var queryDict = CFDictionary.Create(query);
            var status = Native.SecItemDelete(queryDict.Handle);

            if (status != ErrSecSuccess && status != ErrSecItemNotFound)
                ThrowKeychainError(status, "Delete");
        }
        finally
        {
            _lock.Release();
        }
    }

    #region Native P/Invokes

    private static class Native
    {
        private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";

        private const string CoreFoundationFramework =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        // Constants
        public static readonly IntPtr kSecClass;
        public static readonly IntPtr kSecClassGenericPassword;
        public static readonly IntPtr kSecAttrService;
        public static readonly IntPtr kSecAttrAccount;
        public static readonly IntPtr kSecValueData;
        public static readonly IntPtr kSecReturnData;
        public static readonly IntPtr kSecMatchLimit;
        public static readonly IntPtr kSecMatchLimitOne;
        public static readonly IntPtr kSecAttrAccessible;
        public static readonly IntPtr kSecAttrAccessibleAfterFirstUnlock;
        public static readonly IntPtr kCFBooleanTrue;

        static Native()
        {
            var securityLib = dlopen(SecurityFramework, 2);
            var cfLib = dlopen(CoreFoundationFramework, 2);

            if (securityLib == IntPtr.Zero || cfLib == IntPtr.Zero)
                throw new PlatformNotSupportedException("Could not load macOS Security/CoreFoundation frameworks.");

            kSecClass = GetSymbol(securityLib, "kSecClass");
            kSecClassGenericPassword = GetSymbol(securityLib, "kSecClassGenericPassword");
            kSecAttrService = GetSymbol(securityLib, "kSecAttrService");
            kSecAttrAccount = GetSymbol(securityLib, "kSecAttrAccount");
            kSecValueData = GetSymbol(securityLib, "kSecValueData");
            kSecReturnData = GetSymbol(securityLib, "kSecReturnData");
            kSecMatchLimit = GetSymbol(securityLib, "kSecMatchLimit");
            kSecMatchLimitOne = GetSymbol(securityLib, "kSecMatchLimitOne");
            kSecAttrAccessible = GetSymbol(securityLib, "kSecAttrAccessible");
            kSecAttrAccessibleAfterFirstUnlock = GetSymbol(securityLib, "kSecAttrAccessibleAfterFirstUnlock");
            kCFBooleanTrue = GetSymbol(cfLib, "kCFBooleanTrue");

            dlclose(securityLib);
            dlclose(cfLib);
        }

        [DllImport(CoreFoundationFramework)]
        public static extern void CFRelease(IntPtr obj);

        [DllImport(CoreFoundationFramework)]
        public static extern IntPtr CFStringCreateWithCharacters(IntPtr alloc,
            [MarshalAs(UnmanagedType.LPWStr)] string str, nint len);

        [DllImport(CoreFoundationFramework)]
        public static extern nint CFStringGetLength(IntPtr handle);

        [DllImport(CoreFoundationFramework)]
        public static extern IntPtr CFStringGetCharactersPtr(IntPtr handle);

        [DllImport(CoreFoundationFramework)]
        public static extern void CFStringGetCharacters(IntPtr handle, CFRange range, byte[] buffer);

        [DllImport(CoreFoundationFramework)]
        public static extern IntPtr CFDataCreate(IntPtr alloc, byte[] bytes, nint len);

        [DllImport(CoreFoundationFramework)]
        public static extern nint CFDataGetLength(IntPtr handle);

        [DllImport(CoreFoundationFramework)]
        public static extern IntPtr CFDataGetBytePtr(IntPtr handle);

        [DllImport(CoreFoundationFramework)]
        public static extern IntPtr CFDictionaryCreate(IntPtr alloc, IntPtr[] keys, IntPtr[] values, nint count,
            IntPtr keyCallbacks, IntPtr valCallbacks);

        [DllImport(SecurityFramework)]
        public static extern int SecItemAdd(IntPtr attributes, IntPtr result);

        [DllImport(SecurityFramework)]
        public static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

        [DllImport(SecurityFramework)]
        public static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

        [DllImport(SecurityFramework)]
        public static extern int SecItemDelete(IntPtr query);

        [DllImport(SecurityFramework)]
        public static extern IntPtr SecCopyErrorMessageString(int status, IntPtr reserved);

        private static IntPtr GetSymbol(IntPtr lib, string name)
        {
            var sym = dlsym(lib, name);
            return sym == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(sym);
        }

        [DllImport("libSystem.dylib")]
        private static extern IntPtr dlopen(string path, int mode);

        [DllImport("libSystem.dylib")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libSystem.dylib")]
        private static extern int dlclose(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        public struct CFRange(nint loc, nint len)
        {
            public nint Location = loc;
            public nint Length = len;
        }
    }

    #endregion

    #region Wrapper Helpers

    private static void ThrowKeychainError(int status, string operation)
    {
        var msgHandle = Native.SecCopyErrorMessageString(status, IntPtr.Zero);
        var detail = msgHandle != IntPtr.Zero ? CFString.InternalToString(msgHandle) : "Unknown Keychain Error";
        if (msgHandle != IntPtr.Zero) Native.CFRelease(msgHandle);

        throw new InvalidOperationException($"macOS Keychain {operation} failed: {detail} (Status: {status})");
    }

    private sealed class SafeCFHandle(IntPtr cfHandle) : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        public IntPtr Handle => cfHandle;

        protected override bool ReleaseHandle()
        {
            Native.CFRelease(cfHandle);
            return true;
        }
    }

    private static class CFString
    {
        public static SafeCFHandle Create(string s)
        {
            var handle = Native.CFStringCreateWithCharacters(IntPtr.Zero, s, s.Length);
            return handle == IntPtr.Zero ? throw new OutOfMemoryException() : new SafeCFHandle(handle);
        }

        public static string InternalToString(IntPtr handle)
        {
            var length = Native.CFStringGetLength(handle);
            var buffer = Native.CFStringGetCharactersPtr(handle);
            if (buffer == IntPtr.Zero)
            {
                var bytes = new byte[length * 2];
                Native.CFStringGetCharacters(handle, new Native.CFRange(0, length), bytes);
                return Encoding.Unicode.GetString(bytes);
            }

            return Marshal.PtrToStringUni(buffer, (int)length)!;
        }
    }

    private static class CFData
    {
        public static SafeCFHandle Create(byte[] data)
        {
            return new SafeCFHandle(Native.CFDataCreate(IntPtr.Zero, data, data.Length));
        }

        public static string ToString(IntPtr handle)
        {
            var length = Native.CFDataGetLength(handle);
            var ptr = Native.CFDataGetBytePtr(handle);
            var buffer = new byte[length];
            Marshal.Copy(ptr, buffer, 0, (int)length);
            return Encoding.UTF8.GetString(buffer);
        }
    }

    private static class CFDictionary
    {
        public static SafeCFHandle Create(Dictionary<IntPtr, IntPtr> items)
        {
            var keys = new IntPtr[items.Count];
            var values = new IntPtr[items.Count];
            var i = 0;
            foreach (var kvp in items)
            {
                keys[i] = kvp.Key;
                values[i] = kvp.Value;
                i++;
            }

            return new SafeCFHandle(Native.CFDictionaryCreate(IntPtr.Zero, keys, values, items.Count, IntPtr.Zero,
                IntPtr.Zero));
        }
    }

    #endregion
}