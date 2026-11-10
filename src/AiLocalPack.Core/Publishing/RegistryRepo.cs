using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AiLocalPack.Core.Publishing;

/// <summary>
/// The registry repository packs are published to: <c>ailocalpack/registry</c>, or a test registry
/// (<c>pack publish --registry &lt;owner&gt;/&lt;repo&gt;</c>). Patch archives end up as assets of its
/// <see cref="PatchReleaseTag"/> Release (registry CI copies them there from the player's fork).
/// </summary>
public sealed partial record RegistryRepo(string Owner, string Name)
{
    /// <summary>The Release (tag) holding patch archives, in the registry and in the player's fork alike.</summary>
    public const string PatchReleaseTag = "patches";

    /// <summary>Dev: publishes to another registry repository (<c>&lt;owner&gt;/&lt;repo&gt;</c>, e.g. a test registry) instead of <see cref="Default"/>.</summary>
    public const string Variable = "AILOCALPACK_REGISTRY";

    public static RegistryRepo Default { get; } = new("ailocalpack", "registry");

    /// <summary>The registry to publish to: <see cref="Variable"/> when it names a repository, else <see cref="Default"/>.</summary>
    public static RegistryRepo Resolve(Storage.OsEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return TryParse(env.GetVariable(Variable), out var repo) ? repo : Default;
    }

    public string FullName => $"{Owner}/{Name}";

    /// <summary>Where the registry serves a patch archive once CI copied it: <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;/releases/download/patches/&lt;file&gt;</c>.</summary>
    public Uri PatchUrl(string fileName) => new($"https://github.com/{FullName}/releases/download/{PatchReleaseTag}/{Uri.EscapeDataString(fileName)}");

    /// <summary>Reads <c>owner/repo</c> (GitHub's name rules: letters, digits, <c>-</c>; repository also <c>_</c> and <c>.</c>).</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out RegistryRepo? repo)
    {
        repo = null;
        if (text is null || NamePattern().Match(text.Trim()) is not { Success: true } match)
        {
            return false;
        }

        repo = new RegistryRepo(match.Groups["owner"].Value, match.Groups["name"].Value);
        return true;
    }

    public override string ToString() => FullName;

    [GeneratedRegex(@"^(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<name>[A-Za-z0-9._-]{1,100})$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
