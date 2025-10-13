
using Telegram.Bot.Types;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Tasks;
using StackExchange.Redis;
using System.Text.RegularExpressions;

namespace TelegramVerificationBot.Services;

/// <summary>
/// Handles logic related to expired Redis keys, triggered by keyspace notifications.
/// </summary>
public class ExpiredStateService
{
    private readonly ILogger<ExpiredStateService> _logger;
    private readonly FunctionalTaskDispatcher _dispatcher;
    private readonly IDatabase _redisDb;

    public ExpiredStateService(ILogger<ExpiredStateService> logger, FunctionalTaskDispatcher dispatcher, IDatabase redisDb)
    {
        _logger = logger;
        _dispatcher = dispatcher;
        _redisDb = redisDb;
    }

    public async Task HandleRedisKeyEventAsync(RedisKeyEventJob job)
    {
        // We are specifically interested in the expiration of "user_status" keys.
        if (job.Event == "expired" && job.Key.StartsWith("user_status:"))
        {
            await HandleExpiredUserStatusAsync(job.Key);
        }
    }

    private async Task HandleExpiredUserStatusAsync(string expiredKey)
    {
        _logger.LogInformation("Handling expired user status key: {Key}", expiredKey);

        var parts = expiredKey.Split(':');
        if (parts.Length == 3 && long.TryParse(parts[1], out var userId) && long.TryParse(parts[2], out var chatId))
        {
            _logger.LogInformation("Verification window expired for user {UserId} in chat {ChatId}. Automatically denying join request.", userId, chatId);

            // The user's verification time is up. We deny the join request.
            // We construct a temporary User object as we only need the ID for the job.
            var redisValue = await _redisDb.StringGetAsync($"b_user_status:{userId}:{chatId}");

            await _dispatcher.DispatchAsync(new ChatJoinRequestJob(userId, chatId, false));

            if (redisValue.HasValue)
            {
                var valueSpan = redisValue.ToString().AsSpan();

                int separatorIndex = valueSpan.IndexOf('_');

                if (separatorIndex > 0 && separatorIndex < valueSpan.Length - 1)
                {
                    var idSpan = valueSpan.Slice(0, separatorIndex);
                    var chatIdSpan = valueSpan.Slice(separatorIndex + 1);

                    if (int.TryParse(idSpan, out int messageId) &&
                        long.TryParse(chatIdSpan, out long messageChatId))
                    {
                        await _dispatcher.DispatchAsync(new EditMessageJob(messageChatId, messageId, "❌ 验证已过期，入群请求已被拒绝。"));
                    }
                }
            }

            // Note: We don't need to clean up the corresponding "verification_token" because
            // it has its own slightly longer TTL and will be removed by Redis automatically.
        }
        else
        {
            _logger.LogError("Invalid format for user_status key: {Key}", expiredKey);
        }
    }
}
