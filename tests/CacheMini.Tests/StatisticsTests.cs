using CacheMini;

namespace CacheMini.Tests;

/// <summary>
/// Toán học thuần của <see cref="CacheStatistics"/> và tính "snapshot" của nó.
/// </summary>
public sealed class StatisticsTests
{
    [Theory]
    [InlineData(0L, 0L, 0d)]
    [InlineData(1L, 1L, 0.5d)]
    [InlineData(3L, 1L, 0.75d)]
    [InlineData(0L, 4L, 0d)]
    [InlineData(5L, 0L, 1d)]
    public void HitRatio_IsHitsOverTotalRequests(long hits, long misses, double expected)
    {
        var stats = new CacheStatistics(hits, misses, 0);

        Assert.Equal(hits + misses, stats.TotalRequests);
        Assert.Equal(expected, stats.HitRatio, 1e-12);
    }

    [Fact]
    public void DefaultStatistics_HasNoRequestsAndZeroRatio()
    {
        var stats = default(CacheStatistics);

        Assert.Equal(0L, stats.Hits);
        Assert.Equal(0L, stats.Misses);
        Assert.Equal(0L, stats.Expirations);
        Assert.Equal(0L, stats.TotalRequests);
        Assert.Equal(0d, stats.HitRatio);
    }

    [Fact]
    public async Task Statistics_IsASnapshot_ThatDoesNotChangeAfterLaterOperations()
    {
        await using var cache = new MiniCache<string, string>(CacheOptions.Default, new FakeTimeProvider());
        cache.Set("a", "1");
        Assert.True(cache.TryGet("a", out _), "Chuẩn bị: cần một hit trước khi chụp snapshot.");

        var snapshot = cache.Statistics;

        Assert.False(cache.TryGet("b", out _), "Chuẩn bị: thêm một miss sau khi chụp snapshot.");
        Assert.Equal(1L, snapshot.Hits);
        Assert.Equal(0L, snapshot.Misses);
        Assert.Equal(1L, snapshot.TotalRequests);
        Assert.Equal(2L, cache.Statistics.TotalRequests);
    }

    [Fact]
    public async Task Clear_ResetsExpirationsToo()
    {
        var clock = new FakeTimeProvider();
        await using var cache = new MiniCache<string, string>(
            new CacheOptions { DefaultTtl = TimeSpan.FromSeconds(1) },
            clock);
        cache.Set("k", "v");
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(cache.TryGet("k", out _), "Chuẩn bị: entry phải hết hạn để tăng Expirations.");
        Assert.Equal(1L, cache.Statistics.Expirations);

        cache.Clear();

        Assert.Equal(new CacheStatistics(0, 0, 0), cache.Statistics);
    }
}
