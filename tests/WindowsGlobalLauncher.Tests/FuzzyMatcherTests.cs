using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("abc", "abc", 1.0)]
    [InlineData("abc", "abcde", 0.6)]
    [InlineData("ace", "abcde", 0.48)]
    [InlineData("xyz", "abcde", 0)]
    public void GetMatchScore_WorksAsExpected(string query, string target, double expected)
    {
        double score = FuzzyMatcher.GetMatchScore(query, target);
        Assert.Equal(expected, score, 3);
    }

    [Fact]
    public void GetCommandMatchScore_ReturnsMaxOfProperties()
    {
        var cmd = new Command
        {
            Name = "list",
            Description = "list files",
            Shell = "ls"
        };

        double score = FuzzyMatcher.GetCommandMatchScore("ls", cmd);
        Assert.Equal(1.0, score, 3);
    }
}
