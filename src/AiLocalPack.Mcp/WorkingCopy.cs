using AiLocalPack.Core.Storage;
using ModelContextProtocol;

namespace AiLocalPack.Mcp;

/// <summary>
/// The working copy of the game the server is confined to. Every path a tool takes is relative to it, <c>/</c>- or
/// <c>\</c>-separated; absolute paths, drive or UNC names and any <c>..</c> part are refused, and so is a path that
/// passes through a symlink (or junction) leading out of the copy or nowhere.
/// </summary>
public sealed class WorkingCopy
{
    public WorkingCopy(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        RealRoot = PathNames.RealPath(Root);
    }

    /// <summary>The folder as given (absolute).</summary>
    public string Root { get; }

    /// <summary>The folder with symlinks on the way resolved.</summary>
    public string RealRoot { get; }

    /// <summary>The absolute path of <paramref name="relative"/> inside the copy (<c>null</c>, empty or <c>.</c> = the copy itself).</summary>
    /// <exception cref="PathOutsideRootException">The path is absolute, has a <c>..</c> part, or leads out of the copy through a link.</exception>
    public string Resolve(string? relative)
    {
        var text = relative?.Trim() ?? "";
        if (text.Length == 0 || text == ".")
        {
            return Root;
        }

        if (Path.IsPathRooted(text) || text.StartsWith('/') || text.StartsWith('\\') || text.Contains(':', StringComparison.Ordinal))
        {
            throw new PathOutsideRootException(relative!, "absolute paths aren't allowed; give a path relative to the game folder");
        }

        var parts = text.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p == ".."))
        {
            throw new PathOutsideRootException(relative!, "'..' isn't allowed; the tools only see the game folder");
        }

        var current = Root;
        var real = RealRoot;
        foreach (var part in parts.Where(p => p != "."))
        {
            current = Path.Combine(current, part);
            real = Follow(current, real, relative!);
            if (!IsInside(real))
            {
                throw new PathOutsideRootException(relative!, "it leads out of the game folder through a link");
            }
        }

        return current;
    }

    /// <summary><paramref name="full"/> relative to the copy, <c>/</c>-separated (<c>.</c> for the copy itself).</summary>
    public string Relative(string full)
    {
        var relative = Path.GetRelativePath(Root, full).Replace('\\', '/');
        return relative.Length == 0 ? "." : relative;
    }

    /// <summary>An entry found while listing the copy is a link (never followed by the tools).</summary>
    public static bool IsLink(FileSystemInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.LinkTarget is not null;
    }

    private bool IsInside(string realPath) =>
        PathNames.Same(realPath, RealRoot)
        || realPath.StartsWith(RealRoot + Path.DirectorySeparatorChar, PathNames.Comparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>The real path of <paramref name="path"/> whose parent's real path is <paramref name="realParent"/>; a link is followed to its final target.</summary>
    private static string Follow(string path, string realParent, string relative)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is null)
        {
            return Path.Combine(realParent, info.Name);
        }

        FileSystemInfo? target;
        try
        {
            target = info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (IOException)
        {
            target = null;
        }

        if (target is null || !Path.Exists(target.FullName))
        {
            throw new PathOutsideRootException(relative, "it passes through a link whose target is missing");
        }

        return PathNames.RealPath(target.FullName);
    }
}

/// <summary>A tool was given a path outside the working copy; the agent sees the message.</summary>
public sealed class PathOutsideRootException : McpException
{
    public PathOutsideRootException(string path, string reason)
        : base($"Path '{path}' refused: {reason}.")
    {
        RequestedPath = path;
    }

    public PathOutsideRootException()
    {
        RequestedPath = "";
    }

    public PathOutsideRootException(string message)
        : base(message)
    {
        RequestedPath = "";
    }

    public PathOutsideRootException(string message, Exception innerException)
        : base(message, innerException)
    {
        RequestedPath = "";
    }

    public string RequestedPath { get; }
}
