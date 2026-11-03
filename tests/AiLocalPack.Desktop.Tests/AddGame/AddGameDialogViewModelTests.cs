using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.AddGame;
using AiLocalPack.Toolkit.Engines;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.AddGame;

public sealed class AddGameDialogViewModelTests : IDisposable
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

    private FakeFilePicker Picker => _services.GetRequiredService<FakeFilePicker>();

    private AddGameDialogViewModel Open()
    {
        // The main window view model puts the library on the navigation stack, as in the app.
        _services.GetRequiredService<MainWindowViewModel>();
        return Dialogs.Show<AddGameDialogViewModel>();
    }

    private async Task<AddGameDialogViewModel> Analyzed(string folder)
    {
        var vm = Open();
        await vm.AnalyzeAsync(_games.Combine(folder));
        return vm;
    }

    private string RenPyGame(string folder = "Wren and Whistle")
    {
        _games.RenPy(folder, name: folder.Split('/')[^1]);
        return folder;
    }

    // ---------- Step 1 ----------

    [Fact]
    public void Opens_on_step_one()
    {
        // Arrange
        var vm = Open();

        // Act
        var step = (vm.Step, vm.StepText, vm.HasPickError);

        // Assert
        step.Should().Be((AddGameStep.Pick, "Step 1 of 2", false));
    }

    [Fact]
    public async Task Folder_without_games_returns_to_step_one_with_an_error_and_the_hint_open()
    {
        // Arrange
        _games.File("Saves/slot1.sav", "x");

        // Act
        var vm = await Analyzed("Saves");

        // Assert
        vm.Step.Should().Be(AddGameStep.Pick);
        vm.PickError.Should().Be("No games found in “Saves” or its subfolders.");
        vm.IsHintOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Missing_folder_shows_an_error()
    {
        // Arrange
        var vm = Open();

        // Act
        await vm.AnalyzeAsync(_games.Combine("gone"));

        // Assert
        vm.Step.Should().Be(AddGameStep.Pick);
        vm.PickError.Should().StartWith("The folder").And.EndWith("doesn't exist or can't be opened.");
    }

    [Fact]
    public async Task Choose_folder_analyzes_the_picked_folder()
    {
        // Arrange
        var vm = Open();
        Picker.Folder = _games.Combine(RenPyGame());

        // Act
        await vm.ChooseFolderCommand.ExecuteAsync(null);

        // Assert
        vm.Step.Should().Be(AddGameStep.Summary);
        vm.Summary!.Name.Should().Be("Wren and Whistle");
    }

    [Fact]
    public async Task Cancelled_folder_picker_stays_on_step_one()
    {
        // Arrange
        var vm = Open();
        Picker.Folder = null;

        // Act
        await vm.ChooseFolderCommand.ExecuteAsync(null);

        // Assert
        vm.Step.Should().Be(AddGameStep.Pick);
    }

    // ---------- Analysis ----------

    [Fact]
    public async Task Analysis_completes_every_checklist_step()
    {
        // Arrange
        var folder = RenPyGame();

        // Act
        var vm = await Analyzed(folder);

        // Assert
        vm.Steps.Should().OnlyContain(s => s.IsDone);
        vm.Steps[1].Detail.Should().Be("Ren'Py");
        vm.Steps[3].Detail.Should().Be("in a later version");
    }

    [Fact]
    public async Task Cancel_during_the_analysis_returns_to_step_one()
    {
        // Arrange
        var vm = Open();
        var analysis = vm.AnalyzeAsync(_games.Combine(RenPyGame()));

        // Act
        vm.CancelCommand.Execute(null);
        await analysis;

        // Assert
        vm.Step.Should().Be(AddGameStep.Pick);
        vm.HasPickError.Should().BeFalse();
    }

    // ---------- Step 2: one game ----------

    [Fact]
    public async Task One_game_shows_its_summary()
    {
        // Arrange
        var folder = RenPyGame();

        // Act
        var vm = await Analyzed(folder);

        // Assert
        vm.Step.Should().Be(AddGameStep.Summary);
        vm.StepText.Should().Be("Step 2 of 2");
        vm.Summary!.Name.Should().Be("Wren and Whistle");
        vm.Summary!.EngineText.Should().Be("Ren'Py");
        vm.Summary!.ExecutableText.Should().Be("Wren and Whistle.exe");
        vm.Summary!.SizeText.Should().StartWith("6 files · ");
        vm.Summary!.GamePath.Should().Be(_games.Combine(folder));
        vm.PrimaryText.Should().Be("Add game");
        vm.Summary!.ShowRegistryNote.Should().BeTrue();
        vm.Summary!.HasSubfolderNotice.Should().BeFalse();
    }

    [Fact]
    public async Task Game_in_a_subfolder_says_where_it_was_found()
    {
        // Arrange
        RenPyGame("Indie/GOG/Moss");

        // Act
        var vm = await Analyzed("Indie");

        // Assert
        vm.Step.Should().Be(AddGameStep.Summary);
        vm.Summary!.SubfolderNotice.Should().Be($"“Indie” holds one game, in the subfolder GOG{Path.DirectorySeparatorChar}Moss.");
    }

    [Fact]
    public async Task Unknown_engine_warns_and_opens_the_details()
    {
        // Arrange
        _games.File("Old Lighthouse/lighthouse.exe", "MZ");

        // Act
        var vm = await Analyzed("Old Lighthouse");

        // Assert
        vm.Summary!.IsEngineUnknown.Should().BeTrue();
        vm.Summary!.ShowEngineWarning.Should().BeTrue();
        vm.Summary!.IsDetailsOpen.Should().BeTrue();
        vm.Summary!.EngineText.Should().Be("Engine not recognized");
        vm.Summary!.SelectedEngine!.Kind.Should().Be(EngineKind.Unknown);
    }

    [Fact]
    public async Task Detected_engine_is_the_first_choice()
    {
        // Arrange
        var folder = RenPyGame();

        // Act
        var vm = await Analyzed(folder);

        // Assert
        vm.Summary!.IsDetailsOpen.Should().BeFalse();
        vm.Summary!.EngineOptions.Select(o => o.Display).Should().Equal("Ren'Py", "Unity", "Unreal Engine", "RPG Maker", "Godot", "Other / don't know");
        vm.Summary!.SelectedEngine.Should().Be(vm.Summary!.EngineOptions[0]);
    }

    [Fact]
    public async Task Anti_cheat_game_is_offered_as_unavailable()
    {
        // Arrange
        _games.UnityWindows("Frostbound", name: "Frostbound").File("Frostbound/EasyAntiCheat/Settings.json", "{}");

        // Act
        var vm = await Analyzed("Frostbound");

        // Assert
        vm.Summary!.IsBlocked.Should().BeTrue();
        vm.Summary!.AntiCheatTitle.Should().Be("Easy Anti-Cheat detected.");
        vm.PrimaryText.Should().Be("Add as unavailable");
    }

    [Fact]
    public async Task Anti_cheat_game_is_added_with_its_anti_cheat()
    {
        // Arrange
        _games.UnityWindows("Frostbound", name: "Frostbound").File("Frostbound/EasyAntiCheat/Settings.json", "{}");
        var vm = await Analyzed("Frostbound");

        // Act
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Assert
        Catalog.Games.Should().ContainSingle().Which.AntiCheat.Should().Be("eac");
    }

    [Fact]
    public async Task Adding_puts_the_game_in_the_catalog()
    {
        // Arrange
        var vm = await Analyzed(RenPyGame());

        // Act
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Assert
        var game = Catalog.Games.Should().ContainSingle().Subject;
        game.Id.Should().Be(vm.Summary!.Analysis.Id);
        game.Name.Should().Be("Wren and Whistle");
        game.Source.Should().Be(GameSource.Manual);
        game.InstallPath.Should().Be(_games.Combine("Wren and Whistle"));
        game.Engine.Should().Be("renpy");
        game.AntiCheat.Should().Be("none");
        game.Version.Should().Be(vm.Summary!.Analysis.Version);
        game.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Adding_closes_the_dialog_and_opens_the_game_page()
    {
        // Arrange
        var vm = await Analyzed(RenPyGame());

        // Act
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Assert
        Dialogs.Current.Should().BeNull();
        Navigation.Current.Should().BeOfType<GamePageViewModel>().Which.Game.Name.Should().Be("Wren and Whistle");
    }

    [Fact]
    public async Task Added_game_survives_a_catalog_reload()
    {
        // Arrange
        var vm = await Analyzed(RenPyGame());
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Act
        var reloaded = Catalog.Load();

        // Assert
        reloaded.Should().ContainSingle().Which.Name.Should().Be("Wren and Whistle");
    }

    [Fact]
    public async Task Renamed_game_is_added_with_the_new_name()
    {
        // Arrange
        var vm = await Analyzed(RenPyGame());
        vm.Summary!.RenameCommand.Execute(null);
        vm.Summary!.Name = "  Wren & Whistle ";

        // Act
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Assert
        Catalog.Games.Should().ContainSingle().Which.Name.Should().Be("Wren & Whistle");
    }

    [Fact]
    public async Task Blank_name_can_not_be_added()
    {
        // Arrange
        var vm = await Analyzed(RenPyGame());

        // Act
        vm.Summary!.Name = "   ";

        // Assert
        vm.PrimaryCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Rename_toggles_the_name_field()
    {
        // Arrange
        var vm = await Analyzed(RenPyGame());

        // Act
        vm.Summary!.RenameCommand.Execute(null);

        // Assert
        vm.Summary!.IsRenaming.Should().BeTrue();
    }

    [Fact]
    public async Task Escape_ends_renaming_before_closing()
    {
        // Arrange
        var vm = await Analyzed(RenPyGame());
        vm.Summary!.RenameCommand.Execute(null);

        // Act
        var handled = vm.TryHandleEscape();

        // Assert
        handled.Should().BeTrue();
        vm.Summary!.IsRenaming.Should().BeFalse();
    }

    [Fact]
    public async Task Chosen_engine_is_stored()
    {
        // Arrange
        _games.File("Old Lighthouse/lighthouse.exe", "MZ");
        var vm = await Analyzed("Old Lighthouse");
        vm.Summary!.SelectedEngine = vm.Summary!.EngineOptions.Single(o => o.Kind.Engine == GameEngine.Godot);

        // Act
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Assert
        Catalog.Games.Should().ContainSingle().Which.Engine.Should().Be("godot");
    }

    [Fact]
    public async Task Other_main_executable_gives_another_fingerprint()
    {
        // Arrange
        var vm = await Analyzed(RenPyGame());
        var detected = vm.Summary!.Analysis.Id;
        vm.Summary!.SelectedExecutable = "Wren and Whistle-32.exe";

        // Act
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Assert
        Catalog.Games.Should().ContainSingle().Which.Id.Should().NotBe(detected);
        vm.Summary!.ExecutableOptions.Should().Contain("Wren and Whistle-32.exe");
    }

    [Fact]
    public async Task Back_returns_to_step_one()
    {
        // Arrange
        var vm = await Analyzed(RenPyGame());

        // Act
        vm.BackCommand.Execute(null);

        // Assert
        vm.Step.Should().Be(AddGameStep.Pick);
        vm.Summary.Should().BeNull();
    }

    [Fact]
    public async Task Close_removes_the_dialog()
    {
        // Arrange
        var vm = await Analyzed(RenPyGame());

        // Act
        vm.CloseCommand.Execute(null);

        // Assert
        Dialogs.Current.Should().BeNull();
        Catalog.Games.Should().BeEmpty();
    }

    // ---------- Already in the library ----------

    [Fact]
    public async Task Game_already_in_the_library_offers_its_page()
    {
        // Arrange
        var folder = RenPyGame();
        await (await Analyzed(folder)).PrimaryCommand.ExecuteAsync(null);

        // Act
        var vm = await Analyzed(folder);

        // Assert
        vm.Summary!.IsDuplicate.Should().BeTrue();
        vm.Summary!.DuplicateText.Should().Be("This game is already in the library as “Wren and Whistle”.");
        vm.PrimaryText.Should().Be("Open game page");
        vm.Summary!.CanEdit.Should().BeFalse();
        vm.Summary!.ShowRegistryNote.Should().BeFalse();
    }

    [Fact]
    public async Task Game_already_in_the_library_opens_its_page_without_adding_again()
    {
        // Arrange
        var folder = RenPyGame();
        await (await Analyzed(folder)).PrimaryCommand.ExecuteAsync(null);
        Navigation.GoBack();
        var vm = await Analyzed(folder);

        // Act
        await vm.PrimaryCommand.ExecuteAsync(null);

        // Assert
        Catalog.Games.Should().ContainSingle();
        Navigation.Current.Should().BeOfType<GamePageViewModel>();
        Dialogs.Current.Should().BeNull();
    }

    [Fact]
    public async Task Same_game_copied_elsewhere_is_already_in_the_library()
    {
        // Arrange
        await (await Analyzed(RenPyGame("Copy A/Wren"))).PrimaryCommand.ExecuteAsync(null);

        // Act
        var vm = await Analyzed(RenPyGame("Copy B/Wren"));

        // Assert
        vm.Summary!.IsDuplicate.Should().BeTrue();
    }

    [Fact]
    public async Task Folder_of_a_library_game_is_already_in_the_library()
    {
        // Arrange
        var folder = RenPyGame();
        Catalog.AddManual(TestGames.Manual("Known") with { InstallPath = _games.Combine(folder) });

        // Act
        var vm = await Analyzed(folder);

        // Assert
        vm.Summary!.Existing!.Name.Should().Be("Known");
    }

    // ---------- Formatting ----------

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(10_240L, "10 KB")]
    [InlineData(851_443_712L, "812 MB")]
    [InlineData(2_040_109_465L, "1.9 GB")]
    public void Sizes_are_shown_in_the_largest_fitting_unit(long bytes, string expected)
    {
        // Arrange
        var vm = Open();

        // Act
        var text = vm.FormatSize(bytes);

        // Assert
        text.Should().Be(expected);
    }
}
