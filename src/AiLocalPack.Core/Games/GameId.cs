using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AiLocalPack.Core.Games;

public enum GameIdKind
{
    /// <summary><c>steam:&lt;appid&gt;</c>.</summary>
    Steam,

    /// <summary><c>fp:&lt;sha-256 hex&gt;</c>: a manually added game identified by its files.</summary>
    Fingerprint,

    /// <summary><c>epic:&lt;AppName&gt;</c>: the Epic Games Store's app name (task 84).</summary>
    Epic,

    /// <summary><c>gog:&lt;product id&gt;</c>: GOG's product ID, decimal (task 85).</summary>
    Gog,

    /// <summary><c>ea:&lt;content id&gt;</c>: the EA app's content ID (task 86).</summary>
    Ea,
}

/// <summary>
/// Stable identity of a game across machines: <c>steam:1091500</c> (Steam app ID, decimal, no leading zeros),
/// <c>fp:&lt;64 lowercase hex&gt;</c> (SHA-256 fingerprint of a manually added game, task 12), or another store's own ID
/// (task 83): <c>epic:&lt;AppName&gt;</c>, <c>gog:&lt;product id&gt;</c> (decimal, no leading zeros), <c>ea:&lt;content id&gt;</c>.
/// Epic and EA values are <see cref="MaxStoreValueLength"/> characters at most of <c>[A-Za-z0-9._-]</c>, starting and
/// ending with a letter or digit, so <c>RegistryLayout.GameFolder</c> is a valid file name on every OS. Only the
/// canonical form parses, so <see cref="Parse"/> and <see cref="ToString"/> round-trip and equal IDs have equal text.
/// </summary>
public sealed record GameId
{
    public const string SteamPrefix = "steam:";
    public const string FingerprintPrefix = "fp:";
    public const string EpicPrefix = "epic:";
    public const string GogPrefix = "gog:";
    public const string EaPrefix = "ea:";
    public const int FingerprintLength = 64;
    public const int MaxStoreValueLength = 64;

    /// <summary>The ID forms for error messages.</summary>
    public const string Forms = "steam:<app id>, fp:<64 lowercase hex>, epic:<AppName>, gog:<product id> or ea:<content id>";

    private static readonly (GameIdKind Kind, string Prefix)[] Prefixes =
    [
        (GameIdKind.Steam, SteamPrefix),
        (GameIdKind.Fingerprint, FingerprintPrefix),
        (GameIdKind.Epic, EpicPrefix),
        (GameIdKind.Gog, GogPrefix),
        (GameIdKind.Ea, EaPrefix),
    ];

    private GameId(GameIdKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    public GameIdKind Kind { get; }

    /// <summary>The part after the prefix: app ID digits, fingerprint hex, or the store's own ID.</summary>
    public string Value { get; }

    /// <summary>The Steam app ID, or <c>null</c> for a fingerprint.</summary>
    public uint? SteamAppId => Kind == GameIdKind.Steam ? uint.Parse(Value, NumberStyles.None, CultureInfo.InvariantCulture) : null;

    public static GameId Steam(uint appId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(appId);
        return new GameId(GameIdKind.Steam, appId.ToString(CultureInfo.InvariantCulture));
    }

    /// <param name="sha256Hex">64 lowercase hex characters.</param>
    public static GameId Fingerprint(string sha256Hex)
    {
        ArgumentNullException.ThrowIfNull(sha256Hex);
        return IsFingerprint(sha256Hex)
            ? new GameId(GameIdKind.Fingerprint, sha256Hex)
            : throw new ArgumentException($"A fingerprint is {FingerprintLength} lowercase hex characters: '{sha256Hex}'.", nameof(sha256Hex));
    }

    /// <summary>A store ID (<see cref="GameIdKind.Epic"/>, <see cref="GameIdKind.Gog"/>, <see cref="GameIdKind.Ea"/>); <c>null</c> when <paramref name="value"/> isn't canonical.</summary>
    public static GameId? Store(GameIdKind kind, string? value) =>
        kind is GameIdKind.Epic or GameIdKind.Gog or GameIdKind.Ea && TryParse(PrefixOf(kind) + value, out var id) ? id : null;

    /// <summary>The text prefix of <paramref name="kind"/>, e.g. <c>epic:</c>.</summary>
    public static string PrefixOf(GameIdKind kind) => Prefixes.First(p => p.Kind == kind).Prefix;

    /// <summary>Whether the ID comes from a store (not a fingerprint of the files).</summary>
    public bool IsStore => Kind != GameIdKind.Fingerprint;

    public static GameId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new FormatException($"Not a game ID: '{text}'.");

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out GameId? id)
    {
        id = null;
        if (text is null)
        {
            return false;
        }

        foreach (var (kind, prefix) in Prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                var value = text[prefix.Length..];
                if (IsCanonical(kind, value))
                {
                    id = new GameId(kind, value);
                }

                break;
            }
        }

        return id is not null;
    }

    public override string ToString() => PrefixOf(Kind) + Value;

    private static bool IsCanonical(GameIdKind kind, string value) => kind switch
    {
        GameIdKind.Steam => IsDecimal(value) && uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _),
        GameIdKind.Fingerprint => IsFingerprint(value),
        GameIdKind.Gog => IsDecimal(value) && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _),
        _ => IsStoreName(value),
    };

    private static bool IsDecimal(string digits) =>
        digits.Length > 0 && digits[0] != '0' && digits.All(char.IsAsciiDigit);

    private static bool IsStoreName(string value) =>
        value.Length is > 0 and <= MaxStoreValueLength
        && char.IsAsciiLetterOrDigit(value[0])
        && char.IsAsciiLetterOrDigit(value[^1])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static bool IsFingerprint(string hex) =>
        hex.Length == FingerprintLength && hex.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
