using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Registry;

/// <summary>
/// When a pack is <c>verified</c> (task 64, decided): its current revision has at least
/// <see cref="RequiredConfirmations"/> counted "works" reports and more of them than "doesn't work" ones (counted =
/// <see cref="Ratings.RatingTally"/>'s rules: one per account, not an author or a bot, an account at least
/// <see cref="MinAccountAge"/> old, a Steam pack's report from one of its builds). <c>index build</c> works the status
/// out on every run, so nothing is committed; a maintainer's entry in <see cref="VerifiedPolicy"/> overrides it for one
/// revision, and a manifest a maintainer committed with <c>verified</c> counts too (pack PRs must be <c>alpha</c>).
/// </summary>
public static class RegistryVerification
{
    /// <summary>N: independent players who confirmed the revision works.</summary>
    public const int RequiredConfirmations = 3;

    /// <summary>A report counts only from an account at least this old when it was posted (fresh accounts are cheap).</summary>
    public static readonly TimeSpan MinAccountAge = TimeSpan.FromDays(30);

    /// <summary>The counted reports make the revision <c>verified</c>.</summary>
    public static bool IsConfirmed(RegistryRating rating)
    {
        ArgumentNullException.ThrowIfNull(rating);
        return rating.Works >= RequiredConfirmations && rating.Works > rating.DoesNotWork;
    }

    /// <summary>The status the index lists: a maintainer's override, else the manifest's <c>verified</c> or enough confirmations.</summary>
    public static PackStatus Status(PackStatus manifest, RegistryRating rating, PackStatus? overridden) =>
        overridden ?? (manifest == PackStatus.Verified || IsConfirmed(rating) ? PackStatus.Verified : PackStatus.Alpha);
}

/// <summary>
/// <c>policy/verified.json</c> (task 64): maintainers grant or revoke <c>verified</c> for one pack revision,
/// whatever the confirmations say: <c>{schemaVersion: 1, packs: [{pack: "steam:620/ru/full", revision: 3, status:
/// "verified" | "alpha", note?}]}</c>. An entry of an older revision no longer applies (a new revision is judged by the
/// rules again). The whole file is refused on any error, which stops <c>index build</c> like the other policy files.
/// </summary>
public static class VerifiedPolicy
{
    public const int CurrentSchemaVersion = 1;

    public const string Path = "policy/verified.json";

    /// <summary>The overrides by pack and revision, or every problem.</summary>
    public static (IReadOnlyDictionary<(RegistryPackKey Pack, int Revision), PackStatus>? Overrides, IReadOnlyList<PackFormatError> Errors) Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        VerifiedFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize(json, VerifiedPolicyJsonContext.Default.VerifiedFileJson);
        }
        catch (JsonException e)
        {
            return (null, [PackFormat.FromJsonException(e)]);
        }

        if (file is null)
        {
            return (null, [new PackFormatError("", "The file holds no list (null).")]);
        }

        if (PackFormat.CheckSchemaVersion(file.SchemaVersion, CurrentSchemaVersion) is { } versionError)
        {
            return (null, [versionError]);
        }

        var errors = new PackFormatErrors();
        var overrides = new Dictionary<(RegistryPackKey, int), PackStatus>();
        for (var i = 0; i < (file.Packs?.Count ?? 0); i++)
        {
            var field = $"packs[{i}]";
            if (file.Packs![i] is not { } entry)
            {
                errors.Add(field, "is empty.");
                continue;
            }

            var ok = true;
            if (!RegistryPackKey.TryParse(entry.Pack, out var key))
            {
                errors.Add($"{field}.pack", $"'{entry.Pack}' is not a pack (<game id>/<language>/<pack id>).");
                ok = false;
            }

            if (entry.Revision is not >= 1)
            {
                errors.Add($"{field}.revision", entry.Revision is null ? "is required." : $"must be 1 or more, not {entry.Revision}.");
                ok = false;
            }

            PackStatus status = PackStatus.Alpha;
            switch (entry.Status)
            {
                case "verified":
                    status = PackStatus.Verified;
                    break;
                case "alpha":
                    break;
                default:
                    errors.Add($"{field}.status", $"must be \"verified\" or \"alpha\", not {(entry.Status is null ? "missing" : $"\"{entry.Status}\"")}.");
                    ok = false;
                    break;
            }

            if (ok && !overrides.TryAdd((key, entry.Revision!.Value), status))
            {
                errors.Add(field, $"{key} rev {entry.Revision} is listed twice.");
            }
        }

        return errors.Any ? (null, errors.List) : (overrides, []);
    }
}

internal sealed record VerifiedFileJson(int? SchemaVersion, IReadOnlyList<VerifiedEntryJson?>? Packs);

internal sealed record VerifiedEntryJson(string? Pack, int? Revision, string? Status, string? Note);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(VerifiedFileJson))]
internal sealed partial class VerifiedPolicyJsonContext : JsonSerializerContext;
