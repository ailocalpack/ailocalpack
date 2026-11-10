using AiLocalPack.Desktop.Agents;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AiLocalPack.Desktop.Tests.Settings;

public sealed class AgentInfoTests
{
    [Fact]
    public void Claude_tile_shows_its_brand_mark()
    {
        // Arrange
        var claude = AgentInfo.Claude;

        // Act
        var logo = (claude.HasBrandLogo, claude.HasIconLogo, claude.HasMark);

        // Assert
        logo.Should().Be((true, false, false));
    }

    [Fact]
    public void Codex_tile_shows_a_neutral_icon_instead_of_the_openai_mark()
    {
        // Arrange
        var codex = AgentInfo.Codex;

        // Act
        var logo = (codex.LogoKey, codex.HasBrandLogo, codex.HasIconLogo);

        // Assert
        logo.Should().Be(("IconTerminal", false, true));
    }

    [Fact]
    public void Agent_the_app_does_not_describe_shows_two_letters()
    {
        // Arrange
        var id = "gemini";

        // Act
        var info = AgentInfo.Of(id);

        // Assert
        (info.HasMark, info.Mark).Should().Be((true, "ge"));
    }

    [Fact]
    public Task Every_logo_geometry_is_an_app_resource() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        string[] keys = [AgentInfo.Claude.LogoKey!, AgentInfo.Codex.LogoKey!, "BrandSteam", "BrandGitHub", "BrandUnity", "BrandUnreal", "BrandGodot", "BrandRenPy"];

        // Act
        var found = keys.Where(k => Application.Current!.TryFindResource(k, out var r) && r is Geometry).ToList();

        // Assert
        found.Should().Equal(keys);
    });
}
