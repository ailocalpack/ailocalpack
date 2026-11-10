using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AiLocalPack.Core.Secrets;

/// <summary>
/// Windows Credential Manager: one generic credential per secret, target <c>&lt;service&gt;:&lt;name&gt;</c>, persisted for
/// the signed-in user on this machine (<c>CRED_PERSIST_LOCAL_MACHINE</c>: not roamed with the profile); the secret is the
/// UTF-8 credential blob. Shows in Control Panel → Credential Manager → Windows Credentials.
/// </summary>
public sealed class WindowsCredentialStore(string service) : ISecretStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int ErrorNoSuchLogonSession = 1312;

    /// <summary>Windows refuses a bigger blob (<c>CRED_MAX_CREDENTIAL_BLOB_SIZE</c>).</summary>
    private const int MaxBlobBytes = 5 * 512;

    private readonly string _service = string.IsNullOrWhiteSpace(service) ? throw new ArgumentException("A service name is required.", nameof(service)) : service;

    public SecretStoreKind Kind => SecretStoreKind.WindowsCredentialManager;

    public Task<SecretResult> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Task.FromResult(OperatingSystem.IsWindows() ? Read(Target(name)) : NotWindows());
    }

    public Task<SecretResult> WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        return Task.FromResult(OperatingSystem.IsWindows() ? Write(Target(name), value) : NotWindows());
    }

    public Task<SecretResult> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Task.FromResult(OperatingSystem.IsWindows() ? Delete(Target(name)) : NotWindows());
    }

    private static SecretResult NotWindows() => SecretResult.Unavailable("Windows Credential Manager exists only on Windows.");

    private string Target(string name) => $"{_service}:{name}";

    [SupportedOSPlatform("windows")]
    private static SecretResult Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound ? SecretResult.NotFound : Problem("read", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return SecretResult.Ok(string.Empty);
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                return SecretResult.Ok(new UTF8Encoding(false, true).GetString(bytes));
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            CredFree(handle);
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecretResult Write(string target, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxBlobBytes)
        {
            return SecretResult.Failed($"The secret is longer than the {MaxBlobBytes} bytes Credential Manager keeps.");
        }

        var blob = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        var targetName = Marshal.StringToHGlobalUni(target);
        var userName = Marshal.StringToHGlobalUni(Environment.UserName);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = userName,
            };
            return CredWrite(ref credential, 0) ? SecretResult.Ok() : Problem("write", Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(targetName);
            Marshal.FreeHGlobal(userName);
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecretResult Delete(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0))
        {
            return SecretResult.Ok();
        }

        var error = Marshal.GetLastWin32Error();
        return error == ErrorNotFound ? SecretResult.Ok() : Problem("delete", error);
    }

    private static SecretResult Problem(string action, int error)
    {
        var message = $"Credential Manager couldn't {action} the secret: {new Win32Exception(error).Message} ({error}).";

        // No logon session: a service account or a session without a user profile, where there is no vault at all.
        return error == ErrorNoSuchLogonSession ? SecretResult.Unavailable(message) : SecretResult.Failed(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void CredFree(IntPtr buffer);
}
