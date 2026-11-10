using System.Collections.Concurrent;

namespace CacheMini.Tests;

/// <summary>
/// Chạy các "worker" trên luồng riêng và bắt đầu chúng cùng lúc bằng <see cref="Barrier"/>.
/// </summary>
/// <remarks>
/// <para>
/// Vì sao không dùng <c>Task.Run</c> + <c>Barrier</c>: thread-pool có thể không đủ luồng để tất cả
/// participant cùng tới barrier, dẫn tới treo giả hoặc chờ hàng chục giây. Ở đây mỗi worker là một
/// <see cref="TaskCreationOptions.LongRunning"/> (luồng chuyên dụng), nên barrier luôn đủ người.
/// </para>
/// <para>
/// Mọi exception của worker được gom lại và ném thành <see cref="AggregateException"/> ở luồng test,
/// sau khi tất cả worker đã chạy xong — nhờ vậy test không bao giờ bỏ rơi task đang chạy.
/// </para>
/// </remarks>
internal static class ThreadRunner
{
    /// <summary>Thời gian tối đa để tất cả worker tới được barrier.</summary>
    internal static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Thời gian tối đa để tất cả worker chạy xong.</summary>
    internal static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Chạy <paramref name="body"/> trên <paramref name="workerCount"/> luồng chuyên dụng, đồng thời
    /// giải phóng tất cả qua một barrier.
    /// </summary>
    /// <param name="workerCount">Số worker; phải khớp chính xác số participant của barrier.</param>
    /// <param name="body">Thân worker, nhận chỉ số worker (0-based).</param>
    /// <param name="scenario">Tên tình huống, dùng trong thông điệp lỗi.</param>
    internal static async Task RunAsync(int workerCount, Action<int> body, string scenario)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentNullException.ThrowIfNull(body);

        var errors = new ConcurrentQueue<Exception>();
        using var barrier = new Barrier(workerCount);
        var workers = new Task[workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            int index = i;
            workers[index] = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        if (!barrier.SignalAndWait(BarrierTimeout))
                        {
                            throw new TimeoutException(
                                $"Worker {index} không tới được barrier trong {BarrierTimeout.TotalSeconds:F0}s.");
                        }

                        body(index);
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(
                            new InvalidOperationException($"[{scenario}] worker {index} ném exception: {ex.Message}", ex));
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        await Task.WhenAll(workers).WaitAsync(CompletionTimeout).ConfigureAwait(false);

        if (!errors.IsEmpty)
        {
            throw new AggregateException(
                $"[{scenario}] {errors.Count}/{workerCount} worker thất bại. Đầu tiên: {errors.FirstOrDefault()?.Message}",
                errors.ToArray());
        }
    }
}
