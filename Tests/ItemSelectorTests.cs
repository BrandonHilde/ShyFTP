using ShyFtp.Cli;
using ShyFtp.Sync;

namespace ShyFTP.Tests;

public class ItemSelectorTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static List<SyncItem> Items() => new()
    {
        new() { RelativePath = "index.html", State = SyncState.UploadNew, LocalExists = true, LocalTime = Now.AddHours(-1) },
        new() { RelativePath = "css/site.css", State = SyncState.UploadChanged, LocalExists = true, RemoteExists = true, LocalTime = Now.AddDays(-3) },
        new() { RelativePath = "img/logo.png", State = SyncState.DownloadNew, RemoteExists = true },
        new() { RelativePath = "notes.txt", State = SyncState.Different, LocalExists = true, RemoteExists = true, LocalTime = Now.AddMinutes(-10), Selected = true },
        new() { RelativePath = "old.txt", State = SyncState.Unchanged, LocalExists = true, RemoteExists = true, LocalTime = Now.AddDays(-30), Dropped = true },
    };

    private static List<int> Resolve(params string[] tokens) => ItemSelector.Resolve(Items(), tokens, Now);

    [Theory]
    [InlineData("8h", 8 * 60)]
    [InlineData("30m", 30)]
    [InlineData("1.5h", 90)]
    [InlineData("2d", 2 * 24 * 60)]
    [InlineData("1w", 7 * 24 * 60)]
    public void Durations_parse(string token, double minutes)
    {
        Assert.True(ItemSelector.TryParseDuration(token, out var span));
        Assert.Equal(minutes, span.TotalMinutes, 3);
    }

    [Theory]
    [InlineData("8")]
    [InlineData("h")]
    [InlineData("8x")]
    [InlineData("readme.md")]
    public void Non_durations_are_rejected(string token) => Assert.False(ItemSelector.TryParseDuration(token, out _));

    [Fact]
    public void Recent_n_is_rewritten_to_hours() =>
        Assert.Equal(new[] { "8.4h", "*.css" }, ItemSelector.Normalize(new[] { "recent", "8.4", "*.css" }));

    [Fact]
    public void Commas_are_split() =>
        Assert.Equal(new[] { "1", "3", "5-7" }, ItemSelector.Normalize(new[] { "1,3", "5-7" }));

    [Fact]
    public void Indices_and_ranges() => Assert.Equal(new[] { 0, 2, 3 }, Resolve("1,3-4"));

    [Fact]
    public void Out_of_range_is_clamped_and_warned_once()
    {
        var warnings = new List<string>();
        var hits = ItemSelector.Resolve(Items(), new[] { "4-100" }, Now, warnings);
        Assert.Equal(new[] { 3, 4 }, hits);
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData("all", new[] { 0, 1, 2, 3, 4 })]
    [InlineData("up", new[] { 0, 1 })]
    [InlineData("down", new[] { 2 })]
    [InlineData("new", new[] { 0, 2 })]
    [InlineData("changed", new[] { 1, 3 })]
    [InlineData("review", new[] { 3 })]
    [InlineData("unchanged", new[] { 4 })]
    [InlineData("selected", new[] { 3 })]
    [InlineData("added", new[] { 3 })]
    [InlineData("dropped", new[] { 4 })]
    public void Keywords(string keyword, int[] expected) => Assert.Equal(expected, Resolve(keyword));

    [Fact]
    public void Time_windows_match_local_modification() => Assert.Equal(new[] { 0, 3 }, Resolve("2h"));

    [Fact]
    public void Globs_and_substrings() => Assert.Equal(new[] { 1, 3, 4 }, Resolve("*.css", ".txt"));

    [Fact]
    public void Tokens_combine_as_a_union() => Assert.Equal(new[] { 0, 1, 2 }, Resolve("up", "3"));

    [Fact]
    public void Unmatched_token_warns()
    {
        var warnings = new List<string>();
        Assert.Empty(ItemSelector.Resolve(Items(), new[] { "*.zip" }, Now, warnings));
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData("up", true)]
    [InlineData("ALL", true)]
    [InlineData("dropped", true)]
    [InlineData("*.css", false)]
    [InlineData("8h", false)]
    public void Keywords_are_recognized(string token, bool expected) =>
        Assert.Equal(expected, ItemSelector.IsKeyword(token));

    [Fact]
    public void Local_matching_for_add()
    {
        Assert.True(ItemSelector.MatchesLocal("all", "a/b.txt", Now.AddYears(-1), Now));
        Assert.True(ItemSelector.MatchesLocal("8h", "a/b.txt", Now.AddHours(-2), Now));
        Assert.False(ItemSelector.MatchesLocal("8h", "a/b.txt", Now.AddHours(-9), Now));
        Assert.True(ItemSelector.MatchesLocal("*.txt", "a/b.txt", Now, Now));
    }
}
