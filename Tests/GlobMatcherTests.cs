using ShyFtp.Util;

namespace ShyFTP.Tests;

public class GlobMatcherTests
{
    [Theory]
    [InlineData("*", "anything/here.txt", true)]
    [InlineData("**", "a/b/c", true)]
    [InlineData("*.txt", "readme.txt", true)]
    [InlineData("*.txt", "docs/readme.txt", true)]
    [InlineData("*.txt", "readme.md", false)]
    [InlineData("docs/*", "docs/a.txt", true)]
    [InlineData("docs/*", "docs/sub/a.txt", false)]
    [InlineData("docs/**", "docs/sub/a.txt", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "a/c", false)]
    [InlineData("[abc]oo", "foo", false)]
    [InlineData("[abc]oo", "boo", true)]
    [InlineData("[!abc]oo", "foo", true)]
    [InlineData("[!abc]oo", "boo", false)]
    public void Glob_matches(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, path));
    }

    [Theory]
    [InlineData(".txt", "readme.txt", true)]
    [InlineData("readme", "docs/readme.md", true)]
    [InlineData("zzz", "readme.md", false)]
    public void Substring_patterns_match_anywhere(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, path));
    }

    [Fact]
    public void Empty_pattern_does_not_match()
    {
        Assert.False(GlobMatcher.IsMatch("", "a.txt"));
        Assert.False(GlobMatcher.IsMatch("   ", "a.txt"));
    }

    [Fact]
    public void Malformed_character_class_does_not_throw()
    {
        Assert.False(GlobMatcher.IsMatch("[]]", "abc"));
        Assert.False(GlobMatcher.IsMatch("[", "abc"));
    }

    [Fact]
    public void AnyMatches_returns_true_for_one_hit()
    {
        Assert.True(GlobMatcher.AnyMatches(new[] { "*.md", "*.txt" }, "a.txt"));
        Assert.False(GlobMatcher.AnyMatches(new[] { "*.md" }, "a.txt"));
    }
}
