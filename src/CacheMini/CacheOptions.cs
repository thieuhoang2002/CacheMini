namespace CacheMini;

/// <summary>
/// Tuỳ chọn cấu hình cho một <see cref="ICache{TKey, TValue}"/>.
/// </summary>
public sealed class CacheOptions
{
    /// <summary>
    /// Thời gian sống mặc định áp dụng khi lời gọi không truyền <c>ttl</c>.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> (mặc định) nghĩa là entry sống vĩnh viễn cho tới khi bị xoá
    /// hoặc ghi đè. <see cref="Timeout.InfiniteTimeSpan"/> cho kết quả tương đương.
    /// </remarks>
    public TimeSpan? DefaultTtl { get; init; }

    /// <summary>
    /// Chu kỳ chạy sweeper nền để dọn entry hết hạn.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> (mặc định) nghĩa là không chạy sweeper; cache chỉ dọn theo kiểu lazy
    /// khi đọc hoặc khi gọi <see cref="ICache{TKey, TValue}.SweepExpired"/>.
    /// Giá trị phải dương hữu hạn; ngược lại hàm dựng của cache sẽ ném
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </remarks>
    public TimeSpan? SweepInterval { get; init; }

    /// <summary>
    /// Bộ tuỳ chọn mặc định: không TTL, không sweeper nền.
    /// </summary>
    public static CacheOptions Default => new();
}
