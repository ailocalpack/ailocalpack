using System.Text;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace AiLocalPack.Core.Secrets;

/// <summary>
/// Linux: the freedesktop Secret Service API over the session D-Bus (GNOME Keyring, KWallet 5.97+, KeePassXC), through
/// Tmds.DBus.Protocol (the D-Bus library Avalonia uses). One item per secret in the default collection, attributes
/// <c>service</c> + <c>account</c> (the secret's name), label <c>&lt;service&gt; &lt;name&gt;</c>. The transfer session
/// is <c>plain</c> (the secret travels over the local session bus, as with libsecret's default for other clients);
/// a locked collection or item is unlocked through the service's own prompt. No Secret Service running, no session bus
/// (a bare console, a container) or no default collection is <see cref="SecretStatus.Unavailable"/>: the secret is not
/// saved, never written to a file instead.
/// </summary>
public sealed class SecretServiceStore : ISecretStore
{
    private const string Bus = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    private const string NoObject = "/";

    /// <summary>How long an unlock or confirm prompt may stay open.</summary>
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(5);

    private readonly string _service;
    private readonly ILogger<SecretServiceStore> _logger;

    public SecretServiceStore(string service, ILogger<SecretServiceStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(logger);
        _service = service;
        _logger = logger;
    }

    public SecretStoreKind Kind => SecretStoreKind.SecretService;

