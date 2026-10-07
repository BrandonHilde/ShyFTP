# ShyFTP

A console FTP/FTPS client for C# (.NET 9) with a sync-oriented workflow.

ShyFTP remembers which FTP server belongs to which local folder, compares that
folder against the server, presents a numbered list of new/changed files for you
to approve, supports per-folder ignore rules, and lets you search for files by a
substring or glob (for example `.txt`) and then run operations such as upload or
"add to ignore" on the matches.

The FTP engine is written from scratch on top of `TcpClient` and `SslStream` —
there are no third-party packages.

## Features

- Plain FTP and FTPS (explicit `AUTH TLS` and implicit TLS).
- Passive mode (EPSV with PASV fallback) and active mode (PORT).
- Modern `MLSD` listings with `LIST` / `NLST` parsing fallback (Unix and MS-DOS
  formats).
- Binary transfers with progress, `SIZE` / `MDTM`, `MKD` / `RMD` / `DELE` /
  `RNFR`/`RNTO`.
- A single global JSON config mapping local folders to servers and remote paths.
- Bidirectional change detection (upload new/changed, download new/changed,
  review) with a selectable, approvable change list.
- Per-folder ignore rules, plus global rules.
- One `<what>` syntax (numbers, ranges, keywords, time windows, globs) shared
  by every change-list command, plus `help <command>` and typo suggestions.
- UTF-8 support and optional TLS certificate validation bypass.

## Requirements

