using System.Globalization;
using System.Text;
using ShyFtp.Configuration;
using ShyFtp.Ftp;
using ShyFtp.Sync;
using ShyFtp.Util;

namespace ShyFtp.Cli;

public sealed class Shell : IDisposable
{
    private readonly AppConfig _config;
    private readonly ConfigStore _store;

    private string _root;
    private FolderProfile? _folder;
    private ServerProfile? _server;
    private FtpClient? _client;
    private List<SyncItem> _items = new();
    private bool _verbose;

    public Shell(AppConfig config, ConfigStore store, string? startFolder)
    {
        _config = config;
        _store = store;
        _root = Path.GetFullPath(startFolder ?? Directory.GetCurrentDirectory());
        RefreshFolderMapping();
    }

    public int Run()
    {
        PrintBanner();
        ConsoleUtil.Muted($"Config: {_store.Path}");
        ConsoleUtil.Muted("Type 'help' for commands.");

        while (true)
        {
            Console.Write(BuildPrompt());
            var line = Console.ReadLine();
            if (line == null)
                break;

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
                continue;

            var command = tokens[0].ToLowerInvariant();
            var args = tokens.Skip(1).ToList();

            try
            {
                if (command is "quit" or "exit" or "q")
                    break;

                Dispatch(command, args);
            }
            catch (FtpException ex)
            {
                ConsoleUtil.Error($"FTP error ({ex.Code}): {ex.Message}");
            }
            catch (Exception ex)
            {
                ConsoleUtil.Error($"Error: {ex.Message}");
            }
        }

        Disconnect();
        ConsoleUtil.Muted("Bye.");
        return 0;
    }

    private void Dispatch(string command, List<string> args)
    {
        switch (command)
        {
            case "help" or "?" or "h": PrintHelp(); break;
            case "status": PrintStatus(); break;
            case "folder" or "cd": CmdFolder(args); break;
            case "map" or "use": CmdMap(args); break;
            case "unmap": CmdUnmap(); break;
            case "servers": CmdServers(); break;
            case "server-add": CmdServerAdd(args); break;
            case "server-edit": CmdServerEdit(args); break;
            case "server-remove": CmdServerRemove(args); break;
            case "connect" or "open": Connect(); break;
            case "disconnect" or "close": Disconnect(); break;
            case "pwd": CmdPwd(); break;
            case "ls" or "dir": CmdList(args, detailed: false); break;
            case "ll": CmdList(args, detailed: true); break;
            case "scan" or "refresh" or "diff": CmdScan(); break;
            case "changes" or "list": PrintItems(); break;
            case "select": CmdSelect(args, true); break;
            case "deselect": CmdSelect(args, false); break;
            case "add" or "find" or "search": CmdAdd(args); break;
            case "remove": CmdRemove(args); break;
            case "pattern" or "match": CmdPattern(args); break;
            case "upload" or "push": CmdTransfer(upload: true, args); break;
            case "download" or "pull": CmdTransfer(upload: false, args); break;
            case "sync": CmdSync(); break;
            case "ignore": CmdIgnore(args); break;
            case "unignore": CmdUnignore(args); break;
            case "ignored": CmdIgnored(); break;
            case "mkdir": CmdMkdir(args); break;
            case "rmdir": CmdRmdir(args); break;
            case "rm": CmdRm(args); break;
            case "rename": CmdRename(args); break;
            case "get": CmdGet(args); break;
            case "put": CmdPut(args); break;
            case "verbose": _verbose = !_verbose; ConsoleUtil.Info($"Verbose: {_verbose}"); break;
            default:
                ConsoleUtil.Error($"Unknown command '{command}'. Type 'help'.");
                break;
        }
    }

    private void PrintBanner()
    {
        ConsoleUtil.Accent("ShyFTP - console FTP/FTPS sync client");
        ConsoleUtil.Muted("From-scratch FTP engine. Supports FTP, FTPS (explicit/implicit), PASV/EPSV/PORT, MLSD/LIST.");
    }

