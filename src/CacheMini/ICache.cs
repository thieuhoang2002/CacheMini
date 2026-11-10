using System.Diagnostics.CodeAnalysis;

namespace CacheMini;

/// <summary>
/// Hợp đồng của một cache in-memory có thời gian sống (TTL) tuyệt đối.
/// </summary>
/// <remarks>
/// <para>
/// Mọi thành viên phải an toàn khi gọi đồng thời từ nhiều luồng; người dùng không cần tự khoá.
/// </para>
/// <para>
/// Ngữ nghĩa thời gian: <c>ttl</c> null nghĩa là dùng <see cref="CacheOptions.DefaultTtl"/>.
/// <see cref="Timeout.InfiniteTimeSpan"/> nghĩa là entry không bao giờ hết hạn.
/// </para>
/// </remarks>
/// <typeparam name="TKey">Kiểu khoá. Phải khác null và có <c>GetHashCode</c>/<c>Equals</c> ổn định.</typeparam>
/// <typeparam name="TValue">Kiểu giá trị được lưu.</typeparam>
public interface ICache<TKey, TValue>
    where TKey : notnull
{
    /// <summary>
    /// Lấy giá trị theo khoá nếu entry tồn tại và còn hạn.
    /// </summary>
    /// <param name="key">Khoá cần tra.</param>
    /// <param name="value">
    /// Khi trả về <see langword="true"/>: giá trị tương ứng. Khi trả về <see langword="false"/>:
    /// <see langword="default"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> nếu tìm thấy entry còn sống; ngược lại <see langword="false"/>.
    /// Entry đã hết hạn <b>không bao giờ</b> được trả về — nó bị coi là miss.
    /// </returns>
    /// <remarks>Lần gọi này làm tăng <see cref="CacheStatistics.Hits"/> hoặc <see cref="CacheStatistics.Misses"/>.</remarks>
    bool TryGet(TKey key, out TValue? value);

    /// <summary>
    /// Thêm hoặc ghi đè giá trị cho một khoá.
    /// </summary>
    /// <param name="key">Khoá.</param>
    /// <param name="value">Giá trị.</param>
    /// <param name="ttl">
    /// Thời gian sống kể từ lúc gọi. <see langword="null"/> → dùng <see cref="CacheOptions.DefaultTtl"/>.
    /// <see cref="Timeout.InfiniteTimeSpan"/> → không hết hạn. Giá trị âm (trừ vô cực) bị từ chối.
    /// </param>
    /// <remarks>
    /// <see cref="Timeout.InfiniteTimeSpan"/> đúng bằng <c>-1 ms</c>, nên nó là ngoại lệ duy nhất của quy tắc
    /// "số âm bị từ chối": <c>-1 ms</c> nghĩa là vĩnh viễn, còn <c>-2 ms</c> trở xuống mới ném exception.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Khi <paramref name="ttl"/> là số âm hữu hạn.</exception>
    // CA1716: "Set" trùng keyword của VB.NET. Giữ tên này vì nó khớp quy ước của
    // Microsoft.Extensions.Caching.Memory.IMemoryCache.Set; đổi thành "Put" sẽ lệch hệ sinh thái.
    [SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "Set is the established .NET cache vocabulary (IMemoryCache.Set); renaming would break convention.")]
    void Set(TKey key, TValue value, TimeSpan? ttl = null);

    /// <summary>
    /// Trả về giá trị đã có, hoặc tạo mới bằng <paramref name="factory"/> rồi lưu lại.
    /// </summary>
    /// <param name="key">Khoá.</param>
    /// <param name="factory">Hàm tạo giá trị; chỉ được gọi khi cache miss.</param>
    /// <param name="ttl">Như <see cref="Set"/>.</param>
    /// <returns>Giá trị trong cache (cũ hoặc vừa tạo).</returns>
    /// <remarks>
    /// Bảo đảm single-flight: với cùng một khoá, tại một thời điểm chỉ một luồng chạy
    /// <paramref name="factory"/>; các luồng khác chờ và nhận cùng kết quả. Nếu <paramref name="factory"/>
    /// ném exception thì exception được truyền ra và <b>không</b> có gì được cache.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Khi <paramref name="factory"/> là null.</exception>
    TValue GetOrCreate(TKey key, Func<TKey, TValue> factory, TimeSpan? ttl = null);

    /// <summary>
    /// Xoá một khoá, bất kể còn hạn hay không.
    /// </summary>
    /// <param name="key">Khoá cần xoá.</param>
    /// <returns><see langword="true"/> nếu có entry bị xoá.</returns>
    bool Remove(TKey key);

    /// <summary>
    /// Xoá toàn bộ entry và đặt lại các bộ đếm thống kê.
    /// </summary>
    void Clear();

    /// <summary>
    /// Số entry còn sống tại thời điểm gọi (entry đã hết hạn không được tính).
    /// </summary>
    /// <remarks>Độ phức tạp O(n): đây là phép quét, không phải biến đếm O(1).</remarks>
    int Count { get; }

    /// <summary>
    /// Quét và xoá ngay các entry đã hết hạn.
    /// </summary>
    /// <returns>Số entry bị xoá bởi lần quét này.</returns>
    /// <remarks>
    /// Đây là lớp dọn dẹp thứ hai: tính đúng của cache đã được bảo đảm bởi kiểm tra hạn lúc đọc,
    /// nên hàm này chỉ để giải phóng bộ nhớ cho các entry "chết mà không ai đọc".
    /// </remarks>
    int SweepExpired();

    /// <summary>
    /// Ảnh chụp thống kê tại thời điểm gọi.
    /// </summary>
    CacheStatistics Statistics { get; }
}
