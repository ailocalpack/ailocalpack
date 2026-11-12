using System.Text.Json;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Secrets;

/// <summary>
/// Development and tests only (<c>AILOCALPACK_SECRETS_FILE</c>): secrets in a plain-text JSON file
/// <c>{ "&lt;service&gt;": { "&lt;name&gt;": "&lt;secret&gt;" } }</c>, written atomically. A damaged file reads as empty and
/// is replaced on the next write. Not thread-safe across processes beyond the atomic replace.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private readonly string _file;
    private readonly string _service;
    private readonly Lock _lock = new();

    public FileSecretStore(string file, string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        _file = file;
        _service = service;
    }

    public SecretStoreKind Kind => SecretStoreKind.File;

    public Task<SecretResult> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_lock)
        {
            return Task.FromResult(Load() is { } all && all.TryGetValue(_service, out var secrets) && secrets.TryGetValue(name, out var value)
                ? SecretResult.Ok(value)
                : SecretResult.NotFound);
        }
    }

    public Task<SecretResult> WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        return Task.FromResult(Change(secrets => secrets[name] = value));
    }

    public Task<SecretResult> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Task.FromResult(Change(secrets => secrets.Remove(name)));
    }

    private SecretResult Change(Action<Dictionary<string, string>> change)
    {
        lock (_lock)
        {
            var all = Load() ?? [];
            if (!all.TryGetValue(_service, out var secrets))
            {
                secrets = [];
                all[_service] = secrets;
            }

            change(secrets);
            try
            {
                AtomicFile.WriteAllBytes(_file, JsonSerializer.SerializeToUtf8Bytes(all, SecretsJsonContext.Default.DictionaryStringDictionaryStringString));
                return SecretResult.Ok();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return SecretResult.Failed($"Can't write {_file}: {e.Message}");
            }
        }
    }

    private Dictionary<string, Dictionary<string, string>>? Load()
    {
        try
        {
            return File.Exists(_file)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(_file), SecretsJsonContext.Default.DictionaryStringDictionaryStringString)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true, NewLine = "\n")]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
internal sealed partial class SecretsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
