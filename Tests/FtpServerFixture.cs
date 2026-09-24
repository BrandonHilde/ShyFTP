using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ShyFTP.Tests;

public sealed class FtpServerFixture : IAsyncLifetime, IDisposable
{
    private Process? _process;

    public string Root { get; private set; } = "";
    public string LocalWork { get; private set; } = "";
    public int Port { get; private set; }
    public string Username => "test";
    public string Password => "test";
    public bool IsAvailable { get; private set; }

    public static bool CanStart()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "-c \"import pyftpdlib\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            return proc != null && proc.WaitForExit(10000) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public Task InitializeAsync()
    {
        Root = Path.Combine(Path.GetTempPath(), "shyftp-live-" + Guid.NewGuid().ToString("N"));
        LocalWork = Path.Combine(Path.GetTempPath(), "shyftp-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LocalWork);
        Directory.CreateDirectory(Path.Combine(Root, "seed"));
        File.WriteAllText(Path.Combine(Root, "seed", "hello.txt"), "hello from seed");
        File.WriteAllBytes(Path.Combine(Root, "seed", "bin.dat"), new byte[] { 1, 2, 3, 4, 5 });

        Port = GetFreePort();

        var args = $"-m pyftpdlib -i 127.0.0.1 -p {Port} -w -d \"{Root}\" -u {Username} -P {Password} -r 51000-51100";
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            _process = Process.Start(psi);
        }
        catch
        {
            _process = null;
        }

        if (_process == null)
        {
            IsAvailable = false;
            return Task.CompletedTask;
        }

        _ = _process.StandardOutput.ReadToEndAsync();
        _ = _process.StandardError.ReadToEndAsync();

        IsAvailable = WaitForPort(Port, TimeSpan.FromSeconds(15));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try
        {
            if (_process != null && !_process.HasExited)
                _process.Kill(true);
            _process?.Dispose();
        }
        catch
        {
        }

        try
        {
            if (Root.Length > 0 && Directory.Exists(Root))
                Directory.Delete(Root, true);
            if (LocalWork.Length > 0 && Directory.Exists(LocalWork))
                Directory.Delete(LocalWork, true);
        }
        catch
        {
        }

        _process = null;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool WaitForPort(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                var task = client.ConnectAsync(IPAddress.Loopback, port);
                if (task.Wait(500) && client.Connected)
                    return true;
            }
            catch
            {
            }

            Thread.Sleep(200);
        }

        return false;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (!FtpServerFixture.CanStart())
            Skip = "python + pyftpdlib not available";
    }
}

[CollectionDefinition("ftp-live")]
public class FtpLiveCollection : ICollectionFixture<FtpServerFixture>
{
}
