using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Background;

/// <summary>
/// Windows 10+: toast notifications through WinRT (<c>ToastNotificationManager</c>), called over raw COM vtables so the
/// app keeps one cross-platform target framework (no Windows SDK projection). An unpackaged app gets its toast identity
/// from <c>HKCU\Software\Classes\AppUserModelId\io.github.ailocalpack</c> (display name, icon) and
/// <c>SetCurrentProcessExplicitAppUserModelID</c>. The click comes as the toast's <c>Activated</c> event while the app
/// runs (a toast clicked later from the Action Center after the app quit does nothing: there is no COM activator).
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe partial class WindowsToastNotifications : ISystemNotifications
{
    private const int MaxKept = 32;

    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidAgile = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    private static readonly Guid IidManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
    private static readonly Guid IidToastFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    private static readonly Guid IidXmlDocument = new("F7F3A506-1E87-42D6-BCFB-B8C809FA5494");
    private static readonly Guid IidXmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");

    /// <summary><c>TypedEventHandler&lt;ToastNotification, object&gt;</c>.</summary>
    private static readonly Guid IidActivatedHandler = new("AB54DE2D-97D9-5528-B6AD-105AFE156530");

    private static readonly ConcurrentDictionary<IntPtr, (WindowsToastNotifications Owner, string Id)> Handlers = new();
    private static readonly IntPtr* HandlerVtable = CreateHandlerVtable();

    private readonly AppPaths _paths;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Queue<IntPtr> _kept = new();
    private bool _registered;

    public WindowsToastNotifications(AppPaths paths, ILogger<WindowsToastNotifications> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public event EventHandler<string>? Activated;

    public Task ShowAsync(SystemNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return Task.Run(() =>
        {
            try
            {
                _ = RoInitialize(1); // multithreaded; S_FALSE / RPC_E_CHANGED_MODE are fine
                Register();
                Show(notification);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Couldn't show the notification {Title}", notification.Title);
            }
        });
    }

    public void Dispose()
    {
        lock (_lock)
        {
            while (_kept.Count > 0)
            {
                Release(_kept.Dequeue());
            }
        }
    }

    /// <summary>The toast identity of the unpackaged app, once per run.</summary>
    private void Register()
    {
        lock (_lock)
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{SystemNotifications.AppId}", writable: true);
                key.SetValue("DisplayName", "ailocalpack");
                if (WriteIcon() is { } icon)
                {
                    key.SetValue("IconUri", icon);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
            {
                _logger.LogWarning(e, "Couldn't register the notification identity");
            }

            Check(SetCurrentProcessExplicitAppUserModelID(SystemNotifications.AppId), "SetCurrentProcessExplicitAppUserModelID");
        }
    }

    private string? WriteIcon()
    {
        try
        {
            var path = Path.Combine(_paths.Data, "notification-icon.png");
            using var source = Avalonia.Platform.AssetLoader.Open(new Uri("avares://AiLocalPack/Assets/tray.png"));
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            if (!File.Exists(path) || new FileInfo(path).Length != memory.Length)
            {
                AtomicFile.WriteAllBytes(path, memory.ToArray());
            }

            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Couldn't save the notification icon");
            return null;
        }
    }

    private void Show(SystemNotification notification)
    {
        var xml = "<toast><visual><binding template=\"ToastGeneric\"><text>" + Xml(notification.Title) + "</text><text>"
            + Xml(notification.Body) + "</text></binding></visual></toast>";

        IntPtr document = 0, documentIO = 0, xmlDocument = 0, factory = 0, toast = 0, statics = 0, notifier = 0;
        try
        {
            using (var className = new HString("Windows.Data.Xml.Dom.XmlDocument"))
            {
                Check(RoActivateInstance(className.Handle, out document), "RoActivateInstance(XmlDocument)");
            }

            documentIO = Query(document, IidXmlDocumentIO);
            using (var content = new HString(xml))
            {
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(documentIO, 6))(documentIO, content.Handle), "LoadXml");
            }

            xmlDocument = Query(document, IidXmlDocument);
            factory = Factory("Windows.UI.Notifications.ToastNotification", IidToastFactory);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(factory, 6))(factory, xmlDocument, &toast), "CreateToastNotification");

            var handler = CreateHandler(notification.Id);
            long token;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(toast, 11))(toast, handler, &token), "add_Activated");

            statics = Factory("Windows.UI.Notifications.ToastNotificationManager", IidManagerStatics);
            using (var appId = new HString(SystemNotifications.AppId))
            {
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, 7))(statics, appId.Handle, &notifier), "CreateToastNotifierWithId");
            }

            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(notifier, 6))(notifier, toast), "Show");
            Keep(toast);
            toast = 0;
        }
        finally
        {
            Release(notifier);
            Release(statics);
            Release(toast);
            Release(factory);
            Release(xmlDocument);
            Release(documentIO);
            Release(document);
        }
    }

    /// <summary>Keeps the last toasts alive so their <c>Activated</c> handlers still fire.</summary>
    private void Keep(IntPtr toast)
    {
        lock (_lock)
        {
            _kept.Enqueue(toast);
            while (_kept.Count > MaxKept)
            {
                Release(_kept.Dequeue());
            }
        }
    }

    private static IntPtr Factory(string className, Guid iid)
    {
        using var name = new HString(className);
        Check(RoGetActivationFactory(name.Handle, in iid, out var factory), $"RoGetActivationFactory({className})");
        return factory;
    }

    private static IntPtr Query(IntPtr unknown, Guid iid)
    {
        IntPtr result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(unknown, 0))(unknown, &iid, &result), $"QueryInterface({iid})");
        return result;
    }

    private static void Release(IntPtr unknown)
    {
        if (unknown != 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(unknown, 2))(unknown);
        }
    }

    private static IntPtr Slot(IntPtr unknown, int index) => (*(IntPtr**)unknown)[index];

    private static void Check(int hresult, string what)
    {
        if (hresult < 0)
        {
            throw new COMException($"{what} failed", hresult);
        }
    }

    private static string Xml(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>A native <c>TypedEventHandler</c> object: vtable pointer + nothing else; immortal (refcounting ignored).</summary>
    private IntPtr CreateHandler(string id)
    {
        var handler = (IntPtr)NativeMemory.AllocZeroed((nuint)IntPtr.Size);
        *(IntPtr**)handler = HandlerVtable;
        Handlers[handler] = (this, id);
        return handler;
    }

    private static IntPtr* CreateHandlerVtable()
    {
        var vtable = (IntPtr*)NativeMemory.AllocZeroed((nuint)(IntPtr.Size * 4));
        vtable[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&HandlerQueryInterface;
        vtable[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&HandlerAddRef;
        vtable[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&HandlerRelease;
        vtable[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&HandlerInvoke;
        return vtable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HandlerQueryInterface(IntPtr self, Guid* iid, IntPtr* result)
    {
        if (*iid == IidUnknown || *iid == IidAgile || *iid == IidActivatedHandler)
        {
            *result = self;
            return 0;
        }

        *result = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint HandlerAddRef(IntPtr self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint HandlerRelease(IntPtr self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HandlerInvoke(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            if (Handlers.TryGetValue(self, out var target))
            {
                target.Owner.Activated?.Invoke(target.Owner, target.Id);
            }
        }
        catch (Exception e)
        {
            if (Handlers.TryGetValue(self, out var owner))
            {
                owner.Owner._logger.LogWarning(e, "Handling a notification click failed");
            }
        }

        return 0;
    }

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int initType);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, uint length, out IntPtr hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(IntPtr hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    private readonly struct HString : IDisposable
    {
        public HString(string text) => Check(WindowsCreateString(text, (uint)text.Length, out Handle), "WindowsCreateString");

        public readonly IntPtr Handle;

        public void Dispose() => WindowsDeleteString(Handle);
    }
}
