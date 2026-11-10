using System.Collections.Concurrent;
using CacheMini;

namespace CacheMini.Tests;

/// <summary>
/// <c>GetOrCreate</c>: single-flight theo từng key, factory lỗi không được cache,
/// các key khác nhau không bị serialise oan.
/// </summary>
public sealed class GetOrCreateTests
{
    private static MiniCache<string, string> CreateCache(
        CacheOptions? options = null,
        FakeTimeProvider? clock = null)
        => new(options ?? CacheOptions.Default, clock ?? new FakeTimeProvider());

    [Fact]
    public async Task Miss_InvokesFactoryOnce_AndCachesResult()
    {
        await using var cache = CreateCache();
        int calls = 0;
        string? seenKey = null;

        var first = cache.GetOrCreate("k", key =>
        {
            Interlocked.Increment(ref calls);
            seenKey = key;
            return "computed";
        });

        var second = cache.GetOrCreate("k", _ =>
        {
            Interlocked.Increment(ref calls);
            return "from-second-factory";
        });

        Assert.Equal("computed", first);
        Assert.Equal("computed", second);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal("k", seenKey);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task Hit_DoesNotInvokeFactory()
    {
        await using var cache = CreateCache();
        cache.Set("k", "cached");

        var result = cache.GetOrCreate(
            "k",
            _ => throw new InvalidOperationException("Factory không được gọi khi cache hit."));

        Assert.Equal("cached", result);
    }

    [Fact]
    public async Task NullFactory_Throws_AndDoesNotCreateEntry()
    {
        await using var cache = CreateCache();

        Assert.Throws<ArgumentNullException>(() => { cache.GetOrCreate("k", null!); });

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task SingleFlight_64Threads_FactoryRunsExactlyOnce()
    {
        const int threadCount = 64;
        await using var cache = CreateCache();

        int factoryCalls = 0;
        var results = new string[threadCount];
        var workerErrors = new ConcurrentQueue<Exception>();

        using var start = new ManualResetEventSlim(false);
        using var factoryEntered = new ManualResetEventSlim(false);
        using var releaseFactory = new ManualResetEventSlim(false);

        var workers = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int index = i;
            workers[index] = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        start.Wait(TimeSpan.FromSeconds(10));
                        results[index] = cache.GetOrCreate(
                            "hot-key",
                            _ =>
                            {
                                Interlocked.Increment(ref factoryCalls);
                                factoryEntered.Set();

                                // Giữ factory 50ms để 63 luồng còn lại thật sự dồn vào cửa single-flight,
                                // thay vì may mắn thấy cache đã có giá trị.
                                releaseFactory.Wait(TimeSpan.FromMilliseconds(50));
                                return "computed";
                            });
                    }
                    catch (Exception ex)
                    {
                        workerErrors.Enqueue(
                            new InvalidOperationException($"Luồng {index} ném exception: {ex.Message}", ex));
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        start.Set();
        bool factoryStarted = factoryEntered.Wait(TimeSpan.FromSeconds(10));
        releaseFactory.Set();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(factoryStarted, "Factory chưa từng chạy: GetOrCreate không gọi factory khi miss.");
        Assert.True(
            workerErrors.IsEmpty,
            $"Có {workerErrors.Count}/{threadCount} luồng ném exception. Đầu tiên: {workerErrors.FirstOrDefault()?.Message}");
        Assert.True(
            Volatile.Read(ref factoryCalls) == 1,
            $"Factory phải chạy đúng 1 lần cho 64 luồng cùng key, nhưng chạy {Volatile.Read(ref factoryCalls)} lần.");

        for (int i = 0; i < threadCount; i++)
        {
            Assert.True(
                results[i] == "computed",
                $"Luồng {i} nhận '{(results[i] ?? "<null>")}' thay vì 'computed'.");
        }

        Assert.Equal(1, cache.Count);
        var stats = cache.Statistics;
        Assert.True(
            stats.Misses <= threadCount,
            $"Misses phình bất thường: {stats.Misses} > {threadCount} (số luồng gọi), dấu hiệu retry/đếm lặp.");
        Assert.Equal(0L, stats.Expirations);
    }

    [Fact]
    public async Task FactoryThrows_ExceptionEscapes_AndNothingIsCached()
    {
        await using var cache = CreateCache();
        int attempts = 0;

        var thrown = Assert.Throws<InvalidOperationException>(() =>
        {
            cache.GetOrCreate("k", _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("factory boom");
            });
        });

        Assert.Contains("factory boom", thrown.Message);
        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("k", out _), "Factory lỗi không được để lại entry nào.");

        var value = cache.GetOrCreate("k", _ =>
        {
            Interlocked.Increment(ref attempts);
            return "ok";
        });

        Assert.Equal("ok", value);
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task DifferentKeys_RunFactoriesInParallel()
    {
        await using var cache = CreateCache();

        using var bothEntered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(false);

        static string Factory(string key, CountdownEvent entered, ManualResetEventSlim gate)
        {
            entered.Signal();
            gate.Wait(TimeSpan.FromSeconds(5));
            return key + "-value";
        }

        var tasks = new[]
        {
            Task.Factory.StartNew(
                () => cache.GetOrCreate("a", key => Factory(key, bothEntered, release)),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default),
            Task.Factory.StartNew(
                () => cache.GetOrCreate("b", key => Factory(key, bothEntered, release)),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default),
        };

        bool bothStarted = bothEntered.Wait(TimeSpan.FromSeconds(2));
        int enteredCount = 2 - bothEntered.CurrentCount;
        release.Set();
        var values = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(
            bothStarted,
            $"Hai factory cho hai key khác nhau không chạy đồng thời (mới {enteredCount}/2 vào trong 2s). "
            + "GetOrCreate không được serialise toàn cục.");
        Assert.Equal(2, values.Length);
        Assert.Equal("a-value", values[0]);
        Assert.Equal("b-value", values[1]);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task ConcurrentDistinctKeys_EachCallerGetsOwnValue()
    {
        const int keyCount = 8;
        const int threadsPerKey = 4;
        await using var cache = CreateCache();

        var mismatches = new ConcurrentQueue<string>();
        using var start = new ManualResetEventSlim(false);
        var workers = new Task[keyCount * threadsPerKey];

        int next = 0;
        for (int k = 0; k < keyCount; k++)
        {
            int keyIndex = k;
            for (int t = 0; t < threadsPerKey; t++)
            {
                workers[next++] = Task.Factory.StartNew(
                    () =>
                    {
                        start.Wait(TimeSpan.FromSeconds(10));
                        var key = "key-" + keyIndex;
                        var expected = key + "-value";
                        var actual = cache.GetOrCreate(key, _ => expected);
                        if (actual != expected)
                        {
                            mismatches.Enqueue($"{key}: nhận '{actual}' thay vì '{expected}'.");
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
        }

        start.Set();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(
            mismatches.IsEmpty,
            $"{mismatches.Count} lần trả sai giá trị. Ví dụ: {string.Join(" | ", mismatches.Take(3))}");
        Assert.Equal(keyCount, cache.Count);
    }

    [Fact]
    public async Task GetOrCreate_HonoursTtl_AndRefetchesAfterExpiry()
    {
        var clock = new FakeTimeProvider();
        await using var cache = CreateCache(new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) }, clock);
        int calls = 0;

        string Factory(string key) => "v" + Interlocked.Increment(ref calls);

        var first = cache.GetOrCreate("k", Factory, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        var second = cache.GetOrCreate("k", Factory, TimeSpan.FromSeconds(1));

        Assert.Equal("v1", first);
        Assert.Equal("v2", second);
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task SingleFlight_1000ConcurrentCallers_FactoryRunsExactlyOnce()
    {
        // Tiêu chí nghiệm thu #5 nói đúng con số 1000 caller. Dùng luồng chuyên dụng (ThreadRunner)
        // thay vì Task.Run: 999 caller nghẽn trong Lazy.Value sẽ chiếm luồng, và thread-pool không
        // bao giờ bơm đủ 1000 luồng kịp — đó sẽ là phép đo thread-pool, không phải phép đo single-flight.
        const int callerCount = 1000;
        await using var cache = CreateCache();

        int factoryCalls = 0;
        var results = new string[callerCount];
        var mismatches = new System.Collections.Concurrent.ConcurrentQueue<string>();

        await ThreadRunner.RunAsync(
            callerCount,
            index =>
            {
                results[index] = cache.GetOrCreate(
                    "hot",
                    _ =>
                    {
                        Interlocked.Increment(ref factoryCalls);

                        // Giữ factory đủ lâu để 999 caller còn lại thật sự xếp hàng ở cửa single-flight.
                        Thread.SpinWait(2_000_000);
                        return "computed";
                    });
            },
            "1000 caller cùng key");

        Assert.True(
            Volatile.Read(ref factoryCalls) == 1,
            $"Factory phải chạy đúng 1 lần cho 1000 caller cùng key, nhưng chạy {Volatile.Read(ref factoryCalls)} lần.");

        for (int i = 0; i < callerCount; i++)
        {
            if (results[i] != "computed")
            {
                mismatches.Enqueue($"caller {i} nhận '{results[i] ?? "<null>"}'.");
            }
        }

        Assert.True(
            mismatches.IsEmpty,
            $"{mismatches.Count}/{callerCount} caller nhận sai. Ví dụ: {string.Join(" | ", mismatches.Take(3))}");
        Assert.Equal(1, cache.Count);
    }
}
