# CacheMini

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Tests: 51 Passed](https://img.shields.io/badge/Tests-51%20Passed-brightgreen)](tests/CacheMini.Tests)

A lightweight, high-performance in-memory cache library for .NET 10: thread-safe, absolute TTL, single-flight factory execution, and two-tier cleanup.

---

## Why CacheMini?

Ad-hoc in-memory caches commonly fail in three critical concurrency scenarios:
1. **Returning expired data** under race conditions.
2. **Sweeper deleting newly written values** due to naive `TryRemove(key)` calls.
3. **Cache stampedes (dogpiling):** multiple concurrent threads invoking the expensive factory simultaneously on cache misses.

CacheMini solves all three challenges. Moreover, its comprehensive test suite is verified using controlled time abstraction (`TimeProvider`) without relying on brittle `Thread.Sleep` or `Task.Delay`.

---

## Project Structure

```
CacheMini.slnx
src/CacheMini/                 Core Library (.NET 10)
  ICache.cs                    Public contract & interface
  CacheEntry.cs                Immutable record (Value + Expiration)
  CacheOptions.cs              DefaultTtl, SweepInterval configurations
  CacheStatistics.cs           Thread-safe telemetry (Hits / Misses / Expirations)
  MiniCache.cs                 Core implementation
tests/CacheMini.Tests/         Test Suite (xUnit)
  FakeTimeProvider.cs          Deterministic, zero-dependency fake clock
  CacheBasicsTests.cs          CRUD semantics & API verification
  TtlTests.cs                  Boundary & precision tick tests
  GetOrCreateTests.cs          Single-flight factory execution tests
  ConcurrencyTests.cs          High-load race condition stress tests (10k ops)
```

---

## Quick Start

```csharp
using CacheMini;

// Initialize cache with options
await using var cache = new MiniCache<string, UserProfile>(new CacheOptions
{
    DefaultTtl = TimeSpan.FromMinutes(5),
    SweepInterval = TimeSpan.FromMinutes(1), // Set null to disable background sweeper
});

// Set items with default or custom TTL
cache.Set("user:42", profile);
cache.Set("user:43", otherProfile, TimeSpan.FromSeconds(30));

// Try retrieve item
if (cache.TryGet("user:42", out var cached) && cached is not null)
{
    Console.WriteLine(cached.Name);
}

// Single-flight: 1,000 concurrent threads requesting the same key => factory executes exactly once
var loaded = cache.GetOrCreate("user:42", key => LoadFromDatabase(key));

// Access cache telemetry
Console.WriteLine($"Hit Ratio: {cache.Statistics.HitRatio:P2}");
```

> **Note:** If no `TimeProvider` is passed, `TimeProvider.System` is used by default. In unit tests, inject a fake clock to control expiration deterministically without waiting.

---

## Semantic Rules

| Scenario | Behavior |
|---|---|
| `ttl = null` | Uses `CacheOptions.DefaultTtl`; if both are null, entry lives indefinitely |
| `TimeSpan.Zero` | Expires immediately: boundary is `now >= expiresAt` |
| `Timeout.InfiniteTimeSpan` | Never expires |
| `-1 ms` | Alias for `Timeout.InfiniteTimeSpan` → indefinite lifetime (**not** treated as negative TTL) |
| Finite negative TTL (`<= -2 ms`) | Throws `ArgumentOutOfRangeException` |
| Reading expired entry | Evaluated as cache miss, evicted immediately, increments `Expirations` |
| `Remove(key)` | Removes entry without incrementing `Expirations` |
| `Clear()` | Purges all entries and resets telemetry counters |
| `SweepInterval > ~49.7 days` | Clamped to 49.7 days (`uint.MaxValue - 1 ms`, limitation of `System.Threading.Timer`) |

---

## 5 Core Design Decisions

1. **Immutable Entries + `ConcurrentDictionary`:** Each write creates a new immutable instance, ensuring no reader ever observes a half-initialized or corrupted state.
2. **Two-Tier Eviction:** Lazy evaluation on read guarantees correctness; background sweeper periodically reclaims memory for unread expired entries.
3. **Atomic Removal via `TryRemove(KeyValuePair)`:** Avoids `TryRemove(key)`. If `TryRemove(key)` were used, a background sweep could mistakenly delete a *new* value set right after the expiration check.
4. **Deterministic `TimeProvider` Abstraction:** Fully replaces `DateTimeOffset.UtcNow`, enabling deterministic, zero-flakiness unit testing without real-world wall-clock delays.
5. **Single-Flight via `ConcurrentDictionary<TKey, Lazy<TValue>>`:** Concurrent callers for the same key share a single execution gate, unlocked in `finally`. Factory exceptions are not cached, allowing subsequent calls to retry cleanly.

---

## Testing & Quality Assurance

51 unit tests covering 5 test categories: basic semantics, exact tick-boundary TTL, single-flight `GetOrCreate`, high concurrency (10,000 operations, sweeper vs. writer races, concurrent `Clear`), and telemetry statistics.

```bash
# Run tests directly
dotnet test CacheMini.slnx

# Run via companion PowerShell runner (for restricted/sandbox environments)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-tests.ps1
```

### Verification Highlights

| Criterion | Result |
|---|---|
| Build with `-warnaserror` | 0 warnings, 0 errors |
| Test Coverage | 51/51 passed across consecutive test passes |
| `Thread.Sleep` in tests | 0 occurrences (100% deterministic) |
| `TryGet` expired entries | Verified: zero leakage at `-1 / 0 / +1` tick boundaries |
| Single-Flight Verification | Verified: 1,000 concurrent callers invoke factory exactly once |
| `DisposeAsync` Idempotency | Verified: multiple dispose invocations do not throw |
| Race-Condition Eviction | Verified: `TryRemove(KeyValuePair)` eliminates false evictions |

> **Compiler Strictness:** `TreatWarningsAsErrors` and `GenerateDocumentationFile` are enabled across all projects. Any missing XML documentation on public members will trigger a build error immediately.

---

## Intentional Out of Scope

To remain ultra-compact, modular, and zero-dependency, the following features are intentionally out of scope:
- Async factory delegates
- Sliding window expiration
- Capacity bounding / LRU eviction policy
- Persistence & distributed caching
- OpenTelemetry / external metrics export
- Native AOT trimming annotations

---

## License

This project is licensed under the [MIT License](LICENSE).
