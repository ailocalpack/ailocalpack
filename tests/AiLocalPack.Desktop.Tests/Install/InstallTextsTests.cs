using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Localization;

namespace AiLocalPack.Desktop.Tests.Install;

public sealed class InstallTextsTests
{
    private static Localizer En => Localizer.Current;

    [Fact]
    public void File_in_use_names_the_file_and_the_likely_cause()
    {
        // Arrange
        var error = new InstallError(InstallErrorKind.Transaction, Transaction: new InstallFailure(InstallProblem.FileInUse, "resources.assets"));

        // Act
        var reason = InstallTexts.Reason(error, En);

        // Assert
        reason.Should().Be("resources.assets is in use; the game seems to be running. Close it and retry.");
    }

    [Fact]
    public void Offline_download_says_there_is_no_connection()
    {
        // Act
        var reason = InstallTexts.Reason(new InstallError(InstallErrorKind.Fetch, Fetch: PackFetchProblem.Offline), En);

        // Assert
        reason.Should().Be("Couldn't download the pack: no connection to the registry.");
    }

    [Fact]
    public void A_font_that_couldnt_be_downloaded_is_named_with_the_way_out()
    {
        // Act
        var reason = InstallTexts.Reason(new InstallError(InstallErrorKind.Font, Font: FontFetchProblem.Offline, FontName: "Noto Sans JP"), En);

        // Assert
        reason.Should().Be("The game's font lacks letters of this translation, and the open font Noto Sans JP that has them couldn't be downloaded. Check the connection and retry.");
    }

    [Fact]
    public void Dependencies_are_named()
    {
        // Act
        var reason = InstallTexts.Reason(new InstallError(InstallErrorKind.DependenciesUnsupported, Dependencies: [new("bepinex", "5.4.22")]), En);

        // Assert
        reason.Should().Be("This pack needs bepinex 5.4.22, which this version of the app can't install yet.");
    }

    [Fact]
    public void Failure_toast_says_when_restoring_failed_too()
    {
        // Arrange
        var failure = new InstallFailure(InstallProblem.IoError, FilesRestored: false);
        var outcome = new InstallOutcome(InstallOperationKind.Install, TestGames.Steam(10, "Alpha"), "p", "P", new InstallError(InstallErrorKind.Transaction, Transaction: failure), []);

        // Act
        var text = InstallTexts.Toast(outcome, En);

        // Assert
        text.Should().Be("Couldn't install, and restoring files failed. The app retries on the next start.");
    }

    [Fact]
    public void Progress_title_counts_the_steps()
    {
        // Arrange
        var operation = new InstallOperation(InstallBed.Alpha, InstallOperationKind.Install, "p", "Lanterns",
            [InstallStep.Download, InstallStep.Check, InstallStep.Rebuild], StepIndex: 1);

        // Act
        var title = InstallTexts.Title(operation, En);

        // Assert
        title.Should().Be("Installing «Lanterns» · step 2 of 3");
    }

    [Fact]
    public void Operation_percent_adds_the_current_step_fraction()
    {
        // Arrange
        var operation = new InstallOperation(InstallBed.Alpha, InstallOperationKind.Install, "p", "P",
            [InstallStep.Download, InstallStep.Check, InstallStep.Rebuild, InstallStep.Write], StepIndex: 2, Fraction: 0.5);

        // Act
        var percent = operation.Percent;

        // Assert
        percent.Should().Be(62);
    }
}
