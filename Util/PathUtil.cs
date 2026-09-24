namespace ShyFtp.Util;

public static class PathUtil
{
    public static string ToRelative(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        return rel.Replace('\\', '/');
    }

    public static string ToRemoteRelative(string fullRemotePath, string baseRemotePath)
    {
        var full = fullRemotePath.Replace('\\', '/');
        var root = baseRemotePath.Replace('\\', '/').TrimEnd('/');
        if (root.Length == 0)
            return full.TrimStart('/');
        if (full.StartsWith(root + "/", StringComparison.Ordinal))
            return full[(root.Length + 1)..];
        if (string.Equals(full, root, StringComparison.Ordinal))
            return "";
        return full.TrimStart('/');
    }

    public static string JoinRemote(string basePath, string relative)
    {
        var b = (basePath ?? "").Replace('\\', '/');
        var r = (relative ?? "").Replace('\\', '/').TrimStart('/');

        if (r.Length == 0)
        {
            var trimmed = b.TrimEnd('/');
            return trimmed.Length == 0 ? (b.StartsWith('/') ? "/" : "") : trimmed;
        }

        if (b.Length == 0)
            return r;

        if (b == "/")
            return "/" + r;

        return b.TrimEnd('/') + "/" + r;
    }

    public static string RemoteDirectoryName(string remotePath)
    {
        var path = remotePath.Replace('\\', '/').TrimEnd('/');
        if (path.Length == 0)
            return "/";
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }

    public static string RemoteFileName(string remotePath)
    {
        var path = remotePath.Replace('\\', '/').TrimEnd('/');
        var idx = path.LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}