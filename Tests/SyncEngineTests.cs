using ShyFtp.Sync;

namespace ShyFTP.Tests;

public class SyncEngineTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Dictionary<string, EntryInfo> Map(params (string Key, long Size, DateTime Time)[] entries)
    {
        var d = new Dictionary<string, EntryInfo>(StringComparer.Ordinal);
        foreach (var (key, size, time) in entries)
            d[key] = new EntryInfo(size, time);
        return d;
    }

    [Fact]
    public void Local_only_is_UploadNew()
    {
        var items = SyncEngine.Compare(
            Map(("a.txt", 10, T0)),
            Map(),
            "/local", "/remote");

        var item = Assert.Single(items);
        Assert.Equal(SyncState.UploadNew, item.State);
        Assert.Equal("a.txt", item.RelativePath);
        Assert.True(item.LocalExists);
        Assert.False(item.RemoteExists);
    }

    [Fact]
    public void Remote_only_is_DownloadNew()
    {
        var items = SyncEngine.Compare(
            Map(),
            Map(("a.txt", 10, T0)),
            "/local", "/remote");

        var item = Assert.Single(items);
        Assert.Equal(SyncState.DownloadNew, item.State);
    }

    [Fact]
    public void Same_size_and_time_is_Unchanged()
    {
        var items = SyncEngine.Compare(
            Map(("a.txt", 10, T0)),
            Map(("a.txt", 10, T0)),
            "/local", "/remote");

        Assert.Equal(SyncState.Unchanged, Assert.Single(items).State);
    }

    [Fact]
    public void Same_size_local_newer_is_UploadChanged()
    {
        var items = SyncEngine.Compare(
            Map(("a.txt", 10, T0.AddMinutes(5))),
            Map(("a.txt", 10, T0)),
            "/local", "/remote");

        Assert.Equal(SyncState.UploadChanged, Assert.Single(items).State);
    }

    [Fact]
    public void Same_size_remote_newer_is_DownloadChanged()
    {
        var items = SyncEngine.Compare(
            Map(("a.txt", 10, T0)),
            Map(("a.txt", 10, T0.AddMinutes(5))),
            "/local", "/remote");

        Assert.Equal(SyncState.DownloadChanged, Assert.Single(items).State);
    }

    [Fact]
    public void Size_differs_without_clear_winner_is_Different()
    {
        var items = SyncEngine.Compare(
            Map(("a.txt", 10, T0)),
            Map(("a.txt", 20, T0)),
            "/local", "/remote");

        Assert.Equal(SyncState.Different, Assert.Single(items).State);
    }

    [Fact]
    public void Size_differs_local_newer_is_UploadChanged()
    {
        var items = SyncEngine.Compare(
            Map(("a.txt", 10, T0.AddMinutes(5))),
            Map(("a.txt", 20, T0)),
            "/local", "/remote");

        Assert.Equal(SyncState.UploadChanged, Assert.Single(items).State);
    }

    [Fact]
    public void Size_matches_with_unknown_remote_time_is_Unchanged()
    {
        var items = SyncEngine.Compare(
            Map(("a.txt", 10, T0)),
            Map(("a.txt", 10, DateTime.MinValue)),
            "/local", "/remote");

        Assert.Equal(SyncState.Unchanged, Assert.Single(items).State);
    }

    [Fact]
    public void Size_differs_with_unknown_times_is_Different()
    {
        var items = SyncEngine.Compare(
            Map(("a.txt", 10, T0)),
            Map(("a.txt", 20, DateTime.MinValue)),
            "/local", "/remote");

        Assert.Equal(SyncState.Different, Assert.Single(items).State);
    }

    [Fact]
    public void Case_insensitive_paths_are_paired()
    {
        var items = SyncEngine.Compare(
            Map(("File.TXT", 10, T0)),
            Map(("file.txt", 10, T0)),
            "/local", "/remote");

        var item = Assert.Single(items);
        Assert.Equal(SyncState.Unchanged, item.State);
        Assert.Equal("File.TXT", item.RelativePath);
    }

    [Fact]
    public void Remote_case_variants_do_not_collapse()
    {
        var items = SyncEngine.Compare(
            Map(("A.txt", 10, T0)),
            Map(("A.txt", 10, T0), ("a.txt", 20, T0)),
            "/local", "/remote");

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.RelativePath == "A.txt" && i.State == SyncState.Unchanged);
        Assert.Contains(items, i => i.RelativePath == "a.txt" && i.State == SyncState.DownloadNew);
    }

    [Fact]
    public void Full_paths_are_built_from_roots()
    {
        var items = SyncEngine.Compare(
            Map(("sub/a.txt", 1, T0)),
            Map(),
            "/local", "/remote");

        var item = Assert.Single(items);
        Assert.Equal(Path.Combine("/local", "sub", "a.txt"), item.LocalFullPath);
        Assert.Equal("/remote/sub/a.txt", item.RemoteFullPath);
    }
}
