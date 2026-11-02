using AiLocalPack.Core.Steam;

namespace AiLocalPack.Core.Tests.Steam;

public sealed class VdfParserTests
{
    private const string NewLibraryFolders = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"contentid"		"5473928412475289151"
        		"totalsize"		"0"
        		"apps"
        		{
        			"228980"		"433187467"
        			"1091500"		"70318932140"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"label"		"Games"
        		"apps"
        		{
        		}
        	}
        }
        """;

    private const string OldLibraryFolders = """
        "LibraryFolders"
        {
        	"TimeNextStatsReport"		"1612345678"
        	"ContentStatsID"		"-1234567890123456789"
        	"1"		"D:\\SteamLibrary"
        	"2"		"/media/sdcard/SteamLibrary"
        }
        """;

    private const string AppManifest = """
        "AppState"
        {
        	"appid"		"1091500"
        	"Universe"		"1"
        	"name"		"Cyberpunk 2077"
        	"StateFlags"		"4"
        	"installdir"		"Cyberpunk 2077"
        	"LastUpdated"		"1700000000"
        	"buildid"		"12345678"
        	"InstalledDepots"
        	{
        		"1091501"
        		{
        			"manifest"		"7023567417946286458"
        			"size"		"70318932140"
        		}
        	}
        	"UserConfig"
        	{
        		"language"		"english"
        	}
        }
        """;

    [Fact]
    public void Reads_the_new_libraryfolders_format_with_nested_blocks()
    {
        // Arrange
        var text = NewLibraryFolders;

        // Act
        var document = VdfParser.Parse(text);

        // Assert
        var folders = document.GetObject("libraryfolders")!;
        folders.Select(p => p.Key).Should().Equal("0", "1");
        folders.GetObject("0")!.GetString("path").Should().Be(@"C:\Program Files (x86)\Steam");
        folders.GetObject("0")!.GetObject("apps")!.GetString("1091500").Should().Be("70318932140");
        folders.GetObject("1")!.GetObject("apps").Should().BeEmpty();
    }

    [Fact]
    public void Reads_the_old_libraryfolders_format_with_flat_numbered_paths()
    {
        // Arrange
        var text = OldLibraryFolders;

        // Act
        var document = VdfParser.Parse(text);

        // Assert
        var folders = document.GetObject("LibraryFolders")!;
        folders.GetString("1").Should().Be(@"D:\SteamLibrary");
        folders.GetString("2").Should().Be("/media/sdcard/SteamLibrary");
        folders.GetString("ContentStatsID").Should().Be("-1234567890123456789");
    }

    [Fact]
    public void Reads_an_app_manifest()
    {
        // Arrange
        var text = AppManifest;

        // Act
        var document = VdfParser.Parse(text);

        // Assert
        var app = document.GetObject("AppState")!;
        app.GetString("appid").Should().Be("1091500");
        app.GetString("name").Should().Be("Cyberpunk 2077");
        app.GetString("buildid").Should().Be("12345678");
        app.GetString("installdir").Should().Be("Cyberpunk 2077");
        app.GetObject("InstalledDepots")!.GetObject("1091501")!.GetString("manifest").Should().Be("7023567417946286458");
    }

    [Fact]
    public void Looks_up_keys_case_insensitively()
    {
        // Arrange
        var document = VdfParser.Parse(AppManifest);

        // Act
        var buildId = document.GetObject("appstate")!.GetString("BUILDID");

        // Assert
        buildId.Should().Be("12345678");
    }

    [Fact]
    public void Keeps_duplicate_keys_in_order_and_looks_up_the_first()
    {
        // Arrange
        var text = """ "root" { "k" "first" "K" "second" } """;

        // Act
        var root = VdfParser.Parse(text).GetObject("root")!;

        // Assert
        root.Select(p => ((VdfString)p.Value).Value).Should().Equal("first", "second");
        root.GetString("k").Should().Be("first");
    }

    [Fact]
    public void Missing_keys_and_wrong_value_kinds_give_null()
    {
        // Arrange
        var app = VdfParser.Parse(AppManifest).GetObject("AppState")!;

        // Act
        var lookups = new object?[] { app.GetString("nope"), app.GetString("UserConfig"), app.GetObject("name"), app["nope"] };

        // Assert
        lookups.Should().AllSatisfy(v => v.Should().BeNull());
    }

    [Fact]
    public void Unescapes_quotes_backslashes_and_control_sequences()
    {
        // Arrange
        var text = """ "k" "say \"hi\"\\ok\n\tend" """;

        // Act
        var value = VdfParser.Parse(text).GetString("k");

        // Assert
        value.Should().Be("say \"hi\"\\ok\n\tend");
    }

    [Fact]
    public void Keeps_a_backslash_before_an_unknown_escape()
    {
        // Arrange
        var text = """ "path" "D:\Games\Steam" """;

        // Act
        var value = VdfParser.Parse(text).GetString("path");

        // Assert
        value.Should().Be(@"D:\Games\Steam");
    }

    [Fact]
    public void Skips_line_comments_but_not_slashes_inside_strings()
    {
        // Arrange
        var text = """
            // header comment
            "root" // trailing comment
            {
                "url" "https://example.com//x" // another
                // "ignored" "value"
            }
            """;

        // Act
        var root = VdfParser.Parse(text).GetObject("root")!;

        // Assert
        root.Should().ContainSingle();
        root.GetString("url").Should().Be("https://example.com//x");
    }

    [Fact]
    public void Ignores_conditionals_and_keeps_their_properties()
    {
        // Arrange
        var text = """
            "root"
            {
                "a" "1" [$WIN32]
                "b" [$OSX||$LINUX] "2"
                "c" [!$X360]
                {
                    "d" "3"
                } [$WIN32]
            }
            """;

        // Act
        var root = VdfParser.Parse(text).GetObject("root")!;

        // Assert
        root.GetString("a").Should().Be("1");
        root.GetString("b").Should().Be("2");
        root.GetObject("c")!.GetString("d").Should().Be("3");
    }

    [Fact]
    public void Reads_bare_unquoted_tokens()
    {
        // Arrange
        var text = "root { key value nested { x 1 } }";

        // Act
        var root = VdfParser.Parse(text).GetObject("root")!;

        // Assert
        root.GetString("key").Should().Be("value");
        root.GetObject("nested")!.GetString("x").Should().Be("1");
    }

    [Fact]
    public void Skips_a_byte_order_mark()
    {
        // Arrange
        var text = "\uFEFF\"k\" \"v\"";

        // Act
        var document = VdfParser.Parse(text);

        // Assert
        document.GetString("k").Should().Be("v");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n // only a comment\n")]
    public void Empty_input_gives_an_empty_document(string text)
    {
        // Arrange
        // (text from the theory data)

        // Act
        var document = VdfParser.Parse(text);

        // Assert
        document.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"root\"\n{\n  \"k\" \"v\"\n", 4, 1, "'{' at line 2 is never closed")]
    [InlineData("\"root\"\n{\n}\n}", 4, 1, "unexpected '}'")]
    [InlineData("\"root\"\n{\n  \"k\" \"unterminated\n}", 3, 7, "not closed with '\"'")]
    [InlineData("\"root\"\n{\n  \"k\"\n}", 4, 1, "key 'k' has no value")]
    [InlineData("\"k\"", 1, 4, "key 'k' has no value")]
    [InlineData("{ \"k\" \"v\" }", 1, 1, "expected a key")]
    [InlineData("\"root\"\n{\n  \"k\" \"v\" [$WIN32\n}", 3, 11, "conditional is not closed")]
    public void Malformed_input_reports_the_line_and_column(string text, int line, int column, string reason)
    {
        // Arrange
        // (text from the theory data)

        // Act
        var act = () => VdfParser.Parse(text);

        // Assert
        var error = act.Should().Throw<VdfParseException>().Which;
        error.Line.Should().Be(line);
        error.Column.Should().Be(column);
        error.Reason.Should().Contain(reason);
        error.Message.Should().Contain($"line {line}");
    }

    [Fact]
    public void Rejects_nesting_deeper_than_the_limit()
    {
        // Arrange
        var depth = VdfParser.MaxDepth + 1;
        var text = string.Concat(Enumerable.Repeat("k {", depth)) + string.Concat(Enumerable.Repeat("}", depth));

        // Act
        var act = () => VdfParser.Parse(text);

        // Assert
        act.Should().Throw<VdfParseException>().Which.Reason.Should().Contain("nesting");
    }

    [Fact]
    public void Parses_a_file_from_disk()
    {
        // Arrange
        using var dir = new TempDir().File("steamapps/appmanifest_1091500.acf", AppManifest);

        // Act
        var document = VdfParser.ParseFile(dir.Combine("steamapps/appmanifest_1091500.acf"));

        // Assert
        document.GetObject("AppState")!.GetString("appid").Should().Be("1091500");
    }

    [Fact]
    public void A_missing_file_throws_an_io_error()
    {
        // Arrange
        using var dir = new TempDir();

        // Act
        var act = () => VdfParser.ParseFile(dir.Combine("missing.vdf"));

        // Assert
        act.Should().Throw<FileNotFoundException>();
    }
}
