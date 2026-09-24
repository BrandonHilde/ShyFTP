using System.Text;
using ShyFtp.Configuration;
using ShyFtp.Ftp;

namespace ShyFTP.Tests;

[Collection("ftp-live")]
public class FtpClientLiveTests
{
    private readonly FtpServerFixture _fx;

    public FtpClientLiveTests(FtpServerFixture fx) => _fx = fx;

    private FtpClient NewClient(bool passive = true) => new(new ServerProfile
    {
        Host = "127.0.0.1",
        Port = _fx.Port,
        Username = _fx.Username,
        Password = _fx.Password,
        Security = FtpSecurity.None,
        Passive = passive,
        TimeoutSeconds = 10,
        UsePasvAddress = true
    });

    private string Local(string name) => Path.Combine(_fx.LocalWork, name);

    [LiveFact]
    public void Connect_login_and_pwd_work()
    {
        Assert.True(_fx.IsAvailable, "FTP server did not start");
        using var client = NewClient();
        client.Connect();
        Assert.True(client.IsConnected);
        var pwd = client.PrintWorkingDirectory();
        Assert.False(string.IsNullOrWhiteSpace(pwd));
    }

    [LiveFact]
    public void Wrong_password_is_rejected()
    {
        using var client = new FtpClient(new ServerProfile
        {
            Host = "127.0.0.1",
            Port = _fx.Port,
            Username = _fx.Username,
            Password = "not-the-password",
            TimeoutSeconds = 10,
            UsePasvAddress = true
        });
        Assert.Throws<FtpException>(() => client.Connect());
    }

    [LiveFact]
    public void DirectoryExists_restores_cwd_for_nested_paths()
    {
        using var client = NewClient();
        client.Connect();
        client.EnsureDirectory("/cwd-check/a/b/c");

        var before = client.PrintWorkingDirectory();
        Assert.True(client.DirectoryExists("/cwd-check/a/b/c"));
        var after = client.PrintWorkingDirectory();
        Assert.Equal(before, after);

        Assert.True(client.DirectoryExists("/cwd-check/a"));
        Assert.Equal(before, client.PrintWorkingDirectory());

        Assert.False(client.DirectoryExists("/cwd-check/does-not-exist"));
        Assert.Equal(before, client.PrintWorkingDirectory());
    }

    [LiveFact]
    public void EnsureDirectory_relative_path_stays_relative_to_cwd()
    {
        using var client = NewClient();
        client.Connect();
        client.EnsureDirectory("/base");
        client.ChangeDirectory("/base");
        client.EnsureDirectory("rel/inner");

        Assert.True(client.DirectoryExists("/base/rel/inner"));
        Assert.False(client.DirectoryExists("/rel/inner"));
    }

    [LiveFact]
    public void EnsureDirectory_absolute_creates_full_chain()
    {
        using var client = NewClient();
        client.Connect();
        client.EnsureDirectory("/deep/one/two/three");
        Assert.True(client.DirectoryExists("/deep/one/two/three"));
    }

    [LiveFact]
    public void Upload_and_Download_roundtrip_preserves_bytes()
    {
        var localSrc = Local("src.bin");
        var payload = new byte[64 * 1024];
        new Random(42).NextBytes(payload);
        File.WriteAllBytes(localSrc, payload);

        using var client = NewClient();
        client.Connect();

        client.UploadFrom(localSrc, "/rt/src.bin");

        Assert.True(client.FileExists("/rt/src.bin"));
        Assert.True(client.TryGetSize("/rt/src.bin", out var size));
        Assert.Equal(payload.Length, size);

        var localDst = Local("dst.bin");
        client.DownloadTo("/rt/src.bin", localDst);

        var round = File.ReadAllBytes(localDst);
        Assert.Equal(payload, round);
        Assert.False(File.Exists(localDst + ".shyftp-part"));
    }