    public Task<SecretResult> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunAsync("read", async session =>
        {
            var (unlocked, locked) = await session.SearchAsync(Attributes(name)).ConfigureAwait(false);
            var item = unlocked.FirstOrDefault();
            if (item is null && locked.FirstOrDefault() is { } lockedItem)
            {
                if (!await session.UnlockAsync(lockedItem, cancellationToken).ConfigureAwait(false))
                {
                    return SecretResult.Failed("The keyring stayed locked (the unlock prompt was dismissed).");
                }

                item = lockedItem;
            }

            if (item is null)
            {
                return SecretResult.NotFound;
            }

            var bytes = await session.GetSecretAsync(item).ConfigureAwait(false);
            try
            {
                return SecretResult.Ok(new UTF8Encoding(false, true).GetString(bytes));
            }
            catch (DecoderFallbackException)
            {
                return SecretResult.Failed("The keyring item isn't UTF-8 text.");
            }
            finally
            {
                Array.Clear(bytes);
            }
        });
    }

    public Task<SecretResult> WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        return RunAsync("save", async session =>
        {
            var collection = await session.DefaultCollectionAsync().ConfigureAwait(false);
            if (collection == NoObject)
            {
                return SecretResult.Unavailable("The keyring has no default collection to keep secrets in; create one in its settings (e.g. Seahorse, KWalletManager).");
            }

            if (!await session.UnlockAsync(collection, cancellationToken).ConfigureAwait(false))
            {
                return SecretResult.Failed("The keyring stayed locked (the unlock prompt was dismissed).");
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                return await session.CreateItemAsync(collection, $"{_service} {name}", Attributes(name), bytes, cancellationToken).ConfigureAwait(false)
                    ? SecretResult.Ok()
                    : SecretResult.Failed("Saving to the keyring was cancelled at its prompt.");
            }
            finally
            {
                Array.Clear(bytes);
            }
        });
    }

    public Task<SecretResult> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunAsync("delete", async session =>
        {
            var (unlocked, locked) = await session.SearchAsync(Attributes(name)).ConfigureAwait(false);
            foreach (var item in unlocked.Concat(locked))
            {
                if (!await session.DeleteItemAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    return SecretResult.Failed("Deleting from the keyring was cancelled at its prompt.");
                }
            }

            return SecretResult.Ok();
        });
    }

    private Dictionary<string, string> Attributes(string name) => new(StringComparer.Ordinal) { ["service"] = _service, ["account"] = name };

    private async Task<SecretResult> RunAsync(string action, Func<Session, Task<SecretResult>> work)
    {
        if (DBusAddress.Session is not { } address)
        {
            return SecretResult.Unavailable("There is no D-Bus session bus (no desktop session), so there is no keyring to keep the secret in.");
        }

        try
        {
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            var session = await Session.OpenAsync(connection).ConfigureAwait(false);
            return await work(session).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException e) when (e.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner"
            or "org.freedesktop.DBus.Error.Spawn.ServiceNotFound" or "org.freedesktop.DBus.Error.Spawn.ChildExited")
        {
            _logger.LogWarning("No Secret Service on the session bus: {Error}", e.ErrorMessage);
            return SecretResult.Unavailable(
                "No keyring is running (the freedesktop Secret Service: GNOME Keyring, KWallet or KeePassXC with Secret Service on). Start or install one; the secret is not saved in plain text.");
        }
        catch (DBusErrorReplyException e)
        {
            _logger.LogWarning("The Secret Service couldn't {Action} the secret: {Error} {Message}", action, e.ErrorName, e.ErrorMessage);
            return SecretResult.Failed($"The keyring couldn't {action} the secret: {e.ErrorMessage} ({e.ErrorName}).");
        }
        catch (Exception e) when (e is DBusExceptionBase or IOException or TimeoutException or System.Net.Sockets.SocketException)
        {
            _logger.LogWarning(e, "Talking to the Secret Service failed");
            return SecretResult.Unavailable($"Can't reach the keyring over D-Bus: {e.Message}");
        }
    }

    /// <summary>One connection with an open <c>plain</c> transfer session.</summary>
    private sealed class Session
    {
        private readonly DBusConnection _connection;
        private readonly string _path;

        private Session(DBusConnection connection, string path)
        {
            _connection = connection;
            _path = path;
        }

        public static async Task<Session> OpenAsync(DBusConnection connection)
        {
            MessageBuffer Request()
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: Bus, path: ServicePath, @interface: ServiceInterface, signature: "sv", member: "OpenSession");
                writer.WriteString("plain");
                writer.WriteVariantString(string.Empty);
                return writer.CreateMessage();
            }

            var path = await connection.CallMethodAsync(
                Request(),
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    reader.ReadVariantValue();
                    return reader.ReadObjectPathAsString();
                },
                null).ConfigureAwait(false);
            return new Session(connection, path);
        }

        public Task<(string[] Unlocked, string[] Locked)> SearchAsync(IReadOnlyDictionary<string, string> attributes)
        {
            MessageBuffer Request()
            {
                using var writer = _connection.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: Bus, path: ServicePath, @interface: ServiceInterface, signature: "a{ss}", member: "SearchItems");

                // Inline: MessageWriter is a struct, so a helper taking it by value would write past the message's end.
                var start = writer.WriteDictionaryStart();
                foreach (var (key, value) in attributes)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(key);
                    writer.WriteString(value);
                }

                writer.WriteDictionaryEnd(start);
                return writer.CreateMessage();
            }

            return _connection.CallMethodAsync(
                Request(),
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    var unlocked = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
                    var locked = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
                    return (unlocked, locked);
                },
                null);
        }

        public Task<string> DefaultCollectionAsync()
        {
            MessageBuffer Request()
            {
                using var writer = _connection.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: Bus, path: ServicePath, @interface: ServiceInterface, signature: "s", member: "ReadAlias");
                writer.WriteString("default");
                return writer.CreateMessage();
            }

            return _connection.CallMethodAsync(Request(), static (message, _) => message.GetBodyReader().ReadObjectPathAsString(), null);
        }

        /// <summary>Unlocks the item or collection (at once when it isn't locked); <c>false</c> when the prompt was dismissed.</summary>
        public async Task<bool> UnlockAsync(string objectPath, CancellationToken cancellationToken)
        {
            MessageBuffer Request()
            {
                using var writer = _connection.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: Bus, path: ServicePath, @interface: ServiceInterface, signature: "ao", member: "Unlock");
                writer.WriteArray(new ObjectPath[] { new(objectPath) });
                return writer.CreateMessage();
            }

            var (unlocked, prompt) = await _connection.CallMethodAsync(
                Request(),
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    var done = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
                    return (done, reader.ReadObjectPathAsString());
                },
                null).ConfigureAwait(false);
            return unlocked.Contains(objectPath, StringComparer.Ordinal)
                || (prompt != NoObject && await PromptAsync(prompt, cancellationToken).ConfigureAwait(false));
        }

        public Task<byte[]> GetSecretAsync(string item)
        {
            MessageBuffer Request()
            {
                using var writer = _connection.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: Bus, path: item, @interface: ItemInterface, signature: "o", member: "GetSecret");
                writer.WriteObjectPath(_path);
                return writer.CreateMessage();
            }

            return _connection.CallMethodAsync(
                Request(),
                static (message, _) =>
                {
                    // (oayays): session, parameters, value, content type.
                    var reader = message.GetBodyReader();
                    reader.AlignStruct();
                    reader.ReadObjectPath();
                    reader.ReadArrayOfByte();
                    return reader.ReadArrayOfByte();
                },
                null);
        }

        /// <summary>Creates the item, replacing one with the same attributes; <c>false</c> when its prompt was dismissed.</summary>
        public async Task<bool> CreateItemAsync(
            string collection, string label, IReadOnlyDictionary<string, string> attributes, byte[] secret, CancellationToken cancellationToken)
        {
            MessageBuffer Request()
            {
                using var writer = _connection.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: Bus, path: collection, @interface: CollectionInterface, signature: "a{sv}(oayays)b", member: "CreateItem");
                writer.WriteDictionary(new Dictionary<string, VariantValue>
                {
                    ["org.freedesktop.Secret.Item.Label"] = VariantValue.String(label),
                    ["org.freedesktop.Secret.Item.Attributes"] = new Dict<string, string>(attributes.ToDictionary()).AsVariantValue(),
                });
                writer.WriteStructureStart();
                writer.WriteObjectPath(_path);
                writer.WriteArray(Array.Empty<byte>());
                writer.WriteArray(secret);
                writer.WriteString("text/plain; charset=utf8");
                writer.WriteBool(true);
                return writer.CreateMessage();
            }

            var (_, prompt) = await _connection.CallMethodAsync(
                Request(),
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return (reader.ReadObjectPathAsString(), reader.ReadObjectPathAsString());
                },
                null).ConfigureAwait(false);
            return prompt == NoObject || await PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        }

        /// <summary><c>false</c> when the item's delete prompt was dismissed.</summary>
        public async Task<bool> DeleteItemAsync(string item, CancellationToken cancellationToken)
        {
            MessageBuffer Request()
            {
                using var writer = _connection.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: Bus, path: item, @interface: ItemInterface, signature: null, member: "Delete");
                return writer.CreateMessage();
            }

            var prompt = await _connection.CallMethodAsync(Request(), static (message, _) => message.GetBodyReader().ReadObjectPathAsString(), null)
                .ConfigureAwait(false);
            return prompt == NoObject || await PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Shows the service's prompt and waits for its <c>Completed</c> signal; <c>true</c> unless dismissed.</summary>
        private async Task<bool> PromptAsync(string prompt, CancellationToken cancellationToken)
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watch = await _connection.WatchSignalAsync(
                Bus,
                prompt,
                PromptInterface,
                "Completed",
                static (message, _) => message.GetBodyReader().ReadBool(),
                (Notification<bool> signal) =>
                {
                    if (signal.HasValue)
                    {
                        completed.TrySetResult(!signal.Value);
                    }
                    else
                    {
                        completed.TrySetResult(false);
                    }
                },
                ObserverFlags.None,
                false,
                null).ConfigureAwait(false);

            MessageBuffer Request()
            {
                using var writer = _connection.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: Bus, path: prompt, @interface: PromptInterface, signature: "s", member: "Prompt");
                writer.WriteString(string.Empty);
                return writer.CreateMessage();
            }

            await _connection.CallMethodAsync(Request()).ConfigureAwait(false);
            return await completed.Task.WaitAsync(PromptTimeout, cancellationToken).ConfigureAwait(false);
        }
    }
}
