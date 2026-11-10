using AiLocalPack.Core.Secrets;

namespace AiLocalPack.TestKit;

/// <summary>
/// An in-memory <see cref="ISecretStore"/> (the keychain in tests). <see cref="FailWith"/> makes every call answer with a
/// problem instead (a Linux machine without a keyring, a declined unlock prompt).
/// </summary>
public sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public SecretStoreKind Kind => SecretStoreKind.Memory;

    /// <summary>When set, every read, write and delete answers this.</summary>
    public SecretResult? FailWith { get; set; }

    /// <summary>The secrets kept, by name.</summary>
    public IReadOnlyDictionary<string, string> Secrets
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, string>(_secrets, StringComparer.Ordinal);
            }
        }
    }

    public Task<SecretResult> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(FailWith ?? (_secrets.TryGetValue(name, out var value) ? SecretResult.Ok(value) : SecretResult.NotFound));
        }
    }

    public Task<SecretResult> WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (FailWith is { } failure)
            {
                return Task.FromResult(failure);
            }

            _secrets[name] = value;
            return Task.FromResult(SecretResult.Ok());
        }
    }

    public Task<SecretResult> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (FailWith is { } failure)
            {
                return Task.FromResult(failure);
            }

            _secrets.Remove(name);
            return Task.FromResult(SecretResult.Ok());
        }
    }
}
