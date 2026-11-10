using CacheMini;

namespace CacheMini.Tests;

/// <summary>
/// Ngữ nghĩa TTL tuyệt đối, biên hết hạn <c>now &gt;= expiresAt</c>, dọn lazy và <c>SweepExpired</c>.
/// Toàn bộ thời gian đi qua <see cref="FakeTimeProvider"/>; không có sleep thật.
/// </summary>
public sealed class TtlTests
{
    private static MiniCache<string, string> CreateCache(
        CacheOptions? options = null,
        FakeTimeProvider? clock = null)
        => new(options ?? CacheOptions.Default, clock ?? new FakeTimeProvider());

    [Theory]
    [InlineData(-1L, true)]
    [InlineData(0L, false)]
    [InlineData(1L, false)]
    public async Task TryGet_RespectsExactExpiryBoundary(long offsetTicks, bool expectHit)
    {
        var clock = new FakeTimeProvider();
        var ttl = TimeSpan.FromSeconds(10);
        await using var cache = CreateCache(new CacheOptions { DefaultTtl = ttl }, clock);

        cache.Set("k", "v");
        clock.Advance(TimeSpan.FromTicks(ttl.Ticks + offsetTicks));

        bool hit = cache.TryGet("k", out var value);

        Assert.True(
            hit == expectHit,
            $"Lệch {offsetTicks} tick so với hạn: mong đợi hit={expectHit} nhưng nhận hit={hit} (biên phải là now >= expiresAt).");

        if (expectHit)
        {
            Assert.Equal("v", value);
            Assert.Equal(1L, cache.Statistics.Hits);
        }
        else
        {
            Assert.Null(value);
            Assert.Equal(1L, cache.Statistics.Misses);
            Assert.Equal(1L, cache.Statistics.Expirations);
        }
    }

