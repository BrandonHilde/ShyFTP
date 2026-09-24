using ShyFtp.Util;

namespace ShyFTP.Tests;

public class PathUtilTests
{
    [Theory]
    [InlineData("", "foo", "foo")]
    [InlineData("", "", "")]
    [InlineData("/", "foo", "/foo")]
    [InlineData("/", "", "/")]
    [InlineData("/foo", "bar", "/foo/bar")]
    [InlineData("/foo/", "bar", "/foo/bar")]
    [InlineData("/foo", "", "/foo")]
    [InlineData("/foo", "/bar", "/foo/bar")]
    [InlineData("sub", "a/b", "sub/a/b")]
    [InlineData("", "a/b", "a/b")]
    public void JoinRemote_joins(string basePath, string relative, string expected)
    {
        Assert.Equal(expected, PathUtil.JoinRemote(basePath, relative));
    }

    [Theory]
    [InlineData("/a/b/c.txt", "c.txt")]
    [InlineData("/a/b/", "b")]
    [InlineData("c.txt", "c.txt")]
    [InlineData("/", "")]
    public void RemoteFileName_returns_last_segment(string path, string expected)
    {
        Assert.Equal(expected, PathUtil.RemoteFileName(path));
    }

    [Theory]
    [InlineData("/a/b/c.txt", "/a/b")]
    [InlineData("/a/b/", "/a")]
    [InlineData("sub/file.txt", "sub")]
    [InlineData("file.txt", "/")]
    [InlineData("/file.txt", "/")]
    public void RemoteDirectoryName_returns_parent(string path, string expected)
    {
        Assert.Equal(expected, PathUtil.RemoteDirectoryName(path));
    }

    [Theory]
    [InlineData("/base", "/base/sub/file.txt", "sub/file.txt")]
    [InlineData("/base", "/base", "")]
    [InlineData("", "/a/b", "a/b")]
    public void ToRemoteRelative_strips_base(string root, string full, string expected)
    {
        Assert.Equal(expected, PathUtil.ToRemoteRelative(full, root));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    public void FormatSize_formats(long bytes, string expected)
    {
        Assert.Equal(expected, PathUtil.FormatSize(bytes));
    }
}
