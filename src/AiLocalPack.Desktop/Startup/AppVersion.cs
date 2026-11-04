using System.Reflection;

namespace AiLocalPack.Desktop.Startup;

/// <summary>The app's version as shown to the player ("0.4.0"; build metadata after <c>+</c> left out).</summary>
public static class AppVersion
{
    public static string Current { get; } = FromAssembly(typeof(AppVersion).Assembly);

    internal static string FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
