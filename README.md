# CacheMini

Cache in-memory mini cho .NET 10: thread-safe, TTL tuyệt đối, single-flight, dọn dẹp hai lớp.

## Vì sao có repo này

Một cache tự viết thường chết ở ba chỗ: đọc phải dữ liệu đã hết hạn, sweeper xoá nhầm giá trị vừa được ghi lại,
và nhiều luồng cùng gọi factory khi miss. CacheMini xử lý cả ba, và bộ test chứng minh bằng đồng hồ giả
chứ không bằng `Thread.Sleep`.

## Cấu trúc

```
CacheMini.slnx
src/CacheMini/                 thư viện (net10.0)
  ICache.cs                    hợp đồng
  CacheEntry.cs                bản ghi bất biến (value + hạn)
  CacheOptions.cs              DefaultTtl, SweepInterval
  CacheStatistics.cs           Hits / Misses / Expirations
  MiniCache.cs                 hiện thực
tests/CacheMini.Tests/         xUnit
  FakeTimeProvider.cs          đồng hồ điều khiển được, không cần package ngoài
  CacheBasicsTests.cs
  TtlTests.cs
  GetOrCreateTests.cs
  ConcurrencyTests.cs
```

## Dùng

```csharp
using CacheMini;

await using var cache = new MiniCache<string, UserProfile>(new CacheOptions
{
    DefaultTtl = TimeSpan.FromMinutes(5),
    SweepInterval = TimeSpan.FromMinutes(1), // null = không chạy sweeper nền
});

cache.Set("user:42", profile);                       // TTL mặc định
cache.Set("user:43", other, TimeSpan.FromSeconds(30));

if (cache.TryGet("user:42", out var cached) && cached is not null)
    Console.WriteLine(cached.Name);

// Single-flight: 1000 luồng cùng key => factory chỉ chạy 1 lần
var loaded = cache.GetOrCreate("user:42", key => LoadFromDb(key));

Console.WriteLine(cache.Statistics.HitRatio);
```

Không truyền `TimeProvider` thì dùng `TimeProvider.System`. Trong test, truyền đồng hồ giả để điều khiển
thời điểm hết hạn mà không phải chờ.

## Quy ước ngữ nghĩa

| Tình huống | Ý nghĩa |
|---|---|
| `ttl = null` | Dùng `CacheOptions.DefaultTtl`; nếu cả hai null thì sống vĩnh viễn |
| `TimeSpan.Zero` | Hết hạn ngay: biên là `now >= expiresAt` |
| `Timeout.InfiniteTimeSpan` | Không bao giờ hết hạn |
| `-1 ms` | Chính là `Timeout.InfiniteTimeSpan` → vĩnh viễn, **không** bị coi là TTL âm |
| ttl âm hữu hạn (`-2 ms` trở xuống) | `ArgumentOutOfRangeException` |
| Đọc entry đã hết hạn | Tính là miss, xoá luôn, tăng `Expirations` |
| `Remove` | Không tính vào `Expirations` |
| `Clear` | Xoá toàn bộ và reset bộ đếm thống kê |
| `SweepInterval` > ~49.7 ngày | Kẹp xuống 49.7 ngày (`uint.MaxValue-1 ms`, giới hạn của `System.Threading.Timer`) |

## Năm quyết định thiết kế

1. **Entry bất biến + `ConcurrentDictionary`** — mỗi lần ghi tạo instance mới, nên không ai đọc được trạng thái nửa vời.
2. **Dọn dẹp hai lớp** — kiểm tra hạn lúc đọc bảo đảm tính đúng; sweeper nền chỉ để giải phóng bộ nhớ.
3. **Xoá bằng `TryRemove(KeyValuePair)`** — chứ không phải `TryRemove(key)`. Nếu dùng `TryRemove(key)`, sweeper
   có thể xoá mất giá trị *mới* vừa được `Set` trong khe thời gian giữa lúc kiểm tra hạn và lúc xoá.
4. **`TimeProvider` thay cho `DateTimeOffset.UtcNow`** — test điều khiển được thời gian, không flaky.
5. **Single-flight bằng `ConcurrentDictionary<TKey, Lazy<TValue>>`**, gỡ cổng trong `finally`; exception của factory
   không được cache nên lần gọi sau vẫn thử lại được.

## Kiểm thử

51 test, phủ 5 nhóm: ngữ nghĩa cơ bản, TTL (biên chính xác theo tick), `GetOrCreate` single-flight,
đồng thời (10k thao tác, sweeper vs writer, `Clear` chen giữa), thống kê. Toàn bộ dùng đồng hồ giả —
không `Thread.Sleep`, không `Task.Delay`, nên không flaky.

```powershell
# Cách chạy trong workspace này (xem ghi chú bên dưới):
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-tests.ps1

# Trên máy không bị sandbox chặn (CI, terminal thường):
dotnet test CacheMini.slnx
```

### Ghi chú vận hành: vì sao có `scripts/run-tests.ps1`

Trong workspace bị sandbox của DSH gắn ACL/integrity label, `testhost.exe` nằm trong `tests/**/bin`
chết ngay khi khởi động với `0xC0000142` (STATUS_DLL_INIT_FAILED) — cùng file đó copy sang `%TEMP%`
thì chạy bình thường, và một project xunit dựng mới trong `%TEMP%` cũng xanh. Đây là vấn đề vị trí
thư mục, không phải code hay cấu hình project. Script build binary test ra `%TEMP%` rồi chạy `vstest`
từ đó, cho kết quả y hệt `dotnet test`.

### Nghiệm thu

| Tiêu chí | Kết quả |
|---|---|
| Build `-warnaserror` | 0 warning, 0 error |
| Test | 51/51 xanh, 3 lượt liên tiếp, 3.7–5.5s/lượt |
| Không `Thread.Sleep` trong test | ✅ |
| `TryGet` không trả entry hết hạn | ✅ test biên `-1/0/+1` tick |
| `GetOrCreate` 1000 caller cùng key → factory 1 lần | ✅ |
| `DisposeAsync` 2 lần không ném | ✅ |
| Xoá do hết hạn dùng `TryRemove(KeyValuePair)` | ✅ có test tái hiện kịch bản xoá nhầm |

Build bật `TreatWarningsAsErrors` + `GenerateDocumentationFile`: thêm thành viên public mà quên XML doc
là build đỏ ngay, không phải phát hiện muộn lúc review.


## Ngoài phạm vi (cố ý)

Async factory, sliding expiration, giới hạn dung lượng/LRU, persistence, distributed cache, metrics export, AOT.
