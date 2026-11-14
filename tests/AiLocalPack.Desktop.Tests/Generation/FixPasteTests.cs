using AiLocalPack.Core.Generation;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>An image pasted into Fix with AI's request becomes a screenshot chip, with no file to save first.</summary>
public sealed class FixPasteTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G'];

    private static async Task<(InstallBed Bed, FixViewModel Fix, FakeFilePicker Picker)> ComposerAsync()
    {
        var bed = await InstallBed.CreateAsync();
        bed.LocalPack();
        var page = bed.Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(bed.Game, GamePageTab.Generation);
        await Wait.UntilAsync(() => page.Generation.Fix is not null);
        return (bed, page.Generation.Fix!, bed.Services.GetRequiredService<FakeFilePicker>());
    }

    [Fact]
    public async Task A_pasted_image_is_attached_as_a_screenshot()
    {
        // Arrange
        var (bed, fix, picker) = await ComposerAsync();
        using var _bed = bed;
        picker.Clipboard = new ClipboardImage(null, Png);

        // Act
        await fix.PasteImageAsync();

        // Assert
        var chip = fix.Attachments.Single(a => a.Attachment.Kind == FixAttachmentKind.Screenshot);
        (chip.Attachment.Name, Convert.ToHexString(File.ReadAllBytes(chip.Attachment.Path!))).Should().Be((FixViewModel.PastedName, Convert.ToHexString(Png)));
    }

    [Fact]
    public async Task Two_pasted_images_get_two_files()
    {
        // Arrange
        var (bed, fix, picker) = await ComposerAsync();
        using var _bed = bed;
        picker.Clipboard = new ClipboardImage(null, Png);
        await fix.PasteImageAsync();

        // Act
        await fix.PasteImageAsync();

        // Assert
        fix.Attachments.Where(a => a.Attachment.Kind == FixAttachmentKind.Screenshot).Select(a => a.Attachment.Name)
            .Should().Equal("pasted.png", "pasted-2.png");
    }

    [Fact]
    public async Task A_copied_image_file_is_attached_as_a_screenshot()
    {
        // Arrange
        var (bed, fix, picker) = await ComposerAsync();
        using var _bed = bed;
        using var dir = new TempDir();
        dir.File("menu.png", "png");
        picker.Clipboard = new ClipboardImage(dir.Combine("menu.png"), null);

        // Act
        await fix.PasteImageAsync();

        // Assert
        fix.Attachments.Single(a => a.Attachment.Kind == FixAttachmentKind.Screenshot).Attachment.Name.Should().Be("menu.png");
    }

    [Fact]
    public async Task Without_an_image_the_paste_stays_text()
    {
        // Arrange
        var (bed, fix, _) = await ComposerAsync();
        using var _bed = bed;

        // Act
        var attached = await fix.PasteImageAsync();

        // Assert
        (attached, fix.Attachments.Any(a => a.Attachment.Kind == FixAttachmentKind.Screenshot)).Should().Be((false, false));
    }
}