- [.NET SDK 9.0](https://dotnet.microsoft.com/) or later.

## Build and run

```bash
dotnet build
dotnet run
```

Run the unit tests:

```bash
dotnet test
```

Live integration tests run automatically when `python` and `pyftpdlib` are
available (`pip install pyftpdlib`); they spin up a throwaway FTP server on
localhost and exercise the engine end-to-end. Without them those tests are
skipped. To run a server by hand:

```bash
python Tests/tools/ftp_server.py [root] [port]
```

To produce a standalone executable:

```bash
dotnet publish -c Release
```

The assembly is named `shyftp`:

```bash
./bin/Release/net9.0/shyftp --help
```

### Command line

```
shyftp [folder] [--config <path>]

  folder            Start in this local folder (defaults to the current directory)
  --config <path>   Use a specific config file
  -h, --help        Show help
```

The config path may also be set with the `SHYFTP_CONFIG` environment variable.

## Configuration

By default the config lives at:

- Windows: `%APPDATA%\ShyFTP\config.json`
- Other: `~/.config/ShyFTP/config.json`

It is a plain JSON file. It is created automatically on first run and is updated
by the `server`, `map`, and `ignore` commands, so you rarely need to edit it by
hand.

```json
{
  "version": 1,
  "servers": {
    "myserver": {
      "host": "ftp.example.com",
      "port": 21,
      "username": "alice",
      "password": "secret",
      "security": "None",
      "validateCertificate": true,
      "passive": true,
      "timeoutSeconds": 30,
      "utf8": true,
      "usePasvAddress": false
    }
  },
  "folders": [
    {
      "path": "C:\\work\\my-site",
      "server": "myserver",
      "remotePath": "/public_html",
      "recursive": true,
      "ignore": ["*.log", "bin/**", "obj/**", ".git/**"]
    }
  ],
  "globalIgnore": ["Thumbs.db", ".DS_Store"]
}
```

- `security` is one of `None`, `ExplicitTls`, or `ImplicitTls`. When implicit is
  selected and no port is given, port `990` is used.
- `usePasvAddress` forces the address advertised in the server's `PASV` reply to
  be used as-is. By default ShyFTP falls back to the control-connection host when
  the server advertises an unroutable/private address.
- A folder mapping applies to that folder and all subfolders. The most specific
  (longest) matching path wins.

## Quick start

```
map                 # link this folder to a server (walks you through adding one)
scan                # compare local and server
add up              # mark everything that changed locally
drop *.log          # ...except the logs
upload              # send what's marked
```

Or do it all at once with `sync`. Type `help` inside the shell for every
command, and `help <command>` (or `<command> --help`) for details and examples.
Mistyped commands get a "did you mean" suggestion.

## Choosing items: `<what>`

Every command that works on the change list - `list`, `add`, `drop`,
`upload`, `download` - accepts the same `<what>` syntax. Mix as many as you
like; they combine:

| You type | Picks |
| --- | --- |
| `3`, `1,4`, `5-9` | Item numbers from the list |
| `all` | Everything |
| `added`, `dropped` | Items marked `[x]` / `[-]` |
| `up`, `down` | Upload / download candidates |
| `new`, `changed`, `review`, `unchanged` | Items in that state |
| `30m`, `8h`, `2d`, `1w` | Changed locally within that time (`recent 8` also means `8h`) |
| anything else | A glob (`*.css`, `img/**`) or text found in the path (`.txt`) |

```
list up             # show only upload candidates
add *.css 2h        # mark CSS files plus anything changed in the last 2 hours
upload 1-3          # upload exactly items 1 to 3
drop *.log          # keep log files out of every transfer
download review     # pull the server's copy of the 'review' items
```

## Command reference

### Setup

| Command | Description |
| --- | --- |
| `server` | List servers |
| `server add [name] [host] [port] [user] [pass]` | Add a server (prompts for anything missing) |
| `server set <name> <field> [value]` | Change a field; prompts when the value is omitted (handy for `pass`) |
| `server show <name>` | Show a server's settings |
| `server remove <name>` | Delete a server |
| `map [server] [remotePath]` | Link this folder to a server; asks when omitted and offers to create unknown servers |
| `unmap` | Remove this folder's link |
| `cd [path]` | Show or change the local folder (relative to the current one) |
| `status` | Show folder, server, connection and list state |
| `connect` / `disconnect` | Open / close the connection (other commands connect automatically) |
| `verbose [on\|off]` | Show raw FTP commands |

`server set` fields: `host`, `port`, `user`, `pass`, `security`
(`none`/`explicit`/`implicit`), `passive`, `validate`, `utf8`, `pasv-address`
(`on`/`off`), and `timeout` (seconds). Invalid values are rejected.

### Sync

| Command | Description |
| --- | --- |
| `scan` | Compare local vs server and build the change list |
| `sync [-y]` | Scan, then upload and download every change (`review` items are skipped) |
| `upload [what] [-y]` | Upload the `[x]` items, or exactly `<what>` |
| `upload 8h` | Fresh scan, then upload files changed in the last 8 hours that are newer than the server |
| `download [what] [-y]` | Download the `[x]` items, or exactly `<what>` |

With no `<what>` and nothing marked `[x]`, `upload`/`download` offer to
transfer every change in that direction that isn't dropped. Dropped items are
skipped even when named in `<what>`. `-y` skips confirmation prompts.

### Change list

| Command | Description |
| --- | --- |
| `list [what]` | Show the change list, optionally filtered (item numbers are kept) |
| `add <what> [--all]` | Mark items `[x]` to transfer |
| `drop <what>` | Mark items `[-]` so no transfer touches them |
| `add none` | Clear every `[x]` (doesn't drop anything) |
| `add invert` | Swap `[x]` and `[ ]`; dropped items stay dropped |

Each item in the list is in one of three states:

| Mark | Meaning |
| --- | --- |
| `[x]` | Added: `upload` / `download` with no arguments transfers these |
| `[ ]` | Neutral: transferred only by the "all changes" offer or when named |
| `[-]` | Dropped: never transferred until you `add` it back |

Dropped items stay in the list with their numbers, so `add 4` brings one back.
Nothing is ever deleted from disk or the server by `add` or `drop`.

Patterns, time windows and `all` given to `add` also search the local folder,
so you don't need a scan to send a few files: `add *.html` then `upload`.
Files matching ignore rules are skipped unless you pass `--all`. Numbers and
keywords such as `up` only pick from an existing list.

Suggested actions shown in the list:

| Action | Meaning |
| --- | --- |
| `upload (new)` | File exists locally only |
| `upload (changed)` | Exists on both sides, local is newer |
| `download (new)` | File exists remotely only |
| `download (changed)` | Exists on both sides, remote is newer |
| `review` | Different size but timestamps don't indicate a direction |
| `unchanged` | Identical (for example, added to the list by `add`) |

### Ignore rules

| Command | Description |
| --- | --- |
| `ignore` | List rules (or, if items are marked `[x]`, ignore their exact paths) |
| `ignore list` | List rules |
| `ignore <pattern...>` | Add rules for this folder |
| `ignore added` | Ignore the exact paths of the `[x]` items |
| `unignore <pattern...>` | Remove rules |

### Server files

| Command | Description |
| --- | --- |
| `ls [-l] [path]` / `ll [path]` | List a server directory (names / details) |
| `pwd` | Print the server's working directory |
| `get <remote> [local]` | Download one file |
| `put <local> [remote]` | Upload one file |
| `mkdir <path>` | Create a directory (recursively) |
| `rm <path...>` | Delete files |
| `rm -r <dir>` / `rmdir [-r] <dir>` | Delete a directory (`-r` recurses, asks first) |
| `rename <from> <to>` | Rename or move |

Relative local paths are resolved against the shell's current folder; relative
remote paths against the folder's mapped remote path.

### Older command names

Everything from earlier versions still works: `server-add`, `server-edit`,
`server-remove`, `servers`, `folder`, `use`, `open`/`close`, `refresh`/`diff`,
`changes`, `select`/`pattern`/`match`/`find`/`search` (= `add`),
`deselect`/`unselect`/`remove` (= `drop`), `select none` (= `add none`),
`push`/`pull`, `ignored` (= `ignore list`), `dir`, `mv`, `ignore selected`, and
the `recent <hours>` form.

Behaviour changes from earlier versions:

- `upload <what>` / `download <what>` transfer exactly those items. Previously
  they added them to the current selection and transferred the whole selection.
- `drop` (old `remove`) marks items `[-]` instead of deleting them from the
  list, so numbers stay stable and items can be added back. Use `list up`,
  `list changed` etc. to shorten the display instead.

### Ignore pattern syntax

Patterns are matched against the path relative to the folder root. If a pattern
contains no wildcard it is treated as a case-insensitive substring match;
otherwise glob syntax applies:

- `*` matches within a path segment
- `?` matches a single character
- `**` matches across directory separators
- `[abc]` / `[!abc]` match character sets

Examples: `.txt`, `*.log`, `bin/**`, `.git/**`, `build/*.tmp`.

## Notes and limitations

- **Passwords are stored in plaintext** in the config file. This is a deliberate
  consequence of having no external dependencies (no DPAPI/secret-store
  library). Protect the file with OS file permissions. The `server add` password
  prompt does not echo to the terminal.
- **SFTP/SSH is not supported.** The engine implements FTP and FTPS only.
- FTP servers that only support `LIST` (no `MLSD`) lose some metadata; modified
  times on such servers may be approximate.
- Active mode (PORT) requires the server to be able to open a connection back to
  your machine; passive mode is the default and generally preferred.
- If the server refuses `PROT P`, ShyFTP falls back to a clear-text data channel
  and prints a warning after connecting.

## Project layout

```
Program.cs                 Entry point and argument parsing
Configuration/             Config model and JSON store
Ftp/FtpClient.cs           From-scratch FTP/FTPS engine
Ftp/FtpReply.cs            Reply types, exceptions, list item model
Sync/SyncEngine.cs         Local/remote scanning and change comparison
Sync/SyncItem.cs           Change-list item model
Cli/Shell.cs               Interactive console shell and command table
Cli/ItemSelector.cs        The shared <what> selection syntax
Cli/ConsoleUtil.cs         Console helpers
Util/GlobMatcher.cs        Glob / substring matching
Util/PathUtil.cs           Path and size formatting helpers
Tests/                     Unit + live integration tests (xUnit)
Tests/tools/ftp_server.py  Local pyftpdlib launcher for manual testing
```