    [Fact]
    public async Task Entry_IsHitBeforeExpiry_ThenMissAtExactExpiry()
    {
        var clock = new FakeTimeProvider();
        var ttl = TimeSpan.FromSeconds(10);
        await using var cache = CreateCache(new CacheOptions { DefaultTtl = ttl }, clock);

        cache.Set("k", "v");
        clock.Advance(ttl - TimeSpan.FromTicks(1));

        Assert.True(cache.TryGet("k", out var value), "Trước hạn đúng 1 tick vẫn phải hit.");
        Assert.Equal("v", value);

        clock.Advance(TimeSpan.FromTicks(1));

        Assert.False(cache.TryGet("k", out _), "now == expiresAt phải là miss.");
        Assert.Equal(1L, cache.Statistics.Hits);
        Assert.Equal(1L, cache.Statistics.Misses);
        Assert.Equal(1L, cache.Statistics.Expirations);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task ExplicitTtl_OverridesDefaultTtl()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) }, clock);

        cache.Set("k", "v", TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.False(cache.TryGet("k", out _), "ttl tường minh (1s) phải thắng DefaultTtl (5 phút).");
        Assert.Equal(1L, cache.Statistics.Expirations);
    }

    [Fact]
    public async Task DefaultTtl_Applies_WhenTtlIsNull()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(new CacheOptions { DefaultTtl = TimeSpan.FromSeconds(30) }, clock);

        cache.Set("k", "v", null);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(cache.TryGet("k", out _), "Trước DefaultTtl vẫn phải hit.");

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(cache.TryGet("k", out _), "Đúng DefaultTtl phải hết hạn.");
    }

    [Fact]
    public async Task ZeroTtl_ExpiresImmediately()
    {
        await using var cache = CreateCache();

        cache.Set("k", "v", TimeSpan.Zero);

        Assert.False(cache.TryGet("k", out _), "TTL 0 nghĩa là hết hạn ngay tại thời điểm tạo.");
        Assert.Equal(1L, cache.Statistics.Expirations);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task InfiniteTtl_NeverExpires_EvenAfterACentury()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(new CacheOptions { DefaultTtl = TimeSpan.FromSeconds(1) }, clock);

        cache.Set("forever", "v", Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.FromDays(365 * 100));

        Assert.True(cache.TryGet("forever", out var value), "Timeout.InfiniteTimeSpan phải không bao giờ hết hạn.");
        Assert.Equal("v", value);
        Assert.Equal(0, cache.SweepExpired());
        Assert.Equal(1, cache.Count);
        Assert.Equal(0L, cache.Statistics.Expirations);
    }

    [Fact]
    public async Task NullDefaultTtl_MeansNeverExpires()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(CacheOptions.Default, clock);

        cache.Set("k", "v");
        clock.Advance(TimeSpan.FromDays(3_650));

        Assert.True(cache.TryGet("k", out _), "DefaultTtl null nghĩa là sống vĩnh viễn.");
        Assert.Equal(0, cache.SweepExpired());
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task InfiniteDefaultTtl_MeansNeverExpires()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(new CacheOptions { DefaultTtl = Timeout.InfiniteTimeSpan }, clock);

        cache.Set("k", "v");
        clock.Advance(TimeSpan.FromDays(365 * 50));

        Assert.True(cache.TryGet("k", out _), "DefaultTtl = Timeout.InfiniteTimeSpan phải tương đương null.");
        Assert.Equal(0, cache.SweepExpired());
    }

    [Fact]
    public async Task SweepExpired_RemovesOnlyExpired_AndCountDrops()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(CacheOptions.Default, clock);

        cache.Set("short-1", "a", TimeSpan.FromSeconds(1));
        cache.Set("short-2", "b", TimeSpan.FromSeconds(1));
        cache.Set("short-3", "c", TimeSpan.FromSeconds(1));
        cache.Set("long-1", "d", TimeSpan.FromMinutes(10));
        cache.Set("long-2", "e", Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(2, cache.Count);
        Assert.Equal(3, cache.SweepExpired());
        Assert.Equal(2, cache.Count);
        Assert.Equal(0, cache.SweepExpired());
        Assert.Equal(3L, cache.Statistics.Expirations);

        Assert.True(cache.TryGet("long-1", out var longOne), "Entry còn hạn không được bị sweeper xoá.");
        Assert.Equal("d", longOne);
        Assert.True(cache.TryGet("long-2", out var longTwo), "Entry TTL vô hạn không được bị sweeper xoá.");
        Assert.Equal("e", longTwo);
    }

    [Fact]
    public async Task ExpiredEntry_IsRemovedLazily_SoSweepFindsNothing()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(new CacheOptions { DefaultTtl = TimeSpan.FromSeconds(1) }, clock);

        cache.Set("k", "v");
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.False(cache.TryGet("k", out _), "Entry hết hạn phải là miss.");
        Assert.Equal(1L, cache.Statistics.Expirations);
        Assert.Equal(0, cache.SweepExpired());
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Set_RefreshesExpiry_OnOverwrite()
    {
        var clock = new FakeTimeProvider();
        var ttl = TimeSpan.FromSeconds(10);
        await using var cache = CreateCache(CacheOptions.Default, clock);

        cache.Set("k", "v1", ttl);
        clock.Advance(TimeSpan.FromSeconds(9));
        cache.Set("k", "v2", ttl);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(cache.TryGet("k", out var value), "Set lại phải tính hạn từ thời điểm gọi, không giữ hạn cũ.");
        Assert.Equal("v2", value);
    }

    [Fact]
    public async Task Expirations_CountBothLazyReadsAndSweeps()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(new CacheOptions { DefaultTtl = TimeSpan.FromSeconds(1) }, clock);
        cache.Set("lazy-1", "a");
        cache.Set("lazy-2", "b");
        cache.Set("swept", "c");
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.False(cache.TryGet("lazy-1", out _));
        Assert.False(cache.TryGet("lazy-2", out _));
        Assert.Equal(1, cache.SweepExpired());

        var stats = cache.Statistics;
        Assert.Equal(0L, stats.Hits);
        Assert.Equal(2L, stats.Misses);
        Assert.Equal(3L, stats.Expirations);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task ConfiguredSweepInterval_DoesNotReplaceLazyExpiry()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(
            new CacheOptions { DefaultTtl = TimeSpan.FromSeconds(2), SweepInterval = TimeSpan.FromMinutes(1) },
            clock);

        cache.Set("k", "v");
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.False(cache.TryGet("k", out _), "Sweeper nền không được là điều kiện để entry hết hạn.");
        Assert.Equal(1L, cache.Statistics.Expirations);
    }
}
