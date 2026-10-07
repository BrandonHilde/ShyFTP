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
    private readonly List<Command> _commands;

    private string _root;
    private FolderProfile? _folder;
    private ServerProfile? _server;
    private FtpClient? _client;
    private List<SyncItem> _items = new();
    private bool _verbose;

    private sealed record Command(
        string Name,
        string Group,
        string Usage,
        string Summary,
        Action<List<string>> Run,
        string[] Aliases,
        string? Details = null);

    private const string WhatHelp = """
  <what> can be combined freely, e.g.  upload 1-3 *.css 2h
    3   1,4   5-9      item numbers from the list
    all                everything
    added  dropped     items marked [x] / [-]
    up  down           upload / download candidates
    new  changed  review  unchanged
    30m  8h  2d  1w    changed locally within that time (also: recent 8 = 8h)
    *.css  .txt  img/**  anything else is a glob or text to match in the path
""";

    public Shell(AppConfig config, ConfigStore store, string? startFolder)
    {
        _config = config;
        _store = store;
        _root = Path.GetFullPath(startFolder ?? Directory.GetCurrentDirectory());
        _commands = BuildCommands();
        RefreshFolderMapping();
    }

    private List<Command> BuildCommands() => new()
    {
        // Setup
        new("server", "Setup", "server [add|set|show|remove] ...", "List, add, change or remove servers", CmdServer,
            new[] { "servers" }, """
  server                              List servers
  server add [name] [host] [port] [user] [pass]
                                      Add a server (prompts for anything missing)
  server set <name> <field> [value]   Change a field (prompts if value is omitted)
      fields: host port user pass security(none|explicit|implicit)
              passive validate utf8 pasv-address (on|off) timeout(seconds)
  server show <name>                  Show a server's settings
  server remove <name>                Delete a server
"""),
        new("map", "Setup", "map [server] [remotePath]", "Link this folder to a server (asks if omitted)", CmdMap,
            new[] { "use" }, "  If the server doesn't exist yet, you'll be offered to create it."),
        new("unmap", "Setup", "unmap", "Remove this folder's server link", _ => CmdUnmap(), Array.Empty<string>()),
        new("cd", "Setup", "cd [path]", "Show or change the local folder", CmdFolder,
            new[] { "folder" }, "  Relative paths (including ..) are resolved from the current folder."),
        new("status", "Setup", "status", "Show folder, server, connection and list state", _ => PrintStatus(),
            Array.Empty<string>()),
        new("connect", "Setup", "connect", "Connect now (other commands connect automatically)", _ => Connect(),
            new[] { "open" }),
        new("disconnect", "Setup", "disconnect", "Close the connection", _ => Disconnect(), new[] { "close" }),
        new("verbose", "Setup", "verbose [on|off]", "Show raw FTP commands", CmdVerbose, Array.Empty<string>()),

        // Sync
        new("scan", "Sync", "scan", "Compare local and server, build the change list", _ => CmdScan(),
            new[] { "refresh", "diff" }),
        new("sync", "Sync", "sync [-y]", "Scan, then upload and download every change", CmdSync, Array.Empty<string>(),
            "  'review' items (different size, unclear direction) are skipped.\n  -y skips the confirmation."),
        new("upload", "Sync", "upload [what] [-y]", "Upload added items, or exactly <what>", a => CmdTransfer(true, a),
            new[] { "push" }, """
  upload             upload added [x] items (if none, offers every change not dropped)
  upload 1-3 *.css   upload exactly those items (dropped [-] items are skipped)
  upload 8h          fresh scan, then upload files changed in the last 8 hours
                     that are newer than the server (same as: upload recent 8)
  -y                 don't ask for confirmation
"""),
        new("download", "Sync", "download [what] [-y]", "Download added items, or exactly <what>",
            a => CmdTransfer(false, a), new[] { "pull" }),

        // Change list
        new("list", "Change list", "list [what]", "Show the change list (optionally filtered)", CmdList,
            new[] { "changes" }),
        new("add", "Change list", "add <what> [--all]", "Mark items [x] to transfer", CmdAdd,
            new[] { "select", "pattern", "match", "find", "search" }, """
  add 3   add up   add *.css   add 8h   add all
  Patterns and time windows also search the local folder, so files that aren't
  in the list yet work too - even without a scan:  add *.html  then  upload
  --all              also find local files that match ignore rules
  add none           clear every [x] (nothing becomes dropped)
  add invert         swap [x] and [ ] (dropped [-] items stay dropped)
"""),
        new("drop", "Change list", "drop <what>", "Mark items [-] so they won't be transferred", CmdDrop,
            new[] { "remove", "deselect", "unselect" }, """
  drop 4   drop *.log   drop 2h   drop all
  Dropped items stay in the list with their numbers, and every transfer skips
  them - including the 'upload all changes' offer - until you add them back.
  Nothing is deleted from disk or the server.
"""),

        // Ignore
        new("ignore", "Ignore rules", "ignore [pattern...|added]", "Add ignore rules, or list them", CmdIgnore,
            Array.Empty<string>(), """
  ignore             list rules (or ignore the added [x] items, if any)
  ignore *.log bin/** add rules for this folder
  ignore added       ignore the exact paths of added [x] items
"""),
        new("unignore", "Ignore rules", "unignore <pattern...>", "Remove ignore rules", CmdUnignore,
            Array.Empty<string>()),

        // Server files
        new("ls", "Server files", "ls [-l] [path]", "List a server directory", a => CmdList(a, false),
            new[] { "dir" }),
        new("ll", "Server files", "ll [path]", "List with sizes and dates (same as ls -l)", a => CmdList(a, true),
            Array.Empty<string>()),
        new("pwd", "Server files", "pwd", "Show the server's working directory", _ => CmdPwd(), Array.Empty<string>()),
        new("get", "Server files", "get <remote> [local]", "Download one file", CmdGet, Array.Empty<string>()),
        new("put", "Server files", "put <local> [remote]", "Upload one file", CmdPut, Array.Empty<string>()),
        new("mkdir", "Server files", "mkdir <path>", "Create a directory (and parents)", CmdMkdir, Array.Empty<string>()),
        new("rm", "Server files", "rm [-r] <path...>", "Delete files, or a directory with -r", CmdRm,
            new[] { "del" }, "  rm -r <dir> deletes a directory and everything in it (asks first)."),
        new("rmdir", "Server files", "rmdir [-r] <path>", "Delete a directory", CmdRmdir, Array.Empty<string>()),
        new("rename", "Server files", "rename <from> <to>", "Rename or move on the server", CmdRename,
            new[] { "mv" }),

        new("help", "Other", "help [command]", "Show help", CmdHelp, new[] { "?", "h" }),
        new("quit", "Other", "quit", "Leave ShyFTP", _ => { }, new[] { "exit", "q" }),
    };

    public int Run()
    {
        PrintBanner();
        ConsoleUtil.Muted($"Config: {_store.Path}");
        ConsoleUtil.Muted(_config.Servers.Count == 0
            ? "Get started: 'map' links this folder to a server. Type 'help' for all commands."
            : "Type 'help' for commands, 'help <command>' for details.");

        while (true)
        {
            Console.Write(BuildPrompt());
            var line = Console.ReadLine();
            if (line == null)
                break;

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
                continue;

            var name = tokens[0].ToLowerInvariant();
            var args = tokens.Skip(1).ToList();

            try
            {
                if (FindCommand(name)?.Name == "quit")
                    break;

                Dispatch(name, args);
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

    private void Dispatch(string name, List<string> args)
    {
        // Older hyphenated forms: server-add, server-edit, server-remove.
        if (name.StartsWith("server-"))
        {
            args.Insert(0, name["server-".Length..]);
            name = "server";
        }
        else if (name == "ignored")
        {
            args.Insert(0, "list");
            name = "ignore";
        }

        var command = FindCommand(name);
        if (command == null)
        {
            var suggestion = Suggest(name);
            ConsoleUtil.Error(suggestion != null
                ? $"Unknown command '{name}'. Did you mean '{suggestion}'?"
                : $"Unknown command '{name}'. Type 'help'.");
            return;
        }

        if (args.Contains("--help"))
        {
            PrintCommandHelp(command);
            return;
        }

        command.Run(args);
    }

    private Command? FindCommand(string name) =>
        _commands.FirstOrDefault(c => c.Name == name || c.Aliases.Contains(name));

    private string? Suggest(string name)
    {
        var names = _commands.SelectMany(c => c.Aliases.Prepend(c.Name)).Where(n => n.Length > 1);
        var best = names
            .Select(n => (Name: n, Distance: n.StartsWith(name) ? 0 : EditDistance(name, n)))
            .OrderBy(x => x.Distance)
            .FirstOrDefault();
        if (name.Length < 2 || best.Name == null || best.Distance > (name.Length <= 4 ? 1 : 2))
            return null;
        return FindCommand(best.Name)!.Name;
    }

    private static int EditDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    private void Usage(string name)
    {
        var command = FindCommand(name)!;
        ConsoleUtil.Error($"Usage: {command.Usage}   (see 'help {command.Name}')");
    }

    private static bool TakeFlag(List<string> args, params string[] flags) =>
        args.RemoveAll(a => flags.Contains(a, StringComparer.OrdinalIgnoreCase)) > 0;

    private static bool Confirm(string prompt, bool assumeYes) => assumeYes || ConsoleUtil.Confirm(prompt, false);

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
            ConsoleUtil.Error("This folder is not linked to a server yet. Run 'map'.");
            return false;
        }

        if (!_config.Servers.TryGetValue(_folder.Server, out var server))
        {
            ConsoleUtil.Error($"Server '{_folder.Server}' is not defined. Run 'server add {_folder.Server}'.");
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
            if (server.Security != FtpSecurity.None && !_client.IsDataProtected)
                ConsoleUtil.Warn("Warning: the server refused PROT P - data transfers are NOT TLS-protected.");
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

    private string ResolveLocal(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));

    private void PrintStatus()
    {
        ConsoleUtil.Accent("Status");
        Console.WriteLine($"  Local folder : {_root}");
        Console.WriteLine($"  Mapping      : {(_folder == null ? "(none - run 'map')" : _folder.Server)}");
        Console.WriteLine($"  Remote path  : {(_folder?.RemotePath ?? "(n/a)")}");
        Console.WriteLine($"  Recursive    : {_folder?.Recursive ?? false}");
        Console.WriteLine($"  Connection   : {(_client is { IsConnected: true } ? _server?.Host : "(not connected)")}");
        Console.WriteLine($"  Ignore rules : {IgnorePatterns().Count}");
        Console.WriteLine($"  Change list  : {_items.Count} item(s), {_items.Count(i => i.Selected)} added, {_items.Count(i => i.Dropped)} dropped");
    }

    private void CmdFolder(List<string> args)
    {
        if (args.Count == 0)
        {
            ConsoleUtil.Info($"Current folder: {_root}");
            return;
        }

        var target = ResolveLocal(args[0]);
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

    private void CmdVerbose(List<string> args)
    {
        if (args.Count > 0)
        {
            if (!TryParseBool(args[0], out _verbose))
            {
                Usage("verbose");
                return;
            }
        }
        else
        {
            _verbose = !_verbose;
        }

        ConsoleUtil.Info($"Verbose: {(_verbose ? "on" : "off")}");
    }

    // ---------------------------------------------------------------- servers and mapping

    private void CmdMap(List<string> args)
    {
        string name;
        if (args.Count > 0)
        {
            name = args[0];
        }
        else
        {
            if (_config.Servers.Count > 0)
                CmdServers();
            name = Prompt(_config.Servers.Count > 0 ? "Server name (existing or new)" : "Name for the new server");
            if (string.IsNullOrWhiteSpace(name))
                return;
        }

        if (!_config.Servers.ContainsKey(name))
        {
            if (!ConsoleUtil.Confirm($"Server '{name}' doesn't exist yet. Add it now?", true))
                return;
            if (!ServerAdd(new List<string> { name }))
                return;
        }

        var remote = args.Count > 1 ? args[1] : args.Count == 1 ? "/" : Prompt("Remote path", "/");
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
        ConsoleUtil.Muted("Next: 'scan' to see what's changed, or 'sync' to bring both sides up to date.");
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

    private void CmdServer(List<string> args)
    {
        if (args.Count == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            CmdServers();
            return;
        }

        var sub = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToList();
        switch (sub)
        {
            case "add" or "new": ServerAdd(rest); break;
            case "set" or "edit": ServerSet(rest); break;
            case "show" or "info": ServerShow(rest); break;
            case "remove" or "rm" or "delete": ServerRemove(rest); break;
            default:
                if (_config.Servers.ContainsKey(args[0]))
                    ServerShow(args);
                else
                    Usage("server");
                break;
        }
    }

    private void CmdServers()
    {
        if (_config.Servers.Count == 0)
        {
            ConsoleUtil.Warn("No servers defined. Use 'server add' (or just 'map').");
            return;
        }

        ConsoleUtil.Accent($"{"Name",-20} {"Host",-30} {"Port",5} {"Security",-12} Folders");
        foreach (var (name, server) in _config.Servers.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var folders = _config.Folders.Count(f => string.Equals(f.Server, name, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"{name,-20} {server.Host,-30} {server.Port,5} {server.Security,-12} {folders}");
        }
    }

    private bool ServerAdd(List<string> args)
    {
        var name = args.Count > 0 ? args[0] : Prompt("Server name");
        if (string.IsNullOrWhiteSpace(name))
        {
            ConsoleUtil.Error("A server name is required.");
            return false;
        }

        if (_config.Servers.ContainsKey(name))
        {
            ConsoleUtil.Error($"Server '{name}' already exists. Use 'server set {name} <field> <value>'.");
            return false;
        }

        var host = args.Count > 1 ? args[1] : Prompt("Host");
        if (string.IsNullOrWhiteSpace(host))
        {
            ConsoleUtil.Error("A host is required.");
            return false;
        }

        var portText = args.Count > 2 ? args[2] : Prompt("Port", "21");
        var username = args.Count > 3 ? args[3] : Prompt("Username", "anonymous");
        var password = args.Count > 4 ? args[4] : ConsoleUtil.ReadSecret("Password");

        if (!int.TryParse(portText, out var port))
            port = 21;

        var securityText = Prompt("Security (none/explicit/implicit)", "none");
        TryParseSecurity(securityText, out var security);

        var validate = security != FtpSecurity.None && ConsoleUtil.Confirm("Validate TLS certificate?", true);

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
        return true;
    }

    private void ServerSet(List<string> args)
    {
        if (args.Count < 2)
        {
            Usage("server");
            return;
        }

        var name = args[0];
        if (!_config.Servers.TryGetValue(name, out var server))
        {
            ConsoleUtil.Error($"Server '{name}' not found.");
            return;
        }

        var key = args[1].ToLowerInvariant();
        string value;
        if (args.Count > 2)
            value = args[2];
        else if (key is "pass" or "password")
            value = ConsoleUtil.ReadSecret("New password");
        else
            value = Prompt($"New value for {key}");

        bool ok;
        switch (key)
        {
            case "host": server.Host = value; ok = value.Length > 0; break;
            case "port": ok = int.TryParse(value, out var port) && port is > 0 and < 65536; if (ok) server.Port = port; break;
            case "user" or "username": server.Username = value; ok = true; break;
            case "pass" or "password": server.Password = value; ok = true; break;
            case "security": ok = TryParseSecurity(value, out var sec); if (ok) server.Security = sec; break;
            case "passive": ok = TryParseBool(value, out var passive); if (ok) server.Passive = passive; break;
            case "validate": ok = TryParseBool(value, out var validate); if (ok) server.ValidateCertificate = validate; break;
            case "utf8": ok = TryParseBool(value, out var utf8); if (ok) server.Utf8 = utf8; break;
            case "pasv-address" or "usepasvaddress": ok = TryParseBool(value, out var pasv); if (ok) server.UsePasvAddress = pasv; break;
            case "timeout": ok = int.TryParse(value, out var t) && t > 0; if (ok) server.TimeoutSeconds = t; break;
            default:
                ConsoleUtil.Error($"Unknown field '{key}'. See 'help server'.");
                return;
        }

        if (!ok)
        {
            ConsoleUtil.Error($"'{value}' is not a valid value for {key}.");
            return;
        }

        _store.Save(_config);
        ConsoleUtil.Success($"Updated {name}.{key}.");
    }

    private void ServerShow(List<string> args)
    {
        if (args.Count == 0)
        {
            Usage("server");
            return;
        }

        if (!_config.Servers.TryGetValue(args[0], out var s))
        {
            ConsoleUtil.Error($"Server '{args[0]}' not found.");
            return;
        }

        ConsoleUtil.Accent(args[0]);
        Console.WriteLine($"  host         : {s.Host}");
        Console.WriteLine($"  port         : {s.Port}");
        Console.WriteLine($"  user         : {s.Username}");
        Console.WriteLine($"  pass         : {(s.Password.Length > 0 ? "********" : "(empty)")}");
        Console.WriteLine($"  security     : {s.Security}");
        Console.WriteLine($"  validate     : {OnOff(s.ValidateCertificate)}");
        Console.WriteLine($"  passive      : {OnOff(s.Passive)}");
        Console.WriteLine($"  utf8         : {OnOff(s.Utf8)}");
        Console.WriteLine($"  pasv-address : {OnOff(s.UsePasvAddress)}");
        Console.WriteLine($"  timeout      : {s.TimeoutSeconds}s");
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    private void ServerRemove(List<string> args)
    {
        if (args.Count == 0)
        {
            Usage("server");
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

    private static bool TryParseBool(string text, out bool value)
    {
        switch (text.ToLowerInvariant())
        {
            case "on" or "true" or "yes" or "y" or "1": value = true; return true;
            case "off" or "false" or "no" or "n" or "0": value = false; return true;
            default: value = false; return false;
        }
    }

    private static bool TryParseSecurity(string text, out FtpSecurity security)
    {
        switch (text.ToLowerInvariant())
        {
            case "implicit" or "implicit-tls" or "implicittls": security = FtpSecurity.ImplicitTls; return true;
            case "explicit" or "explicit-tls" or "explicittls" or "tls" or "ftps": security = FtpSecurity.ExplicitTls; return true;
            case "none" or "plain" or "ftp" or "": security = FtpSecurity.None; return true;
            default: security = FtpSecurity.None; return false;
        }
    }

    // ---------------------------------------------------------------- remote browsing

    private void CmdPwd()
    {
        if (!EnsureClient())
            return;
        ConsoleUtil.Info("Remote directory: " + _client!.PrintWorkingDirectory());
    }

    private void CmdList(List<string> args, bool detailed)
    {
        detailed |= TakeFlag(args, "-l");
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

    // ---------------------------------------------------------------- change list

    private bool ScanAll()
    {
        if (_folder == null)
        {
            ConsoleUtil.Error("This folder is not linked to a server yet. Run 'map'.");
            return false;
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
        return true;
    }

    private void CmdScan()
    {
        if (!EnsureClient())
            return;

        if (!ScanAll())
            return;

        var uploads = _items.Count(i => i.State is SyncState.UploadNew or SyncState.UploadChanged);
        var downloads = _items.Count(i => i.State is SyncState.DownloadNew or SyncState.DownloadChanged);
        var diffs = _items.Count(i => i.State == SyncState.Different);

        ConsoleUtil.Success($"Compare complete: {_items.Count} entries - {uploads} upload, {downloads} download, {diffs} review.");
        PrintItems();
    }

    private void CmdList(List<string> args)
    {
        if (args.Count == 0 || _items.Count == 0)
        {
            PrintItems();
            return;
        }

        var hits = Resolve(args);
        if (hits.Count == 0)
            return;

        PrintHeader();
        foreach (var i in hits)
            WriteItemLine(i + 1, _items[i]);
        ConsoleUtil.Muted($"{hits.Count} of {_items.Count} item(s) shown.");
    }

    private void PrintItems()
    {
        if (_items.Count == 0)
        {
            ConsoleUtil.Muted("(change list is empty - run 'scan', or 'add <pattern>')");
            return;
        }

        PrintHeader();
        for (var i = 0; i < _items.Count; i++)
            WriteItemLine(i + 1, _items[i]);
    }

    private static void PrintHeader() => ConsoleUtil.Accent($"{"#",5}       {"Suggested",-18} {"Size",10}  Path");

    private void WriteItemLine(int index, SyncItem item)
    {
        var marker = item.Selected ? ">" : " ";
        var box = item.Selected ? "[x]" : item.Dropped ? "[-]" : "[ ]";
        var color = item.Dropped ? ConsoleColor.DarkGray : item.State switch
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

        Console.WriteLine($"{marker}{index,4}  {box}  {item.SuggestedAction,-18} {size,10}  {item.RelativePath}");

        try
        {
            Console.ForegroundColor = previous;
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Resolves &lt;what&gt; tokens against the change list, printing any warnings.</summary>
    private List<int> Resolve(IEnumerable<string> tokens)
    {
        var warnings = new List<string>();
        var hits = ItemSelector.Resolve(_items, tokens, DateTime.UtcNow, warnings);
        foreach (var warning in warnings)
            ConsoleUtil.Warn(warning);
        return hits;
    }

    private static void Mark(SyncItem item, bool add)
    {
        item.Selected = add;
        item.Dropped = !add;
    }

    private void CmdAdd(List<string> args)
    {
        var includeIgnored = TakeFlag(args, "--all");
        var tokens = ItemSelector.Normalize(args);
        if (tokens.Count == 0)
        {
            Usage("add");
            return;
        }

        var now = DateTime.UtcNow;
        Dictionary<string, EntryInfo>? local = null;
        var fromDisk = 0;

        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "none":
                    _items.ForEach(i => i.Selected = false);
                    continue;
                case "invert":
                    foreach (var item in _items.Where(i => !i.Dropped))
                        item.Selected = !item.Selected;
                    continue;
            }

            var isRange = ItemSelector.TryParseRange(token, out _, out _);
            if (_items.Count == 0 && (isRange || ItemSelector.IsKeyword(token) && !token.Equals("all", StringComparison.OrdinalIgnoreCase)))
            {
                ConsoleUtil.Warn($"'{token}' picks from the list, which is empty - run 'scan' first.");
                continue;
            }

            // Numbers print their own out-of-range warning; other tokens may still match on disk.
            var hits = isRange ? Resolve(new[] { token }) : ItemSelector.Resolve(_items, new[] { token }, now);
            foreach (var i in hits)
                Mark(_items[i], true);
            var matched = hits.Count > 0;

            // Patterns, time windows and 'all' also search the local folder for files not yet listed.
            var searchesDisk = !isRange && (!ItemSelector.IsKeyword(token) || token.Equals("all", StringComparison.OrdinalIgnoreCase));
            if (searchesDisk)
            {
                local ??= ScanLocalForAdd(includeIgnored);
                foreach (var (relative, info) in local.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!ItemSelector.MatchesLocal(token, relative, info.ModifiedUtc, now))
                        continue;

                    matched = true;
                    var item = FindItem(relative);
                    if (item == null)
                    {
                        item = CreateLocalItem(relative, info);
                        _items.Add(item);
                        fromDisk++;
                    }

                    Mark(item, true);
                }
            }

            if (!matched && !isRange)
                ConsoleUtil.Warn($"Nothing matched '{token}'.{(searchesDisk && !includeIgnored ? " (add --all to include ignored files)" : "")}");
        }

        if (_items.Count == 0)
            return;

        PrintItems();
        var found = fromDisk > 0 ? $", {fromDisk} found on disk" : "";
        ConsoleUtil.Success($"{_items.Count(i => i.Selected)} item(s) marked [x]{found}. 'upload' / 'download' to transfer them.");
    }

    private Dictionary<string, EntryInfo> ScanLocalForAdd(bool includeIgnored)
    {
        ConsoleUtil.Info("Searching local files...");
        IEnumerable<string> ignore = includeIgnored ? Array.Empty<string>() : IgnorePatterns();
        return SyncEngine.ScanLocal(_root, _folder?.Recursive ?? true, ignore);
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

    private void CmdDrop(List<string> args)
    {
        if (args.Count == 0)
        {
            Usage("drop");
            return;
        }

        if (_items.Count == 0)
        {
            PrintItems();
            return;
        }

        var hits = Resolve(args);
        foreach (var i in hits)
            Mark(_items[i], false);

        PrintItems();
        ConsoleUtil.Success($"Dropped {hits.Count} item(s) - shown as [-], they won't be transferred until you add them back.");
    }

    private SyncItem? FindItem(string relative) =>
        _items.FirstOrDefault(i => string.Equals(i.RelativePath, relative, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- transfers

    private void CmdTransfer(bool upload, List<string> args)
    {
        var yes = TakeFlag(args, "-y", "--yes");
        var tokens = ItemSelector.Normalize(args);

        if (upload && tokens.Count == 1 && ItemSelector.TryParseDuration(tokens[0], out var window))
        {
            UploadRecent(window, tokens[0], yes);
            return;
        }

        if (!EnsureClient())
            return;

        var label = upload ? "upload" : "download";
        bool CanTransfer(SyncItem i) => upload ? i.LocalExists : i.RemoteExists;

        List<SyncItem> targets;
        if (tokens.Count > 0)
        {
            if (_items.Count == 0)
            {
                ConsoleUtil.Error($"The change list is empty. Run 'scan'{(upload ? " or 'add <pattern>'" : "")} first.");
                return;
            }

            targets = Resolve(tokens).Select(i => _items[i]).Where(CanTransfer).ToList();
            var dropped = targets.RemoveAll(i => i.Dropped);
            if (dropped > 0)
                ConsoleUtil.Warn($"Skipping {dropped} dropped [-] item(s) - 'add' them back to include them.");
        }
        else
        {
            targets = _items.Where(i => i.Selected && CanTransfer(i)).ToList();
            if (targets.Count == 0)
            {
                var changes = _items
                    .Where(i => !i.Dropped && (upload
                        ? i.State is SyncState.UploadNew or SyncState.UploadChanged
                        : i.State is SyncState.DownloadNew or SyncState.DownloadChanged))
                    .ToList();

                if (changes.Count == 0)
                {
                    ConsoleUtil.Error(_items.Count == 0
                        ? $"Nothing to {label}. Run 'scan'{(upload ? " or 'add <pattern>'" : "")} first."
                        : $"Nothing added [x] and no {label} changes left that aren't dropped.");
                    return;
                }

                if (!Confirm($"Nothing added [x]. {char.ToUpper(label[0])}{label[1..]} all {changes.Count} changed file(s)?", yes))
                {
                    ConsoleUtil.Muted("Cancelled.");
                    return;
                }

                yes = true;
                targets = changes;
            }
        }

        if (targets.Count == 0)
        {
            ConsoleUtil.Warn($"None of those items can be {label}ed.");
            return;
        }

        ConsoleUtil.Info($"About to {label} {targets.Count} file(s).");
        if (!Confirm("Proceed?", yes))
        {
            ConsoleUtil.Muted("Cancelled.");
            return;
        }

        if (upload)
            ApplyUploads(targets);
        else
            ApplyDownloads(targets);
    }

    private void UploadRecent(TimeSpan window, string label, bool yes)
    {
        if (!EnsureClient())
            return;

        if (!ScanAll())
            return;

        var targets = SyncEngine.RecentUploads(_items, DateTime.UtcNow - window);

        if (targets.Count == 0)
        {
            ConsoleUtil.Warn($"No files changed in the last {label} are newer than the server.");
            return;
        }

        ConsoleUtil.Info($"Files to upload ({targets.Count}, changed in the last {label} and newer than the server):");
        foreach (var item in targets)
            Console.WriteLine($"  {item.SuggestedAction,-18} {item.RelativePath}");

        if (!Confirm($"Upload all {targets.Count} file(s)?", yes))
        {
            ConsoleUtil.Muted("Cancelled.");
            return;
        }

        ApplyUploads(targets);
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

    private void CmdSync(List<string> args)
    {
        var yes = TakeFlag(args, "-y", "--yes");
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
            ConsoleUtil.Warn($"{review.Count} item(s) marked 'review' will be skipped - handle them with 'upload review' or 'download review'.");

        if (!Confirm("Apply this sync?", yes))
        {
            ConsoleUtil.Muted("Cancelled.");
            return;
        }

        if (uploads.Count > 0)
            ApplyUploads(uploads);
        if (downloads.Count > 0)
            ApplyDownloads(downloads);
    }

    // ---------------------------------------------------------------- ignore rules

    private void AddIgnorePattern(string pattern)
    {
        if (_folder == null)
            return;

        if (!_folder.Ignore.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            _folder.Ignore.Add(pattern);
    }

    private void CmdIgnore(List<string> args)
    {
        var selected = _items.Where(i => i.Selected).ToList();
        var ignoreSelected = args.Count == 1 && args[0].ToLowerInvariant() is "added" or "selected";

        if ((args.Count == 0 && selected.Count == 0) || (args.Count == 1 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase)))
        {
            CmdIgnored();
            return;
        }

        if (_folder == null)
        {
            ConsoleUtil.Error("This folder is not linked to a server yet. Run 'map' before adding ignore rules.");
            return;
        }

        if (args.Count == 0 || ignoreSelected)
        {
            if (selected.Count == 0)
            {
                ConsoleUtil.Warn("Nothing is added [x].");
                return;
            }

            foreach (var item in selected)
            {
                AddIgnorePattern(item.RelativePath);
                _items.Remove(item);
            }

            _store.Save(_config);
            PrintItems();
            ConsoleUtil.Success($"Added {selected.Count} path(s) to the ignore list.");
            return;
        }

        foreach (var pattern in args)
            AddIgnorePattern(pattern);

        var before = _items.Count;
        _items.RemoveAll(i => GlobMatcher.AnyMatches(args, i.RelativePath));
        _store.Save(_config);
        PrintItems();
        ConsoleUtil.Success($"Added {args.Count} pattern(s) to the ignore list ({before - _items.Count} item(s) hidden).");
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
            Usage("unignore");
            return;
        }

        var removed = _folder.Ignore.RemoveAll(p =>
            args.Any(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase)));

        _store.Save(_config);
        if (removed > 0)
            ConsoleUtil.Success($"Removed {removed} ignore rule(s).");
        else if (_config.GlobalIgnore.Any(g => args.Contains(g, StringComparer.OrdinalIgnoreCase)))
            ConsoleUtil.Warn("That is a global rule - edit 'globalIgnore' in the config file to remove it.");
        else
            ConsoleUtil.Warn("No matching ignore rule. Type 'ignore' to list them.");
    }

    private void CmdIgnored()
    {
        if (IgnorePatterns().Count == 0)
        {
            ConsoleUtil.Muted("(no ignore rules - add one with 'ignore <pattern>')");
            return;
        }

        ConsoleUtil.Accent("Ignore rules:");
        foreach (var pattern in _config.GlobalIgnore)
            Console.WriteLine($"  {pattern,-30} (global)");
        foreach (var pattern in _folder?.Ignore ?? new List<string>())
            Console.WriteLine($"  {pattern}");
    }

    // ---------------------------------------------------------------- single-file remote operations

    private void CmdMkdir(List<string> args)
    {
        if (args.Count == 0)
        {
            Usage("mkdir");
            return;
        }

        if (!EnsureClient())
            return;

        _client!.EnsureDirectory(ResolveRemote(args[0]));
        ConsoleUtil.Success("Directory created (or already exists).");
    }

    private void CmdRmdir(List<string> args)
    {
        var recursive = TakeFlag(args, "-r", "-rf", "--recursive");
        if (args.Count == 0)
        {
            Usage("rmdir");
            return;
        }

        if (!EnsureClient())
            return;

        var path = ResolveRemote(args[0]);
        if (recursive && !ConsoleUtil.Confirm($"Recursively delete remote '{path}'?", false))
            return;

        _client!.DeleteDirectory(path, recursive);
        ConsoleUtil.Success("Directory removed.");
    }

    private void CmdRm(List<string> args)
    {
        if (args.Any(a => a is "-r" or "-rf" or "--recursive"))
        {
            CmdRmdir(args);
            return;
        }

        if (args.Count == 0)
        {
            Usage("rm");
            return;
        }

        if (!EnsureClient())
            return;

        foreach (var arg in args)
        {
            _client!.DeleteFile(ResolveRemote(arg));
            ConsoleUtil.Success($"Removed {arg}.");
        }
    }

    private void CmdRename(List<string> args)
    {
        if (args.Count < 2)
        {
            Usage("rename");
            return;
        }

        if (!EnsureClient())
            return;

        _client!.Rename(ResolveRemote(args[0]), ResolveRemote(args[1]));
        ConsoleUtil.Success("Renamed.");
    }

    private void CmdGet(List<string> args)
    {
        if (args.Count == 0)
        {
            Usage("get");
            return;
        }

        if (!EnsureClient())
            return;

        var remote = ResolveRemote(args[0]);
        var local = args.Count > 1
            ? ResolveLocal(args[1])
            : Path.Combine(_root, PathUtil.RemoteFileName(remote));

        ConsoleUtil.Info($"Downloading {remote} -> {local}");
        _client!.DownloadTo(remote, local, (current, total) => ShowProgress(PathUtil.RemoteFileName(remote), current, total));
        ClearProgress();
        ConsoleUtil.Success("Downloaded.");
    }

    private void CmdPut(List<string> args)
    {
        if (args.Count == 0)
        {
            Usage("put");
            return;
        }

        var local = ResolveLocal(args[0]);
        if (!File.Exists(local))
        {
            ConsoleUtil.Error($"Local file not found: {local}");
            return;
        }

        if (!EnsureClient())
            return;

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

    // ---------------------------------------------------------------- help

    private void CmdHelp(List<string> args)
    {
        if (args.Count > 0)
        {
            var name = args[0].ToLowerInvariant();
            var command = FindCommand(name.StartsWith("server-") ? "server" : name);
            if (command != null)
            {
                PrintCommandHelp(command);
                return;
            }

            if (name is "what" or "selectors" or "select-syntax")
            {
                Console.WriteLine(WhatHelp);
                return;
            }

            var suggestion = Suggest(name);
            ConsoleUtil.Error($"No help for '{name}'.{(suggestion != null ? $" Did you mean '{suggestion}'?" : "")}");
            return;
        }

        ConsoleUtil.Accent("Typical workflow:  map  ->  scan  ->  add / drop  ->  upload   (or just: sync)");
        foreach (var group in _commands.GroupBy(c => c.Group))
        {
            Console.WriteLine();
            ConsoleUtil.Accent(group.Key);
            foreach (var command in group)
                Console.WriteLine($"  {command.Usage,-34} {command.Summary}");
        }

        Console.WriteLine();
        Console.WriteLine(WhatHelp);
        ConsoleUtil.Muted("'help <command>' shows details and examples. Older command names still work.");
    }

    private static void PrintCommandHelp(Command command)
    {
        ConsoleUtil.Accent(command.Usage);
        Console.WriteLine("  " + command.Summary);
        if (command.Details != null)
            Console.WriteLine(command.Details.TrimEnd());
        if (command.Usage.Contains("what"))
            Console.WriteLine(WhatHelp.TrimEnd());
        if (command.Aliases.Length > 0)
            ConsoleUtil.Muted("  Also: " + string.Join(", ", command.Aliases));
    }

    public void Dispose() => Disconnect();
}
