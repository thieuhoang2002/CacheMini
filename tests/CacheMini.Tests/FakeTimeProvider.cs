namespace CacheMini.Tests;

/// <summary>
/// Đồng hồ giả điều khiển được dùng cho toàn bộ test TTL. Không phụ thuộc package ngoài
/// (không dùng <c>Microsoft.Extensions.TimeProvider.Testing</c>).
/// </summary>
/// <remarks>
/// <para>
/// Test chỉ dịch chuyển thời gian bằng <see cref="Advance"/> / <see cref="SetUtcNow"/>;
/// không có <c>Thread.Sleep</c> hay <c>Task.Delay</c> thật nào để chờ hết hạn.
/// </para>
/// <para>
/// <see cref="CreateTimer"/> trả về một timer <b>không bao giờ bắn</b>. Đây là lựa chọn có chủ đích:
/// test không được phụ thuộc vào sweeper nền, và một timer thật sẽ đưa thời gian thật quay lại
/// bài test (nguồn flaky). Mọi đường dọn dẹp trong test đều đi qua <c>SweepExpired</c> tường minh.
/// </para>
/// </remarks>
public sealed class FakeTimeProvider : TimeProvider
{
    /// <summary>Mốc khởi đầu mặc định: 2026-01-01T00:00:00Z.</summary>
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private long _utcTicks;
    private int _createTimerCalls;

    /// <summary>Tạo đồng hồ bắt đầu tại <see cref="Start"/>.</summary>
    public FakeTimeProvider()
        : this(Start)
    {
    }

    /// <summary>Tạo đồng hồ bắt đầu tại thời điểm <paramref name="start"/>.</summary>
    /// <param name="start">Mốc thời gian bất kỳ; được chuẩn hoá về UTC.</param>
    public FakeTimeProvider(DateTimeOffset start) => _utcTicks = start.UtcTicks;

    /// <summary>
    /// Số lần <see cref="CreateTimer"/> được gọi. Cho biết hiện thực có thử dựng sweeper nền hay không;
    /// test không assert vào giá trị này.
    /// </summary>
    public int CreateTimerCalls => Volatile.Read(ref _createTimerCalls);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    /// <inheritdoc />
    public override long GetTimestamp() => Interlocked.Read(ref _utcTicks);

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>
    /// Tua đồng hồ tới thêm <paramref name="delta"/>.
    /// </summary>
    /// <param name="delta">Khoảng thời gian không âm.</param>
    /// <exception cref="ArgumentOutOfRangeException">Khi <paramref name="delta"/> âm.</exception>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "Đồng hồ giả chỉ tua tới, không tua lùi.");
        }

        Interlocked.Add(ref _utcTicks, delta.Ticks);
    }

    /// <summary>Đặt lại thời điểm hiện tại (dùng khi cần mốc tuyệt đối khác).</summary>
    /// <param name="value">Mốc thời gian mới.</param>
    public void SetUtcNow(DateTimeOffset value) => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);

    /// <summary>
    /// Trả về một timer không bao giờ bắn. Sweeper nền (nếu hiện thực có dựng) sẽ không bao giờ chạy,
    /// nhờ vậy kết quả test chỉ phụ thuộc vào <see cref="Advance"/> và các lời gọi tường minh.
    /// </summary>
    /// <param name="callback">Callback (bị bỏ qua).</param>
    /// <param name="state">State (bị bỏ qua).</param>
    /// <param name="dueTime">Thời điểm bắn đầu (bị bỏ qua).</param>
    /// <param name="period">Chu kỳ (bị bỏ qua).</param>
    /// <returns>Timer trơ.</returns>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Interlocked.Increment(ref _createTimerCalls);
        return InertTimer.Instance;
    }

    private sealed class InertTimer : ITimer
    {
        internal static readonly InertTimer Instance = new();

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
