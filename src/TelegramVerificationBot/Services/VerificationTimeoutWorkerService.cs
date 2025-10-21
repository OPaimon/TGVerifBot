// 建议新建文件：Services/VerificationTimeoutWorkerService.cs

using System.Text.Json;
using StackExchange.Redis;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Tasks; // 确保你在这里（或新的Tasks文件中）定义了 ProcessVerificationTimeoutJob

namespace TelegramVerificationBot.Services;

public class VerificationTimeoutWorkerService(
    ILogger<VerificationTimeoutWorkerService> logger,
    IDatabase redisDb, // IDatabase 实例由 DI 注入
    ITaskDispatcher dispatcher,
    AppJsonSerializerContext jsonContext
) : BackgroundService {
  private const int _pollIntervalSeconds = 1; // 1秒轮询一次

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    logger.LogInformation("Verification Timeout Worker Service is starting.");

    while (!stoppingToken.IsCancellationRequested) {
      try {
        var nowTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var queueKey = "verify:timeout_queue"; // 这是你的新超时队列

        // 1. 查找所有已到期的任务（score <= now）
        var tasksToProcess = await redisDb.SortedSetRangeByScoreAsync(queueKey, double.NegativeInfinity, nowTimestamp);

        if (tasksToProcess.Length > 0) {
          // 2. 立即从队列中移除它们，防止被重复处理
          if (await redisDb.SortedSetRemoveRangeByScoreAsync(queueKey, double.NegativeInfinity, nowTimestamp) > 0) {
            logger.LogInformation("Processing {Count} verification timeouts.", tasksToProcess.Length);

            foreach (var sessionIdValue in tasksToProcess) {
              try {
                var sessionId = sessionIdValue.ToString();
                if (string.IsNullOrEmpty(sessionId)) {
                  logger.LogWarning("Found an empty session ID in timeout queue. Skipping.");
                  continue;
                }
                logger.LogInformation("Dispatching timeout job for session {SessionId}", sessionId);
                var sessionKey = $"verify:session:{sessionId}";
                var sessionJson = await redisDb.StringGetAsync(sessionKey);

                await redisDb.KeyDeleteAsync([sessionKey]);

                var session = JsonSerializer.Deserialize(sessionJson.ToString(), jsonContext.VerificationSession);
                if (session is null) {
                  logger.LogError("Failed to deserialize session {SessionId} for timeout action.", sessionId);
                  continue;
                }
                // 3. 分发一个新的 Job 来处理超时逻辑
                // 你需要创建这个 Job 和它的 Handler
                await dispatcher.DispatchAsync(new ProcessVerificationTimeoutJob(session));
              } catch (Exception ex) {
                // 即使单个任务处理失败，也不会影响其他任务
                logger.LogError(ex, "Error processing individual timeout task: {SessionId}", sessionIdValue);
              }
            }
          }
        }
      } catch (Exception ex) {
        logger.LogError(ex, "Error during timeout worker execution.");
      }

      // 等待固定的间隔
      await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
    }

    logger.LogInformation("Verification Timeout Worker Service is stopping.");
  }
}
