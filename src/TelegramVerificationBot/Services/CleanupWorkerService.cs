using System.Text.Json;
using StackExchange.Redis;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot.Services;

public class CleanupWorkerService(
  ILogger<CleanupWorkerService> logger,
  IDatabase redisDb,
  AppJsonSerializerContext jsonContext,
  ITaskDispatcher dispatcher
) : BackgroundService {

  // 这个常量在这里是合适的，因为它决定了Worker的轮询频率
  private const int _cleanupIntervalSeconds = 1; // 建议缩短轮询间隔以提高及时性

  // 这个常量不属于Worker，它应该在“生产者”（VerificationServiceROP）中定义
  // private const int MessageDeletionDelaySeconds = 10;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    logger.LogInformation("Cleanup Worker Service is starting.");

    while (!stoppingToken.IsCancellationRequested) {
      try {
        var nowTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var queueKey = "cleanup_queue";

        var tasksToProcess = await redisDb.SortedSetRangeByScoreAsync(queueKey, double.NegativeInfinity, nowTimestamp);

        if (tasksToProcess.Length > 0) {
          if (await redisDb.SortedSetRemoveRangeByScoreAsync(queueKey, double.NegativeInfinity, nowTimestamp) > 0) {

            logger.LogInformation("Processing {Count} cleanup tasks.", tasksToProcess.Length);

            foreach (var taskJson in tasksToProcess) {
              try {
                var cleanupTask = JsonSerializer.Deserialize(taskJson.ToString(), jsonContext.CleanupTask);
                if (cleanupTask == null) {
                  logger.LogWarning("Failed to deserialize cleanup task and it has been removed from queue: {TaskJson}", taskJson);
                  continue; // 无需再手动删除，因为整个范围都已被移除
                }

                logger.LogInformation("Dispatching delete job for message {MessageId} in chat {ChatId}", cleanupTask.MessageId, cleanupTask.MessageChatId);
                await dispatcher.DispatchAsync(new DeleteMessageJob(cleanupTask.MessageId, cleanupTask.MessageChatId));

              } catch (Exception ex) {
                // 即使单个任务处理失败，也不会影响其他任务，因为它们已从队列中移除
                logger.LogError(ex, "Error processing individual cleanup task: {TaskJson}", taskJson);
              }
            }
          }
        }
      } catch (Exception ex) {
        logger.LogError(ex, "Error during cleanup worker execution.");
      }

      await Task.Delay(TimeSpan.FromSeconds(_cleanupIntervalSeconds), stoppingToken);
    }
    logger.LogInformation("Cleanup Worker Service is stopping.");
  }
}
