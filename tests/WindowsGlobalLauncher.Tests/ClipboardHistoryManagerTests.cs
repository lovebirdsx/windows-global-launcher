using System;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests;

public class ClipboardHistoryManagerTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 12, 0, 0);

    /// <summary>构造一条文本历史；minutesAgo 越大越旧。</summary>
    private static ClipboardEntry TextEntry(string text, int minutesAgo) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Text = text,
        Timestamp = BaseTime.AddMinutes(-minutesAgo),
    };

    [Fact]
    public void MatchFilter_SortsByTimestampDescending()
    {
        var oldest = TextEntry("时间旧", 30);
        var newest = TextEntry("时间新", 1);
        var middle = TextEntry("时间中", 10);

        var result = ClipboardHistoryManager.MatchFilter([oldest, newest, middle], "时间");

        Assert.Equal(new[] { newest, middle, oldest }, result);
    }

    [Fact]
    public void MatchFilter_ExcludesImageEntries()
    {
        var image = new ClipboardEntry { Id = "img", IsImage = true, Timestamp = BaseTime };
        var text = TextEntry("命中", 1);

        var result = ClipboardHistoryManager.MatchFilter([image, text], "命中");

        Assert.Equal(new[] { text }, result);
    }

    [Fact]
    public void MatchFilter_ExcludesNonMatchingText()
    {
        var match = TextEntry("关键词", 1);
        var miss = TextEntry("无关内容", 2);

        var result = ClipboardHistoryManager.MatchFilter([match, miss], "关键词");

        Assert.Equal(new[] { match }, result);
    }

    [Fact]
    public void MatchFilter_ScoreDoesNotAffectOrder()
    {
        // 旧条目与查询完全一致（分数 1.0），新条目仅子序列命中（分数 0.5）：排序只看时间，新条目在前
        var oldExact = TextEntry("ab", 30);
        var newLoose = TextEntry("axxb", 1);
        Assert.True(FuzzyMatcher.GetMatchScore("ab", "ab") > FuzzyMatcher.GetMatchScore("ab", "axxb"),
            "用例前提：两条目的匹配分数须不同");

        var result = ClipboardHistoryManager.MatchFilter([oldExact, newLoose], "ab");

        Assert.Equal(new[] { newLoose, oldExact }, result);
    }

    [Fact]
    public void MatchFilter_KeepsInputOrderForEqualTimestamp()
    {
        // 同一时间戳（快速连续复制）走 LINQ 稳定排序，保持传入顺序（_entries 头部最新）
        var first = TextEntry("相同时间", 5);
        var second = TextEntry("相同时间", 5);

        var result = ClipboardHistoryManager.MatchFilter([first, second], "相同时间");

        Assert.Equal(new[] { first, second }, result);
    }
}
