using System.Text;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Tests.Registry;

namespace AiLocalPack.Core.Tests.Packs;

public sealed class PackValidatorTests : IDisposable
{
    private const string PackPath = "games/steam-620/ru/full";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>A copy of the sample registry's <c>steam:620/ru/full</c> pack (valid) at <paramref name="at"/>; returns its folder.</summary>
    private string CopyPack(string at = PackPath)
    {
        _dir.CopyFolder(Path.Combine(RegistryFixtures.Folder, PackPath), at);
        return _dir.Combine(at);
    }

    private string Manifest(string folder) => Path.Combine(folder, "manifest.json");

    private void EditManifest(string folder, string from, string to) =>
        File.WriteAllText(Manifest(folder), File.ReadAllText(Manifest(folder)).Replace(from, to, StringComparison.Ordinal));

    [Fact]
    public void Sample_registry_packs_are_valid()
    {
        // Arrange
        var folders = Directory.GetDirectories(Path.Combine(RegistryFixtures.Folder, "games"), "*", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
            .ToList();

        // Act
        var problems = folders.SelectMany(f => PackValidator.Validate(f, TestContext.Current.CancellationToken).Problems).ToList();

        // Assert
        (folders.Count, problems.Count).Should().Be((4, 0));
    }

    [Fact]
    public void Valid_pack_returns_its_manifest()
    {
        // Arrange
        var folder = CopyPack();

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        (result.IsValid, result.Manifest?.Id).Should().Be((true, "full"));
    }

    [Fact]
    public void Missing_folder_is_a_problem()
    {
        // Act
        var result = PackValidator.Validate(_dir.Combine(PackPath), TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.Message.Should().Contain("doesn't exist");
    }

    [Fact]
    public void Missing_manifest_is_a_problem()
    {
        // Arrange
        var folder = CopyPack();
        File.Delete(Manifest(folder));

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().Contain(p => p.File == "manifest.json" && p.Message == "is missing.");
    }

    [Fact]
    public void Manifest_errors_carry_their_field()
    {
        // Arrange
        var folder = CopyPack();
        EditManifest(folder, "\"github\": \"bob-2\"", "\"github\": \"bob_2\"");

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().Contain(p => p.File == "manifest.json" && p.Field == "authors[1].github");
    }

    [Fact]
    public void Folder_not_named_after_the_pack_is_a_problem()
    {
        // Arrange
        var folder = CopyPack("games/steam-620/ru/other");

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.Message.Should().Contain("must be games/steam-620/ru/full/");
    }

    [Fact]
    public void Folder_of_another_language_is_a_problem()
    {
        // Arrange
        var folder = CopyPack("games/steam-620/de/full");

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.Message.Should().Contain("not games/steam-620/de/full/");
    }

    [Fact]
    public void Folder_outside_games_is_a_problem()
    {
        // Arrange
        var folder = CopyPack("packs/steam-620/ru/full");

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.Message.Should().Contain("must be games/steam-620/ru/full/");
    }

    [Fact]
    public void Wrong_string_table_hash_is_a_problem()
    {
        // Arrange
        var folder = CopyPack();
        EditManifest(folder, "5140d3f499f0bdfc7fbd01c224750f7effa27c7cf720911a99036572b6f3ef9b", new string('0', 64));

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.Field.Should().Be("strings.sha256");
    }

    [Fact]
    public void Wrong_string_table_size_is_a_problem()
    {
        // Arrange
        var folder = CopyPack();
        EditManifest(folder, "\"size\": 142", "\"size\": 143");

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.Field.Should().Be("strings.size");
    }

    [Fact]
    public void Missing_string_table_is_a_problem()
    {
        // Arrange
        var folder = CopyPack();
        File.Delete(Path.Combine(folder, "strings", "strings.json.gz"));

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.Should().Match<PackProblem>(p =>
            p.File == "strings/strings.json.gz" && p.Message.StartsWith("is missing", StringComparison.Ordinal));
    }

    /// <summary>Replaces the string table with <paramref name="table"/> and puts its size and hash in the manifest.</summary>
    private void ReplaceTable(string folder, StringTable table)
    {
        using var stream = new MemoryStream();
        StringTableSerializer.Write(table, stream);
        var bytes = stream.ToArray();
        File.WriteAllBytes(Path.Combine(folder, "strings", "strings.json.gz"), bytes);
        EditManifest(folder, "5140d3f499f0bdfc7fbd01c224750f7effa27c7cf720911a99036572b6f3ef9b",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        EditManifest(folder, "\"size\": 142", $"\"size\": {bytes.Length}");
    }

    [Fact]
    public void String_table_in_another_language_is_a_problem()
    {
        // Arrange
        var folder = CopyPack();
        ReplaceTable(folder, new StringTable("de", new Dictionary<string, string> { ["a"] = "b" }));

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.Should().Match<PackProblem>(p =>
            p.File == "strings/strings.json.gz" && p.Field == "language");
    }

    [Fact]
    public void Broken_string_table_is_a_problem()
    {
        // Arrange
        var folder = CopyPack();
        var bytes = Encoding.UTF8.GetBytes("""{"schemaVersion": 1, "language": "ru"}""");
        File.WriteAllBytes(Path.Combine(folder, "strings", "strings.json.gz"), bytes);
        EditManifest(folder, "5140d3f499f0bdfc7fbd01c224750f7effa27c7cf720911a99036572b6f3ef9b",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        EditManifest(folder, "\"size\": 142", $"\"size\": {bytes.Length}");

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.Should().Match<PackProblem>(p =>
            p.File == "strings/strings.json.gz" && p.Field == "strings");
    }

    [Fact]
    public void Extra_file_is_a_problem()
    {
        // Arrange
        var folder = CopyPack();
        _dir.File($"{PackPath}/README.md", "notes");

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.Should().Match<PackProblem>(p =>
            p.File == "README.md" && p.Message.Contains("not part of the pack", StringComparison.Ordinal));
    }

    [Fact]
    public void Executable_disguised_as_json_is_reported_once()
    {
        // Arrange
        var folder = CopyPack();
        _dir.File($"{PackPath}/strings/extra.json", [.. "MZ"u8, 0x90, 0x00]);

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.Should().Match<PackProblem>(p =>
            p.File == "strings/extra.json" && p.Message.Contains("Windows executable", StringComparison.Ordinal));
    }

    [Fact]
    public void Executable_in_place_of_the_string_table_is_a_problem()
    {
        // Arrange
        var folder = CopyPack();
        File.WriteAllBytes(Path.Combine(folder, "strings", "strings.json.gz"), [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1]);

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().Contain(p => p.File == "strings/strings.json.gz" && p.Message.Contains("Linux executable", StringComparison.Ordinal));
    }

    [Fact]
    public void File_over_the_size_limit_is_a_problem()
    {
        // Arrange
        var folder = CopyPack();
        using (var big = File.Create(Path.Combine(folder, "strings", "big.json")))
        {
            big.SetLength(PackValidator.MaxFileSize + 1);
        }

        // Act
        var result = PackValidator.Validate(folder, TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().Contain(p => p.File == "strings/big.json" && p.Message.Contains("limit for one file", StringComparison.Ordinal));
    }
}
