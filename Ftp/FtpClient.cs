using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using ShyFtp.Configuration;
using ShyFtp.Util;

namespace ShyFtp.Ftp;

public sealed class FtpClient : IDisposable
{
    private readonly ServerProfile _profile;
    private readonly Action<string>? _log;

    private TcpClient? _control;
    private Stream? _controlStream;
    private Encoding _encoding = new UTF8Encoding(false);

    private bool _passive;
    private bool _dataProtected;
    private bool _supportsMlsd = true;
    private bool _supportsSize = true;
    private bool _supportsMdtm = true;
    private bool _supportsMfmt;
    private long _lastProgressTicks;

    public bool IsConnected { get; private set; }
    public ServerProfile Profile => _profile;

    public FtpClient(ServerProfile profile, Action<string>? log = null)
    {
        _profile = profile;
        _log = log;
        _passive = profile.Passive;
        _encoding = profile.Utf8 ? new UTF8Encoding(false) : Encoding.Latin1;
    }

    public void Connect()
    {
        if (IsConnected)
            return;

        var host = _profile.Host;
        var port = _profile.Port;
        if (_profile.Security == FtpSecurity.ImplicitTls && port == 21)
            port = 990;

        _control = new TcpClient { NoDelay = true };
        ConnectSocket(_control, host, port, _profile.TimeoutSeconds * 1000);
        _controlStream = _control.GetStream();

        if (_profile.Security == FtpSecurity.ImplicitTls)
            UpgradeControlToTls();

        var greeting = ReadReply();
        if (greeting.Code != 220)
            throw new FtpException(greeting.Code, $"Unexpected greeting: {greeting.Message}");

        if (_profile.Security == FtpSecurity.ExplicitTls)
        {
            var auth = Send("AUTH TLS");
            if (auth.Code != 234 && auth.Code != 334)
                throw new FtpException(auth.Code, $"AUTH TLS refused: {auth.Message}");
            UpgradeControlToTls();
        }

        Login();

        if (_profile.Utf8)
            TrySend("OPTS UTF8 ON");

        ReadFeatures();

        if (_profile.Security != FtpSecurity.None)
        {
            var pbsz = Send("PBSZ 0");
            if (pbsz.Code != 200)
                throw new FtpException(pbsz.Code, $"PBSZ failed: {pbsz.Message}");

            var prot = Send("PROT P");
            if (prot.Code == 200)
                _dataProtected = true;
            else
                TrySend("PROT C");
        }

        SetBinaryMode();
        IsConnected = true;
    }

    private void Login()
    {
        var user = Send($"USER {_profile.Username}");
        switch (user.Code)
        {
            case 230:
                return;
            case 331:
                var pass = Send($"PASS {_profile.Password}");
                if (pass.Code is 230 or 202)
                    return;
                throw new FtpException(pass.Code, $"Login failed: {pass.Message}");
            case 332:
                throw new FtpException(user.Code, "Server requires an ACCT account, which is not supported.");
            default:
                throw new FtpException(user.Code, $"USER rejected: {user.Message}");
        }
    }

