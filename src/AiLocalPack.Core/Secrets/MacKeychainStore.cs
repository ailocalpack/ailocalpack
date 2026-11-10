using System.Runtime.InteropServices;
using System.Text;

namespace AiLocalPack.Core.Secrets;

/// <summary>
/// macOS Keychain: one generic password per secret (service = the app's service name, account = the secret's name) in
/// the default (login) keychain, through Security.framework's generic-password calls (the same ones Git Credential
/// Manager uses). The item's access list names the program that created it; another build of the app (an update, the
/// CLI) reading it gets the system's "allow access" prompt once. Without a GUI session (SSH) a locked keychain is
/// <see cref="SecretStatus.Unavailable"/>.
/// </summary>
public sealed class MacKeychainStore(string service) : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;
    private const int ErrSecInteractionNotAllowed = -25308;
    private const int ErrSecNoDefaultKeychain = -25307;
    private const int ErrSecAuthFailed = -25293;
    private const int ErrSecUserCanceled = -128;

    private readonly byte[] _service = Encoding.UTF8.GetBytes(
        string.IsNullOrWhiteSpace(service) ? throw new ArgumentException("A service name is required.", nameof(service)) : service);

    public SecretStoreKind Kind => SecretStoreKind.MacKeychain;

    public Task<SecretResult> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Task.FromResult(OperatingSystem.IsMacOS() ? Read(Encoding.UTF8.GetBytes(name)) : NotMac());
    }

    public Task<SecretResult> WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        return Task.FromResult(OperatingSystem.IsMacOS() ? Write(Encoding.UTF8.GetBytes(name), value) : NotMac());
    }

    public Task<SecretResult> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Task.FromResult(OperatingSystem.IsMacOS() ? Delete(Encoding.UTF8.GetBytes(name)) : NotMac());
    }

    private static SecretResult NotMac() => SecretResult.Unavailable("The macOS Keychain exists only on macOS.");

    private SecretResult Read(byte[] account)
    {
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero, (uint)_service.Length, _service, (uint)account.Length, account, out var length, out var data, out var item);
        if (status != ErrSecSuccess)
        {
            return status == ErrSecItemNotFound ? SecretResult.NotFound : Problem("read", status);
        }

        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            try
            {
                return SecretResult.Ok(new UTF8Encoding(false, true).GetString(bytes));
            }
            catch (DecoderFallbackException)
            {
                return SecretResult.Failed("The keychain item isn't UTF-8 text.");
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            Release(item);
        }
    }

    private SecretResult Write(byte[] account, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            var status = FindItem(account, out var item);
            if (status == ErrSecSuccess)
            {
                try
                {
                    status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)bytes.Length, bytes);
                    return status == ErrSecSuccess ? SecretResult.Ok() : Problem("update", status);
                }
                finally
                {
                    Release(item);
                }
            }

            if (status != ErrSecItemNotFound)
            {
                return Problem("read", status);
            }

            status = SecKeychainAddGenericPassword(
                IntPtr.Zero, (uint)_service.Length, _service, (uint)account.Length, account, (uint)bytes.Length, bytes, IntPtr.Zero);
            return status switch
            {
                ErrSecSuccess => SecretResult.Ok(),

                // Added by another process between the find and the add: the next write updates it.
                ErrSecDuplicateItem => SecretResult.Failed("The keychain item was created at the same time by another program; try again."),
                _ => Problem("save", status),
            };
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private SecretResult Delete(byte[] account)
    {
        var status = FindItem(account, out var item);
        if (status == ErrSecItemNotFound)
        {
            return SecretResult.Ok();
        }

        if (status != ErrSecSuccess)
        {
            return Problem("read", status);
        }

        try
        {
            status = SecKeychainItemDelete(item);
            return status is ErrSecSuccess or ErrSecItemNotFound ? SecretResult.Ok() : Problem("delete", status);
        }
        finally
        {
            Release(item);
        }
    }

    /// <summary>Finds the item without reading its secret (no access prompt for that).</summary>
    private int FindItem(byte[] account, out IntPtr item) =>
        SecKeychainFindGenericPasswordItem(IntPtr.Zero, (uint)_service.Length, _service, (uint)account.Length, account, IntPtr.Zero, IntPtr.Zero, out item);

    private static SecretResult Problem(string action, int status)
    {
        var message = $"The keychain couldn't {action} the item: {Describe(status)} (OSStatus {status}).";
        return status is ErrSecInteractionNotAllowed or ErrSecNoDefaultKeychain
            ? SecretResult.Unavailable(message)
            : SecretResult.Failed(message);
    }

    private static string Describe(int status) => status switch
    {
        ErrSecInteractionNotAllowed => "the keychain is locked and can't ask for the password here (no desktop session)",
        ErrSecNoDefaultKeychain => "there is no default keychain",
        ErrSecAuthFailed => "access was refused",
        ErrSecUserCanceled => "the prompt was cancelled",
        _ => CopyMessage(status) ?? "unknown error",
    };

    private static string? CopyMessage(int status)
    {
        var message = SecCopyErrorMessageString(status, IntPtr.Zero);
        if (message == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var length = CFStringGetLength(message);
            var buffer = new char[length];
            CFStringGetCharacters(message, new CFRange(0, length), buffer);
            return new string(buffer);
        }
        finally
        {
            Release(message);
        }
    }

    private static void Release(IntPtr reference)
    {
        if (reference != IntPtr.Zero)
        {
            CFRelease(reference);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CFRange(nint location, nint length)
    {
        public readonly nint Location = location;
        public readonly nint Length = length;
    }

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int SecKeychainFindGenericPasswordItem(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        IntPtr passwordLength,
        IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(Security)]
    private static extern IntPtr SecCopyErrorMessageString(int status, IntPtr reserved);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr reference);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetLength(IntPtr text);

    [DllImport(CoreFoundation)]
    private static extern void CFStringGetCharacters(IntPtr text, CFRange range, [Out] char[] buffer);
}
