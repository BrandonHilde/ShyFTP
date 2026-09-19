using ShyFtp.Ftp;
using ShyFtp.Util;

namespace ShyFtp.Sync;

public readonly record struct EntryInfo(long Size, DateTime ModifiedUtc);

public static class SyncEngine
{
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    public static Dictionary<string, EntryInfo> ScanLocal(
        string localRoot,
        bool recursive,
        IEnumerable<string> ignorePatterns)
    {
        var ignore = ignorePatterns.ToList();
        var result = new Dictionary<string, EntryInfo>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(localRoot))
            return result;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        foreach (var file in Directory.EnumerateFiles(localRoot, "*", options))
        {
            var relative = PathUtil.ToRelative(localRoot, file);
            if (GlobMatcher.AnyMatches(ignore, relative))
                continue;

            var info = new FileInfo(file);
            result[relative] = new EntryInfo(info.Length, info.LastWriteTimeUtc);
        }

        return result;
    }

    public static Dictionary<string, EntryInfo> ScanRemote(
        FtpClient client,
        string baseRemotePath,
        bool recursive,
        Action<string>? progress = null)
    {
        const int maxDepth = 64;
        var result = new Dictionary<string, EntryInfo>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Absolute, string Relative, int Depth)>();
        queue.Enqueue((baseRemotePath, "", 0));

        while (queue.Count > 0)
        {
            var (absolute, relative, depth) = queue.Dequeue();
            if (!visited.Add(PathUtil.JoinRemote(absolute, ".")) || depth > maxDepth)
                continue;

            progress?.Invoke(relative.Length == 0 ? "/" : relative);

            List<FtpListItem> entries;
            try
            {
                entries = client.List(absolute);
            }
            catch (FtpException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Name is "." or "..")
                    continue;

                var childRelative = relative.Length == 0
                    ? entry.Name
                    : relative + "/" + entry.Name;

                if (entry.IsDirectory)
                {
                    if (recursive && depth < maxDepth)
                        queue.Enqueue((PathUtil.JoinRemote(absolute, entry.Name), childRelative, depth + 1));
                    continue;
                }

                if (entry.IsSymlink)
                    continue;

                var modified = entry.ModifiedUtc ?? DateTime.MinValue;
                result[childRelative] = new EntryInfo(entry.Size, modified);
            }
        }

        return result;
    }

    public static List<SyncItem> Compare(
        Dictionary<string, EntryInfo> local,
        Dictionary<string, EntryInfo> remote,
        string localRoot,
        string baseRemotePath)
    {
        var keys = new HashSet<string>(local.Keys, StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(remote.Keys);

        var items = new List<SyncItem>();
        foreach (var key in keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            local.TryGetValue(key, out var localInfo);
            remote.TryGetValue(key, out var remoteInfo);

            var hasLocal = local.ContainsKey(key);
            var hasRemote = remote.ContainsKey(key);

            var item = new SyncItem
            {
                RelativePath = key,
                LocalExists = hasLocal,
                RemoteExists = hasRemote,
                LocalSize = localInfo.Size,
                LocalTime = localInfo.ModifiedUtc,
                RemoteSize = remoteInfo.Size,
                RemoteTime = remoteInfo.ModifiedUtc,
                LocalFullPath = Path.Combine(localRoot, key.Replace('/', Path.DirectorySeparatorChar)),
                RemoteFullPath = PathUtil.JoinRemote(baseRemotePath, key)
            };

            if (hasLocal && !hasRemote)
            {
                item.State = SyncState.UploadNew;
            }
            else if (!hasLocal && hasRemote)
            {
                item.State = SyncState.DownloadNew;
            }
            else
            {
                var sizeDiffers = localInfo.Size != remoteInfo.Size;
                var localNewer = localInfo.ModifiedUtc - remoteInfo.ModifiedUtc > TimeTolerance;
                var remoteNewer = remoteInfo.ModifiedUtc - localInfo.ModifiedUtc > TimeTolerance;

                if (sizeDiffers)
                {
                    if (remoteNewer && !localNewer)
                        item.State = SyncState.DownloadChanged;
                    else if (localNewer && !remoteNewer)
                        item.State = SyncState.UploadChanged;
                    else
                        item.State = SyncState.Different;
                }
                else if (localNewer)
                {
                    item.State = SyncState.UploadChanged;
                }
                else if (remoteNewer)
                {
                    item.State = SyncState.DownloadChanged;
                }
                else
                {
                    item.State = SyncState.Unchanged;
                }
            }

            items.Add(item);
        }

        return items;
    }
}