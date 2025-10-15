using System.Text.Json;
using Microsoft.Extensions.ObjectPool;
using StackExchange.Redis;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models; // 确保 VerificationSession 在这里
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot.Services;

/// <summary>
/// Handles the expiration of verification timeouts by listening to Redis keyspace notifications.
/// </summary>
public class ExpiredStateService(
  ILogger<ExpiredStateService> logger,
  ITaskDispatcher dispatcher,
  IDatabase redisDb,
  AppJsonSerializerContext jsonContext
) {
  public async Task HandleRedisKeyEventAsync(RedisKeyEventJob job) {
    // 新的职责：只关心 verify:timeout:* 键的过期事件
    if (job.Event == "expired" && job.Key.StartsWith("verify:timeout:")) {
      await HandleVerificationTimeoutAsync(job.Key);
    }
  }

  /// <summary>
  /// Parses the timeout key into its ChatId and UserId components.
  /// Expected format: verify:timeout:ChatId:UserId
  /// </summary>
  private bool TryParseTimeoutKey(string key, out long chatId, out long userId) {
    chatId = 0;
    userId = 0;
    var parts = key.Split(':');
    return parts.Length == 4 && long.TryParse(parts[2], out chatId) && long.TryParse(parts[3], out userId);
  }

  /// <summary>
  /// Core logic to handle an expired verification.
  /// </summary>
  private async Task HandleVerificationTimeoutAsync(string expiredKey) {
    logger.LogInformation("Handling verification timeout for key: {Key}", expiredKey);

    if (!TryParseTimeoutKey(expiredKey, out var chatId, out var userId)) {
      logger.LogError("Invalid format for timeout key: {Key}", expiredKey);
      return;
    }

    var lookupKey = $"verify:lookup:{chatId}:{userId}";
    var sessionId = await redisDb.StringGetAsync(lookupKey);

    if (sessionId.IsNullOrEmpty) {
      logger.LogInformation("Session for user {UserId} in chat {ChatId} seems to be already resolved. Ignoring timeout event.", userId, chatId);
      return;
    }

    var sessionKey = $"verify:session:{sessionId}";
    var sessionJson = await redisDb.StringGetAsync(sessionKey);

    await redisDb.KeyDeleteAsync([sessionKey, lookupKey]);

    if (sessionJson.IsNullOrEmpty) {
      logger.LogError("Session data for {SessionId} is missing, cannot perform context-aware timeout action.", sessionId);
      await dispatcher.DispatchAsync(new ChatJoinRequestJob(userId, chatId, false));
      return;
    }

    var session = JsonSerializer.Deserialize(sessionJson.ToString(), jsonContext.VerificationSession);
    if (session is null) {
      logger.LogError("Failed to deserialize session {SessionId} for timeout action.", sessionId);
      return;
    }

    logger.LogInformation("Verification timed out for user {UserId}. Context: {Context}. Kicking/Declining.", userId, session.ContextType);

    var denialMessage = "❌ 验证已超时，操作已被取消。";

    Task finalActionTask = session.ContextType switch {
      VerificationContextType.InGroupRestriction => dispatcher.DispatchAsync(new KickUserJob(chatId, userId)),
      VerificationContextType.JoinRequest => dispatcher.DispatchAsync(new ChatJoinRequestJob(userId, chatId, false)),
      _ => Task.CompletedTask
    };

    Task editMessageTask = Task.CompletedTask;
    if (session.VerificationMessageId.HasValue) {
      if (session.ContextType == VerificationContextType.InGroupRestriction) {
        editMessageTask = dispatcher.DispatchAsync(new EditMessageJob(chatId, session.VerificationMessageId.Value, denialMessage));
      } else if (session.ContextType == VerificationContextType.JoinRequest) {
        editMessageTask = dispatcher.DispatchAsync(new EditMessageJob(userId, session.VerificationMessageId.Value, denialMessage));
      }
    }

    await Task.WhenAll(finalActionTask, editMessageTask);
  }
}
