namespace CacheMini;

/// <summary>
/// Ảnh chụp các bộ đếm thống kê của cache tại một thời điểm.
/// </summary>
/// <param name="Hits">Số lần <c>TryGet</c> tìm thấy entry còn sống.</param>
/// <param name="Misses">Số lần <c>TryGet</c> không tìm thấy, hoặc tìm thấy entry đã hết hạn.</param>
/// <param name="Expirations">
/// Số entry bị loại vì hết hạn, do bất kỳ đường nào: kiểm tra lazy lúc đọc hoặc sweeper.
/// Xoá bằng <see cref="ICache{TKey, TValue}.Remove"/> không tính vào đây.
/// </param>
public readonly record struct CacheStatistics(long Hits, long Misses, long Expirations)
{
    /// <summary>
    /// Tổng số lần tra cứu đã ghi nhận: <see cref="Hits"/> + <see cref="Misses"/>.
    /// </summary>
    public long TotalRequests => Hits + Misses;

    /// <summary>
    /// Tỷ lệ tìm thấy, trong khoảng [0, 1]. Trả về 0 khi chưa có lần tra cứu nào.
    /// </summary>
    public double HitRatio => TotalRequests == 0 ? 0d : (double)Hits / TotalRequests;
}
