using System.Buffers.Binary;
using AiLocalPack.Toolkit.Folders;
using static AiLocalPack.TestKit.IconFixture;

namespace AiLocalPack.Toolkit.Tests.Folders;

public sealed class GameIconExtractorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameIconExtractor _extractor = new();

    public void Dispose() => _dir.Dispose();

    private GameIcon? Extract(string? exe, string folder = "") =>
        _extractor.Extract(folder.Length == 0 ? _dir.Path : _dir.Combine(folder), exe);

    // ---------- Windows: PE icon resources ----------

    [Fact]
    public void Windows_exe_gives_its_largest_png_icon()
    {
        // Arrange
        var pe = Pe([new(32, 32, Png(32, "small")), new(256, 32, Png(256, "large")), new(48, 32, Png(48, "medium"))]);
        _dir.File("Game.exe", pe);

        // Act
        var icon = Extract("Game.exe");

        // Assert
        icon.Should().NotBeNull();
        icon!.Format.Should().Be(GameIconFormat.Png);
        icon.Data.Should().Equal(Png(256, "large"));
    }

    [Fact]
    public void Windows_exe_prefers_the_deeper_color_icon_of_the_same_size()
    {
        // Arrange
        var pe = Pe([new(48, 8, Png(48, "8-bit")), new(48, 32, Png(48, "32-bit"))]);
        _dir.File("Game.exe", pe);

        // Act
        var icon = Extract("Game.exe");

        // Assert
        icon!.Data.Should().Equal(Png(48, "32-bit"));
    }

    [Fact]
    public void Windows_bitmap_icon_is_wrapped_into_an_ico_file()
    {
        // Arrange
        var dib = Dib(48, "pixels");
        _dir.File("Game.exe", Pe([new(48, 32, dib)]));

        // Act
        var icon = Extract("Game.exe");

        // Assert
        icon!.Format.Should().Be(GameIconFormat.Ico);
        icon.Extension.Should().Be(".ico");
        var data = icon.Data.AsSpan();
        BinaryPrimitives.ReadUInt16LittleEndian(data[2..]).Should().Be(1, "type 1 = icon");
        BinaryPrimitives.ReadUInt16LittleEndian(data[4..]).Should().Be(1, "one image");
        data[6].Should().Be(48);
        BinaryPrimitives.ReadUInt32LittleEndian(data[14..]).Should().Be((uint)dib.Length);
        BinaryPrimitives.ReadUInt32LittleEndian(data[18..]).Should().Be(22);
        data[22..].ToArray().Should().Equal(dib);
    }

    [Fact]
    public void Reads_a_32_bit_pe()
    {
        // Arrange
        _dir.File("Game.exe", Pe([new(32, 32, Png(32, "x86"))], pe32Plus: false));

        // Act
        var icon = Extract("Game.exe");

        // Assert
        icon!.Data.Should().Equal(Png(32, "x86"));
    }

    [Fact]
    public void Exe_without_icon_resources_gives_no_icon()
    {
        // Arrange
        _dir.File("Game.exe", Pe([]));

        // Act
        var icon = Extract("Game.exe");

        // Assert
        icon.Should().BeNull();
    }

    [Fact]
    public void Not_a_pe_file_gives_no_icon()
    {
        // Arrange
        _dir.File("Game.exe", "MZ");

        // Act
        var icon = Extract("Game.exe");

        // Assert
        icon.Should().BeNull();
    }

    [Fact]
    public void Truncated_pe_gives_no_icon()
    {
        // Arrange
        var pe = Pe([new(32, 32, Png(32))]);
        _dir.File("Game.exe", pe[..0x210]);

        // Act
        var icon = Extract("Game.exe");

        // Assert
        icon.Should().BeNull();
    }

    [Fact]
    public void Missing_exe_gives_no_icon()
    {
        // Arrange
        _dir.Dir("empty");

        // Act
        var icon = Extract("Game.exe", "empty");

        // Assert
        icon.Should().BeNull();
    }

    // ---------- macOS: .icns ----------

    [Fact]
    public void Mac_bundle_gives_the_largest_png_of_the_plist_icon()
    {
        // Arrange
        _dir.File("Game.app/Contents/Info.plist", Plist("GameIcon"))
            .File("Game.app/Contents/Resources/GameIcon.icns", Icns(("ic07", Png(128, "128")), ("ic09", Png(512, "512")), ("ic08", Png(256, "256"))))
            .File("Game.app/Contents/Resources/AAA.icns", Icns(("ic10", Png(1024, "other"))));

        // Act
        var icon = Extract("Game.app");

        // Assert
        icon!.Format.Should().Be(GameIconFormat.Png);
        icon.Data.Should().Equal(Png(512, "512"));
    }

    [Fact]
    public void Mac_bundle_without_plist_icon_uses_the_first_icns()
    {
        // Arrange
        _dir.File("Game.app/Contents/Info.plist", "bplist00")
            .File("Game.app/Contents/Resources/PlayerIcon.icns", Icns(("ic08", Png(256, "player"))));

        // Act
        var icon = Extract("Game.app");

        // Assert
        icon!.Data.Should().Equal(Png(256, "player"));
    }

    [Fact]
    public void Mac_bundle_executable_path_finds_its_bundle()
    {
        // Arrange
        _dir.File("Game.app/Contents/Info.plist", Plist("App.icns"))
            .File("Game.app/Contents/Resources/App.icns", Icns(("ic08", Png(256, "app"))));

        // Act
        var icon = Extract("Game.app/Contents/MacOS/Game");

        // Assert
        icon!.Data.Should().Equal(Png(256, "app"));
    }

    [Fact]
    public void Game_folder_that_is_a_bare_bundle_uses_its_icns()
    {
        // Arrange
        _dir.File("Contents/Info.plist", Plist("Icon"))
            .File("Contents/MacOS/Game", "macho")
            .File("Contents/Resources/Icon.icns", Icns(("ic07", Png(128, "bare"))));

        // Act
        var icon = Extract("Contents/MacOS/Game");

        // Assert
        icon!.Data.Should().Equal(Png(128, "bare"));
    }

    [Fact]
    public void Icns_with_only_legacy_images_gives_no_icon()
    {
        // Arrange
        _dir.File("Game.app/Contents/Info.plist", Plist("Icon"))
            .File("Game.app/Contents/Resources/Icon.icns", Icns(("is32", [1, 2, 3, 4]), ("s8mk", [5, 6])));

        // Act
        var icon = Extract("Game.app");

        // Assert
        icon.Should().BeNull();
    }

    [Fact]
    public void Broken_icns_length_stops_reading_without_error()
    {
        // Arrange
        var icns = Icns(("ic08", Png(256, "ok")), ("ic09", Png(512, "cut")));
        BinaryPrimitives.WriteUInt32BigEndian(icns.AsSpan(8 + 8 + Png(256, "ok").Length + 4), 0xFFFFFF);

        // Act
        var png = GameIconExtractor.FromIcns(icns);

        // Assert
        png.Should().Equal(Png(256, "ok"));
    }

    [Fact]
    public void Plist_icon_name_cannot_leave_the_bundle()
    {
        // Arrange
        _dir.File("Game.app/Contents/Info.plist", Plist("../../../Secret"))
            .File("Secret.icns", Icns(("ic08", Png(256, "outside"))));

        // Act
        var icon = Extract("Game.app");

        // Assert
        icon.Should().BeNull();
    }

    // ---------- Linux: .desktop and PNGs ----------

    [Fact]
    public void Linux_desktop_file_icon_path_is_used()
    {
        // Arrange
        _dir.File("Game.x86_64", EngineFixture.Elf)
            .File("game.desktop", "[Desktop Entry]\nName=Game\nExec=./Game.x86_64\nIcon=art/game-icon.png\n")
            .File("art/game-icon.png", Png(128, "desktop"));

        // Act
        var icon = Extract("Game.x86_64");

        // Assert
        icon!.Data.Should().Equal(Png(128, "desktop"));
    }

    [Fact]
    public void Linux_desktop_theme_icon_name_falls_back_to_known_pngs()
    {
        // Arrange
        _dir.File("Game.x86_64", EngineFixture.Elf)
            .File("game.desktop", "[Desktop Entry]\nIcon=steam_icon_123\n")
            .File("icon.png", Png(64, "fallback"));

        // Act
        var icon = Extract("Game.x86_64");

        // Assert
        icon!.Data.Should().Equal(Png(64, "fallback"));
    }

    [Fact]
    public void Unity_player_icon_is_found()
    {
        // Arrange
        _dir.UnityLinux().File("Game_Data/Resources/UnityPlayer.png", Png(128, "unity"));

        // Act
        var icon = Extract("Game.x86_64");

        // Assert
        icon!.Data.Should().Equal(Png(128, "unity"));
    }

    [Fact]
    public void RenPy_window_icon_is_found()
    {
        // Arrange
        _dir.RenPy().File("game/gui/window_icon.png", Png(64, "renpy"));

        // Act
        var icon = Extract("Game.sh");

        // Assert
        icon!.Data.Should().Equal(Png(64, "renpy"));
    }

    [Fact]
    public void Png_named_like_the_executable_is_found()
    {
        // Arrange
        _dir.File("Moss.x86_64", EngineFixture.Elf).File("Moss.png", Png(96, "named"));

        // Act
        var icon = Extract("Moss.x86_64");

        // Assert
        icon!.Data.Should().Equal(Png(96, "named"));
    }

    [Fact]
    public void Windows_exe_without_icon_falls_back_to_a_png()
    {
        // Arrange
        _dir.File("Game.exe", Pe([])).File("icon.png", Png(64, "png"));

        // Act
        var icon = Extract("Game.exe");

        // Assert
        icon!.Data.Should().Equal(Png(64, "png"));
    }

    [Fact]
    public void File_named_png_that_is_not_a_png_is_ignored()
    {
        // Arrange
        _dir.File("Game.sh", "#!/bin/sh").File("icon.png", "not an image");

        // Act
        var icon = Extract("Game.sh");

        // Assert
        icon.Should().BeNull();
    }

    [Fact]
    public void Missing_game_folder_gives_no_icon()
    {
        // Arrange
        var missing = _dir.Combine("gone");

        // Act
        var icon = _extractor.Extract(missing, "Game.exe");

        // Assert
        icon.Should().BeNull();
    }
}
