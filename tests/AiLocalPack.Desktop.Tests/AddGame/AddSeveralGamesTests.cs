using System.ComponentModel;
using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.AddGame;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.AddGame;

/// <summary>Add folder dialog with a folder holding several games (design §2): scanning, overview, review, result.</summary>
public sealed class AddSeveralGamesTests : IDisposable
{
    private readonly TempDir _games = new();
    private readonly ServiceProvider _services = TestServices.Build();

    public void Dispose()
    {
        _services.Dispose();
        _games.Dispose();
    }

    private GameCatalog Catalog => _services.GetRequiredService<GameCatalog>();

    private INavigationService Navigation => _services.GetRequiredService<INavigationService>();

    private IDialogService Dialogs => _services.GetRequiredService<IDialogService>();

    private AddGameDialogViewModel Open()
    {
        // The main window view model puts the library on the navigation stack, as in the app.
        _services.GetRequiredService<MainWindowViewModel>();
        return Dialogs.Show<AddGameDialogViewModel>();
    }

    /// <summary>"Indie" with Wren, Moss and Lark (Ren'Py) and a <c>_CommonRedist</c> folder.</summary>
    private string Indie()
    {
        _games.RenPy("Indie/Wren", name: "Wren").RenPy("Indie/Moss", name: "Moss").RenPy("Indie/Lark", name: "Lark")
            .File("Indie/_CommonRedist/vcredist_x64.exe", "MZ");
        return _games.Combine("Indie");
    }

    private async Task<AddGameDialogViewModel> Overview(string folder)
    {
        var vm = Open();
        await vm.AnalyzeAsync(folder);
        return vm;
    }

    private async Task<AddGameDialogViewModel> Reviewing(string folder)
    {
        var vm = await Overview(folder);
        vm.StartCommand.Execute(null);
        return vm;
    }

    // ---------- Overview ----------

    [Fact]
    public async Task Folder_with_several_games_shows_the_overview()
    {
        // Arrange
        var folder = Indie();

        // Act
        var vm = await Overview(folder);

        // Assert
        vm.Step.Should().Be(AddGameStep.Overview);
        vm.Title.Should().Be("Add games");
        vm.StepText.Should().Be("Step 2 of 3");
        vm.OverviewTitle.Should().Be("Found 3 games in “Indie”");
        vm.Games.Select(g => g.Name).Should().Equal("Lark", "Moss", "Wren");
    }

    [Fact]
    public async Task Overview_lists_the_skipped_folders_with_their_reason()
    {
        // Arrange
        var folder = Indie();

        // Act
        var vm = await Overview(folder);

        // Assert
        vm.SkippedTitle.Should().Be("Skipped 1 folder without games");
        vm.SkippedFolders.Should().ContainSingle()
            .Which.Should().Be(new SkippedFolderViewModel("_CommonRedist", "runtime installers (DirectX, Visual C++…)"));
    }

    [Fact]
    public async Task Overview_numbers_the_games_in_review_order()
    {
        // Arrange
        var folder = Indie();

        // Act
        var vm = await Overview(folder);

        // Assert
        vm.Games.Select(g => g.Number).Should().Equal(1, 2, 3);
        vm.QueueText.Should().Be("3 games, one at a time");
    }

    [Fact]
    public async Task Overview_tally_counts_games_by_status()
    {
        // Arrange
        var folder = Indie();
        _games.UnityWindows("Indie/Frost", name: "Frost").File("Indie/Frost/EasyAntiCheat/Settings.json", "{}");
        Catalog.AddManual(TestGames.Manual("Known") with { InstallPath = _games.Combine("Indie/Moss") });

        // Act
        var vm = await Overview(folder);

        // Assert
        vm.Tally.Select(t => t.Text).Should().Equal("2 new", "1 with anti-cheat", "1 already in the library");
    }

