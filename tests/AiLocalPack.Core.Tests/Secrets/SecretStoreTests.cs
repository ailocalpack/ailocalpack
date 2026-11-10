using AiLocalPack.Core.Secrets;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Secrets;

public sealed class SecretStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static OsEnvironment Env(OsKind os, Dictionary<string, string>? variables = null) =>
        new(os, "/home/player", name => variables?.GetValueOrDefault(name));

    private FileSecretStore FileStore(string service = "ailocalpack") => new(_dir.Combine("secrets.json"), service);

    [Fact]
    public void The_real_app_files_its_secrets_under_ailocalpack()
    {
        // Act
        var service = SecretStores.Service(Env(OsKind.Linux));

        // Assert
        service.Should().Be("ailocalpack");
    }

    [Fact]
    public void A_dev_home_gets_its_own_service_so_it_never_touches_the_real_sign_in()
    {
        // Arrange
        var home = _dir.Combine("home");

        // Act
        var service = SecretStores.Service(Env(OsKind.Linux, new() { [AppPaths.HomeVariable] = home }));

        // Assert
        service.Should().Be($"ailocalpack ({home})");
    }

    [Theory]
    [InlineData(OsKind.Windows, SecretStoreKind.WindowsCredentialManager)]
    [InlineData(OsKind.MacOS, SecretStoreKind.MacKeychain)]
    [InlineData(OsKind.Linux, SecretStoreKind.SecretService)]
    public void Each_os_gets_its_own_keychain(OsKind os, SecretStoreKind expected)
    {
        // Act
        var store = SecretStores.Create(Env(os));

        // Assert
        store.Kind.Should().Be(expected);
    }

    [Fact]
    public void The_secrets_file_variable_picks_the_dev_file_store()
    {
        // Act
        var store = SecretStores.Create(Env(OsKind.MacOS, new() { [SecretStores.FileVariable] = _dir.Combine("secrets.json") }));

        // Assert
        store.Kind.Should().Be(SecretStoreKind.File);
    }

    [Fact]
    public async Task Another_os_keychain_is_unavailable_here()
    {
        // Arrange
        ISecretStore store = OperatingSystem.IsWindows() ? new MacKeychainStore("ailocalpack") : new WindowsCredentialStore("ailocalpack");

        // Act
        var result = await store.ReadAsync("github-token", TestContext.Current.CancellationToken);

        // Assert
        result.Status.Should().Be(SecretStatus.Unavailable);
    }

    [Fact]
    public async Task The_file_store_reads_what_it_wrote()
    {
        // Arrange
        await FileStore().WriteAsync("github-token", "gho_secret", TestContext.Current.CancellationToken);

        // Act
        var result = await FileStore().ReadAsync("github-token", TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(SecretResult.Ok("gho_secret"));
    }

    [Fact]
    public async Task The_file_store_keeps_services_apart()
    {
        // Arrange
        await FileStore("ailocalpack (dev)").WriteAsync("github-token", "gho_dev", TestContext.Current.CancellationToken);

        // Act
        var result = await FileStore().ReadAsync("github-token", TestContext.Current.CancellationToken);

        // Assert
        result.Status.Should().Be(SecretStatus.NotFound);
    }

    [Fact]
    public async Task The_file_store_forgets_a_deleted_secret()
    {
        // Arrange
        await FileStore().WriteAsync("github-token", "gho_secret", TestContext.Current.CancellationToken);
        await FileStore().DeleteAsync("github-token", TestContext.Current.CancellationToken);

        // Act
        var result = await FileStore().ReadAsync("github-token", TestContext.Current.CancellationToken);

        // Assert
        result.Status.Should().Be(SecretStatus.NotFound);
    }

    [Fact]
    public async Task Deleting_a_missing_secret_is_fine()
    {
        // Act
        var result = await FileStore().DeleteAsync("github-token", TestContext.Current.CancellationToken);

        // Assert
        result.IsOk.Should().BeTrue();
    }

    [Fact]
    public async Task A_damaged_secrets_file_reads_as_empty()
    {
        // Arrange
        _dir.File("secrets.json", "{ damaged");

        // Act
        var result = await FileStore().ReadAsync("github-token", TestContext.Current.CancellationToken);

        // Assert
        result.Status.Should().Be(SecretStatus.NotFound);
    }
}
