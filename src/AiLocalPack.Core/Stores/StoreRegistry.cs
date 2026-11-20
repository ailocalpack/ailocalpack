using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace AiLocalPack.Core.Stores;

/// <summary>The registry hive a store keeps its records in.</summary>
public enum RegistryHiveKind
{
    LocalMachine,
    CurrentUser,
}

/// <summary>
/// Reads what game stores write into the Windows registry (GOG's games, the EA app's uninstall entries, Epic's metadata
/// folder). Never throws: a missing key, a denied read or a non-Windows OS give nothing.
/// </summary>
public interface IStoreRegistry
{
    /// <summary>Sub-key names of <paramref name="keyPath"/>; empty when the key is missing.</summary>
    /// <param name="registry32">The 32-bit view (<c>WOW6432Node</c> on 64-bit Windows) instead of the 64-bit one.</param>
    IReadOnlyList<string> SubKeyNames(RegistryHiveKind hive, string keyPath, bool registry32);

    /// <summary>A string value (an expandable one expanded, a number as text), or <c>null</c>.</summary>
    string? GetString(RegistryHiveKind hive, string keyPath, string valueName, bool registry32);
}

/// <summary>The machine's registry.</summary>
public sealed class WindowsStoreRegistry : IStoreRegistry
{
    public static WindowsStoreRegistry Instance { get; } = new();

    private WindowsStoreRegistry()
    {
    }

    public IReadOnlyList<string> SubKeyNames(RegistryHiveKind hive, string keyPath, bool registry32)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            using var key = Open(hive, keyPath, registry32);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public string? GetString(RegistryHiveKind hive, string keyPath, string valueName, bool registry32)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Open(hive, keyPath, registry32);
            return key?.GetValue(valueName) switch
            {
                string text => text,
                int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static RegistryKey? Open(RegistryHiveKind hive, string keyPath, bool registry32)
    {
        using var root = RegistryKey.OpenBaseKey(
            hive == RegistryHiveKind.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            registry32 ? RegistryView.Registry32 : RegistryView.Registry64);
        return root.OpenSubKey(keyPath);
    }
}

/// <summary>
/// A registry read from a JSON file (dev, tests; <see cref="StoreLocations.RootVariable"/>): an object of key paths
/// <c>HKLM\…</c> / <c>HKCU\…</c> (both registry views see the same keys; a path under <c>HKLM32\…</c> or <c>HKLM64\…</c>
/// only that view) → an object of string values. Sub-keys are the paths one level below. A missing or broken file reads
/// as an empty registry.
/// </summary>
public sealed class FileStoreRegistry : IStoreRegistry
{
    private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public FileStoreRegistry(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var json = JsonDocument.Parse(stream);
            foreach (var key in json.RootElement.EnumerateObject())
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (key.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var value in key.Value.EnumerateObject())
                    {
                        values[value.Name] = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : value.Value.GetRawText();
                    }
                }

                _keys[key.Name.TrimEnd('\\')] = values;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _keys.Clear();
        }
    }

    public IReadOnlyList<string> SubKeyNames(RegistryHiveKind hive, string keyPath, bool registry32)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in Prefixes(hive, keyPath, registry32))
        {
            foreach (var path in _keys.Keys)
            {
                if (path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(path[(prefix.Length + 1)..].Split('\\')[0]);
                }
            }
        }

        return [.. names];
    }

    public string? GetString(RegistryHiveKind hive, string keyPath, string valueName, bool registry32)
    {
        foreach (var prefix in Prefixes(hive, keyPath, registry32))
        {
            if (_keys.TryGetValue(prefix, out var values) && values.TryGetValue(valueName, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static IEnumerable<string> Prefixes(RegistryHiveKind hive, string keyPath, bool registry32)
    {
        var name = hive == RegistryHiveKind.LocalMachine ? "HKLM" : "HKCU";
        var path = keyPath.Trim('\\');
        yield return $"{name}\\{path}";
        yield return $"{name}{(registry32 ? "32" : "64")}\\{path}";
    }
}
