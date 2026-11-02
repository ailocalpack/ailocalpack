using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AiLocalPack.Core.Games;

public enum GameIdKind
{
    /// <summary><c>steam:&lt;appid&gt;</c>.</summary>
    Steam,

    /// <summary><c>fp:&lt;sha-256 hex&gt;</c>: a manually added game identified by its files.</summary>
    Fingerprint,
}

/// <summary>
/// Stable identity of a game across machines: <c>steam:1091500</c> (Steam app ID, decimal, no leading zeros) or
/// <c>fp:&lt;64 lowercase hex&gt;</c> (SHA-256 fingerprint of a manually added game, task 12). Only the canonical form
/// parses, so <see cref="Parse"/> and <see cref="ToString"/> round-trip and equal IDs have equal text.
/// </summary>
public sealed record GameId
{
    public const string SteamPrefix = "steam:";
    public const string FingerprintPrefix = "fp:";
    public const int FingerprintLength = 64;

    private GameId(GameIdKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    public GameIdKind Kind { get; }

    /// <summary>The part after the prefix: app ID digits or fingerprint hex.</summary>
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

    public static GameId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new FormatException($"Not a game ID: '{text}'.");

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out GameId? id)
    {
        id = null;
        if (text is null)
        {
            return false;
        }

        if (text.StartsWith(SteamPrefix, StringComparison.Ordinal))
        {
            var digits = text[SteamPrefix.Length..];
            if (digits.Length > 0
                && digits[0] != '0'
                && uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                id = new GameId(GameIdKind.Steam, digits);
            }
        }
        else if (text.StartsWith(FingerprintPrefix, StringComparison.Ordinal))
        {
            var hex = text[FingerprintPrefix.Length..];
            if (IsFingerprint(hex))
            {
                id = new GameId(GameIdKind.Fingerprint, hex);
            }
        }

        return id is not null;
    }

    public override string ToString() => (Kind == GameIdKind.Steam ? SteamPrefix : FingerprintPrefix) + Value;

    private static bool IsFingerprint(string hex) =>
        hex.Length == FingerprintLength && hex.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