    [Fact]
    public async Task Games_already_in_the_library_are_skipped_automatically()
    {
        // Arrange
        var folder = Indie();
        Catalog.AddManual(TestGames.Manual("Known") with { InstallPath = _games.Combine("Indie/Moss") });

        // Act
        var vm = await Overview(folder);

        // Assert
        vm.Queue.Select(g => g.Name).Should().Equal("Lark", "Wren");
        vm.Games.Single(g => g.Game.Name == "Moss").Should().Match<FolderGameViewModel>(g => g.Number == 0 && g.StatusText == "In library");
    }

    [Fact]
    public async Task Folder_whose_games_are_all_in_the_library_has_nothing_to_add()
    {
        // Arrange
        _games.RenPy("Pair/Wren", name: "Wren").RenPy("Pair/Moss", name: "Moss");
        Catalog.AddManual(TestGames.Manual("Known Wren") with { InstallPath = _games.Combine("Pair/Wren") });
        Catalog.AddManual(TestGames.Manual("Known Moss") with { Id = GameId.Fingerprint(new string('b', GameId.FingerprintLength)), InstallPath = _games.Combine("Pair/Moss") });

        // Act
        var vm = await Overview(_games.Combine("Pair"));

        // Assert
        vm.CanStart.Should().BeFalse();
        vm.QueueText.Should().Be("Nothing new to add");
    }

    [Fact]
    public async Task Back_from_the_overview_returns_to_step_one()
    {
        // Arrange
        var vm = await Overview(Indie());

        // Act
        vm.BackCommand.Execute(null);

        // Assert
        vm.Step.Should().Be(AddGameStep.Pick);
        vm.Games.Should().BeEmpty();
    }

    // ---------- Scanning ----------

