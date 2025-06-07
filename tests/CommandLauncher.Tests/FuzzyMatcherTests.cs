using CommandLauncher;
using Xunit;

namespace CommandLauncher.Tests
{
    public class FuzzyMatcherTests
    {
        [Theory]
        [InlineData("abc", "abc", 1.0)]
        [InlineData("ab", "abc", 0.6666666666666667)]
        [InlineData("ABC", "abc", 1.0)]
        public void GetMatchScore_ReturnsExpectedScore(string query, string target, double expected)
        {
            var score = FuzzyMatcher.GetMatchScore(query, target);
            Assert.Equal(expected, score, 5);
        }

        [Fact]
        public void GetCommandMatchScore_ReturnsHighestFieldScore()
        {
            var command = new Command
            {
                Name = "foo",
                Description = "bar",
                Shell = "baz"
            };

            var scoreDesc = FuzzyMatcher.GetCommandMatchScore("bar", command);
            Assert.Equal(1.0, scoreDesc, 5);

            var expectedPartial = FuzzyMatcher.GetMatchScore("fo", command.Name);
            var scorePartial = FuzzyMatcher.GetCommandMatchScore("fo", command);
            Assert.Equal(expectedPartial, scorePartial, 5);

            var scoreCase = FuzzyMatcher.GetCommandMatchScore("FOO", command);
            Assert.Equal(1.0, scoreCase, 5);
        }
    }
}
