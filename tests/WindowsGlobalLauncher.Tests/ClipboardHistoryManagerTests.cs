using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

    /// <summary>构造一条已置顶的文本历史。</summary>
    private static ClipboardEntry PinnedEntry(string text, int minutesAgo)
    {
        var entry = TextEntry(text, minutesAgo);
        entry.IsPinned = true;
        return entry;
    }

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

    [Fact]
    public void MatchFilter_PinnedEntriesComeFirstRegardlessOfTimestamp()
    {
        // 置顶条目即使更旧也排在最前；输入顺序刻意把未置顶的放前面
        var pinnedOld = PinnedEntry("置顶内容 旧", 30);
        var unpinnedNew = TextEntry("置顶内容 新", 1);

        var result = ClipboardHistoryManager.MatchFilter([unpinnedNew, pinnedOld], "置顶内容");

        Assert.Equal(new[] { pinnedOld, unpinnedNew }, result);
    }

    [Fact]
    public void MatchFilter_SortsPinnedGroupByTimestampDescending()
    {
        var pinnedOld = PinnedEntry("置顶内容 旧", 30);
        var pinnedNew = PinnedEntry("置顶内容 新", 1);

        var result = ClipboardHistoryManager.MatchFilter([pinnedOld, pinnedNew], "置顶内容");

        Assert.Equal(new[] { pinnedNew, pinnedOld }, result);
    }

    [Fact]
    public void MatchFilter_KeepsUnpinnedOrderAfterPinned()
    {
        var pinned = PinnedEntry("关键词 置顶", 50);
        var unpinnedNew = TextEntry("关键词 新", 1);
        var unpinnedOld = TextEntry("关键词 旧", 20);

        var result = ClipboardHistoryManager.MatchFilter([unpinnedOld, unpinnedNew, pinned], "关键词");

        Assert.Equal(new[] { pinned, unpinnedNew, unpinnedOld }, result);
    }

    [Fact]
    public void MatchFilter_PinnedEntryStillNeedsToMatchKeyword()
    {
        // 置顶只影响排序，不豁免搜索：不匹配的置顶条目不显示
        var pinnedMiss = PinnedEntry("无关内容", 1);
        var pinnedMatch = PinnedEntry("目标关键词", 10);

        var result = ClipboardHistoryManager.MatchFilter([pinnedMiss, pinnedMatch], "关键词");

        Assert.Equal(new[] { pinnedMatch }, result);
    }

    [Fact]
    public void MatchFilter_StillExcludesPinnedImageEntry()
    {
        // 图片条目不参与文本搜索，置顶后同样不可见
        var pinnedImage = new ClipboardEntry { Id = "img", IsImage = true, IsPinned = true, Timestamp = BaseTime };
        var text = TextEntry("命中", 1);

        var result = ClipboardHistoryManager.MatchFilter([pinnedImage, text], "命中");

        Assert.Equal(new[] { text }, result);
    }

    [Fact]
    public void MatchFilter_EqualTimestampStableOrderWithPins()
    {
        // 同时间戳下：置顶组内部、未置顶组内部都保持传入顺序（稳定排序）
        var pinnedFirst = PinnedEntry("同一时间", 5);
        var pinnedSecond = PinnedEntry("同一时间", 5);
        var unpinnedFirst = TextEntry("同一时间", 5);
        var unpinnedSecond = TextEntry("同一时间", 5);

        var result = ClipboardHistoryManager.MatchFilter(
            [pinnedFirst, unpinnedFirst, pinnedSecond, unpinnedSecond], "同一时间");

        Assert.Equal(new[] { pinnedFirst, pinnedSecond, unpinnedFirst, unpinnedSecond }, result);
    }

    [Fact]
    public void SortForDisplay_PinnedFirstThenTimestampDescending()
    {
        // 覆盖无搜索（Query("")）的排序路径
        var unpinnedNew = TextEntry("未置顶 新", 1);
        var pinnedOld = PinnedEntry("置顶 旧", 30);
        var pinnedNew = PinnedEntry("置顶 新", 2);
        var unpinnedOld = TextEntry("未置顶 旧", 20);

        var result = ClipboardHistoryManager.SortForDisplay([unpinnedNew, pinnedOld, unpinnedOld, pinnedNew]);

        Assert.Equal(new[] { pinnedNew, pinnedOld, unpinnedNew, unpinnedOld }, result);
    }

    [Fact]
    public void SelectEvictions_NeverEvictsPinnedEntries()
    {
        var entries = new List<ClipboardEntry>();
        for (int i = 0; i < ClipboardHistoryManager.MaxEntries; i++)
            entries.Add(TextEntry($"条目{i}", i));

        // 列表尾部为最旧：把最旧 3 条置顶
        entries[^1].IsPinned = true;
        entries[^2].IsPinned = true;
        entries[^3].IsPinned = true;

        // 头部新增 2 条（列表头部为最新），总量超上限 2 条
        entries.Insert(0, TextEntry("新增1", 0));
        entries.Insert(0, TextEntry("新增2", 0));

        var evictions = ClipboardHistoryManager.SelectEvictions(entries);

        Assert.Equal(2, evictions.Count);
        Assert.DoesNotContain(evictions, e => e.IsPinned);
        // 越过尾部 3 条置顶后，优先淘汰最旧的未置顶条目
        Assert.Equal(new[] { entries[^4], entries[^5] }, evictions);
    }

    [Fact]
    public void SelectEvictions_EvictsOldestUnpinnedFirst()
    {
        var entries = new List<ClipboardEntry>();
        for (int i = 0; i < ClipboardHistoryManager.MaxEntries + 3; i++)
            entries.Add(TextEntry($"条目{i}", i));

        var evictions = ClipboardHistoryManager.SelectEvictions(entries);

        Assert.Equal(3, evictions.Count);
        // 从尾部（最旧）向前依次淘汰
        Assert.Equal(new[] { entries[^1], entries[^2], entries[^3] }, evictions);

        // 未超上限时不淘汰任何条目
        Assert.Empty(ClipboardHistoryManager.SelectEvictions(entries.Take(ClipboardHistoryManager.MaxEntries).ToList()));
    }

    [Fact]
    public void SelectEvictions_ProtectsJustInsertedEntryWhenAllOthersPinned()
    {
        // 外部编辑把历史改成「超过上限且全部置顶」：新复制条目是唯一未置顶项，
        // 也必须保留，否则用户刚复制的内容会被立即丢弃（图片还会留下孤儿 PNG）
        var entries = new List<ClipboardEntry>();
        for (int i = 0; i < ClipboardHistoryManager.MaxEntries; i++)
            entries.Add(PinnedEntry($"置顶{i}", i));

        var justInserted = TextEntry("刚复制", 0);
        entries.Insert(0, justInserted);

        Assert.Equal(new[] { justInserted }, ClipboardHistoryManager.SelectEvictions(entries));
        Assert.Empty(ClipboardHistoryManager.SelectEvictions(entries, justInserted));
    }

    [Fact]
    public void SelectEvictions_AllPinnedReturnsNoEvictions()
    {
        // 全置顶且超限（仅外部编辑可达）：没有可淘汰的未置顶条目，由调用方保留并记 WARN
        var entries = new List<ClipboardEntry>();
        for (int i = 0; i < ClipboardHistoryManager.MaxEntries + 5; i++)
            entries.Add(PinnedEntry($"置顶{i}", i));

        Assert.Empty(ClipboardHistoryManager.SelectEvictions(entries));
    }

    [Fact]
    public void IsPinLimitReached_ReturnsTrueAt100Pins()
    {
        var entries = new List<ClipboardEntry>();
        for (int i = 0; i < ClipboardHistoryManager.MaxPinnedEntries - 1; i++)
            entries.Add(PinnedEntry($"置顶{i}", i));
        entries.Add(TextEntry("未置顶（不计数）", 0));

        Assert.False(ClipboardHistoryManager.IsPinLimitReached(entries), "未满 100 条置顶时不应判定达上限");

        entries.Add(PinnedEntry("第 100 条置顶", 0));

        Assert.True(ClipboardHistoryManager.IsPinLimitReached(entries), "达到 100 条置顶时应判定达上限");
    }

    [Fact]
    public void ClipboardEntry_IsPinnedDefaultsFalseWhenMissingFromJson()
    {
        // 旧版 JSON 没有 IsPinned 字段，反序列化应得到 false（无需迁移）
        var entry = JsonSerializer.Deserialize<ClipboardEntry>("""{"Id":"a1","Text":"旧版数据"}""");

        Assert.NotNull(entry);
        Assert.False(entry!.IsPinned);
    }

    [Fact]
    public void ClipboardEntry_IsPinnedRoundTripsThroughJson()
    {
        var entry = PinnedEntry("置顶内容", 0);

        var json = JsonSerializer.Serialize(entry);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("IsPinned").GetBoolean());

        var roundTrip = JsonSerializer.Deserialize<ClipboardEntry>(json);
        Assert.NotNull(roundTrip);
        Assert.True(roundTrip!.IsPinned);
    }
}
