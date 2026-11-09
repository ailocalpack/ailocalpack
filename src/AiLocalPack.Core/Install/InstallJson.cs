using System.Text.Json.Serialization;

namespace AiLocalPack.Core.Install;

/// <summary>On-disk shape of <c>installed-packs.json</c>, kept apart from the domain types.</summary>
internal sealed record InstalledPacksFile(int SchemaVersion, IReadOnlyList<InstalledPackJson?>? Packs, IReadOnlyList<InstalledDependencyJson?>? Dependencies = null);

internal sealed record InstalledPackJson(
    string? Game,
    string? Language,
    string? Pack,
    int Revision,
    DateTimeOffset InstalledAt,
    string? InstallPath,
    string? Backup,
    IReadOnlyList<InstalledFileJson?>? Files,
    IReadOnlyList<string>? AddedFolders,
    IReadOnlyList<string>? GeneratedFiles = null,
    bool? Local = null,
    string? Title = null);

internal sealed record InstalledDependencyJson(
    string? Game,
    string? Id,
    string? Version,
    IReadOnlyList<string>? UsedBy,
    DateTimeOffset InstalledAt,
    string? InstallPath,
    string? Backup,
    IReadOnlyList<InstalledFileJson?>? Files,
    IReadOnlyList<string>? AddedFolders,
    IReadOnlyList<string>? GeneratedFiles = null);

/// <param name="Original">SHA-256 of the original; <c>null</c> = the pack added the file.</param>
internal sealed record InstalledFileJson(string? Path, string? Original, string? Installed);

/// <summary>
/// <c>install-journal.json</c>: the transaction in progress, written before any game file changes, so a crash can be
/// undone (or finished) on the next start.
/// </summary>
/// <param name="Operation"><c>install</c> (also re-apply) or <c>uninstall</c>.</param>
/// <param name="Phase">
/// <c>backup</c> (game files untouched), <c>apply</c> (game files may be changed: roll back), <c>commit</c> (everything
/// written and checked: finish).
/// </param>
/// <param name="Transaction">This transaction's backup folder name.</param>
/// <param name="Replaces">The backup folder of the record it replaces (re-apply), deleted on commit.</param>
/// <param name="Pack">The record committed at the end (install only).</param>
/// <param name="CreatedFolders">Folders the transaction creates, deepest last (removed on rollback when empty).</param>
/// <param name="RemovesPack">Uninstall: the game's pack goes (<c>false</c>: only dependencies no pack uses).</param>
/// <param name="KeepDependencies">Uninstall: dependencies (<c>id@version</c>) kept for the next pack of a replacement.</param>
internal sealed record InstallJournalFile(
    int SchemaVersion,
    string? Operation,
    string? Phase,
    string? Game,
    string? InstallPath,
    string? Transaction,
    string? Replaces,
    InstalledPackJson? Pack,
    IReadOnlyList<JournalEntryJson?>? Entries,
    IReadOnlyList<string>? CreatedFolders,
    bool? RemovesPack = null,
    IReadOnlyList<string>? KeepDependencies = null);

/// <summary>How to put one file back as it was before the transaction.</summary>
/// <param name="Write">The transaction writes this file (others are only carried into the record).</param>
/// <param name="Before">SHA-256 of the file before; <c>null</c> = it didn't exist (rollback deletes it).</param>
/// <param name="BeforeCopy">Where its copy is: <c>backup</c> (the original in the backup) or <c>before</c> (a rollback-only copy).</param>
internal sealed record JournalEntryJson(string? Path, bool Write, string? Before, string? BeforeCopy);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(InstalledPacksFile))]
[JsonSerializable(typeof(InstallJournalFile))]
internal sealed partial class InstallJsonContext : JsonSerializerContext;
