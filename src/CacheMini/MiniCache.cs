using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace CacheMini;

/// <summary>
/// Hiện thực in-memory của <see cref="ICache{TKey, TValue}"/>: entry bất biến, TTL tuyệt đối,
/// tạo giá trị single-flight và sweeper nền tuỳ chọn.
/// </summary>
/// <remarks>
/// <para>
/// An toàn luồng: trạng thái chỉ nằm trong <see cref="ConcurrentDictionary{TKey, TValue}"/> và các bộ đếm
/// cập nhật bằng <see cref="Interlocked"/>; mỗi lần <see cref="Set"/> ghi một
/// <see cref="CacheEntry{TValue}"/> mới nên luồng đọc không bao giờ thấy bản ghi nửa vời.
/// </para>
/// <para>
/// Thời gian: mọi mốc hết hạn được tính từ <see cref="TimeProvider"/> được inject (không đọc đồng hồ hệ thống
/// trực tiếp), nên test có thể điều khiển thời gian bằng đồng hồ giả.
/// </para>
/// <para>
/// <see cref="Count"/> và <see cref="SweepExpired"/> là O(n) theo số entry; các thao tác còn lại là O(1) trung bình.
/// </para>
/// </remarks>
/// <typeparam name="TKey">Kiểu khoá; phải khác <see langword="null"/>.</typeparam>
/// <typeparam name="TValue">Kiểu giá trị được lưu.</typeparam>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Kiểu sở hữu ITimer nhưng hợp đồng chỉ yêu cầu IAsyncDisposable; CA1001 chỉ nhận diện IDisposable đồng bộ nên báo nhầm.")]
public sealed class MiniCache<TKey, TValue> : ICache<TKey, TValue>, IAsyncDisposable
    where TKey : notnull
{
    // Bản đồ chính. CacheEntry là bất biến nên mọi thao tác ghi đều thay cả instance.
    private readonly ConcurrentDictionary<TKey, CacheEntry<TValue>> _map = new();

    // Cổng single-flight: mỗi khoá có tối đa một Lazy<TValue> đang được đánh giá.
    private readonly ConcurrentDictionary<TKey, Lazy<TValue>> _gates = new();

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan? _defaultTtl;

    // Timer sweeper nền; null khi không cấu hình CacheOptions.SweepInterval.
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "Timer được huỷ trong DisposeAsync; analyzer không lần theo được lời gọi Dispose qua null-conditional.")]
    private readonly ITimer? _sweeper;

    private long _hits;
    private long _misses;
    private long _expirations;
    private int _disposed;

    /// <summary>
    /// Khởi tạo cache với tuỳ chọn và nguồn thời gian cho trước.
    /// </summary>
    /// <param name="options">
    /// Tuỳ chọn cấu hình; <see langword="null"/> → <see cref="CacheOptions.Default"/>
    /// (không TTL mặc định, không sweeper nền).
    /// </param>
    /// <param name="timeProvider">
    /// Nguồn thời gian dùng cho mọi phép tính hạn và cho sweeper; <see langword="null"/> → <see cref="TimeProvider.System"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Khi <see cref="CacheOptions.DefaultTtl"/> là số âm hữu hạn, hoặc <see cref="CacheOptions.SweepInterval"/>
    /// không dương hữu hạn (nhỏ hơn hoặc bằng 0, hoặc <see cref="Timeout.InfiniteTimeSpan"/>).
    /// </exception>
    public MiniCache(CacheOptions? options = null, TimeProvider? timeProvider = null)
    {
        options ??= CacheOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (options.DefaultTtl is { } defaultTtl && defaultTtl < TimeSpan.Zero && defaultTtl != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                defaultTtl,
                "CacheOptions.DefaultTtl phải là số không âm hoặc Timeout.InfiniteTimeSpan.");
        }

        _defaultTtl = options.DefaultTtl;

        if (options.SweepInterval is { } sweepInterval)
        {
            if (sweepInterval <= TimeSpan.Zero || sweepInterval == Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    sweepInterval,
                    "CacheOptions.SweepInterval phải là khoảng thời gian dương hữu hạn.");
            }

            // Timer của .NET chỉ nhận dueTime/period tới ~49,7 ngày (uint.MaxValue - 1 ms).
            // Kẹp các giá trị lớn hơn để cấu hình "gần như vĩnh viễn" vẫn dựng được cache thay vì ném lúc khởi tạo.
            TimeSpan maxTimerInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
            TimeSpan interval = sweepInterval > maxTimerInterval ? maxTimerInterval : sweepInterval;

            // Dùng CreateTimer của TimeProvider (không phải PeriodicTimer) để đồng hồ giả điều khiển được.
            _sweeper = _timeProvider.CreateTimer(
                static state =>
                {
                    _ = ((MiniCache<TKey, TValue>)state!).SweepExpired();
                },
                this,
                interval,
                interval);
        }
    }

    /// <summary>
    /// Lấy giá trị theo khoá nếu entry tồn tại và còn hạn.
    /// </summary>
    /// <param name="key">Khoá cần tra.</param>
    /// <param name="value">Giá trị khi tìm thấy; ngược lại <see langword="default"/>.</param>
    /// <returns><see langword="true"/> nếu có entry còn sống; ngược lại <see langword="false"/>.</returns>
    /// <remarks>
    /// Entry đã hết hạn bị coi là miss: nó được xoá ngay (tăng <see cref="CacheStatistics.Expirations"/>)
    /// và không bao giờ được trả về. Mỗi lần gọi làm tăng <see cref="CacheStatistics.Hits"/> hoặc
    /// <see cref="CacheStatistics.Misses"/>.
    /// </remarks>
    public bool TryGet(TKey key, out TValue? value)
    {
        if (TryGetLive(key, out value))
        {
            Interlocked.Increment(ref _hits);
            return true;
        }

        Interlocked.Increment(ref _misses);
        return false;
    }

    // CA1716: "Set" trùng keyword của VB.NET. Giữ tên này để khớp ICache<TKey,TValue>.Set
    // và quy ước Microsoft.Extensions.Caching.Memory.IMemoryCache.Set.
    /// <summary>
    /// Thêm hoặc ghi đè giá trị cho một khoá.
    /// </summary>
    /// <param name="key">Khoá.</param>
    /// <param name="value">Giá trị cần lưu.</param>
    /// <param name="ttl">
    /// Thời gian sống kể từ lúc gọi. <see langword="null"/> → dùng <see cref="CacheOptions.DefaultTtl"/>
    /// (nếu cũng null thì entry không bao giờ hết hạn). <see cref="Timeout.InfiniteTimeSpan"/> → không hết hạn.
    /// <see cref="TimeSpan.Zero"/> → hết hạn ngay. Số âm hữu hạn bị từ chối.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Khi <paramref name="ttl"/> là số âm hữu hạn.</exception>
    /// <remarks>
    /// Ghi đè luôn tạo <see cref="CacheEntry{TValue}"/> mới, kể cả khi khoá đã tồn tại.
    /// <see cref="Timeout.InfiniteTimeSpan"/> đúng bằng <c>-1 ms</c> và được ưu tiên hơn quy tắc
    /// "số âm bị từ chối" — nó là sentinel "vĩnh viễn", không phải TTL âm.
    /// </remarks>
    [SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "Set is the established .NET cache vocabulary (IMemoryCache.Set) and must match ICache<TKey,TValue>.Set.")]
    public void Set(TKey key, TValue value, TimeSpan? ttl = null)
    {
        if (ttl is { } requestedTtl && requestedTtl < TimeSpan.Zero && requestedTtl != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                ttl,
                "TTL phải là số không âm hoặc Timeout.InfiniteTimeSpan.");
        }

        _map[key] = new CacheEntry<TValue>(value, ComputeExpiresAtTicks(ttl ?? _defaultTtl));
    }

    /// <summary>
    /// Trả về giá trị đã có, hoặc tạo mới bằng <paramref name="factory"/> rồi lưu lại.
    /// </summary>
    /// <param name="key">Khoá.</param>
    /// <param name="factory">Hàm tạo giá trị; chỉ được gọi khi cache miss.</param>
    /// <param name="ttl">Như <see cref="Set"/>.</param>
    /// <returns>Giá trị trong cache (cũ hoặc vừa tạo).</returns>
    /// <remarks>
    /// <para>
    /// Single-flight theo khoá: với cùng một khoá, tại một thời điểm chỉ một luồng chạy
    /// <paramref name="factory"/>; các luồng khác chờ và nhận cùng kết quả. Nếu <paramref name="factory"/>
    /// ném exception thì exception truyền ra ngoài, không có gì được cache, và lần gọi sau sẽ thử lại.
    /// </para>
    /// <para>
    /// Không làm thay đổi <see cref="CacheStatistics.Hits"/> hay <see cref="CacheStatistics.Misses"/>
    /// (chỉ <see cref="TryGet"/> cập nhật hai bộ đếm này).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Khi <paramref name="factory"/> là <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Khi <paramref name="ttl"/> là số âm hữu hạn.</exception>
    public TValue GetOrCreate(TKey key, Func<TKey, TValue> factory, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (ttl is { } requestedTtl && requestedTtl < TimeSpan.Zero && requestedTtl != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                ttl,
                "TTL phải là số không âm hoặc Timeout.InfiniteTimeSpan.");
        }

        if (TryGetLive(key, out TValue? cached))
        {
            return cached!;
        }

        // CỔNG SINGLE-FLIGHT THEO KHOÁ:
        // GetOrAdd bảo đảm mọi luồng cùng khoá nhận về đúng một instance Lazy. Với
        // LazyThreadSafetyMode.ExecutionAndPublication, factory chỉ chạy một lần; các luồng còn lại
        // chờ rồi dùng chung kết quả (hoặc chung exception). Tham số của luồng tạo cổng (factory/ttl)
        // là tham số được dùng cho lần tạo giá trị đó.
        Lazy<TValue> gate = _gates.GetOrAdd(
            key,
            _ => new Lazy<TValue>(
                () => CreateAndCache(key, factory, ttl),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return gate.Value;
        }
        finally
        {
            // Gỡ cổng để dictionary không phình mãi. TryRemove theo cặp (key, gate) để không xoá nhầm
            // cổng mới hơn do luồng khác vừa tạo cho cùng khoá.
            _ = _gates.TryRemove(new KeyValuePair<TKey, Lazy<TValue>>(key, gate));
        }
    }

    /// <summary>
    /// Xoá một khoá, bất kể entry còn hạn hay không.
    /// </summary>
    /// <param name="key">Khoá cần xoá.</param>
    /// <returns><see langword="true"/> nếu có entry bị xoá.</returns>
    /// <remarks>Xoá thủ công không làm tăng <see cref="CacheStatistics.Expirations"/>.</remarks>
    public bool Remove(TKey key) => _map.TryRemove(key, out _);

    /// <summary>
    /// Xoá toàn bộ entry và đặt lại cả ba bộ đếm thống kê về 0.
    /// </summary>
    public void Clear()
    {
        _map.Clear();
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
        Interlocked.Exchange(ref _expirations, 0);
    }

    /// <summary>
    /// Số entry còn sống tại thời điểm gọi.
    /// </summary>
    /// <remarks>
    /// Độ phức tạp O(n): quét và đếm, không dùng biến đếm O(1). Entry đã hết hạn không được tính
    /// và cũng không bị xoá bởi phép đọc này (xoá là việc của <see cref="SweepExpired"/>).
    /// </remarks>
    public int Count
    {
        get
        {
            long now = NowTicks;
            int alive = 0;

            foreach (KeyValuePair<TKey, CacheEntry<TValue>> pair in _map)
            {
                if (!pair.Value.IsExpired(now))
                {
                    alive++;
                }
            }

            return alive;
        }
    }

    /// <summary>
    /// Quét và xoá ngay các entry đã hết hạn.
    /// </summary>
    /// <returns>Số entry bị xoá bởi lần quét này.</returns>
    /// <remarks>
    /// Mỗi entry bị xoá làm tăng <see cref="CacheStatistics.Expirations"/>. Vẫn gọi được sau
    /// <see cref="DisposeAsync"/>: khi đó đây chỉ là một lần quét đồng bộ, không ném exception.
    /// </remarks>
    public int SweepExpired()
    {
        long now = NowTicks;
        int removed = 0;

        foreach (KeyValuePair<TKey, CacheEntry<TValue>> pair in _map)
        {
            if (!pair.Value.IsExpired(now))
            {
                continue;
            }

            // Xoá theo cặp (key, entry) để không xoá nhầm entry vừa được Set lại cho cùng khoá.
            if (_map.TryRemove(pair))
            {
                removed++;
                Interlocked.Increment(ref _expirations);
            }
        }

        return removed;
    }

    /// <summary>
    /// Ảnh chụp thống kê tại thời điểm gọi.
    /// </summary>
    /// <remarks>Ba bộ đếm được đọc bằng <see cref="Interlocked"/> nên ảnh chụp nhất quán theo từng bộ đếm.</remarks>
    public CacheStatistics Statistics => new(
        Interlocked.Read(ref _hits),
        Interlocked.Read(ref _misses),
        Interlocked.Read(ref _expirations));

    /// <summary>
    /// Huỷ sweeper nền (nếu có). Cache vẫn dùng được đồng bộ sau khi dispose.
    /// </summary>
    /// <returns>Một <see cref="ValueTask"/> đã hoàn tất.</returns>
    /// <remarks>
    /// Idempotent: gọi nhiều lần không ném exception; chỉ lần đầu tiên thực sự huỷ timer.
    /// Bản đồ entry không bị xoá, nên <see cref="SweepExpired"/>, <see cref="Count"/> và các thao tác
    /// đọc/ghi vẫn hoạt động bình thường sau đó.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _sweeper?.Dispose();
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private long NowTicks => _timeProvider.GetUtcNow().UtcTicks;

    // Đọc không tăng Hits/Misses, dùng cho đường GetOrCreate và cho phép kiểm tra nội bộ.
    // Entry hết hạn gặp trên đường này cũng bị xoá và tính vào Expirations.
    private bool TryGetLive(TKey key, out TValue? value)
    {
        if (!_map.TryGetValue(key, out var entry))
        {
            value = default;
            return false;
        }

        if (!entry.IsExpired(NowTicks))
        {
            value = entry.Value;
            return true;
        }

        ExpireEntry(key, entry);
        value = default;
        return false;
    }

    private TValue CreateAndCache(TKey key, Func<TKey, TValue> factory, TimeSpan? ttl)
    {
        // Double-check bên trong cổng: luồng vừa rời cổng trước đó có thể đã ghi giá trị vào cache.
        if (TryGetLive(key, out TValue? cached))
        {
            return cached!;
        }

        TValue created = factory(key);
        Set(key, created, ttl);
        return created;
    }

    private void ExpireEntry(TKey key, CacheEntry<TValue> entry)
    {
        // YÊU CẦU NGHIỆM THU #7 — chỉ xoá đúng instance đã hết hạn.
        // TryRemove(KeyValuePair<,>) (có từ .NET 9) so sánh cả value, nên nếu một luồng khác vừa
        // Set lại cùng khoá thì entry mới KHÔNG bị xoá nhầm: lệnh này trả false và giá trị mới vẫn sống.
        // Không dùng TryRemove(key) vì nó xoá bất kể giá trị hiện tại là gì.
        if (_map.TryRemove(new KeyValuePair<TKey, CacheEntry<TValue>>(key, entry)))
        {
            Interlocked.Increment(ref _expirations);
        }
    }

    private long ComputeExpiresAtTicks(TimeSpan? ttl)
    {
        if (!ttl.HasValue || ttl.Value == Timeout.InfiniteTimeSpan)
        {
            return CacheEntry<TValue>.NeverExpires;
        }

        TimeSpan effective = ttl.Value;
        long now = NowTicks;

        // Chặn tràn long: TTL khổng lồ (tới TimeSpan.MaxValue) được coi như vĩnh viễn thay vì
        // tràn số rồi hết hạn sai.
        return effective.Ticks > long.MaxValue - now
            ? CacheEntry<TValue>.NeverExpires
            : now + effective.Ticks;
    }
}
