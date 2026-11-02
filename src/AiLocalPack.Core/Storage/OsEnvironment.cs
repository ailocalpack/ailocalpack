using System.Runtime.InteropServices;

namespace AiLocalPack.Core.Storage;

public enum OsKind
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// What path resolution reads from the machine: the OS, the user's home folder and environment variables.
/// Production uses <see cref="Current"/>; tests build one for any OS.
/// </summary>
public sealed class OsEnvironment
{
    private readonly Func<string, string?> _getVariable;

    public OsEnvironment(OsKind os, string homeDirectory, Func<string, string?> getVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArgumentNullException.ThrowIfNull(getVariable);
        Os = os;
        HomeDirectory = homeDirectory;
        _getVariable = getVariable;
    }

    /// <summary>This machine. FreeBSD and other Unix-likes are treated as Linux (XDG).</summary>
    public static OsEnvironment Current { get; } = new(
        OperatingSystem.IsWindows() ? OsKind.Windows
        : OperatingSystem.IsMacOS() ? OsKind.MacOS
        : OsKind.Linux,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
        Environment.GetEnvironmentVariable);

    public OsKind Os { get; }

    public string HomeDirectory { get; }

    /// <summary>The variable's value, or <c>null</c> when it is unset or blank.</summary>
    public string? GetVariable(string name)
    {
        var value = _getVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public override string ToString() => $"{Os} ({RuntimeInformation.OSDescription})";
}
