using CacheMini;

namespace CacheMini.Tests;

/// <summary>
/// Hợp đồng cơ bản: Set / TryGet / Remove / Clear / Count, giá trị null và thống kê Hits / Misses.
/// </summary>
public sealed class CacheBasicsTests
{
    private static MiniCache<string, string> CreateCache(
        CacheOptions? options = null,
        FakeTimeProvider? clock = null)
        => new(options ?? CacheOptions.Default, clock ?? new FakeTimeProvider());

    [Fact]
    public async Task Set_ThenTryGet_ReturnsStoredValue_AndCountsHit()
    {
        await using var cache = CreateCache();

        cache.Set("alpha", "one");

        Assert.True(cache.TryGet("alpha", out var value), "TryGet phải hit ngay sau Set.");
        Assert.Equal("one", value);
        Assert.Equal(1L, cache.Statistics.Hits);
        Assert.Equal(0L, cache.Statistics.Misses);
    }

    [Fact]
    public async Task TryGet_MissingKey_ReturnsFalse_DefaultValue_AndCountsMiss()
    {
        await using var cache = CreateCache();

        Assert.False(cache.TryGet("missing", out var value), "Key chưa Set phải là miss.");

        Assert.Null(value);
        var stats = cache.Statistics;
        Assert.Equal(0L, stats.Hits);
        Assert.Equal(1L, stats.Misses);
        Assert.Equal(1L, stats.TotalRequests);
        Assert.Equal(0d, stats.HitRatio);
    }

    [Fact]
    public async Task Set_SameKeyTwice_Overwrites_AndKeepsSingleEntry()
    {
        await using var cache = CreateCache();

        cache.Set("k", "v1");
        cache.Set("k", "v2");

        Assert.True(cache.TryGet("k", out var value), "Key đã Set hai lần vẫn phải hit.");
        Assert.Equal("v2", value);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task Remove_ExistingKey_ReturnsTrueThenFalse()
    {
        await using var cache = CreateCache();
        cache.Set("k", "v");

        Assert.True(cache.Remove("k"), "Remove key đang có phải trả true.");
        Assert.False(cache.Remove("k"), "Remove lần hai phải trả false.");
        Assert.False(cache.TryGet("k", out _), "Sau Remove, key không được còn giá trị.");
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Remove_ExpiredButNotYetSwept_ReturnsTrue_AndDoesNotCountAsExpiration()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(new CacheOptions { DefaultTtl = TimeSpan.FromSeconds(1) }, clock);
        cache.Set("k", "v");
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(cache.Remove("k"), "Entry hết hạn nhưng chưa bị dọn vẫn tồn tại vật lý; Remove phải trả true.");
        Assert.Equal(0L, cache.Statistics.Expirations);
    }

    [Fact]
    public async Task Clear_RemovesEverything_AndResetsStatistics()
    {
        await using var cache = CreateCache();
        cache.Set("a", "1");
        cache.Set("b", "2");
        Assert.True(cache.TryGet("a", out _), "Chuẩn bị: cần ít nhất một hit trước khi Clear.");
        Assert.False(cache.TryGet("nope", out _), "Chuẩn bị: cần ít nhất một miss trước khi Clear.");

        cache.Clear();

        Assert.Equal(0, cache.Count);
        var stats = cache.Statistics;
        Assert.Equal(0L, stats.Hits);
        Assert.Equal(0L, stats.Misses);
        Assert.Equal(0L, stats.Expirations);
        Assert.False(cache.TryGet("a", out _), "Sau Clear, key cũ không được còn giá trị.");
        Assert.Equal(1L, cache.Statistics.Misses);
    }

    [Fact]
    public async Task Count_ReflectsLiveEntries_OnSetRemoveAndOverwrite()
    {
        await using var cache = CreateCache();

        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3");
        Assert.Equal(3, cache.Count);

        cache.Remove("b");
        Assert.Equal(2, cache.Count);

        cache.Set("a", "1b");
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task NullValue_IsStored_AndCountedAsHit()
    {
        await using var cache = CreateCache();

        cache.Set("k", null!);

        Assert.True(cache.TryGet("k", out var value), "Entry có value null vẫn là entry tồn tại nên phải hit.");
        Assert.Null(value);
        Assert.Equal(1, cache.Count);
        Assert.Equal(1L, cache.Statistics.Hits);
        Assert.Equal(0L, cache.Statistics.Misses);
    }

    [Fact]
    public async Task Statistics_TrackHitsMissesAndHitRatio()
    {
        await using var cache = CreateCache();
        cache.Set("hit-1", "a");
        cache.Set("hit-2", "b");

        Assert.True(cache.TryGet("hit-1", out _));
        Assert.True(cache.TryGet("hit-2", out _));
        Assert.False(cache.TryGet("miss-1", out _));
        Assert.False(cache.TryGet("miss-2", out _));
        Assert.False(cache.TryGet("miss-3", out _));

        var stats = cache.Statistics;
        Assert.Equal(2L, stats.Hits);
        Assert.Equal(3L, stats.Misses);
        Assert.Equal(5L, stats.TotalRequests);
        Assert.Equal(0.4d, stats.HitRatio, 1e-12);
    }

    [Fact]
    public async Task Statistics_StartAtZero()
    {
        await using var cache = CreateCache();

        var stats = cache.Statistics;

        Assert.Equal(new CacheStatistics(0, 0, 0), stats);
        Assert.Equal(0L, stats.TotalRequests);
        Assert.Equal(0d, stats.HitRatio);
    }

    [Theory]
    // Lưu ý: -1 ms KHÔNG nằm ở đây, vì TimeSpan.FromMilliseconds(-1) chính là Timeout.InfiniteTimeSpan
    // (-1 ms là sentinel "vĩnh viễn" theo quy ước của .NET). Ca đó được kiểm riêng trong TtlTests.
    [InlineData(-2L)]
    [InlineData(-1_000L)]
    [InlineData(-86_400_000L)]
    public async Task Set_NegativeTtl_Throws_AndDoesNotCreateEntry(long milliseconds)
    {
        await using var cache = CreateCache();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => cache.Set("k", "v", TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("k", out _), "Set bị từ chối không được tạo entry.");
    }

    [Fact]
    public async Task GetOrCreate_NullFactory_Throws_AndDoesNotCreateEntry()
    {
        await using var cache = CreateCache();

        Assert.Throws<ArgumentNullException>(() => { cache.GetOrCreate("k", null!); });

        Assert.Equal(0, cache.Count);
    }
}
