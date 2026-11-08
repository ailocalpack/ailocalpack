using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Background;

/// <summary>
/// macOS: <c>UNUserNotificationCenter</c> through the Objective-C runtime (no binding library): authorization asked on
/// the first notification, a delegate shows banners while the app is in front too and reports the click
/// (<c>didReceiveNotificationResponse</c>, default action) by the request identifier. The center only exists for an app
/// bundle (<c>NSBundle.mainBundle.bundleIdentifier</c>); a run outside one (<c>dotnet run</c>) falls back to
/// <c>osascript display notification</c>, which shows the text but can't report a click.
/// </summary>
public sealed unsafe partial class MacNotifications(ILogger<MacNotifications> logger) : ISystemNotifications
{
    private readonly ILogger _logger = logger;

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string DefaultAction = "com.apple.UNNotificationDefaultActionIdentifier";
    private const nuint PresentList = 1 << 3;
    private const nuint PresentBanner = 1 << 4;
    private const nuint AuthorizeAlert = 1 << 2;
    private const nuint AuthorizeSound = 1 << 1;
    private const int BlockIsGlobal = 1 << 28;

    private static readonly Lock InitLock = new();
    private static MacNotifications? _instance;
    private static IntPtr _center;
    private static IntPtr _delegate;
    private static bool _initialized;

    public event EventHandler<string>? Activated;

    public Task ShowAsync(SystemNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return Task.Run(() =>
        {
            try
            {
                if (EnsureCenter())
                {
                    Post(notification);
                }
                else
                {
                    ShowWithScript(notification);
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Couldn't show the notification {Title}", notification.Title);
            }
        });
    }

    public void Prepare() => Task.Run(() =>
    {
        try
        {
            EnsureCenter();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Couldn't ask for notification permission");
        }
    });

    public void Dispose()
    {
        // The delegate object lives as long as the process (the center holds it weakly).
    }

    private bool EnsureCenter()
    {
        lock (InitLock)
        {
            _instance = this;
            if (_initialized)
            {
                return _center != IntPtr.Zero;
            }

            _initialized = true;
            var pool = objc_autoreleasePoolPush();
            try
            {
                var bundle = Send(Class("NSBundle"), Sel("mainBundle"));
                if (bundle == IntPtr.Zero || Send(bundle, Sel("bundleIdentifier")) == IntPtr.Zero)
                {
                    _logger.LogInformation("Not running from an app bundle: notifications go through osascript, without click handling");
                    return false;
                }

                if (NativeLibrary.Load("/System/Library/Frameworks/UserNotifications.framework/UserNotifications") == IntPtr.Zero)
                {
                    return false;
                }

                var center = Send(Class("UNUserNotificationCenter"), Sel("currentNotificationCenter"));
                if (center == IntPtr.Zero)
                {
                    return false;
                }

                _delegate = CreateDelegate();
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)MsgSend)(center, Sel("setDelegate:"), _delegate);
                ((delegate* unmanaged<IntPtr, IntPtr, nuint, IntPtr, void>)MsgSend)(
                    center, Sel("requestAuthorizationWithOptions:completionHandler:"), AuthorizeAlert | AuthorizeSound, GlobalBlock(&OnAuthorized));
                _center = center;
                return true;
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }
    }

