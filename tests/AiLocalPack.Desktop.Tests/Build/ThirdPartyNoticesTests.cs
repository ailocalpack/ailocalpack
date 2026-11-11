using AiLocalPack.Build;
using AiLocalPack.Desktop.ViewModels.Settings.About;

namespace AiLocalPack.Desktop.Tests.Build;

/// <summary>The third-party notices generator (<c>build/ThirdPartyNotices.cs</c>, task 67) and what the build embedded.</summary>
public sealed class ThirdPartyNoticesTests
{
    private static readonly IReadOnlyDictionary<string, string> Texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MIT"] = "MIT text",
        ["Apache-2.0"] = "Apache text",
    };

    private static string Nuspec(string license, string extra = "") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>Sample.Lib</id>
            <version>1.2.3</version>
            <authors>Jane Doe</authors>
            {license}
            <projectUrl>https://example.org/sample</projectUrl>
            {extra}
          </metadata>
        </package>
        """;

    private static NoticeComponent Component(string name, string? license = "MIT", string? text = null, string? url = null) =>
        new(name, "1.0.0", license, text, url, null, null);

    [Fact]
    public void Nuspec_with_an_expression_gives_its_license()
    {
        // Arrange
        var xml = Nuspec("""<license type="expression">MIT</license>""", "<copyright>© Jane</copyright>");

        // Act
        var component = ThirdPartyNotices.FromNuspec(xml, _ => null);

        // Assert
        component.Should().BeEquivalentTo(new
        {
            Name = "Sample.Lib",
            Version = "1.2.3",
            License = "MIT",
            LicenseText = (string?)null,
            ProjectUrl = "https://example.org/sample",
            Copyright = "© Jane",
        });
    }

    [Fact]
    public void Nuspec_without_a_copyright_names_the_authors()
    {
        // Arrange
        var xml = Nuspec("""<license type="expression">MIT</license>""");

        // Act
        var component = ThirdPartyNotices.FromNuspec(xml, _ => null);

        // Assert
        component.Copyright.Should().Be("Authors: Jane Doe");
    }

    [Fact]
    public void Nuspec_with_a_license_file_reads_it_from_the_package()
    {
        // Arrange
        var xml = Nuspec("""<license type="file">docs/LICENSE.txt</license>""");

        // Act
        var component = ThirdPartyNotices.FromNuspec(xml, file => file == "docs/LICENSE.txt" ? "Own terms" : null);

        // Assert
        (component.License, component.LicenseText).Should().Be(((string?)null, "Own terms"));
    }

    [Fact]
    public void Nuspec_with_only_a_license_url_keeps_the_url()
    {
        // Arrange
        var xml = Nuspec("<licenseUrl>https://example.org/LICENSE</licenseUrl>");

        // Act
        var component = ThirdPartyNotices.FromNuspec(xml, _ => null);

        // Assert
        (component.License, component.LicenseUrl).Should().Be(((string?)null, "https://example.org/LICENSE"));
    }

    [Fact]
    public void Override_replaces_a_url_only_license()
    {
        // Arrange
        var xml = Nuspec("<licenseUrl>https://example.org/LICENSE</licenseUrl>");

        // Act
        var component = ThirdPartyNotices.FromNuspec(xml, _ => null, "MIT");

        // Assert
        (component.License, component.LicenseUrl).Should().Be(("MIT", (string?)null));
    }

    [Fact]
    public void Expression_ids_leave_out_operators_and_exceptions()
    {
        // Arrange
        const string expression = "(MIT OR Apache-2.0) AND GPL-2.0-only WITH Classpath-exception-2.0";

        // Act
        var ids = ThirdPartyNotices.LicenseIds(expression);

        // Assert
        ids.Should().Equal("MIT", "Apache-2.0", "GPL-2.0-only");
    }

    [Fact]
    public void Components_are_listed_by_name()
    {
        // Arrange
        var components = new[] { Component("Zeta"), Component("alpha"), Component("Beta") };

        // Act
        var text = ThirdPartyNotices.Render(components, Texts).Text;

        // Assert
        text.IndexOf("alpha 1.0.0", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Beta 1.0.0", StringComparison.Ordinal))
            .And.BeLessThan(text.IndexOf("Zeta 1.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_license_text_appears_once()
    {
        // Arrange
        var components = new[] { Component("A"), Component("B"), Component("C", "MIT OR Apache-2.0") };

        // Act
        var result = ThirdPartyNotices.Render(components, Texts);

        // Assert
        (CountOf(result.Text, "MIT text"), CountOf(result.Text, "Apache text"), result.Problems.Count).Should().Be((1, 1, 0));
    }

    [Fact]
    public void Component_license_file_is_printed_under_its_name()
    {
        // Arrange
        var components = new[] { Component("Own", license: null, text: "Own terms\r\nline two") };

        // Act
        var text = ThirdPartyNotices.Render(components, Texts).Text;

        // Assert
        text.Should().Contain("Own 1.0.0: license\n\nOwn terms\nline two\n").And.NotContain("\r");
    }

    [Fact]
    public void License_without_a_text_is_a_problem()
    {
        // Arrange
        var components = new[] { Component("Lib", "BSD-3-Clause") };

        // Act
        var problems = ThirdPartyNotices.Render(components, Texts).Problems;

        // Assert
        problems.Should().ContainSingle().Which.Should().Contain("BSD-3-Clause").And.Contain("build/licenses/BSD-3-Clause.txt");
    }

    [Fact]
    public void Url_only_license_is_a_problem()
    {
        // Arrange
        var components = new[] { Component("Old", license: null, url: "https://example.org/LICENSE") };

        // Act
        var problems = ThirdPartyNotices.Render(components, Texts).Problems;

        // Assert
        problems.Should().ContainSingle().Which.Should().Contain("NoticeLicense");
    }

    [Fact]
    public void Same_component_twice_is_listed_once()
    {
        // Arrange
        var components = new[] { Component("Lib"), Component("Lib") };

        // Act
        var text = ThirdPartyNotices.Render(components, Texts).Text;

        // Assert
        CountOf(text, "Lib 1.0.0").Should().Be(1);
    }

    [Fact]
    public void Build_embeds_the_notices_of_the_shipped_packages()
    {
        // Arrange
        var expected = new[] { "Avalonia 12.1.5", "Velopack 1.2.161", "Noto Sans fonts (bundled)", ".NET runtime", "\nOFL-1.1\n", "\nMIT\n", "\nApache-2.0\n" };

        // Act
        var text = ThirdPartyNoticesText.Load();

        // Assert
        text.Should().NotBeNull().And.ContainAll(expected);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
