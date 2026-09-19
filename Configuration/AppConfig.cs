using System.Text.Json.Serialization;

namespace ShyFtp.Configuration;

public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    public Dictionary<string, ServerProfile> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<FolderProfile> Folders { get; set; } = new();

    public List<string> GlobalIgnore { get; set; } = new();
}

public enum FtpSecurity
{
    None,
    ExplicitTls,
    ImplicitTls
}

public sealed class ServerProfile
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 21;
    public string Username { get; set; } = "anonymous";
    public string Password { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FtpSecurity Security { get; set; } = FtpSecurity.None;

    public bool ValidateCertificate { get; set; } = true;
    public bool Passive { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
    public bool Utf8 { get; set; } = true;
    public bool UsePasvAddress { get; set; } = false;
}

public sealed class FolderProfile
{
    public string Path { get; set; } = "";
    public string Server { get; set; } = "";
    public string RemotePath { get; set; } = "/";
    public bool Recursive { get; set; } = true;
    public List<string> Ignore { get; set; } = new();
}