using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Secrets;

/// <summary>Picks this machine's <see cref="ISecretStore"/> and the service name secrets are filed under.</summary>
public static class SecretStores
{
    /// <summary>The service (keychain item / credential target prefix) of the player's real app.</summary>
    public const string ServiceName = "ailocalpack";

    /// <summary>
    /// Dev / tests: keep secrets in this plain-text JSON file instead of the OS keychain (never set by the app itself,
    /// so a player's token is never written in plain text).
    /// </summary>
    public const string FileVariable = "AILOCALPACK_SECRETS_FILE";

    /// <summary>
    /// The service name for <paramref name="env"/>: <c>ailocalpack</c>, or with <c>AILOCALPACK_HOME</c> set
    /// <c>ailocalpack (&lt;home&gt;)</c>, so a dev or test home never reads or replaces the real app's secrets.
    /// </summary>
    public static string Service(OsEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return env.GetVariable(AppPaths.HomeVariable) is { } home ? $"{ServiceName} ({Path.GetFullPath(home)})" : ServiceName;
    }

    /// <summary>
    /// The store for this machine: <c>AILOCALPACK_SECRETS_FILE</c> when set, else Windows Credential Manager, the macOS
    /// Keychain or the Linux Secret Service.
    /// </summary>
    public static ISecretStore Create(OsEnvironment env, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(env);
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        var service = Service(env);
        if (env.GetVariable(FileVariable) is { } file)
        {
            return new FileSecretStore(Path.GetFullPath(file), service);
        }

        return env.Os switch
        {
            OsKind.Windows => new WindowsCredentialStore(service),
            OsKind.MacOS => new MacKeychainStore(service),
            _ => new SecretServiceStore(service, factory.CreateLogger<SecretServiceStore>()),
        };
    }
}