    private static void Post(SystemNotification notification)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var content = Send(Send(Class("UNMutableNotificationContent"), Sel("alloc")), Sel("init"));
            SendVoid(content, Sel("setTitle:"), NSString(notification.Title));
            SendVoid(content, Sel("setBody:"), NSString(notification.Body));
            var request = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend)(
                Class("UNNotificationRequest"), Sel("requestWithIdentifier:content:trigger:"), NSString(notification.Id), content, IntPtr.Zero);
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)MsgSend)(_center, Sel("addNotificationRequest:withCompletionHandler:"), request, IntPtr.Zero);
            SendVoid(content, Sel("release"), IntPtr.Zero);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private void ShowWithScript(SystemNotification notification)
    {
        var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add($"display notification {AppleScript(notification.Body)} with title {AppleScript(notification.Title)}");
        using var process = Process.Start(start);
        if (process is not null && !process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            _logger.LogWarning("osascript didn't finish showing the notification");
        }
    }

    private static string AppleScript(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>A subclass of NSObject conforming to <c>UNUserNotificationCenterDelegate</c>, with both callbacks.</summary>
    private static IntPtr CreateDelegate()
    {
        const string name = "AiLocalPackNotificationDelegate";
        var cls = objc_getClass(name);
        if (cls == IntPtr.Zero)
        {
            cls = objc_allocateClassPair(Class("NSObject"), name, 0);
            var protocol = objc_getProtocol("UNUserNotificationCenterDelegate");
            if (protocol != IntPtr.Zero)
            {
                class_addProtocol(cls, protocol);
            }

            class_addMethod(cls, Sel("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnResponse, "v@:@@@?");
            class_addMethod(cls, Sel("userNotificationCenter:willPresentNotification:withCompletionHandler:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnWillPresent, "v@:@@@?");
            objc_registerClassPair(cls);
        }

        return Send(Send(cls, Sel("alloc")), Sel("init"));
    }

    [UnmanagedCallersOnly]
    private static void OnResponse(IntPtr self, IntPtr cmd, IntPtr center, IntPtr response, IntPtr completion)
    {
        try
        {
            var action = Utf8(Send(response, Sel("actionIdentifier")));
            var id = Utf8(Send(Send(Send(response, Sel("notification")), Sel("request")), Sel("identifier")));
            if (action == DefaultAction && id is not null && _instance is { } instance)
            {
                instance.Activated?.Invoke(instance, id);
            }
        }
        catch (Exception e)
        {
            _instance?._logger.LogWarning(e, "Handling a notification click failed");
        }
        finally
        {
            InvokeBlock(completion);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnWillPresent(IntPtr self, IntPtr cmd, IntPtr center, IntPtr notification, IntPtr completion)
    {
        if (completion != IntPtr.Zero)
        {
            var invoke = ((BlockLiteral*)completion)->Invoke;
            ((delegate* unmanaged<IntPtr, nuint, void>)invoke)(completion, PresentBanner | PresentList);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnAuthorized(IntPtr block, byte granted, IntPtr error)
    {
        var reason = error == IntPtr.Zero ? null : Utf8(Send(error, Sel("localizedDescription")));
        _instance?._logger.LogInformation("Notification permission {Answer}{Reason}", granted != 0 ? "granted" : "not granted", reason is null ? string.Empty : ": " + reason);
    }

    private static void InvokeBlock(IntPtr block)
    {
        if (block != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, void>)((BlockLiteral*)block)->Invoke)(block);
        }
    }

    /// <summary>A global block (never copied or freed) calling <paramref name="invoke"/>; made once per function, kept for the process.</summary>
    private static IntPtr GlobalBlock(delegate* unmanaged<IntPtr, byte, IntPtr, void> invoke)
    {
        var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        descriptor->Size = (nuint)sizeof(BlockLiteral);
        var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        block->Isa = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");
        block->Flags = BlockIsGlobal;
        block->Invoke = (IntPtr)invoke;
        block->Descriptor = descriptor;
        return (IntPtr)block;
    }

    private static IntPtr NSString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* p = bytes)
        {
            return ((delegate* unmanaged<IntPtr, IntPtr, byte*, IntPtr>)MsgSend)(Class("NSString"), Sel("stringWithUTF8String:"), p);
        }
    }

    private static string? Utf8(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero)
        {
            return null;
        }

        var chars = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)MsgSend)(nsString, Sel("UTF8String"));
        return Marshal.PtrToStringUTF8(chars);
    }

    private static IntPtr MsgSend { get; } = OperatingSystem.IsMacOS() ? NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend") : IntPtr.Zero;

    private static IntPtr Send(IntPtr target, IntPtr selector) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)MsgSend)(target, selector);

    private static void SendVoid(IntPtr target, IntPtr selector, IntPtr argument) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)MsgSend)(target, selector, argument);

    private static IntPtr Class(string name) => objc_getClass(name);

    private static IntPtr Sel(string name) => sel_registerName(name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [LibraryImport(ObjC)]
    private static partial void objc_registerClassPair(IntPtr cls);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getProtocol(string name);

    [LibraryImport(ObjC)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addProtocol(IntPtr cls, IntPtr protocol);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);

    [LibraryImport(ObjC)]
    private static partial IntPtr objc_autoreleasePoolPush();

    [LibraryImport(ObjC)]
    private static partial void objc_autoreleasePoolPop(IntPtr pool);

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public BlockDescriptor* Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }
}
