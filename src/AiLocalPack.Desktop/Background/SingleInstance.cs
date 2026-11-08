using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Background;

/// <summary>
/// One app per data folder (task 39: the app may already run in the tray when the player starts it again). The first
/// instance holds <c>&lt;data&gt;/app.lock</c> (opened exclusively) and listens on a local socket; a later one asks it to
/// show its window and exits. The socket lives in the temp folder (Unix socket paths are short), named after the data
/// folder's hash, so a throwaway <c>AILOCALPACK_HOME</c> runs beside the real app.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ShowCommand = "show";

    private readonly FileStream _lock;
    private readonly Socket _listener;
    private readonly string _socketPath;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(FileStream lockFile, Socket listener, string socketPath, ILogger logger)
    {
        _lock = lockFile;
        _listener = listener;
        _socketPath = socketPath;
        _logger = logger;
        _ = AcceptLoopAsync(_stop.Token);
    }

    /// <summary>Another start asked for the window (raised on a pool thread).</summary>
    public event EventHandler? ShowRequested;

    /// <summary>
    /// This process is the app: the instance to keep for the run. <c>null</c> = another one runs; it was asked to show
    /// its window unless <paramref name="show"/> is <c>false</c> (an autostart in the background), and this one should exit.
    /// </summary>
    public static SingleInstance? TryAcquire(string dataFolder, bool show = true, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        logger ??= NullLogger.Instance;
        Directory.CreateDirectory(dataFolder);
        var socketPath = SocketPath(dataFolder);
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(Path.Combine(dataFolder, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            if (show)
            {
                Signal(socketPath, logger);
            }

            return null;
        }

        try
        {
            File.Delete(socketPath); // left by a crashed run; the lock says nobody uses it
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(4);
            return new SingleInstance(lockFile, listener, socketPath, logger);
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Still the only instance (the lock is ours); a later start just can't reach it.
            logger.LogWarning(e, "Couldn't listen on {Socket}; a second start won't find this instance", socketPath);
            return new SingleInstance(lockFile, new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified), string.Empty, logger);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        if (_socketPath.Length > 0)
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch (IOException)
            {
            }
        }

        _lock.Dispose();
        _stop.Dispose();
    }

    internal static string SocketPath(string dataFolder)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataFolder))))[..16];
        return Path.Combine(Path.GetTempPath(), $"ailocalpack-{hash}.sock");
    }

    private static void Signal(string socketPath, ILogger logger)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(socketPath));
            client.Send(Encoding.ASCII.GetBytes(ShowCommand + "\n"));
        }
        catch (Exception e) when (e is SocketException or IOException or PlatformNotSupportedException)
        {
            logger.LogWarning(e, "The app already runs but didn't answer on {Socket}", socketPath);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (!_listener.IsBound)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[64];
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (Encoding.ASCII.GetString(buffer, 0, read).Trim() == ShowCommand)
                {
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                _logger.LogDebug(e, "A second start's message couldn't be read");
            }
        }
    }
}
