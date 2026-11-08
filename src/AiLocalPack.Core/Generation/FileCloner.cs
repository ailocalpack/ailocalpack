using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// Copy-on-write clones of files (a clone shares the source's blocks until one of them changes, so it takes no disk
/// space and no time). Behind an interface so working copies are tested with and without it.
/// </summary>
public interface IFileCloner
{
    /// <summary>
    /// Clones <paramref name="source"/> into <paramref name="destination"/> (which must not exist), contents, times and
    /// permissions included. <c>false</c> = the file system can't (another volume, no clone support) and nothing was
    /// left at <paramref name="destination"/>; never throws for disk state.
    /// </summary>
    bool TryClone(string source, string destination);
}

/// <summary>
/// The OS's clone call: macOS APFS <c>clonefile</c>; Linux <c>ioctl(FICLONE)</c> (Btrfs, XFS with reflinks,
/// bcachefs); Windows none (copies go through <c>CopyFileEx</c>, which on Windows 11 24H2+ block-clones on ReFS and
/// Dev Drive by itself, but can't tell, so the disk check counts a full copy there).
/// </summary>
public sealed class FileCloner : IFileCloner
{
    private const uint CloneNoFollow = 0x0001;
    private const ulong FiClone = 0x40049409;

    public static IFileCloner Create() => new FileCloner();

    public bool TryClone(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return MacClone(source, destination, CloneNoFollow) == 0;
            }

            if (OperatingSystem.IsLinux())
            {
                return LinuxClone(source, destination);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            // No clone: the caller copies instead.
        }

        return false;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static bool LinuxClone(string source, string destination)
    {
        var cloned = false;
        var created = false;
        try
        {
            using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var to = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                cloned = Ioctl(Fd(to.SafeFileHandle), FiClone, Fd(from.SafeFileHandle)) == 0;
            }

            if (cloned)
            {
                File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            }

            return cloned;
        }
        finally
        {
            if (created && !cloned)
            {
                TryDelete(destination);
            }
        }
    }

    private static int Fd(SafeFileHandle handle) => (int)handle.DangerousGetHandle();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing was cloned; a stray empty file is removed with the working copy.
        }
    }

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "clonefile", SetLastError = true)]
    private static extern int MacClone([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, ulong request, int argument);
}
