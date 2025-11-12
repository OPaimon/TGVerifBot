using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using RedisRateLimiting;
using TelegramVerificationBot.Configuration;

namespace TelegramVerificationBot.Services;

public interface IRateLimiter {
  Task<bool> AllowStartVerificationAsync(long userId, long chatId);
  Task<bool> AllowCallbackAsync(long userId, long chatId);
}

public class RedisRateLimiter : IRateLimiter
{
    private readonly ILogger<RedisRateLimiter> _logger;
    private readonly FixedWindowSettings _settings;
    private readonly Func<IConnectionMultiplexer> _connectionFactory;
    private readonly ConcurrentDictionary<string, RateLimiter> _limiters = new();

    public RedisRateLimiter(IConnectionMultiplexer multiplexer, ILogger<RedisRateLimiter> logger, IOptions<RateLimitingSettings> options)
    {
        _connectionFactory = () => multiplexer;
        _logger = logger;
        _settings = options.Value.FixedWindow;
    }

    private async Task<bool> AllowAsync(string action, long userId, long chatId, int limit, TimeSpan window)
    {
        try
        {
            var partitionKey = $"rl:{action}:{userId}:{chatId}";

            var limiter = _limiters.GetOrAdd(partitionKey, (key) =>
            {
                var options = new RedisFixedWindowRateLimiterOptions
                {
                    ConnectionMultiplexerFactory = _connectionFactory,
                    PermitLimit = limit,
                    Window = window
                };

                return new RedisRateLimiting.RedisFixedWindowRateLimiter<string>(key, options);
            });

            using RateLimitLease lease = await limiter.AcquireAsync(1);
            return lease.IsAcquired;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FixedWindowRateLimiter: Redis unavailable, defaulting to DENY");
            return false;
        }
    }

    public Task<bool> AllowStartVerificationAsync(long userId, long chatId)
    {
        return AllowAsync("start", userId, chatId, _settings.StartVerificationLimit, TimeSpan.FromSeconds(_settings.StartVerificationWindowSeconds));
    }

    public Task<bool> AllowCallbackAsync(long userId, long chatId)
    {
        return AllowAsync("callback", userId, chatId, _settings.CallbackLimit, TimeSpan.FromSeconds(_settings.CallbackWindowSeconds));
    }
}

public class RedisTokenBucketRateLimiter : IRateLimiter
{
    private readonly ILogger<RedisTokenBucketRateLimiter> _logger;
    private readonly TokenBucketSettings _settings;
    private readonly Func<IConnectionMultiplexer> _connectionFactory;
    private readonly ConcurrentDictionary<string, RateLimiter> _limiters = new();

    public RedisTokenBucketRateLimiter(IConnectionMultiplexer multiplexer, ILogger<RedisTokenBucketRateLimiter> logger, IOptions<RateLimitingSettings> options)
    {
        _connectionFactory = () => multiplexer;
        _logger = logger;
        _settings = options.Value.TokenBucket;
    }

    private async Task<bool> AllowAsync(string action, long userId, long chatId, int capacity, double refillRatePerSecond)
    {
        try
        {
            if (refillRatePerSecond <= 0) return false;
            var partitionKey = $"tb:{action}:{userId}:{chatId}";

            var limiter = _limiters.GetOrAdd(partitionKey, (key) =>
            {
                var options = new RedisTokenBucketRateLimiterOptions
                {
                    ConnectionMultiplexerFactory = _connectionFactory,
                    TokenLimit = capacity,
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1.0 / refillRatePerSecond)
                };

                // 关键修复 1：添加 <string> 泛型
                return new RedisRateLimiting.RedisTokenBucketRateLimiter<string>(key, options);
            });

            // 关键修复 2：调用 AcquireAsync (它在库中的行为是 "Attempt")
            using RateLimitLease lease = await limiter.AcquireAsync(1);
            return lease.IsAcquired;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TokenBucketRateLimiter: Redis unavailable, defaulting to DENY");
            return false;
        }
    }

    public Task<bool> AllowStartVerificationAsync(long userId, long chatId)
    {
        return AllowAsync("start", userId, chatId, _settings.StartVerificationCapacity, _settings.StartVerificationRefillRatePerSecond);
    }

    public Task<bool> AllowCallbackAsync(long userId, long chatId)
    {
        return AllowAsync("callback", userId, chatId, _settings.CallbackCapacity, _settings.CallbackRefillRatePerSecond);
    }
}

public class RedisLeakyBucketRateLimiter : IRateLimiter
{
    private readonly ILogger<RedisLeakyBucketRateLimiter> _logger;
    private readonly LeakyBucketSettings _settings;
    private readonly Func<IConnectionMultiplexer> _connectionFactory;
    private readonly ConcurrentDictionary<string, RateLimiter> _limiters = new();

    public RedisLeakyBucketRateLimiter(IConnectionMultiplexer multiplexer, ILogger<RedisLeakyBucketRateLimiter> logger, IOptions<RateLimitingSettings> options)
    {
        _connectionFactory = () => multiplexer;
        _logger = logger;
        _settings = options.Value.LeakyBucket;
    }

    private async Task<bool> AllowAsync(string action, long userId, long chatId, TimeSpan leakInterval)
    {
        try
        {
            var partitionKey = $"lb:{action}:{userId}:{chatId}";

            var limiter = _limiters.GetOrAdd(partitionKey, (key) =>
            {
                var options = new RedisFixedWindowRateLimiterOptions
                {
                    ConnectionMultiplexerFactory = _connectionFactory,
                    PermitLimit = 1,
                    Window = leakInterval
                };

                // 关键修复 1：添加 <string> 泛型
                return new RedisRateLimiting.RedisFixedWindowRateLimiter<string>(key, options);
            });

            // 关键修复 2：调用 AcquireAsync (它在库中的行为是 "Attempt")
            using RateLimitLease lease = await limiter.AcquireAsync(1);
            return lease.IsAcquired;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LeakyBucketRateLimiter: Redis unavailable, defaulting to DENY");
            return false;
        }
    }

    public Task<bool> AllowStartVerificationAsync(long userId, long chatId)
    {
        return AllowAsync("start", userId, chatId, TimeSpan.FromSeconds(_settings.StartVerificationIntervalSeconds));
    }

    public Task<bool> AllowCallbackAsync(long userId, long chatId)
    {
        return AllowAsync("callback", userId, chatId, TimeSpan.FromMilliseconds(_settings.CallbackIntervalMilliseconds));
    }
}
