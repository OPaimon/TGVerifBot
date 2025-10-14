using System.Text.Json;
using StackExchange.Redis;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot.Services;

/// <summary>
/// Handles logic related to expired Redis keys, triggered by keyspace notifications.
/// </summary>
public class ExpiredStateService(
  ILogger<ExpiredStateService> logger,
  ITaskDispatcher dispatcher,
  IDatabase redisDb,
  AppJsonSerializerContext jsonContext
) {
  // 保持外部接口不变
  public async Task HandleRedisKeyEventAsync(RedisKeyEventJob job) {
    // We are specifically interested in the expiration of "user_status" keys.
    // Keys are expected to be in the format: "user_statusE:userId:chatId" or "user_statusD:userId:chatId"
    // The general check is sufficient to route the event.
    if (job.Event == "expired" && job.Key.StartsWith("user_status")) {
      await HandleExpiredUserStatusAsync(job.Key);
    }
  }

  // ========================================================================
  // 私有方法：核心逻辑入口
  // ========================================================================
  private async Task HandleExpiredUserStatusAsync(string expiredKey) {
    logger.LogInformation("Handling expired user status key: {Key}", expiredKey);

    // 1. 统一解析 Key
    if (!TryParseKey(expiredKey, out var prefix, out var userId, out var chatId)) {
      logger.LogError("Invalid format for user_status key: {Key}", expiredKey);
      return;
    }

    // 2. 根据前缀清晰分支
    switch (prefix) {
      case "user_statusE":
        // E: Expiration - 验证超时，执行拒绝逻辑
        await HandleExpirationDenialAsync(userId, chatId);
        break;

      case "user_statusD":
        // D: Delayed Message Deletion - 延迟删除消息
        await HandleDelayedMessageDeletionAsync(userId, chatId);
        break;

      default:
        logger.LogError("Unknown key prefix in expired user status key: {Key}", expiredKey);
        break;
    }
  }

  // ========================================================================
  // 私有方法：Key 解析
  // ========================================================================

  /// <summary>
  /// Parses the expired Redis key into its prefix, UserId, and ChatId components.
  /// Expected format: PREFIX:UserId:ChatId (e.g., user_statusE:12345:67890)
  /// </summary>
  private bool TryParseKey(string key, out string prefix, out long userId, out long chatId) {
    prefix = string.Empty;
    userId = 0;
    chatId = 0;

    var parts = key.Split(':');
    if (parts.Length != 3) {
      return false;
    }

    prefix = parts[0];
    if (!long.TryParse(parts[1], out userId)) {
      return false;
    }
    if (!long.TryParse(parts[2], out chatId)) {
      return false;
    }

    return true;
  }

  // ========================================================================
  // 私有方法：状态检索
  // ========================================================================

  /// <summary>
  /// Retrieves and deserializes the BVerificationState from Redis.
  /// </summary>
  private async Task<BVerificationState?> GetVerificationStateAsync(long userId, long chatId) {
    var redisKey = $"b_user_status:{userId}:{chatId}";
    var redisValue = await redisDb.StringGetAsync(redisKey);

    if (!redisValue.HasValue) {
      logger.LogWarning("BVerificationState not found for user {UserId} in chat {ChatId}. Key: {Key}", userId, chatId, redisKey);
      return null;
    }

    var state = JsonSerializer.Deserialize(redisValue.ToString(), jsonContext.BVerificationState);
    if (state is null) {
      logger.LogError("Failed to deserialize BVerificationState from Redis value: {Value}", redisValue);
    }

    return state;
  }

  // ========================================================================
  // 私有方法：业务逻辑分离
  // ========================================================================

  /// <summary>
  /// Handles the logic for expired verification window (user_statusE). Denies the join request or unbans.
  /// </summary>
  private async Task HandleExpirationDenialAsync(long userId, long chatId) {
    logger.LogInformation("Verification window expired for user {UserId} in chat {ChatId}. Checking state for denial/unban.", userId, chatId);

    var state = await GetVerificationStateAsync(userId, chatId);

    // 即使获取状态失败，也应该尽量拒绝或解除封禁，防止用户被永久卡住。
    // 但是要发送消息，我们必须有 state 中的 MessageId。
    if (state is null) {
      logger.LogWarning("Missing BVerificationState for denial, dispatching ChatJoinRequestJob (Deny) without message update for user {UserId} in chat {ChatId}.", userId, chatId);
      // 至少尝试拒绝加入请求，假设其不在群内，这是更安全的选择。
      await dispatcher.DispatchAsync(new ChatJoinRequestJob(userId, chatId, false));
      return;
    }

    var denialMessage = "❌ 验证已过期，入群请求已被拒绝。";

    if (state.IsInChat) {
      logger.LogInformation("User {UserId} is already in chat {ChatId}, unbanning instead of denying join request.", userId, chatId);
      await Task.WhenAll(
        dispatcher.DispatchAsync(new UnBanUserJob(chatId, userId, true)), // true: remove restrictions
        dispatcher.DispatchAsync(new EditMessageJob(state.MessageChatId, state.MessageId, denialMessage))
      );
    } else {
      logger.LogInformation("Denying join request for user {UserId} in chat {ChatId}.", userId, chatId);
      await Task.WhenAll(
        dispatcher.DispatchAsync(new ChatJoinRequestJob(userId, chatId, false)),
        dispatcher.DispatchAsync(new EditMessageJob(state.MessageChatId, state.MessageId, denialMessage))
      );
    }
  }

  /// <summary>
  /// Handles the logic for delayed message deletion (user_statusD).
  /// </summary>
  private async Task HandleDelayedMessageDeletionAsync(long userId, long chatId) {
    var state = await GetVerificationStateAsync(userId, chatId);

    if (state is null) {
      // 如果状态不存在，说明在删除消息之前，另一个逻辑可能已经删除了消息和状态（如用户手动完成验证）。
      logger.LogInformation("BVerificationState not found for user {UserId} in chat {ChatId}. Message may have been deleted already.", userId, chatId);
      return;
    }

    logger.LogInformation("Deleting verification message {MessageId} in chat {ChatId} for user {UserId}.", state.MessageId, state.MessageChatId, userId);
    await dispatcher.DispatchAsync(new DeleteMessageJob(state.MessageId, state.MessageChatId));
  }
}
