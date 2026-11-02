using System.Security;
using Microsoft.Win32;

namespace AiLocalPack.Core.Steam;

/// <summary>Reads string values from the Windows registry. Tests pass a fake; off Windows the real one returns nothing.</summary>
public interface IWindowsRegistry
{
    /// <summary>A string value under <c>HKEY_CURRENT_USER\<paramref name="keyPath"/></c>, or <c>null</c> when missing or unreadable.</summary>
    string? GetCurrentUserString(string keyPath, string valueName);
}

/// <summary>The machine's registry. Never throws: a missing key, a denied read or a non-Windows OS give <c>null</c>.</summary>
public sealed class WindowsRegistry : IWindowsRegistry
{
    public static WindowsRegistry Instance { get; } = new();

    private WindowsRegistry()
    {
    }

    public string? GetCurrentUserString(string keyPath, string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
