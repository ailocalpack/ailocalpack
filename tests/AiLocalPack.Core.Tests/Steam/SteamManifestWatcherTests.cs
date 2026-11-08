using AiLocalPack.Core.Steam;

namespace AiLocalPack.Core.Tests.Steam;

/// <summary>App manifest changes in the Steam libraries wake the tray app's re-check (task 39).</summary>
public sealed class SteamManifestWatcherTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Written_manifest_is_reported()
    {
        // Arrange
        using var library = new TempDir().Dir("steamapps").File("steamapps/appmanifest_620.acf", "\"AppState\" {}");
        using var watcher = new SteamManifestWatcher();
        var changed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += (_, path) => changed.TrySetResult(path);
        watcher.Watch([library.Combine("steamapps")]);

        // Act
        await File.WriteAllTextAsync(library.Combine("steamapps/appmanifest_620.acf"), "\"AppState\" { \"buildid\" \"2\" }", TestContext.Current.CancellationToken);

        // Assert
        (await changed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken)).Should().EndWith("appmanifest_620.acf");
    }

    [Fact]
    public void Missing_folder_is_skipped()
    {
        // Arrange
        using var root = new TempDir().Dir("steamapps");
        using var watcher = new SteamManifestWatcher();

        // Act
        watcher.Watch([root.Combine("steamapps"), root.Combine("gone/steamapps")]);

        // Assert
        watcher.Folders.Should().Equal(Path.GetFullPath(root.Combine("steamapps")));
    }

    [Fact]
    public void Watching_a_new_set_drops_the_old_folders()
    {
        // Arrange
        using var root = new TempDir().Dir("a/steamapps").Dir("b/steamapps");
        using var watcher = new SteamManifestWatcher();
        watcher.Watch([root.Combine("a/steamapps")]);

        // Act
        watcher.Watch([root.Combine("b/steamapps")]);

        // Assert
        watcher.Folders.Should().Equal(Path.GetFullPath(root.Combine("b/steamapps")));
    }
}