    [LiveFact]
    public void Download_cleans_up_temp_file_on_failure()
    {
        using var client = NewClient();
        client.Connect();
        var missing = Local("never.bin");
        Assert.Throws<FtpException>(() => client.DownloadTo("/rt/does-not-exist.bin", missing));
        Assert.False(File.Exists(missing));
        Assert.False(File.Exists(missing + ".shyftp-part"));
    }

    [LiveFact]
    public void List_returns_names_and_sizes()
    {
        using var client = NewClient();
        client.Connect();
        client.EnsureDirectory("/listing");
        client.UploadFrom(Path.Combine(_fx.Root, "seed", "hello.txt"), "/listing/hello.txt");
        client.UploadFrom(Path.Combine(_fx.Root, "seed", "bin.dat"), "/listing/bin.dat");

        var items = client.List("/listing");
        var names = items.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("hello.txt", names);
        Assert.Contains("bin.dat", names);

        var bin = items.First(i => i.Name == "bin.dat");
        Assert.False(bin.IsDirectory);
        Assert.Equal(5, bin.Size);

        var sub = items.FirstOrDefault(i => i.Name == "." || i.Name == "..");
        Assert.Null(sub);
    }

    [LiveFact]
    public void Rename_moves_a_file()
    {
        using var client = NewClient();
        client.Connect();
        client.EnsureDirectory("/ren");
        client.UploadFrom(Path.Combine(_fx.Root, "seed", "hello.txt"), "/ren/old.txt");
        client.Rename("/ren/old.txt", "/ren/new.txt");

        Assert.False(client.FileExists("/ren/old.txt"));
        Assert.True(client.FileExists("/ren/new.txt"));
    }

    [LiveFact]
    public void Delete_file_and_directory()
    {
        using var client = NewClient();
        client.Connect();
        client.EnsureDirectory("/del/inner");
        client.UploadFrom(Path.Combine(_fx.Root, "seed", "hello.txt"), "/del/inner/x.txt");

        client.DeleteFile("/del/inner/x.txt");
        Assert.False(client.FileExists("/del/inner/x.txt"));

        client.DeleteDirectory("/del", recursive: true);
        Assert.False(client.DirectoryExists("/del/inner"));
        Assert.False(client.DirectoryExists("/del"));
    }

    [LiveFact]
    public void Set_and_get_modified_time()
    {
        using var client = NewClient();
        client.Connect();
        client.EnsureDirectory("/mt");
        client.UploadFrom(Path.Combine(_fx.Root, "seed", "hello.txt"), "/mt/a.txt");

        var stamp = new DateTime(2024, 6, 1, 12, 30, 0, DateTimeKind.Utc);
        Assert.True(client.TrySetModifiedTime("/mt/a.txt", stamp));
        Assert.True(client.TryGetModifiedTime("/mt/a.txt", out var read));
        Assert.Equal(stamp, read);
    }

    [LiveFact]
    public void Crlf_in_path_is_rejected()
    {
        using var client = NewClient();
        client.Connect();
        Assert.Throws<FtpException>(() => client.DeleteFile("/evil\r\nNOOP"));
    }

    [LiveFact]
    public void Active_mode_transfer_works()
    {
        var localSrc = Local("active.txt");
        File.WriteAllText(localSrc, "active-mode payload");

        using var client = NewClient(passive: false);
        client.Connect();
        client.UploadFrom(localSrc, "/active.txt");

        var localDst = Local("active-dst.txt");
        client.DownloadTo("/active.txt", localDst);
        Assert.Equal("active-mode payload", File.ReadAllText(localDst));
    }

    [LiveFact]
    public void Utf8_file_names_roundtrip()
    {
        var name = "café-ümlaut-π.txt";
        var localSrc = Local(name);
        File.WriteAllText(localSrc, "unicode ok", Encoding.UTF8);

        using var client = NewClient();
        client.Connect();
        client.EnsureDirectory("/uni");
        client.UploadFrom(localSrc, "/uni/" + name);

        var items = client.List("/uni");
        Assert.Contains(items, i => i.Name == name);
    }
}
