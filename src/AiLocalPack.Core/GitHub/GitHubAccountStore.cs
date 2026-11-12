using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Core.GitHub;

/// <summary>The GitHub account the app is signed in as (the token itself is in the keychain).</summary>
/// <param name="Scopes">What the token was granted.</param>
public sealed record GitHubAccount(string Login, string? Name, Uri? AvatarUrl, Uri ProfileUrl, IReadOnlyList<string> Scopes, DateTimeOffset SignedInAt)
{
    public static GitHubAccount From(GitHubUser user, IReadOnlyList<string> scopes, DateTimeOffset signedInAt)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(scopes);
        return new GitHubAccount(user.Login, user.Name, user.AvatarUrl, user.ProfileUrl, scopes, signedInAt);
    }
}

/// <summary>
/// <c>&lt;data&gt;/github-account.json</c>: <see cref="GitHubAccount"/> for showing who is signed in without network.
/// Atomic writes; a damaged file is moved aside as <c>.&lt;utc&gt;.bak</c> and reads as signed out; a newer-format file
/// is read as signed out and never overwritten or deleted. Never throws for disk state.
/// </summary>
internal sealed class GitHubAccountStore(AppPaths paths, TimeProvider time, ILogger logger)
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _file = paths.GitHubAccountFile;
    private bool _newerFormat;

    public GitHubAccount? Load()
    {
        _newerFormat = false;
        byte[] bytes;
        try
        {
            if (!File.Exists(_file))
            {
                return null;
            }

            bytes = File.ReadAllBytes(_file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Can't read {File}", _file);
            return null;
        }

        GitHubAccountJson? json;
        try
        {
            json = JsonSerializer.Deserialize(bytes, GitHubJsonContext.Default.GitHubAccountJson);
        }
        catch (JsonException e)
        {
            MoveAside(e);
            return null;
        }

        if (json?.SchemaVersion > CurrentSchemaVersion)
        {
            logger.LogWarning("{File} has schema {Version}, newer than this app; read as signed out and left as it is", _file, json.SchemaVersion);
            _newerFormat = true;
            return null;
        }

        if (json is null || string.IsNullOrWhiteSpace(json.Login) || !Uri.TryCreate(json.ProfileUrl, UriKind.Absolute, out var profile))
        {
            MoveAside(null);
            return null;
        }

        Uri.TryCreate(json.AvatarUrl, UriKind.Absolute, out var avatar);
        return new GitHubAccount(json.Login, json.Name, avatar, profile, json.Scopes?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [], json.SignedInAt);
    }

    /// <summary><c>false</c> when the file couldn't be written (logged).</summary>
    public bool Save(GitHubAccount account)
    {
        Load();
        if (_newerFormat)
        {
            return false;
        }

        var json = new GitHubAccountJson(
            CurrentSchemaVersion, account.Login, account.Name, account.AvatarUrl?.AbsoluteUri, account.ProfileUrl.AbsoluteUri, account.Scopes, account.SignedInAt.ToUniversalTime());
        try
        {
            AtomicFile.WriteAllBytes(_file, JsonSerializer.SerializeToUtf8Bytes(json, GitHubJsonContext.Default.GitHubAccountJson));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError(e, "Saving {File} failed", _file);
            return false;
        }
    }

    public void Delete()
    {
        Load();
        if (_newerFormat)
        {
            return;
        }

        try
        {
            File.Delete(_file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError(e, "Deleting {File} failed", _file);
        }
    }

    private void MoveAside(Exception? e)
    {
        var backup = $"{_file}.{time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.bak";
        logger.LogError(e, "{File} is damaged; moved to {Backup}", _file, backup);
        try
        {
            File.Move(_file, backup, overwrite: true);
        }
        catch (Exception move) when (move is IOException or UnauthorizedAccessException)
        {
            logger.LogError(move, "Can't move {File} aside", _file);
        }
    }
}

internal sealed record GitHubAccountJson(
    int SchemaVersion,
    string? Login,
    string? Name,
    string? AvatarUrl,
    string? ProfileUrl,
    IReadOnlyList<string>? Scopes,
    DateTimeOffset SignedInAt);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GitHubAccountJson))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
