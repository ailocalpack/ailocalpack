using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Services;

/// <summary>Hands things to the OS (file manager, browser); behind an interface so view models are tested without side effects.</summary>
public interface ISystemShell
{
    /// <summary>Opens <paramref name="path"/> in the OS file manager (Explorer, Finder, the Linux default). False when it couldn't.</summary>
    bool OpenFolder(string path);

    /// <summary>Opens an <c>https</c> address in the default browser. False when it couldn't.</summary>
    bool OpenUrl(Uri url);
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

    public bool OpenUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogWarning("Won't open {Url}: only https addresses go to the browser", url);
            return false;
        }

        try
        {
            // Shell execute hands the address to the default browser (open on macOS, xdg-open on Linux).
            using var process = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            logger.LogError(e, "Can't open {Url} in the browser", url);
            return false;
        }
    }
}
