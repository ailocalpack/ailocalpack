using System.Security;
using Microsoft.Win32;

namespace AiLocalPack.Core.Steam;

/// <summary>Reads string values from the Windows registry. Tests pass a fake; off Windows the real one returns nothing.</summary>
public interface IWindowsRegistry
{
    /// <summary>A string value under <c>HKEY_CURRENT_USER\<paramref name="keyPath"/></c>, or <c>null</c> when missing or unreadable.</summary>
    string? GetCurrentUserString(string keyPath, string valueName);
}

/// <summary>Also writes string values under <c>HKEY_CURRENT_USER</c> (the autostart Run value, notification registration).</summary>
public interface IWindowsRegistryWriter : IWindowsRegistry
{
    /// <summary>Sets a string value, creating the key; <c>false</c> when it can't be written (or not on Windows).</summary>
    bool SetCurrentUserString(string keyPath, string valueName, string value);

    /// <summary>Removes a value; <c>true</c> when it is gone (also when it never existed), <c>false</c> on failure.</summary>
    bool DeleteCurrentUserValue(string keyPath, string valueName);
}

/// <summary>The machine's registry. Never throws: a missing key, a denied read or a non-Windows OS give <c>null</c> / <c>false</c>.</summary>
public sealed class WindowsRegistry : IWindowsRegistryWriter
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
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public bool SetCurrentUserString(string keyPath, string valueName, string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key.SetValue(valueName, value, RegistryValueKind.String);
            return true;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public bool DeleteCurrentUserValue(string keyPath, string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
