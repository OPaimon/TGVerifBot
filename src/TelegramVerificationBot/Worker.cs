using TelegramVerificationBot.Services;

namespace TelegramVerificationBot;

/// <summary>
/// The main background service that hosts the application's long-running processes.
/// </summary>
public class Worker(ILogger<Worker> logger, TelegramService telegramService)
  : BackgroundService {
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    logger.LogInformation("Worker starting at: {time}", DateTimeOffset.Now);

    try {
      // This is the single core task of the Worker:
      // to start the Telegram service and wait for it to complete.
      // We pass the stoppingToken down so the TelegramService knows when to stop.
      await telegramService.ConnectAndListenAsync(stoppingToken);
    } catch (Exception ex) when (ex is not TaskCanceledException) {
      // If an unhandled fatal exception occurs in the main loop of TelegramService,
      // it's caught and logged here, and the application will then stop.
      logger.LogCritical(ex, "The main application loop has crashed unexpectedly.");
    } finally {
      logger.LogInformation("Worker gracefully stopping at: {time}", DateTimeOffset.Now);
    }
  }
}