    [Fact]
    public async Task Scanning_streams_the_games_found()
    {
        // Arrange
        var vm = Open();
        var steps = new List<AddGameStep>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Step))
            {
                steps.Add(vm.Step);
            }
        };

        // Act
        await vm.AnalyzeAsync(Indie());

        // Assert
        steps.Should().Equal(AddGameStep.Analyzing, AddGameStep.Scanning, AddGameStep.Overview);
        vm.FoundGames.Select(g => g.Name).Should().BeEquivalentTo("Wren", "Moss", "Lark");
    }

    [Fact]
    public async Task Stop_during_the_scan_shows_the_games_found_so_far()
    {
        // Arrange
        _games.RenPy("Big/A", name: "A").RenPy("Big/B", name: "B").RenPy("Big/C", name: "C").RenPy("Big/D", name: "D");
        var vm = Open();
        void StopOnScanning(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(vm.Step) && vm.Step == AddGameStep.Scanning)
            {
                vm.PropertyChanged -= StopOnScanning;
                vm.StopCommand.Execute(null);
            }
        }

        vm.PropertyChanged += StopOnScanning;

        // Act
        await vm.AnalyzeAsync(_games.Combine("Big"));

        // Assert
        vm.Step.Should().Be(AddGameStep.Overview);
        vm.WasStopped.Should().BeTrue();
        vm.Games.Select(g => g.Name).Should().Equal("A", "B");
    }

    [Fact]
    public async Task Cancel_during_the_scan_returns_to_step_one()
    {
        // Arrange
        var vm = Open();
        void CancelOnScanning(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(vm.Step) && vm.Step == AddGameStep.Scanning)
            {
                vm.PropertyChanged -= CancelOnScanning;
                vm.CancelCommand.Execute(null);
            }
        }

        vm.PropertyChanged += CancelOnScanning;

        // Act
        await vm.AnalyzeAsync(Indie());

        // Assert
        vm.Step.Should().Be(AddGameStep.Pick);
        vm.Games.Should().BeEmpty();
    }

    // ---------- Review ----------

    [Fact]
    public async Task Start_adding_reviews_the_first_game()
    {
        // Arrange
        var vm = await Overview(Indie());

        // Act
        vm.StartCommand.Execute(null);

        // Assert
        vm.Step.Should().Be(AddGameStep.Review);
        vm.StepText.Should().Be("Game 1 of 3");
        vm.Summary.Should().BeSameAs(vm.Queue[0].Summary);
        vm.Queue.Select(g => g.IsCurrent).Should().Equal(true, false, false);
        vm.PrimaryText.Should().Be("Add");
    }

    [Fact]
    public async Task Add_puts_the_game_in_the_catalog_and_moves_to_the_next_one()
    {
        // Arrange
        var vm = await Reviewing(Indie());

        // Act
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Assert
        Catalog.Games.Should().ContainSingle().Which.Name.Should().Be("Lark");
        vm.Queue[0].State.Should().Be(ReviewState.Added);
        vm.StepText.Should().Be("Game 2 of 3");
        Dialogs.Current.Should().BeSameAs(vm);
    }

    [Fact]
    public async Task Skip_moves_on_without_adding()
    {
        // Arrange
        var vm = await Reviewing(Indie());

        // Act
        vm.SkipCommand.Execute(null);

        // Assert
        Catalog.Games.Should().BeEmpty();
        vm.Queue[0].State.Should().Be(ReviewState.Skipped);
        vm.Summary!.Name.Should().Be("Moss");
    }

    [Fact]
    public async Task Previous_returns_to_the_previous_game()
    {
        // Arrange
        var vm = await Reviewing(Indie());
        vm.SkipCommand.Execute(null);

        // Act
        vm.PreviousCommand.Execute(null);

        // Assert
        vm.StepText.Should().Be("Game 1 of 3");
        vm.Summary!.Name.Should().Be("Lark");
    }

    [Fact]
    public async Task First_game_has_no_previous()
    {
        // Arrange
        var vm = await Overview(Indie());

        // Act
        vm.StartCommand.Execute(null);

        // Assert
        vm.PreviousCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Going_back_to_an_added_game_shows_it_as_added()
    {
        // Arrange
        var vm = await Reviewing(Indie());
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Act
        vm.PreviousCommand.Execute(null);

        // Assert
        vm.Summary!.DuplicateText.Should().Be("Added to the library as “Lark”.");
        vm.PrimaryText.Should().Be("Next");
        vm.Summary.CanEdit.Should().BeFalse();
    }

    [Fact]
    public async Task Next_on_an_added_game_does_not_add_it_again()
    {
        // Arrange
        var vm = await Reviewing(Indie());
        await vm.PrimaryCommand.ExecuteAsync(null);
        vm.PreviousCommand.Execute(null);

        // Act
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Assert
        Catalog.Games.Should().ContainSingle();
        vm.StepText.Should().Be("Game 2 of 3");
    }

    [Fact]
    public async Task Edits_to_a_game_survive_moving_between_games()
    {
        // Arrange
        var vm = await Reviewing(Indie());
        vm.Summary!.Name = "Lark Renamed";
        vm.SkipCommand.Execute(null);

        // Act
        vm.PreviousCommand.Execute(null);

        // Assert
        vm.Summary!.Name.Should().Be("Lark Renamed");
    }

    [Fact]
    public async Task Renamed_game_is_added_with_the_new_name()
    {
        // Arrange
        var vm = await Reviewing(Indie());
        vm.Summary!.Name = "Lark: Director's Cut";

        // Act
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Assert
        Catalog.Games.Should().ContainSingle().Which.Name.Should().Be("Lark: Director's Cut");
    }

    [Fact]
    public async Task Anti_cheat_game_is_reviewed_as_unavailable()
    {
        // Arrange
        _games.RenPy("Mixed/Wren", name: "Wren")
            .UnityWindows("Mixed/Frost", name: "Frost").File("Mixed/Frost/EasyAntiCheat/Settings.json", "{}");

        // Act
        var vm = await Reviewing(_games.Combine("Mixed"));

        // Assert
        vm.Summary!.Name.Should().Be("Frost");
        vm.PrimaryText.Should().Be("Add as unavailable");
        vm.IsPrimaryQuiet.Should().BeTrue();
    }

    [Fact]
    public async Task All_games_returns_to_the_overview_keeping_the_choices()
    {
        // Arrange
        var vm = await Reviewing(Indie());
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Act
        vm.AllGamesCommand.Execute(null);

        // Assert
        vm.Step.Should().Be(AddGameStep.Overview);
        vm.Queue[0].StatusText.Should().Be("Added");
        vm.Queue.Should().OnlyContain(g => !g.IsCurrent);
    }

    [Fact]
    public async Task Start_again_continues_from_the_first_game_not_reviewed()
    {
        // Arrange
        var vm = await Reviewing(Indie());
        vm.SkipCommand.Execute(null);
        vm.AllGamesCommand.Execute(null);

        // Act
        vm.StartCommand.Execute(null);

        // Assert
        vm.StepText.Should().Be("Game 2 of 3");
    }

    [Fact]
    public async Task Escape_in_the_review_ends_renaming_first()
    {
        // Arrange
        var vm = await Reviewing(Indie());
        vm.Summary!.RenameCommand.Execute(null);

        // Act
        var handled = vm.TryHandleEscape();

        // Assert
        handled.Should().BeTrue();
        vm.Summary.IsRenaming.Should().BeFalse();
    }

    // ---------- Result ----------

    [Fact]
    public async Task Last_game_leads_to_the_result()
    {
        // Arrange
        var vm = await Reviewing(Indie());
        await vm.PrimaryCommand.ExecuteAsync(null);
        vm.SkipCommand.Execute(null);

        // Act
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Assert
        vm.Step.Should().Be(AddGameStep.Result);
        vm.StepText.Should().Be("Step 3 of 3");
        vm.ResultTitle.Should().Be("Added 2 games");
        Catalog.Games.Select(g => g.Name).Should().BeEquivalentTo("Lark", "Wren");
    }

    [Fact]
    public async Task Result_says_what_happened_to_each_game()
    {
        // Arrange
        var folder = Indie();
        Catalog.AddManual(TestGames.Manual("Known") with { InstallPath = _games.Combine("Indie/Wren") });
        var vm = await Reviewing(folder);
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Act
        vm.SkipCommand.Execute(null);

        // Assert
        vm.Games.Select(g => (g.Name, g.ResultText)).Should().Equal(
            ("Lark", "added"), ("Moss", "skipped"), ("Wren", "already in the library"));
    }

    [Fact]
    public async Task Result_with_every_game_skipped_says_none_was_added()
    {
        // Arrange
        _games.RenPy("Pair/Wren", name: "Wren").RenPy("Pair/Moss", name: "Moss");
        var vm = await Reviewing(_games.Combine("Pair"));
        vm.SkipCommand.Execute(null);

        // Act
        vm.SkipCommand.Execute(null);

        // Assert
        vm.ResultTitle.Should().Be("No games added");
    }

    [Fact]
    public async Task To_library_closes_the_dialog_on_the_library()
    {
        // Arrange
        var vm = await Reviewing(Indie());
        vm.SkipCommand.Execute(null);
        vm.SkipCommand.Execute(null);
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Act
        vm.ToLibraryCommand.Execute(null);

        // Assert
        Dialogs.Current.Should().BeNull();
        Navigation.Current.Should().BeOfType<LibraryViewModel>();
        Catalog.Games.Should().ContainSingle().Which.Name.Should().Be("Wren");
    }

    [Fact]
    public async Task Closing_during_the_review_keeps_the_games_added()
    {
        // Arrange
        var vm = await Reviewing(Indie());
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Act
        vm.CloseCommand.Execute(null);

        // Assert
        Dialogs.Current.Should().BeNull();
        Catalog.Games.Should().ContainSingle();
    }
}
