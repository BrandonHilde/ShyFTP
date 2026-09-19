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
- `find <text>` to search the local tree and multi-select matches, and
  `pattern <glob>` to select from the current change list.
- UTF-8 support and optional TLS certificate validation bypass.

## Requirements

- [.NET SDK 9.0](https://dotnet.microsoft.com/) or later.

## Build and run

```bash
dotnet build
dotnet run
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
by the `server-*`, `map`, and `ignore` commands, so you rarely need to edit it by
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
server-add myserver ftp.example.com
map myserver /public_html
connect
scan
select up
upload
```

## Command reference

### Folders and servers

| Command | Description |
| --- | --- |
| `folder [path]` / `cd [path]` | Show or change the current local folder |
| `map <server> [remotePath]` | Map this folder to a server and remote path |
| `unmap` | Remove this folder's mapping |
| `status` | Show mapping, connection, and change-list state |
| `servers` | List defined servers |
| `server-add <name> <host> [port] [user] [pass]` | Add a server (prompts for missing values) |
| `server-edit <name> <field> <value>` | Edit a server field |
| `server-remove <name>` | Delete a server |
| `connect` / `disconnect` | Open / close the FTP connection |
| `verbose` | Toggle raw FTP command logging |

Editable server fields: `host`, `port`, `user`, `pass`, `security`, `passive`,
`validate`, `utf8`.

### Remote operations

| Command | Description |
| --- | --- |
| `pwd` | Print the remote working directory |
| `ls [path]` / `ll [path]` | List a remote directory (names / detailed) |
| `mkdir <remotePath>` | Create a remote directory (recursively) |
| `rmdir [-r] <remotePath>` | Remove a remote directory (`-r` asks to recurse) |
| `rm <remotePath>` | Delete a remote file |
| `rename <from> <to>` | Rename a remote entry |
| `get <remote> [local]` | Download one file |
| `put <local> [remote]` | Upload one file |

### Sync and changing files

| Command | Description |
| --- | --- |
| `scan` / `refresh` / `diff` | Compare local vs remote and build the change list |
| `changes` / `list` | Show the change list |
| `select <sel...>` | Select items |
| `deselect <sel...>` | Deselect items |
| `upload` / `push [sel...]` | Upload selected items (or all upload candidates) |
| `download` / `pull [sel...]` | Download selected items (or all download candidates) |
| `add <pattern...>` | Add matching local files to the change list and select them |
| `remove <pattern...>` | Remove matching items from the change list |
| `sync` | Scan, then approve the whole upload/download plan |

`<sel...>` accepts a comma-separated list of one-based indices, ranges, or
keywords:

```
1,3,5-7        specific items and ranges
all           everything
none          clear the selection
up            upload candidates
down          download candidates
new           newly added on either side
changed       modified on either side (and review items)
invert        toggle the selection
```

The change list uses these suggested actions:

| Action | Meaning |
| --- | --- |
| `upload (new)` | File exists locally only |
| `upload (changed)` | Exists on both sides, local is newer |
| `download (new)` | File exists remotely only |
| `download (changed)` | Exists on both sides, remote is newer |
| `review` | Different size but timestamps don't indicate a direction |
| `unchanged` | Identical (for example, added to the list by `add`) |

### Building the upload list (`add` / `remove`)

`add` and `remove` manage what is in the list selected for upload. `add` searches
the current local folder (respecting the folder's recursive setting), adds
matching files to the change list, and selects them; `remove` pulls matching
items back out so they will not be uploaded. Both skip ignored files unless you
pass `--all`.

| Command | Description |
| --- | --- |
| `add <pattern...> [--all]` | Add local files whose path matches any pattern (substring or glob) |
| `add recent <hours> [--all]` | Add local files modified within the last N hours |
| `add all [--all]` | Add every local file |
| `remove <pattern...>` | Remove matching items from the upload list |
| `remove recent <hours>` | Remove items modified within the last N hours |
| `remove selected` | Remove every currently selected item |
| `remove all` | Empty the upload list |
| `remove <n,n,a-b>` | Remove items by index or range |

Examples:

```
add .txt            # every local file whose path contains ".txt"
add *.html *.css    # glob patterns
add recent 8.4      # files changed in the last 8.4 hours
remove recent 8.4   # drop those again
remove .log         # drop matching items from the list
upload              # upload whatever remains in the list
```

`find <pattern>` is an alias for `add <pattern>`.

### Ignoring files

| Command | Description |
| --- | --- |
| `pattern <glob>` | Select change-list items matching a glob |
| `ignore <pattern...>` | Add ignore rule(s) |
| `ignore` | With items selected, ignore those exact paths |
| `unignore <pattern>` | Remove an ignore rule |
| `ignored` | Show all ignore rules |

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
  library). Protect the file with OS file permissions.
- **SFTP/SSH is not supported.** The engine implements FTP and FTPS only.
- FTP servers that only support `LIST` (no `MLSD`) lose some metadata; modified
  times on such servers may be approximate.
- Active mode (PORT) requires the server to be able to open a connection back to
  your machine; passive mode is the default and generally preferred.

## Project layout

```
Program.cs                 Entry point and argument parsing
Configuration/             Config model and JSON store
Ftp/FtpClient.cs           From-scratch FTP/FTPS engine
Ftp/FtpReply.cs            Reply types, exceptions, list item model
Sync/SyncEngine.cs         Local/remote scanning and change comparison
Sync/SyncItem.cs           Change-list item model
Cli/Shell.cs               Interactive console shell
Cli/ConsoleUtil.cs         Console helpers
Util/GlobMatcher.cs        Glob / substring matching
Util/PathUtil.cs           Path and size formatting helpers
```
