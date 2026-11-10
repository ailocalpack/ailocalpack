namespace AiLocalPack.Core.Secrets;

/// <summary>Where secrets are kept (the Settings → GitHub keychain note names it).</summary>
public enum SecretStoreKind
{
    /// <summary>Windows Credential Manager (generic credentials of the signed-in user).</summary>
    WindowsCredentialManager,

    /// <summary>macOS Keychain (the login keychain).</summary>
    MacKeychain,

    /// <summary>Linux: the freedesktop Secret Service over D-Bus (GNOME Keyring, KWallet, KeePassXC).</summary>
    SecretService,

    /// <summary>A plain-text JSON file named by <c>AILOCALPACK_SECRETS_FILE</c>: development and tests only.</summary>
    File,

    /// <summary>In memory (tests).</summary>
    Memory,
}

public enum SecretStatus
{
    Ok,

    /// <summary>No secret by that name (a delete of a missing secret is <see cref="Ok"/>).</summary>
    NotFound,

    /// <summary>
    /// There is no store to use: Linux without a running Secret Service, a locked keychain without a way to ask the
    /// player. Nothing was saved (secrets are never put in plain text instead).
    /// </summary>
    Unavailable,

    /// <summary>The store refused or failed (the player declined the unlock prompt, an OS error).</summary>
    Failed,
}

/// <param name="Value">The secret, for a read that found it.</param>
/// <param name="Problem">For <see cref="SecretStatus.Unavailable"/> / <see cref="SecretStatus.Failed"/>: what went wrong, in English (logs, CLI).</param>
public sealed record SecretResult(SecretStatus Status, string? Value = null, string? Problem = null)
{
    public static SecretResult Ok(string? value = null) => new(SecretStatus.Ok, value);

    public static SecretResult NotFound { get; } = new(SecretStatus.NotFound);

    public static SecretResult Unavailable(string problem) => new(SecretStatus.Unavailable, Problem: problem);

    public static SecretResult Failed(string problem) => new(SecretStatus.Failed, Problem: problem);

    public bool IsOk => Status == SecretStatus.Ok;

    public override string ToString() => Problem is null ? Status.ToString() : $"{Status}: {Problem}";
}

/// <summary>
/// The OS keychain: small secrets (the GitHub token) by name, under the app's service name (<see cref="SecretStores"/>).
/// Never throws for store problems (only for a cancelled token); they come back as <see cref="SecretResult"/>.
/// </summary>
public interface ISecretStore
{
    SecretStoreKind Kind { get; }

    Task<SecretResult> ReadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the secret.</summary>
    Task<SecretResult> WriteAsync(string name, string value, CancellationToken cancellationToken = default);

    /// <summary>Removes the secret; <see cref="SecretStatus.Ok"/> also when there was none.</summary>
    Task<SecretResult> DeleteAsync(string name, CancellationToken cancellationToken = default);
}
