using System.Net.Sockets;

namespace v2rayN.Desktop.Services;

/// <summary>
/// One Colitu per user session. The first instance listens on a Unix socket in
/// the user's runtime directory; a second launch connects, asks it to show its
/// window and exits (Windows uses a named event for the same).
/// </summary>
public sealed class ColituSingleInstance : IDisposable
{
    private const string ShowCommand = "show";
    private readonly Socket? _listener;
    private readonly string _path;

    private ColituSingleInstance(Socket? listener, string path)
    {
        _listener = listener;
        _path = path;
    }

    public event Action? ShowRequested;

    private static string SocketPath()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtime) || !Directory.Exists(runtime))
        {
            // Not /tmp: another local user could take the predictable name there first, and this
            // app would then "find itself running" and quit at every start. The data folder is
            // the user's own (the same one in every run, before LocalAppData is decided).
            runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "ColituVPN");
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        return Path.Combine(runtime, $"colitu-vpn-{Environment.UserName}.sock");
    }

    /// <summary>
    /// Returns the guard of the first instance, or null when another instance
    /// already runs (it has been asked to come to the front).
    /// </summary>
    public static ColituSingleInstance? Acquire()
    {
        var path = SocketPath();
        if (TrySignalRunning(path))
        {
            return null;
        }
        try
        {
            // Nobody answered: a stale socket file from a crash.
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            listener.Listen(4);
            var guard = new ColituSingleInstance(listener, path);
            _ = guard.AcceptLoopAsync();
            return guard;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituSingleInstance.Acquire", ex);
            return new ColituSingleInstance(null, path);
        }
    }

    private static bool TrySignalRunning(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));
            client.Send(Encoding.ASCII.GetBytes(ShowCommand));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener != null)
        {
            try
            {
                using var client = await _listener.AcceptAsync();
                var buffer = new byte[16];
                var read = await client.ReceiveAsync(buffer);
                if (Encoding.ASCII.GetString(buffer, 0, read) == ShowCommand)
                {
                    ShowRequested?.Invoke();
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ColituSingleInstance.Accept", ex);
                await Task.Delay(500);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _listener?.Dispose();
            if (_listener != null && File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // Best effort at exit.
        }
    }
}