    private void ReadFeatures()
    {
        var feat = Send("FEAT");
        if (feat.Code != 211)
            return;

        foreach (var raw in feat.Lines)
        {
            var line = raw.Trim().TrimStart(' ');
            if (line.StartsWith("MLSD", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("MLST", StringComparison.OrdinalIgnoreCase))
                _supportsMlsd = true;
            else if (line.StartsWith("SIZE", StringComparison.OrdinalIgnoreCase))
                _supportsSize = true;
            else if (line.StartsWith("MDTM", StringComparison.OrdinalIgnoreCase))
                _supportsMdtm = true;
            else if (line.StartsWith("MFMT", StringComparison.OrdinalIgnoreCase))
                _supportsMfmt = true;
        }
    }

    public void SetBinaryMode()
    {
        var reply = Send("TYPE I");
        if (reply.Code != 200)
            throw new FtpException(reply.Code, $"TYPE I failed: {reply.Message}");
    }

    public void SetAsciiMode() => Send("TYPE A");

    public string PrintWorkingDirectory()
    {
        var reply = Expect("PWD", 257);
        var text = reply.Lines[0];
        var first = text.IndexOf('"');
        var last = text.LastIndexOf('"');
        if (first >= 0 && last > first)
            return text.Substring(first + 1, last - first - 1);
        return text;
    }

    public void ChangeDirectory(string path) => Expect("CWD " + path, 250);

    public bool DirectoryExists(string path)
    {
        var reply = Send("CWD " + path);
        if (reply.Code == 250)
        {
            TrySend("CDUP");
            return true;
        }

        return false;
    }

    public bool FileExists(string path)
    {
        if (TryGetSize(path, out _))
            return true;
        if (TryGetModifiedTime(path, out _))
            return true;
        return false;
    }

    public void CreateDirectory(string path) => Expect("MKD " + path, 257);

    public void EnsureDirectory(string remotePath)
    {
        var normalized = remotePath.Replace('\\', '/');
        if (normalized.Length == 0 || normalized == "/" || normalized == ".")
            return;

        var absolute = normalized.StartsWith('/');
        var current = absolute ? "/" : "";
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            current = PathUtil.JoinRemote(current, part);
            var reply = Send("MKD " + current);
            if (reply.Code is not (257 or 250) && reply.Code != 550)
                throw new FtpException(reply.Code, $"MKD {current} failed: {reply.Message}");
        }
    }

    public void DeleteFile(string path) => Expect("DELE " + path, 250, 200);

    public void Rename(string from, string to)
    {
        Expect("RNFR " + from, 350);
        Expect("RNTO " + to, 250);
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        if (recursive)
        {
            foreach (var item in List(path))
            {
                var child = PathUtil.JoinRemote(path, item.Name);
                if (item.IsDirectory)
                    DeleteDirectory(child, true);
                else
                    DeleteFile(child);
            }
        }

        Expect("RMD " + path, 250);
    }

    public bool TryGetSize(string path, out long size)
    {
        size = 0;
        if (!_supportsSize)
            return false;

        var reply = Send("SIZE " + path);
        if (reply.Code != 213)
            return false;

        var token = reply.Lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
    }

    public bool TryGetModifiedTime(string path, out DateTime modifiedUtc)
    {
        modifiedUtc = default;
        if (!_supportsMdtm)
            return false;

        var reply = Send("MDTM " + path);
        if (reply.Code != 213)
            return false;

        var token = reply.Lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (token == null)
            return false;

        return TryParseMlsdTime(token, out modifiedUtc);
    }

    public bool TrySetModifiedTime(string path, DateTime modifiedUtc)
    {
        if (!_supportsMfmt)
            return false;

        var stamp = modifiedUtc.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var reply = Send($"MFMT {stamp} {path}");
        return reply.Code == 213;
    }

    public List<FtpListItem> List(string path)
    {
        if (_supportsMlsd)
        {
            try
            {
                return ListMlsd(path);
            }
            catch (FtpException)
            {
                _supportsMlsd = false;
            }
        }

        return ListUnix(path);
    }

