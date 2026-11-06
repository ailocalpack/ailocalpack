using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Install;

/// <summary>Turns the OS errors of file operations into <see cref="InstallProblem"/>s.</summary>
public static class InstallErrors
{
    // Windows Win32 codes (in the low word of an HRESULT 0x8007xxxx).
    private const int ErrorAccessDenied = 5;
    private const int ErrorWriteProtect = 19;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorDiskFull = 112;
    private const int ErrorUserMappedFile = 1224;

    // Unix errno values, which .NET puts in IOException.HResult on Linux and macOS.
    private const int Eperm = 1;
    private const int Eacces = 13;
    private const int Ebusy = 16;
    private const int Etxtbsy = 26;
    private const int Enospc = 28;
    private const int Erofs = 30;
    private const int EdquotMac = 69;
    private const int EdquotLinux = 122;

    /// <summary>The problem behind <paramref name="exception"/> thrown on <paramref name="path"/> (a full path, may be <c>null</c>).</summary>
    public static InstallProblem Classify(Exception exception, string? path) => Classify(exception, path, OsEnvironment.Current.Os);

    internal static InstallProblem Classify(Exception exception, string? path, OsKind os)
    {
        ArgumentNullException.ThrowIfNull(exception);
        switch (exception)
        {
            case UnauthorizedAccessException:
                return path is not null && IsReadOnly(path) ? InstallProblem.ReadOnly : InstallProblem.PermissionDenied;
            case IOException io when os == OsKind.Windows:
                return (io.HResult & 0xFFFF) switch
                {
                    ErrorSharingViolation or ErrorLockViolation or ErrorUserMappedFile => InstallProblem.FileInUse,
                    ErrorDiskFull or ErrorHandleDiskFull => InstallProblem.DiskFull,
                    ErrorWriteProtect => InstallProblem.ReadOnly,
                    ErrorAccessDenied => InstallProblem.PermissionDenied,
                    _ => InstallProblem.IoError,
                };
            case IOException io:
                return io.HResult switch
                {
                    Etxtbsy or Ebusy => InstallProblem.FileInUse,
                    Enospc => InstallProblem.DiskFull,
                    EdquotLinux when os == OsKind.Linux => InstallProblem.DiskFull,
                    EdquotMac when os == OsKind.MacOS => InstallProblem.DiskFull,
                    Erofs => InstallProblem.ReadOnly,
                    Eacces or Eperm => InstallProblem.PermissionDenied,
                    _ => InstallProblem.IoError,
                };
            default:
                return InstallProblem.IoError;
        }
    }

    /// <summary>
    /// The file is read-only: the Windows attribute, or no write permission for its owner on Linux / macOS (where a
    /// rename would replace it anyway, so the install checks it first). Never throws.
    /// </summary>
    public static bool IsReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                return File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
            }

            return (File.GetUnixFileMode(path) & UnixFileMode.UserWrite) == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
