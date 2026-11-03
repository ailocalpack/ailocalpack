using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Services;

/// <summary>Hands things to the OS (file manager); behind an interface so view models are tested without side effects.</summary>
public interface ISystemShell
{
    /// <summary>Opens <paramref name="path"/> in the OS file manager (Explorer, Finder, the Linux default). False when it couldn't.</summary>
    bool OpenFolder(string path);
}

internal sealed class SystemShell(ILogger<SystemShell> logger) : ISystemShell
{
    public bool OpenFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Directory.Exists(path))
        {
            logger.LogWarning("Can't open folder {Path}: it does not exist", path);
            return false;
        }

        try
        {
            // Shell execute opens a folder in Explorer / Finder, and through xdg-open on Linux.
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            logger.LogError(e, "Can't open folder {Path}", path);
            return false;
        }
    }
}
