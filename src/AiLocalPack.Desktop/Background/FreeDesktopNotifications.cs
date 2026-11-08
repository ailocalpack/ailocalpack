using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace AiLocalPack.Desktop.Background;

/// <summary>
/// Linux: the Desktop Notifications spec over the session D-Bus (<c>org.freedesktop.Notifications.Notify</c> with a
/// <c>default</c> action; its <c>ActionInvoked</c> signal is the click). Tmds.DBus.Protocol is the D-Bus library
/// Avalonia itself uses on Linux. The connection is made on the first notification.
/// </summary>
public sealed class FreeDesktopNotifications(ILogger<FreeDesktopNotifications> logger) : ISystemNotifications
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<uint, string> _shown = new();
    private DBusConnection? _connection;
    private IDisposable? _actions;

    public event EventHandler<string>? Activated;

    public async Task ShowAsync(SystemNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        try
        {
            var connection = await ConnectAsync().ConfigureAwait(false);
            var id = await connection.CallMethodAsync(Notify(connection, notification), static (message, _) => message.GetBodyReader().ReadUInt32(), null)
                .ConfigureAwait(false);
            _shown[id] = notification.Id;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Couldn't show the notification {Title}", notification.Title);
        }
    }

    public void Dispose()
    {
        _actions?.Dispose();
        _connection?.Dispose();
        _gate.Dispose();
    }

    private static MessageBuffer Notify(DBusConnection connection, SystemNotification notification)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: Service, signature: "susssasa{sv}i", member: "Notify");
        writer.WriteString("ailocalpack");
        writer.WriteUInt32(0);
        writer.WriteString(string.Empty);
        writer.WriteString(notification.Title);
        writer.WriteString(notification.Body);
        writer.WriteArray(new[] { "default", "Open" });
        var hints = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("desktop-entry");
        writer.WriteVariantString("ailocalpack");
        writer.WriteDictionaryEnd(hints);
        writer.WriteInt32(-1);
        return writer.CreateMessage();
    }

    private static MessageBuffer NameHasOwner(DBusConnection connection, string name)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus", @interface: "org.freedesktop.DBus", signature: "s", member: "NameHasOwner");
        writer.WriteString(name);
        return writer.CreateMessage();
    }

    private async Task<DBusConnection> ConnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is { } known)
            {
                return known;
            }

            var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No D-Bus session bus."));
            await connection.ConnectAsync().ConfigureAwait(false);
            _actions = await connection.WatchSignalAsync(
                Service,
                ObjectPath,
                Service,
                "ActionInvoked",
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return (Id: reader.ReadUInt32(), Action: reader.ReadString());
                },
                (Notification<(uint Id, string Action)> signal) =>
                {
                    if (signal.HasValue && signal.Value.Action == "default" && _shown.TryGetValue(signal.Value.Id, out var id))
                    {
                        Activated?.Invoke(this, id);
                    }
                },
                ObserverFlags.None,
                false,
                null).ConfigureAwait(false);
            _connection = connection;
            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whether a StatusNotifierItem host runs (<c>org.kde.StatusNotifierWatcher</c> owned on the session bus), so a tray
    /// icon shows: KDE (Steam Deck's desktop mode), most others; GNOME only with the AppIndicator extension. Without one
    /// the window isn't closed to a tray nobody can see.
    /// </summary>
    public static async Task<bool> HasTrayAsync(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            using var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No D-Bus session bus."));
            await connection.ConnectAsync().ConfigureAwait(false);
            return await connection.CallMethodAsync(NameHasOwner(connection, "org.kde.StatusNotifierWatcher"), static (message, _) => message.GetBodyReader().ReadBool(), null)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Couldn't ask D-Bus for a tray host");
            return false;
        }
    }
}
