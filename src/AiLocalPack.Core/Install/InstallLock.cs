using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Install;

/// <summary>
/// The lock that lets one change of game files or backups run at a time across app and CLI (<c>&lt;data&gt;/install.lock</c>
/// opened <see cref="FileShare.None"/>): installs, re-applies and uninstalls (<see cref="PackInstaller"/>), moving the
/// backups (<see cref="BackupLocation"/>) and clearing the cache (<see cref="AppCache"/>) take it.
/// </summary>
internal static class InstallLock
{
    /// <summary>The held lock (dispose to release); <c>null</c> when another transaction holds it.</summary>
    public static FileStream? TryAcquire(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Data);
        try
        {
            return new FileStream(paths.InstallLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
