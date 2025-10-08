using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TelegramVerificationBot.Configuration;

namespace TelegramVerificationBot;

public interface IRateLimiter
{
    Task<bool> AllowStartVerificationAsync(long userId, long chatId);
    Task<bool> AllowCallbackAsync(long userId, long chatId);
}

public class RedisRateLimiter : IRateLimiter
{
    private readonly IDatabase _redis;
    private readonly ILogger<RedisRateLimiter> _logger;
    private readonly FixedWindowSettings _settings;

    public RedisRateLimiter(IDatabase redis, ILogger<RedisRateLimiter> logger, IOptions<RateLimitingSettings> options)
    {
        _redis = redis;
        _logger = logger;
        _settings = options.Value.FixedWindow;
    }

    private async Task<bool> AllowAsync(string action, long userId, long chatId, int limit, TimeSpan window)
    {
        try
        {
            var key = $"rl:{action}:{userId}:{chatId}";
            var value = await _redis.StringIncrementAsync(key).ConfigureAwait(false);
            if (value == 1)
            {
                await _redis.KeyExpireAsync(key, window).ConfigureAwait(false);
            }
            return value <= limit;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RateLimiter: Redis unavailable, defaulting to allow");
            return true;
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
    private readonly IDatabase _redis;
    private readonly ILogger<RedisTokenBucketRateLimiter> _logger;
    private readonly TokenBucketSettings _settings;

    private const string TokenBucketLuaScript = @"
        local key = KEYS[1]
        local capacity = tonumber(ARGV[1])
        local refill_rate = tonumber(ARGV[2])
        local current_time = tonumber(ARGV[3])
        local requested_tokens = tonumber(ARGV[4])

        local bucket = redis.call('hgetall', key)
        local last_refill_time = 0
        local tokens = capacity

        if #bucket > 0 then
            for i=1, #bucket, 2 do
                if bucket[i] == 'last_refill_time' then
                    last_refill_time = tonumber(bucket[i+1])
                elseif bucket[i] == 'tokens' then
                    tokens = tonumber(bucket[i+1])
                end
            end
        end

        local time_elapsed = current_time - last_refill_time
        local new_tokens = time_elapsed * refill_rate
        
        tokens = math.min(capacity, tokens + new_tokens)
        last_refill_time = current_time

        if tokens >= requested_tokens then
            tokens = tokens - requested_tokens
            redis.call('hset', key, 'tokens', tokens, 'last_refill_time', last_refill_time)
            redis.call('expire', key, capacity / refill_rate * 2) 
            return 1
        else
            redis.call('hset', key, 'tokens', tokens, 'last_refill_time', last_refill_time)
            redis.call('expire', key, capacity / refill_rate * 2)
            return 0
        end
    ";

    public RedisTokenBucketRateLimiter(IDatabase redis, ILogger<RedisTokenBucketRateLimiter> logger, IOptions<RateLimitingSettings> options)
    {
        _redis = redis;
        _logger = logger;
        _settings = options.Value.TokenBucket;
    }

    private async Task<bool> AllowAsync(string action, long userId, long chatId, int capacity, double refillRatePerSecond)
    {
        try
        {
            var key = $"tb:{action}:{userId}:{chatId}";
            var currentTime = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
            
            var result = await _redis.ScriptEvaluateAsync(
                TokenBucketLuaScript,
                new RedisKey[] { key },
                new RedisValue[] { capacity, refillRatePerSecond, currentTime, 1 }
            );

            return (long)result == 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TokenBucketRateLimiter: Redis unavailable, defaulting to allow");
            return true;
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
    private readonly IDatabase _redis;
    private readonly ILogger<RedisLeakyBucketRateLimiter> _logger;
    private readonly LeakyBucketSettings _settings;

    private const string LeakyBucketLuaScript = @"
        local key = KEYS[1]
        local leak_interval_ms = tonumber(ARGV[1])
        local current_time_ms = tonumber(ARGV[2])

        local next_allowed_time = redis.call('get', key)
        if not next_allowed_time then
            next_allowed_time = current_time_ms
        end
        next_allowed_time = tonumber(next_allowed_time)

        if current_time_ms >= next_allowed_time then
            local new_next_time = current_time_ms + leak_interval_ms
            redis.call('set', key, new_next_time, 'PX', leak_interval_ms * 2)
            return 1
        else
            return 0
        end
    ";

    public RedisLeakyBucketRateLimiter(IDatabase redis, ILogger<RedisLeakyBucketRateLimiter> logger, IOptions<RateLimitingSettings> options)
    {
        _redis = redis;
        _logger = logger;
        _settings = options.Value.LeakyBucket;
    }

    private async Task<bool> AllowAsync(string action, long userId, long chatId, TimeSpan leakInterval)
    {
        try
        {
            var key = $"lb:{action}:{userId}:{chatId}";
            var currentTimeMs = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
            var leakIntervalMs = leakInterval.TotalMilliseconds;

            var result = await _redis.ScriptEvaluateAsync(
                LeakyBucketLuaScript,
                new RedisKey[] { key },
                new RedisValue[] { leakIntervalMs, currentTimeMs }
            );

            return (long)result == 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LeakyBucketRateLimiter: Redis unavailable, defaulting to allow");
            return true;
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