    private string BuildPrompt()
    {
        var mapped = _folder != null ? $" ({_folder.Server})" : " (unmapped)";
        var connected = _client is { IsConnected: true } ? "*" : "";
        return $"shyftp{connected}:{_root}{mapped}> ";
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var quote = '\0';

        foreach (var c in input)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    sb.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }

                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
            tokens.Add(sb.ToString());
        return tokens;
    }

    private void RefreshFolderMapping()
    {
        _folder = _store.FindFolder(_config, _root);
        _server = _folder != null && _config.Servers.TryGetValue(_folder.Server, out var server) ? server : null;
        _items = new List<SyncItem>();
    }

    private List<string> IgnorePatterns()
    {
        var list = new List<string>(_config.GlobalIgnore);
        if (_folder != null)
            list.AddRange(_folder.Ignore);
        return list;
    }

    private FolderProfile? FindExactFolder()
    {
        var target = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return _config.Folders.FirstOrDefault(f =>
            string.Equals(Path.GetFullPath(f.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                target, StringComparison.OrdinalIgnoreCase));
    }

    private bool Connect()
    {
        if (_folder == null)
        {
            ConsoleUtil.Error("This folder is not mapped to a server. Use 'map <server> [remotePath]'.");
            return false;
        }

        if (!_config.Servers.TryGetValue(_folder.Server, out var server))
        {
            ConsoleUtil.Error($"Server '{_folder.Server}' is not defined. Use 'servers' or 'server-add'.");
            return false;
        }

        Disconnect();
        _server = server;
        _client = new FtpClient(server, s =>
        {
            if (_verbose)
                ConsoleUtil.Muted(s);
        });

        ConsoleUtil.Info($"Connecting to {server.Host}:{server.Port} ({server.Security})...");
        try
        {
            _client.Connect();
            var pwd = _client.PrintWorkingDirectory();
            ConsoleUtil.Success($"Connected. Remote directory: {pwd}");
            return true;
        }
        catch (Exception ex)
        {
            ConsoleUtil.Error("Connection failed: " + ex.Message);
            _client.Dispose();
            _client = null;
            return false;
        }
    }

    private bool EnsureClient() => _client is { IsConnected: true } || Connect();

    private void Disconnect()
    {
        if (_client == null)
            return;

        _client.Dispose();
        _client = null;
    }

    private static string Prompt(string label, string? defaultValue = null)
    {
        var suffix = string.IsNullOrEmpty(defaultValue) ? "" : $" [{defaultValue}]";
        Console.Write($"{label}{suffix}: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
            return defaultValue ?? "";
        return input.Trim();
    }

    private void PrintStatus()
    {
        ConsoleUtil.Accent("Status");
        Console.WriteLine($"  Local folder : {_root}");
        Console.WriteLine($"  Mapping      : {(_folder == null ? "(none)" : _folder.Server)}");
        Console.WriteLine($"  Remote path  : {(_folder?.RemotePath ?? "(n/a)")}");
        Console.WriteLine($"  Recursive    : {_folder?.Recursive ?? false}");
        Console.WriteLine($"  Connection   : {(_client is { IsConnected: true } ? _server?.Host : "(not connected)")}");
        Console.WriteLine($"  Ignore rules : {IgnorePatterns().Count}");
        Console.WriteLine($"  Change list  : {_items.Count} item(s), {_items.Count(i => i.Selected)} selected");
    }

    private void CmdFolder(List<string> args)
    {
        if (args.Count == 0)
        {
            ConsoleUtil.Info($"Current folder: {_root}");
            return;
        }

        var target = Path.GetFullPath(args[0]);
        if (!Directory.Exists(target))
        {
            ConsoleUtil.Error($"Directory not found: {target}");
            return;
        }

        Disconnect();
        _root = target;
        RefreshFolderMapping();
        ConsoleUtil.Success($"Folder: {_root}");
        if (_folder != null)
            ConsoleUtil.Info($"Mapped to server '{_folder.Server}' ({_folder.RemotePath}).");
    }

    private void CmdMap(List<string> args)
    {
        if (args.Count == 0)
        {
            ConsoleUtil.Error("Usage: map <serverName> [remotePath]");
            return;
        }

        var name = args[0];
        if (!_config.Servers.ContainsKey(name))
        {
            ConsoleUtil.Error($"Server '{name}' not defined. Use 'server-add {name} <host>' first.");
            return;
        }

        var remote = args.Count > 1 ? args[1] : "/";
        var folder = FindExactFolder();
        if (folder == null)
        {
            folder = new FolderProfile { Path = _root };
            _config.Folders.Add(folder);
        }

        folder.Server = name;
        folder.RemotePath = remote.StartsWith('/') ? remote : "/" + remote;
        _store.Save(_config);
        RefreshFolderMapping();
        ConsoleUtil.Success($"Mapped {_root} -> {name}:{folder.RemotePath}");
    }

    private void CmdUnmap()
    {
        var folder = FindExactFolder();
        if (folder == null)
        {
            ConsoleUtil.Warn("This folder is not mapped.");
            return;
        }

        _config.Folders.Remove(folder);
        _store.Save(_config);
        Disconnect();
        RefreshFolderMapping();
        ConsoleUtil.Success("Mapping removed.");
    }

    private void CmdServers()
    {
        if (_config.Servers.Count == 0)
        {
            ConsoleUtil.Warn("No servers defined. Use 'server-add <name> <host>'.");
            return;
        }

        ConsoleUtil.Accent($"{"Name",-20} {"Host",-30} {"Port",5} {"Security",-12} Folders");
        foreach (var (name, server) in _config.Servers.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var folders = _config.Folders.Count(f => string.Equals(f.Server, name, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"{name,-20} {server.Host,-30} {server.Port,5} {server.Security,-12} {folders}");
        }
    }

    private void CmdServerAdd(List<string> args)
    {
        var name = args.Count > 0 ? args[0] : Prompt("Server name");
        if (string.IsNullOrWhiteSpace(name))
        {
            ConsoleUtil.Error("A server name is required.");
            return;
        }

        if (_config.Servers.ContainsKey(name))
        {
            ConsoleUtil.Error($"Server '{name}' already exists. Use 'server-edit {name} ...'.");
            return;
        }

        var host = args.Count > 1 ? args[1] : Prompt("Host");
        var portText = args.Count > 2 ? args[2] : Prompt("Port", "21");
        var username = args.Count > 3 ? args[3] : Prompt("Username", "anonymous");
        var password = args.Count > 4 ? args[4] : Prompt("Password", "");

        if (!int.TryParse(portText, out var port))
            port = 21;

        var securityText = Prompt("Security (none/explicit/implicit)", "none");
        var security = securityText.ToLowerInvariant() switch
        {
            "implicit" or "implicit-tls" => FtpSecurity.ImplicitTls,
            "explicit" or "explicit-tls" or "tls" or "ftps" => FtpSecurity.ExplicitTls,
            _ => FtpSecurity.None
        };

        var validate = !securityText.Equals("none", StringComparison.OrdinalIgnoreCase)
            && ConsoleUtil.Confirm("Validate TLS certificate?", true);

        _config.Servers[name] = new ServerProfile
        {
            Host = host,
            Port = port,
            Username = string.IsNullOrEmpty(username) ? "anonymous" : username,
            Password = password,
            Security = security,
            ValidateCertificate = validate
        };

        _store.Save(_config);
        ConsoleUtil.Success($"Server '{name}' saved.");
    }

    private void CmdServerEdit(List<string> args)
    {
        if (args.Count < 3)
        {
            ConsoleUtil.Error("Usage: server-edit <name> <host|port|user|pass|security|passive|validate|utf8> <value>");
            return;
        }

        var name = args[0];
        if (!_config.Servers.TryGetValue(name, out var server))
        {
            ConsoleUtil.Error($"Server '{name}' not found.");
            return;
        }

        var key = args[1].ToLowerInvariant();
        var value = args[2];

        switch (key)
        {
            case "host": server.Host = value; break;
            case "port": if (int.TryParse(value, out var port)) server.Port = port; break;
            case "user" or "username": server.Username = value; break;
            case "pass" or "password": server.Password = value; break;
            case "security":
                server.Security = value.ToLowerInvariant() switch
                {
                    "implicit" => FtpSecurity.ImplicitTls,
                    "explicit" or "tls" or "ftps" => FtpSecurity.ExplicitTls,
                    _ => FtpSecurity.None
                };
                break;
            case "passive": server.Passive = value is "1" or "true" or "yes"; break;
            case "validate": server.ValidateCertificate = value is "1" or "true" or "yes"; break;
            case "utf8": server.Utf8 = value is "1" or "true" or "yes"; break;
            default:
                ConsoleUtil.Error($"Unknown field '{key}'.");
                return;
        }

        _store.Save(_config);
        ConsoleUtil.Success($"Updated {name}.{key}.");
    }

    private void CmdServerRemove(List<string> args)
    {
        if (args.Count == 0)
        {
            ConsoleUtil.Error("Usage: server-remove <name>");
            return;
        }

        if (!_config.Servers.Remove(args[0]))
        {
            ConsoleUtil.Error($"Server '{args[0]}' not found.");
            return;
        }

        _store.Save(_config);
        ConsoleUtil.Success($"Removed server '{args[0]}'.");
    }

    private void CmdPwd()
    {
        if (!EnsureClient())
            return;
        ConsoleUtil.Info("Remote directory: " + _client!.PrintWorkingDirectory());
    }

    private void CmdList(List<string> args, bool detailed)
    {
        if (!EnsureClient())
            return;

        var path = args.Count > 0 ? ResolveRemote(args[0]) : _folder?.RemotePath ?? "";
        var entries = _client!.List(path);

        if (entries.Count == 0)
        {
            ConsoleUtil.Muted("(empty)");
            return;
        }

        ConsoleUtil.Accent($"Listing {(path.Length == 0 ? "/" : path)}");
        foreach (var entry in entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (detailed)
            {
                var kind = entry.IsDirectory ? "d" : entry.IsSymlink ? "l" : "-";
                var size = entry.IsDirectory ? "" : PathUtil.FormatSize(entry.Size);
                var when = entry.ModifiedUtc?.ToString("yyyy-MM-dd HH:mm") ?? "";
                Console.WriteLine($"  {kind} {size,10} {when,-16} {entry.Name}");
            }
            else
            {
                Console.WriteLine($"  {entry.Name}{(entry.IsDirectory ? "/" : "")}");
            }
        }
    }

    private string ResolveRemote(string path)
    {
        if (path.StartsWith('/'))
            return path;
        return PathUtil.JoinRemote(_folder?.RemotePath ?? "/", path);
    }

    private void CmdScan()
    {
        if (!EnsureClient())
            return;

        if (_folder == null)
        {
            ConsoleUtil.Error("No server mapped to this folder. Use 'map <server> [remotePath]'.");
            return;
        }

        ConsoleUtil.Info("Scanning local files...");
        var local = SyncEngine.ScanLocal(_root, _folder.Recursive, IgnorePatterns());

        ConsoleUtil.Info($"Scanning remote {_folder.RemotePath} ...");
        var remote = SyncEngine.ScanRemote(
            _client!,
            _folder.RemotePath,
            _folder.Recursive,
            _verbose ? p => ConsoleUtil.Muted("  listed /" + p) : null);

        _items = SyncEngine.Compare(local, remote, _root, _folder.RemotePath);

        var uploads = _items.Count(i => i.State is SyncState.UploadNew or SyncState.UploadChanged);
        var downloads = _items.Count(i => i.State is SyncState.DownloadNew or SyncState.DownloadChanged);
        var diffs = _items.Count(i => i.State == SyncState.Different);

        ConsoleUtil.Success($"Compare complete: {_items.Count} entries - {uploads} upload, {downloads} download, {diffs} review.");
        PrintItems();
    }

    private void PrintItems()
    {
        if (_items.Count == 0)
        {
            ConsoleUtil.Muted("(change list is empty - run 'scan')");
            return;
        }

        ConsoleUtil.Accent($"{"#",4}  Sel  {"Suggested",-18} {"Size",10}  Path");
        for (var i = 0; i < _items.Count; i++)
            WriteItemLine(i + 1, _items[i]);
    }

    private void WriteItemLine(int index, SyncItem item)
    {
        var marker = item.Selected ? ">" : " ";
        var color = item.State switch
        {
            SyncState.UploadNew or SyncState.UploadChanged => ConsoleColor.Yellow,
            SyncState.DownloadNew or SyncState.DownloadChanged => ConsoleColor.Cyan,
            SyncState.Different => ConsoleColor.Red,
            _ => ConsoleColor.DarkGray
        };

        var size = item.State switch
        {
            SyncState.UploadNew or SyncState.UploadChanged => PathUtil.FormatSize(item.LocalSize),
            SyncState.DownloadNew or SyncState.DownloadChanged => PathUtil.FormatSize(item.RemoteSize),
            _ => PathUtil.FormatSize(item.LocalSize)
        };

        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
        }
        catch (IOException)
        {
        }

        Console.WriteLine($"{marker}{index,4}  {(item.Selected ? "[x]" : "[ ]")}  {item.SuggestedAction,-18} {size,10}  {item.RelativePath}");

        try
        {
            Console.ForegroundColor = previous;
        }
        catch (IOException)
        {
        }
    }

    private void CmdSelect(List<string> args, bool select)
    {
        if (args.Count == 0)
        {
            ConsoleUtil.Error($"Usage: {(select ? "select" : "deselect")} <n,n,n | a-b | all|none|up|down|new|changed|invert>");
            return;
        }

        ApplySelectionTokens(args, select);
        PrintItems();
    }

    private void ApplySelectionTokens(IEnumerable<string> tokens, bool select)
    {
        foreach (var token in ExpandCommas(tokens))
        {
            switch (token.ToLowerInvariant())
            {
                case "all":
                    foreach (var item in _items)
                        item.Selected = select;
                    break;
                case "none":
                    foreach (var item in _items)
                        item.Selected = false;
                    break;
                case "invert":
                    foreach (var item in _items)
                        item.Selected = !item.Selected;
                    break;
                case "up" or "uploads":
                    foreach (var item in _items.Where(i => i.State is SyncState.UploadNew or SyncState.UploadChanged))
                        item.Selected = select;
                    break;
                case "down" or "downloads":
                    foreach (var item in _items.Where(i => i.State is SyncState.DownloadNew or SyncState.DownloadChanged))
                        item.Selected = select;
                    break;
                case "new":
                    foreach (var item in _items.Where(i => i.State is SyncState.UploadNew or SyncState.DownloadNew))
                        item.Selected = select;
                    break;
                case "changed":
                    foreach (var item in _items.Where(i => i.State is SyncState.UploadChanged or SyncState.DownloadChanged or SyncState.Different))
                        item.Selected = select;
                    break;
                default:
                    ApplyIndexOrRange(token, select);
                    break;
            }
        }
    }

    private static IEnumerable<string> ExpandCommas(IEnumerable<string> tokens)
    {
        foreach (var token in tokens)
        {
            foreach (var part in token.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return part;
        }
    }

    private void ApplyIndexOrRange(string token, bool select)
    {
        var dash = token.IndexOf('-');
        if (dash > 0 && int.TryParse(token[..dash], out var start) && int.TryParse(token[(dash + 1)..], out var end))
        {
            for (var i = start; i <= end; i++)
                SetIndex(i, select);
            return;
        }

        if (int.TryParse(token, out var index))
        {
            SetIndex(index, select);
            return;
        }

        ConsoleUtil.Warn($"Ignoring unrecognized selector '{token}'.");
    }

    private void SetIndex(int oneBased, bool select)
    {
        if (oneBased < 1 || oneBased > _items.Count)
        {
            ConsoleUtil.Warn($"Index out of range: {oneBased}");
            return;
        }

        _items[oneBased - 1].Selected = select;
    }

    private void CmdAdd(List<string> args)
    {
        if (args.Count == 0)
        {
            ConsoleUtil.Error("Usage: add <pattern...> | add recent <hours> | add all [--all]");
            return;
        }

        var includeIgnored = args.Remove("--all");

        if (args.Count >= 2 && args[0].Equals("recent", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseHours(args[1], out var hours))
            {
                ConsoleUtil.Error("Usage: add recent <hours>  (e.g. add recent 8.4)");
                return;
            }

            var cutoff = DateTime.UtcNow.AddHours(-hours);
            AddLocalFiles(
                includeIgnored,
                (_, info) => info.ModifiedUtc >= cutoff,
                $"modified in the last {hours} hour(s)");
            return;
        }

        if (args.Count == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            AddLocalFiles(includeIgnored, (_, _) => true, "all local files");
            return;
        }

        var patterns = args.ToList();
        AddLocalFiles(
            includeIgnored,
            (relative, _) => GlobMatcher.AnyMatches(patterns, relative),
            $"matching {string.Join(", ", patterns)}");
    }

    private void AddLocalFiles(bool includeIgnored, Func<string, EntryInfo, bool> predicate, string description)
    {
        var recursive = _folder?.Recursive ?? true;
        IEnumerable<string> ignore = includeIgnored ? Array.Empty<string>() : IgnorePatterns();

        ConsoleUtil.Info($"Searching local files ({description})...");
        var local = SyncEngine.ScanLocal(_root, recursive, ignore);

        var matches = local
            .Where(kv => predicate(kv.Key, kv.Value))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            ConsoleUtil.Warn("No matching files.");
            return;
        }

        var added = 0;
        foreach (var relative in matches)
        {
            var item = FindItem(relative);
            if (item == null)
            {
                local.TryGetValue(relative, out var info);
                item = CreateLocalItem(relative, info);
                _items.Add(item);
                added++;
            }

            item.Selected = true;
        }

        ConsoleUtil.Success($"{matches.Count} match(es), {added} added to list, selected for upload.");
        PrintItems();
    }

    private SyncItem CreateLocalItem(string relative, EntryInfo info) => new()
    {
        RelativePath = relative,
        LocalExists = true,
        LocalSize = info.Size,
        LocalTime = info.ModifiedUtc,
        LocalFullPath = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)),
        RemoteFullPath = PathUtil.JoinRemote(_folder?.RemotePath ?? "/", relative),
        State = SyncState.Unchanged
    };

    private void CmdRemove(List<string> args)
    {
        if (args.Count == 0)
        {
            ConsoleUtil.Error("Usage: remove <pattern...> | remove recent <hours> | remove all | remove selected | remove <n,n,a-b>");
            return;
        }

        if (args.Count == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var count = _items.Count;
            _items.Clear();
            ConsoleUtil.Success($"Removed all {count} item(s) from the upload list.");
            return;
        }

        if (args.Count == 1 && args[0].Equals("selected", StringComparison.OrdinalIgnoreCase))
        {
            var removed = _items.RemoveAll(i => i.Selected);
            ConsoleUtil.Success($"Removed {removed} selected item(s) from the upload list.");
            PrintItems();
            return;
        }

        if (args.Count >= 2 && args[0].Equals("recent", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseHours(args[1], out var hours))
            {
                ConsoleUtil.Error("Usage: remove recent <hours>  (e.g. remove recent 8.4)");
                return;
            }

            var cutoff = DateTime.UtcNow.AddHours(-hours);
            var removed = _items.RemoveAll(i => i.LocalExists && i.LocalTime >= cutoff);
            ConsoleUtil.Success($"Removed {removed} item(s) modified in the last {hours} hour(s).");
            PrintItems();
            return;
        }

        if (args.All(IsIndexToken))
        {
            var removed = RemoveIndices(args);
            ConsoleUtil.Success($"Removed {removed} item(s) by index.");
            PrintItems();
            return;
        }

        var removedCount = _items.RemoveAll(i => GlobMatcher.AnyMatches(args, i.RelativePath));
        ConsoleUtil.Success($"Removed {removedCount} item(s) matching {string.Join(", ", args)}.");
        PrintItems();
    }

    private static bool TryParseHours(string text, out double hours) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out hours) && hours >= 0;

    private static bool IsIndexToken(string token) =>
        token.Length > 0 && token.All(c => char.IsDigit(c) || c == '-' || c == ',');

    private int RemoveIndices(IEnumerable<string> tokens)
    {
        var indexes = new SortedSet<int>();
        foreach (var token in ExpandCommas(tokens))
        {
            var dash = token.IndexOf('-');
            if (dash > 0 && int.TryParse(token[..dash], out var start) && int.TryParse(token[(dash + 1)..], out var end))
            {
                for (var i = start; i <= end; i++)
                    indexes.Add(i);
            }
            else if (int.TryParse(token, out var index))
            {
                indexes.Add(index);
            }
        }

        var removed = 0;
        foreach (var index in indexes.Reverse())
        {
            if (index >= 1 && index <= _items.Count)
            {
                _items.RemoveAt(index - 1);
                removed++;
            }
        }

        return removed;
    }

    private SyncItem? FindItem(string relative) =>
        _items.FirstOrDefault(i => string.Equals(i.RelativePath, relative, StringComparison.OrdinalIgnoreCase));

    private void CmdPattern(List<string> args)
    {
        if (args.Count == 0)
        {
            ConsoleUtil.Error("Usage: pattern <glob>  (e.g. pattern *.txt)");
            return;
        }

        var pattern = args[0];
        var count = 0;
        foreach (var item in _items)
        {
            if (GlobMatcher.IsMatch(pattern, item.RelativePath))
            {
                item.Selected = true;
                count++;
            }
        }

        ConsoleUtil.Success($"Selected {count} item(s) matching '{pattern}'.");
        PrintItems();
    }

    private void CmdTransfer(bool upload, List<string> args)
    {
        if (!EnsureClient())
            return;

        if (args.Count > 0)
            ApplySelectionTokens(args, true);

        var candidates = _items
            .Where(i => upload ? i.LocalExists : i.RemoteExists)
            .ToList();

        if (candidates.Count == 0)
        {
            ConsoleUtil.Error(upload
                ? "Nothing to upload. Run 'scan' or 'find <pattern>' first."
                : "Nothing to download. Run 'scan' first.");
            return;
        }

        var targets = candidates.Where(i => i.Selected).ToList();
        if (targets.Count == 0)
        {
            if (!ConsoleUtil.Confirm($"No items selected. Use all {candidates.Count} candidate(s)?", false))
            {
                ConsoleUtil.Muted("Cancelled.");
                return;
            }

            targets = candidates;
        }

        var label = upload ? "upload" : "download";
        ConsoleUtil.Info($"About to {label} {targets.Count} file(s).");
        if (!ConsoleUtil.Confirm("Proceed?", false))
        {
            ConsoleUtil.Muted("Cancelled.");
            return;
        }

        if (upload)
            ApplyUploads(targets);
        else
            ApplyDownloads(targets);
    }

    private void ApplyUploads(List<SyncItem> targets)
    {
        var ok = 0;
        var failed = 0;

        foreach (var item in targets)
        {
            if (!item.LocalExists || item.LocalFullPath == null)
                continue;

            ConsoleUtil.Info($"Uploading {item.RelativePath} ...");
            try
            {
                _client!.UploadFrom(item.LocalFullPath, item.RemoteFullPath!,
                    (current, total) => ShowProgress(item.RelativePath, current, total));
                ClearProgress();
                ConsoleUtil.Success($"  uploaded {item.RelativePath}");
                item.State = SyncState.Unchanged;
                item.RemoteExists = true;
                if (File.Exists(item.LocalFullPath))
                {
                    var info = new FileInfo(item.LocalFullPath);
                    item.LocalSize = info.Length;
                    item.LocalTime = info.LastWriteTimeUtc;
                }

                item.RemoteSize = item.LocalSize;
                item.RemoteTime = _client.TryGetModifiedTime(item.RemoteFullPath!, out var remoteTime)
                    ? remoteTime
                    : item.LocalTime;
                item.Selected = false;
                ok++;
            }
            catch (Exception ex)
            {
                ClearProgress();
                ConsoleUtil.Error($"  failed {item.RelativePath}: {ex.Message}");
                failed++;
            }
        }

        ConsoleUtil.Success($"Upload complete: {ok} succeeded, {failed} failed.");
    }

    private void ApplyDownloads(List<SyncItem> targets)
    {
        var ok = 0;
        var failed = 0;

        foreach (var item in targets)
        {
            if (!item.RemoteExists || item.RemoteFullPath == null || item.LocalFullPath == null)
                continue;

            ConsoleUtil.Info($"Downloading {item.RelativePath} ...");
            try
            {
                _client!.DownloadTo(item.RemoteFullPath, item.LocalFullPath,
                    (current, total) => ShowProgress(item.RelativePath, current, total));
                ClearProgress();
                ConsoleUtil.Success($"  downloaded {item.RelativePath}");
                item.State = SyncState.Unchanged;
                item.LocalExists = true;
                if (File.Exists(item.LocalFullPath))
                {
                    var info = new FileInfo(item.LocalFullPath);
                    item.LocalSize = info.Length;
                    item.LocalTime = info.LastWriteTimeUtc;
                }

                item.Selected = false;
                ok++;
            }
            catch (Exception ex)
            {
                ClearProgress();
                ConsoleUtil.Error($"  failed {item.RelativePath}: {ex.Message}");
                failed++;
            }
        }

        ConsoleUtil.Success($"Download complete: {ok} succeeded, {failed} failed.");
    }

    private static readonly bool ProgressEnabled = !Console.IsOutputRedirected;

    private static void ShowProgress(string label, long current, long total)
    {
        if (!ProgressEnabled)
            return;

        if (total > 0)
        {
            var percent = (int)(current * 100 / total);
            Console.Write($"\r  {label} {percent,3}% ({PathUtil.FormatSize(current)}/{PathUtil.FormatSize(total)})        ");
        }
        else
        {
            Console.Write($"\r  {label} {PathUtil.FormatSize(current)}        ");
        }
    }

    private static void ClearProgress()
    {
        if (!ProgressEnabled)
            return;

        int width;
        try
        {
            width = Math.Min(Console.WindowWidth - 1, 120);
        }
        catch (IOException)
        {
            width = 80;
        }

        Console.Write("\r" + new string(' ', Math.Max(width, 1)) + "\r");
    }

    private void CmdSync()
    {
        if (!EnsureClient())
            return;

        CmdScan();

        var uploads = _items
            .Where(i => i.LocalExists && i.State is SyncState.UploadNew or SyncState.UploadChanged)
            .ToList();
        var downloads = _items
            .Where(i => i.RemoteExists && i.State is SyncState.DownloadNew or SyncState.DownloadChanged)
            .ToList();
        var review = _items.Where(i => i.State == SyncState.Different).ToList();

        if (uploads.Count == 0 && downloads.Count == 0)
        {
            ConsoleUtil.Success("Everything is in sync.");
            return;
        }

        ConsoleUtil.Info($"Plan: {uploads.Count} upload(s), {downloads.Count} download(s).");
        if (review.Count > 0)
            ConsoleUtil.Warn($"{review.Count} item(s) marked 'review' will be skipped - handle them individually.");

        if (!ConsoleUtil.Confirm("Apply this sync?", false))
        {
            ConsoleUtil.Muted("Cancelled.");
            return;
        }

        if (uploads.Count > 0)
            ApplyUploads(uploads);
        if (downloads.Count > 0)
            ApplyDownloads(downloads);
    }

    private void AddIgnorePattern(string pattern)
    {
        if (_folder == null)
            return;

        if (!_folder.Ignore.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            _folder.Ignore.Add(pattern);
    }

    private void CmdIgnore(List<string> args)
    {
        if (_folder == null)
        {
            ConsoleUtil.Error("No mapping for this folder. Use 'map' before setting ignores.");
            return;
        }

        if (args.Count == 0)
        {
            var selected = _items.Where(i => i.Selected).ToList();
            if (selected.Count == 0)
            {
                ConsoleUtil.Error("Usage: ignore <pattern...>  |  select items then run 'ignore'.");
                return;
            }

            foreach (var item in selected)
            {
                AddIgnorePattern(item.RelativePath);
                _items.Remove(item);
            }

            _store.Save(_config);
            ConsoleUtil.Success($"Added {selected.Count} path(s) to ignore list.");
            PrintItems();
            return;
        }

        foreach (var pattern in args)
            AddIgnorePattern(pattern);

        var before = _items.Count;
        _items.RemoveAll(i => GlobMatcher.AnyMatches(args, i.RelativePath));
        _store.Save(_config);
        ConsoleUtil.Success($"Added {args.Count} pattern(s) to ignore list ({before - _items.Count} item(s) hidden).");
        PrintItems();
    }

    private void CmdUnignore(List<string> args)
    {
        if (_folder == null)
        {
            ConsoleUtil.Error("No mapping for this folder.");
            return;
        }

        if (args.Count == 0)
        {
            ConsoleUtil.Error("Usage: unignore <pattern>");
            return;
        }

        var removed = _folder.Ignore.RemoveAll(p =>
            args.Any(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase)));

        _store.Save(_config);
        if (removed > 0)
            ConsoleUtil.Success($"Removed {removed} ignore rule(s).");
        else
            ConsoleUtil.Warn("No matching ignore rule.");
    }

    private void CmdIgnored()
    {
        var patterns = IgnorePatterns();
        if (patterns.Count == 0)
        {
            ConsoleUtil.Muted("(no ignore rules)");
            return;
        }

        ConsoleUtil.Accent("Ignore rules (global + folder):");
        foreach (var pattern in patterns)
            Console.WriteLine("  " + pattern);
    }

    private void CmdMkdir(List<string> args)
    {
        if (!EnsureClient())
            return;
        if (args.Count == 0)
        {
            ConsoleUtil.Error("Usage: mkdir <remotePath>");
            return;
        }

        _client!.EnsureDirectory(ResolveRemote(args[0]));
        ConsoleUtil.Success("Directory created (or already exists).");
    }

    private void CmdRmdir(List<string> args)
    {
        if (!EnsureClient())
            return;

        var recursive = args.Remove("-r");
        if (args.Count == 0)
        {
            ConsoleUtil.Error("Usage: rmdir [-r] <remotePath>");
            return;
        }

        var path = ResolveRemote(args[0]);
        if (recursive && !ConsoleUtil.Confirm($"Recursively delete remote '{path}'?", false))
            return;

        _client!.DeleteDirectory(path, recursive);
        ConsoleUtil.Success("Directory removed.");
    }

    private void CmdRm(List<string> args)
    {
        if (!EnsureClient())
            return;
        if (args.Count == 0)
        {
            ConsoleUtil.Error("Usage: rm <remotePath>");
            return;
        }

        _client!.DeleteFile(ResolveRemote(args[0]));
        ConsoleUtil.Success("File removed.");
    }

    private void CmdRename(List<string> args)
    {
        if (!EnsureClient())
            return;
        if (args.Count < 2)
        {
            ConsoleUtil.Error("Usage: rename <from> <to>");
            return;
        }

        _client!.Rename(ResolveRemote(args[0]), ResolveRemote(args[1]));
        ConsoleUtil.Success("Renamed.");
    }

    private void CmdGet(List<string> args)
    {
        if (!EnsureClient())
            return;
        if (args.Count == 0)
        {
            ConsoleUtil.Error("Usage: get <remotePath> [localPath]");
            return;
        }

        var remote = ResolveRemote(args[0]);
        var local = args.Count > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(_root, PathUtil.RemoteFileName(remote));

        ConsoleUtil.Info($"Downloading {remote} -> {local}");
        _client!.DownloadTo(remote, local, (current, total) => ShowProgress(PathUtil.RemoteFileName(remote), current, total));
        ClearProgress();
        ConsoleUtil.Success("Downloaded.");
    }

    private void CmdPut(List<string> args)
    {
        if (!EnsureClient())
            return;
        if (args.Count == 0)
        {
            ConsoleUtil.Error("Usage: put <localPath> [remotePath]");
            return;
        }

        var local = Path.GetFullPath(args[0]);
        if (!File.Exists(local))
        {
            ConsoleUtil.Error($"Local file not found: {local}");
            return;
        }

        string remote;
        if (args.Count > 1)
        {
            remote = ResolveRemote(args[1]);
        }
        else
        {
            var relative = PathUtil.ToRelative(_root, local);
            if (relative.StartsWith(".."))
                relative = Path.GetFileName(local);
            remote = PathUtil.JoinRemote(_folder?.RemotePath ?? "/", relative);
        }

        ConsoleUtil.Info($"Uploading {local} -> {remote}");
        _client!.UploadFrom(local, remote, (current, total) => ShowProgress(Path.GetFileName(local), current, total));
        ClearProgress();
        ConsoleUtil.Success("Uploaded.");
    }

    private void PrintHelp()
    {
        ConsoleUtil.Accent("Commands");
        Console.WriteLine("""
  folder [path] / cd [path]      Show or change the current local folder
  map <server> [remotePath]      Map this folder to a server + remote path
  unmap                          Remove this folder's mapping
  status                         Show current mapping/connection state

  servers                        List defined servers
  server-add <name> <host> ...   Add a server (prompts for missing values)
  server-edit <name> <field> <v> Edit a server field
  server-remove <name>           Delete a server
  connect / disconnect           Open/close the FTP connection
  verbose                        Toggle raw FTP command logging

  ls [path] / ll [path]          List remote directory (names / details)
  pwd                            Print remote working directory
  mkdir / rmdir [-r] / rm        Remote directory/file operations
  rename <from> <to>             Rename a remote entry
  get <remote> [local]           Download one file
  put <local> [remote]           Upload one file

  scan / refresh / diff          Compare local vs remote, build change list
  changes / list                 Show the change list
  select <sel...>                Select items (1,3,5-7,all,up,down,new,changed,invert)
  deselect <sel...>              Deselect items
  add <pattern...> [--all]       Add local files to the upload list and select them (e.g. add .txt)
  add recent <hours>             Add files modified within the last N hours (e.g. add recent 8.4)
  add all [--all]                Add every local file
  remove <pattern...>            Remove matching items from the upload list
  remove recent <hours>          Remove items modified within the last N hours
  remove selected | all | n,a-b  Remove selected items, everything, or by index
  find <pattern> [--all]         Alias for 'add <pattern>'
  pattern <glob>                 Select change-list items matching a glob (e.g. pattern *.txt)
  upload / push [sel...]         Upload selected (or all candidates)
  download / pull [sel...]       Download selected (or all candidates)
  sync                           Scan and approve the full upload/download plan

  ignore <pattern...>            Add ignore rule(s) (e.g. ignore *.log)
  ignore                         With items selected: ignore those paths
  unignore <pattern>             Remove an ignore rule
  ignored                        Show all ignore rules

  quit / exit                    Leave ShyFTP
""");
    }

    public void Dispose() => Disconnect();
}