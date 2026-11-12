using AiLocalPack.Core.Secrets;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Secrets;

/// <summary>
/// This machine's real keychain (Windows Credential Manager, macOS Keychain, Linux Secret Service), under a throwaway
/// service name. Runs only with <c>AILOCALPACK_KEYCHAIN_TESTS=1</c>: CI sets it on all three OSes (Linux under a
/// D-Bus session with an unlocked gnome-keyring); a developer machine would show keychain prompts.
/// </summary>
public sealed class KeychainSmokeTests
{
    private static readonly string Service = $"ailocalpack-smoke-{Guid.NewGuid():N}";

    private static ISecretStore Store() => OsEnvironment.Current.Os switch
    {
        OsKind.Windows => new WindowsCredentialStore(Service),
        OsKind.MacOS => new MacKeychainStore(Service),
        _ => new SecretServiceStore(Service, new TestLogger<SecretServiceStore>()),
    };

    public static bool KeychainTestsOn => Environment.GetEnvironmentVariable("AILOCALPACK_KEYCHAIN_TESTS") == "1";

    [Fact(SkipUnless = nameof(KeychainTestsOn), Skip = "Needs the OS keychain: set AILOCALPACK_KEYCHAIN_TESTS=1 (CI does)")]
    public async Task Writes_replaces_reads_and_deletes_a_secret()
    {
        // Arrange
        var store = Store();
        var cancel = TestContext.Current.CancellationToken;
        var steps = new List<SecretResult>();

        // Act
        steps.Add(await store.WriteAsync("token", "first", cancel));
        steps.Add(await store.WriteAsync("token", "second ✓", cancel));
        steps.Add(await store.ReadAsync("token", cancel));
        steps.Add(await store.DeleteAsync("token", cancel));
        steps.Add(await store.ReadAsync("token", cancel));
        steps.Add(await store.DeleteAsync("token", cancel));

        // Assert
        steps.Select(s => $"{s} {s.Value}".Trim()).Should().Equal("Ok", "Ok", "Ok second ✓", "Ok", "NotFound", "Ok");
    }
}
