using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Launch;

/// <summary>The programs running on the machine; behind an interface so session tracking is tested with a fake list.</summary>
public interface IProcessList
{
    /// <summary>Every process whose executable path could be read (others, like system processes, are left out). Never throws.</summary>
    IReadOnlyList<RunningProcess> Snapshot();
}

/// <summary>
/// The real process list, cheap enough to poll every couple of seconds: Windows <c>QueryFullProcessImageName</c>, macOS
/// <c>proc_pidpath</c>, Linux <c>/proc/&lt;pid&gt;/exe</c>. On Linux a Wine / Proton process is reported with the Windows
/// program it runs (its <c>argv[0]</c>, <c>Z:\home\…\Game.exe</c> → <c>/home/…/Game.exe</c>), so a Windows game started
/// through Steam is found in its folder.
/// </summary>
public sealed partial class SystemProcessList : IProcessList
{
    public IReadOnlyList<RunningProcess> Snapshot()
    {
        try
        {
            return OperatingSystem.IsLinux() ? Linux() : Portable();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static List<RunningProcess> Portable()
    {
        var list = new List<RunningProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var id = process.Id;
                var path = OperatingSystem.IsWindows() ? WindowsPath(id) : OperatingSystem.IsMacOS() ? MacPath(id) : null;
                if (path is not null)
                {
                    list.Add(new RunningProcess(id, path));
                }
            }
        }

        return list;
    }

    private static List<RunningProcess> Linux()
    {
        var list = new List<RunningProcess>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var id))
            {
                continue;
            }

            try
            {
                var exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget;
                if (exe is null)
                {
                    continue;
                }

                var wine = Path.GetFileName(exe).Contains("wine", StringComparison.OrdinalIgnoreCase)
                    ? WinePath(File.ReadAllBytes(Path.Combine(dir, "cmdline")))
                    : null;
                list.Add(new RunningProcess(id, wine ?? exe));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Gone meanwhile, or another user's.
            }
        }

        return list;
    }

    /// <summary>The Unix path of a Wine process's Windows program from its command line (<c>Z:</c> = the root), else <c>null</c>.</summary>
    internal static string? WinePath(ReadOnlySpan<byte> cmdline)
    {
        var end = cmdline.IndexOf((byte)0);
        var argv0 = Encoding.UTF8.GetString(end < 0 ? cmdline : cmdline[..end]);
        return argv0.Length > 3 && (argv0[0] is 'Z' or 'z') && argv0[1] == ':' && argv0[2] == '\\'
            ? argv0[2..].Replace('\\', '/')
            : null;
    }

    private static string? WindowsPath(int id)
    {
        const uint queryLimitedInformation = 0x1000;
        var handle = OpenProcess(queryLimitedInformation, false, id);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[32768];
            var size = buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? MacPath(int id)
    {
        var buffer = new byte[4096];
        var length = ProcPidPath(id, buffer, (uint)buffer.Length);
        return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] name, ref int size);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidpath")]
    private static extern int ProcPidPath(int pid, [Out] byte[] buffer, uint size);
}

/// <summary>Starts programs and hands addresses to the OS; behind an interface so launching is tested without side effects.</summary>
public interface IProcessStarter
{
    /// <summary>Opens <paramref name="url"/> with the OS handler of its scheme (<c>steam://</c> → Steam). Throws when the OS refuses.</summary>
    void OpenUrl(Uri url);

    /// <summary>Starts <paramref name="fileName"/> (no shell) and lets it run on its own. Throws when the OS refuses.</summary>
    void Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory);
}

public sealed class SystemProcessStarter : IProcessStarter
{
    public void OpenUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        // Shell execute: the registered handler on Windows, open on macOS, xdg-open on Linux.
        using var process = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }

    public void Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, WorkingDirectory = workingDirectory };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);
    }
}
