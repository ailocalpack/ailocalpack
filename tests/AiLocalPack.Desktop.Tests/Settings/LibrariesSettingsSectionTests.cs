using AiLocalPack.Core.Games;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.AddGame;
using AiLocalPack.Desktop.ViewModels.Settings;
using AiLocalPack.Desktop.ViewModels.Settings.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Settings;

public sealed class LibrariesSettingsSectionTests : IDisposable
{
    private ServiceProvider? _services;
    private SteamFixture? _steam;

    public void Dispose() => _services?.Dispose();

    /// <summary>A service graph with a fixture Steam built by <paramref name="steam"/> (already scanned once).</summary>
    private LibrariesSettingsSection Section(Action<SteamFixture>? steam = null)
    {
        _services = TestServices.Build(steam: s =>
        {
            _steam = s;
            steam?.Invoke(s);
        });
        return _services.GetRequiredService<LibrariesSettingsSection>();
    }

    /// <summary>A graph where Steam is not found.</summary>
    private LibrariesSettingsSection SectionWithoutSteam()
    {
        _services = TestServices.Build();
        _services.GetRequiredService<GameLibrary>().Refresh();
        return _services.GetRequiredService<LibrariesSettingsSection>();
    }

    private T Get<T>() where T : notnull => _services!.GetRequiredService<T>();

    private string Dir(string relativePath) => Get<TempDir>().Dir(relativePath).Combine(relativePath);

    /// <summary>A Steam library folder outside Steam's list, with one game.</summary>
    private string ExtraLibrary(string relativePath = "Extra", uint appId = 30, string name = "Gamma")
    {
        Dir($"{relativePath}/steamapps");
        var path = Get<TempDir>().Combine(relativePath);
        _steam!.AddGame(appId, name, library: path);
        return path;
    }

    private Game AddManual(string name = "Wren and Whistle")
    {
        var folder = Dir($"games/{name}");
        var game = TestGames.Manual(name, engine: "renpy") with { InstallPath = folder };
        Get<GameCatalog>().AddManual(game);
        return game;
    }

    private ConfirmDialogViewModel Confirmation() => Get<IDialogService>().Current.Should().BeOfType<ConfirmDialogViewModel>().Subject;

    [Fact]
    public void Lists_steam_library_folders_with_their_game_counts()
    {
        // Arrange
        string sdCard = "";

        // Act
        var section = Section(s =>
        {
            sdCard = s.AddLibrary("SdCard");
            s.AddGame(10, "Alpha").AddGame(20, "Beta", library: sdCard).AddGame(21, "Gamma", library: sdCard);
        });

        // Assert
        section.Folders.Select(f => (f.Path, f.GameCount, f.Help)).Should().Equal(
            (_steam!.Root, 1, "1 game · found by Steam"),
            (sdCard, 2, "2 games · found by Steam"));
    }

    [Fact]
    public void Summary_counts_folders_and_games()
    {
        // Arrange
        // Act
        var section = Section(s => s.AddLibrary("SdCard"));

        // Assert
        section.SteamSummary.Label.Should().Be("Steam · 2 folders · 0 games");
        section.SteamSummary.Help.Should().StartWith("Last scan at ");
    }

    [Fact]
    public void Summary_says_steam_not_found_when_there_is_no_library()
    {
        // Arrange
        // Act
        var section = SectionWithoutSteam();

        // Assert
        section.SteamSummary.Label.Should().Be("Steam not found");
        section.Folders.Should().BeEmpty();
    }