    private List<FtpListItem> ListMlsd(string path)
    {
        var items = new List<FtpListItem>();
        ExecuteData(
            () => Send(path.Length == 0 ? "MLSD" : "MLSD " + path),
            stream =>
            {
                using var reader = new StreamReader(stream, _encoding, false, 4096, true);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var item = ParseMlsdLine(line, path);
                    if (item != null)
                        items.Add(item);
                }
            });
        return items;
    }

    private List<FtpListItem> ListUnix(string path)
    {
        var raw = FetchListLines(path, all: true);
        var items = ParseListLines(raw, path);

        if (items.Count == 0 && raw.Count > 0)
        {
            raw = FetchListLines(path, all: false);
            items = ParseListLines(raw, path);
        }

        if (items.Count == 0 && raw.Count > 0)
            items = ListNames(path);

        return items;
    }

    private List<string> FetchListLines(string path, bool all)
    {
        var raw = new List<string>();
        var command = all ? "LIST -a" : "LIST";
        if (path.Length > 0)
            command += " " + path;

        ExecuteData(
            () => Send(command),
            stream =>
            {
                using var reader = new StreamReader(stream, _encoding, false, 4096, true);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    raw.Add(line);
            });

        return raw;
    }

    private static List<FtpListItem> ParseListLines(IEnumerable<string> raw, string path)
    {
        var items = new List<FtpListItem>();
        foreach (var line in raw)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
                continue;

            var item = ParseUnixLine(line, path) ?? ParseDosLine(line, path);
            if (item != null)
                items.Add(item);
        }

        return items;
    }

    private List<FtpListItem> ListNames(string path)
    {
        var items = new List<FtpListItem>();
        ExecuteData(
            () => Send(path.Length == 0 ? "NLST" : "NLST " + path),
            stream =>
            {
                using var reader = new StreamReader(stream, _encoding, false, 4096, true);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var name = line.Trim();
                    if (name.Length == 0 || name.EndsWith("/.") || name.EndsWith("/.."))
                        continue;
                    var shortName = PathUtil.RemoteFileName(name);
                    if (shortName is "." or "..")
                        continue;
                    items.Add(new FtpListItem
                    {
                        Name = shortName,
                        FullPath = name.StartsWith('/') ? name : PathUtil.JoinRemote(path, name),
                        IsDirectory = false
                    });
                }
            });
        return items;
    }

    public void DownloadTo(string remotePath, string localPath, Action<long, long>? progress = null)
    {
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = localPath + ".shyftp-part";
        long total = TryGetSize(remotePath, out var size) ? size : -1;

        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ExecuteData(
                    () => Send("RETR " + remotePath),
                    stream =>
                    {
                        CopyStream(stream, file, total, progress);
                        file.Flush();
                    });
            }

            File.Move(temp, localPath, true);

            if (TryGetModifiedTime(remotePath, out var remoteModified))
                File.SetLastWriteTimeUtc(localPath, remoteModified);
        }
        catch
        {
            if (File.Exists(temp))
                File.Delete(temp);
            throw;
        }
    }

    public void UploadFrom(string localPath, string remotePath, Action<long, long>? progress = null)
    {
        var directory = PathUtil.RemoteDirectoryName(remotePath);
        EnsureDirectory(directory);

        var localModified = File.GetLastWriteTimeUtc(localPath);

        using (var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var total = file.Length;
            ExecuteData(
                () => Send("STOR " + remotePath),
                stream =>
                {
                    CopyStream(file, stream, total, progress);
                    stream.Flush();
                });
        }

        if (!TrySetModifiedTime(remotePath, localModified) &&
            TryGetModifiedTime(remotePath, out var remoteModified) &&
            Math.Abs((remoteModified - localModified).TotalSeconds) > 1)
        {
            File.SetLastWriteTimeUtc(localPath, remoteModified);
        }
    }

    public void Delete(string remotePath)
    {
        var reply = Send("DELE " + remotePath);
        if (reply.Code is not (250 or 200))
            throw new FtpException(reply.Code, $"DELE failed: {reply.Message}");
    }

    private void CopyStream(Stream source, Stream destination, long total, Action<long, long>? progress)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;

        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            copied += read;

            if (progress != null)
            {
                var now = Environment.TickCount64;
                if (now - _lastProgressTicks >= 120 || (total > 0 && copied >= total))
                {
                    _lastProgressTicks = now;
                    progress(copied, total);
                }
            }
        }
    }

    private void ExecuteData(Func<FtpReply> initiate, Action<Stream> transfer)
    {
        TcpClient? dataClient = null;
        TcpListener? listener = null;

        try
        {
            if (_passive)
            {
                dataClient = CreatePassiveDataConnection();
            }
            else
            {
                listener = new TcpListener(IPAddress.Any, 0);
                listener.Start();
                var local = (IPEndPoint)listener.LocalEndpoint;
                var address = ((IPEndPoint)_control!.Client.LocalEndPoint!).Address;
                if (address.IsIPv4MappedToIPv6)
                    address = address.MapToIPv4();
                if (address.AddressFamily != AddressFamily.InterNetwork)
                    throw new FtpException("Active mode requires an IPv4 local address. Use passive mode instead.");
                var bytes = address.GetAddressBytes();
                var port = local.Port;
                var reply = Send($"PORT {bytes[0]},{bytes[1]},{bytes[2]},{bytes[3]},{port / 256},{port % 256}");
                if (reply.Code != 200)
                    throw new FtpException(reply.Code, $"PORT failed: {reply.Message}");
            }

            var preliminary = initiate();
            if (preliminary.Code is not (125 or 150))
                throw new FtpException(preliminary.Code, $"Data command rejected: {preliminary.Message}");

            if (!_passive && listener != null)
                dataClient = listener.AcceptTcpClient();

            if (dataClient == null)
                throw new FtpException("No data connection was established.");

            using (var stream = WrapDataStream(dataClient))
            {
                transfer(stream);
                try
                {
                    stream.Flush();
                }
                catch (IOException)
                {
                }
            }

            var final = ReadReply();
            if (final.Code is not (226 or 250 or 200))
                throw new FtpException(final.Code, $"Transfer failed: {final.Message}");
        }
        finally
        {
            dataClient?.Dispose();
            listener?.Stop();
        }
    }

    private TcpClient CreatePassiveDataConnection()
    {
        string host;
        int port;

        var epsv = Send("EPSV");
        if (epsv.Code == 229)
        {
            var value = ExtractParenthesized(epsv.Lines[0]);
            var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || !int.TryParse(parts[^1], out port))
                throw new FtpException(epsv.Code, $"Could not parse EPSV reply: {epsv.Message}");
            host = ((IPEndPoint)_control!.Client.RemoteEndPoint!).Address.ToString();
        }
        else
        {
            var pasv = Send("PASV");
            if (pasv.Code != 227)
                throw new FtpException(pasv.Code, $"PASV failed: {pasv.Message}");

            var value = ExtractParenthesized(pasv.Lines[0]);
            var numbers = value.Split(',').Select(x => x.Trim()).ToArray();
            if (numbers.Length != 6 || !numbers.All(n => int.TryParse(n, out _)))
                throw new FtpException(pasv.Code, $"Could not parse PASV reply: {pasv.Message}");

            host = $"{numbers[0]}.{numbers[1]}.{numbers[2]}.{numbers[3]}";
            port = int.Parse(numbers[4]) * 256 + int.Parse(numbers[5]);
            host = ResolvePasvHost(host);
        }

        var client = new TcpClient { NoDelay = true };
        ConnectSocket(client, host, port, _profile.TimeoutSeconds * 1000);
        return client;
    }

    private string ResolvePasvHost(string host)
    {
        if (_profile.UsePasvAddress)
            return host;

        var controlAddress = ((IPEndPoint)_control!.Client.RemoteEndPoint!).Address;
        if (!IPAddress.TryParse(host, out var pasvAddress))
            return host;

        if (pasvAddress.Equals(IPAddress.Any) || pasvAddress.Equals(IPAddress.None))
            return controlAddress.ToString();

        if (IsPrivate(pasvAddress) && IsPublic(controlAddress))
            return controlAddress.ToString();

        return host;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var b = address.GetAddressBytes();
        if (b[0] == 10)
            return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            return true;
        if (b[0] == 192 && b[1] == 168)
            return true;
        if (b[0] == 127)
            return true;
        return false;
    }

    private static bool IsPublic(IPAddress address) => !IsPrivate(address);

    private Stream WrapDataStream(TcpClient client)
    {
        var stream = client.GetStream();
        if (!_dataProtected)
            return stream;

        var ssl = new SslStream(stream, false, ValidateCertificate);
        ssl.AuthenticateAsClient(_profile.Host);
        return ssl;
    }

    private void UpgradeControlToTls()
    {
        var ssl = new SslStream(_controlStream!, false, ValidateCertificate);
        ssl.AuthenticateAsClient(_profile.Host);
        _controlStream = ssl;
    }

    private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (!_profile.ValidateCertificate)
            return true;
        return errors == SslPolicyErrors.None;
    }

    private FtpReply Send(string command)
    {
        WriteLine(command);
        return ReadReply();
    }

    private FtpReply Expect(string command, params int[] codes)
    {
        var reply = Send(command);
        if (!codes.Contains(reply.Code))
            throw new FtpException(reply.Code, $"{command} -> {reply.Message}");
        return reply;
    }

    private void TrySend(string command)
    {
        try
        {
            Send(command);
        }
        catch (FtpException)
        {
        }
    }

    private void WriteLine(string line)
    {
        _log?.Invoke(">> " + (line.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) ? "PASS ****" : line));
        var bytes = _encoding.GetBytes(line + "\r\n");
        _controlStream!.Write(bytes, 0, bytes.Length);
        _controlStream.Flush();
    }

    private string ReadLine()
    {
        var buffer = new List<byte>(128);
        while (true)
        {
            int b;
            try
            {
                b = _controlStream!.ReadByte();
            }
            catch (IOException ex)
            {
                throw new FtpException("Connection lost: " + ex.Message);
            }

            if (b < 0)
                throw new FtpException("Connection closed by server.");
            if (b == '\n')
                break;
            if (b == '\r')
                continue;

            buffer.Add((byte)b);
        }

        return _encoding.GetString(buffer.ToArray());
    }

    private FtpReply ReadReply()
    {
        var first = ReadLine();
        _log?.Invoke("<< " + first);

        var lines = new List<string> { first };
        if (first.Length >= 4 && first[3] == '-')
        {
            var code = first[..3];
            while (true)
            {
                var line = ReadLine();
                _log?.Invoke("<< " + line);
                lines.Add(line);
                if (line.Length >= 4 && line.StartsWith(code, StringComparison.Ordinal) && line[3] == ' ')
                    break;
                if (line.Length == 3 && line == code)
                    break;
            }
        }

        var parsedCode = first.Length >= 3 && int.TryParse(first[..3], out var value) ? value : 0;
        return new FtpReply(parsedCode, lines);
    }

    private static string ExtractParenthesized(string line)
    {
        var open = line.IndexOf('(');
        var close = line.LastIndexOf(')');
        if (open >= 0 && close > open)
            return line.Substring(open + 1, close - open - 1);
        return line;
    }

    private FtpListItem? ParseMlsdLine(string line, string path)
    {
        var separator = line.IndexOf(' ');
        if (separator < 0)
            return null;

        var facts = line[..separator];
        var name = line[(separator + 1)..];
        if (name is "." or "..")
            return null;

        var item = new FtpListItem
        {
            Name = name,
            FullPath = PathUtil.JoinRemote(path, name)
        };

        foreach (var fact in facts.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = fact.IndexOf('=');
            if (equals < 0)
                continue;

            var key = fact[..equals].ToLowerInvariant();
            var value = fact[(equals + 1)..];

            switch (key)
            {
                case "type":
                    if (value.Equals("dir", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("cdir", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("pdir", StringComparison.OrdinalIgnoreCase))
                        item.IsDirectory = true;
                    else if (value.Contains("slink", StringComparison.OrdinalIgnoreCase))
                        item.IsSymlink = true;
                    break;
                case "size":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                        item.Size = size;
                    break;
                case "modify":
                    if (TryParseMlsdTime(value, out var modified))
                        item.ModifiedUtc = modified;
                    break;
            }
        }

        return item;
    }

    private static readonly Regex UnixListRegex = new(
        @"^(?<type>[-dlbcps])(?<perms>[rwxstST-]{9})\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<month>[A-Za-z]{3})\s+(?<day>\d{1,2})\s+(?<year>\d{4}|\d{1,2}:\d{2})\s+(?<name>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex DosListRegex = new(
        @"^(?<month>\d{2})-(?<day>\d{2})-(?<year>\d{2,4})\s+(?<hour>\d{2}):(?<minute>\d{2})(?<ampm>AM|PM)\s+(?<size><DIR>|\d+)\s+(?<name>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] Months =
    {
        "jan", "feb", "mar", "apr", "may", "jun",
        "jul", "aug", "sep", "oct", "nov", "dec"
    };

    private static FtpListItem? ParseUnixLine(string line, string path)
    {
        var match = UnixListRegex.Match(line);
        if (!match.Success)
            return null;

        var type = match.Groups["type"].Value[0];
        var name = match.Groups["name"].Value;
        var isSymlink = type == 'l';
        if (isSymlink)
        {
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
                name = name[..arrow];
        }

        if (name.Trim() is "." or "..")
            return null;

        var monthIndex = Array.IndexOf(Months, match.Groups["month"].Value.ToLowerInvariant());
        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var yearToken = match.Groups["year"].Value;
        int year;
        int hour = 0;
        var minute = 0;

        if (yearToken.Contains(':'))
        {
            year = DateTime.UtcNow.Year;
            var parts = yearToken.Split(':');
            hour = int.Parse(parts[0], CultureInfo.InvariantCulture);
            minute = int.Parse(parts[1], CultureInfo.InvariantCulture);
        }
        else
        {
            year = int.Parse(yearToken, CultureInfo.InvariantCulture);
        }

        DateTime? modified = null;
        if (monthIndex >= 0 && day is >= 1 and <= 31)
            modified = new DateTime(year, monthIndex + 1, day, hour, minute, 0, DateTimeKind.Utc);

        return new FtpListItem
        {
            Name = name,
            FullPath = PathUtil.JoinRemote(path, name),
            IsDirectory = type == 'd',
            IsSymlink = isSymlink,
            Size = long.TryParse(match.Groups["size"].Value, out var size) ? size : 0,
            ModifiedUtc = modified
        };
    }

    private static FtpListItem? ParseDosLine(string line, string path)
    {
        var match = DosListRegex.Match(line);
        if (!match.Success)
            return null;

        var name = match.Groups["name"].Value.Trim();
        if (name is "." or "..")
            return null;

        var isDirectory = match.Groups["size"].Value.Equals("<DIR>", StringComparison.OrdinalIgnoreCase);
        var month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        if (year < 100)
            year += 2000;

        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture) % 12;
        if (match.Groups["ampm"].Value.Equals("PM", StringComparison.OrdinalIgnoreCase))
            hour += 12;
        var minute = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);

        DateTime? modified = null;
        if (month is >= 1 and <= 12 && day is >= 1 and <= 31)
            modified = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);

        return new FtpListItem
        {
            Name = name,
            FullPath = PathUtil.JoinRemote(path, name),
            IsDirectory = isDirectory,
            Size = long.TryParse(match.Groups["size"].Value, out var size) ? size : 0,
            ModifiedUtc = modified
        };
    }

    private static readonly string[] MlsdTimeFormats =
    {
        "yyyyMMddHHmmss",
        "yyyyMMddHHmmss'.'f",
        "yyyyMMddHHmmss'.'ff",
        "yyyyMMddHHmmss'.'fff"
    };

    private static bool TryParseMlsdTime(string value, out DateTime result)
    {
        return DateTime.TryParseExact(
            value,
            MlsdTimeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);
    }

    private static void ConnectSocket(TcpClient client, string host, int port, int timeoutMs)
    {
        try
        {
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(timeoutMs))
                throw new FtpException($"Timed out connecting to {host}:{port}.");
            task.GetAwaiter().GetResult();
        }
        catch (AggregateException ex)
        {
            var inner = ex.InnerException ?? ex;
            throw new FtpException($"Failed to connect to {host}:{port} - {inner.Message}");
        }
    }

    public void Dispose()
    {
        if (_control == null)
            return;

        try
        {
            if (IsConnected)
                TrySend("QUIT");
        }
        catch
        {
        }

        try
        {
            _controlStream?.Dispose();
            _control?.Dispose();
        }
        catch
        {
        }

        _controlStream = null;
        _control = null;
        IsConnected = false;
    }
}