using System.Collections.Concurrent;
using CacheMini;

namespace CacheMini.Tests;

/// <summary>
/// Stress đồng thời: Set / TryGet / Remove chồng lấn, sweeper chạy song song với ghi,
/// Clear chen giữa lúc ghi, và tính idempotent của DisposeAsync.
/// </summary>
public sealed class ConcurrencyTests
{
    private static MiniCache<string, string> CreateCache(
        CacheOptions? options = null,
        FakeTimeProvider? clock = null)
        => new(options ?? CacheOptions.Default, clock ?? new FakeTimeProvider());

    private static Task StartLoop(Func<bool> shouldStop, Action body, ConcurrentQueue<Exception> errors)
        => Task.Factory.StartNew(
            () =>
            {
                try
                {
                    while (!shouldStop())
                    {
                        body();
                        Thread.Yield();
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    [Fact]
    public async Task MixedSetGetRemove_10kOps_NoThrow_AndNeverReturnsExpiredValue()
    {
        var clock = new FakeTimeProvider();
        await using var cache = new MiniCache<string, StampedValue>(CacheOptions.Default, clock);

        const int workerCount = 8;
        const int opsPerWorker = 1_250;
        const int keyCount = 16;
        var ttls = new[]
        {
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
        };

        using var clockGate = new ReaderWriterLockSlim();
        var violations = new ConcurrentQueue<string>();
        var tickerErrors = new ConcurrentQueue<Exception>();
        int stop = 0;

        // Ticker nhích đồng hồ giả liên tục để entry hết hạn ngay trong lúc các luồng khác đọc/ghi.
        var ticker = StartLoop(
            () => Volatile.Read(ref stop) != 0,
            () =>
            {
                clockGate.EnterWriteLock();
                try
                {
                    clock.Advance(TimeSpan.FromMilliseconds(25));
                }
                finally
                {
                    clockGate.ExitWriteLock();
                }
            },
            tickerErrors);

        try
        {
            await ThreadRunner.RunAsync(
                workerCount,
                worker =>
                {
                    uint state = unchecked(((uint)worker + 1) * 2_654_435_761u);
                    for (int op = 0; op < opsPerWorker; op++)
                    {
                        var key = "key-" + (int)(Next(ref state) % (uint)keyCount);
                        switch ((int)(Next(ref state) % 10u))
                        {
                            case 0 or 1 or 2 or 3 or 4:
                            {
                                var ttl = ttls[(int)(Next(ref state) % (uint)ttls.Length)];

                                // Giữ read lock để ticker không thể nhích đồng hồ giữa lúc chụp
                                // "now" và lúc Set: nhờ vậy hạn ghi trong value đúng bằng hạn thật.
                                clockGate.EnterReadLock();
                                try
                                {
                                    var expiresAt = clock.GetUtcNow().UtcTicks + ttl.Ticks;
                                    cache.Set(key, new StampedValue(key, expiresAt), ttl);
                                }
                                finally
                                {
                                    clockGate.ExitReadLock();
                                }

                                break;
                            }

                            case 5 or 6 or 7:
                            {
                                var readAt = clock.GetUtcNow().UtcTicks;
                                if (cache.TryGet(key, out var value))
                                {
                                    if (value is null)
                                    {
                                        violations.Enqueue($"{key}: TryGet trả true nhưng value null.");
                                    }
                                    else if (value.Key != key)
                                    {
                                        violations.Enqueue($"{key}: trả value của key khác ('{value.Key}').");
                                    }
                                    else if (value.ExpiresAtUtcTicks <= readAt)
                                    {
                                        var lateMs = (readAt - value.ExpiresAtUtcTicks) / 10_000.0;
                                        violations.Enqueue(
                                            $"{key}: trả value đã hết hạn {lateMs:F3}ms trước lúc đọc "
                                            + $"(expiresAt={value.ExpiresAtUtcTicks}, readAt={readAt}).");
                                    }
                                }

                                break;
                            }

                            default:
                                cache.Remove(key);
                                break;
                        }
                    }
                },
                "mixed-ops");
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            try
            {
                await ticker.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                // Ticker chỉ chạm đồng hồ giả; nếu nó kẹt thì test đã đỏ ở assertion bên dưới.
            }
        }

        Assert.True(tickerErrors.IsEmpty, $"Ticker đồng hồ giả ném exception: {tickerErrors.FirstOrDefault()?.Message}");
        Assert.True(
            violations.IsEmpty,
            $"TryGet trả giá trị đã hết hạn ở {violations.Count} lần. Ví dụ: {string.Join(" | ", violations.Take(3))}");
        Assert.True(cache.Count >= 0, $"Count không được âm, nhưng là {cache.Count}.");
        Assert.True(cache.Count <= keyCount, $"Count ({cache.Count}) vượt quá số key đã dùng ({keyCount}).");
    }

    [Fact]
    public async Task Sweeper_DoesNotRemove_LiveEntries_WhileWritersChurn()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(CacheOptions.Default, clock);

        const int keepCount = 8;
        for (int i = 0; i < keepCount; i++)
        {
            cache.Set("keep-" + i, "keep-" + i, Timeout.InfiniteTimeSpan);
        }

        int stop = 0;
        var violations = new ConcurrentQueue<string>();
        var sweeperErrors = new ConcurrentQueue<Exception>();
        var sweepers = Enumerable.Range(0, 3)
            .Select(_ => StartLoop(
                () => Volatile.Read(ref stop) != 0,
                () => cache.SweepExpired(),
                sweeperErrors))
            .ToArray();

        try
        {
            await ThreadRunner.RunAsync(
                4,
                worker =>
                {
                    uint state = unchecked(((uint)worker + 1) * 2_654_435_761u);
                    for (int i = 0; i < 2_500; i++)
                    {
                        clock.Advance(TimeSpan.FromMilliseconds(5));

                        var churnKey = "churn-" + (int)(Next(ref state) % 12u);
                        cache.Set(churnKey, churnKey + "#" + i, TimeSpan.FromMilliseconds(50));
                        if (i % 7 == 0)
                        {
                            cache.Remove(churnKey);
                        }

                        var keepKey = "keep-" + (int)(Next(ref state) % (uint)keepCount);
                        if (!cache.TryGet(keepKey, out var keepValue) || keepValue != keepKey)
                        {
                            violations.Enqueue(
                                $"{keepKey}: entry TTL vô hạn bị mất hoặc đổi giá trị ('{keepValue ?? "<null>"}').");
                        }
                    }
                },
                "sweeper-vs-writers");
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            try
            {
                await Task.WhenAll(sweepers).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
            }
        }

        Assert.True(sweeperErrors.IsEmpty, $"Sweeper ném exception: {sweeperErrors.FirstOrDefault()?.Message}");
        Assert.True(
            violations.IsEmpty,
            $"{violations.Count} lần entry còn hạn bị mất khi sweeper chạy. Ví dụ: {string.Join(" | ", violations.Take(3))}");

        for (int i = 0; i < keepCount; i++)
        {
            var keepKey = "keep-" + i;
            Assert.True(
                cache.TryGet(keepKey, out var value) && value == keepKey,
                $"{keepKey} không còn sau khi sweeper chạy liên tục.");
        }
    }

    [Fact]
    public async Task Sweeper_DoesNotRemove_EntryRefreshedToInfinite_AfterExpiry()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(CacheOptions.Default, clock);

        const string key = "refresh-race";
        const int iterations = 3_000;

        int stop = 0;
        var violations = new ConcurrentQueue<string>();
        var sweeperErrors = new ConcurrentQueue<Exception>();
        var sweepers = Enumerable.Range(0, 3)
            .Select(_ => StartLoop(
                () => Volatile.Read(ref stop) != 0,
                () => cache.SweepExpired(),
                sweeperErrors))
            .ToArray();

        try
        {
            for (int i = 0; i < iterations; i++)
            {
                // Entry hết hạn ngay; sweeper có thể đã chụp được nó trước khi ta Set lại.
                cache.Set(key, "stale-" + i, TimeSpan.FromMilliseconds(1));
                clock.Advance(TimeSpan.FromMilliseconds(2));
                Thread.Yield();

                // Set lại với TTL vô hạn: không sweeper nào được phép xoá entry mới này.
                var fresh = "fresh-" + i;
                cache.Set(key, fresh, Timeout.InfiniteTimeSpan);
                Thread.Yield();

                if (!cache.TryGet(key, out var value) || value != fresh)
                {
                    violations.Enqueue(
                        $"Vòng {i}: entry vừa Set lại (TTL vô hạn) bị xoá; đọc được '{value ?? "<null>"}' thay vì '{fresh}'.");
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            try
            {
                await Task.WhenAll(sweepers).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
            }
        }

        Assert.True(sweeperErrors.IsEmpty, $"Sweeper ném exception: {sweeperErrors.FirstOrDefault()?.Message}");
        Assert.True(
            violations.IsEmpty,
            $"Sweeper xoá nhầm entry vừa Set lại ở {violations.Count}/{iterations} vòng. "
            + $"Ví dụ: {string.Join(" | ", violations.Take(3))}");
    }

    [Fact]
    public async Task Clear_ConcurrentWithWriters_LeavesConsistentState()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(CacheOptions.Default, clock);

        int stop = 0;
        int clears = 0;
        var clearerErrors = new ConcurrentQueue<Exception>();
        var clearer = StartLoop(
            () => Volatile.Read(ref stop) != 0,
            () =>
            {
                cache.Clear();
                Interlocked.Increment(ref clears);
            },
            clearerErrors);

        try
        {
            await ThreadRunner.RunAsync(
                8,
                worker =>
                {
                    uint state = unchecked(((uint)worker + 1) * 2_654_435_761u);
                    for (int i = 0; i < 1_250; i++)
                    {
                        var key = "key-" + (int)(Next(ref state) % 8u);
                        switch ((int)(Next(ref state) % 3u))
                        {
                            case 0:
                                cache.Set(key, key + "#" + i, TimeSpan.FromMinutes(1));
                                break;
                            case 1:
                                cache.TryGet(key, out _);
                                break;
                            default:
                                cache.Remove(key);
                                break;
                        }
                    }
                },
                "clear-vs-writers");
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            try
            {
                await clearer.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
            }
        }

        Assert.True(clearerErrors.IsEmpty, $"Clear ném exception: {clearerErrors.FirstOrDefault()?.Message}");
        Assert.True(clears > 0, "Không có lần Clear nào chạy xen giữa lúc các luồng đang ghi.");

        cache.Clear();
        cache.Set("final", "v");
        Assert.True(cache.TryGet("final", out var final), "Sau Clear cuối, cache phải dùng lại được bình thường.");
        Assert.Equal("v", final);
        Assert.Equal(1, cache.Count);
        Assert.Equal(1L, cache.Statistics.Hits);
        Assert.Equal(0L, cache.Statistics.Misses);
    }

    [Fact]
    public async Task DisposeAsync_Twice_IsSafe_AndPostDisposeCallsDoNotThrow()
    {
        var clock = new FakeTimeProvider();
        var cache = CreateCache(CacheOptions.Default, clock);
        cache.Set("k", "v");

        await cache.DisposeAsync();
        await cache.DisposeAsync();

        Assert.Null(Capture(() => { cache.SweepExpired(); }));
        Assert.Null(Capture(() => { cache.TryGet("k", out _); }));
        Assert.Null(Capture(() => { cache.Remove("k"); }));
        Assert.Null(Capture(() => { cache.Clear(); }));
        Assert.True(cache.Count >= 0, $"Count sau dispose không được âm, nhưng là {cache.Count}.");
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // Xorshift32: chia hoạt động tất định, không phụ thuộc Random/seed của runtime.
    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private sealed record StampedValue(string Key, long ExpiresAtUtcTicks);
}