    [Fact]
    public void Summary_shows_scanning_while_a_scan_runs()
    {
        // Arrange
        var section = Section();
        var library = Get<GameLibrary>();
        string? seen = null;
        library.StateChanged += (_, _) => seen ??= section.SteamSummary.Label;

        // Act
        library.Refresh();

        // Assert
        seen.Should().Be("Steam · scanning…");
        section.SteamSummary.RescanCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Folder_that_is_not_available_raises_an_alert_and_the_badge()
    {
        // Arrange
        // Act
        var section = Section(s => s.AddLibrary("SdCard", create: false));

        // Assert
        section.BadgeCount.Should().Be(1);
        section.BadgeIsError.Should().BeFalse();
        section.Alerts.Should().ContainSingle().Which.Title.Should().Be("A Steam library isn't available");
        section.Folders[1].IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Alert_says_how_many_games_are_hidden()
    {
        // Arrange
        string card = "";
        var section = Section(s =>
        {
            card = s.AddLibrary("SdCard");
            s.AddGame(20, "Beta", library: card);
        });
        Directory.Delete(card, recursive: true);

        // Act
        await section.RescanAsync();

        // Assert
        section.Alerts.Should().ContainSingle().Which.Text.Should().Be($"{card}: 1 game is hidden until the folder is back. Installed translations are untouched.");
        section.Folders[1].Help.Should().Be("1 game hidden");
    }

    [Fact]
    public async Task Rescan_after_the_drive_is_back_clears_the_alert()
    {
        // Arrange
        string card = "";
        var section = Section(s => card = s.AddLibrary("SdCard", create: false));
        Directory.CreateDirectory(Path.Combine(card, "steamapps"));

        // Act
        await section.RescanAsync();

        // Assert
        section.Alerts.Should().BeEmpty();
        section.BadgeCount.Should().Be(0);
        section.Folders[1].IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Folders_found_by_steam_can_be_opened_but_not_removed()
    {
        // Arrange
        var section = Section();
        var root = section.Folders.Single();

        // Act
        root.OpenCommand.Execute(null);

        // Assert
        Get<FakeShell>().OpenedFolders.Should().Equal(_steam!.Root);
        root.RemoveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Adding_a_steam_library_folder_saves_it_and_shows_its_games()
    {
        // Arrange
        var section = Section();
        var extra = ExtraLibrary();
        Get<FakeFilePicker>().Folder = extra;

        // Act
        await section.AddSteamFolderAsync();

        // Assert
        Get<SettingsStore>().Current.SteamLibraryFolders.Should().Equal(extra);
        section.Folders.Select(f => (f.Path, f.IsAddedByHand, f.Help)).Should().Contain((extra, true, "1 game · added by you"));
        Get<GameCatalog>().Games.Should().ContainSingle(g => g.Name == "Gamma");
        section.AddSteamFolder.Error.Should().BeNull();
    }

    [Fact]
    public async Task Picking_the_steamapps_folder_adds_the_library_above_it()
    {
        // Arrange
        var section = Section();
        var extra = ExtraLibrary();
        Get<FakeFilePicker>().Folder = Path.Combine(extra, "steamapps");

        // Act
        await section.AddSteamFolderAsync();

        // Assert
        Get<SettingsStore>().Current.SteamLibraryFolders.Should().Equal(extra);
    }

    [Fact]
    public async Task A_folder_without_steamapps_is_not_added()
    {
        // Arrange
        var section = Section();
        var folder = Dir("Games");
        Get<FakeFilePicker>().Folder = folder;

        // Act
        await section.AddSteamFolderAsync();

        // Assert
        section.AddSteamFolder.Error.Should().Be($"«{folder}» isn't a Steam library: there is no steamapps folder in it.");
        Get<SettingsStore>().Current.SteamLibraryFolders.Should().BeNull();
    }

    [Fact]
    public async Task A_folder_already_in_the_list_is_not_added_again()
    {
        // Arrange
        var section = Section();
        Get<FakeFilePicker>().Folder = _steam!.Root;

        // Act
        await section.AddSteamFolderAsync();

        // Assert
        section.AddSteamFolder.Error.Should().Be($"«{_steam.Root}» is already in the list.");
        Get<SettingsStore>().Current.SteamLibraryFolders.Should().BeNull();
    }

    [Fact]
    public async Task Cancelling_the_picker_changes_nothing()
    {
        // Arrange
        var section = Section();
        Get<FakeFilePicker>().Folder = null;

        // Act
        await section.AddSteamFolderAsync();

        // Assert
        section.AddSteamFolder.Error.Should().BeNull();
        Get<SettingsStore>().Current.SteamLibraryFolders.Should().BeNull();
    }

    [Fact]
    public async Task Removing_a_folder_asks_first_and_says_what_stays()
    {
        // Arrange
        var section = Section();
        var extra = ExtraLibrary();
        Get<FakeFilePicker>().Folder = extra;
        await section.AddSteamFolderAsync();

        // Act
        section.Folders.Single(f => f.IsAddedByHand).RemoveCommand.Execute(null);

        // Assert
        var dialog = Confirmation();
        dialog.Title.Should().Be($"Remove «{extra}» from AI Local Pack?");
        dialog.Message.Should().Be(
            "Its games leave the library. The folder, the games and installed translations stay on disk, and Steam is not changed. You can add the folder again later.");
        dialog.ConfirmText.Should().Be("Remove");
        Get<SettingsStore>().Current.SteamLibraryFolders.Should().Equal(extra);
    }

    [Fact]
    public async Task Confirming_removes_a_folder_added_by_hand_and_its_games()
    {
        // Arrange
        var section = Section();
        var extra = ExtraLibrary();
        Get<FakeFilePicker>().Folder = extra;
        await section.AddSteamFolderAsync();
        section.Folders.Single(f => f.IsAddedByHand).RemoveCommand.Execute(null);

        // Act
        Confirmation().ConfirmCommand.Execute(null);
        await Get<GameLibrary>().RefreshAsync();

        // Assert
        Get<SettingsStore>().Current.SteamLibraryFolders.Should().BeEmpty();
        Get<SettingsStore>().Current.IgnoredSteamLibraries.Should().BeNullOrEmpty();
        Get<GameCatalog>().Games.Should().BeEmpty();
        section.Folders.Select(f => f.Path).Should().Equal(_steam!.Root);
        Directory.Exists(extra).Should().BeTrue();
    }

    [Fact]
    public void Cancelling_keeps_the_folder()
    {
        // Arrange
        var section = Section(s => s.AddLibrary("SdCard", create: false));
        section.Folders[1].RemoveCommand.Execute(null);

        // Act
        Confirmation().CancelCommand.Execute(null);

        // Assert
        Get<IDialogService>().Current.Should().BeNull();
        Get<SettingsStore>().Current.IgnoredSteamLibraries.Should().BeNull();
        section.BadgeCount.Should().Be(1);
    }

    [Fact]
    public async Task Removing_a_steam_folder_that_is_not_available_forgets_it_and_its_hidden_games()
    {
        // Arrange
        string card = "";
        var section = Section(s =>
        {
            card = s.AddLibrary("SdCard");
            s.AddGame(20, "Beta", library: card);
        });
        Directory.Delete(card, recursive: true);
        await section.RescanAsync();
        section.Folders[1].RemoveCommand.Execute(null);

        // Act
        Confirmation().ConfirmCommand.Execute(null);
        await Get<GameLibrary>().RefreshAsync();

        // Assert
        Get<SettingsStore>().Current.IgnoredSteamLibraries.Should().Equal(card);
        Get<GameCatalog>().Games.Should().BeEmpty();
        section.Alerts.Should().BeEmpty();
        section.BadgeCount.Should().Be(0);
        section.Folders.Select(f => f.Path).Should().Equal(_steam!.Root);
    }

    [Fact]
    public async Task Adding_a_removed_folder_again_brings_it_back()
    {
        // Arrange
        string card = "";
        var section = Section(s => card = s.AddLibrary("SdCard"));
        Get<SettingsStore>().Update(s => s with { IgnoredSteamLibraries = [card] });
        await section.RescanAsync();
        Get<FakeFilePicker>().Folder = card;

        // Act
        await section.AddSteamFolderAsync();

        // Assert
        Get<SettingsStore>().Current.IgnoredSteamLibraries.Should().BeEmpty();
        section.Folders.Select(f => f.Path).Should().Contain(card);
    }

    [Fact]
    public void Lists_manually_added_games_with_path_and_engine()
    {
        // Arrange
        var section = Section();

        // Act
        var game = AddManual();

        // Assert
        section.ManualGames.Select(r => (r.Label, r.Path, r.Engine)).Should().Equal(("Wren and Whistle", game.InstallPath, "Ren'Py"));
        section.Groups[1].Rows.Should().NotContain(section.NoManualGames);
    }

    [Fact]
    public void Without_manual_games_shows_the_empty_note_and_add_game_folder()
    {
        // Arrange
        // Act
        var section = Section();

        // Assert
        section.Groups[1].Rows.Should().Equal(section.NoManualGames, section.AddGameFolder);
        section.NoManualGames.Label.Should().StartWith("No manually added games.");
    }

    [Fact]
    public void Removing_a_game_asks_first_and_says_the_folder_and_pack_stay()
    {
        // Arrange
        var section = Section();
        AddManual();

        // Act
        section.ManualGames.Single().RemoveCommand.Execute(null);

        // Assert
        var dialog = Confirmation();
        dialog.Title.Should().Be("Remove «Wren and Whistle» from AI Local Pack?");
        dialog.Message.Should().Be("The game folder and any installed translation stay on disk. You can add the folder again later.");
        Get<GameCatalog>().Games.Should().ContainSingle();
    }

    [Fact]
    public void Confirming_removes_the_game_but_not_its_folder()
    {
        // Arrange
        var section = Section();
        var game = AddManual();
        section.ManualGames.Single().RemoveCommand.Execute(null);

        // Act
        Confirmation().ConfirmCommand.Execute(null);

        // Assert
        Get<GameCatalog>().Games.Should().BeEmpty();
        section.ManualGames.Should().BeEmpty();
        Directory.Exists(game.InstallPath).Should().BeTrue();
    }

    [Fact]
    public void Open_shows_a_manual_game_folder()
    {
        // Arrange
        var section = Section();
        var game = AddManual();

        // Act
        section.ManualGames.Single().OpenCommand.Execute(null);

        // Assert
        Get<FakeShell>().OpenedFolders.Should().Equal(game.InstallPath);
    }

    [Fact]
    public async Task Add_game_folder_opens_the_add_folder_dialog()
    {
        // Arrange
        var section = Section();

        // Act
        await section.AddGameFolder.Command.ExecuteAsync(null);

        // Assert
        Get<IDialogService>().Current.Should().BeOfType<AddGameDialogViewModel>().Which.Step.Should().Be(AddGameStep.Pick);
    }

    [Fact]
    public void Other_stores_are_coming_soon()
    {
        // Arrange
        // Act
        var section = Section();

        // Assert
        section.Groups[2].Rows.Should().ContainSingle().Which.Should().BeOfType<ComingSoonSettingRow>()
            .Which.Label.Should().Be("Epic Games, GOG, EA app");
    }

    [Fact]
    public void Search_finds_add_steam_library_folder_under_libraries()
    {
        // Arrange
        Section();
        var settings = Get<IPageFactory>().Create<SettingsViewModel>();

        // Act
        settings.Query = "sd card";

        // Assert
        settings.Results.Should().ContainSingle().Which.Section.Id.Should().Be(SettingsSectionId.Libraries);
        settings.Results[0].Rows.Should().Contain(Get<LibrariesSettingsSection>().AddSteamFolder);
    }

    [Fact]
    public void Library_hides_steam_games_of_a_folder_that_is_not_available()
    {
        // Arrange
        string card = "";
        Section(s =>
        {
            card = s.AddLibrary("SdCard");
            s.AddGame(10, "Alpha").AddGame(20, "Beta", library: card);
        });
        Directory.Delete(card, recursive: true);
        Get<GameLibrary>().Refresh();

        // Act
        var library = Get<IPageFactory>().Create<LibraryViewModel>();

        // Assert
        library.VisibleCards.Select(c => c.Name).Should().Equal("Alpha");
        Get<GameCatalog>().Games.Should().HaveCount(2);
    }
}
