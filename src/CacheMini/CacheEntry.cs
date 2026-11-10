namespace CacheMini;

/// <summary>
/// Một bản ghi bất biến trong cache: giá trị kèm thời điểm hết hạn tuyệt đối.
/// </summary>
/// <remarks>
/// Bất biến có chủ đích: mỗi lần <c>Set</c> tạo một instance mới thay vì sửa instance cũ,
/// nhờ vậy một luồng đang đọc không bao giờ thấy trạng thái nửa vời, và luồng dọn dẹp
/// không thể vô tình sửa entry mà người khác đang dùng.
/// </remarks>
/// <typeparam name="TValue">Kiểu giá trị được lưu.</typeparam>
public sealed class CacheEntry<TValue>
{
    /// <summary>
    /// Giá trị đặc biệt cho <see cref="ExpiresAtUtcTicks"/>: entry không bao giờ hết hạn.
    /// </summary>
    public const long NeverExpires = -1L;

    /// <summary>
    /// Tạo một bản ghi cache.
    /// </summary>
    /// <param name="value">Giá trị cần lưu.</param>
    /// <param name="expiresAtUtcTicks">
    /// Thời điểm hết hạn, tính bằng tick UTC — xem <see cref="DateTimeOffset.UtcTicks"/>.
    /// Dùng <see cref="NeverExpires"/> cho entry sống vĩnh viễn.
    /// </param>
    public CacheEntry(TValue value, long expiresAtUtcTicks)
    {
        Value = value;
        ExpiresAtUtcTicks = expiresAtUtcTicks;
    }

    /// <summary>
    /// Giá trị được lưu.
    /// </summary>
    public TValue Value { get; }

    /// <summary>
    /// Thời điểm hết hạn (tick UTC), hoặc <see cref="NeverExpires"/>.
    /// </summary>
    public long ExpiresAtUtcTicks { get; }

    /// <summary>
    /// Cho biết bản ghi đã hết hạn tại thời điểm <paramref name="nowUtcTicks"/> hay chưa.
    /// </summary>
    /// <param name="nowUtcTicks">Thời điểm hiện tại, tính bằng tick UTC.</param>
    /// <returns>
    /// <see langword="true"/> nếu đã hết hạn. Biên được chọn là <c>now &gt;= ExpiresAt</c>:
    /// một entry có TTL bằng không sẽ hết hạn ngay tại thời điểm tạo.
    /// </returns>
    public bool IsExpired(long nowUtcTicks) =>
        ExpiresAtUtcTicks != NeverExpires && nowUtcTicks >= ExpiresAtUtcTicks;
}
